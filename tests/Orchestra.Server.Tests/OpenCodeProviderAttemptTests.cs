using System.Globalization;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Copilot;
using Orchestra.Engine;
using Orchestra.OpenCode;
using Xunit;

namespace Orchestra.Server.Tests;

/// <summary>
/// CI-safe integration test proving that a step routed to <c>provider: opencode</c> actually
/// attempts to use the OpenCode provider end-to-end — it is NOT silently substituted with
/// Copilot (the exact failure mode of the Portal bug).
///
/// It wires the <b>real</b> <see cref="AgentProviderRegistry"/> with the real
/// <see cref="CopilotAgentBuilder"/> and <see cref="OpenCodeAgentBuilder"/> — exactly as
/// <c>AddOrchestraAgentProviders()</c> composes them — but points the OpenCode CLI path at a
/// non-existent binary. Running the step therefore drives the real OpenCode adapter through
/// <see cref="OrchestrationExecutor"/> and <c>PromptExecutor</c>, which tries to spawn
/// <c>opencode serve</c> and fails fast with a distinctive OpenCode error. That error is
/// something only the OpenCode provider can produce — Copilot never would — so its presence is
/// positive proof the correct provider was selected and invoked.
///
/// Deterministic on every machine (installed opencode is bypassed via the bogus CLI path) and
/// requires no credentials, so it runs in normal CI (not tagged E2E).
/// </summary>
public class OpenCodeProviderAttemptTests
{
	private readonly IScheduler _scheduler = new OrchestrationScheduler();

	/// <summary>
	/// Builds an executor whose registry matches the production composition (copilot + opencode)
	/// but forces the OpenCode builder to spawn a guaranteed-missing binary, so any real attempt
	/// to use OpenCode fails immediately and unmistakably.
	/// </summary>
	private static (OrchestrationExecutor Executor, string BogusCliPath) BuildExecutorWithUnreachableOpenCode()
	{
		var lf = NullLoggerFactory.Instance;

		// An absolute path that cannot exist. It contains a directory separator so the OpenCode
		// bootstrap treats it as an explicit executable (no PATH search) and Process.Start fails
		// deterministically — independent of whether a real `opencode` is installed on the host.
		var bogusCliPath = Path.Combine(Path.GetTempPath(), $"orchestra-no-such-opencode-{Guid.NewGuid():N}.exe");

		var copilot = new CopilotAgentBuilder(lf);
		var opencode = new OpenCodeAgentBuilder(lf, new OpenCodeAgentPoolOptions
		{
			DefaultMinInstances = 0,
			DefaultMaxInstancesPerRun = 1,
			FallbackProvider = "github-copilot",
			CliPath = bogusCliPath,
			// Fail on the first spawn attempt: no in-provider swap retries on a dead binary.
			SwapBudgetPerStep = 0,
			EngineToolBridgeEnabled = false,
		});

		var registry = new AgentProviderRegistry(
			new Dictionary<string, AgentBuilder> { ["copilot"] = copilot, ["opencode"] = opencode },
			defaultProviderName: "copilot");

		var executor = new OrchestrationExecutor(new OrchestrationScheduler(), registry, NullOrchestrationReporter.Instance, lf);
		return (executor, bogusCliPath);
	}

	private static PromptOrchestrationStep OpenCodeStep(string name) => new()
	{
		Name = name,
		Type = OrchestrationStepType.Prompt,
		Provider = "opencode",
		// Bare model id → resolves via the OpenCode fallback provider (github-copilot).
		Model = "claude-opus-4.8",
		SystemPrompt = "You are a terse assistant.",
		UserPrompt = "Reply with exactly: ok",
	};

	[Fact]
	public async Task OpenCodeStep_ActuallyAttemptsOpenCode_NotCopilot()
	{
		var (executor, bogusCliPath) = BuildExecutorWithUnreachableOpenCode();

		var orchestration = new Orchestration
		{
			Name = "opencode-attempt",
			Description = "a single step routed to the opencode provider",
			Steps = [OpenCodeStep("research-opencode")],
		};

		var result = await executor.ExecuteAsync(orchestration, cancellationToken: Timeout());

		result.Status.Should().Be(ExecutionStatus.Failed,
			"the OpenCode binary is intentionally unreachable, so the step must fail");

		var step = result.StepResults["research-opencode"];
		step.Status.Should().Be(ExecutionStatus.Failed);

		// Positive proof the OpenCode provider was the one that ran: the failure is the OpenCode
		// adapter's own spawn error, which references launching `opencode` from the exact bogus
		// path we configured. Copilot could never produce this — so the provider was not
		// silently substituted.
		step.ErrorMessage.Should().NotBeNullOrEmpty();
		step.ErrorMessage.Should().Contain("opencode",
			"the failure must come from the OpenCode adapter, proving the step ran on OpenCode");
		step.ErrorMessage.Should().Contain(bogusCliPath,
			"the OpenCode adapter tried to spawn exactly the CLI path configured for the OpenCode provider");

		// The engine categorizes a dead OpenCode server as ClientUnhealthy (an OpenCode-adapter
		// signal), not a generic validation/unknown-provider error.
		step.ErrorCategory.Should().Be(StepErrorCategory.ClientUnhealthy);

		// Guard against a silent substitution regression: it must NOT be the "unknown provider"
		// error, nor the guardrail's provider-substitution error.
		step.ErrorMessage.Should().NotContain("Unknown agent provider");
		step.ErrorMessage.Should().NotContain("resolved it to provider");

		// The trace records that the step was both configured for AND actually run on opencode.
		step.Trace!.ConfiguredProvider.Should().Be("opencode");
		step.Trace!.ActualProvider.Should().Be("opencode");
	}

	private static CancellationToken Timeout() => new CancellationTokenSource(TimeSpan.FromMinutes(2)).Token;
}
