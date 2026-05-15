using Microsoft.AspNetCore.Http;

namespace Orchestra.Host.Api;

/// <summary>
/// Writes an <see cref="SseEvent"/> to an HTTP response in SSE wire format:
/// <code>
/// id: 42
/// event: step-completed
/// data: {"stepName":"foo", ...}
///
/// </code>
/// The <c>id:</c> line is omitted when the event's <see cref="SseEvent.Sequence"/> is 0
/// (heartbeats and other ephemeral frames that cannot be resumed from). Clients that
/// support reconnection automatically include the most recently seen <c>id:</c> as the
/// <c>Last-Event-Id</c> request header on reconnect; the server uses it as a cursor in
/// <see cref="SseReporter.SubscribeWithSnapshot(long?)"/>.
/// </summary>
internal static class SseEventWriter
{
	/// <summary>
	/// Writes a single SSE event frame. Caller is responsible for flushing the response
	/// when desired (typically after each event for low-latency streams, or after a
	/// batch for high-throughput initial replay).
	/// </summary>
	public static async Task WriteAsync(HttpResponse response, SseEvent evt, CancellationToken cancellationToken)
	{
		if (evt.Sequence > 0)
		{
			await response.WriteAsync($"id: {evt.Sequence}\n", cancellationToken);
		}
		await response.WriteAsync($"event: {evt.Type}\n", cancellationToken);
		await response.WriteAsync($"data: {evt.Data}\n\n", cancellationToken);
	}

	/// <summary>
	/// Parses the <c>Last-Event-Id</c> request header into a sequence number, or returns
	/// null when the header is missing or unparseable. Per the SSE spec, the browser
	/// resends the header value verbatim from the most recently seen <c>id:</c> line.
	/// </summary>
	public static long? ParseLastEventId(HttpRequest request)
	{
		if (!request.Headers.TryGetValue("Last-Event-Id", out var values))
			return null;
		var raw = values.ToString();
		return long.TryParse(raw, System.Globalization.NumberStyles.Integer,
			System.Globalization.CultureInfo.InvariantCulture, out var seq)
			? seq
			: null;
	}
}
