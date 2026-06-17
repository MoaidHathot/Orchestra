using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Copilot;
using Orchestra.Engine;
using Orchestra.OpenCode;
using Xunit;

namespace Orchestra.Server.Tests;

/// <summary>
/// End-to-end tests that run a real orchestration through <see cref="OrchestrationExecutor"/>
/// with a real <see cref="AgentProviderRegistry"/> wiring both the Copilot and OpenCode
/// providers — exercising per-step provider selection and "same orchestration, different
/// provider" exactly as the Server composes them. Opt-in (requires authenticated Copilot CLI
/// AND a usable <c>opencode</c>); tagged <c>Category=E2E</c> so CI filters them out.
/// </summary>
[Trait("Category", "E2E")]
public class MixedProviderOrchestrationE2ETests
{
	private readonly IScheduler _scheduler = new OrchestrationScheduler();

	private static OrchestrationExecutor BuildExecutor()
	{
		var lf = NullLoggerFactory.Instance;
		var copilot = new CopilotAgentBuilder(lf);
		var opencode = new OpenCodeAgentBuilder(lf, new OpenCodeAgentPoolOptions
		{
			DefaultMinInstances = 0,
			DefaultMaxInstancesPerRun = 1,
			FallbackProvider = "github-copilot",
		});
		var registry = new AgentProviderRegistry(
			new Dictionary<string, AgentBuilder> { ["copilot"] = copilot, ["opencode"] = opencode },
			defaultProviderName: "copilot");
		return new OrchestrationExecutor(new OrchestrationScheduler(), registry, NullOrchestrationReporter.Instance, lf);
	}

	private static PromptOrchestrationStep Step(string name, string? provider, string model, string reply, string[]? dependsOn = null) => new()
	{
		Name = name,
		Type = OrchestrationStepType.Prompt,
		DependsOn = dependsOn ?? [],
		Provider = provider,
		Model = model,
		SystemPrompt = "You are a terse assistant. Reply with exactly the requested text and nothing else.",
		UserPrompt = $"Reply with exactly: {reply}",
	};

	[MixedProviderE2EFact]
	public async Task PerStepProviders_RunsCopilotAndOpenCodeStepsInOneOrchestration()
	{
		var executor = BuildExecutor();
		var orchestration = new Orchestration
		{
			Name = "mixed-provider-e2e",
			Description = "one step on copilot, one on opencode",
			Steps =
			[
				Step("copilot-step", provider: "copilot", model: "claude-opus-4.8", reply: "copilot-ok"),
				Step("opencode-step", provider: "opencode", model: "github-copilot/claude-opus-4.8", reply: "opencode-ok", dependsOn: ["copilot-step"]),
			],
		};

		var result = await executor.ExecuteAsync(orchestration, cancellationToken: Timeout());

		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["copilot-step"].Content.Should().Contain("copilot-ok");
		result.StepResults["opencode-step"].Content.Should().Contain("opencode-ok");
	}

	[MixedProviderE2EFact]
	public async Task ChangeProvider_CopilotStyleOrchestrationRunsOnOpenCode()
	{
		// A Copilot-style orchestration (bare model id, no per-step provider) runs on OpenCode
		// simply by setting defaultProvider — the bare "claude-opus-4.8" resolves to the OpenCode
		// fallback provider (github-copilot/claude-opus-4.8).
		var executor = BuildExecutor();
		var orchestration = new Orchestration
		{
			Name = "change-provider-e2e",
			Description = "copilot-authored orchestration, run on opencode",
			DefaultProvider = "opencode",
			Steps = [Step("step", provider: null, model: "claude-opus-4.8", reply: "switched-ok")],
		};

		var result = await executor.ExecuteAsync(orchestration, cancellationToken: Timeout());

		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["step"].Content.Should().Contain("switched-ok");
	}

	private static CancellationToken Timeout() => new CancellationTokenSource(TimeSpan.FromMinutes(3)).Token;
}

/// <summary>Skips the mixed-provider E2E unless explicitly opted in via <c>ORCHESTRA_OPENCODE_E2E=1</c>.</summary>
public sealed class MixedProviderE2EFactAttribute : FactAttribute
{
	public MixedProviderE2EFactAttribute()
	{
		var optIn = Environment.GetEnvironmentVariable("ORCHESTRA_OPENCODE_E2E");
		if (optIn is not ("1" or "true"))
			Skip = "Mixed-provider E2E is opt-in. Set ORCHESTRA_OPENCODE_E2E=1 (requires authenticated Copilot + opencode).";
	}
}
