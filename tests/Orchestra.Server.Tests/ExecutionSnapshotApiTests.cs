using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Orchestra.Engine;
using Orchestra.Host.Api;
using Orchestra.Host.Triggers;
using Xunit;

namespace Orchestra.Server.Tests;

/// <summary>
/// Integration tests for the SSE <c>execution-snapshot</c> frame and the REST
/// <c>GET /api/execution/{id}/state</c> / friendly-alias <c>GET /api/orchestrations/{name}/runs/{runId}/state</c>
/// endpoints introduced to fix the "DAG nodes not colored when attaching mid-run" bug.
/// </summary>
public class ExecutionSnapshotApiTests : IClassFixture<ServerWebApplicationFactory>, IDisposable
{
	private readonly ServerWebApplicationFactory _factory;
	private readonly HttpClient _client;

	public ExecutionSnapshotApiTests(ServerWebApplicationFactory factory)
	{
		_factory = factory;
		_client = factory.CreateClient();
	}

	public void Dispose() => _client.Dispose();

	[Fact]
	public async Task StateEndpoint_UnknownExecution_Returns404()
	{
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		using var response = await _client.GetAsync("/api/execution/nope/state", cts.Token);

		response.StatusCode.Should().Be(HttpStatusCode.NotFound);
		response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
	}

	[Fact]
	public async Task StateEndpoint_WithSseReporter_ReturnsAuthoritativeSnapshot()
	{
		var executionId = $"snap-test-{Guid.NewGuid():N}";
		var (reporter, info) = RegisterFakeActiveExecution(executionId, "snap-orch", "Snapshot Demo");

		try
		{
			// Simulate some events the engine would have emitted.
			reporter.ReportStepStarted("fetch");
			reporter.ReportStepCompleted("fetch",
				new AgentResult { Content = "hello", ActualModel = "claude-opus-4.6" },
				OrchestrationStepType.Prompt);
			reporter.ReportStepStarted("transform");

			using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
			using var response = await _client.GetAsync($"/api/execution/{executionId}/state", cts.Token);

			response.StatusCode.Should().Be(HttpStatusCode.OK);
			response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

			var json = await response.Content.ReadAsStringAsync(cts.Token);
			using var doc = JsonDocument.Parse(json);
			doc.RootElement.GetProperty("executionId").GetString().Should().Be(executionId);
			doc.RootElement.GetProperty("orchestrationName").GetString().Should().Be("Snapshot Demo");

			var steps = doc.RootElement.GetProperty("steps");
			steps.GetProperty("fetch").GetProperty("status").GetString().Should().Be("completed");
			steps.GetProperty("fetch").GetProperty("contentPreview").GetString().Should().Be("hello");
			steps.GetProperty("transform").GetProperty("status").GetString().Should().Be("running");
		}
		finally
		{
			RemoveActiveExecution(executionId);
			reporter.Dispose();
		}
	}

	[Fact]
	public async Task FriendlyAliasStateEndpoint_VerifiesOrchestrationName()
	{
		var executionId = $"snap-alias-{Guid.NewGuid():N}";
		var (reporter, info) = RegisterFakeActiveExecution(executionId, "my-orch", "My Orchestration");

		try
		{
			using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

			// Correct alias works.
			using var ok = await _client.GetAsync($"/api/orchestrations/My Orchestration/runs/{executionId}/state", cts.Token);
			ok.StatusCode.Should().Be(HttpStatusCode.OK);

			// Wrong orchestration name returns 404 even though the runId exists, so callers
			// don't accidentally attach to the wrong orchestration via a copy-pasted runId.
			using var bad = await _client.GetAsync($"/api/orchestrations/Other/runs/{executionId}/state", cts.Token);
			bad.StatusCode.Should().Be(HttpStatusCode.NotFound);
		}
		finally
		{
			RemoveActiveExecution(executionId);
			reporter.Dispose();
		}
	}

	[Fact]
	public async Task AttachEndpoint_EmitsExecutionSnapshotFrameFirst()
	{
		var executionId = $"attach-snap-{Guid.NewGuid():N}";
		var (reporter, info) = RegisterFakeActiveExecution(executionId, "attach-orch", "Attach Demo");

		try
		{
			// Pre-populate authoritative state for a step the engine has already finished.
			reporter.ReportStepStarted("anchor");
			reporter.ReportStepCompleted("anchor",
				new AgentResult { Content = "anchor-result" },
				OrchestrationStepType.Prompt);

			using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
			using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/execution/{executionId}/attach");
			using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

			response.StatusCode.Should().Be(HttpStatusCode.OK);
			response.Content.Headers.ContentType?.MediaType.Should().Be("text/event-stream");

			using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
			using var reader = new StreamReader(stream);

			// First frame: execution-info (sent by the attach handler).
			var first = await ReadSseFrameAsync(reader, cts.Token);
			first.Type.Should().Be("execution-info");

			// Second frame: execution-snapshot — the authoritative snapshot. This is the
			// critical guarantee: even if events were evicted later, the client still gets
			// the full per-step state upfront.
			var snapshot = await ReadSseFrameAsync(reader, cts.Token);
			snapshot.Type.Should().Be("execution-snapshot");

			using var snapDoc = JsonDocument.Parse(snapshot.Data);
			snapDoc.RootElement.GetProperty("executionId").GetString().Should().Be(executionId);
			snapDoc.RootElement.GetProperty("steps").GetProperty("anchor")
				.GetProperty("status").GetString().Should().Be("completed");
		}
		finally
		{
			RemoveActiveExecution(executionId);
			reporter.Dispose();
		}
	}

	[Fact]
	public async Task AttachEndpoint_WritesSseIdField_ForResume()
	{
		var executionId = $"id-test-{Guid.NewGuid():N}";
		var (reporter, info) = RegisterFakeActiveExecution(executionId, "id-orch", "Id Demo");

		try
		{
			reporter.ReportStepStarted("step-1"); // seq 1
			reporter.ReportStepCompleted("step-1",
				new AgentResult { Content = "ok" },
				OrchestrationStepType.Prompt); // seq 2

			using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
			using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/execution/{executionId}/attach");
			using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

			using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
			using var reader = new StreamReader(stream);

			// Read frames until we find a step-completed frame; assert it has an id: line.
			var foundIdField = false;
			var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
			while (DateTime.UtcNow < deadline && !foundIdField)
			{
				var line = await reader.ReadLineAsync(cts.Token);
				if (line is null) break;
				if (line.StartsWith("id: ", StringComparison.Ordinal))
				{
					foundIdField = true;
				}
			}

			foundIdField.Should().BeTrue("server must write SSE id: <sequence> so clients can resume via Last-Event-Id");
		}
		finally
		{
			RemoveActiveExecution(executionId);
			reporter.Dispose();
		}
	}

	[Fact]
	public async Task AttachEndpoint_WithLastEventIdHeader_ResumesFromCursor()
	{
		var executionId = $"resume-{Guid.NewGuid():N}";
		var (reporter, info) = RegisterFakeActiveExecution(executionId, "resume-orch", "Resume Demo");

		try
		{
			reporter.ReportStepStarted("a"); // seq 1
			reporter.ReportStepStarted("b"); // seq 2
			reporter.ReportStepStarted("c"); // seq 3

			using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
			using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/execution/{executionId}/attach");
			request.Headers.TryAddWithoutValidation("Last-Event-Id", "2");
			using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

			using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
			using var reader = new StreamReader(stream);

			// Discard execution-info + snapshot
			await ReadSseFrameAsync(reader, cts.Token); // execution-info
			await ReadSseFrameAsync(reader, cts.Token); // execution-snapshot

			// The replay should ONLY contain seq > 2 — i.e. just step-started for "c".
			var frame = await ReadSseFrameAsync(reader, cts.Token);
			frame.Type.Should().Be("step-started");
			frame.Data.Should().Contain("\"stepName\":\"c\"",
				"Last-Event-Id of 2 means the client has already seen events for a and b");
		}
		finally
		{
			RemoveActiveExecution(executionId);
			reporter.Dispose();
		}
	}

	[Fact]
	public async Task AttachEndpoint_WithStaleLastEventId_EmitsReplayTruncatedFrame()
	{
		var executionId = $"trunc-{Guid.NewGuid():N}";
		var (reporter, info) = RegisterFakeActiveExecution(executionId, "trunc-orch", "Trunc Demo",
			// Tiny buffer so it's easy to force truncation.
			new SseOptionsOverride { MaxAccumulatedEvents = 32 });

		try
		{
			// Flood the buffer so seq 1..50 are evicted.
			for (var i = 0; i < 100; i++)
			{
				reporter.ReportStepOutput($"step-{i % 3}", $"chunk-{i}");
			}

			using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
			using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/execution/{executionId}/attach");
			request.Headers.TryAddWithoutValidation("Last-Event-Id", "5");
			using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

			using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
			using var reader = new StreamReader(stream);

			await ReadSseFrameAsync(reader, cts.Token); // execution-info
			await ReadSseFrameAsync(reader, cts.Token); // execution-snapshot

			var replayTruncated = await ReadSseFrameAsync(reader, cts.Token);
			replayTruncated.Type.Should().Be("replay-truncated");
			using var doc = JsonDocument.Parse(replayTruncated.Data);
			doc.RootElement.GetProperty("requestedLastEventId").GetInt64().Should().Be(5);
		}
		finally
		{
			RemoveActiveExecution(executionId);
			reporter.Dispose();
		}
	}

	// ── Helpers ──

	private sealed class SseOptionsOverride
	{
		public int? MaxAccumulatedEvents { get; set; }
	}

	/// <summary>
	/// Inserts a synthetic <see cref="ActiveExecutionInfo"/> into the running server's
	/// active executions dictionary with a dedicated <see cref="SseReporter"/> attached.
	/// This lets us drive snapshot/replay/attach behavior end-to-end without spinning
	/// up the engine and a real orchestration.
	/// </summary>
	private (SseReporter reporter, ActiveExecutionInfo info) RegisterFakeActiveExecution(
		string executionId,
		string orchestrationId,
		string orchestrationName,
		SseOptionsOverride? overrides = null)
	{
		var sseOptions = _factory.Services.GetRequiredService<Orchestra.Host.Hosting.SseOptions>();
		// Construct a tailored options instance so we don't mutate the singleton (which
		// other tests share via IClassFixture).
		var effective = new Orchestra.Host.Hosting.SseOptions
		{
			MaxAccumulatedEvents = overrides?.MaxAccumulatedEvents ?? sseOptions.MaxAccumulatedEvents,
			MaxChannelCapacity = sseOptions.MaxChannelCapacity,
			MaxSubscribers = sseOptions.MaxSubscribers,
			HeartbeatInterval = sseOptions.HeartbeatInterval,
		};

		var reporter = new SseReporter(
			dashboardBroadcaster: null,
			options: effective,
			logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<SseReporter>.Instance);

		var cts = new CancellationTokenSource();
		var info = new ActiveExecutionInfo
		{
			ExecutionId = executionId,
			OrchestrationId = orchestrationId,
			OrchestrationName = orchestrationName,
			StartedAt = DateTimeOffset.UtcNow,
			TriggeredBy = "test",
			CancellationTokenSource = cts,
			Reporter = reporter,
		};

		var dict = _factory.Services.GetRequiredService<ConcurrentDictionary<string, ActiveExecutionInfo>>();
		dict[executionId] = info;
		return (reporter, info);
	}

	private void RemoveActiveExecution(string executionId)
	{
		var dict = _factory.Services.GetRequiredService<ConcurrentDictionary<string, ActiveExecutionInfo>>();
		dict.TryRemove(executionId, out _);
	}

	private readonly record struct SseFrame(string Type, string Data);

	private static async Task<SseFrame> ReadSseFrameAsync(StreamReader reader, CancellationToken token)
	{
		string? eventType = null;
		var dataBuilder = new StringBuilder();
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
			// id:, retry:, and comments are ignored.
		}
	}
}
