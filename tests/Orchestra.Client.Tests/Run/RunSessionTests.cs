using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Client.Run;
using Xunit;

namespace Orchestra.Client.Tests.Run;

/// <summary>
/// Unit tests for <see cref="RunSession"/>: feed it a canned SSE stream wrapped in an
/// <see cref="HttpResponseMessage"/> and verify the observer + prompter callbacks.
/// </summary>
public class RunSessionTests
{
	[Fact]
	public async Task RunAsync_HappyPath_Reports_Steps_And_Returns_Succeeded()
	{
		var sse = string.Join("",
			Frame("execution-started", """{"executionId":"abc123"}"""),
			Frame("run-context", """{"runId":"run-1","orchestrationName":"orch-1"}"""),
			Frame("step-started", """{"stepName":"build"}"""),
			Frame("step-completed", """{"stepName":"build"}"""),
			Frame("orchestration-done", """{"status":"Succeeded"}"""));
		var response = MakeResponse(sse);

		var observer = new RecordingRunObserver();
		var prompter = new StubHumanInputPrompter(_ => new HumanInputResponse("approve", null, null));
		var responder = new StubResponder();
		var session = new RunSession(observer, prompter, responder, NullLogger<RunSession>.Instance);

		var result = await session.RunAsync(response, "orch-1", CancellationToken.None);

		result.Outcome.Should().Be(RunSessionOutcome.Succeeded);
		result.FinalStatus.Should().Be("Succeeded");
		observer.ExecutionId.Should().Be("abc123");
		observer.OrchestrationName.Should().Be("orch-1");
		observer.RunId.Should().Be("run-1");
		observer.StepStarted.Should().ContainSingle().Which.Should().Be("build");
		observer.StepCompleted.Should().ContainSingle().Which.Should().Be("build");
		observer.FinalStatus.Should().Be("Succeeded");
		responder.Calls.Should().BeEmpty("no HITL pause occurred");
	}

	[Fact]
	public async Task RunAsync_AwaitingInput_Calls_Prompter_And_Responder()
	{
		var sse = string.Join("",
			Frame("run-context", """{"runId":"run-2","orchestrationName":"orch-2"}"""),
			Frame("step-started", """{"stepName":"review-deploy"}"""),
			Frame("awaiting-input", """{"orchestrationName":"orch-2","runId":"run-2","stepName":"review-deploy","kind":"Approval","prompt":"Approve?","choices":["approve","reject"],"createdAt":"2025-05-09T12:34:56Z"}"""),
			Frame("input-received", """{"stepName":"review-deploy","choice":"approve","reply":null,"respondedBy":"alice"}"""),
			Frame("step-completed", """{"stepName":"review-deploy"}"""),
			Frame("orchestration-done", """{"status":"Succeeded"}"""));
		var response = MakeResponse(sse);

		var observer = new RecordingRunObserver();
		var prompter = new StubHumanInputPrompter(info =>
		{
			info.StepName.Should().Be("review-deploy");
			info.Choices.Should().Equal("approve", "reject");
			return new HumanInputResponse("approve", null, "alice");
		});
		var responder = new StubResponder();
		var session = new RunSession(observer, prompter, responder, NullLogger<RunSession>.Instance);

		var result = await session.RunAsync(response, "orch-2", CancellationToken.None);

		result.Outcome.Should().Be(RunSessionOutcome.Succeeded);
		prompter.Calls.Should().ContainSingle();
		prompter.Calls[0].StepName.Should().Be("review-deploy");
		responder.Calls.Should().ContainSingle();
		responder.Calls[0].Info.StepName.Should().Be("review-deploy");
		responder.Calls[0].Response.Choice.Should().Be("approve");
		responder.Calls[0].Response.RespondedBy.Should().Be("alice");
		observer.AwaitingInput.Should().ContainSingle();
		observer.InputReceived.Should().ContainSingle()
			.Which.Should().Be(("review-deploy", "approve", (string?)null, "alice"));
	}

	[Fact]
	public async Task RunAsync_FreeForm_Reply_When_No_Choices()
	{
		var sse = string.Join("",
			Frame("run-context", """{"runId":"run-3","orchestrationName":"clarify"}"""),
			Frame("awaiting-input", """{"orchestrationName":"clarify","runId":"run-3","stepName":"draft","kind":"EngineTool","prompt":"What angle?","createdAt":"2025-05-09T12:35:00Z"}"""),
			Frame("input-received", """{"stepName":"draft","reply":"AI angle"}"""),
			Frame("orchestration-done", """{"status":"Succeeded"}"""));
		var response = MakeResponse(sse);

		var observer = new RecordingRunObserver();
		var prompter = new StubHumanInputPrompter(_ => new HumanInputResponse(null, "AI angle", null));
		var responder = new StubResponder();
		var session = new RunSession(observer, prompter, responder, NullLogger<RunSession>.Instance);

		var result = await session.RunAsync(response, "clarify", CancellationToken.None);

		result.Outcome.Should().Be(RunSessionOutcome.Succeeded);
		responder.Calls.Should().ContainSingle();
		responder.Calls[0].Response.Reply.Should().Be("AI angle");
		responder.Calls[0].Response.Choice.Should().BeNull();
		observer.AwaitingInput[0].Choices.Should().BeEmpty();
	}

	[Fact]
	public async Task RunAsync_NonInteractive_Returns_Abort_Outcome()
	{
		var sse = string.Join("",
			Frame("run-context", """{"runId":"run-x","orchestrationName":"orch-x"}"""),
			Frame("awaiting-input", """{"orchestrationName":"orch-x","runId":"run-x","stepName":"gate","kind":"Approval","prompt":"Approve?","choices":["approve"],"createdAt":"2025-05-09T12:35:00Z"}"""),
			Frame("orchestration-done", """{"status":"Succeeded"}"""));
		var response = MakeResponse(sse);

		var observer = new RecordingRunObserver();
		var prompter = new AbortingHumanInputPrompter();
		var responder = new StubResponder();
		var session = new RunSession(observer, prompter, responder, NullLogger<RunSession>.Instance);

		var result = await session.RunAsync(response, "orch-x", CancellationToken.None);

		result.Outcome.Should().Be(RunSessionOutcome.NonInteractiveAbort);
		result.OrchestrationName.Should().Be("orch-x");
		result.RunId.Should().Be("run-x");
		prompter.Calls.Should().ContainSingle();
		responder.Calls.Should().BeEmpty();
		observer.FinalStatus.Should().BeNull("we aborted before the terminal event");
	}

	[Fact]
	public async Task RunAsync_OrchestrationError_Returns_Errored()
	{
		var sse = string.Join("",
			Frame("run-context", """{"runId":"r","orchestrationName":"o"}"""),
			Frame("orchestration-error", """{"status":"Failed","error":"boom"}"""));
		var response = MakeResponse(sse);

		var observer = new RecordingRunObserver();
		var session = new RunSession(observer, new StubHumanInputPrompter(_ => new HumanInputResponse("x", null, null)), new StubResponder(), NullLogger<RunSession>.Instance);

		var result = await session.RunAsync(response, "o", CancellationToken.None);

		result.Outcome.Should().Be(RunSessionOutcome.Errored);
		result.ErrorMessage.Should().Be("boom");
		observer.FinalError.Should().Be("boom");
	}

	[Fact]
	public async Task RunAsync_Cancelled_Returns_NonSuccessfulTerminal()
	{
		var sse = string.Join("",
			Frame("run-context", """{"runId":"r","orchestrationName":"o"}"""),
			Frame("orchestration-cancelled", """{"status":"Cancelled","cancellation":{"kind":"Caller","reason":"user-cancel"}}"""));
		var response = MakeResponse(sse);

		var observer = new RecordingRunObserver();
		var session = new RunSession(observer, new StubHumanInputPrompter(_ => new HumanInputResponse("x", null, null)), new StubResponder(), NullLogger<RunSession>.Instance);

		var result = await session.RunAsync(response, "o", CancellationToken.None);

		result.Outcome.Should().Be(RunSessionOutcome.NonSuccessfulTerminal);
		result.FinalStatus.Should().Be("Cancelled");
		observer.CancellationReason.Should().Be("user-cancel");
	}

	[Fact]
	public async Task RunAsync_Stream_Ends_Without_Terminal_Event_Returns_Disconnected()
	{
		var sse = string.Join("",
			Frame("run-context", """{"runId":"r","orchestrationName":"o"}"""),
			Frame("step-started", """{"stepName":"a"}"""));
		var response = MakeResponse(sse);

		var observer = new RecordingRunObserver();
		var session = new RunSession(observer, new StubHumanInputPrompter(_ => new HumanInputResponse(null, null, null)), new StubResponder(), NullLogger<RunSession>.Instance);

		var result = await session.RunAsync(response, "o", CancellationToken.None);

		result.Outcome.Should().Be(RunSessionOutcome.Disconnected);
		observer.StreamInterruptReason.Should().NotBeNull();
	}

	[Fact]
	public async Task RunAsync_Skips_Heartbeats()
	{
		var sse = string.Join("",
			Frame("heartbeat", "{}"),
			Frame("run-context", """{"runId":"r","orchestrationName":"o"}"""),
			Frame("heartbeat", "{}"),
			Frame("orchestration-done", """{"status":"Succeeded"}"""));
		var response = MakeResponse(sse);

		var observer = new RecordingRunObserver();
		var session = new RunSession(observer, new StubHumanInputPrompter(_ => new HumanInputResponse(null, null, null)), new StubResponder(), NullLogger<RunSession>.Instance);

		var result = await session.RunAsync(response, "o", CancellationToken.None);

		result.Outcome.Should().Be(RunSessionOutcome.Succeeded);
		observer.Unknown.Should().NotContain(u => u.EventType == "heartbeat");
	}

	[Fact]
	public async Task RunAsync_NonSuccess_HttpStatus_Throws_Before_Streaming()
	{
		var response = new HttpResponseMessage(HttpStatusCode.NotFound)
		{
			Content = new StringContent("""{"detail":"orchestration 'foo' not found"}""", Encoding.UTF8, "application/problem+json"),
		};

		var session = new RunSession(new RecordingRunObserver(), new StubHumanInputPrompter(_ => new HumanInputResponse("x", null, null)), new StubResponder(), NullLogger<RunSession>.Instance);

		await session.Invoking(s => s.RunAsync(response, "foo", CancellationToken.None))
			.Should().ThrowAsync<HttpRequestException>()
			.Where(ex => ex.StatusCode == HttpStatusCode.NotFound);
	}

	[Fact]
	public async Task RunAsync_Cancellation_Returns_Disconnected()
	{
		// A pipe-backed body that never receives data so cancellation is the only exit path.
		var pipe = new System.IO.Pipelines.Pipe();
		var response = new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StreamContent(pipe.Reader.AsStream()),
		};
		response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");

		var observer = new RecordingRunObserver();
		var session = new RunSession(observer, new StubHumanInputPrompter(_ => new HumanInputResponse(null, null, null)), new StubResponder(), NullLogger<RunSession>.Instance);

		using var cts = new CancellationTokenSource();
		var task = session.RunAsync(response, "o", cts.Token);
		cts.CancelAfter(50);

		var result = await task;
		result.Outcome.Should().Be(RunSessionOutcome.Disconnected);
	}

	private static string Frame(string evt, string data) => $"event: {evt}\ndata: {data}\n\n";

	private static HttpResponseMessage MakeResponse(string body)
	{
		var resp = new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
		};
		return resp;
	}

	private sealed class StubResponder : IHumanInputResponder
	{
		public List<(AwaitingInputInfo Info, HumanInputResponse Response)> Calls { get; } = new();

		public Task RespondAsync(AwaitingInputInfo info, HumanInputResponse response, CancellationToken cancellationToken)
		{
			Calls.Add((info, response));
			return Task.CompletedTask;
		}
	}
}
