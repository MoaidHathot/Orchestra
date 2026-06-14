namespace Orchestra.Client.Sse;

/// <summary>
/// A single Server-Sent Events frame: an event type plus its data payload.
/// </summary>
/// <param name="Event">The SSE <c>event:</c> field. Empty when the frame had no event type.</param>
/// <param name="Data">The concatenated <c>data:</c> field(s). Multi-line data fields are joined with <c>\n</c>.</param>
public readonly record struct SseFrame(string Event, string Data);
