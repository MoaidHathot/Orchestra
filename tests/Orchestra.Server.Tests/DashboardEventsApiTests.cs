using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Orchestra.Host.Api;
using Orchestra.Host.Profiles;
using Xunit;

namespace Orchestra.Server.Tests;

/// <summary>
/// Integration tests for the /api/events dashboard SSE stream.
///
/// Verifies that:
///   1. /api/events returns a text/event-stream response.
///   2. An initial "connected" event is emitted.
///   3. Broadcasting a profile-active-set-change produces a corresponding SSE frame.
///   4. Broadcasting execution-started / execution-completed events are forwarded.
///   5. Subscriber count is tracked on the <see cref="DashboardEventBroadcaster"/> singleton.
/// </summary>
public class DashboardEventsApiTests : IClassFixture<ServerWebApplicationFactory>, IDisposable
{
	private readonly ServerWebApplicationFactory _factory;
	private readonly HttpClient _client;

	public DashboardEventsApiTests(ServerWebApplicationFactory factory)
	{
		_factory = factory;
		_client = factory.CreateClient();
	}

	public void Dispose() => _client.Dispose();

	[Fact]
	public async Task Events_Endpoint_Returns_EventStream_ContentType()
	{
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		using var request = new HttpRequestMessage(HttpMethod.Get, "/api/events");
		using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		response.Content.Headers.ContentType?.MediaType.Should().Be("text/event-stream");
	}

	[Fact]
	public async Task Events_Endpoint_Emits_Connected_Then_ProfileActiveSetChanged()
	{
		var broadcaster = _factory.Services.GetRequiredService<DashboardEventBroadcaster>();

		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		using var request = new HttpRequestMessage(HttpMethod.Get, "/api/events");
		using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

		response.StatusCode.Should().Be(HttpStatusCode.OK);

		using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
		using var reader = new StreamReader(stream);

		// Consume the "connected" frame
		var firstFrame = await ReadSseFrameAsync(reader, cts.Token);
		firstFrame.Type.Should().Be("connected");

		// Give the endpoint a moment to register its subscriber before we broadcast
		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
		while (broadcaster.SubscriberCount == 0 && DateTime.UtcNow < deadline)
		{
			await Task.Delay(25, cts.Token);
		}
		broadcaster.SubscriberCount.Should().BeGreaterThan(0);

		// Broadcast a profile change
		broadcaster.BroadcastProfileActiveSetChanged(
			activatedOrchestrationIds: new[] { "orch-a", "orch-b" },
			deactivatedOrchestrationIds: Array.Empty<string>(),
			trigger: "schedule");

		// Read frames until we see profile-active-set-changed (skipping heartbeats)
		SseFrame frame;
		do
		{
			frame = await ReadSseFrameAsync(reader, cts.Token);
		}
		while (frame.Type == "heartbeat");

		frame.Type.Should().Be("profile-active-set-changed");
		using var doc = JsonDocument.Parse(frame.Data);
		doc.RootElement.GetProperty("trigger").GetString().Should().Be("schedule");
		doc.RootElement.GetProperty("activatedOrchestrationIds").GetArrayLength().Should().Be(2);
	}

	[Fact]
	public async Task Events_Endpoint_Forwards_ExecutionStarted_And_Completed()
	{
		var broadcaster = _factory.Services.GetRequiredService<DashboardEventBroadcaster>();

		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		using var request = new HttpRequestMessage(HttpMethod.Get, "/api/events");
		using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

		using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
		using var reader = new StreamReader(stream);

		var connected = await ReadSseFrameAsync(reader, cts.Token);
		connected.Type.Should().Be("connected");

		// Wait for subscription to land
		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
		while (broadcaster.SubscriberCount == 0 && DateTime.UtcNow < deadline)
		{
			await Task.Delay(25, cts.Token);
		}

		broadcaster.BroadcastExecutionStarted("exec-1", "orch-x", "Orch X", "manual");
		broadcaster.BroadcastExecutionCompleted("exec-1", "orch-x", "Orch X", "Completed");

		// Collect frames until we've seen both (ignore heartbeats)
		var started = false;
		var completed = false;
		while (!(started && completed))
		{
			var frame = await ReadSseFrameAsync(reader, cts.Token);
			if (frame.Type == "execution-started") started = true;
			if (frame.Type == "execution-completed") completed = true;
		}

		started.Should().BeTrue();
		completed.Should().BeTrue();
	}

	[Fact]
	public async Task ProfileManager_Activation_Broadcasts_To_Events_Stream()
	{
		var broadcaster = _factory.Services.GetRequiredService<DashboardEventBroadcaster>();
		var profileManager = _factory.Services.GetRequiredService<ProfileManager>();

		// Create a profile with a wildcard filter so activation actually changes the
		// effective set (otherwise RecomputeEffectiveActiveSet returns early with no event).
		var createResp = await _client.PostAsJsonAsync("/api/profiles", new
		{
			name = $"Sse Test Profile {Guid.NewGuid():N}",
			description = "test",
			filter = new { tags = new[] { "*" } }
		});
		createResp.IsSuccessStatusCode.Should().BeTrue();
		var created = await createResp.Content.ReadFromJsonAsync<JsonElement>();
		var profileId = created.GetProperty("id").GetString()!;

		// Open the SSE stream
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		using var request = new HttpRequestMessage(HttpMethod.Get, "/api/events");
		using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
		using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
		using var reader = new StreamReader(stream);

		var connected = await ReadSseFrameAsync(reader, cts.Token);
		connected.Type.Should().Be("connected");

		// Wait for subscription
		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
		while (broadcaster.SubscriberCount == 0 && DateTime.UtcNow < deadline)
		{
			await Task.Delay(25, cts.Token);
		}

		// Trigger a real profile activation through the ProfileManager — this should fire
		// OnEffectiveActiveSetChanged, which our broadcaster is subscribed to.
		profileManager.ActivateProfile(profileId, trigger: "test");

		// Wait for the matching SSE frame
		SseFrame? match = null;
		while (DateTime.UtcNow < deadline.AddSeconds(8))
		{
			var frame = await ReadSseFrameAsync(reader, cts.Token);
			if (frame.Type == "profile-active-set-changed")
			{
				match = frame;
				break;
			}
		}

		match.Should().NotBeNull("activating a profile should broadcast a profile-active-set-changed SSE event");
		using var doc = JsonDocument.Parse(match!.Value.Data);
		doc.RootElement.GetProperty("trigger").GetString().Should().Be("test");
	}

	// ── SSE parsing helper ────────────────────────────────────────────────

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
				continue; // blank line with no prior event — skip
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
			// ignore other fields (retry:, id:, comments)
		}
	}
}
