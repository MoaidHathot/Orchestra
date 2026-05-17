using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orchestra.Engine;
using Orchestra.Host.Api;
using Orchestra.Host.Persistence;
using Orchestra.Host.Registry;
using Orchestra.Host.Services;
using Orchestra.Host.Triggers;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// End-to-end tests for the <c>POST /api/active/{executionId}/cancel</c> REST endpoint.
/// Verifies that the endpoint attributes the cancel by populating
/// <see cref="ActiveExecutionInfo.CancellationCauseOverride"/> BEFORE triggering the CTS, so
/// the engine's probe records a precise <see cref="CancellationDetails"/> on the run record
/// (with a non-null <c>Detail</c> string) instead of a generic anonymous "cancelled by caller".
/// </summary>
public sealed class RunsApiCancelEndpointTests : IDisposable
{
	private readonly string _tempDir;
	private readonly FileSystemRunStore _store;

	public RunsApiCancelEndpointTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-cancel-api-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_tempDir);
		_store = new FileSystemRunStore(_tempDir);
	}

	public void Dispose()
	{
		if (Directory.Exists(_tempDir))
		{
			try { Directory.Delete(_tempDir, recursive: true); }
			catch { /* best-effort cleanup */ }
		}
	}

	[Fact]
	public async Task PostCancel_ExistingExecution_SetsExternalCauseOverrideWithRestDetail()
	{
		using var cts = new CancellationTokenSource();
		var execId = "rest-cancel-1";
		var info = new ActiveExecutionInfo
		{
			ExecutionId = execId,
			OrchestrationId = "orch",
			OrchestrationName = "orch",
			StartedAt = DateTimeOffset.UtcNow,
			TriggeredBy = "manual",
			CancellationTokenSource = cts,
			Reporter = NullOrchestrationReporter.Instance,
		};
		var infos = new ConcurrentDictionary<string, ActiveExecutionInfo>();
		infos[execId] = info;

		using var host = await BuildHostAsync(infos);
		using var client = host.GetTestClient();

		// Act — historical "empty body" callers (legacy CLI, curl, manual tests) must keep
		// working without supplying a body. The detail still records the canonical route, but
		// caller-supplied fields are null because none were sent.
		var response = await client.PostAsync($"/api/active/{execId}/cancel", content: null);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		info.CancellationCauseOverride.Should().NotBeNull();
		info.CancellationCauseOverride!.Kind.Should().Be(CancellationCauseKind.External);
		info.CancellationCauseOverride.Source.Should().Be("caller");
		info.CancellationCauseOverride.Detail.Should().Be("REST /api/active/{id}/cancel");
		info.CancellationCauseOverride.RequestedAt.Should().NotBeNull();
		// No body was sent — caller-supplied fields stay null so dashboards can tell
		// "legacy empty-body cancel" apart from a structured one.
		info.CancellationCauseOverride.CallerReason.Should().BeNull();
		info.CancellationCauseOverride.CallerSource.Should().BeNull();
		// HTTP-derived identity is best-effort. The TestServer doesn't set RemoteIpAddress,
		// so we only assert that User-Agent is captured when the test client sends one (it
		// doesn't by default), and that nothing throws.
		cts.IsCancellationRequested.Should().BeTrue();
	}

	[Fact]
	public async Task PostCancel_WithStructuredBody_PersistsCallerReasonAndSource()
	{
		// New behaviour: callers can supply { "reason": "...", "source": "<label>" } so the
		// run record explains "who" cancelled and "why", not just "REST endpoint was hit".
		using var cts = new CancellationTokenSource();
		var execId = "rest-cancel-structured";
		var info = new ActiveExecutionInfo
		{
			ExecutionId = execId,
			OrchestrationId = "orch",
			OrchestrationName = "orch",
			StartedAt = DateTimeOffset.UtcNow,
			TriggeredBy = "manual",
			CancellationTokenSource = cts,
			Reporter = NullOrchestrationReporter.Instance,
		};
		var infos = new ConcurrentDictionary<string, ActiveExecutionInfo>();
		infos[execId] = info;

		using var host = await BuildHostAsync(infos);
		using var client = host.GetTestClient();

		var response = await client.PostAsJsonAsync(
			$"/api/active/{execId}/cancel",
			new { reason = "superseded by a newer scheduled run", source = "portal-ui" });

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		info.CancellationCauseOverride.Should().NotBeNull();
		info.CancellationCauseOverride!.Kind.Should().Be(CancellationCauseKind.External);
		// Source stays "caller" for backwards-compat; CallerSource is the new label.
		info.CancellationCauseOverride.Source.Should().Be("caller");
		info.CancellationCauseOverride.CallerSource.Should().Be("portal-ui");
		info.CancellationCauseOverride.CallerReason.Should().Be("superseded by a newer scheduled run");
		// The Reason getter weaves both into the human-readable summary so it shows up in
		// run.json's finalContent and SSE error messages.
		info.CancellationCauseOverride.Reason.Should().Contain("portal-ui");
		info.CancellationCauseOverride.Reason.Should().Contain("superseded by a newer scheduled run");
		cts.IsCancellationRequested.Should().BeTrue();
	}

	[Fact]
	public async Task PostCancel_WithWhitespaceReason_PersistsAsNullNotEmpty()
	{
		// Whitespace-only fields are noise; normalize them to null so dashboards don't render
		// blank "Reason:" rows.
		using var cts = new CancellationTokenSource();
		var execId = "rest-cancel-whitespace";
		var info = new ActiveExecutionInfo
		{
			ExecutionId = execId,
			OrchestrationId = "orch",
			OrchestrationName = "orch",
			StartedAt = DateTimeOffset.UtcNow,
			TriggeredBy = "manual",
			CancellationTokenSource = cts,
			Reporter = NullOrchestrationReporter.Instance,
		};
		var infos = new ConcurrentDictionary<string, ActiveExecutionInfo>();
		infos[execId] = info;

		using var host = await BuildHostAsync(infos);
		using var client = host.GetTestClient();

		var response = await client.PostAsJsonAsync(
			$"/api/active/{execId}/cancel",
			new { reason = "   ", source = "\t" });

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		info.CancellationCauseOverride!.CallerReason.Should().BeNull();
		info.CancellationCauseOverride.CallerSource.Should().BeNull();
	}

	[Fact]
	public async Task PostCancel_WithMalformedJsonBody_ReturnsBadRequest()
	{
		// Defensive: a malformed body must not silently fall through to "cancel succeeded"
		// because that would hide a client-side bug while still cancelling the run.
		using var cts = new CancellationTokenSource();
		var execId = "rest-cancel-bad-json";
		var info = new ActiveExecutionInfo
		{
			ExecutionId = execId,
			OrchestrationId = "orch",
			OrchestrationName = "orch",
			StartedAt = DateTimeOffset.UtcNow,
			TriggeredBy = "manual",
			CancellationTokenSource = cts,
			Reporter = NullOrchestrationReporter.Instance,
		};
		var infos = new ConcurrentDictionary<string, ActiveExecutionInfo>();
		infos[execId] = info;

		using var host = await BuildHostAsync(infos);
		using var client = host.GetTestClient();

		using var badContent = new StringContent("{not-json", System.Text.Encoding.UTF8, "application/json");
		var response = await client.PostAsync($"/api/active/{execId}/cancel", badContent);

		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
		// The run must NOT have been cancelled — bad input rejects, doesn't half-execute.
		cts.IsCancellationRequested.Should().BeFalse();
		info.CancellationCauseOverride.Should().BeNull();
	}

	[Fact]
	public async Task PostCancel_CapturesUserAgentFromRequestHeader()
	{
		// User-Agent is the only HTTP-attribution field the TestServer reliably carries
		// (RemoteIpAddress is null on TestServer, no auth is wired). Verify it lands on the
		// run record so production cancels by curl/automation are recognisable.
		using var cts = new CancellationTokenSource();
		var execId = "rest-cancel-ua";
		var info = new ActiveExecutionInfo
		{
			ExecutionId = execId,
			OrchestrationId = "orch",
			OrchestrationName = "orch",
			StartedAt = DateTimeOffset.UtcNow,
			TriggeredBy = "manual",
			CancellationTokenSource = cts,
			Reporter = NullOrchestrationReporter.Instance,
		};
		var infos = new ConcurrentDictionary<string, ActiveExecutionInfo>();
		infos[execId] = info;

		using var host = await BuildHostAsync(infos);
		using var client = host.GetTestClient();

		using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/active/{execId}/cancel");
		request.Headers.UserAgent.ParseAdd("orchestra-test-suite/1.0");
		var response = await client.SendAsync(request);

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		info.CancellationCauseOverride!.CallerUserAgent.Should().Be("orchestra-test-suite/1.0");
	}

	[Fact]
	public async Task PostCancel_NotFoundExecution_ReturnsNotFoundAndDoesNotCrash()
	{
		using var host = await BuildHostAsync(new ConcurrentDictionary<string, ActiveExecutionInfo>());
		using var client = host.GetTestClient();

		var response = await client.PostAsync($"/api/active/nonexistent/cancel", content: null);
		response.StatusCode.Should().Be(HttpStatusCode.NotFound);
	}

	[Fact]
	public async Task PostCancel_HostShutdownOverrideAlreadySet_DoesNotClobber()
	{
		// The TriggerManager pre-sets a HostShutdown override before initiating shutdown
		// cancels. The REST cancel must not clobber that — first writer wins.
		using var cts = new CancellationTokenSource();
		var execId = "no-clobber";
		var info = new ActiveExecutionInfo
		{
			ExecutionId = execId,
			OrchestrationId = "orch",
			OrchestrationName = "orch",
			StartedAt = DateTimeOffset.UtcNow,
			TriggeredBy = "manual",
			CancellationTokenSource = cts,
			Reporter = NullOrchestrationReporter.Instance,
			CancellationCauseOverride = CancellationDetails.HostShutdown("process stopping"),
		};
		var infos = new ConcurrentDictionary<string, ActiveExecutionInfo>();
		infos[execId] = info;

		using var host = await BuildHostAsync(infos);
		using var client = host.GetTestClient();

		await client.PostAsync($"/api/active/{execId}/cancel", content: null);

		info.CancellationCauseOverride!.Kind.Should().Be(CancellationCauseKind.HostShutdown,
			"the pre-set HostShutdown override must NOT be overwritten by the REST cancel");
	}

	[Fact]
	public async Task GetActive_NestedExecution_ExposesLineageFields()
	{
		// /api/active is the canonical live-progress endpoint for Portal and external
		// observers. A child run must surface its parent/root/depth so observers can render
		// "running inside chain X" instead of treating each active run as orphaned.
		using var cts = new CancellationTokenSource();
		var execId = "active-child-1";
		var info = new ActiveExecutionInfo
		{
			ExecutionId = execId,
			OrchestrationId = "child-orch",
			OrchestrationName = "child-orch",
			StartedAt = DateTimeOffset.UtcNow,
			TriggeredBy = "orchestration:root-AA",
			CancellationTokenSource = cts,
			Reporter = NullOrchestrationReporter.Instance,
			NestingMetadata = new Orchestra.Host.McpServer.ExecutionMetadata
			{
				ParentExecutionId = "root-AA",
				ParentStepName = "invoke",
				RootExecutionId = "root-AA",
				Depth = 1,
			},
		};
		var infos = new ConcurrentDictionary<string, ActiveExecutionInfo>();
		infos[execId] = info;

		using var host = await BuildHostAsync(infos);
		using var client = host.GetTestClient();

		var response = await client.GetAsync("/api/active");
		response.StatusCode.Should().Be(HttpStatusCode.OK);

		var body = await response.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		var array = doc.RootElement.GetProperty("running");

		var entry = array.EnumerateArray()
			.First(e => e.GetProperty("executionId").GetString() == execId);
		entry.GetProperty("parentExecutionId").GetString().Should().Be("root-AA");
		entry.GetProperty("rootExecutionId").GetString().Should().Be("root-AA");
		entry.GetProperty("nestingDepth").GetInt32().Should().Be(1);
		entry.GetProperty("parentStepName").GetString().Should().Be("invoke");

		cts.Dispose();
	}

	[Fact]
	public async Task GetActive_TopLevelExecution_OmitsLineageFields()
	{
		// Top-level executions have NestingMetadata = null; the lineage fields must be
		// elided so observers see "no parent" rather than literal null entries.
		using var cts = new CancellationTokenSource();
		var execId = "top-level-1";
		var info = new ActiveExecutionInfo
		{
			ExecutionId = execId,
			OrchestrationId = "orch",
			OrchestrationName = "orch",
			StartedAt = DateTimeOffset.UtcNow,
			TriggeredBy = "manual",
			CancellationTokenSource = cts,
			Reporter = NullOrchestrationReporter.Instance,
			// NestingMetadata intentionally null.
		};
		var infos = new ConcurrentDictionary<string, ActiveExecutionInfo>();
		infos[execId] = info;

		using var host = await BuildHostAsync(infos);
		using var client = host.GetTestClient();

		var response = await client.GetAsync("/api/active");
		var body = await response.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		var array = doc.RootElement.GetProperty("running");

		var entry = array.EnumerateArray()
			.First(e => e.GetProperty("executionId").GetString() == execId);
		// JSON serializer default options for this endpoint emit nulls as `null` rather than
		// omitting them, so the assertion accommodates both shapes.
		var hasParent = entry.TryGetProperty("parentExecutionId", out var parentEl)
			&& parentEl.ValueKind != JsonValueKind.Null;
		hasParent.Should().BeFalse("top-level runs must not fabricate a parent id");

		cts.Dispose();
	}

	private async Task<IHost> BuildHostAsync(ConcurrentDictionary<string, ActiveExecutionInfo> activeExecutionInfos)
	{
		var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
		// Build a TriggerManager — needed because MapRunsApi registers other endpoints that
		// reference it; minimal-API endpoint registration analyses ALL handler signatures and
		// fails if any required parameter type isn't resolvable from DI.
		var triggerManager = new TriggerManager(
			new ConcurrentDictionary<string, CancellationTokenSource>(),
			activeExecutionInfos,
			agentBuilder: null!,
			scheduler: new OrchestrationScheduler(),
			loggerFactory: NullLoggerFactory.Instance,
			logger: NullLogger<TriggerManager>.Instance,
			runsDir: _tempDir,
			runStore: _store,
			checkpointStore: Substitute.For<ICheckpointStore>(),
			launcher: Substitute.For<IChildOrchestrationLauncher>());

		var host = new HostBuilder()
			.ConfigureWebHost(webHost =>
			{
				webHost.UseTestServer();
				webHost.ConfigureServices(services =>
				{
					services.AddRouting();
					services.AddLogging();
					services.AddSingleton(activeExecutionInfos);
					services.AddSingleton(_store);
					services.AddSingleton(triggerManager);
					services.AddSingleton(new OrchestrationRegistry());
				});
				webHost.Configure(app =>
				{
					app.UseRouting();
					app.UseEndpoints(endpoints => endpoints.MapRunsApi(jsonOptions));
				});
			})
			.Build();

		await host.StartAsync();
		return host;
	}
}
