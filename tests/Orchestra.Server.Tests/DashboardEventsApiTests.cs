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

		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

		// Deactivate every currently-active profile so our test activation produces a real diff.
		// On CI a Default wildcard profile is auto-created/activated at startup — if we leave it
		// active and then activate another wildcard profile, RecomputeEffectiveActiveSet returns
		// early (no diff) and no event is fired.
		var listResp = await _client.GetAsync("/api/profiles", cts.Token);
		listResp.IsSuccessStatusCode.Should().BeTrue();
		var list = await listResp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cts.Token);
		foreach (var p in list.GetProperty("profiles").EnumerateArray())
		{
			if (p.GetProperty("isActive").GetBoolean())
			{
				var existingId = p.GetProperty("id").GetString()!;
				var deactivateResp = await _client.PostAsync($"/api/profiles/{existingId}/deactivate", content: null, cts.Token);
				deactivateResp.IsSuccessStatusCode.Should().BeTrue();
			}
		}

		// Create a profile with a wildcard filter so activation actually changes the
		// effective set (from "nothing active" to "everything active").
		var createResp = await _client.PostAsJsonAsync("/api/profiles", new
		{
			name = $"Sse Test Profile {Guid.NewGuid():N}",
			description = "test",
			filter = new { tags = new[] { "*" } }
		}, cts.Token);
		createResp.IsSuccessStatusCode.Should().BeTrue();
		var created = await createResp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cts.Token);
		var profileId = created.GetProperty("id").GetString()!;

		// Open the SSE stream AFTER setup so we don't have to drain pre-test frames from deactivations.
		using var request = new HttpRequestMessage(HttpMethod.Get, "/api/events");
		using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
		using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
		using var reader = new StreamReader(stream);

		var connected = await ReadSseFrameAsync(reader, cts.Token);
		connected.Type.Should().Be("connected");

		// Wait for subscription to register before broadcasting
		var subscriberDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
		while (broadcaster.SubscriberCount == 0 && DateTime.UtcNow < subscriberDeadline)
		{
			await Task.Delay(25, cts.Token);
		}
		broadcaster.SubscriberCount.Should().BeGreaterThan(0);

		// Trigger a real profile activation through the ProfileManager — this should fire
		// OnEffectiveActiveSetChanged, which our broadcaster is subscribed to.
		profileManager.ActivateProfile(profileId, trigger: "test");

		// Read frames until we find the profile-active-set-changed one (skipping heartbeats
		// and any other event types).
		SseFrame? match = null;
		var frameDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
		while (DateTime.UtcNow < frameDeadline)
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
