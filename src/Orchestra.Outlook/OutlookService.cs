using Azure.Identity;
using Microsoft.Graph;
using Microsoft.Graph.Models;

namespace Orchestra.Outlook;

/// <summary>
/// Service for interacting with Outlook via Microsoft Graph API.
/// Uses DefaultAzureCredential for authentication, which automatically tries:
/// - Azure CLI (az login)
/// - Azure PowerShell (Connect-AzAccount)
/// - Managed Identity (Azure VMs, App Service)
/// - Environment variables (service principal)
/// - And more...
/// </summary>
public class OutlookService : IDisposable
{
	private GraphServiceClient? _graphClient;
	private readonly GraphAuthOptions _authOptions;
	private readonly object _lock = new();
	private bool _disposed;

	/// <summary>
	/// Current connection status.
	/// </summary>
	public OutlookConnectionStatus Status { get; private set; } = OutlookConnectionStatus.Disconnected;

	/// <summary>
	/// Last error message, if any.
	/// </summary>
	public string? LastError { get; private set; }

	/// <summary>
	/// Timestamp of the last successful poll operation.
	/// </summary>
	public DateTime? LastSuccessfulPoll { get; private set; }

	/// <summary>
	/// Total number of messages processed since service started.
	/// </summary>
	public int ProcessedCount { get; private set; }

	/// <summary>
	/// The authenticated user's principal name (email).
	/// </summary>
	public string? AuthenticatedUser { get; private set; }

	/// <summary>
	/// Creates a new OutlookService with the specified authentication options.
	/// </summary>
	/// <param name="authOptions">Authentication configuration.</param>
	public OutlookService(GraphAuthOptions authOptions)
	{
		_authOptions = authOptions ?? throw new ArgumentNullException(nameof(authOptions));
	}

	/// <summary>
	/// Attempts to authenticate with Microsoft Graph using DefaultAzureCredential.
	/// </summary>
	/// <returns>True if authentication was successful, false otherwise.</returns>
	public async Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
	{
		lock (_lock)
		{
			if (_disposed) return false;
		}

		try
		{
			Status = OutlookConnectionStatus.Authenticating;

			// Use InteractiveBrowserCredential for proper token binding
			// This satisfies Token Protection / Conditional Access policies
			var credentialOptions = new InteractiveBrowserCredentialOptions
			{
				TenantId = _authOptions.TenantId,
				ClientId = _authOptions.ClientId,
				// Use the default system browser
				TokenCachePersistenceOptions = new TokenCachePersistenceOptions
				{
					Name = "Orchestra.Outlook.TokenCache",
					UnsafeAllowUnencryptedStorage = false
				}
			};

			var credential = new InteractiveBrowserCredential(credentialOptions);

			// Use minimal scopes - Mail.ReadBasic only reads metadata (no body), User.Read for /me endpoint
			var scopes = new[] { "Mail.ReadBasic", "User.Read" };

			_graphClient = new GraphServiceClient(credential, scopes);

			// Test the connection by getting current user via /me endpoint
			var me = await _graphClient.Me.GetAsync(cancellationToken: cancellationToken);
			AuthenticatedUser = me?.UserPrincipalName ?? me?.Mail;

			if (string.IsNullOrEmpty(AuthenticatedUser))
			{
				Status = OutlookConnectionStatus.AuthenticationFailed;
				LastError = "Could not determine authenticated user.";
				return false;
			}

			Status = OutlookConnectionStatus.Connected;
			LastError = null;
			return true;
		}
		catch (AuthenticationFailedException ex)
		{
			Status = OutlookConnectionStatus.AuthenticationFailed;
			LastError = $"Authentication failed: {ex.Message}";
			return false;
		}
		catch (Exception ex) when (ex.Message.Contains("insufficient privileges", StringComparison.OrdinalIgnoreCase)
								   || ex.Message.Contains("Authorization_RequestDenied", StringComparison.OrdinalIgnoreCase))
		{
			Status = OutlookConnectionStatus.InsufficientPermissions;
			LastError = $"Insufficient permissions: {ex.Message}";
			return false;
		}
		catch (Exception ex)
		{
			Status = OutlookConnectionStatus.Error;
			LastError = ex.Message;
			return false;
		}
	}

	/// <summary>
	/// Whether the service is currently connected.
	/// </summary>
	public bool IsConnected => Status == OutlookConnectionStatus.Connected && _graphClient is not null;

	/// <summary>
	/// Gets unread messages from the specified folder based on the polling options.
	/// </summary>
	/// <param name="options">Polling configuration options.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>List of unread messages matching the criteria.</returns>
	public async Task<List<OutlookMessage>> GetUnreadMessagesAsync(OutlookPollingOptions options, CancellationToken cancellationToken = default)
	{
		var messages = new List<OutlookMessage>();

		if (!IsConnected)
		{
			LastError = "Not connected. Call ConnectAsync first.";
			return messages;
		}

		try
		{
			// Get the folder ID
			var folderId = await GetFolderIdAsync(options.FolderPath, cancellationToken);
			if (folderId == null)
			{
				LastError = $"Folder not found: {options.FolderPath}";
				return messages;
			}

			// Build filter for unread items
			var filter = "isRead eq false";

			// Add subject filter if specified
			if (!string.IsNullOrEmpty(options.SubjectContains))
			{
				filter += $" and contains(subject, '{EscapeODataString(options.SubjectContains)}')";
			}

			// Get messages from the folder using /me endpoint
			// Note: Mail.ReadBasic only allows metadata fields, not body content
			var result = await _graphClient!.Me
				.MailFolders[folderId]
				.Messages
				.GetAsync(requestConfig =>
				{
					requestConfig.QueryParameters.Filter = filter;
					requestConfig.QueryParameters.Top = options.MaxItemsPerPoll;
					requestConfig.QueryParameters.Orderby = ["receivedDateTime desc"];
					// Mail.ReadBasic only supports: id, subject, from, toRecipients, receivedDateTime, isRead, conversationId
					// body and bodyPreview require Mail.Read
					requestConfig.QueryParameters.Select = [
						"id", "subject", "from", "toRecipients",
						"receivedDateTime", "isRead", "conversationId"
					];
				}, cancellationToken);

			if (result?.Value == null)
				return messages;

			foreach (var msg in result.Value)
			{
				// Apply sender filter if specified (Graph OData doesn't support contains on from)
				if (!string.IsNullOrEmpty(options.SenderContains))
				{
					var senderEmail = msg.From?.EmailAddress?.Address ?? "";
					var senderName = msg.From?.EmailAddress?.Name ?? "";
					if (!senderEmail.Contains(options.SenderContains, StringComparison.OrdinalIgnoreCase) &&
						!senderName.Contains(options.SenderContains, StringComparison.OrdinalIgnoreCase))
					{
						continue;
					}
				}

				var recipients = msg.ToRecipients?
					.Select(r => r.EmailAddress?.Address ?? r.EmailAddress?.Name ?? "")
					.Where(r => !string.IsNullOrEmpty(r))
					.ToArray() ?? [];

				messages.Add(new OutlookMessage
				{
					EntryId = msg.Id ?? "",
					Subject = msg.Subject ?? "",
					Body = "", // Mail.ReadBasic doesn't include body
					HtmlBody = null,
					Sender = msg.From?.EmailAddress?.Name ?? "",
					SenderEmail = msg.From?.EmailAddress?.Address ?? "",
					ReceivedTime = msg.ReceivedDateTime?.UtcDateTime ?? DateTime.UtcNow,
					Recipients = recipients,
					IsUnread = !(msg.IsRead ?? false),
					ConversationId = msg.ConversationId
				});
			}

			LastSuccessfulPoll = DateTime.UtcNow;
			LastError = null;
			return messages;
		}
		catch (Exception ex)
		{
			LastError = ex.Message;
			if (ex.Message.Contains("token", StringComparison.OrdinalIgnoreCase) &&
				ex.Message.Contains("expired", StringComparison.OrdinalIgnoreCase))
			{
				Status = OutlookConnectionStatus.TokenExpired;
			}
			return messages;
		}
	}

	/// <summary>
	/// Marks a message as read by its ID.
	/// Note: This requires Mail.ReadWrite permission. With Mail.ReadBasic, this will fail silently
	/// and return false. The caller should track processed message IDs locally instead.
	/// </summary>
	/// <param name="messageId">The ID of the message to mark as read.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>True if successful, false otherwise (including when permission is insufficient).</returns>
	public async Task<bool> MarkAsReadAsync(string messageId, CancellationToken cancellationToken = default)
	{
		if (!IsConnected) return false;

		try
		{
			// Use /me endpoint - requires Mail.ReadWrite permission
			await _graphClient!.Me
				.Messages[messageId]
				.PatchAsync(new Message { IsRead = true }, cancellationToken: cancellationToken);

			ProcessedCount++;
			return true;
		}
		catch (Exception ex)
		{
			// This will fail with Mail.ReadBasic - that's expected
			LastError = $"Failed to mark as read (requires Mail.ReadWrite): {ex.Message}";
			return false;
		}
	}

	/// <summary>
	/// Gets the folder ID for a given folder path.
	/// </summary>
	/// <param name="path">Folder path like "Inbox" or "Inbox/Teams".</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The folder ID, or null if not found.</returns>
	private async Task<string?> GetFolderIdAsync(string path, CancellationToken cancellationToken)
	{
		var parts = path.Split('/', '\\');

		// Get the root folder
		var rootFolderName = parts[0].ToLowerInvariant();
		string? folderId;

		// Map common folder names to well-known folder names
		var wellKnownFolder = rootFolderName switch
		{
			"inbox" => "inbox",
			"sent" or "sentmail" or "sent items" or "sentitems" => "sentitems",
			"drafts" => "drafts",
			"deleted" or "deleteditems" or "trash" => "deleteditems",
			"junk" or "junkmail" or "spam" or "junkemail" => "junkemail",
			"archive" => "archive",
			_ => null
		};

		if (wellKnownFolder != null)
		{
			// Use well-known folder name directly with /me endpoint
			var folder = await _graphClient!.Me
				.MailFolders[wellKnownFolder]
				.GetAsync(cancellationToken: cancellationToken);
			folderId = folder?.Id;
		}
		else
		{
			// Search for the folder by display name
			var folders = await _graphClient!.Me
				.MailFolders
				.GetAsync(requestConfig =>
				{
					requestConfig.QueryParameters.Filter = $"displayName eq '{EscapeODataString(parts[0])}'";
				}, cancellationToken);

			folderId = folders?.Value?.FirstOrDefault()?.Id;
		}

		if (folderId == null) return null;

		// Navigate to subfolders
		for (var i = 1; i < parts.Length; i++)
		{
			var childFolders = await _graphClient!.Me
				.MailFolders[folderId]
				.ChildFolders
				.GetAsync(requestConfig =>
				{
					requestConfig.QueryParameters.Filter = $"displayName eq '{EscapeODataString(parts[i])}'";
				}, cancellationToken);

			folderId = childFolders?.Value?.FirstOrDefault()?.Id;
			if (folderId == null) return null;
		}

		return folderId;
	}

	/// <summary>
	/// Escapes a string for use in OData queries.
	/// </summary>
	private static string EscapeODataString(string value)
	{
		return value.Replace("'", "''");
	}

	/// <summary>
	/// Disconnects from Microsoft Graph.
	/// </summary>
	public void Disconnect()
	{
		lock (_lock)
		{
			_graphClient = null;
			AuthenticatedUser = null;
			Status = OutlookConnectionStatus.Disconnected;
		}
	}

	/// <summary>
	/// Disposes of the service.
	/// </summary>
	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;
		Disconnect();
		GC.SuppressFinalize(this);
	}
}
