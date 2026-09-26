using System.Text;

namespace Orchestra.Engine;

/// <summary>
/// Bounds the output a step may capture. Command, Script and Http steps buffer their whole
/// output in memory and hand it to dependents through templates, so an unbounded capture of a
/// runaway process or a multi-gigabyte response would exhaust the host. Every capture goes
/// through an <see cref="OutputBudget"/>: once the budget is spent, further output is dropped,
/// a visible marker is appended, and the step reports an <c>output-truncated</c> session warning.
/// </summary>
public static class OutputLimits
{
	/// <summary>Default cap when a step sets no <c>maxOutputBytes</c>: 10 MiB.</summary>
	public const int DefaultMaxOutputBytes = 10 * 1024 * 1024;

	/// <summary>Warning type passed to <c>IOrchestrationReporter.ReportSessionWarning</c>.</summary>
	public const string WarningType = "output-truncated";

	/// <summary>
	/// Resolves the effective cap for a step: <c>null</c> means <see cref="DefaultMaxOutputBytes"/>,
	/// <c>0</c> disables the cap (returns <see cref="long.MaxValue"/>), anything else is taken as-is.
	/// Negative values are rejected by the parsers, so they never reach this method.
	/// </summary>
	public static long Resolve(int? configured) => configured switch
	{
		null => DefaultMaxOutputBytes,
		0 => long.MaxValue,
		_ => configured.Value,
	};

	/// <summary>Whether a cap is in effect (i.e. not disabled with <c>0</c>).</summary>
	public static bool IsBounded(int? configured) => configured != 0;

	/// <summary>Marker appended to truncated output so readers (and downstream steps) can tell.</summary>
	public static string Marker(int? configured, long droppedBytes)
		=> $"{Environment.NewLine}[output truncated: exceeded maxOutputBytes ({FormatCap(configured)}); {droppedBytes:N0} bytes dropped]";

	/// <summary>Message for the <see cref="WarningType"/> session warning.</summary>
	public static string WarningMessage(string stepName, int? configured, long droppedBytes)
		=> $"Step '{stepName}' produced more output than maxOutputBytes ({FormatCap(configured)}); {droppedBytes:N0} bytes were dropped. Raise maxOutputBytes on the step, set it to 0 to disable the cap, or write large output to a file.";

	private static string FormatCap(int? configured)
	{
		var cap = Resolve(configured);
		return configured is null ? $"default {cap:N0} bytes" : $"{cap:N0} bytes";
	}
}

/// <summary>
/// A byte budget shared by every stream a step captures (stdout and stderr share one budget so
/// the cap is a per-step limit, not a per-stream one). Thread-safe: the two process streams
/// raise their events on different threads.
/// </summary>
public sealed class OutputBudget
{
	private long _remaining;
	private long _dropped;
	private int _truncated;

	public OutputBudget(long maxBytes)
	{
		if (maxBytes < 0)
			throw new ArgumentOutOfRangeException(nameof(maxBytes), maxBytes, "The output budget cannot be negative.");
		_remaining = maxBytes;
	}

	/// <summary>Creates a budget for a step's configured <c>maxOutputBytes</c>.</summary>
	public static OutputBudget For(int? configuredMaxOutputBytes) => new(OutputLimits.Resolve(configuredMaxOutputBytes));

	/// <summary>Whether any output has been dropped.</summary>
	public bool Truncated => Volatile.Read(ref _truncated) == 1;

	/// <summary>Total bytes dropped so far.</summary>
	public long DroppedBytes => Interlocked.Read(ref _dropped);

	/// <summary>
	/// Reserves <paramref name="bytes"/> from the budget. Returns true when the whole chunk fits;
	/// otherwise records the chunk as dropped and returns false. Chunks are all-or-nothing so a
	/// line is never cut in the middle of a character.
	/// </summary>
	public bool TryReserve(long bytes)
	{
		if (bytes < 0)
			throw new ArgumentOutOfRangeException(nameof(bytes));

		while (true)
		{
			var remaining = Interlocked.Read(ref _remaining);
			if (bytes > remaining)
			{
				Interlocked.Add(ref _dropped, bytes);
				Interlocked.Exchange(ref _truncated, 1);
				return false;
			}

			if (Interlocked.CompareExchange(ref _remaining, remaining - bytes, remaining) == remaining)
				return true;
		}
	}

	/// <summary>
	/// Marks the budget as truncated with <paramref name="droppedBytes"/> more bytes dropped. Used
	/// when the producer stops reading instead of feeding chunks (the Http body stream).
	/// </summary>
	public void MarkTruncated(long droppedBytes)
	{
		Interlocked.Add(ref _dropped, Math.Max(0, droppedBytes));
		Interlocked.Exchange(ref _truncated, 1);
	}
}

/// <summary>
/// Line-oriented text capture for one process stream, charged against a shared
/// <see cref="OutputBudget"/>. Once the budget is exhausted, lines are dropped rather than
/// appended. The caller appends <see cref="OutputLimits.Marker"/> when it materialises the text
/// so the marker is never charged against the budget itself.
/// </summary>
public sealed class BoundedOutputBuffer
{
	private static readonly int s_newLineBytes = Encoding.UTF8.GetByteCount(Environment.NewLine);

	private readonly StringBuilder _builder = new();
	private readonly OutputBudget _budget;
	private readonly Lock _gate = new();

	public BoundedOutputBuffer(OutputBudget budget)
	{
		_budget = budget;
	}

	/// <summary>
	/// Appends <paramref name="line"/> followed by a newline if it fits in the budget. Returns
	/// true when the line was kept, false when it was dropped.
	/// </summary>
	public bool TryAppendLine(string line)
	{
		var bytes = (long)Encoding.UTF8.GetByteCount(line) + s_newLineBytes;
		if (!_budget.TryReserve(bytes))
			return false;

		lock (_gate)
			_builder.AppendLine(line);
		return true;
	}

	/// <summary>The captured text with trailing whitespace removed.</summary>
	public string ToStringTrimmed()
	{
		lock (_gate)
			return _builder.ToString().TrimEnd();
	}
}
