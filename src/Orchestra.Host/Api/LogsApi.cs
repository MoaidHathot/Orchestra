using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Orchestra.Host.Hosting;
using Orchestra.Host.Logging;

namespace Orchestra.Host.Api;

/// <summary>
/// <c>GET /api/logs</c>: tail of the host's current log file, for <c>orchestra logs</c> and
/// the portal. Reads the file the <see cref="FileLoggerProvider"/> writes; works even when file
/// logging is disabled on this host (it then reports the newest file left by a previous one).
/// </summary>
public static class LogsApi
{
	/// <summary>Largest tail a single request may ask for.</summary>
	public const int MaxLines = 5_000;

	public static IEndpointRouteBuilder MapLogsApi(this IEndpointRouteBuilder endpoints)
	{
		// GET /api/logs?lines=200            -> last N lines of the newest log file
		// GET /api/logs?offset=<bytes>&file= -> everything appended after <offset> (follow mode)
		endpoints.MapGet("/api/logs", async (
			int? lines,
			long? offset,
			string? file,
			OrchestrationHostOptions options,
			CancellationToken cancellationToken) =>
		{
			if (lines is < 0 || offset is < 0)
				return ProblemDetailsHelpers.BadRequest("lines and offset must be 0 or greater.");

			var directory = options.FileLogging.ResolveDirectory(options.DataPath);
			var tail = await LogFileReader.ReadAsync(directory, options.FileLogging.FilePattern, lines ?? 200, offset, file, cancellationToken);

			if (tail is null)
				return ProblemDetailsHelpers.NotFound(options.FileLogging.Enabled
					? $"No log file has been written yet under '{directory}'."
					: $"File logging is disabled on this host and no log file exists under '{directory}'.");

			return Results.Ok(new
			{
				directory,
				file = tail.FileName,
				path = tail.Path,
				sizeBytes = tail.SizeBytes,
				offset = tail.Offset,
				truncated = tail.Truncated,
				enabled = options.FileLogging.Enabled,
				lines = tail.Lines,
			});
		});

		return endpoints;
	}
}

/// <summary>
/// Reads the tail of a rolling log directory. Files are named <c>{prefix}-yyyy-MM-dd.log</c>,
/// so the newest file is the ordinal-greatest name.
/// </summary>
public static class LogFileReader
{
	/// <summary>Result of one read.</summary>
	/// <param name="FileName">The file that was read.</param>
	/// <param name="Path">Its full path.</param>
	/// <param name="SizeBytes">Its size when read - pass back as <c>offset</c> to follow.</param>
	/// <param name="Offset">Alias of <see cref="SizeBytes"/>: where the next follow read should start.</param>
	/// <param name="Truncated">True when a tail request was capped at <see cref="LogsApi.MaxLines"/>.</param>
	/// <param name="Lines">The lines read, oldest first.</param>
	public sealed record Tail(string FileName, string Path, long SizeBytes, long Offset, bool Truncated, IReadOnlyList<string> Lines);

	/// <summary>The newest log file in <paramref name="directory"/>, or null when there is none.</summary>
	public static string? FindNewestFile(string directory, string pattern)
	{
		if (!Directory.Exists(directory))
			return null;

		return Directory.GetFiles(directory, pattern)
			.OrderByDescending(f => System.IO.Path.GetFileName(f), StringComparer.Ordinal)
			.FirstOrDefault();
	}

	/// <summary>
	/// Tail mode (<paramref name="offset"/> null): the last <paramref name="lines"/> lines of the
	/// newest file. Follow mode (<paramref name="offset"/> set): everything appended to
	/// <paramref name="file"/> after <paramref name="offset"/>; when a newer file exists (the day
	/// rolled) or the file shrank, reading restarts from the beginning of the newest file.
	/// Returns null when the directory holds no log file.
	/// </summary>
	public static async Task<Tail?> ReadAsync(string directory, string pattern, int lines, long? offset, string? file, CancellationToken cancellationToken)
	{
		var newest = FindNewestFile(directory, pattern);
		if (newest is null)
			return null;

		lines = Math.Clamp(lines, 0, LogsApi.MaxLines);

		if (offset is null)
			return await ReadTailAsync(newest, lines, cancellationToken);

		// Follow: continue on the same file if it is still the newest; otherwise start over on
		// the new day's file so the caller sees the roll instead of silently stalling.
		var sameFile = file is not null && string.Equals(System.IO.Path.GetFileName(newest), file, StringComparison.OrdinalIgnoreCase);
		var start = sameFile ? offset.Value : 0;
		return await ReadFromOffsetAsync(newest, start, cancellationToken);
	}

	private static async Task<Tail> ReadTailAsync(string path, int lines, CancellationToken cancellationToken)
	{
		// Share-friendly read: the provider holds the file open for append.
		await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		var size = stream.Length;
		if (lines == 0)
			return new Tail(System.IO.Path.GetFileName(path), path, size, size, false, []);

		// Read backwards in chunks until enough newlines are found or the file starts.
		const int chunk = 64 * 1024;
		var buffer = new List<byte>();
		var position = size;
		var newlines = 0;
		while (position > 0 && newlines <= lines)
		{
			var read = (int)Math.Min(chunk, position);
			position -= read;
			stream.Seek(position, SeekOrigin.Begin);
			var bytes = new byte[read];
			await stream.ReadExactlyAsync(bytes, cancellationToken);
			buffer.InsertRange(0, bytes);
			newlines += bytes.Count(b => b == (byte)'\n');
		}

		var text = Encoding.UTF8.GetString(buffer.ToArray());
		var all = SplitLines(text);
		var droppedLines = all.Count > lines;
		var tail = droppedLines ? all.Skip(all.Count - lines).ToList() : all;
		// Truncated = the response is not the whole file: either we dropped lines from what we
		// read, or we stopped reading backwards before reaching the start of the file.
		return new Tail(System.IO.Path.GetFileName(path), path, size, size, droppedLines || position > 0, tail);
	}

	private static async Task<Tail> ReadFromOffsetAsync(string path, long offset, CancellationToken cancellationToken)
	{
		await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		var size = stream.Length;
		if (offset > size)
			offset = 0; // the file was replaced or truncated; start over

		stream.Seek(offset, SeekOrigin.Begin);
		var bytes = new byte[size - offset];
		await stream.ReadExactlyAsync(bytes, cancellationToken);
		var text = Encoding.UTF8.GetString(bytes);
		return new Tail(System.IO.Path.GetFileName(path), path, size, size, false, SplitLines(text));
	}

	private static List<string> SplitLines(string text)
	{
		if (text.Length == 0)
			return [];

		var parts = text.Split('\n');
		var result = new List<string>(parts.Length);
		foreach (var part in parts)
			result.Add(part.TrimEnd('\r'));

		// A trailing newline yields one empty final element that is not a line.
		if (result.Count > 0 && result[^1].Length == 0)
			result.RemoveAt(result.Count - 1);
		return result;
	}
}
