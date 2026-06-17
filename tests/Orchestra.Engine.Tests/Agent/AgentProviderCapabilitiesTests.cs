using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Orchestra.Engine.Tests.Agent;

/// <summary>
/// Provider-capability contract conformance.
///
/// Every <see cref="AgentBuilder"/> must declare the step-level features it supports via
/// <see cref="AgentBuilder.GetCapabilities"/>. The executor compares those capabilities against
/// each step's config and warns about anything the provider cannot honor — the safety net that
/// would have surfaced the earlier "OpenCode silently ignored step MCPs" gap as a visible warning.
/// </summary>
public class AgentProviderCapabilitiesTests
{
	private readonly IScheduler _scheduler = new OrchestrationScheduler();
	private readonly IOrchestrationReporter _reporter = Substitute.For<IOrchestrationReporter>();

	[Fact]
	public void FindUnsupported_AllCapabilities_ReturnsNothing_ForFullyLoadedConfig()
	{
		var caps = AgentProviderCapabilities.All("everything");
		var config = new AgentBuildConfig
		{
			Model = "claude-opus-4.8",
			Mcps = [new LocalMcp { Name = "m", Type = McpType.Local, Command = "cmd", Arguments = [] }],
			Subagents = [new Subagent { Name = "s", Description = "d", Prompt = "p" }],
			ReasoningLevel = ReasoningLevel.High,
			WorkingDirectory = "/tmp/work",
			SkillDirectories = ["/tmp/skills"],
			Attachments = [new BlobImageAttachment { Data = "AAEC", MimeType = "image/png" }],
			HumanInput = true,
			ExcludedTools = ["shell"],
		};

		caps.FindUnsupported(config).Should().BeEmpty();
	}

	[Fact]
	public void FindUnsupported_ReportsEachUnsupportedFeatureTheConfigUses()
	{
		// A provider that supports nothing, exercised by a config that uses several features.
		var caps = new AgentProviderCapabilities { Provider = "nothing" };
		var config = new AgentBuildConfig
		{
			Model = "m",
			Mcps = [new LocalMcp { Name = "m", Type = McpType.Local, Command = "cmd", Arguments = [] }],
			ReasoningLevel = ReasoningLevel.High,
			SkillDirectories = ["/tmp/skills"],
			ExcludedTools = ["shell"],
		};

		var unsupported = caps.FindUnsupported(config).Select(g => g.Feature).ToArray();

		unsupported.Should().Contain(nameof(AgentBuildConfig.Mcps));
		unsupported.Should().Contain(nameof(AgentBuildConfig.ReasoningLevel));
		unsupported.Should().Contain(nameof(AgentBuildConfig.SkillDirectories));
		unsupported.Should().Contain(nameof(AgentBuildConfig.ExcludedTools));
		// Features the config did not request are not reported.
		unsupported.Should().NotContain(nameof(AgentBuildConfig.Subagents));
		unsupported.Should().NotContain(nameof(AgentBuildConfig.Attachments));
	}

	[Fact]
	public void FindUnsupported_SupportedFeature_IsNotReported()
	{
		var caps = new AgentProviderCapabilities { Provider = "mcp-only", Mcps = true };
		var config = new AgentBuildConfig
		{
			Model = "m",
			Mcps = [new LocalMcp { Name = "m", Type = McpType.Local, Command = "cmd", Arguments = [] }],
		};

		caps.FindUnsupported(config).Should().BeEmpty();
	}

	[Fact]
	public void FindUnsupported_SystemPromptMode_OnlyWarnsForAppendOrCustomize_NotReplace()
	{
		// A provider that does not support non-Replace modes (e.g. OpenCode).
		var caps = new AgentProviderCapabilities { Provider = "replace-only" };

		// Replace is the universal baseline — never reported.
		caps.FindUnsupported(new AgentBuildConfig { Model = "m", SystemPromptMode = SystemPromptMode.Replace })
			.Select(g => g.Feature).Should().NotContain(nameof(AgentBuildConfig.SystemPromptMode));

		// Append and Customize are reported as unsupported.
		caps.FindUnsupported(new AgentBuildConfig { Model = "m", SystemPromptMode = SystemPromptMode.Append })
			.Select(g => g.Feature).Should().Contain(nameof(AgentBuildConfig.SystemPromptMode));
		caps.FindUnsupported(new AgentBuildConfig { Model = "m", SystemPromptMode = SystemPromptMode.Customize })
			.Select(g => g.Feature).Should().Contain(nameof(AgentBuildConfig.SystemPromptMode));

		// A provider that supports them never reports any mode.
		var full = AgentProviderCapabilities.All("full");
		full.FindUnsupported(new AgentBuildConfig { Model = "m", SystemPromptMode = SystemPromptMode.Customize })
			.Select(g => g.Feature).Should().NotContain(nameof(AgentBuildConfig.SystemPromptMode));
	}

	[Fact]
	public void SeverityOf_ClassifiesSecurityAndContractFeaturesAsError_RestAsWarning()
	{
		// Unsafe / contract-breaking to drop silently → Error.
		AgentProviderCapabilities.SeverityOf(nameof(AgentBuildConfig.Mcps)).Should().Be(CapabilityGapSeverity.Error);
		AgentProviderCapabilities.SeverityOf(nameof(AgentBuildConfig.Subagents)).Should().Be(CapabilityGapSeverity.Error);
		AgentProviderCapabilities.SeverityOf(nameof(AgentBuildConfig.ReasoningLevel)).Should().Be(CapabilityGapSeverity.Error);
		AgentProviderCapabilities.SeverityOf(nameof(AgentBuildConfig.SkillDirectories)).Should().Be(CapabilityGapSeverity.Error);
		AgentProviderCapabilities.SeverityOf(nameof(AgentBuildConfig.EngineTools)).Should().Be(CapabilityGapSeverity.Error);
		AgentProviderCapabilities.SeverityOf(nameof(AgentBuildConfig.HumanInput)).Should().Be(CapabilityGapSeverity.Error);
		AgentProviderCapabilities.SeverityOf(nameof(AgentBuildConfig.PermissionPolicy)).Should().Be(CapabilityGapSeverity.Error);
		AgentProviderCapabilities.SeverityOf(nameof(AgentBuildConfig.SandboxPolicy)).Should().Be(CapabilityGapSeverity.Error);
		AgentProviderCapabilities.SeverityOf(nameof(AgentBuildConfig.ExcludedTools)).Should().Be(CapabilityGapSeverity.Error);

		// Degrade gracefully → Warning.
		AgentProviderCapabilities.SeverityOf(nameof(AgentBuildConfig.ReasoningSummary)).Should().Be(CapabilityGapSeverity.Warning);
		AgentProviderCapabilities.SeverityOf(nameof(AgentBuildConfig.ContextTier)).Should().Be(CapabilityGapSeverity.Warning);
		AgentProviderCapabilities.SeverityOf(nameof(AgentBuildConfig.WorkingDirectory)).Should().Be(CapabilityGapSeverity.Warning);
		AgentProviderCapabilities.SeverityOf(nameof(AgentBuildConfig.GitHubToken)).Should().Be(CapabilityGapSeverity.Warning);
		AgentProviderCapabilities.SeverityOf(nameof(AgentBuildConfig.SystemPromptMode)).Should().Be(CapabilityGapSeverity.Warning);
		AgentProviderCapabilities.SeverityOf(nameof(AgentBuildConfig.Attachments)).Should().Be(CapabilityGapSeverity.Warning);
	}

	[Fact]
	public void FindUnsupported_TagsEachGapWithItsSeverity()
	{
		var caps = new AgentProviderCapabilities { Provider = "nothing" };
		var config = new AgentBuildConfig
		{
			Model = "m",
			SandboxPolicy = new SandboxPolicy(),
			ContextTier = ContextTier.LongContext,
		};

		var gaps = caps.FindUnsupported(config).ToArray();

		gaps.Single(g => g.Feature == nameof(AgentBuildConfig.SandboxPolicy)).Severity.Should().Be(CapabilityGapSeverity.Error);
		gaps.Single(g => g.Feature == nameof(AgentBuildConfig.ContextTier)).Severity.Should().Be(CapabilityGapSeverity.Warning);
	}

	[Fact]
	public async Task Executor_Warns_WhenStepUsesFeatureProviderDoesNotSupport()
	{
		// Provider declares no reasoning support; the step asks for High reasoning.
		var builder = new ConfigurableCapabilityAgentBuilder(
			new AgentProviderCapabilities { Provider = "limited", EngineTools = true });
		var registry = new AgentProviderRegistry(
			new Dictionary<string, AgentBuilder> { ["limited"] = builder },
			defaultProviderName: "limited");
		var logger = new CapturingLoggerFactory();
		var executor = new OrchestrationExecutor(_scheduler, registry, _reporter, logger);

		var result = await executor.ExecuteAsync(new Orchestration
		{
			Name = "warn",
			Description = "unsupported feature",
			DefaultProvider = "limited",
			Steps =
			[
				new PromptOrchestrationStep
				{
					Name = "a",
					Type = OrchestrationStepType.Prompt,
					SystemPrompt = "sys",
					UserPrompt = "user",
					Model = "claude-opus-4.8",
					ContextTier = ContextTier.LongContext,
				},
			],
		});

		result.Status.Should().Be(ExecutionStatus.Succeeded);
		logger.Warnings.Should().ContainSingle(w =>
			w.Contains(nameof(AgentBuildConfig.ContextTier)) && w.Contains("limited"));
	}

	[Fact]
	public async Task Executor_FailsStep_WhenProviderLacksAnErrorSeverityFeature()
	{
		// Sandbox is an Error-severity feature: silently running unsandboxed is unsafe, so a
		// provider that doesn't support it must fail the step rather than warn-and-proceed.
		var builder = new ConfigurableCapabilityAgentBuilder(
			new AgentProviderCapabilities { Provider = "limited" });
		var registry = new AgentProviderRegistry(
			new Dictionary<string, AgentBuilder> { ["limited"] = builder },
			defaultProviderName: "limited");
		var executor = new OrchestrationExecutor(_scheduler, registry, _reporter, new CapturingLoggerFactory());

		var result = await executor.ExecuteAsync(new Orchestration
		{
			Name = "fail",
			Description = "unsupported security feature",
			DefaultProvider = "limited",
			Steps =
			[
				new PromptOrchestrationStep
				{
					Name = "a",
					Type = OrchestrationStepType.Prompt,
					SystemPrompt = "sys",
					UserPrompt = "user",
					Model = "claude-opus-4.8",
					Sandbox = new SandboxPolicy(),
				},
			],
		});

		result.Status.Should().Be(ExecutionStatus.Failed);
		result.StepResults["a"].Status.Should().Be(ExecutionStatus.Failed);
		result.StepResults["a"].ErrorMessage.Should().Contain(nameof(AgentBuildConfig.SandboxPolicy));
		result.StepResults["a"].ErrorCategory.Should().Be(StepErrorCategory.ValidationError);
	}

	[Fact]
	public async Task Executor_DoesNotWarn_WhenProviderSupportsTheRequestedFeature()
	{
		var builder = new ConfigurableCapabilityAgentBuilder(
			new AgentProviderCapabilities { Provider = "reasoner", ReasoningLevel = true, EngineTools = true });
		var registry = new AgentProviderRegistry(
			new Dictionary<string, AgentBuilder> { ["reasoner"] = builder },
			defaultProviderName: "reasoner");
		var logger = new CapturingLoggerFactory();
		var executor = new OrchestrationExecutor(_scheduler, registry, _reporter, logger);

		var result = await executor.ExecuteAsync(new Orchestration
		{
			Name = "ok",
			Description = "supported feature",
			DefaultProvider = "reasoner",
			Steps =
			[
				new PromptOrchestrationStep
				{
					Name = "a",
					Type = OrchestrationStepType.Prompt,
					SystemPrompt = "sys",
					UserPrompt = "user",
					Model = "claude-opus-4.8",
					ReasoningLevel = ReasoningLevel.High,
				},
			],
		});

		result.Status.Should().Be(ExecutionStatus.Succeeded);
		logger.Warnings.Should().NotContain(w => w.Contains(nameof(AgentBuildConfig.ReasoningLevel)));
	}

	private sealed class ConfigurableCapabilityAgentBuilder(AgentProviderCapabilities capabilities) : AgentBuilder
	{
		public override AgentProviderCapabilities GetCapabilities() => capabilities;

		public override Task<IAgent> BuildAgentAsync(CancellationToken cancellationToken = default)
			=> Task.FromResult<IAgent>(new NoopAgent());

		public override Task<IAgent> BuildAgentAsync(AgentBuildConfig config, CancellationToken cancellationToken = default)
			=> Task.FromResult<IAgent>(new NoopAgent());

		private sealed class NoopAgent : IAgent
		{
			public AgentTask SendAsync(string prompt, CancellationToken cancellationToken = default)
			{
				var channel = Channel.CreateUnbounded<AgentEvent>();
				var resultTask = Task.Run(async () =>
				{
					await channel.Writer.WriteAsync(new AgentEvent { Type = AgentEventType.MessageDelta, Content = "ok" }, cancellationToken);
					channel.Writer.Complete();
					return new AgentResult { Content = "ok", ActualModel = "claude-opus-4.8" };
				}, cancellationToken);
				return new AgentTask(channel.Reader, resultTask);
			}
		}
	}

	private sealed class CapturingLoggerFactory : ILoggerFactory
	{
		private readonly List<string> _warnings = [];
		private readonly object _gate = new();

		public IReadOnlyList<string> Warnings
		{
			get { lock (_gate) { return [.. _warnings]; } }
		}

		public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

		public void AddProvider(ILoggerProvider provider) { }

		public void Dispose() { }

		private void Record(LogLevel level, string message)
		{
			if (level >= LogLevel.Warning)
			{
				lock (_gate) { _warnings.Add(message); }
			}
		}

		private sealed class CapturingLogger(CapturingLoggerFactory owner) : ILogger
		{
			public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

			public bool IsEnabled(LogLevel logLevel) => true;

			public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
				=> owner.Record(logLevel, formatter(state, exception));
		}
	}
}
