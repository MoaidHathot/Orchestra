using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Orchestra.Engine;

/// <summary>
/// Executes HTTP steps by making HTTP requests to external APIs.
/// Supports template resolution in URL, headers, and body.
/// </summary>
public sealed partial class HttpStepExecutor : IStepExecutor
{
	/// <summary>
	/// Process-wide client shared by every executor that does not inject its own. One
	/// <see cref="HttpClient"/> for the process is the documented usage pattern (a new client
	/// per run leaks sockets and never gets disposed), and its timeout is disabled so the only
	/// clock that governs an Http step is the step / orchestration timeout: the client's own
	/// default 100 s timeout used to surface as a <see cref="TaskCanceledException"/> that the
	/// engine mistook for an external cancellation, marking the step Cancelled (never retried)
	/// and cancelling every pending step with it.
	/// </summary>
	public static HttpClient SharedHttpClient { get; } = new()
	{
		Timeout = Timeout.InfiniteTimeSpan,
	};

	private readonly HttpClient _httpClient;
	private readonly IOrchestrationReporter _reporter;
	private readonly ILogger<HttpStepExecutor> _logger;

	/// <summary>
	/// Creates an executor that uses <see cref="SharedHttpClient"/>.
	/// </summary>
	public HttpStepExecutor(
		IOrchestrationReporter reporter,
		ILogger<HttpStepExecutor> logger)
		: this(SharedHttpClient, reporter, logger)
	{
	}

	public HttpStepExecutor(
		HttpClient httpClient,
		IOrchestrationReporter reporter,
		ILogger<HttpStepExecutor> logger)
	{
		_httpClient = httpClient;
		_reporter = reporter;
		_logger = logger;
	}

	public OrchestrationStepType StepType => OrchestrationStepType.Http;

	public async Task<ExecutionResult> ExecuteAsync(
		OrchestrationStep step,
		OrchestrationExecutionContext context,
		CancellationToken cancellationToken = default)
	{
		if (step is not HttpOrchestrationStep httpStep)
			throw new InvalidOperationException(
				$"HttpStepExecutor received a step of type '{step.GetType().Name}' " +
				$"but expected '{nameof(HttpOrchestrationStep)}'.");

		var rawDependencyOutputs = context.GetRawDependencyOutputs(step.DependsOn);

		try
		{
			// Resolve template expressions in URL
			var url = TemplateResolver.Resolve(httpStep.Url, context.Parameters, context, step.DependsOn, step);

			// Resolve template expressions in body
			string? body = null;
			if (httpStep.Body is not null)
			{
				body = TemplateResolver.Resolve(httpStep.Body, context.Parameters, context, step.DependsOn, step);
			}

			// Build the HTTP request
			var method = new HttpMethod(httpStep.Method.ToUpperInvariant());
			using var request = new HttpRequestMessage(method, url);

			// Resolve and collect headers for trace
			var resolvedHeaders = new Dictionary<string, string>();
			foreach (var (key, value) in httpStep.Headers)
			{
				var resolvedValue = TemplateResolver.Resolve(value, context.Parameters, context, step.DependsOn, step);
				request.Headers.TryAddWithoutValidation(key, resolvedValue);
				resolvedHeaders[key] = resolvedValue;
			}

			// Set body if present
			if (body is not null && method != HttpMethod.Get && method != HttpMethod.Head)
			{
				request.Content = new StringContent(body, Encoding.UTF8, httpStep.ContentType);
			}

			LogHttpRequest(step.Name, httpStep.Method, url);

			// Send the request. Headers-only completion so the body is read through the cap
			// below instead of being buffered whole by HttpClient.
			using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

			var (responseBody, droppedBytes) = await ReadBodyBoundedAsync(response, httpStep.MaxOutputBytes, cancellationToken);
			if (droppedBytes > 0)
			{
				responseBody += OutputLimits.Marker(httpStep.MaxOutputBytes, droppedBytes);
				LogHttpBodyTruncated(step.Name, droppedBytes);
				_reporter.ReportSessionWarning(OutputLimits.WarningType, OutputLimits.WarningMessage(step.Name, httpStep.MaxOutputBytes, droppedBytes));
			}

			// Build trace for the HTTP step
			var responseHeaders = response.Headers
				.Concat(response.Content.Headers)
				.ToDictionary(h => h.Key, h => string.Join(", ", h.Value));

			var trace = new StepExecutionTrace
			{
				// Use SystemPrompt to store the request summary
				SystemPrompt = $"HTTP {httpStep.Method.ToUpperInvariant()} {url}",
				// Use UserPromptRaw to store request body
				UserPromptRaw = body,
				// Use FinalResponse to store the response body
				FinalResponse = responseBody,
				// Use McpServers to store request/response metadata
				McpServers =
				[
					$"Method: {httpStep.Method.ToUpperInvariant()}",
					$"URL: {url}",
					$"Status: {(int)response.StatusCode} {response.ReasonPhrase}",
					$"ContentType: {httpStep.ContentType}",
					..resolvedHeaders.Select(h => $"Request-Header: {h.Key}: {h.Value}"),
					..responseHeaders.Select(h => $"Response-Header: {h.Key}: {h.Value}"),
				],
			};

			// Report the step trace for live trace viewing
			_reporter.ReportStepTrace(step.Name, trace);

			if (response.IsSuccessStatusCode)
			{
				LogHttpSuccess(step.Name, (int)response.StatusCode);
				return ExecutionResult.Succeeded(
					responseBody,
					rawDependencyOutputs: rawDependencyOutputs,
					trace: trace);
			}
			else
			{
				var errorMessage = $"HTTP {httpStep.Method} {url} returned {(int)response.StatusCode} {response.ReasonPhrase}";
				LogHttpFailure(step.Name, (int)response.StatusCode, errorMessage);
				_reporter.ReportStepError(step.Name, errorMessage);
				return ExecutionResult.Failed(errorMessage, rawDependencyOutputs, trace: trace, errorCategory: StepErrorCategory.HttpError);
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw; // Step timeout or external cancellation: let the executor classify it.
		}
		catch (OperationCanceledException ex)
		{
			// The token was NOT cancelled, so this came from HttpClient.Timeout on an injected
			// client (SharedHttpClient disables it). Report it as a timeout failure so retry
			// policies apply and dependents are skipped rather than the whole run cancelled.
			var timeout = _httpClient.Timeout == Timeout.InfiniteTimeSpan
				? "the HTTP client's timeout"
				: $"the HTTP client's {_httpClient.Timeout.TotalSeconds:0.#} s timeout";
			var errorMessage = $"HTTP request timed out after {timeout}: {ex.Message}";
			LogHttpException(step.Name, ex);
			_reporter.ReportStepError(step.Name, errorMessage);
			return ExecutionResult.Failed(errorMessage, rawDependencyOutputs, errorCategory: StepErrorCategory.Timeout);
		}
		catch (HttpRequestException ex)
		{
			var errorMessage = $"HTTP request failed: {ex.Message}";
			LogHttpException(step.Name, ex);
			_reporter.ReportStepError(step.Name, errorMessage);
			return ExecutionResult.Failed(errorMessage, rawDependencyOutputs, errorCategory: StepErrorCategory.NetworkError);
		}
		catch (Exception ex)
		{
			var errorMessage = $"HTTP request failed: {ex.Message}";
			LogHttpException(step.Name, ex);
			_reporter.ReportStepError(step.Name, errorMessage);
			return ExecutionResult.Failed(errorMessage, rawDependencyOutputs, errorCategory: StepErrorCategory.HttpError);
		}
	}

	/// <summary>
	/// Reads the response body up to the step's <c>maxOutputBytes</c>. Returns the decoded text
	/// and how many bytes were left unread (0 when the whole body fit). The body is decoded with
	/// the charset the server declared, falling back to UTF-8; a multi-byte character cut by the
	/// cap decodes to U+FFFD and is trimmed so the marker follows clean text.
	/// </summary>
	internal static async Task<(string Body, long DroppedBytes)> ReadBodyBoundedAsync(HttpResponseMessage response, int? maxOutputBytes, CancellationToken cancellationToken)
	{
		var encoding = ResolveEncoding(response.Content.Headers.ContentType?.CharSet);
		await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

		if (!OutputLimits.IsBounded(maxOutputBytes))
		{
			using var reader = new StreamReader(stream, encoding);
			return (await reader.ReadToEndAsync(cancellationToken), 0);
		}

		var cap = OutputLimits.Resolve(maxOutputBytes);
		var buffer = new MemoryStream();
		var chunk = new byte[81920];
		long dropped = 0;

		while (true)
		{
			var read = await stream.ReadAsync(chunk, cancellationToken);
			if (read == 0)
				break;

			var room = cap - buffer.Length;
			if (room >= read)
			{
				buffer.Write(chunk, 0, read);
				continue;
			}

			if (room > 0)
				buffer.Write(chunk, 0, (int)room);
			dropped += read - Math.Max(0, room);

			// Count (but do not keep) whatever remains so the warning states the real size.
			while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
				dropped += read;
			break;
		}

		var text = encoding.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
		if (dropped > 0)
			text = text.TrimEnd('\uFFFD');
		return (text, dropped);
	}

	private static Encoding ResolveEncoding(string? charSet)
	{
		if (string.IsNullOrWhiteSpace(charSet))
			return Encoding.UTF8;

		try
		{
			return Encoding.GetEncoding(charSet.Trim('"'));
		}
		catch (ArgumentException)
		{
			return Encoding.UTF8;
		}
	}

	#region Source-Generated Logging

	[LoggerMessage(
		EventId = 1,
		Level = LogLevel.Information,
		Message = "Step '{StepName}' sending HTTP {Method} to {Url}")]
	private partial void LogHttpRequest(string stepName, string method, string url);

	[LoggerMessage(
		EventId = 2,
		Level = LogLevel.Information,
		Message = "Step '{StepName}' HTTP request succeeded with status {StatusCode}")]
	private partial void LogHttpSuccess(string stepName, int statusCode);

	[LoggerMessage(
		EventId = 3,
		Level = LogLevel.Warning,
		Message = "Step '{StepName}' HTTP request failed with status {StatusCode}: {Error}")]
	private partial void LogHttpFailure(string stepName, int statusCode, string error);

	[LoggerMessage(
		EventId = 4,
		Level = LogLevel.Error,
		Message = "Step '{StepName}' HTTP request threw an exception")]
	private partial void LogHttpException(string stepName, Exception ex);

	[LoggerMessage(
		EventId = 5,
		Level = LogLevel.Warning,
		Message = "Step '{StepName}' HTTP response body exceeded maxOutputBytes; {DroppedBytes} bytes dropped")]
	private partial void LogHttpBodyTruncated(string stepName, long droppedBytes);

	#endregion
}
