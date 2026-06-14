using FluentAssertions;
using GitHub.Copilot;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine;
using Xunit;

namespace Orchestra.Copilot.Tests;

/// <summary>
/// Tests the per-step permission policy: the deny-list glob matching logic and the
/// BuildSessionConfig wiring (approve-all default vs a custom policy handler).
/// </summary>
public class CopilotAgentPermissionPolicyTests
{
	private static CopilotAgent CreateAgent(PermissionPolicy? policy)
	{
		return new CopilotAgent(
			clientPool: new FixedCopilotClientPool(new CopilotSdkClientAdapter(new CopilotClient(), ownsClient: false)),
			model: "test-model",
			systemPrompt: null,
			mcps: [],
			subagents: [],
			reasoningLevel: null,
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
			reasoningSummary: null,
			contextTier: null,
			workingDirectory: null,
			gitHubToken: null,
			humanInput: false,
			permissionPolicy: policy);
	}

	[Theory]
	[InlineData("shell", "rm -rf /", new[] { "shell" }, true)]   // kind match
	[InlineData("write", "C:/app/secrets.env", new[] { "*.env" }, true)] // target glob
	[InlineData("url", "https://evil.example/x", new[] { "https://evil.example/*" }, true)] // url glob
	[InlineData("SHELL", null, new[] { "shell" }, true)]         // case-insensitive
	[InlineData("read", "C:/app/file.txt", new[] { "shell", "url" }, false)] // no match
	[InlineData("read", "ok.txt", new string[0], false)]         // empty deny
	public void IsDeniedByPolicy_MatchesKindOrTargetGlobs(string kind, string? target, string[] deny, bool expected)
	{
		CopilotAgent.IsDeniedByPolicy(kind, target, deny).Should().Be(expected);
	}

	[Fact]
	public void BuildSessionConfig_NullPolicy_UsesApproveAll()
	{
		var config = CreateAgent(policy: null).BuildSessionConfig();

		config.OnPermissionRequest.Should().BeSameAs(PermissionHandler.ApproveAll);
	}

	[Fact]
	public void BuildSessionConfig_ApproveAllPolicy_UsesApproveAll()
	{
		var config = CreateAgent(new PermissionPolicy { Mode = PermissionMode.ApproveAll }).BuildSessionConfig();

		config.OnPermissionRequest.Should().BeSameAs(PermissionHandler.ApproveAll);
	}

	[Fact]
	public void BuildSessionConfig_DenyListPolicy_UsesCustomHandler()
	{
		var config = CreateAgent(new PermissionPolicy { Mode = PermissionMode.DenyList, Deny = ["shell"] }).BuildSessionConfig();

		config.OnPermissionRequest.Should().NotBeNull();
		config.OnPermissionRequest.Should().NotBeSameAs(PermissionHandler.ApproveAll);
	}

	[Fact]
	public void BuildSessionConfig_RequireHumanApproval_UsesCustomHandler()
	{
		var config = CreateAgent(new PermissionPolicy { Mode = PermissionMode.RequireHumanApproval }).BuildSessionConfig();

		config.OnPermissionRequest.Should().NotBeNull();
		config.OnPermissionRequest.Should().NotBeSameAs(PermissionHandler.ApproveAll);
	}
}
