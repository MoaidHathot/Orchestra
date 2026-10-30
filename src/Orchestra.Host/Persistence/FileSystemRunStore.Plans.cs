using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Orchestra.Engine;

namespace Orchestra.Host.Persistence;

public partial class FileSystemRunStore : IPlanHistoryStore
{
	private static readonly JsonSerializerOptions PlanJson = new(JsonSerializerDefaults.Web);
	public const string PlanHistoryFileName = "plan-events.jsonl";

	public async Task AppendPlanAsync(AgentPlanRevision revision, CancellationToken cancellationToken = default)
	{
		var run = revision.Run;
		ValidatePlanRunId(run.RunId);
		var directory = ResolveRunDirectory(run.OrchestrationName, run.OrchestrationVersion, run.TriggeredBy, run.StartedAt, run.RunId);
		var gate = _fileWriteLocks.GetOrAdd(run.OrchestrationName, _ => new SemaphoreSlim(1, 1));
		await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			Directory.CreateDirectory(directory);
			await using var stream = new FileStream(Path.Combine(directory, PlanHistoryFileName), FileMode.OpenOrCreate,
				FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
			// A newline commits a record. Discard an interrupted final write before appending.
			var end = stream.Length;
			while (end > 0)
			{
				stream.Position = end - 1;
				if (stream.ReadByte() == '\n') break;
				end--;
			}
			if (end != stream.Length) stream.SetLength(end);
			stream.Position = end;
			var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(revision, PlanJson) + "\n");
			await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
			await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
		}
		finally { gate.Release(); }
	}

	public async Task<PlanHistoryPage> ReadPlansAsync(string runId, string? stepName = null, long after = 0, int limit = 100, CancellationToken cancellationToken = default)
	{
		ValidatePlanRunId(runId);
		if (after < 0 || limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit), "Plan history requires after >= 0 and limit between 1 and 1000.");
		var directories = Directory.EnumerateDirectories(_rootPath)
			.Where(d => !Path.GetFileName(d).StartsWith('.'))
			.SelectMany(d => Directory.EnumerateDirectories(d))
			.Where(d => Path.GetFileName(d).EndsWith("_" + runId, StringComparison.Ordinal)).Take(2).ToArray();
		if (directories.Length == 0) return new([], null);
		if (directories.Length > 1) throw new InvalidOperationException("The run ID matches more than one stored execution.");
		var path = Path.Combine(directories[0], PlanHistoryFileName);
		if (!File.Exists(path)) return new([], null);
		var items = new List<AgentPlanRevision>();
		var partial = false;
		try
		{
			await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
			// Read a fixed committed prefix; a concurrent append never produces a torn JSON row.
			var length = stream.Length;
			if (length == 0) return new([], null);
			stream.Position = length - 1;
			var terminated = stream.ReadByte() == '\n';
			stream.Position = 0;
			using var prefix = new PlanHistoryPrefix(stream, length);
			using var reader = new StreamReader(prefix, Encoding.UTF8);
			long cursor = 0;
			var nextLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
			while (nextLine is { } line)
			{
				nextLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
				cursor++;
				if (nextLine is null && !terminated) { partial = true; break; }
				if (cursor <= after) continue;
				AgentPlanRevision? revision;
				try { revision = JsonSerializer.Deserialize<AgentPlanRevision>(line, PlanJson); }
				catch (JsonException ex) { partial = true; LogInvalidPlanHistory(_logger, ex, runId, cursor); continue; }
				if (revision?.Run is null || revision.Plan is null) { partial = true; continue; }
				if (revision.Run.RunId != runId || (stepName is not null && revision.StepName != stepName)) continue;
				if (items.Count == limit) return new(items, cursor - 1, partial);
				items.Add(revision);
			}
		}
		catch (FileNotFoundException) { return new([], null); }
		return new(items, null, partial);
	}

	private static void ValidatePlanRunId(string runId)
	{
		if (string.IsNullOrWhiteSpace(runId) || runId.Length > 128 || runId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
			throw new ArgumentException("Invalid execution ID.", nameof(runId));
	}

	private sealed class PlanHistoryPrefix(Stream source, long length) : Stream
	{
		private long _position;
		public override bool CanRead => true;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => length;
		public override long Position { get => _position; set => throw new NotSupportedException(); }
		public override int Read(byte[] buffer, int offset, int count)
		{
			var read = source.Read(buffer, offset, (int)Math.Min(count, length - _position));
			_position += read;
			return read;
		}
		public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
		{
			var read = await source.ReadAsync(buffer[..(int)Math.Min(buffer.Length, length - _position)], cancellationToken).ConfigureAwait(false);
			_position += read;
			return read;
		}
		public override void Flush() { }
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
	}

	[LoggerMessage(Level = LogLevel.Warning, Message = "Skipping malformed plan history in run {RunId}, line {Line}")]
	private static partial void LogInvalidPlanHistory(ILogger logger, Exception ex, string runId, long line);
}
