using System.ComponentModel;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Orchestra.Cli.Commands;

// ─────────────────────────────────────────────────────────────────────────────
// logs
//
// The host writes one log file per day under {dataPath}/logs. This tails the newest one
// through the host API, so it works against a running server as well as a workspace with
// no server up (the spawned management host serves the files without writing to them).
// ─────────────────────────────────────────────────────────────────────────────

public sealed class LogsSettings : ManagedCommandSettings
{
	[CommandOption("-n|--lines <N>")]
	[Description("Number of trailing lines to show (default 100, max 5000)")]
	[DefaultValue(100)]
	public int Lines { get; set; } = 100;

	[CommandOption("-f|--follow")]
	[Description("Keep printing new lines as the host writes them (Ctrl+C to stop)")]
	public bool Follow { get; set; }

	[CommandOption("--level <LEVEL>")]
	[Description("Show only entries at this level or above: Trace, Debug, Information, Warning, Error, Critical")]
	public string? Level { get; set; }

	[CommandOption("--interval-ms <MS>")]
	[Description("Poll interval for --follow (default 1000)")]
	[DefaultValue(1000)]
	public int IntervalMs { get; set; } = 1000;

	public override ValidationResult Validate()
	{
		if (Lines < 0)
			return ValidationResult.Error("--lines must be 0 or greater.");
		if (IntervalMs < 100)
			return ValidationResult.Error("--interval-ms must be at least 100.");
		if (Level is not null && !Enum.TryParse<LogLevel>(Level, ignoreCase: true, out _))
			return ValidationResult.Error($"--level '{Level}' is not a log level. Use Trace, Debug, Information, Warning, Error or Critical.");
		return base.Validate();
	}
}

public sealed class LogsCommand : AsyncCommand<LogsSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, LogsSettings settings)
		=> await ManagedSession.RunAsync(settings, async client =>
		{
			var minimum = settings.Level is null ? LogLevel.Trace : Enum.Parse<LogLevel>(settings.Level, ignoreCase: true);
			var filter = new LogLineFilter(minimum);

			JsonElement first;
			try
			{
				first = await client.GetLogsAsync(lines: settings.Lines);
			}
			catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
			{
				Console.Error.WriteLine(ex.Message.Replace("Server returned 404: ", string.Empty));
				return;
			}

			if (string.Equals(settings.Format, "json", StringComparison.OrdinalIgnoreCase) && !settings.Follow)
			{
				OutputWriter.Write(first, settings.Format);
				return;
			}

			PrintLines(first, filter);
			if (!settings.Follow)
				return;

			var file = first.GetProperty("file").GetString();
			var offset = first.GetProperty("offset").GetInt64();

			using var stop = new CancellationTokenSource();
			ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
			Console.CancelKeyPress += onCancel;
			try
			{
				while (!stop.IsCancellationRequested)
				{
					try
					{
						await Task.Delay(settings.IntervalMs, stop.Token);
					}
					catch (OperationCanceledException)
					{
						break;
					}

					JsonElement next;
					try
					{
						next = await client.GetLogsAsync(offset: offset, file: file);
					}
					catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
					{
						continue; // the file may be mid-roll; try again next tick
					}

					var nextFile = next.GetProperty("file").GetString();
					if (!string.Equals(nextFile, file, StringComparison.OrdinalIgnoreCase))
						Console.Error.WriteLine($"--- log rolled to {nextFile} ---");

					PrintLines(next, filter);
					file = nextFile;
					offset = next.GetProperty("offset").GetInt64();
				}
			}
			finally
			{
				Console.CancelKeyPress -= onCancel;
			}
		});

	private static void PrintLines(JsonElement payload, LogLineFilter filter)
	{
		foreach (var line in payload.GetProperty("lines").EnumerateArray())
		{
			var text = line.GetString() ?? string.Empty;
			if (filter.Accept(text))
				Console.Out.WriteLine(text);
		}
	}
}

/// <summary>
/// Level filter over the file logger's line format
/// (<c>yyyy-MM-dd HH:mm:ss [Level] Category: message</c>). Lines that do not start with a
/// timestamp are continuations (exception stacks) and follow the entry they belong to.
/// </summary>
public sealed class LogLineFilter(LogLevel minimum)
{
	private bool _lastAccepted = true;

	public bool Accept(string line)
	{
		if (minimum == LogLevel.Trace)
			return true;

		var level = TryParseLevel(line);
		if (level is null)
			return _lastAccepted; // continuation line

		_lastAccepted = level.Value >= minimum;
		return _lastAccepted;
	}

	/// <summary>The bracketed level of an entry line, or null for continuation lines.</summary>
	public static LogLevel? TryParseLevel(string line)
	{
		// "2026-09-27 10:15:42 [Warning] ..." -> the level sits right after the 19-char timestamp.
		if (line.Length < 22 || line[4] != '-' || line[10] != ' ' || line[19] != ' ' || line[20] != '[')
			return null;

		var close = line.IndexOf(']', 21);
		if (close < 0)
			return null;

		return Enum.TryParse<LogLevel>(line.AsSpan(21, close - 21), ignoreCase: true, out var level) ? level : null;
	}
}
