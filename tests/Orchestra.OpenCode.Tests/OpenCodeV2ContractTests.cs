using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine;

namespace Orchestra.OpenCode.Tests;

public class OpenCodeV2ContractTests
{
	[Fact]
	public void PriorSuccessfulTurn_CannotCompleteANewerPrompt()
	{
		var handler = NewHandler(new TaskCompletionSource());
		var messages = new[] { Element("""{"type":"user","id":"old"}"""),
			Element("""{"type":"assistant","content":[{"type":"text","text":"old answer"}]}"""),
			Element("""{"type":"idle","outcome":"succeeded"}"""), Element("""{"type":"user","id":"new"}""") };
		Action missing = () => handler.ApplyMessages(messages, "never-admitted");
		missing.Should().Throw<OpenCodeSessionFailedException>().WithMessage("*no durable completion*");
		Action pending = () => handler.ApplyMessages(messages, "new");
		pending.Should().Throw<OpenCodeSessionFailedException>().WithMessage("*no durable completion*");
	}

	[Fact]
	public void NativeForms_MapLabelsAndTypedAnswers()
	{
		OpenCodeFormAnswers.ConvertValue("string", "Production", [("prod-id", "Production")]).Should().Be("prod-id");
		OpenCodeFormAnswers.ConvertValue("integer", "42", []).Should().Be(42L);
		OpenCodeFormAnswers.ConvertValue("boolean", "yes", []).Should().Be(true);
		OpenCodeFormAnswers.ConvertValue("multiselect", "Alpha, Beta", [("a", "Alpha"), ("b", "Beta")]).Should().BeEquivalentTo(new[] { "a", "b" });
		Action invalid = () => OpenCodeFormAnswers.ConvertValue("integer", "forty two", []);
		invalid.Should().Throw<FormatException>();
	}

	[Fact]
	public void ExcludedSubagentTool_CannotBeReenabledByInlineAgentScope()
	{
		var plan = OpenCodeConfigBuilder.Build(new("github-copilot", "model"), "system", null,
			[new Subagent { Name = "reviewer", Prompt = "Review" }], [], ["subagent"], "github-copilot");
		var json = JsonSerializer.SerializeToElement(plan.Config);
		var last = json.GetProperty("agents").GetProperty("orchestra-primary").GetProperty("permissions").EnumerateArray().Last();
		last.GetProperty("action").GetString().Should().Be("subagent");
		last.GetProperty("effect").GetString().Should().Be("deny");
	}

	[Fact]
	public async Task McpReadiness_DoesNotReportPendingAsFailureOrFailedAsConnected()
	{
		var calls = 0;
		using var http = new HttpClient(new Handler(_ => Task.FromResult(Json(++calls == 1
			? """{"data":[{"name":"required","status":{"status":"pending"}}]}"""
			: """{"data":[{"name":"required","status":{"status":"connected"}},{"name":"broken","status":{"status":"failed"}}]}"""))));
		await using var client = new OpenCodeHttpClient("http://localhost", null, null, http);
		var connected = await client.WaitForMcpNamesAsync(["required"], TimeSpan.FromSeconds(2), default);
		connected.Should().Equal("required");
		calls.Should().Be(2);
	}

	[Fact]
	public void Capabilities_DescribeNativeDifferencesWithoutDroppingControls()
	{
		var capabilities = new OpenCodeAgentBuilder(NullLoggerFactory.Instance).GetCapabilities();
		capabilities.StructuredOutput.Should().BeTrue();
		capabilities.NativeStructuredOutput.Should().BeFalse();
		capabilities.FindUnsupported(new AgentBuildConfig { Model = "model", EnableConfigDiscovery = true, EnableOnDemandInstructionDiscovery = true }).Should().BeEmpty();
		capabilities.FindUnsupported(new AgentBuildConfig { Model = "model", EnableConfigDiscovery = false }).Should().Contain(nameof(AgentBuildConfig.EnableConfigDiscovery));
		capabilities.FindUnsupported(new AgentBuildConfig { Model = "model", InfiniteSessionConfig = new() { BackgroundCompactionThreshold = .8 } }).Should().NotBeEmpty();
	}

	[Fact]
	public async Task CreatePromptWait_UsesV2ContractsAndReadsEveryPage()
	{
		var requests = new List<(string Method, string Path, string Body)>();
		var page = 0;
		using var http = new HttpClient(new Handler(async r =>
		{
			var path = r.RequestUri!.PathAndQuery;
			requests.Add((r.Method.Method, path, r.Content is null ? "" : await r.Content.ReadAsStringAsync()));
			return path switch
			{
				"/api/session" => Json("""{"data":{"id":"ses-test"}}"""),
				"/api/session/ses-test/prompt" => Json("""{"data":{"id":"msg-input"}}"""),
				"/api/experimental/session/ses-test/wait" => new HttpResponseMessage(HttpStatusCode.NoContent),
				_ when path.StartsWith("/api/session/ses-test/message?") => Json(++page == 1
					? """{"data":[{"type":"assistant","id":"msg-answer"}],"cursor":{"next":"cursor+next"}}"""
					: """{"data":[{"type":"idle","outcome":"succeeded"}],"cursor":{}}"""),
				_ => throw new InvalidOperationException(path),
			};
		}));
		await using var client = new OpenCodeHttpClient("http://localhost", "opencode", "test", http);
		var model = new OpenCodeModelDto { ProviderId = "github-copilot", ModelId = "model", Variant = "high" };
		var id = await client.CreateSessionAsync(new OpenCodeCreateSessionRequest { Model = model, Agent = "orchestra-primary", Location = new { directory = "C:/workflow" } }, default);
		await client.PromptAsync(id, new OpenCodePromptRequest { Model = model, Parts = [OpenCodePartDto.TextPart("do work"), new() { Type = "file", Url = "file:///C:/image.png", Filename = "image.png" }] }, default);
		var messages = await client.WaitForMessagesAsync(id, default);
		messages.Should().HaveCount(2);
		using var create = JsonDocument.Parse(requests[0].Body);
		create.RootElement.GetProperty("model").GetProperty("id").GetString().Should().Be("model");
		create.RootElement.GetProperty("location").GetProperty("directory").GetString().Should().Be("C:/workflow");
		using var prompt = JsonDocument.Parse(requests[1].Body);
		prompt.RootElement.GetProperty("text").GetString().Should().Be("do work");
		prompt.RootElement.GetProperty("files")[0].GetProperty("uri").GetString().Should().Be("file:///C:/image.png");
		prompt.RootElement.TryGetProperty("model", out _).Should().BeFalse("V2 selects model on the session, not on a prompt");
		requests[2].Method.Should().Be("POST");
		requests.Last().Path.Should().Contain("cursor=cursor%2Bnext");
	}

	[Fact]
	public async Task V2Sse_DecodesDataEnvelopeAndSignalsReadiness()
	{
		const string stream = "data: {\"type\":\"server.connected\",\"data\":{}}\n\ndata: {\"type\":\"session.text.delta\",\"data\":{\"sessionID\":\"s\",\"delta\":\"hello\"}}\n\n";
		using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(stream, Encoding.UTF8, "text/event-stream") })));
		await using var client = new OpenCodeHttpClient("http://localhost", null, null, http);
		var events = new List<OpenCodeServerEvent>();
		await foreach (var e in client.SubscribeAsync(default))
			events.Add(e);
		events[0].Type.Should().Be("server.connected");
		events[1].Properties.GetProperty("delta").GetString().Should().Be("hello");
	}

	[Fact]
	public void DurableFinalResponse_ExcludesCommentaryAndSumsUsage()
	{
		var done = new TaskCompletionSource();
		var handler = NewHandler(done);
		handler.ApplyMessages([
			Element("""{"type":"user","id":"request"}"""),
			Element("""{"type":"assistant","content":[{"type":"text","text":"I will inspect the file"}],"tokens":{"input":10,"output":2,"reasoning":0,"cache":{"read":0,"write":0}}} """),
			Element("""{"type":"assistant","model":{"id":"model","providerID":"provider"},"content":[{"type":"text","text":"{\"count\":42}"}],"tokens":{"input":15,"output":5,"reasoning":1,"cache":{"read":3,"write":0}}} """),
			Element("""{"type":"idle","outcome":"succeeded"}""")], "request");
		handler.FinalContent.Should().Be("{\"count\":42}");
		handler.Usage!.InputTokens.Should().Be(25);
		handler.ActualModel.Should().Be("provider/model");
		done.Task.IsCompleted.Should().BeFalse("the same session may continue; durable wait, not the event pump, owns successful completion");
	}

	[Theory]
	[InlineData("failed")]
	[InlineData("interrupted")]
	public void DurableFailure_CannotBecomeSuccessAfterIdleEvent(string outcome)
	{
		var handler = NewHandler(new TaskCompletionSource());
		handler.Handle(new() { Type = "session.idle", Properties = Element("""{"sessionID":"root"}""") });
		Action apply = () => handler.ApplyMessages([Element("""{"type":"user","id":"request"}"""), Element($$"""{"type":"idle","outcome":"{{outcome}}"}""")], "request");
		apply.Should().Throw<OpenCodeSessionFailedException>().WithMessage($"*{outcome}*");
	}

	[Fact]
	public void V2ChildAndUnscopedEvents_CannotCompleteOrPolluteRoot()
	{
		var channel = Channel.CreateUnbounded<AgentEvent>();
		var done = new TaskCompletionSource();
		var handler = new OpenCodeSessionHandler("root", channel.Writer, NullOrchestrationReporter.Instance, "model", done, NullLogger.Instance);
		handler.Handle(new() { Type = "session.execution.failed", Properties = Element("""{"sessionID":"child","error":{"message":"child failure"}}""") });
		handler.Handle(new() { Type = "session.text.delta", Properties = Element("""{"sessionID":"child","delta":"wrong"}""") });
		handler.Handle(new() { Type = "session.idle", Properties = Element("{}") });
		done.Task.IsCompleted.Should().BeFalse();
		channel.Reader.TryRead(out _).Should().BeFalse();
	}

	private static OpenCodeSessionHandler NewHandler(TaskCompletionSource done) => new("root", Channel.CreateUnbounded<AgentEvent>().Writer, NullOrchestrationReporter.Instance, "model", done, NullLogger.Instance);
	private static JsonElement Element(string json) => JsonSerializer.Deserialize<JsonElement>(json);
	private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
	private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
	}
}
