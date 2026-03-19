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
	private readonly HttpClient _httpClient;
	private readonly IOrchestrationReporter _reporter;
	private readonly ILogger<HttpStepExecutor> _logger;

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
			var url = TemplateResolver.Resolve(httpStep.Url, context.Parameters, context, step.DependsOn);

			// Resolve template expressions in body
			string? body = null;
			if (httpStep.Body is not null)
			{
				body = TemplateResolver.Resolve(httpStep.Body, context.Parameters, context, step.DependsOn);
			}

			// Build the HTTP request
			var method = new HttpMethod(httpStep.Method.ToUpperInvariant());
			using var request = new HttpRequestMessage(method, url);

			// Resolve template expressions in headers
			foreach (var (key, value) in httpStep.Headers)
			{
				var resolvedValue = TemplateResolver.Resolve(value, context.Parameters, context, step.DependsOn);
				request.Headers.TryAddWithoutValidation(key, resolvedValue);
			}

			// Set body if present
			if (body is not null && method != HttpMethod.Get && method != HttpMethod.Head)
			{
				request.Content = new StringContent(body, Encoding.UTF8, httpStep.ContentType);
			}

			LogHttpRequest(step.Name, httpStep.Method, url);

			// Send the request
			using var response = await _httpClient.SendAsync(request, cancellationToken);

			var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

			if (response.IsSuccessStatusCode)
			{
				LogHttpSuccess(step.Name, (int)response.StatusCode);
				return ExecutionResult.Succeeded(
					responseBody,
					rawDependencyOutputs: rawDependencyOutputs);
			}
			else
			{
				var errorMessage = $"HTTP {httpStep.Method} {url} returned {(int)response.StatusCode} {response.ReasonPhrase}";
				LogHttpFailure(step.Name, (int)response.StatusCode, errorMessage);
				_reporter.ReportStepError(step.Name, errorMessage);
				return ExecutionResult.Failed(errorMessage, rawDependencyOutputs);
			}
		}
		catch (OperationCanceledException)
		{
			throw; // Let cancellation propagate for timeout handling
		}
		catch (Exception ex)
		{
			var errorMessage = $"HTTP request failed: {ex.Message}";
			LogHttpException(step.Name, ex);
			_reporter.ReportStepError(step.Name, errorMessage);
			return ExecutionResult.Failed(errorMessage, rawDependencyOutputs);
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

	#endregion
}
