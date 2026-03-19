using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orchestra.Engine.Tests.TestHelpers;

namespace Orchestra.Engine.Tests.Executor;

public class PluginIntegrationTests
{
	private readonly IScheduler _scheduler = new OrchestrationScheduler();
	private readonly IOrchestrationReporter _reporter = Substitute.For<IOrchestrationReporter>();
	private readonly ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;

	/// <summary>
	/// Creates a StepExecutorRegistry with all built-in types, using a mock HTTP handler.
	/// </summary>
	private StepExecutorRegistry CreateRegistry(
		MockAgentBuilder agentBuilder,
		HttpMessageHandler? httpHandler = null)
	{
		var httpClient = httpHandler is not null
			? new HttpClient(httpHandler)
			: new HttpClient(new MockHttpHandler("default http response"));

		var promptExecutor = new PromptExecutor(
			agentBuilder,
			_reporter,
			DefaultPromptFormatter.Instance,
			_loggerFactory.CreateLogger<PromptExecutor>());

		return new StepExecutorRegistry()
			.Register(new PromptStepExecutor(promptExecutor))
			.Register(new HttpStepExecutor(httpClient, _reporter, _loggerFactory.CreateLogger<HttpStepExecutor>()))
			.Register(new TransformStepExecutor(_loggerFactory.CreateLogger<TransformStepExecutor>()));
	}

	[Fact]
	public async Task ExecuteAsync_TransformStepOnly_ExecutesSuccessfully()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("unused");
		var registry = CreateRegistry(agentBuilder);
		var executor = new OrchestrationExecutor(
			_scheduler, agentBuilder, _reporter, _loggerFactory,
			stepExecutorRegistry: registry);

		var orchestration = new Orchestration
		{
			Name = "transform-only",
			Description = "Single transform step",
			Steps =
			[
				new TransformOrchestrationStep
				{
					Name = "greet",
					Type = OrchestrationStepType.Transform,
					DependsOn = [],
					Template = "Hello {{param.name}}, welcome!",
					Parameters = ["name"],
				}
			]
		};

		// Act
		var result = await executor.ExecuteAsync(
			orchestration,
			new Dictionary<string, string> { ["name"] = "Alice" });

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults.Should().HaveCount(1);
		result.StepResults["greet"].Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["greet"].Content.Should().Be("Hello Alice, welcome!");
	}

	[Fact]
	public async Task ExecuteAsync_PromptThenTransform_ChainsDependencies()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("AI-generated summary");
		var registry = CreateRegistry(agentBuilder);
		var executor = new OrchestrationExecutor(
			_scheduler, agentBuilder, _reporter, _loggerFactory,
			stepExecutorRegistry: registry);

		var orchestration = new Orchestration
		{
			Name = "prompt-then-transform",
			Description = "Prompt step followed by transform",
			Steps =
			[
				new PromptOrchestrationStep
				{
					Name = "summarize",
					Type = OrchestrationStepType.Prompt,
					DependsOn = [],
					SystemPrompt = "You are a summarizer.",
					UserPrompt = "Summarize the input.",
					Model = "claude-opus-4.5",
				},
				new TransformOrchestrationStep
				{
					Name = "format",
					Type = OrchestrationStepType.Transform,
					DependsOn = ["summarize"],
					Template = "## Summary\n{{summarize.output}}",
				}
			]
		};

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults.Should().HaveCount(2);
		result.StepResults["summarize"].Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["summarize"].Content.Should().Be("AI-generated summary");
		result.StepResults["format"].Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["format"].Content.Should().Be("## Summary\nAI-generated summary");
	}

	[Fact]
	public async Task ExecuteAsync_HttpThenTransform_ChainsDependencies()
	{
		// Arrange
		var httpHandler = new MockHttpHandler("{\"status\": \"ok\", \"count\": 42}");
		var agentBuilder = new MockAgentBuilder().WithResponse("unused");
		var registry = CreateRegistry(agentBuilder, httpHandler);
		var executor = new OrchestrationExecutor(
			_scheduler, agentBuilder, _reporter, _loggerFactory,
			stepExecutorRegistry: registry);

		var orchestration = new Orchestration
		{
			Name = "http-then-transform",
			Description = "HTTP step followed by transform",
			Steps =
			[
				new HttpOrchestrationStep
				{
					Name = "fetch-data",
					Type = OrchestrationStepType.Http,
					DependsOn = [],
					Method = "GET",
					Url = "https://api.example.com/data",
				},
				new TransformOrchestrationStep
				{
					Name = "wrap-result",
					Type = OrchestrationStepType.Transform,
					DependsOn = ["fetch-data"],
					Template = "API Response: {{fetch-data.output}}",
				}
			]
		};

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults.Should().HaveCount(2);
		result.StepResults["fetch-data"].Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["fetch-data"].Content.Should().Be("{\"status\": \"ok\", \"count\": 42}");
		result.StepResults["wrap-result"].Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["wrap-result"].Content.Should().Be("API Response: {\"status\": \"ok\", \"count\": 42}");
	}

	[Fact]
	public async Task ExecuteAsync_MixedStepTypes_ExecutesInCorrectOrder()
	{
		// Arrange: Prompt + Http in parallel -> Transform depends on both
		var httpHandler = new MockHttpHandler("{\"data\": \"from-api\"}");
		var agentBuilder = new MockAgentBuilder().WithResponse("from-llm");
		var registry = CreateRegistry(agentBuilder, httpHandler);
		var executor = new OrchestrationExecutor(
			_scheduler, agentBuilder, _reporter, _loggerFactory,
			stepExecutorRegistry: registry);

		var orchestration = new Orchestration
		{
			Name = "mixed-steps",
			Description = "Prompt + Http in parallel, then Transform",
			Steps =
			[
				new PromptOrchestrationStep
				{
					Name = "analyze",
					Type = OrchestrationStepType.Prompt,
					DependsOn = [],
					SystemPrompt = "You analyze data.",
					UserPrompt = "Analyze this.",
					Model = "claude-opus-4.5",
				},
				new HttpOrchestrationStep
				{
					Name = "fetch",
					Type = OrchestrationStepType.Http,
					DependsOn = [],
					Method = "GET",
					Url = "https://api.example.com/enrichment",
				},
				new TransformOrchestrationStep
				{
					Name = "combine",
					Type = OrchestrationStepType.Transform,
					DependsOn = ["analyze", "fetch"],
					Template = "Analysis: {{analyze.output}} | Data: {{fetch.output}}",
				}
			]
		};

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults.Should().HaveCount(3);

		// Parallel steps both succeeded
		result.StepResults["analyze"].Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["analyze"].Content.Should().Be("from-llm");
		result.StepResults["fetch"].Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["fetch"].Content.Should().Be("{\"data\": \"from-api\"}");

		// Transform combined both outputs
		result.StepResults["combine"].Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["combine"].Content.Should().Be("Analysis: from-llm | Data: {\"data\": \"from-api\"}");

		// Only the terminal step (combine) should be in Results
		result.Results.Should().HaveCount(1);
		result.Results.Should().ContainKey("combine");
	}

	[Fact]
	public async Task ExecuteAsync_DefaultRegistry_IncludesAllBuiltInTypes()
	{
		// Arrange: Use a custom registry (simulating default behavior) with all 3 types
		// to verify Prompt, Http, and Transform all execute correctly together.
		var httpHandler = new MockHttpHandler("http-response-body");
		var agentBuilder = new MockAgentBuilder().WithResponse("prompt-response");
		var registry = CreateRegistry(agentBuilder, httpHandler);
		var executor = new OrchestrationExecutor(
			_scheduler, agentBuilder, _reporter, _loggerFactory,
			stepExecutorRegistry: registry);

		var orchestration = new Orchestration
		{
			Name = "all-types",
			Description = "Uses all three built-in step types",
			Steps =
			[
				new PromptOrchestrationStep
				{
					Name = "prompt-step",
					Type = OrchestrationStepType.Prompt,
					DependsOn = [],
					SystemPrompt = "System prompt.",
					UserPrompt = "User prompt.",
					Model = "claude-opus-4.5",
				},
				new HttpOrchestrationStep
				{
					Name = "http-step",
					Type = OrchestrationStepType.Http,
					DependsOn = [],
					Method = "GET",
					Url = "https://api.example.com/info",
				},
				new TransformOrchestrationStep
				{
					Name = "transform-step",
					Type = OrchestrationStepType.Transform,
					DependsOn = ["prompt-step", "http-step"],
					Template = "Prompt: {{prompt-step.output}} | Http: {{http-step.output}}",
				}
			]
		};

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults.Should().HaveCount(3);

		result.StepResults["prompt-step"].Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["prompt-step"].Content.Should().Be("prompt-response");

		result.StepResults["http-step"].Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["http-step"].Content.Should().Be("http-response-body");

		result.StepResults["transform-step"].Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["transform-step"].Content.Should().Be("Prompt: prompt-response | Http: http-response-body");
	}

	/// <summary>
	/// A mock DelegatingHandler that returns a fixed response for any HTTP request.
	/// </summary>
	private sealed class MockHttpHandler : HttpMessageHandler
	{
		private readonly string _responseBody;
		private readonly HttpStatusCode _statusCode;

		public MockHttpHandler(string responseBody, HttpStatusCode statusCode = HttpStatusCode.OK)
		{
			_responseBody = responseBody;
			_statusCode = statusCode;
		}

		protected override Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request,
			CancellationToken cancellationToken)
		{
			var response = new HttpResponseMessage(_statusCode)
			{
				Content = new StringContent(_responseBody)
			};
			return Task.FromResult(response);
		}
	}
}
