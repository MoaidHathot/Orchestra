using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine;

namespace Orchestra.OpenCode.Tests;

public class OpenCodeSessionHandlerTests
{
	private const string Sid = "ses_test";

	private static (OpenCodeSessionHandler Handler, ChannelReader<AgentEvent> Reader, TaskCompletionSource Done) Create()
	{
		var channel = Channel.CreateUnbounded<AgentEvent>();
		var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var handler = new OpenCodeSessionHandler(Sid, channel.Writer, NullOrchestrationReporter.Instance, "github-copilot/claude-opus-4.8", done, NullLogger.Instance);
		return (handler, channel.Reader, done);
	}

	private static List<AgentEvent> Drain(ChannelReader<AgentEvent> reader)
	{
		var events = new List<AgentEvent>();
		while (reader.TryRead(out var e))
			events.Add(e);
		return events;
	}

	[Fact]
	public void TextParts_EmitSuffixDeltas_AndAssembleFinalContent()
	{
		var (handler, reader, _) = Create();

		handler.Handle(TestEvents.Event("message.part.updated", $$"""{ "part": { "type": "text", "id": "p1", "sessionID": "{{Sid}}", "text": "Hello" } }"""));
		handler.Handle(TestEvents.Event("message.part.updated", $$"""{ "part": { "type": "text", "id": "p1", "sessionID": "{{Sid}}", "text": "Hello, world" } }"""));

		var events = Drain(reader);
		events.Should().HaveCount(2);
		events.Should().AllSatisfy(e => e.Type.Should().Be(AgentEventType.MessageDelta));
		events[0].Content.Should().Be("Hello");
		events[1].Content.Should().Be(", world");
		handler.FinalContent.Should().Be("Hello, world");
	}

	[Fact]
	public void ReasoningPart_EmitsReasoningDelta()
	{
		var (handler, reader, _) = Create();
		handler.Handle(TestEvents.Event("message.part.updated", $$"""{ "part": { "type": "reasoning", "id": "r1", "sessionID": "{{Sid}}", "text": "thinking" } }"""));

		var events = Drain(reader);
		events.Should().ContainSingle();
		events[0].Type.Should().Be(AgentEventType.ReasoningDelta);
		events[0].Content.Should().Be("thinking");
	}

	[Fact]
	public void ToolPart_RunningThenCompleted_EmitsStartAndComplete()
	{
		var (handler, reader, _) = Create();

		handler.Handle(TestEvents.Event("message.part.updated", $$"""{ "part": { "type": "tool", "callID": "c1", "tool": "bash", "sessionID": "{{Sid}}", "state": { "status": "running", "input": { "command": "ls" } } } }"""));
		handler.Handle(TestEvents.Event("message.part.updated", $$"""{ "part": { "type": "tool", "callID": "c1", "tool": "bash", "sessionID": "{{Sid}}", "state": { "status": "completed", "output": "file1.txt" } } }"""));

		var events = Drain(reader);
		events.Should().HaveCount(2);
		events[0].Type.Should().Be(AgentEventType.ToolExecutionStart);
		events[0].ToolCallId.Should().Be("c1");
		events[0].ToolName.Should().Be("bash");
		events[1].Type.Should().Be(AgentEventType.ToolExecutionComplete);
		events[1].ToolSuccess.Should().BeTrue();
		events[1].ToolResult.Should().Be("file1.txt");
	}

	[Fact]
	public void ToolPart_Error_EmitsCompleteWithError()
	{
		var (handler, reader, _) = Create();
		handler.Handle(TestEvents.Event("message.part.updated", $$"""{ "part": { "type": "tool", "callID": "c9", "tool": "bash", "sessionID": "{{Sid}}", "state": { "status": "error", "error": "boom" } } }"""));

		var events = Drain(reader);
		events.Should().ContainSingle();
		events[0].Type.Should().Be(AgentEventType.ToolExecutionComplete);
		events[0].ToolSuccess.Should().BeFalse();
		events[0].ToolError.Should().Be("boom");
	}

	[Fact]
	public void MessageUpdated_Assistant_CapturesUsageAndModel()
	{
		var (handler, reader, _) = Create();
		handler.Handle(TestEvents.Event("message.updated", $$"""
			{ "info": { "role": "assistant", "sessionID": "{{Sid}}", "providerID": "github-copilot", "modelID": "claude-opus-4.8",
			  "cost": 0.0123, "tokens": { "input": 100, "output": 42, "reasoning": 8, "cache": { "read": 5, "write": 3 } } } }
			"""));

		handler.ActualModel.Should().Be("github-copilot/claude-opus-4.8");
		handler.Usage.Should().NotBeNull();
		handler.Usage!.InputTokens.Should().Be(100);
		handler.Usage.OutputTokens.Should().Be(42);
		handler.Usage.ReasoningTokens.Should().Be(8);
		handler.Usage.CacheReadTokens.Should().Be(5);
		handler.Usage.CacheWriteTokens.Should().Be(3);
		handler.Usage.Cost.Should().Be(0.0123);

		var events = Drain(reader);
		events.Should().ContainSingle(e => e.Type == AgentEventType.Usage);
	}

	[Fact]
	public void SessionIdle_CompletesDone()
	{
		var (handler, _, done) = Create();
		handler.Handle(TestEvents.Event("session.idle", $$"""{ "sessionID": "{{Sid}}" }"""));
		done.Task.IsCompletedSuccessfully.Should().BeTrue();
	}

	[Fact]
	public void SessionError_FaultsDoneWithSessionFailedException()
	{
		var (handler, reader, done) = Create();
		handler.Handle(TestEvents.Event("session.error", $$"""{ "sessionID": "{{Sid}}", "error": { "name": "ProviderError", "message": "rate limit exceeded" } }"""));

		done.Task.IsFaulted.Should().BeTrue();
		var ex = done.Task.Exception!.InnerException;
		ex.Should().BeOfType<OpenCodeSessionFailedException>();
		((OpenCodeSessionFailedException)ex!).Details!.TransientUpstreamFailure.Should().BeTrue();

		Drain(reader).Should().ContainSingle(e => e.Type == AgentEventType.Error);
	}

	[Fact]
	public void Events_ForOtherSession_AreIgnored()
	{
		var (handler, reader, done) = Create();
		handler.Handle(TestEvents.Event("message.part.updated", """{ "part": { "type": "text", "id": "p1", "sessionID": "OTHER", "text": "nope" } }"""));
		handler.Handle(TestEvents.Event("session.idle", """{ "sessionID": "OTHER" }"""));

		Drain(reader).Should().BeEmpty();
		done.Task.IsCompleted.Should().BeFalse();
		handler.FinalContent.Should().BeNull();
	}

	[Fact]
	public void PermissionUpdated_EmitsPermissionRequested()
	{
		var (handler, reader, _) = Create();
		handler.Handle(TestEvents.Event("permission.updated", $$"""{ "sessionID": "{{Sid}}", "id": "perm1", "type": "bash", "title": "rm -rf /" }"""));

		var events = Drain(reader);
		events.Should().ContainSingle();
		events[0].Type.Should().Be(AgentEventType.PermissionRequested);
		events[0].PermissionRequestId.Should().Be("perm1");
		events[0].PermissionKind.Should().Be("bash");
		events[0].PermissionTarget.Should().Be("rm -rf /");
	}

	[Fact]
	public void TaskTool_EmitsSubagentStarted_OutputDelta_AndCompleted()
	{
		var (handler, reader, _) = Create();
		handler.Handle(TestEvents.Event("message.part.updated", $$"""{ "part": { "type": "tool", "callID": "t1", "tool": "task", "sessionID": "{{Sid}}", "state": { "status": "running", "input": { "subagent_type": "researcher", "description": "find data" } } } }"""));
		handler.Handle(TestEvents.Event("message.part.updated", $$"""{ "part": { "type": "tool", "callID": "t1", "tool": "task", "sessionID": "{{Sid}}", "state": { "status": "completed", "output": "done" } } }"""));

		var events = Drain(reader);
		events.Should().HaveCount(3);

		events[0].Type.Should().Be(AgentEventType.SubagentStarted);
		events[0].SubagentName.Should().Be("researcher");
		events[0].SubagentDescription.Should().Be("find data");

		// The sub-agent's result (task output) is surfaced as actor-attributed content so the
		// Portal renders it in the sub-agent card rather than showing "No output produced".
		events[1].Type.Should().Be(AgentEventType.MessageDelta);
		events[1].Content.Should().Be("done");
		events[1].ActorAgentName.Should().Be("researcher");
		events[1].ActorToolCallId.Should().Be("t1");
		events[1].ActorDepth.Should().Be(1);

		events[2].Type.Should().Be(AgentEventType.SubagentCompleted);
		events[2].SubagentName.Should().Be("researcher");
	}

	[Fact]
	public void TaskTool_CompletedWithoutOutput_EmitsNoContentDelta()
	{
		var (handler, reader, _) = Create();
		handler.Handle(TestEvents.Event("message.part.updated", $$"""{ "part": { "type": "tool", "callID": "t2", "tool": "task", "sessionID": "{{Sid}}", "state": { "status": "running", "input": { "subagent_type": "researcher" } } } }"""));
		handler.Handle(TestEvents.Event("message.part.updated", $$"""{ "part": { "type": "tool", "callID": "t2", "tool": "task", "sessionID": "{{Sid}}", "state": { "status": "completed" } } }"""));

		var events = Drain(reader);
		events.Should().HaveCount(2);
		events[0].Type.Should().Be(AgentEventType.SubagentStarted);
		events[1].Type.Should().Be(AgentEventType.SubagentCompleted);
		events.Should().NotContain(e => e.Type == AgentEventType.MessageDelta);
	}

	[Fact]
	public void TaskTool_Error_EmitsSubagentFailed_WithNameFromStart()
	{
		var (handler, reader, _) = Create();
		// The completion frame omits input; the sub-agent name must come from the start frame.
		handler.Handle(TestEvents.Event("message.part.updated", $$"""{ "part": { "type": "tool", "callID": "t3", "tool": "task", "sessionID": "{{Sid}}", "state": { "status": "running", "input": { "subagent_type": "researcher", "description": "find data" } } } }"""));
		handler.Handle(TestEvents.Event("message.part.updated", $$"""{ "part": { "type": "tool", "callID": "t3", "tool": "task", "sessionID": "{{Sid}}", "state": { "status": "error", "error": "boom" } } }"""));

		var events = Drain(reader);
		events.Should().HaveCount(2);
		events[0].Type.Should().Be(AgentEventType.SubagentStarted);
		events[1].Type.Should().Be(AgentEventType.SubagentFailed);
		events[1].SubagentName.Should().Be("researcher");
		events[1].ErrorMessage.Should().Be("boom");
	}
}
