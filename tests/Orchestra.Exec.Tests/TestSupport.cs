using System.Text.Json;
using System.Threading.Channels;
using Orchestra.Client.Run;
using Orchestra.Engine;

namespace Orchestra.Exec.Tests;

/// <summary>
/// Minimal hand-rolled <see cref="AgentBuilder"/> for exec integration tests — returns canned
/// content (or throws) without needing the real Copilot CLI. Registered via
/// <see cref="ExecHooks.ConfigureServices"/> so it wins over the default Copilot builder.
/// </summary>
internal sealed class FakeAgentBuilder : AgentBuilder
{
	private readonly string _content;
	private readonly bool _throws;

	private FakeAgentBuilder(string content, bool throws)
	{
		_content = content;
		_throws = throws;
	}

	public static FakeAgentBuilder Returning(string content) => new(content, throws: false);
	public static FakeAgentBuilder Throwing() => new(string.Empty, throws: true);

	public override Task<IAgent> BuildAgentAsync(CancellationToken cancellationToken = default)
		=> Task.FromResult<IAgent>(new FakeAgent(_content, _throws));

	public override Task<IAgent> BuildAgentAsync(AgentBuildConfig config, CancellationToken cancellationToken = default)
		=> Task.FromResult<IAgent>(new FakeAgent(_content, _throws));

	private sealed class FakeAgent : IAgent
	{
		private readonly string _content;
		private readonly bool _throws;

		public FakeAgent(string content, bool throws)
		{
			_content = content;
			_throws = throws;
		}

		public AgentTask SendAsync(string prompt, CancellationToken cancellationToken = default)
		{
			var channel = Channel.CreateUnbounded<AgentEvent>();

			if (_throws)
			{
				channel.Writer.Complete();
				return new AgentTask(
					channel.Reader,
					Task.FromException<AgentResult>(new InvalidOperationException("fake agent failure")));
			}

			var resultTask = Task.Run(async () =>
			{
				await channel.Writer.WriteAsync(
					new AgentEvent { Type = AgentEventType.MessageDelta, Content = _content },
					cancellationToken);
				channel.Writer.Complete();
				return new AgentResult { Content = _content };
			}, cancellationToken);

			return new AgentTask(channel.Reader, resultTask);
		}
	}
}

/// <summary>
/// Scripted HITL prompter that always returns the same response — stands in for an
/// interactive user so the run-with-approval path can be exercised deterministically.
/// </summary>
internal sealed class ScriptedPrompter : IHumanInputPrompter
{
	private readonly HumanInputResponse _response;

	public ScriptedPrompter(string? choice = null, string? reply = null, string respondedBy = "test")
		=> _response = new HumanInputResponse(choice, reply, respondedBy);

	public Task<HumanInputResponse> PromptAsync(AwaitingInputInfo info, CancellationToken cancellationToken)
		=> Task.FromResult(_response);
}

/// <summary>
/// Helpers to author orchestration files in a temp workspace for exec tests.
/// </summary>
internal static class TestOrchestrations
{
	public static string WritePromptOrchestration(string directory, string name)
	{
		var orchestration = new
		{
			name,
			description = $"Test prompt orchestration: {name}",
			version = "1.0.0",
			model = "claude-opus-4.6",
			steps = new[]
			{
				new
				{
					name = "say-hello",
					type = "prompt",
					systemPrompt = "You are a test.",
					userPrompt = "Say hello.",
					model = "claude-opus-4.6",
				},
			},
		};
		return Write(directory, name, orchestration);
	}

	public static string WriteApprovalOrchestration(string directory, string name)
	{
		var orchestration = new
		{
			name,
			description = $"Test approval orchestration: {name}",
			version = "1.0.0",
			model = "claude-opus-4.6",
			steps = new object[]
			{
				new
				{
					name = "gate",
					type = "Approval",
					prompt = "Proceed?",
					choices = new[] { "approve", "reject" },
					onTimeout = "fail",
				},
			},
		};
		return Write(directory, name, orchestration);
	}

	/// <summary>An orchestration with a scheduler trigger (interval 1s) — used to prove it does
	/// NOT fire under exec's isolated host.</summary>
	public static string WriteScheduledOrchestration(string directory, string name)
	{
		var orchestration = new
		{
			name,
			description = $"Scheduled orchestration that must not fire: {name}",
			version = "1.0.0",
			model = "claude-opus-4.6",
			trigger = new { type = "scheduler", enabled = true, intervalSeconds = 1 },
			steps = new[]
			{
				new
				{
					name = "tick",
					type = "prompt",
					systemPrompt = "You are a test.",
					userPrompt = "Tick.",
					model = "claude-opus-4.6",
				},
			},
		};
		return Write(directory, name, orchestration);
	}

	private static string Write(string directory, string name, object orchestration)
	{
		Directory.CreateDirectory(directory);
		var path = Path.Combine(directory, $"{name}.json");
		File.WriteAllText(path, JsonSerializer.Serialize(orchestration, new JsonSerializerOptions { WriteIndented = true }));
		return path;
	}
}
