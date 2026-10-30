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
			var end = await CommittedLengthAsync(stream, stream.Length, cancellationToken).ConfigureAwait(false);
			if (end != stream.Length)
				stream.SetLength(end);
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
		if (after < 0 || limit is < 1 or > 1000)
			throw new ArgumentOutOfRangeException(nameof(limit), "Plan history requires after >= 0 and limit between 1 and 1000.");
		var directories = FindPlanDirectories(runId, cancellationToken);
		if (directories.Length > 1)
		{
			// An execution ID may itself contain underscores. Folder suffixes are only a
			// lookup hint; the journal's stored identity disambiguates e.g. run and other_run.
			var matching = new List<string>();
			foreach (var directory in directories)
				if (await JournalBelongsToRunAsync(Path.Combine(directory, PlanHistoryFileName), runId, cancellationToken).ConfigureAwait(false))
					matching.Add(directory);
			directories = matching.ToArray();
		}
		if (directories.Length == 0)
			return new([], null);
		if (directories.Length > 1)
			throw new InvalidOperationException("The run ID matches more than one stored execution.");
		var path = Path.Combine(directories[0], PlanHistoryFileName);
		if (!File.Exists(path))
			return new([], null);
		var items = new List<AgentPlanRevision>();
		var partial = false;
		try
		{
			await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
			// Read a fixed committed prefix; a concurrent append never produces a torn JSON row.
			var length = stream.Length;
			if (length == 0)
				return new([], null);
			var committedLength = await CommittedLengthAsync(stream, length, cancellationToken).ConfigureAwait(false);
			partial = committedLength != length;
			stream.Position = 0;
			using var prefix = new PlanHistoryPrefix(stream, committedLength);
			using var reader = new StreamReader(prefix, Encoding.UTF8);
			long cursor = 0;
			while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
			{
				cancellationToken.ThrowIfCancellationRequested();
				cursor++;
				if (cursor <= after)
					continue;
				AgentPlanRevision? revision;
				try
				{ revision = JsonSerializer.Deserialize<AgentPlanRevision>(line, PlanJson); }
				catch (JsonException ex) { partial = true; LogInvalidPlanHistory(_logger, ex, runId, cursor); continue; }
				if (revision?.Run is null || revision.Plan is null)
				{ partial = true; continue; }
				if (revision.Run.RunId != runId || (stepName is not null && revision.StepName != stepName))
					continue;
				if (items.Count == limit)
					return new(items, cursor - 1, partial);
				items.Add(revision);
			}
		}
		catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return new([], null); }
		return new(items, null, partial);
	}

	private static void ValidatePlanRunId(string runId)
	{
		if (string.IsNullOrWhiteSpace(runId) || runId.Length > 128 || runId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
			throw new ArgumentException("Invalid execution ID.", nameof(runId));
	}

	private static async Task<long> CommittedLengthAsync(FileStream stream, long length, CancellationToken token)
	{
		var buffer = new byte[4096];
		while (length > 0)
		{
			token.ThrowIfCancellationRequested();
			var count = (int)Math.Min(length, buffer.Length);
			var start = length - count;
			stream.Position = start;
			// A writer may be repairing an uncommitted tail concurrently. A short read is
			// harmless: only an already-terminated prefix can be returned to the caller.
			var read = await stream.ReadAtLeastAsync(buffer.AsMemory(0, count), count, throwOnEndOfStream: false, token).ConfigureAwait(false);
			var newline = buffer.AsSpan(0, read).LastIndexOf((byte)'\n');
			if (newline >= 0)
				return start + newline + 1;
			length = start;
		}
		return 0;
	}

	private string[] FindPlanDirectories(string runId, CancellationToken token)
	{
		var result = new List<string>();
		try
		{
			foreach (var orchestration in Directory.GetDirectories(_rootPath))
			{
				token.ThrowIfCancellationRequested();
				try
				{
					result.AddRange(Directory.GetDirectories(orchestration)
						.Where(d => Path.GetFileName(d).EndsWith("_" + runId, StringComparison.Ordinal)));
				}
				catch (DirectoryNotFoundException) { /* concurrent run retention */ }
			}
		}
		catch (DirectoryNotFoundException) { }
		return result.ToArray();
	}

	private static async Task<bool> JournalBelongsToRunAsync(string path, string runId, CancellationToken token)
	{
		try
		{
			await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
			using var reader = new StreamReader(stream, Encoding.UTF8);
			while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line)
			{
				token.ThrowIfCancellationRequested();
				try
				{
					if (JsonSerializer.Deserialize<AgentPlanRevision>(line, PlanJson)?.Run is { } run)
						return run.RunId == runId;
				}
				catch (JsonException) { }
			}
		}
		catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
		return false;
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
