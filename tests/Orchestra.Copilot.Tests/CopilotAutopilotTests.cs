using System.Threading.Channels;
using FluentAssertions;
using GitHub.Copilot;
using Orchestra.Engine;

namespace Orchestra.Copilot.Tests;

public class CopilotAutopilotTests
{
	[Fact]
	public void LateChildContent_CannotReplaceRootJsonOrFallbackText()
	{
		var channel = Channel.CreateUnbounded<AgentEvent>();
		var handler = new CopilotSessionHandler(channel.Writer, NullOrchestrationReporter.Instance, "model", new());
		handler.HandleEvent(new AssistantMessageEvent { Data = new() { MessageId = "root", Content = "{\"count\":42}" } });
		handler.HandleEvent(new AssistantMessageEvent { AgentId = "late-child", Data = new() { MessageId = "child", Content = "child prose" } });
		handler.HandleEvent(new AssistantMessageDeltaEvent { AgentId = "late-child", Data = new() { MessageId = "child", DeltaContent = "more child prose" } });
		handler.FinalContent.Should().Be("{\"count\":42}");
		channel.Reader.TryRead(out var root).Should().BeTrue();
		root!.Actor.IsMain.Should().BeTrue();
		channel.Reader.TryRead(out var child).Should().BeTrue();
		child!.Actor.IsMain.Should().BeFalse();
		var onlyChild = new CopilotSessionHandler(Channel.CreateUnbounded<AgentEvent>().Writer, NullOrchestrationReporter.Instance, "model", new());
		onlyChild.HandleEvent(new AssistantMessageDeltaEvent { AgentId = "child", Data = new() { MessageId = "c", DeltaContent = "not root output" } });
		onlyChild.FinalContent.Should().BeNull();
	}

	[Fact]
	public async Task CleanShutdownWithoutTaskCompletion_IsNotAutopilotSuccess()
	{
		var done = new TaskCompletionSource();
		var handler = new CopilotSessionHandler(Channel.CreateUnbounded<AgentEvent>().Writer, NullOrchestrationReporter.Instance, "model", done, autopilot: true);
		handler.BindAutopilotRequest("request");
		handler.HandleEvent(new SessionShutdownEvent
		{
			Data = new SessionShutdownData
			{
				ShutdownType = ShutdownType.Routine,
				SessionStartTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
				TotalApiDuration = TimeSpan.Zero,
				CodeChanges = new ShutdownCodeChanges { FilesModified = [], LinesAdded = 0, LinesRemoved = 0 },
				ModelMetrics = new Dictionary<string, ShutdownModelMetric>(),
			}
		});
		Func<Task> wait = () => done.Task;
		await wait.Should().ThrowAsync<InvalidOperationException>().WithMessage("*before*completion*");
	}

	[Fact]
	public void Autopilot_WaitsBeyondIdle_AndIgnoresChildCompletion()
	{
		var done = new TaskCompletionSource();
		var handler = new CopilotSessionHandler(Channel.CreateUnbounded<AgentEvent>().Writer, NullOrchestrationReporter.Instance, "model", done, autopilot: true);
		handler.BindAutopilotRequest("request");
		handler.HandleEvent(new SessionIdleEvent { Data = new SessionIdleData() });
		handler.HandleEvent(new SessionAutopilotStoppedEvent { AgentId = "child", Data = new() { RequestId = "request", Reason = "task_complete" } });
		handler.HandleEvent(new SessionAutopilotStoppedEvent { Data = new() { RequestId = "previous", Reason = "task_complete" } });
		done.Task.IsCompleted.Should().BeFalse();
		handler.HandleEvent(new SessionAutopilotStoppedEvent { Data = new() { RequestId = "request", Reason = "task_complete" } });
		done.Task.IsCompletedSuccessfully.Should().BeTrue();
	}

	[Theory]
	[InlineData("max_continues")]
	[InlineData("aborted")]
	[InlineData("error")]
	public async Task Autopilot_NonCompletionStopFails(string reason)
	{
		var done = new TaskCompletionSource();
		var handler = new CopilotSessionHandler(Channel.CreateUnbounded<AgentEvent>().Writer, NullOrchestrationReporter.Instance, "model", done, autopilot: true);
		handler.HandleEvent(new SessionAutopilotStoppedEvent { Data = new() { RequestId = "request", Reason = reason } });
		done.Task.IsCompleted.Should().BeFalse();
		handler.BindAutopilotRequest("request");
		Func<Task> wait = () => done.Task;
		await wait.Should().ThrowAsync<InvalidOperationException>().WithMessage($"*{reason}*");
	}
}
