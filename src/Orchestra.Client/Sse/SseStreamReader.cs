using System.Runtime.CompilerServices;
using System.Text;

namespace Orchestra.Client.Sse;

/// <summary>
/// Streams Server-Sent Events from a <see cref="StreamReader"/> as <see cref="SseFrame"/>
/// values. Implements the parsing rules from the WHATWG SSE spec that we actually need:
/// <list type="bullet">
///   <item><c>event: NAME</c> sets the type for the next frame.</item>
///   <item><c>data: VALUE</c> appends to the frame's data buffer; multiple data lines are joined with <c>\n</c>.</item>
///   <item>A blank line dispatches the buffered frame.</item>
///   <item>Lines starting with <c>:</c> are comments and ignored.</item>
///   <item><c>id:</c> and <c>retry:</c> fields are accepted but not surfaced (the CLI does not need them).</item>
/// </list>
/// </summary>
public static class SseStreamReader
{
	private const string EventPrefix = "event: ";
	private const string DataPrefix = "data: ";

	/// <summary>
	/// Reads frames from <paramref name="reader"/> until the underlying stream ends or
	/// <paramref name="cancellationToken"/> is cancelled. Frames without any <c>event:</c>
	/// field are emitted with <see cref="SseFrame.Event"/> set to the empty string (per
	/// the spec's default of "message"; the CLI maps that to no rendering).
	/// </summary>
	public static async IAsyncEnumerable<SseFrame> ReadAsync(
		StreamReader reader,
		[EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		string? eventType = null;
		var dataBuilder = new StringBuilder();

		while (true)
		{
			cancellationToken.ThrowIfCancellationRequested();

			string? line;
			try
			{
				line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				yield break;
			}

			if (line is null)
			{
				// End of stream. If we have a partial frame buffered, dispatch it for forward
				// progress; otherwise just exit.
				if (eventType is not null || dataBuilder.Length > 0)
				{
					yield return new SseFrame(eventType ?? string.Empty, dataBuilder.ToString());
				}
				yield break;
			}

			if (line.Length == 0)
			{
				// Blank line: dispatch.
				if (eventType is not null || dataBuilder.Length > 0)
				{
					var frame = new SseFrame(eventType ?? string.Empty, dataBuilder.ToString());
					eventType = null;
					dataBuilder.Clear();
					yield return frame;
				}
				continue;
			}

			if (line[0] == ':')
			{
				// Comment line per SSE spec — ignore.
				continue;
			}

			if (line.StartsWith(EventPrefix, StringComparison.Ordinal))
			{
				eventType = line[EventPrefix.Length..];
			}
			else if (line.StartsWith(DataPrefix, StringComparison.Ordinal))
			{
				if (dataBuilder.Length > 0)
				{
					dataBuilder.Append('\n');
				}
				dataBuilder.Append(line[DataPrefix.Length..]);
			}
			else if (line.Equals("event:", StringComparison.Ordinal))
			{
				eventType = string.Empty;
			}
			else if (line.Equals("data:", StringComparison.Ordinal))
			{
				if (dataBuilder.Length > 0)
				{
					dataBuilder.Append('\n');
				}
			}
			// Other fields (id:, retry:) are ignored; the CLI doesn't need reconnect logic.
		}
	}
}
