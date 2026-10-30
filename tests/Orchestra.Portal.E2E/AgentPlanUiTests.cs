using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using Orchestra.Engine;
using Orchestra.Host.Api;
using Orchestra.Host.Hosting;
using Orchestra.Host.Persistence;
using Xunit;

namespace Orchestra.Portal.E2E;

[Trait("Category", "E2E")]
public class AgentPlanUiTests
{
	[Fact]
	public async Task LivePlans_ReconnectFromSnapshot_AndReadDurableRevisionHistory()
	{
		var root = new DirectoryInfo(AppContext.BaseDirectory);
		while (root is not null && !File.Exists(Path.Combine(root.FullName, "OrchestrationEngine.slnx")))
			root = root.Parent;
		Assert.NotNull(root);
		using var data = new TestDirectory();
		var json = new JsonSerializerOptions(Orchestra.Host.Extensions.EndpointRouteBuilderExtensions.DefaultJsonOptions);
		using var store = new FileSystemRunStore(data.Path);
		using var reporter = new SseReporter(null, new SseOptions { MaxAccumulatedEvents = 5 }, Microsoft.Extensions.Logging.Abstractions.NullLogger<SseReporter>.Instance);
		var run = new PlanRunIdentity("browser-run", "plan-browser", "1", "manual", DateTimeOffset.UtcNow);
		reporter.SetExecutionContext(run.RunId, "plan-browser", "plan-browser", run.StartedAt, "manual", []);
		reporter.ReportStepStarted("work");
		AgentPlanRevision Revision(long number, string status) => new()
		{
			Run = run,
			StepName = "work",
			StepExecutionId = "attempt",
			StreamId = "tasks",
			Revision = number,
			CapturedAt = DateTimeOffset.UtcNow,
			Plan = new()
			{
				Provider = "test",
				SessionId = "session",
				Source = "tasks",
				Title = "Verification plan",
				Tasks = [new() { Id = "verify", Title = "Verify behavior", Status = status }],
				Markdown = "## Validation\nRun the checks."
			},
		};
		var first = Revision(1, "in_progress");
		await store.AppendPlanAsync(first);
		reporter.ReportPlanUpdated(first);
		var builder = WebApplication.CreateBuilder(new WebApplicationOptions
		{
			ContentRootPath = root!.FullName,
			WebRootPath = Path.Combine(root.FullName, "src", "Orchestra.Portal", "wwwroot"),
			EnvironmentName = "Testing",
		});
		builder.Logging.ClearProviders();
		builder.Services.AddSingleton<IRunStore>(store);
		await using var app = builder.Build();
		app.Urls.Add("http://127.0.0.1:0");
		app.UseDefaultFiles();
		app.UseStaticFiles();
		var orchestration = new { id = "plan-browser", name = "plan-browser", description = "Plan browser integration", version = "1", steps = new[] { new { name = "work", type = "Prompt", model = "test" } } };
		app.MapGet("/api/orchestrations", () => Results.Json(new { orchestrations = new[] { orchestration } }));
		app.MapGet("/api/orchestrations/plan-browser", () => Results.Json(orchestration));
		app.MapGet("/api/active", () => Results.Json(new { running = new[] { new { executionId = run.RunId, orchestrationId = "plan-browser", orchestrationName = "plan-browser", status = "Running", startedAt = run.StartedAt, currentStep = "work", completedSteps = 0, totalSteps = 1 } }, pending = Array.Empty<object>() }));
		app.MapGet("/api/history", () => Results.Json(new { runs = Array.Empty<object>(), totalCount = 0 }));
		app.MapGet("/api/runs/pending", () => Results.Json(Array.Empty<object>()));
		app.MapGet("/api/execution/{id}/attach", async (HttpContext context) =>
		{
			context.Response.ContentType = "text/event-stream";
			var subscription = reporter.SubscribeWithSnapshot();
			async Task Send(string type, string payload, long id)
			{
				await context.Response.WriteAsync($"id: {id}\nevent: {type}\ndata: {payload}\n\n", context.RequestAborted);
				await context.Response.Body.FlushAsync(context.RequestAborted);
			}
			try
			{
				await Send("execution-snapshot", JsonSerializer.Serialize(subscription.Snapshot, json), subscription.Snapshot.LastEventSequence);
				foreach (var evt in subscription.Replay)
					await Send(evt.Type, evt.Data, evt.Sequence);
				if (subscription.Future is not null)
				{
					await foreach (var evt in subscription.Future.ReadAllAsync(context.RequestAborted))
						await Send(evt.Type, evt.Data, evt.Sequence);
				}
			}
			catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
			finally { reporter.Unsubscribe(subscription.Future); }
		});
		app.MapPlanHistoryEndpoints(json);
		app.MapGet("/api/{**path}", () => Results.Json(new { }));
		await app.StartAsync();
		try
		{
			using var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
			await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true, Channel = Environment.GetEnvironmentVariable("ORCHESTRA_PLAYWRIGHT_CHANNEL") });
			var page = await browser.NewPageAsync();
			var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
			page.PageError += (_, error) => errors.Enqueue(error);
			try
			{
				await page.GotoAsync(app.Urls.Single());
				await page.Locator(".orch-card").Filter(new() { HasText = run.RunId }).ClickAsync();
				await page.Locator(".node").Filter(new() { HasText = "work" }).First.ClickAsync();
				await Assertions.Expect(page.GetByText("0 of 1 tasks completed")).ToBeVisibleAsync();
				await Assertions.Expect(page.GetByRole(AriaRole.Checkbox, new() { Name = "Verify behavior" })).ToBeDisabledAsync();
				var final = Revision(2, "completed");
				await store.AppendPlanAsync(final);
				reporter.ReportPlanUpdated(final);
				await Assertions.Expect(page.GetByText("1 of 1 tasks completed")).ToBeVisibleAsync();
				// Evict plan events and reconnect through the browser; state must come from snapshot.
				for (var i = 0; i < 15; i++)
					reporter.ReportContentDelta("work", "progress ");
				await page.ReloadAsync();
				await page.Locator(".orch-card").Filter(new() { HasText = run.RunId }).ClickAsync();
				await page.Locator(".node").Filter(new() { HasText = "work" }).First.ClickAsync();
				await Assertions.Expect(page.GetByText("1 of 1 tasks completed")).ToBeVisibleAsync();
				await page.GetByRole(AriaRole.Button, new() { Name = "Revision history", Exact = true }).ClickAsync();
				await page.GetByLabel("Plan revision").SelectOptionAsync("attempt:tasks:1");
				await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Load more revisions" })).ToHaveCountAsync(0);
				await Assertions.Expect(page.GetByText("0 of 1 tasks completed")).ToBeVisibleAsync();
				if (Environment.GetEnvironmentVariable("ORCHESTRA_PLAN_BROWSER_EVIDENCE") is { Length: > 0 } evidence)
				{
					Directory.CreateDirectory(evidence);
					await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "plan-history.png"), FullPage = true });
				}
			}
			catch
			{
				if (Environment.GetEnvironmentVariable("ORCHESTRA_PLAN_BROWSER_EVIDENCE") is { Length: > 0 } evidence)
				{
					Directory.CreateDirectory(evidence);
					await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "failure.png"), FullPage = true });
					await File.WriteAllTextAsync(Path.Combine(evidence, "failure.html"), await page.ContentAsync());
					await File.WriteAllTextAsync(Path.Combine(evidence, "failure-state.json"), JsonSerializer.Serialize(new { errors, snapshot = reporter.GetCurrentSnapshot() }, json));
				}
				throw;
			}
		}
		finally { await app.StopAsync(); }
	}

	private sealed class TestDirectory : IDisposable
	{
		public string Path { get; } = Directory.CreateTempSubdirectory("orchestra-plan-browser-").FullName;
		public void Dispose() => Directory.Delete(Path, true);
	}
}
