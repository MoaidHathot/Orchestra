using FluentAssertions;
using GitHub.Copilot;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine;
using Xunit;

namespace Orchestra.Copilot.Tests;

/// <summary>
/// Tests that per-step session-tuning options (reasoning summary, context tier,
/// working directory, GitHub token) flow onto the SDK <see cref="SessionConfig"/> and
/// are mirrored onto the resume path so a CLI swap+resume preserves them.
/// </summary>
public class CopilotAgentSessionTuningTests
{
	private static CopilotAgent CreateAgent(
		Orchestra.Engine.ReasoningSummaryLevel? reasoningSummary = null,
		Orchestra.Engine.ContextTier? contextTier = null,
		string? workingDirectory = null,
		string? gitHubToken = null,
		ReasoningLevel? reasoningLevel = null)
	{
		return new CopilotAgent(
			clientPool: new FixedCopilotClientPool(new CopilotSdkClientAdapter(new CopilotClient(), ownsClient: false)),
			model: "test-model",
			systemPrompt: null,
			mcps: [],
			subagents: [],
			reasoningLevel: reasoningLevel,
			systemPromptMode: null,
			systemPromptSections: null,
			reporter: NullOrchestrationReporter.Instance,
			engineTools: [],
			engineToolContext: null,
			skillDirectories: [],
			infiniteSessionConfig: null,
			attachments: [],
			swapOptions: null,
			logger: NullLoggerFactory.Instance.CreateLogger<CopilotAgent>(),
			loggerFactory: null,
			excludedTools: null,
			reasoningSummary: reasoningSummary,
			contextTier: contextTier,
			workingDirectory: workingDirectory,
			gitHubToken: gitHubToken);
	}

	// Every ReasoningLevel member must map to the exact lower-case string the Copilot CLI
	// accepts for reasoning_effort ("low" | "medium" | "high" | "xhigh" | "max"). XHigh is the
	// one member whose ToString() differs from the wire form in more than case, so this
	// theory guards the ToLowerInvariant() mapping against a future rename.
	[Theory]
	[InlineData(ReasoningLevel.Low, "low")]
	[InlineData(ReasoningLevel.Medium, "medium")]
	[InlineData(ReasoningLevel.High, "high")]
	[InlineData(ReasoningLevel.XHigh, "xhigh")]
	[InlineData(ReasoningLevel.Max, "max")]
	public void BuildSessionConfig_MapsReasoningLevelToCliEffortString(ReasoningLevel level, string expected)
	{
		var config = CreateAgent(reasoningLevel: level).BuildSessionConfig();

		config.ReasoningEffort.Should().Be(expected);
	}

	[Fact]
	public void BuildSessionConfig_NoReasoningLevel_LeavesEffortUnsetForProviderDefault()
	{
		var config = CreateAgent().BuildSessionConfig();

		config.ReasoningEffort.Should().BeNull();
	}

	[Fact]
	public void ReasoningLevel_EnumExposesAllCliEffortTiers()
	{
		// The CLI advertises five effort tiers; Orchestra must be able to request each one.
		Enum.GetNames<ReasoningLevel>().Should().BeEquivalentTo("Low", "Medium", "High", "XHigh", "Max");
	}

	[Fact]
	public void BuildResumeSessionConfig_PreservesReasoningLevel()
	{
		var agent = CreateAgent(reasoningLevel: ReasoningLevel.Max);
		var baseConfig = agent.BuildSessionConfig();

		var resume = agent.BuildResumeSessionConfig(baseConfig);

		resume.ReasoningEffort.Should().Be("max");
	}

	[Theory]
	[InlineData(Orchestra.Engine.ReasoningSummaryLevel.None)]
	[InlineData(Orchestra.Engine.ReasoningSummaryLevel.Concise)]
	[InlineData(Orchestra.Engine.ReasoningSummaryLevel.Detailed)]
	public void BuildSessionConfig_MapsReasoningSummary(Orchestra.Engine.ReasoningSummaryLevel level)
	{
		var expected = level switch
		{
			Orchestra.Engine.ReasoningSummaryLevel.None => GitHub.Copilot.ReasoningSummary.None,
			Orchestra.Engine.ReasoningSummaryLevel.Concise => GitHub.Copilot.ReasoningSummary.Concise,
			_ => GitHub.Copilot.ReasoningSummary.Detailed,
		};

		var config = CreateAgent(reasoningSummary: level).BuildSessionConfig();

		config.ReasoningSummary.Should().Be(expected);
	}

	[Theory]
	[InlineData(Orchestra.Engine.ContextTier.Default)]
	[InlineData(Orchestra.Engine.ContextTier.LongContext)]
	public void BuildSessionConfig_MapsContextTier(Orchestra.Engine.ContextTier tier)
	{
		var expected = tier == Orchestra.Engine.ContextTier.LongContext
			? GitHub.Copilot.ContextTier.LongContext
			: GitHub.Copilot.ContextTier.Default;

		var config = CreateAgent(contextTier: tier).BuildSessionConfig();

		config.ContextTier.Should().Be(expected);
	}

	[Fact]
	public void BuildSessionConfig_SetsWorkingDirectoryAndGitHubToken()
	{
		var dir = Path.GetTempPath();

		var config = CreateAgent(workingDirectory: dir, gitHubToken: "tok-123").BuildSessionConfig();

		config.WorkingDirectory.Should().Be(dir);
		config.GitHubToken.Should().Be("tok-123");
	}

	[Fact]
	public void BuildSessionConfig_Defaults_LeaveTuningUnset()
	{
		var config = CreateAgent().BuildSessionConfig();

		config.ReasoningSummary.Should().BeNull();
		config.ContextTier.Should().BeNull();
		config.WorkingDirectory.Should().BeNull();
		config.GitHubToken.Should().BeNull();
	}

	[Fact]
	public void BuildResumeSessionConfig_PreservesTuning()
	{
		var dir = Path.GetTempPath();
		var agent = CreateAgent(
			reasoningSummary: Orchestra.Engine.ReasoningSummaryLevel.Detailed,
			contextTier: Orchestra.Engine.ContextTier.LongContext,
			workingDirectory: dir,
			gitHubToken: "tok-xyz");
		var baseConfig = agent.BuildSessionConfig();

		var resume = agent.BuildResumeSessionConfig(baseConfig);

		resume.ReasoningSummary.Should().Be(GitHub.Copilot.ReasoningSummary.Detailed);
		resume.ContextTier.Should().Be(GitHub.Copilot.ContextTier.LongContext);
		resume.WorkingDirectory.Should().Be(dir);
		resume.GitHubToken.Should().Be("tok-xyz");
	}
}
