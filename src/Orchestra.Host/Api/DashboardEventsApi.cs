using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Orchestra.Host.Api;

/// <summary>
/// SSE endpoint that streams dashboard-wide events (profile activation, execution lifecycle) to
/// connected Portal clients. This lets the UI refresh in real time instead of polling.
///
/// Backed by <see cref="DashboardEventBroadcaster"/>.
/// </summary>
public static class DashboardEventsApi
{
	/// <summary>Heartbeat interval — keeps SSE connections alive through proxies.</summary>
	private static readonly TimeSpan s_heartbeatInterval = TimeSpan.FromSeconds(20);

	/// <summary>
	/// Maps the GET /api/events dashboard SSE endpoint.
	/// </summary>
	public static IEndpointRouteBuilder MapDashboardEventsApi(this IEndpointRouteBuilder endpoints, JsonSerializerOptions jsonOptions)
	{
		endpoints.MapGet("/api/events", async (
			HttpContext httpContext,
			DashboardEventBroadcaster broadcaster) =>
		{
			httpContext.Response.ContentType = "text/event-stream";
			httpContext.Response.Headers.CacheControl = "no-cache";
			httpContext.Response.Headers.Connection = "keep-alive";

			// Advise the client to retry in 3s if the connection drops
			await httpContext.Response.WriteAsync("retry: 3000\n\n");
			await httpContext.Response.Body.FlushAsync();

			var reader = broadcaster.Subscribe();
			if (reader is null)
			{
				// Subscriber cap reached — report and exit
				httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
				await httpContext.Response.WriteAsync("event: rejected\n");
				await httpContext.Response.WriteAsync("data: {\"reason\":\"subscriber-limit-reached\"}\n\n");
				await httpContext.Response.Body.FlushAsync();
				return;
			}

			// Send an initial "connected" event so the client knows the stream is live
			await httpContext.Response.WriteAsync("event: connected\n");
			await httpContext.Response.WriteAsync("data: {}\n\n");
			await httpContext.Response.Body.FlushAsync();

			var sseToken = httpContext.RequestAborted;

			// Background task: periodic heartbeats. Cancelled when the request aborts.
			using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(sseToken);
			var heartbeatTask = Task.Run(async () =>
			{
				try
				{
					while (!heartbeatCts.IsCancellationRequested)
					{
						await Task.Delay(s_heartbeatInterval, heartbeatCts.Token);
						broadcaster.SendHeartbeat();
					}
				}
				catch (OperationCanceledException) { /* expected on shutdown/disconnect */ }
			}, heartbeatCts.Token);

			try
			{
				await foreach (var evt in reader.ReadAllAsync(sseToken))
				{
					await httpContext.Response.WriteAsync($"event: {evt.Type}\n", sseToken);
					await httpContext.Response.WriteAsync($"data: {evt.Data}\n\n", sseToken);
					await httpContext.Response.Body.FlushAsync(sseToken);
				}
			}
			catch (OperationCanceledException)
			{
				// Client disconnected — normal
			}
			finally
			{
				broadcaster.Unsubscribe(reader);
				heartbeatCts.Cancel();
				try { await heartbeatTask; } catch { /* swallow */ }
			}
		});

		return endpoints;
	}
}
