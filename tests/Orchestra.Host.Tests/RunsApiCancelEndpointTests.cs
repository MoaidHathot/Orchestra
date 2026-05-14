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

		// Act
		var response = await client.PostAsync($"/api/active/{execId}/cancel", content: null);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		info.CancellationCauseOverride.Should().NotBeNull();
		info.CancellationCauseOverride!.Kind.Should().Be(CancellationCauseKind.External);
		info.CancellationCauseOverride.Source.Should().Be("caller");
		info.CancellationCauseOverride.Detail.Should().Be("REST /api/active/{id}/cancel");
		info.CancellationCauseOverride.RequestedAt.Should().NotBeNull();
		cts.IsCancellationRequested.Should().BeTrue();
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
