using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Orchestra.Engine;
using Orchestra.Host.Api;
using Xunit;

namespace Orchestra.Server.Tests;

/// <summary>
/// Integration tests for the HITL fan-out hook on <see cref="DashboardEventBroadcaster"/>.
///
/// Verifies that:
///   1. Calling <see cref="DashboardEventBroadcaster.BroadcastAwaitingInput"/> directly emits
///      an <c>awaiting-input</c> SSE frame on <c>/api/events</c>.
///   2. <c>BroadcastInputReceived</c> emits an <c>input-received</c> SSE frame.
///   3. <c>BroadcastInputTimeout</c> emits an <c>input-timeout</c> SSE frame.
///   4. End-to-end: an <see cref="SseReporter"/> created by the DI <see cref="SseReporterFactory"/>
///      fans HITL events to the dashboard stream so the Portal can update its waiting-list
///      without subscribing to every per-execution stream.
/// </summary>
public class DashboardEventsHitlTests : IClassFixture<ServerWebApplicationFactory>, IDisposable
{
	private readonly ServerWebApplicationFactory _factory;
	private readonly HttpClient _client;

	public DashboardEventsHitlTests(ServerWebApplicationFactory factory)
	{
		_factory = factory;
		_client = factory.CreateClient();
	}

	public void Dispose() => _client.Dispose();

	[Fact]
	public async Task BroadcastAwaitingInput_Emits_AwaitingInput_Frame()
	{
		var broadcaster = _factory.Services.GetRequiredService<DashboardEventBroadcaster>();

		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		using var reader = await OpenEventsStreamAsync(cts.Token);

		await WaitForSubscriberAsync(broadcaster, cts.Token);

		var createdAt = new DateTimeOffset(2025, 5, 9, 12, 34, 56, TimeSpan.Zero);
		var expiresAt = createdAt.AddMinutes(10);
		broadcaster.BroadcastAwaitingInput(
			orchestrationName: "approval-deploy",
			runId: "run-abc123",
			stepName: "review-deploy",
			kind: "Approval",
			prompt: "Approve deploy?",
			choices: new[] { "approve", "reject" },
			createdAt: createdAt,
			expiresAt: expiresAt);

		var frame = await WaitForFrameAsync(reader, "awaiting-input", cts.Token);
		using var doc = JsonDocument.Parse(frame.Data);
		doc.RootElement.GetProperty("orchestrationName").GetString().Should().Be("approval-deploy");
		doc.RootElement.GetProperty("runId").GetString().Should().Be("run-abc123");
		doc.RootElement.GetProperty("stepName").GetString().Should().Be("review-deploy");
		doc.RootElement.GetProperty("kind").GetString().Should().Be("Approval");
		doc.RootElement.GetProperty("prompt").GetString().Should().Be("Approve deploy?");
		var choices = doc.RootElement.GetProperty("choices").EnumerateArray().Select(e => e.GetString()).ToArray();
		choices.Should().BeEquivalentTo(new[] { "approve", "reject" });
		doc.RootElement.GetProperty("createdAt").GetString().Should().Be(createdAt.ToString("o"));
		doc.RootElement.GetProperty("expiresAt").GetString().Should().Be(expiresAt.ToString("o"));
	}

	[Fact]
	public async Task BroadcastAwaitingInput_OmitsChoices_WhenNullOrEmpty()
	{
		var broadcaster = _factory.Services.GetRequiredService<DashboardEventBroadcaster>();

		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		using var reader = await OpenEventsStreamAsync(cts.Token);

		await WaitForSubscriberAsync(broadcaster, cts.Token);

		broadcaster.BroadcastAwaitingInput(
			orchestrationName: "clarify",
			runId: "run-no-choices",
			stepName: "draft",
			kind: "EngineTool",
			prompt: "What angle?",
			choices: null,
			createdAt: DateTimeOffset.UtcNow,
			expiresAt: null);

		var frame = await WaitForFrameAsync(reader, "awaiting-input", cts.Token);
		using var doc = JsonDocument.Parse(frame.Data);
		doc.RootElement.TryGetProperty("choices", out _).Should().BeFalse(
			"DefaultIgnoreCondition.WhenWritingNull should drop the property when no choices");
		doc.RootElement.TryGetProperty("expiresAt", out _).Should().BeFalse();
	}

	[Fact]
	public async Task BroadcastInputReceived_Emits_InputReceived_Frame()
	{
		var broadcaster = _factory.Services.GetRequiredService<DashboardEventBroadcaster>();

		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		using var reader = await OpenEventsStreamAsync(cts.Token);

		await WaitForSubscriberAsync(broadcaster, cts.Token);

		var respondedAt = new DateTimeOffset(2025, 5, 9, 12, 35, 0, TimeSpan.Zero);
		broadcaster.BroadcastInputReceived(
			orchestrationName: "approval-deploy",
			runId: "run-abc123",
			stepName: "review-deploy",
			choice: "approve",
			reply: null,
			respondedBy: "alice",
			respondedAt: respondedAt);

		var frame = await WaitForFrameAsync(reader, "input-received", cts.Token);
		using var doc = JsonDocument.Parse(frame.Data);
		doc.RootElement.GetProperty("orchestrationName").GetString().Should().Be("approval-deploy");
		doc.RootElement.GetProperty("runId").GetString().Should().Be("run-abc123");
		doc.RootElement.GetProperty("stepName").GetString().Should().Be("review-deploy");
		doc.RootElement.GetProperty("choice").GetString().Should().Be("approve");
		doc.RootElement.GetProperty("respondedBy").GetString().Should().Be("alice");
		doc.RootElement.GetProperty("respondedAt").GetString().Should().Be(respondedAt.ToString("o"));
		doc.RootElement.TryGetProperty("reply", out _).Should().BeFalse(
			"null reply should be omitted via DefaultIgnoreCondition.WhenWritingNull");
	}

	[Fact]
	public async Task BroadcastInputTimeout_Emits_InputTimeout_Frame()
	{
		var broadcaster = _factory.Services.GetRequiredService<DashboardEventBroadcaster>();

		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		using var reader = await OpenEventsStreamAsync(cts.Token);

		await WaitForSubscriberAsync(broadcaster, cts.Token);

		broadcaster.BroadcastInputTimeout(
			orchestrationName: "approval-deploy",
			runId: "run-abc123",
			stepName: "review-deploy",
			onTimeout: "Reject");

		var frame = await WaitForFrameAsync(reader, "input-timeout", cts.Token);
		using var doc = JsonDocument.Parse(frame.Data);
		doc.RootElement.GetProperty("orchestrationName").GetString().Should().Be("approval-deploy");
		doc.RootElement.GetProperty("runId").GetString().Should().Be("run-abc123");
		doc.RootElement.GetProperty("stepName").GetString().Should().Be("review-deploy");
		doc.RootElement.GetProperty("onTimeout").GetString().Should().Be("Reject");
	}

	[Fact]
	public async Task SseReporter_From_DI_Factory_Fans_AwaitingInput_To_Dashboard()
	{
		// End-to-end: resolve the DI-registered factory, create a reporter, and confirm
		// that ReportAwaitingInput on that reporter both lands on its own subscriber AND
		// gets fanned out to the global /api/events dashboard stream.
		var factoryService = _factory.Services.GetRequiredService<IOrchestrationReporterFactory>();
		var broadcaster = _factory.Services.GetRequiredService<DashboardEventBroadcaster>();

		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		using var reader = await OpenEventsStreamAsync(cts.Token);

		await WaitForSubscriberAsync(broadcaster, cts.Token);

		using var reporter = (SseReporter)factoryService.Create();
		var record = new PendingInputRecord
		{
			RunId = "run-fan-1",
			OrchestrationName = "fan-orch",
			StepName = "approval",
			Kind = PendingInputKind.Approval,
			Prompt = "Proceed?",
			Choices = new[] { "yes", "no" },
			CreatedAt = DateTimeOffset.UtcNow,
		};

		reporter.ReportAwaitingInput(record);

		var frame = await WaitForFrameAsync(reader, "awaiting-input", cts.Token);
		using var doc = JsonDocument.Parse(frame.Data);
		doc.RootElement.GetProperty("orchestrationName").GetString().Should().Be("fan-orch");
		doc.RootElement.GetProperty("runId").GetString().Should().Be("run-fan-1");
		doc.RootElement.GetProperty("stepName").GetString().Should().Be("approval");
	}

	[Fact]
	public async Task SseReporter_From_DI_Factory_Fans_InputReceived_To_Dashboard()
	{
		var factoryService = _factory.Services.GetRequiredService<IOrchestrationReporterFactory>();
		var broadcaster = _factory.Services.GetRequiredService<DashboardEventBroadcaster>();

		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		using var reader = await OpenEventsStreamAsync(cts.Token);

		await WaitForSubscriberAsync(broadcaster, cts.Token);

		using var reporter = (SseReporter)factoryService.Create();
		var response = new UserInputResponse
		{
			Choice = "yes",
			Reply = null,
			RespondedBy = "bob",
			RespondedAt = DateTimeOffset.UtcNow,
		};

		reporter.ReportInputReceived("fan-orch", "run-fan-2", "approval", response);

		var frame = await WaitForFrameAsync(reader, "input-received", cts.Token);
		using var doc = JsonDocument.Parse(frame.Data);
		doc.RootElement.GetProperty("choice").GetString().Should().Be("yes");
		doc.RootElement.GetProperty("respondedBy").GetString().Should().Be("bob");
	}

	[Fact]
	public void SseReporter_Without_Broadcaster_Skips_FanOut_Silently()
	{
		// Manual construction (e.g. in unit tests) keeps the broadcaster null and the
		// HITL methods must not throw.
		using var reporter = new SseReporter(dashboardBroadcaster: null);
		var record = new PendingInputRecord
		{
			RunId = "r",
			OrchestrationName = "o",
			StepName = "s",
			Kind = PendingInputKind.Approval,
			Prompt = "?",
			CreatedAt = DateTimeOffset.UtcNow,
		};

		Action act = () => reporter.ReportAwaitingInput(record);
		act.Should().NotThrow();
	}

	// ── helpers ─────────────────────────────────────────────────────────

	private async Task<StreamReader> OpenEventsStreamAsync(CancellationToken token)
	{
		var request = new HttpRequestMessage(HttpMethod.Get, "/api/events");
		var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var stream = await response.Content.ReadAsStreamAsync(token);
		var reader = new StreamReader(stream);

		// Drain the initial "connected" frame so callers can broadcast next.
		var connected = await ReadSseFrameAsync(reader, token);
		connected.Type.Should().Be("connected");
		return reader;
	}

	private static async Task WaitForSubscriberAsync(DashboardEventBroadcaster broadcaster, CancellationToken token)
	{
		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
		while (broadcaster.SubscriberCount == 0 && DateTime.UtcNow < deadline)
		{
			await Task.Delay(25, token);
		}
		broadcaster.SubscriberCount.Should().BeGreaterThan(0);
	}

	private static async Task<SseFrame> WaitForFrameAsync(StreamReader reader, string expectedType, CancellationToken token)
	{
		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
		while (DateTime.UtcNow < deadline)
		{
			var frame = await ReadSseFrameAsync(reader, token);
			if (frame.Type == expectedType) return frame;
		}
		throw new TimeoutException($"Did not see SSE event '{expectedType}' within deadline");
	}

	private readonly record struct SseFrame(string Type, string Data);

	private static async Task<SseFrame> ReadSseFrameAsync(StreamReader reader, CancellationToken token)
	{
		string? eventType = null;
		var dataBuilder = new System.Text.StringBuilder();
		while (true)
		{
			token.ThrowIfCancellationRequested();
			var line = await reader.ReadLineAsync(token);
			if (line is null)
				throw new IOException("SSE stream ended unexpectedly");
			if (line.Length == 0)
			{
				if (eventType is not null)
					return new SseFrame(eventType, dataBuilder.ToString());
				continue;
			}
			if (line.StartsWith("event: ", StringComparison.Ordinal))
			{
				eventType = line["event: ".Length..];
			}
			else if (line.StartsWith("data: ", StringComparison.Ordinal))
			{
				if (dataBuilder.Length > 0) dataBuilder.Append('\n');
				dataBuilder.Append(line["data: ".Length..]);
			}
		}
	}
}
