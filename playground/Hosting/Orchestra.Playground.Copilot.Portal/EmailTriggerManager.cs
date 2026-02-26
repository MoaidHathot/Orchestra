using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orchestra.Engine;
using Orchestra.Outlook;

namespace Orchestra.Playground.Copilot.Portal;

/// <summary>
/// Background service that manages email trigger polling.
/// Implements a shared poll loop for all email triggers, using the minimum interval
/// among all registered triggers.
/// </summary>
public class EmailTriggerManager : BackgroundService
{
	private readonly TriggerManager _triggerManager;
	private readonly PortalStatusService _statusService;
	private readonly ILogger<EmailTriggerManager> _logger;
	private readonly GraphAuthOptions _authOptions;
	private OutlookService? _outlookService;

	// Track processed message IDs to avoid reprocessing (since Mail.ReadBasic can't mark as read)
	private readonly HashSet<string> _processedMessageIds = [];
	private const int MaxProcessedIdsToKeep = 1000; // Prevent memory growth

	// Retry configuration
	private const int RetryDelaySeconds = 30;
	private const int MaxConsecutiveErrors = 5;
	private int _consecutiveErrors;

	public EmailTriggerManager(
		TriggerManager triggerManager,
		PortalStatusService statusService,
		GraphAuthOptions authOptions,
		ILogger<EmailTriggerManager> logger)
	{
		_triggerManager = triggerManager;
		_statusService = statusService;
		_authOptions = authOptions;
		_logger = logger;
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		_logger.LogInformation("EmailTriggerManager started.");

		while (!stoppingToken.IsCancellationRequested)
		{
			try
			{
				// Get all email triggers
				var emailTriggers = GetActiveEmailTriggers();
				_statusService.UpdateActiveEmailTriggerCount(emailTriggers.Count);

				if (emailTriggers.Count == 0)
				{
					// No email triggers, wait and check again
					_logger.LogInformation("No active email triggers found. Register an orchestration with an email trigger first. Waiting 10s...");
					await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
					continue;
				}

				_logger.LogInformation("Found {Count} active email trigger(s). Checking connection...", emailTriggers.Count);

				// Calculate the minimum poll interval
				var minInterval = emailTriggers.Min(t => ((EmailTriggerConfig)t.Config).PollIntervalSeconds);
				minInterval = Math.Max(minInterval, 10); // At least 10 seconds

				// Initialize OutlookService if not yet created
				_outlookService ??= new OutlookService(_authOptions);

				// Try to connect if not connected
				if (!_outlookService.IsConnected)
				{
					_statusService.UpdateOutlookStatus(OutlookConnectionStatus.Authenticating);
					_logger.LogInformation("Attempting to connect to Microsoft Graph...");

					if (await _outlookService.ConnectAsync(stoppingToken))
					{
						_logger.LogInformation("Connected to Microsoft Graph as {User}.", _outlookService.AuthenticatedUser);
						_statusService.UpdateOutlookStatus(OutlookConnectionStatus.Connected);
						_consecutiveErrors = 0;
					}
					else
					{
						_statusService.UpdateOutlookStatus(_outlookService.Status, _outlookService.LastError);
						_logger.LogWarning("Failed to connect to Microsoft Graph: {Error}. Retrying in {Delay}s...",
							_outlookService.LastError, RetryDelaySeconds);
						await Task.Delay(TimeSpan.FromSeconds(RetryDelaySeconds), stoppingToken);
						continue;
					}
				}

				// Poll for each unique folder path
				var folderGroups = emailTriggers
					.GroupBy(t => ((EmailTriggerConfig)t.Config).FolderPath)
					.ToList();

				var totalProcessed = 0;

				foreach (var group in folderGroups)
				{
					var folderPath = group.Key;
					var triggersForFolder = group.ToList();

					// Use the max items from the most permissive trigger for this folder
					var maxItems = triggersForFolder.Max(t => ((EmailTriggerConfig)t.Config).MaxItemsPerPoll);

					// Build combined polling options
					var options = new OutlookPollingOptions
					{
						FolderPath = folderPath,
						MaxItemsPerPoll = maxItems,
						UnreadOnly = true
					};

					var messages = await _outlookService.GetUnreadMessagesAsync(options, stoppingToken);

					// Filter out already processed messages (since Mail.ReadBasic can't mark as read)
					var newMessages = messages.Where(m => !_processedMessageIds.Contains(m.EntryId)).ToList();

					if (newMessages.Count > 0)
					{
						_logger.LogInformation("Found {Count} new unread messages in {Folder} ({Total} total unread).",
							newMessages.Count, folderPath, messages.Count);
					}

					foreach (var message in newMessages)
					{
						// Find matching triggers for this message
						var matchingTriggers = triggersForFolder
							.Where(t => MessageMatchesTrigger(message, (EmailTriggerConfig)t.Config))
							.ToList();

						foreach (var trigger in matchingTriggers)
						{
							try
							{
								// Build parameters from the email
								var parameters = BuildEmailParameters(message);

								// Fire the trigger
								var (found, executionId) = await _triggerManager.FireTriggerAsync(trigger.Id, parameters);

								if (found && executionId != null)
								{
									_logger.LogInformation(
										"Fired trigger '{TriggerId}' for email '{Subject}' (execution={ExecutionId}).",
										trigger.Id, message.Subject, executionId);
								}
							}
							catch (Exception ex)
							{
								_logger.LogError(ex, "Error firing trigger '{TriggerId}' for email '{Subject}'.",
									trigger.Id, message.Subject);
							}
						}

						// Track as processed (even if no triggers matched, to avoid re-checking)
						if (matchingTriggers.Count > 0 || !message.IsUnread)
						{
							_processedMessageIds.Add(message.EntryId);
							totalProcessed++;

							// Try to mark as read (will fail with Mail.ReadBasic, but that's OK)
							await _outlookService.MarkAsReadAsync(message.EntryId, stoppingToken);
						}
					}

					// Prevent unbounded memory growth
					if (_processedMessageIds.Count > MaxProcessedIdsToKeep)
					{
						// Remove oldest entries (just clear half since HashSet doesn't maintain order)
						var toRemove = _processedMessageIds.Take(_processedMessageIds.Count / 2).ToList();
						foreach (var id in toRemove)
						{
							_processedMessageIds.Remove(id);
						}
						_logger.LogDebug("Trimmed processed message ID cache to {Count} entries.", _processedMessageIds.Count);
					}
				}

				if (totalProcessed > 0)
				{
					_statusService.RecordSuccessfulPoll(totalProcessed);
				}
				else
				{
					_statusService.RecordSuccessfulPoll(0);
				}

				_consecutiveErrors = 0;
				_statusService.UpdateOutlookStatus(OutlookConnectionStatus.Connected);

				// Wait for the next poll
				await Task.Delay(TimeSpan.FromSeconds(minInterval), stoppingToken);
			}
			catch (OperationCanceledException)
			{
				break;
			}
			catch (Exception ex)
			{
				_consecutiveErrors++;
				_statusService.UpdateOutlookStatus(OutlookConnectionStatus.Error, ex.Message);
				_logger.LogError(ex, "Error in email trigger polling loop (consecutive errors: {Count}).", _consecutiveErrors);

				if (_consecutiveErrors >= MaxConsecutiveErrors)
				{
					_logger.LogWarning("Too many consecutive errors, disconnecting and will retry...");
					_outlookService?.Disconnect();
				}

				await Task.Delay(TimeSpan.FromSeconds(RetryDelaySeconds), stoppingToken);
			}
		}

		_outlookService?.Dispose();
		_logger.LogInformation("EmailTriggerManager stopped.");
	}

	private List<TriggerRegistration> GetActiveEmailTriggers()
	{
		return _triggerManager.GetAllTriggers()
			.Where(t => t.Config is EmailTriggerConfig && t.Config.Enabled && t.Status != TriggerStatus.Paused)
			.ToList();
	}

	private static bool MessageMatchesTrigger(OutlookMessage message, EmailTriggerConfig config)
	{
		// Check subject filter
		if (!string.IsNullOrEmpty(config.SubjectContains) &&
			!message.Subject.Contains(config.SubjectContains, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		// Check sender filter
		if (!string.IsNullOrEmpty(config.SenderContains) &&
			!message.Sender.Contains(config.SenderContains, StringComparison.OrdinalIgnoreCase) &&
			!message.SenderEmail.Contains(config.SenderContains, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		return true;
	}

	private static Dictionary<string, string> BuildEmailParameters(OutlookMessage message)
	{
		return new Dictionary<string, string>
		{
			["emailSubject"] = message.Subject,
			["emailBody"] = message.Body,
			["emailHtmlBody"] = message.HtmlBody ?? "",
			["emailSender"] = message.Sender,
			["emailSenderEmail"] = message.SenderEmail,
			["emailReceivedTime"] = message.ReceivedTime.ToString("o"),
			["emailRecipients"] = string.Join(", ", message.Recipients),
			["emailEntryId"] = message.EntryId,
			["emailConversationId"] = message.ConversationId ?? "",
		};
	}

	public override void Dispose()
	{
		_outlookService?.Dispose();
		base.Dispose();
	}
}
