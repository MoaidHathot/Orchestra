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
		HttpMessageHandler? httpHandler = null,
		IMcpResolver? mcpResolver = null)
	{
		var httpClient = httpHandler is not null
			? new HttpClient(httpHandler)
			: new HttpClient(new MockHttpHandler("default http response"));

		var promptExecutor = new PromptExecutor(
			agentBuilder,
			_reporter,
			DefaultPromptFormatter.Instance,
			_loggerFactory.CreateLogger<PromptExecutor>(),
			mcpResolver: mcpResolver);

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

	[Fact]
	public async Task ExecuteAsync_PromptThenPrompt_InlineTemplateExpansion()
	{
		// Arrange: Prompt A produces output, Prompt B uses {{A.output}} inline in its userPrompt.
		// This tests the exact scenario where one Prompt step depends on another and
		// references its output via inline template expansion.
		var callCount = 0;
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			var response = Interlocked.Increment(ref callCount) == 1
				? "[\"770343639\", \"760607426\"]"
				: "processed the incidents";
			return MockAgentBuilderExtensions.CreateWithResponse(response)
				.BuildAgentAsync(ct).Result.SendAsync(prompt, ct);
		});

		var registry = CreateRegistry(agentBuilder);
		var executor = new OrchestrationExecutor(
			_scheduler, agentBuilder, _reporter, _loggerFactory,
			stepExecutorRegistry: registry);

		var orchestration = new Orchestration
		{
			Name = "prompt-chain",
			Description = "Prompt A -> Prompt B with inline template",
			Steps =
			[
				new PromptOrchestrationStep
				{
					Name = "check-watchlist",
					Type = OrchestrationStepType.Prompt,
					DependsOn = [],
					SystemPrompt = "You are a gate check.",
					UserPrompt = "Check the watchlist.",
					Model = "claude-opus-4.5",
				},
				new PromptOrchestrationStep
				{
					Name = "load-previous-snapshots",
					Type = OrchestrationStepType.Prompt,
					DependsOn = ["check-watchlist"],
					SystemPrompt = "You retrieve snapshots.",
					UserPrompt = "Retrieve snapshots for:\n\n{{check-watchlist.output}}",
					Model = "claude-opus-4.5",
				}
			]
		};

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults.Should().HaveCount(2);
		result.StepResults["check-watchlist"].Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["check-watchlist"].Content.Should().Be("[\"770343639\", \"760607426\"]");
		result.StepResults["load-previous-snapshots"].Status.Should().Be(ExecutionStatus.Succeeded);

		// The PromptSent should contain the resolved output, not the literal template
		var promptSent = result.StepResults["load-previous-snapshots"].PromptSent;
		promptSent.Should().NotBeNull();
		promptSent.Should().Contain("[\"770343639\", \"760607426\"]");
		promptSent.Should().NotContain("{{check-watchlist.output}}");
	}

	[Fact]
	public async Task ExecuteAsync_PromptThenParallelPrompts_BothResolveInlineTemplates()
	{
		// Arrange: Prompt A -> Prompt B + Prompt C in parallel.
		// Both B and C use {{A.output}} inline. This mirrors the icm-tracker scenario
		// where check-watchlist fans out to fetch-incident-details and load-previous-snapshots.
		var callCount = 0;
		string? capturedPromptB = null;
		string? capturedPromptC = null;
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			var n = Interlocked.Increment(ref callCount);
			string response;
			if (n == 1)
			{
				response = "[\"INC001\", \"INC002\"]";
			}
			else
			{
				// Track which prompt was sent to B vs C
				if (prompt.Contains("Fetch details"))
				{
					capturedPromptB = prompt;
					response = "{\"INC001\": {\"state\": \"Active\"}}";
				}
				else
				{
					capturedPromptC = prompt;
					response = "{\"INC001\": null}";
				}
			}
			return MockAgentBuilderExtensions.CreateWithResponse(response)
				.BuildAgentAsync(ct).Result.SendAsync(prompt, ct);
		});

		var registry = CreateRegistry(agentBuilder);
		var executor = new OrchestrationExecutor(
			_scheduler, agentBuilder, _reporter, _loggerFactory,
			stepExecutorRegistry: registry);

		var orchestration = new Orchestration
		{
			Name = "fan-out-with-templates",
			Description = "A -> B+C with inline templates",
			Steps =
			[
				new PromptOrchestrationStep
				{
					Name = "check-watchlist",
					Type = OrchestrationStepType.Prompt,
					DependsOn = [],
					SystemPrompt = "Gate check.",
					UserPrompt = "Check the watchlist.",
					Model = "claude-opus-4.5",
				},
				new PromptOrchestrationStep
				{
					Name = "fetch-incident-details",
					Type = OrchestrationStepType.Prompt,
					DependsOn = ["check-watchlist"],
					SystemPrompt = "Data collector.",
					UserPrompt = "Fetch details for:\n\n{{check-watchlist.output}}",
					Model = "claude-opus-4.5",
				},
				new PromptOrchestrationStep
				{
					Name = "load-previous-snapshots",
					Type = OrchestrationStepType.Prompt,
					DependsOn = ["check-watchlist"],
					SystemPrompt = "Retrieval assistant.",
					UserPrompt = "Retrieve snapshots for:\n\n{{check-watchlist.output}}",
					Model = "claude-opus-4.5",
				}
			]
		};

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults.Should().HaveCount(3);

		// check-watchlist completed successfully
		result.StepResults["check-watchlist"].Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["check-watchlist"].Content.Should().Be("[\"INC001\", \"INC002\"]");

		// Both parallel steps completed
		result.StepResults["fetch-incident-details"].Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["load-previous-snapshots"].Status.Should().Be(ExecutionStatus.Succeeded);

		// Both should have resolved {{check-watchlist.output}} in their prompts
		capturedPromptB.Should().NotBeNull();
		capturedPromptB.Should().Contain("[\"INC001\", \"INC002\"]");
		capturedPromptB.Should().NotContain("{{check-watchlist.output}}");

		capturedPromptC.Should().NotBeNull();
		capturedPromptC.Should().Contain("[\"INC001\", \"INC002\"]");
		capturedPromptC.Should().NotContain("{{check-watchlist.output}}");
	}

	[Fact]
	public async Task ExecuteAsync_CommandThenPrompt_InlineTemplateExpansion()
	{
		// Arrange: Command step produces output, Prompt step uses {{command.output}} inline.
		// This mirrors the icm-tracker's load-watchlist (Command) -> check-watchlist (Prompt).
		var agentBuilder = new MockAgentBuilder();
		string? capturedPrompt = null;
		agentBuilder.WithHandler((prompt, ct) =>
		{
			capturedPrompt = prompt;
			return MockAgentBuilderExtensions.CreateWithResponse("[\"INC123\"]")
				.BuildAgentAsync(ct).Result.SendAsync(prompt, ct);
		});

		var registry = CreateRegistry(agentBuilder);

		// Also register CommandStepExecutor
		var commandExecutor = new CommandStepExecutor(
			_reporter,
			_loggerFactory.CreateLogger<CommandStepExecutor>());
		registry.Register(commandExecutor);

		var executor = new OrchestrationExecutor(
			_scheduler, agentBuilder, _reporter, _loggerFactory,
			stepExecutorRegistry: registry);

		var orchestration = new Orchestration
		{
			Name = "command-then-prompt",
			Description = "Command -> Prompt with inline template",
			Steps =
			[
				new CommandOrchestrationStep
				{
					Name = "load-watchlist",
					Type = OrchestrationStepType.Command,
					DependsOn = [],
					Command = "pwsh",
					Arguments = ["-NoProfile", "-Command", "Write-Output '{\"incidents\": [\"INC001\"]}'"],
				},
				new PromptOrchestrationStep
				{
					Name = "check-watchlist",
					Type = OrchestrationStepType.Prompt,
					DependsOn = ["load-watchlist"],
					SystemPrompt = "Gate check.",
					UserPrompt = "Review the watchlist:\n\n{{load-watchlist.output}}\n\nOutput the incident IDs as JSON.",
					Model = "claude-opus-4.5",
				}
			]
		};

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["load-watchlist"].Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["load-watchlist"].Content.Should().Contain("INC001");
		result.StepResults["check-watchlist"].Status.Should().Be(ExecutionStatus.Succeeded);

		// The prompt should contain the resolved command output
		capturedPrompt.Should().NotBeNull();
		capturedPrompt.Should().Contain("INC001");
		capturedPrompt.Should().NotContain("{{load-watchlist.output}}");
	}

	[Fact]
	public async Task ExecuteAsync_ThreeStepChain_EachStepResolvesUpstreamOutput()
	{
		// Arrange: A -> B -> C, each referencing the previous step's output inline.
		var callCount = 0;
		string? capturedPromptB = null;
		string? capturedPromptC = null;
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			var n = Interlocked.Increment(ref callCount);
			string response;
			switch (n)
			{
				case 1:
					response = "step-a-data";
					break;
				case 2:
					capturedPromptB = prompt;
					response = "step-b-data";
					break;
				default:
					capturedPromptC = prompt;
					response = "step-c-data";
					break;
			}
			return MockAgentBuilderExtensions.CreateWithResponse(response)
				.BuildAgentAsync(ct).Result.SendAsync(prompt, ct);
		});

		var registry = CreateRegistry(agentBuilder);
		var executor = new OrchestrationExecutor(
			_scheduler, agentBuilder, _reporter, _loggerFactory,
			stepExecutorRegistry: registry);

		var orchestration = new Orchestration
		{
			Name = "three-step-chain",
			Description = "A -> B -> C with template resolution at each level",
			Steps =
			[
				new PromptOrchestrationStep
				{
					Name = "step-a",
					Type = OrchestrationStepType.Prompt,
					DependsOn = [],
					SystemPrompt = "Step A.",
					UserPrompt = "Produce data.",
					Model = "claude-opus-4.5",
				},
				new PromptOrchestrationStep
				{
					Name = "step-b",
					Type = OrchestrationStepType.Prompt,
					DependsOn = ["step-a"],
					SystemPrompt = "Step B.",
					UserPrompt = "Process A's output: {{step-a.output}}",
					Model = "claude-opus-4.5",
				},
				new PromptOrchestrationStep
				{
					Name = "step-c",
					Type = OrchestrationStepType.Prompt,
					DependsOn = ["step-b"],
					SystemPrompt = "Step C.",
					UserPrompt = "Finalize from B: {{step-b.output}} and A: {{step-a.output}}",
					Model = "claude-opus-4.5",
				}
			]
		};

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);

		// Step B should have resolved {{step-a.output}}
		capturedPromptB.Should().NotBeNull();
		capturedPromptB.Should().Contain("step-a-data");
		capturedPromptB.Should().NotContain("{{step-a.output}}");

		// Step C should have resolved both {{step-b.output}} and {{step-a.output}} (transitive)
		capturedPromptC.Should().NotBeNull();
		capturedPromptC.Should().Contain("step-b-data");
		capturedPromptC.Should().Contain("step-a-data");
		capturedPromptC.Should().NotContain("{{step-b.output}}");
		capturedPromptC.Should().NotContain("{{step-a.output}}");
	}

	#region IMcpResolver End-to-End Integration

	[Fact]
	public async Task ExecuteAsync_WithMcpResolver_GlobalMcpsResolvedToProxyInSingleStep()
	{
		// Arrange — global MCP should be replaced with proxy endpoint by resolver
		var globalMcp = new LocalMcp { Name = "azdo", Type = McpType.Local, Command = "azdo-mcp", Arguments = [] };
		var proxyMcp = new RemoteMcp { Name = "orchestra-mcp-proxy", Type = McpType.Remote, Endpoint = "http://localhost:5555/mcp", Headers = [] };

		var resolver = Substitute.For<IMcpResolver>();
		resolver.Resolve(Arg.Any<Mcp[]>()).Returns(new Mcp[] { proxyMcp });

		var agentBuilder = new MockAgentBuilder().WithResponse("Step output");
		var registry = CreateRegistry(agentBuilder, mcpResolver: resolver);
		var executor = new OrchestrationExecutor(
			_scheduler, agentBuilder, _reporter, _loggerFactory,
			stepExecutorRegistry: registry);

		var orchestration = new Orchestration
		{
			Name = "mcp-resolver-test",
			Description = "Single step with global MCP",
			Steps =
			[
				new PromptOrchestrationStep
				{
					Name = "step1",
					Type = OrchestrationStepType.Prompt,
					DependsOn = [],
					Parameters = [],
					SystemPrompt = "System",
					UserPrompt = "Prompt",
					Model = "claude-opus-4.5",
					Mcps = [globalMcp]
				}
			]
		};

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		resolver.Received(1).Resolve(Arg.Any<Mcp[]>());

		// Verify the agent received the proxy MCP, not the original global
		agentBuilder.CapturedMcps.Should().HaveCount(1);
		agentBuilder.CapturedMcps[0].Should().BeOfType<RemoteMcp>();
		agentBuilder.CapturedMcps[0].Name.Should().Be("orchestra-mcp-proxy");
	}

	[Fact]
	public async Task ExecuteAsync_WithMcpResolver_MultiStepOrchestration_EachStepResolved()
	{
		// Arrange — Two sequential steps, each with a global MCP.
		// The resolver should be called once per step.
		var globalMcp = new LocalMcp { Name = "icm", Type = McpType.Local, Command = "icm-mcp", Arguments = [] };
		var proxyMcp = new RemoteMcp { Name = "orchestra-mcp-proxy", Type = McpType.Remote, Endpoint = "http://localhost:6666/mcp", Headers = [] };

		var resolver = Substitute.For<IMcpResolver>();
		resolver.Resolve(Arg.Any<Mcp[]>()).Returns(new Mcp[] { proxyMcp });

		var agentBuilder = new MockAgentBuilder().WithResponse("Step output");
		var registry = CreateRegistry(agentBuilder, mcpResolver: resolver);
		var executor = new OrchestrationExecutor(
			_scheduler, agentBuilder, _reporter, _loggerFactory,
			stepExecutorRegistry: registry);

		var orchestration = new Orchestration
		{
			Name = "multi-step-mcp-resolver",
			Description = "Two steps each with global MCPs",
			Steps =
			[
				new PromptOrchestrationStep
				{
					Name = "step-a",
					Type = OrchestrationStepType.Prompt,
					DependsOn = [],
					Parameters = [],
					SystemPrompt = "System A",
					UserPrompt = "Prompt A",
					Model = "claude-opus-4.5",
					Mcps = [globalMcp]
				},
				new PromptOrchestrationStep
				{
					Name = "step-b",
					Type = OrchestrationStepType.Prompt,
					DependsOn = ["step-a"],
					Parameters = [],
					SystemPrompt = "System B",
					UserPrompt = "Prompt B: {{step-a.output}}",
					Model = "claude-opus-4.5",
					Mcps = [globalMcp]
				}
			]
		};

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert — both steps succeeded and resolver was called for each
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		resolver.Received(2).Resolve(Arg.Any<Mcp[]>());
	}

	[Fact]
	public async Task ExecuteAsync_WithMcpResolver_ParallelSteps_AllResolvedViaProxy()
	{
		// Arrange — Three parallel steps with the same global MCP.
		// All should be resolved via the same proxy.
		var globalMcp = new LocalMcp { Name = "shared-mcp", Type = McpType.Local, Command = "shared", Arguments = [] };
		var proxyMcp = new RemoteMcp { Name = "orchestra-mcp-proxy", Type = McpType.Remote, Endpoint = "http://localhost:4444/mcp", Headers = [] };

		var resolver = Substitute.For<IMcpResolver>();
		resolver.Resolve(Arg.Any<Mcp[]>()).Returns(new Mcp[] { proxyMcp });

		var agentBuilder = new MockAgentBuilder().WithResponse("Parallel output");
		var registry = CreateRegistry(agentBuilder, mcpResolver: resolver);
		var executor = new OrchestrationExecutor(
			_scheduler, agentBuilder, _reporter, _loggerFactory,
			stepExecutorRegistry: registry);

		var orchestration = new Orchestration
		{
			Name = "parallel-mcp-resolver",
			Description = "Three parallel steps sharing global MCP",
			Steps =
			[
				new PromptOrchestrationStep { Name = "A", Type = OrchestrationStepType.Prompt, DependsOn = [], Parameters = [], SystemPrompt = "S", UserPrompt = "P", Model = "claude-opus-4.5", Mcps = [globalMcp] },
				new PromptOrchestrationStep { Name = "B", Type = OrchestrationStepType.Prompt, DependsOn = [], Parameters = [], SystemPrompt = "S", UserPrompt = "P", Model = "claude-opus-4.5", Mcps = [globalMcp] },
				new PromptOrchestrationStep { Name = "C", Type = OrchestrationStepType.Prompt, DependsOn = [], Parameters = [], SystemPrompt = "S", UserPrompt = "P", Model = "claude-opus-4.5", Mcps = [globalMcp] },
			]
		};

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert — all three steps resolved
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		resolver.Received(3).Resolve(Arg.Any<Mcp[]>());
	}

	[Fact]
	public async Task ExecuteAsync_WithMcpResolver_ViaConstructorDefault_ResolverFlowsThrough()
	{
		// Arrange — Pass mcpResolver through the OrchestrationExecutor constructor
		// (the default path, not via stepExecutorRegistry). This tests the path used by
		// ExecutionApi, DataPlaneTools, TriggerManager, etc.
		var globalMcp = new LocalMcp { Name = "global-server", Type = McpType.Local, Command = "cmd", Arguments = [] };
		var proxyMcp = new RemoteMcp { Name = "orchestra-mcp-proxy", Type = McpType.Remote, Endpoint = "http://localhost:3333/mcp", Headers = [] };

		var resolver = Substitute.For<IMcpResolver>();
		resolver.Resolve(Arg.Any<Mcp[]>()).Returns(new Mcp[] { proxyMcp });

		var agentBuilder = new MockAgentBuilder().WithResponse("Result");

		// Use mcpResolver param directly on OrchestrationExecutor (no stepExecutorRegistry)
		// This is the real path: OrchestrationExecutor creates its own PromptExecutor internally
		var executor = new OrchestrationExecutor(
			_scheduler, agentBuilder, _reporter, _loggerFactory,
			mcpResolver: resolver);

		var orchestration = new Orchestration
		{
			Name = "constructor-mcp-resolver",
			Description = "Tests mcpResolver flows through OrchestrationExecutor constructor",
			Steps =
			[
				new PromptOrchestrationStep
				{
					Name = "step1",
					Type = OrchestrationStepType.Prompt,
					DependsOn = [],
					Parameters = [],
					SystemPrompt = "System",
					UserPrompt = "Prompt",
					Model = "claude-opus-4.5",
					Mcps = [globalMcp]
				}
			]
		};

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		resolver.Received(1).Resolve(Arg.Any<Mcp[]>());

		// The agent should have received the proxy MCP
		agentBuilder.CapturedMcps.Should().HaveCount(1);
		agentBuilder.CapturedMcps[0].Name.Should().Be("orchestra-mcp-proxy");
		agentBuilder.CapturedMcps[0].Should().BeOfType<RemoteMcp>();
	}

	[Fact]
	public async Task ExecuteAsync_WithoutMcpResolver_GlobalMcpsPassedAsIs()
	{
		// Arrange — No mcpResolver: global MCPs should be passed directly to agent
		var globalMcp = new LocalMcp { Name = "azdo", Type = McpType.Local, Command = "azdo-mcp", Arguments = [] };

		var agentBuilder = new MockAgentBuilder().WithResponse("Result");
		var executor = new OrchestrationExecutor(
			_scheduler, agentBuilder, _reporter, _loggerFactory);

		var orchestration = new Orchestration
		{
			Name = "no-resolver-test",
			Description = "Without resolver, MCPs pass through unchanged",
			Steps =
			[
				new PromptOrchestrationStep
				{
					Name = "step1",
					Type = OrchestrationStepType.Prompt,
					DependsOn = [],
					Parameters = [],
					SystemPrompt = "System",
					UserPrompt = "Prompt",
					Model = "claude-opus-4.5",
					Mcps = [globalMcp]
				}
			]
		};

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert — MCP passed unchanged (no proxy)
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		agentBuilder.CapturedMcps.Should().HaveCount(1);
		agentBuilder.CapturedMcps[0].Name.Should().Be("azdo");
		agentBuilder.CapturedMcps[0].Should().BeOfType<LocalMcp>();
	}

	#endregion

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
