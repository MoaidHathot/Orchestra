using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;

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
			IHostApplicationLifetime lifetime,
			DashboardEventBroadcaster broadcaster) =>
		{
			// Subscribe BEFORE writing anything: once the first byte of an event-stream body is
			// flushed the status code is committed as 200, so a cap rejection set afterwards
			// never reaches the client — it would see a 200 stream that immediately closed and
			// EventSource would reconnect in a tight loop. Reject up front with a real 503 and
			// a Retry-After so the browser backs off.
			var reader = broadcaster.Subscribe();
			if (reader is null)
			{
				httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
				httpContext.Response.Headers.RetryAfter = "10";
				httpContext.Response.ContentType = "application/problem+json";
				await httpContext.Response.WriteAsync(JsonSerializer.Serialize(new
				{
					type = "https://tools.ietf.org/html/rfc7231#section-6.6.4",
					title = "Dashboard event stream subscriber limit reached",
					status = StatusCodes.Status503ServiceUnavailable,
					detail = $"Too many concurrent /api/events subscribers (limit {DashboardEventBroadcaster.MaxSubscribers}). Close an existing dashboard tab and retry.",
					reason = "subscriber-limit-reached",
				}, jsonOptions));
				return;
			}

			httpContext.Response.ContentType = "text/event-stream";
			httpContext.Response.Headers.CacheControl = "no-cache";
			httpContext.Response.Headers.Connection = "keep-alive";

			try
			{
				// Advise the client to retry in 3s if the connection drops
				await httpContext.Response.WriteAsync("retry: 3000\n\n");
				await httpContext.Response.Body.FlushAsync();

				// Send an initial "connected" event so the client knows the stream is live
				await httpContext.Response.WriteAsync("event: connected\n");
				await httpContext.Response.WriteAsync("data: {}\n\n");
				await httpContext.Response.Body.FlushAsync();
			}
			catch
			{
				// The client went away before the preamble finished; release the slot.
				broadcaster.Unsubscribe(reader);
				throw;
			}

			using var sseCts = CancellationTokenSource.CreateLinkedTokenSource(
				httpContext.RequestAborted,
				lifetime.ApplicationStopping);
			var sseToken = sseCts.Token;

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
					await SseEventWriter.WriteAsync(httpContext.Response, evt, sseToken);
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
