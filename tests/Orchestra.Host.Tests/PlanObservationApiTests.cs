using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Orchestra.Engine;
using Orchestra.Host.Api;
using Orchestra.Host.Hosting;
using Orchestra.Host.Persistence;
using Xunit;

namespace Orchestra.Host.Tests;

public class PlanObservationApiTests
{
	private static AgentPlanRevision Revision(long revision = 1) => new()
	{
		Run = new("run1", "workflow", "1", "manual", DateTimeOffset.UtcNow),
		StepName = "step",
		StepExecutionId = "attempt",
		StreamId = "source",
		Revision = revision,
		CapturedAt = DateTimeOffset.UtcNow,
		Plan = new() { Provider = "copilot", SessionId = "session", Source = "todos", Tasks = [new() { Title = "Test", Status = revision == 1 ? "pending" : "completed" }] },
	};

	[Fact]
	public void Snapshot_RetainsLatestPlanAfterReplayEvictionAndIgnoresOlderRevisions()
	{
		using var reporter = new SseReporter(null, new SseOptions { MaxAccumulatedEvents = 5 }, Microsoft.Extensions.Logging.Abstractions.NullLogger<SseReporter>.Instance);
		reporter.ReportStepStarted("step");
		reporter.ReportPlanUpdated(Revision(2));
		reporter.ReportPlanUpdated(Revision(1));
		for (var i = 0; i < 20; i++)
			reporter.ReportContentDelta("step", "text");
		var snapshot = reporter.SubscribeWithSnapshot().Snapshot;
		snapshot.Steps["step"].Plans.Single().Revision.Should().Be(2);
		snapshot.Steps["step"].Status.Should().Be("running", "plan completion is not DAG step completion");
		reporter.ReportStepRetry("step", 1, 2, "failure", TimeSpan.Zero);
		reporter.GetCurrentSnapshot().Steps["step"].Plans.Should().BeEmpty();
	}

	[Fact]
	public void Snapshot_HydratesRestoredTracePlans_AndDoesNotResurrectRetiredExecutions()
	{
		using var reporter = new SseReporter();
		var restored = Revision();
		reporter.ReportStepTrace("step", new() { Plans = [restored] });
		reporter.GetCurrentSnapshot().Steps["step"].Plans.Should().ContainSingle();
		reporter.ReportStepRetry("step", 1, 2, "failure", TimeSpan.Zero);
		reporter.ReportPlanUpdated(restored with { Revision = 2, CapturedAt = DateTimeOffset.UtcNow });
		reporter.GetCurrentSnapshot().Steps["step"].Plans.Should().BeEmpty();
		var next = restored with { StepExecutionId = "next-attempt", Revision = 1, CapturedAt = DateTimeOffset.UtcNow };
		reporter.ReportPlanUpdated(next);
		reporter.ReportStepTrace("step", new() { Plans = [restored] });
		reporter.GetCurrentSnapshot().Steps["step"].Plans.Single().StepExecutionId.Should().Be("next-attempt");
	}

	[Fact]
	public async Task HistoryApi_ReadsUnfinishedRunsAndRejectsEditingAndInvalidPagination()
	{
		var directory = Directory.CreateTempSubdirectory("orchestra-plan-api-").FullName;
		try
		{
			using var store = new FileSystemRunStore(directory);
			await store.AppendPlanAsync(Revision());
			var builder = WebApplication.CreateBuilder();
			builder.WebHost.UseTestServer();
			builder.Services.AddSingleton<IRunStore>(store);
			await using var app = builder.Build();
			app.MapPlanHistoryEndpoints(Orchestra.Host.Extensions.EndpointRouteBuilderExtensions.DefaultJsonOptions);
			await app.StartAsync();
			using var client = app.GetTestClient();
			using var result = await client.GetAsync("/api/execution/run1/plans?stepName=step");
			result.StatusCode.Should().Be(HttpStatusCode.OK);
			using var json = JsonDocument.Parse(await result.Content.ReadAsStringAsync());
			json.RootElement.GetProperty("items")[0].GetProperty("plan").GetProperty("tasks")[0].GetProperty("title").GetString().Should().Be("Test");
			json.RootElement.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null, "the terminal cursor must survive the host's null-omitting serializer settings");
			(await client.GetAsync("/api/execution/run1/plans?after=-1")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
			(await client.PostAsync("/api/execution/run1/plans", null)).StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
			await app.StopAsync();
		}
		finally { Directory.Delete(directory, true); }
	}
}
