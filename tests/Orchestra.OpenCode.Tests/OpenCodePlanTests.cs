using System.Text.Json;
using System.Threading.Channels;
using FluentAssertions;
using Orchestra.Engine;
using System.Net;

namespace Orchestra.OpenCode.Tests;

public class OpenCodePlanTests
{
	[Fact]
	public async Task NativeTodos_AreCommittedOnlyOnSuccess_AndScopedToOwnedSessions()
	{
		using var http = new HttpClient(new OwnershipHandler());
		await using var client = new OpenCodeHttpClient("http://localhost", null, null, http);
		var channel = Channel.CreateUnbounded<AgentEvent>();
		var capture = new OpenCodeNativePlanCapture("root", client, channel.Writer);
		await Send(capture, "root", "failed", "session.tool.input.started", new { name = "todowrite" });
		await Send(capture, "root", "failed", "session.tool.called", new { input = new { todos = new[] { new { content = "Ignore failed update", status = "completed" } } } });
		channel.Reader.TryRead(out _).Should().BeFalse();
		await Send(capture, "root", "failed", "session.tool.failed", new { });
		channel.Reader.TryRead(out _).Should().BeFalse();
		foreach (var session in new[] { "child", "unrelated" })
		{
			await Send(capture, session, "call", "session.tool.input.started", new { name = "todowrite" });
			await Send(capture, session, "call", "session.tool.called", new { input = new { todos = new[] { new { content = "Child task", status = "in_progress" } } } });
			await Send(capture, session, "call", "session.tool.success", new { });
		}
		channel.Reader.TryRead(out var observed).Should().BeTrue();
		observed!.Plan!.ActorId.Should().Be("child");
		observed.Plan.Tasks.Single().Title.Should().Be("Child task");
		channel.Reader.TryRead(out _).Should().BeFalse();
	}

	[Fact]
	public async Task DurableReconciliation_PreventsLateOldToolEventsFromRevertingThePlan()
	{
		using var http = new HttpClient(new OwnershipHandler());
		await using var client = new OpenCodeHttpClient("http://localhost", null, null, http);
		var channel = Channel.CreateUnbounded<AgentEvent>();
		var capture = new OpenCodeNativePlanCapture("root", client, channel.Writer);
		await Send(capture, "root", "old", "session.tool.input.started", new { name = "todowrite" });
		await Send(capture, "root", "old", "session.tool.called", new { input = new { todos = new[] { new { content = "Task", status = "pending" } } } });
		capture.Reconcile([JsonSerializer.SerializeToElement(new { type = "assistant", content = new[] {
			new { type = "tool", id = "old", name = "todowrite", state = new { status = "completed", input = new { todos = new[] { new { content = "Task", status = "pending" } } } } },
			new { type = "tool", id = "new", name = "todowrite", state = new { status = "completed", input = new { todos = new[] { new { content = "Task", status = "completed" } } } } },
		} })]);
		await Send(capture, "root", "old", "session.tool.success", new { });
		channel.Reader.TryRead(out var observed).Should().BeTrue();
		observed!.Plan!.Tasks.Single().Status.Should().Be("completed");
		channel.Reader.TryRead(out _).Should().BeFalse();
	}

	private static Task Send(OpenCodeNativePlanCapture capture, string session, string id, string type, object fields)
	{
		var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(fields))!;
		data["sessionID"] = JsonSerializer.SerializeToElement(session);
		data["id"] = JsonSerializer.SerializeToElement(id);
		return capture.HandleAsync(new() { Type = type, Properties = JsonSerializer.SerializeToElement(data) }, default);
	}
	private sealed class OwnershipHandler : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
			=> Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("/child") ? "{\"data\":{\"parentID\":\"root\"}}" : "{\"data\":{}}") });
	}
	[Fact]
	public void CompatibilityTool_EmitsExplicitPlanAndDoesNotCompleteTheStep()
	{
		var channel = Channel.CreateUnbounded<AgentEvent>();
		var context = new EngineToolContext();
		var tool = new OpenCodePlanTool(channel.Writer);
		tool.Bind("session", resumed: false);
		tool.Execute("""{"tasks":[{"id":"a","title":"Inspect","status":"in_progress"}],"markdown":"# Plan\nInspect and verify."}""", context);
		tool.Execute("""{"tasks":[{"id":"a","title":"Inspect","status":"completed"}]}""", context);
		var events = new List<AgentEvent>();
		while (channel.Reader.TryRead(out var evt)) events.Add(evt);
		events.Should().HaveCount(3);
		events.Last().Plan!.Tasks.Single().Status.Should().Be("completed");
		events.Last().Plan!.Markdown.Should().Contain("Inspect and verify");
		events.Last().Plan!.Shared.Should().BeTrue();
		context.StepCompletionRequested.Should().BeFalse();
		context.HasStatusOverride.Should().BeFalse();
	}

	[Fact]
	public void InvalidToolUpdate_DoesNotPublishAnInventedPlan()
	{
		var channel = Channel.CreateUnbounded<AgentEvent>();
		var tool = new OpenCodePlanTool(channel.Writer);
		tool.Bind("session", resumed: true);
		tool.Execute("""{"tasks":[{"status":"completed"}]}""", new()).Should().StartWith("Invalid plan");
		channel.Reader.TryRead(out _).Should().BeFalse();
	}
}
