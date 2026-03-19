using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Orchestra.Engine.Tests.Executor;

public class HttpStepExecutorTests
{
	private readonly IOrchestrationReporter _reporter = Substitute.For<IOrchestrationReporter>();
	private readonly ILogger<HttpStepExecutor> _logger = NullLoggerFactory.Instance.CreateLogger<HttpStepExecutor>();

	private HttpStepExecutor CreateExecutor(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
	{
		var httpClient = new HttpClient(new MockHttpHandler(handler));
		return new HttpStepExecutor(httpClient, _reporter, _logger);
	}

	private static HttpOrchestrationStep CreateHttpStep(
		string name = "http-step",
		string method = "GET",
		string url = "https://api.example.com/data",
		Dictionary<string, string>? headers = null,
		string? body = null,
		string contentType = "application/json",
		string[]? dependsOn = null,
		string[]? parameters = null) => new()
	{
		Name = name,
		Type = OrchestrationStepType.Http,
		DependsOn = dependsOn ?? [],
		Parameters = parameters ?? [],
		Method = method,
		Url = url,
		Headers = headers ?? [],
		Body = body,
		ContentType = contentType,
	};

	#region Success Scenarios

	[Fact]
	public async Task ExecuteAsync_GetRequest_ReturnsSuccessResponse()
	{
		// Arrange
		var executor = CreateExecutor((request, ct) =>
		{
			var response = new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent("{\"status\":\"ok\"}")
			};
			return Task.FromResult(response);
		});

		var step = CreateHttpStep();
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Be("{\"status\":\"ok\"}");
	}

	[Fact]
	public async Task ExecuteAsync_PostRequest_SendsBodyAndReturnsResponse()
	{
		// Arrange
		string? capturedBody = null;
		HttpMethod? capturedMethod = null;

		var executor = CreateExecutor(async (request, ct) =>
		{
			capturedMethod = request.Method;
			if (request.Content is not null)
				capturedBody = await request.Content.ReadAsStringAsync(ct);

			return new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent("{\"id\":1}")
			};
		});

		var step = CreateHttpStep(
			method: "POST",
			body: "{\"name\":\"test\"}");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Be("{\"id\":1}");
		capturedMethod.Should().Be(HttpMethod.Post);
		capturedBody.Should().Be("{\"name\":\"test\"}");
	}

	#endregion

	#region Failure Scenarios

	[Fact]
	public async Task ExecuteAsync_NonSuccessStatusCode_ReturnsFailedResult()
	{
		// Arrange
		var executor = CreateExecutor((request, ct) =>
		{
			var response = new HttpResponseMessage(HttpStatusCode.NotFound)
			{
				ReasonPhrase = "Not Found",
				Content = new StringContent("Resource not found")
			};
			return Task.FromResult(response);
		});

		var step = CreateHttpStep();
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Failed);
		result.ErrorMessage.Should().Contain("404");
	}

	[Fact]
	public async Task ExecuteAsync_HttpException_ReturnsFailedResult()
	{
		// Arrange
		var executor = CreateExecutor((request, ct) =>
		{
			throw new HttpRequestException("Connection refused");
		});

		var step = CreateHttpStep();
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Failed);
		result.ErrorMessage.Should().Contain("Connection refused");
	}

	#endregion

	#region Template Resolution

	[Fact]
	public async Task ExecuteAsync_TemplateResolution_ResolvesUrlAndHeaders()
	{
		// Arrange
		string? capturedUrl = null;
		string? capturedAuthHeader = null;

		var executor = CreateExecutor((request, ct) =>
		{
			capturedUrl = request.RequestUri?.ToString();
			capturedAuthHeader = request.Headers.TryGetValues("Authorization", out var values)
				? string.Join("", values)
				: null;

			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent("ok")
			});
		});

		var step = CreateHttpStep(
			url: "https://{{param.host}}/api/data",
			headers: new Dictionary<string, string>
			{
				["Authorization"] = "Bearer {{param.token}}"
			},
			parameters: ["host", "token"]);

		var context = new OrchestrationExecutionContext
		{
			Parameters = new Dictionary<string, string>
			{
				["host"] = "api.example.com",
				["token"] = "secret-token-123"
			}
		};

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		capturedUrl.Should().Be("https://api.example.com/api/data");
		capturedAuthHeader.Should().Be("Bearer secret-token-123");
	}

	#endregion

	#region Wrong Step Type

	[Fact]
	public async Task ExecuteAsync_WrongStepType_ThrowsInvalidOperationException()
	{
		// Arrange
		var executor = CreateExecutor((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

		var wrongStep = new PromptOrchestrationStep
		{
			Name = "wrong-step",
			Type = OrchestrationStepType.Prompt,
			DependsOn = [],
			SystemPrompt = "system",
			UserPrompt = "user",
			Model = "claude-opus-4.5"
		};

		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };

		// Act
		var act = () => executor.ExecuteAsync(wrongStep, context);

		// Assert
		await act.Should().ThrowAsync<InvalidOperationException>()
			.WithMessage("*HttpStepExecutor*PromptOrchestrationStep*HttpOrchestrationStep*");
	}

	#endregion

	#region Cancellation

	[Fact]
	public async Task ExecuteAsync_Cancellation_ThrowsOperationCanceledException()
	{
		// Arrange
		var executor = CreateExecutor((_, ct) =>
		{
			ct.ThrowIfCancellationRequested();
			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
		});

		var step = CreateHttpStep();
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };
		using var cts = new CancellationTokenSource();
		cts.Cancel();

		// Act
		var act = () => executor.ExecuteAsync(step, context, cts.Token);

		// Assert
		await act.Should().ThrowAsync<OperationCanceledException>();
	}

	#endregion

	#region MockHttpHandler

	private class MockHttpHandler : DelegatingHandler
	{
		private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

		public MockHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
		{
			_handler = handler;
		}

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
			=> _handler(request, ct);
	}

	#endregion
}
