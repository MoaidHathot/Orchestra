using System.Text.Json;
using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine.Tests.TestHelpers;

namespace Orchestra.Engine.Tests.Executor;

public class PromptStepControlsTests
{
	[Fact]
	public async Task OutputHandler_PreservesStepControlsWithoutRepeatingAutopilotOrTools()
	{
		var configs = new List<AgentBuildConfig>();
		var builder = new MockAgentBuilder().WithHandler((_, config, _) => { configs.Add(config!); return Response("{\"count\":42}"); });
		var step = new PromptOrchestrationStep
		{
			Name = "task",
			Type = OrchestrationStepType.Prompt,
			Model = "model",
			SystemPrompt = "",
			UserPrompt = "go",
			OutputHandlerPrompt = "Normalize",
			GitHubToken = "test-token",
			WorkingDirectory = Path.GetTempPath(),
			ResponseSchema = Schema,
			EnableConfigDiscovery = false,
			EnableOnDemandInstructionDiscovery = false,
			InstructionDirectories = [],
			PermissionPolicy = new() { Mode = PermissionMode.DenyList, Deny = ["shell"] },
			Sandbox = new() { Enabled = true },
			ExecutionPolicy = new() { Mode = AgentExecutionMode.Autopilot },
			HumanInput = true
		};
		var result = await Executor(builder).ExecuteAsync(step, Context());
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		configs.Should().HaveCount(2);
		var transform = configs[1];
		transform.GitHubToken.Should().Be("test-token");
		transform.WorkingDirectory.Should().Be(Path.GetFullPath(Path.GetTempPath()));
		transform.EnableConfigDiscovery.Should().BeFalse();
		transform.SandboxPolicy.Should().BeSameAs(configs[0].SandboxPolicy);
		transform.PermissionPolicy.Should().BeSameAs(configs[0].PermissionPolicy);
		transform.EngineTools.Should().BeEmpty();
		transform.HumanInput.Should().BeFalse();
		transform.ExecutionPolicy.Should().BeNull();
	}

	private const string SchemaText = """{"type":"object","properties":{"count":{"type":"integer"}},"required":["count"],"additionalProperties":false}""";
	private static JsonElement Schema => JsonSerializer.Deserialize<JsonElement>(SchemaText);
	private static OrchestrationExecutionContext Context(string? sourceDirectory = null) => new()
	{
		OrchestrationInfo = new("test", "1", "run", DateTimeOffset.UtcNow, SourceDirectory: sourceDirectory),
	};
	private static PromptOrchestrationStep Step(JsonElement? schema = null, bool? humanInput = null, string[]? enableTools = null, string? outputHandler = null) => new()
	{
		Name = "task",
		Type = OrchestrationStepType.Prompt,
		Model = "test-model",
		SystemPrompt = "",
		UserPrompt = "Do the task",
		ResponseSchema = schema,
		HumanInput = humanInput,
		EnableTools = enableTools,
		OutputHandlerPrompt = outputHandler,
	};
	private static PromptExecutor Executor(AgentBuilder builder) => new(builder, NullOrchestrationReporter.Instance,
		DefaultPromptFormatter.Instance, NullLogger<PromptExecutor>.Instance);

	[Theory]
	[InlineData("{\"count\":7}", true)]
	[InlineData("{\"count\":\"7\"}", false)]
	[InlineData("{}", false)]
	[InlineData("{\"count\":7,\"extra\":true}", false)]
	[InlineData("```json\n{\"count\":7}\n```", false)]
	[InlineData("All done", false)]
	public async Task StructuredOutput_EnforcesJsonAndSchema(string response, bool valid)
	{
		var builder = new MockAgentBuilder().WithResponse(response);
		var result = await Executor(builder).ExecuteAsync(Step(Schema), Context());
		result.Status.Should().Be(valid ? ExecutionStatus.Succeeded : ExecutionStatus.Failed);
		builder.CapturedConfig!.ResponseSchema!.Value.GetRawText().Should().Be(Schema.GetRawText());
		if (valid)
			result.Content.Should().Be(response);
		else
		{
			result.ErrorCategory.Should().Be(StepErrorCategory.ValidationError);
			result.RawContent.Should().Be(response);
			result.Trace!.FinalResponse.Should().Be(response);
		}
	}

	[Fact]
	public async Task InvalidSchema_FailsBeforeBuildingAgent()
	{
		var builder = new MockAgentBuilder().WithResponse("{}");
		var result = await Executor(builder).ExecuteAsync(Step(JsonSerializer.Deserialize<JsonElement>("{\"type\":42}")), Context());
		result.Status.Should().Be(ExecutionStatus.Failed);
		result.ErrorMessage.Should().Contain("invalid responseSchema");
		builder.CapturedConfig.Should().BeNull();
	}

	[Fact]
	public async Task OrdinaryText_IsNotForcedToJson_AndRetainsControlTools()
	{
		var builder = new MockAgentBuilder().WithResponse("Ordinary prose");
		var result = await Executor(builder).ExecuteAsync(Step(), Context());
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Be("Ordinary prose");
		builder.CapturedConfig!.ResponseSchema.Should().BeNull();
		builder.CapturedEngineTools.Select(tool => tool.Name).Should().Contain("orchestra_set_status").And.Contain("orchestra_complete");
	}

	[Fact]
	public async Task StructuredOutput_OmitsImmediateStopTools_ButRetainsFileTools()
	{
		var builder = new MockAgentBuilder().WithResponse("{\"count\":7}");
		await Executor(builder).ExecuteAsync(Step(Schema), Context());
		builder.CapturedEngineTools.Select(tool => tool.Name).Should().NotContain("orchestra_set_status").And.NotContain("orchestra_complete")
			.And.Contain("orchestra_save_file").And.Contain("orchestra_read_file");
	}

	[Fact]
	public async Task UnsupportedProvider_RejectsNativeStructuredOutputBeforeCallingAgent()
	{
		var builder = new NoStructuredOutputBuilder();
		var result = await Executor(builder).ExecuteAsync(Step(Schema), Context());
		result.Status.Should().Be(ExecutionStatus.Failed);
		result.ErrorMessage.Should().Contain(nameof(AgentBuildConfig.ResponseSchema));
		builder.CapturedConfig.Should().BeNull();
	}

	[Fact]
	public async Task StructuredOutput_AppliesToOutputHandler_AndChecksExportedContent()
	{
		var configs = new List<AgentBuildConfig>();
		var builder = new MockAgentBuilder().WithHandler((_, config, _) =>
		{
			configs.Add(config!);
			var response = configs.Count == 1 ? "{\"count\":7}" : "Handler returned invalid prose";
			return Response(response);
		});
		var result = await Executor(builder).ExecuteAsync(Step(Schema, outputHandler: "Normalize the response"), Context());
		configs.Should().HaveCount(2);
		configs.Should().OnlyContain(config => config.ResponseSchema.HasValue);
		result.Status.Should().Be(ExecutionStatus.Failed);
		result.ErrorMessage.Should().Contain("not valid JSON");
	}

	[Fact]
	public async Task StatusShortCircuit_CannotBypassResponseValidation()
	{
		var builder = new MockAgentBuilder().WithHandler((_, config, ct) =>
		{
			config!.EngineToolCtx!.SetStatus(ExecutionStatus.Succeeded, "Not JSON");
			config.EngineToolCtx.RequestStepCompletion();
			var channel = Channel.CreateUnbounded<AgentEvent>();
			channel.Writer.Complete();
			return new AgentTask(channel.Reader, Task.FromCanceled<AgentResult>(ct));
		});
		var result = await Executor(builder).ExecuteAsync(Step(Schema), Context());
		result.Status.Should().Be(ExecutionStatus.Failed);
		result.ErrorCategory.Should().Be(StepErrorCategory.ValidationError);
	}

	[Theory]
	[InlineData(null, false, false)]
	[InlineData(false, false, false)]
	[InlineData(true, false, true)]
	[InlineData(null, true, true)]
	[InlineData(false, true, false)]
	public async Task HumanInput_DefaultsToNonInteractive_WithExplicitAndLegacyOverrides(bool? humanInput, bool enableLegacyTool, bool expected)
	{
		var builder = new MockAgentBuilder().WithResponse("ok");
		await Executor(builder).ExecuteAsync(Step(humanInput: humanInput,
			enableTools: enableLegacyTool ? [RequestUserInputTool.OptInName] : []), Context());
		builder.CapturedConfig!.HumanInput.Should().Be(expected);
		if (humanInput == false)
			builder.CapturedEngineTools.Select(tool => tool.Name).Should().NotContain(RequestUserInputTool.OptInName);
	}

	[Fact]
	public async Task Discovery_ResolvesStepOverOrchestrationDefaults()
	{
		var orchestration = OrchestrationParser.ParseOrchestration("""
			{"name":"discovery","description":"precedence","defaultModel":"test-model",
			 "defaultEnableConfigDiscovery":true,"defaultEnableOnDemandInstructionDiscovery":true,
			 "steps":[{"name":"task","type":"Prompt","systemPrompt":"","userPrompt":"hello","enableConfigDiscovery":false}]}
			""", []);
		var builder = new MockAgentBuilder().WithResponse("ok");
		var executor = new OrchestrationExecutor(new OrchestrationScheduler(), builder, NullOrchestrationReporter.Instance, NullLoggerFactory.Instance);
		(await executor.ExecuteAsync(orchestration)).Status.Should().Be(ExecutionStatus.Succeeded);
		builder.CapturedConfig!.EnableConfigDiscovery.Should().BeFalse();
		builder.CapturedConfig.EnableOnDemandInstructionDiscovery.Should().BeTrue();
	}

	[Fact]
	public async Task TemplatedExplicitDirectories_ResolveFromDefinition_NotAgentWorkingDirectory()
	{
		var source = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "definitions"));
		var builder = new MockAgentBuilder().WithResponse("ok");
		var step = new PromptOrchestrationStep
		{
			Name = "task",
			Type = OrchestrationStepType.Prompt,
			Model = "test-model",
			SystemPrompt = "",
			UserPrompt = "hello",
			WorkingDirectory = Path.GetTempPath(),
			SkillDirectories = ["{{param.skills}}"],
			InstructionDirectories = ["{{param.instructions}}"],
		};
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = new("test", "1", "run", DateTimeOffset.UtcNow, SourceDirectory: source),
			Parameters = new() { ["skills"] = "./skills", ["instructions"] = "./instructions" },
		};
		await Executor(builder).ExecuteAsync(step, context);
		builder.CapturedConfig!.WorkingDirectory.Should().Be(Path.GetTempPath());
		builder.CapturedConfig.SkillDirectories.Should().Equal(Path.Combine(source, "skills"));
		builder.CapturedConfig.InstructionDirectories.Should().Equal(Path.Combine(source, "instructions"));
	}

	[Fact]
	public async Task OmittedWorkingDirectory_UsesDefinitionDirectory()
	{
		var builder = new MockAgentBuilder().WithResponse("ok");
		await Executor(builder).ExecuteAsync(Step(), Context(Path.GetTempPath()));
		builder.CapturedConfig!.WorkingDirectory.Should().Be(Path.GetFullPath(Path.GetTempPath()));
	}

	[Fact]
	public void InstructionDirectoryTemplates_AreValidatedBeforeExecution()
	{
		var orchestration = OrchestrationParser.ParseOrchestration("""
			{"name":"bad-directory","description":"Validate templates","defaultModel":"test-model",
			 "steps":[{"name":"task","type":"Prompt","systemPrompt":"","userPrompt":"hello","instructionDirectories":["{{vars.missing}}"]}]}
			""", []);
		var validation = TemplateExpressionValidator.ValidateOrchestration(orchestration);
		validation.IsValid.Should().BeFalse();
		validation.FormatErrors().Should().Contain("InstructionDirectories").And.Contain("missing");
	}

	private static AgentTask Response(string content)
	{
		var channel = Channel.CreateUnbounded<AgentEvent>();
		channel.Writer.TryWrite(new AgentEvent { Type = AgentEventType.Message, Content = content });
		channel.Writer.Complete();
		return new AgentTask(channel.Reader, Task.FromResult(new AgentResult { Content = content }));
	}

	private sealed class NoStructuredOutputBuilder : MockAgentBuilder
	{
		public override AgentProviderCapabilities GetCapabilities() => AgentProviderCapabilities.All("mock") with { StructuredOutput = false };
	}
}
