using System.Threading.Channels;
using FluentAssertions;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orchestra.Engine;

namespace Orchestra.Copilot.Tests;

/// <summary>
/// Tests covering the actor-attribution behaviour added to <see cref="CopilotSessionHandler"/>:
/// every emitted <see cref="AgentEvent"/> must carry an <see cref="ActorContext"/> identifying
/// whether the main agent or a specific sub-agent invocation produced it. Attribution is
/// driven by the SDK's <c>ParentToolCallId</c> field when available, otherwise by an internal
/// sub-agent stack pushed by <c>SubagentStartedEvent</c> and popped by
/// <c>SubagentCompletedEvent</c>/<c>SubagentFailedEvent</c>.
/// </summary>
public class CopilotSessionHandlerActorContextTests
{
	private readonly Channel<AgentEvent> _channel = Channel.CreateUnbounded<AgentEvent>();
	private readonly IOrchestrationReporter _reporter = Substitute.For<IOrchestrationReporter>();
	private readonly TaskCompletionSource _done = new();
	private readonly TestCaptureLogger _logger = new();
	private readonly CopilotSessionHandler _handler;

	public CopilotSessionHandlerActorContextTests()
	{
		_handler = new CopilotSessionHandler(
			_channel.Writer,
			_reporter,
			requestedModel: "claude-opus-4.6",
			done: _done,
			logger: _logger);
	}

	// ── Helpers ────────────────────────────────────────────────────────────────

	private List<AgentEvent> DrainChannel()
	{
		var events = new List<AgentEvent>();
		while (_channel.Reader.TryRead(out var evt))
			events.Add(evt);
		return events;
	}

	private static AssistantMessageDeltaEvent MessageDelta(string content, string? parentToolCallId = null) => new()
	{
		Data = new AssistantMessageDeltaData
		{
			MessageId = "msg-id",
			DeltaContent = content,
			ParentToolCallId = parentToolCallId!,
		},
	};

	private static AssistantMessageEvent Message(string content, string? parentToolCallId = null) => new()
	{
		Data = new AssistantMessageData
		{
			MessageId = "msg-id",
			Content = content,
			ParentToolCallId = parentToolCallId!,
		},
	};

	private static AssistantReasoningDeltaEvent ReasoningDelta(string content) => new()
	{
		Data = new AssistantReasoningDeltaData
		{
			ReasoningId = "r-id",
			DeltaContent = content,
		},
	};

	private static AssistantReasoningEvent Reasoning(string content) => new()
	{
		Data = new AssistantReasoningData
		{
			ReasoningId = "r-id",
			Content = content,
		},
	};

	private static ToolExecutionStartEvent ToolStart(string toolCallId, string toolName, string? parentToolCallId = null) => new()
	{
		Data = new ToolExecutionStartData
		{
			ToolCallId = toolCallId,
			ToolName = toolName,
			ParentToolCallId = parentToolCallId!,
		},
	};

	private static ToolExecutionCompleteEvent ToolComplete(string toolCallId, string? parentToolCallId = null) => new()
	{
		Data = new ToolExecutionCompleteData
		{
			ToolCallId = toolCallId,
			Success = true,
			ParentToolCallId = parentToolCallId!,
		},
	};

	private static SubagentStartedEvent SubagentStarted(string toolCallId, string agentName, string? displayName = null) => new()
	{
		Data = new SubagentStartedData
		{
			ToolCallId = toolCallId,
			AgentName = agentName,
			AgentDisplayName = displayName!,
			AgentDescription = null!,
		},
	};

	private static SubagentCompletedEvent SubagentCompleted(string toolCallId, string agentName) => new()
	{
		Data = new SubagentCompletedData
		{
			ToolCallId = toolCallId,
			AgentName = agentName,
			AgentDisplayName = null!,
		},
	};

	private static SubagentFailedEvent SubagentFailed(string toolCallId, string agentName, string error = "boom") => new()
	{
		Data = new SubagentFailedData
		{
			ToolCallId = toolCallId,
			AgentName = agentName,
			AgentDisplayName = null!,
			Error = error,
		},
	};

	private static SubagentDeselectedEvent SubagentDeselected() => new()
	{
		Data = new SubagentDeselectedData(),
	};

	// ── Main-only attribution ──────────────────────────────────────────────────

	[Fact]
	public void EventsBeforeAnySubagent_AreAttributedToMain()
	{
		_handler.HandleEvent(MessageDelta("hello "));
		_handler.HandleEvent(ReasoningDelta("thinking"));
		_handler.HandleEvent(ToolStart("call-1", "read_file"));
		_handler.HandleEvent(ToolComplete("call-1"));

		var events = DrainChannel();
		events.Should().HaveCount(4);
		events.Should().AllSatisfy(e =>
		{
			e.Actor.IsMain.Should().BeTrue();
			e.ActorAgentName.Should().BeNull();
			e.ActorToolCallId.Should().BeNull();
			e.ActorDepth.Should().Be(0);
		});
	}

	// ── Single-sub-agent attribution via the stack ────────────────────────────

	[Fact]
	public void EventsBetweenStartAndComplete_AreAttributedToSubagent()
	{
		_handler.HandleEvent(SubagentStarted("sub-1", "researcher", "Researcher"));
		_handler.HandleEvent(MessageDelta("partial answer"));
		_handler.HandleEvent(ReasoningDelta("inner thought"));
		_handler.HandleEvent(ToolStart("inner-call", "search"));
		_handler.HandleEvent(ToolComplete("inner-call"));
		_handler.HandleEvent(SubagentCompleted("sub-1", "researcher"));

		var events = DrainChannel();

		// SubagentStarted is stamped with the *parent* actor (main) so the parent
		// timeline shows the delegation point.
		var started = events.Single(e => e.Type == AgentEventType.SubagentStarted);
		started.Actor.IsMain.Should().BeTrue();

		// All inner events between start and complete are attributed to the sub-agent.
		var inner = events
			.Where(e => e.Type is AgentEventType.MessageDelta
				or AgentEventType.ReasoningDelta
				or AgentEventType.ToolExecutionStart
				or AgentEventType.ToolExecutionComplete)
			.ToList();
		inner.Should().HaveCount(4);
		inner.Should().AllSatisfy(e =>
		{
			e.ActorAgentName.Should().Be("researcher");
			e.ActorAgentDisplayName.Should().Be("Researcher");
			e.ActorToolCallId.Should().Be("sub-1");
			e.ActorDepth.Should().Be(1);
		});

		// SubagentCompleted is emitted *after* the pop, so it carries the parent (main).
		var completed = events.Single(e => e.Type == AgentEventType.SubagentCompleted);
		completed.Actor.IsMain.Should().BeTrue();
	}

	[Fact]
	public void EventsAfterSubagentCompletes_RevertToMain()
	{
		_handler.HandleEvent(SubagentStarted("sub-1", "researcher"));
		_handler.HandleEvent(SubagentCompleted("sub-1", "researcher"));
		_handler.HandleEvent(MessageDelta("back to main"));

		var events = DrainChannel();
		events.Last().Type.Should().Be(AgentEventType.MessageDelta);
		events.Last().Actor.IsMain.Should().BeTrue();
	}

	// ── SDK ParentToolCallId honored ──────────────────────────────────────────

	[Fact]
	public void ParentToolCallId_OnDelta_PinsToMatchingFrame()
	{
		// Two sub-agents active simultaneously. Without ParentToolCallId both would
		// be attributed to the top-of-stack ("inner"). With it, deltas can be pinned
		// to the outer frame.
		_handler.HandleEvent(SubagentStarted("outer", "outer-agent"));
		_handler.HandleEvent(SubagentStarted("inner", "inner-agent"));

		_handler.HandleEvent(MessageDelta("from inner")); // no parent → top
		_handler.HandleEvent(MessageDelta("from outer", parentToolCallId: "outer"));
		_handler.HandleEvent(MessageDelta("also inner", parentToolCallId: "inner"));

		var deltas = DrainChannel().Where(e => e.Type == AgentEventType.MessageDelta).ToList();
		deltas[0].ActorAgentName.Should().Be("inner-agent");
		deltas[0].ActorDepth.Should().Be(2);

		deltas[1].ActorAgentName.Should().Be("outer-agent");
		deltas[1].ActorDepth.Should().Be(1);

		deltas[2].ActorAgentName.Should().Be("inner-agent");
		deltas[2].ActorDepth.Should().Be(2);
	}

	[Fact]
	public void ParentToolCallId_NotInStack_FallsBackToCurrentTop_AndWarns()
	{
		_handler.HandleEvent(SubagentStarted("sub-1", "researcher"));
		_handler.HandleEvent(MessageDelta("delta", parentToolCallId: "ghost"));

		var delta = DrainChannel().Last(e => e.Type == AgentEventType.MessageDelta);
		// Falls back to current top of stack.
		delta.ActorAgentName.Should().Be("researcher");

		_logger.Entries.Should().Contain(e =>
			e.Level == LogLevel.Warning &&
			e.Message.Contains("ghost", StringComparison.Ordinal));
	}

	// ── Nested sub-agents ──────────────────────────────────────────────────────

	[Fact]
	public void NestedSubagents_ReportCorrectDepthAndPopOrder()
	{
		_handler.HandleEvent(SubagentStarted("outer", "outer-agent"));
		_handler.HandleEvent(MessageDelta("at depth 1"));
		_handler.HandleEvent(SubagentStarted("inner", "inner-agent"));
		_handler.HandleEvent(MessageDelta("at depth 2"));
		_handler.HandleEvent(ReasoningDelta("inner thinking"));
		_handler.HandleEvent(SubagentCompleted("inner", "inner-agent"));
		_handler.HandleEvent(MessageDelta("back at depth 1"));
		_handler.HandleEvent(SubagentCompleted("outer", "outer-agent"));
		_handler.HandleEvent(MessageDelta("back at main"));

		var deltas = DrainChannel()
			.Where(e => e.Type is AgentEventType.MessageDelta or AgentEventType.ReasoningDelta)
			.ToList();

		deltas[0].ActorAgentName.Should().Be("outer-agent");
		deltas[0].ActorDepth.Should().Be(1);

		deltas[1].ActorAgentName.Should().Be("inner-agent");
		deltas[1].ActorDepth.Should().Be(2);

		deltas[2].ActorAgentName.Should().Be("inner-agent");
		deltas[2].ActorDepth.Should().Be(2);

		deltas[3].ActorAgentName.Should().Be("outer-agent");
		deltas[3].ActorDepth.Should().Be(1);

		deltas[4].Actor.IsMain.Should().BeTrue();
	}

	// ── Out-of-order completion ────────────────────────────────────────────────

	[Fact]
	public void ParallelSiblingCompletion_RemovesCorrectFrame_WithoutWarning()
	{
		_handler.HandleEvent(SubagentStarted("outer", "outer-agent"));
		_handler.HandleEvent(SubagentStarted("inner", "inner-agent"));

		// Complete the earlier sibling first. This is normal for parallel sub-agents,
		// so the handler should remove that frame without warning and leave the latest
		// active sibling as the fallback current actor.
		_handler.HandleEvent(SubagentCompleted("outer", "outer-agent"));

		_handler.HandleEvent(MessageDelta("after weird pop"));

		var delta = DrainChannel().Last(e => e.Type == AgentEventType.MessageDelta);
		delta.ActorAgentName.Should().Be("inner-agent");
		// Depth reported is the index+1 of the surviving frame; with one frame left it must be 1.
		delta.ActorDepth.Should().Be(1);

		_logger.Entries.Should().NotContain(e => e.Level == LogLevel.Warning);
	}

	[Fact]
	public void CompletionForUnknownToolCallId_LeavesStackIntact_AndWarns()
	{
		_handler.HandleEvent(SubagentStarted("sub-1", "researcher"));
		_handler.HandleEvent(SubagentCompleted("ghost", "phantom"));
		_handler.HandleEvent(MessageDelta("still inside researcher"));

		var delta = DrainChannel().Last(e => e.Type == AgentEventType.MessageDelta);
		delta.ActorAgentName.Should().Be("researcher");

		_logger.Entries.Should().Contain(e =>
			e.Level == LogLevel.Warning &&
			e.Message.Contains("ghost", StringComparison.Ordinal));
	}

	// ── SubagentDeselected does NOT pop ────────────────────────────────────────

	[Fact]
	public void SubagentDeselected_DoesNotPopStack()
	{
		_handler.HandleEvent(SubagentStarted("sub-1", "researcher"));
		_handler.HandleEvent(SubagentDeselected());
		_handler.HandleEvent(MessageDelta("still inside researcher"));

		var delta = DrainChannel().Last(e => e.Type == AgentEventType.MessageDelta);
		delta.ActorAgentName.Should().Be("researcher");
		delta.ActorDepth.Should().Be(1);
	}

	// ── SubagentFailed pops & emits with parent actor ──────────────────────────

	[Fact]
	public void SubagentFailed_PopsAndEmitsWithParentActor()
	{
		_handler.HandleEvent(SubagentStarted("sub-1", "researcher"));
		_handler.HandleEvent(SubagentFailed("sub-1", "researcher", "kaboom"));
		_handler.HandleEvent(MessageDelta("recovered"));

		var events = DrainChannel();
		var failed = events.Single(e => e.Type == AgentEventType.SubagentFailed);
		failed.Actor.IsMain.Should().BeTrue();
		failed.ErrorMessage.Should().Be("kaboom");

		var delta = events.Last(e => e.Type == AgentEventType.MessageDelta);
		delta.Actor.IsMain.Should().BeTrue();
	}

	// ── Reasoning attribution falls back to stack only ────────────────────────

	[Fact]
	public void ReasoningEvents_AreAttributedViaStackOnly()
	{
		_handler.HandleEvent(SubagentStarted("sub-1", "researcher"));
		_handler.HandleEvent(Reasoning("complete reasoning"));
		_handler.HandleEvent(ReasoningDelta("partial"));

		var reasoningEvents = DrainChannel()
			.Where(e => e.Type is AgentEventType.Reasoning or AgentEventType.ReasoningDelta)
			.ToList();
		reasoningEvents.Should().HaveCount(2);
		reasoningEvents.Should().AllSatisfy(e =>
		{
			e.ActorAgentName.Should().Be("researcher");
			e.ActorDepth.Should().Be(1);
		});
	}

	// ── Test logger that captures structured entries ───────────────────────────

	private sealed class TestCaptureLogger : ILogger<CopilotSessionHandler>
	{
		public List<(LogLevel Level, EventId EventId, string Message)> Entries { get; } = [];

		IDisposable? ILogger.BeginScope<TState>(TState state) => NullScope.Instance;
		public bool IsEnabled(LogLevel logLevel) => true;

		public void Log<TState>(
			LogLevel logLevel,
			EventId eventId,
			TState state,
			Exception? exception,
			Func<TState, Exception?, string> formatter)
		{
			Entries.Add((logLevel, eventId, formatter(state, exception)));
		}

		private sealed class NullScope : IDisposable
		{
			public static readonly NullScope Instance = new();
			public void Dispose() { }
		}
	}
}
