using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Orchestra.Engine.Tests.Executor;

/// <summary>
/// Coverage for the Script-step control channel (ORCHESTRA_CONTROL_FILE) — the non-LLM
/// equivalent of orchestra_complete / orchestra_set_status. Split from the main
/// <see cref="ScriptStepExecutorTests"/> file for focus. The integration tests shell out to a
/// real pwsh, which is already a prerequisite of the existing script-executor tests.
/// </summary>
public class ScriptStepExecutorControlChannelTests
{
	private static readonly OrchestrationInfo s_info = new("test-orchestration", "1.0.0", "run123", DateTimeOffset.UtcNow);
	private readonly IOrchestrationReporter _reporter = Substitute.For<IOrchestrationReporter>();

	private ScriptStepExecutor CreateExecutor() => new(_reporter, NullLoggerFactory.Instance.CreateLogger<ScriptStepExecutor>());

	private static ScriptOrchestrationStep PwshStep(string script, bool? strictMode = null) => new()
	{
		Name = "script-step",
		Type = OrchestrationStepType.Script,
		DependsOn = [],
		Parameters = [],
		Shell = "pwsh",
		Script = script,
		Arguments = [],
		Environment = [],
		StrictMode = strictMode,
	};

	private static OrchestrationExecutionContext Context(OrchestrationTempFileStore? store = null) =>
		new() { OrchestrationInfo = s_info, Parameters = new Dictionary<string, string>(), TempFileStore = store };

	// ── ScriptControlSignal.TryParse (pure) ──────────────────────────────────────

	[Theory]
	[InlineData("{\"action\":\"complete\",\"status\":\"success\",\"reason\":\"done\"}", true, ExecutionStatus.Succeeded)]
	[InlineData("{\"action\":\"complete\",\"status\":\"failed\"}", true, ExecutionStatus.Failed)]
	[InlineData("{\"action\":\"set_status\",\"status\":\"no_action\"}", false, ExecutionStatus.NoAction)]
	[InlineData("{\"action\":\"set-status\",\"status\":\"failed\"}", false, ExecutionStatus.Failed)]
	[InlineData("{\"status\":\"success\"}", false, ExecutionStatus.Succeeded)] // action defaults to set_status
	public void TryParse_ValidPayloads(string json, bool expectComplete, ExecutionStatus status)
	{
		ScriptControlSignal.TryParse(json, out var signal, out var error).Should().BeTrue(error);
		signal!.Action.Should().Be(expectComplete ? ScriptControlAction.Complete : ScriptControlAction.SetStatus);
		signal.Status.Should().Be(status);
	}

	[Theory]
	[InlineData("{\"action\":\"complete\",\"status\":\"no_action\"}")] // complete cannot no_action
	[InlineData("{\"action\":\"bogus\",\"status\":\"success\"}")]      // unknown action
	[InlineData("{\"action\":\"complete\",\"status\":\"maybe\"}")]     // unknown status
	[InlineData("not json")]                                             // malformed
	[InlineData("[]")]                                                   // not an object
	[InlineData("   ")]                                                  // empty
	public void TryParse_InvalidPayloads_ReturnFalseWithError(string json)
	{
		ScriptControlSignal.TryParse(json, out var signal, out var error).Should().BeFalse();
		signal.Should().BeNull();
		error.Should().NotBeNullOrWhiteSpace();
	}

	// ── pwsh helpers (integration) ───────────────────────────────────────────────

	[Fact]
	public async Task Helper_OrchestraComplete_Success_RequestsCompletion_AndPreservesStdout()
	{
		var result = await CreateExecutor().ExecuteAsync(
			PwshStep("Orchestra-Complete -Status success -Reason 'inbox empty'\nWrite-Output '[]'"),
			Context());

		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.OrchestrationCompleteRequested.Should().BeTrue();
		result.OrchestrationCompleteStatus.Should().Be(ExecutionStatus.Succeeded);
		result.OrchestrationCompleteReason.Should().Contain("inbox empty");
		result.OrchestrationCompleteStepName.Should().Be("script-step");
		result.Content.Should().Contain("[]", "the script's stdout is preserved as the step output");
	}

	[Fact]
	public async Task Helper_OrchestraComplete_Failed_RequestsFailedCompletion()
	{
		var result = await CreateExecutor().ExecuteAsync(
			PwshStep("Orchestra-Complete -Status failed -Reason 'fatal config error'"),
			Context());

		result.Status.Should().Be(ExecutionStatus.Failed);
		result.OrchestrationCompleteRequested.Should().BeTrue();
		result.OrchestrationCompleteStatus.Should().Be(ExecutionStatus.Failed);
		result.ErrorMessage.Should().Contain("fatal config error");
	}

	[Fact]
	public async Task Helper_OrchestraSetStatus_NoAction_ReturnsNoAction()
	{
		var result = await CreateExecutor().ExecuteAsync(
			PwshStep("Orchestra-SetStatus -Status no_action -Reason 'nothing to do'"),
			Context());

		result.Status.Should().Be(ExecutionStatus.NoAction);
		result.OrchestrationCompleteRequested.Should().BeFalse();
		result.Content.Should().Contain("nothing to do");
	}

	[Fact]
	public async Task Helper_OrchestraSetStatus_Failed_ReturnsFailed()
	{
		var result = await CreateExecutor().ExecuteAsync(
			PwshStep("Orchestra-SetStatus -Status failed -Reason 'boom'"),
			Context());

		result.Status.Should().Be(ExecutionStatus.Failed);
		result.ErrorMessage.Should().Contain("boom");
	}

	[Fact]
	public async Task Helpers_AreAvailable_EvenWhenStrictModeFalse()
	{
		// strictMode:false previously injected no preamble; the control helpers must still exist.
		var result = await CreateExecutor().ExecuteAsync(
			PwshStep("Orchestra-SetStatus -Status no_action -Reason 'strict off'", strictMode: false),
			Context());

		result.Status.Should().Be(ExecutionStatus.NoAction);
	}

	// ── raw file write (shell-agnostic contract) ─────────────────────────────────

	[Fact]
	public async Task RawJsonWrite_WithoutHelper_IsHonored()
	{
		var result = await CreateExecutor().ExecuteAsync(
			PwshStep("Set-Content -LiteralPath $env:ORCHESTRA_CONTROL_FILE -NoNewline -Value '{\"action\":\"complete\",\"status\":\"success\",\"reason\":\"raw\"}'"),
			Context());

		result.OrchestrationCompleteRequested.Should().BeTrue();
		result.OrchestrationCompleteReason.Should().Contain("raw");
	}

	[Fact]
	public async Task MalformedControlFile_FailsTheStep()
	{
		var result = await CreateExecutor().ExecuteAsync(
			PwshStep("Set-Content -LiteralPath $env:ORCHESTRA_CONTROL_FILE -NoNewline -Value 'not-json'"),
			Context());

		result.Status.Should().Be(ExecutionStatus.Failed);
		result.ErrorMessage.Should().Contain("ORCHESTRA_CONTROL_FILE");
	}

	[Fact]
	public async Task NoControlFile_NormalSuccess()
	{
		var result = await CreateExecutor().ExecuteAsync(
			PwshStep("Write-Output 'hello'"),
			Context());

		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.OrchestrationCompleteRequested.Should().BeFalse();
		result.Content.Should().Contain("hello");
	}

	[Fact]
	public async Task NonZeroExit_IgnoresControlFile()
	{
		// Even though the script writes a complete-success signal, a non-zero exit must win and
		// the control file must NOT be read.
		var result = await CreateExecutor().ExecuteAsync(
			PwshStep("Orchestra-Complete -Status success -Reason 'should be ignored'; exit 3"),
			Context());

		result.Status.Should().Be(ExecutionStatus.Failed);
		result.OrchestrationCompleteRequested.Should().BeFalse();
	}

	[Fact]
	public async Task ControlSignal_RawPayload_PersistedToRunHistory()
	{
		var baseDir = Path.Combine(Path.GetTempPath(), $"orchestra-ctrl-test-{Guid.NewGuid():N}");
		var store = new OrchestrationTempFileStore(baseDir, "test-orchestration", "run123");
		try
		{
			var result = await CreateExecutor().ExecuteAsync(
				PwshStep("Orchestra-SetStatus -Status no_action -Reason 'audit me'"),
				Context(store));

			result.Status.Should().Be(ExecutionStatus.NoAction);

			var files = store.GetFilesForStep("script-step");
			files.Should().ContainSingle(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
			var persisted = File.ReadAllText(files.Single());
			persisted.Should().Contain("no_action").And.Contain("audit me");
			result.SavedFiles.Should().Contain(files.Single());
		}
		finally
		{
			try { Directory.Delete(baseDir, recursive: true); } catch { /* best effort */ }
		}
	}
}
