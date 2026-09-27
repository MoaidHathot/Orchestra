using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Orchestra.Host.Hosting;
using Orchestra.Watchers;
using Orchestra.Watchers.Config;
using Orchestra.Watchers.Models;
using Orchestra.Watchers.Runtime;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Orchestra.Cli.Commands;

/// <summary>
/// Settings for <see cref="WatcherTestCommand"/>.
/// </summary>
public sealed class WatcherTestSettings : CommandSettings
{
	[CommandOption("-w|--watcher <NAME>")]
	[Description("Name of the watcher to run (a key in orchestra.watchers.json).")]
	public string? Watcher { get; set; }

	[CommandOption("--config-path <PATH>")]
	[Description("Path to the orchestra.watchers.json to load. Defaults to the one beside the orchestra.json in effect.")]
	public string? ConfigPath { get; set; }

	[CommandOption("--from-state <PATH>")]
	[Description("Start from a state file written by a previous --save-state run (cursor and simulated seen keys).")]
	public string? FromState { get; set; }

	[CommandOption("--empty-state")]
	[Description("Start with no cursor and no seen keys (the default when --from-state is not given).")]
	public bool EmptyState { get; set; }

	[CommandOption("--save-state <PATH>")]
	[Description("Write the resulting cursor and simulated seen keys to this file for a follow-up --from-state run.")]
	public string? SaveState { get; set; }

	[CommandOption("--ticks <N>")]
	[Description("Number of fetches to run in sequence (the cursor and seen keys carry over between ticks).")]
	[DefaultValue(1)]
	public int Ticks { get; set; } = 1;

	[CommandOption("--subscribers <IDS>")]
	[Description("Comma-separated orchestration ids to simulate dedup for. Without it items are only listed.")]
	public string? Subscribers { get; set; }

	[CommandOption("--print-input")]
	[Description("Print the full stdin payload (indented) instead of a one-line summary.")]
	public bool PrintInput { get; set; }

	public override ValidationResult Validate()
	{
		if (string.IsNullOrWhiteSpace(Watcher))
			return ValidationResult.Error("--watcher <NAME> is required.");

		if (Ticks < 1)
			return ValidationResult.Error("--ticks must be at least 1.");

		if (FromState is not null && EmptyState)
			return ValidationResult.Error("--from-state and --empty-state are mutually exclusive.");

		return ValidationResult.Success();
	}

	/// <summary>Simulated subscriber ids (deduplicated, in order).</summary>
	public IReadOnlyList<string> SubscriberIds =>
		(Subscribers ?? string.Empty)
			.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Distinct(StringComparer.Ordinal)
			.ToArray();
}

/// <summary>
/// <c>orchestra watcher test</c> - runs a watcher's fetcher once (or a few times) exactly as the
/// host would, validates the output against the v1 protocol schema and shows which items each
/// simulated subscriber would dispatch. Read-only: it never starts the host, never touches the
/// host's watcher state and never launches an orchestration.
/// </summary>
public sealed class WatcherTestCommand : AsyncCommand<WatcherTestSettings>
{
	/// <summary>Every tick fetched, validated and extracted cleanly.</summary>
	public const int SuccessExitCode = 0;

	/// <summary>A fetch, validation or extraction failed on some tick.</summary>
	public const int FetchFailureExitCode = 1;

	/// <summary>The library or watcher could not be resolved.</summary>
	public const int ConfigErrorExitCode = 2;

	private static readonly JsonSerializerOptions s_stateJson = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};

	private readonly IAnsiConsole _console;

	public WatcherTestCommand(IAnsiConsole console) => _console = console;

	public override async Task<int> ExecuteAsync(CommandContext context, WatcherTestSettings settings)
	{
		var cancellationToken = CancellationToken.None;
		var libraryPath = settings.ConfigPath is not null
			? Path.GetFullPath(settings.ConfigPath)
			: OrchestraConfigLoader.ResolveWatchersConfigPath();

		if (libraryPath is null || !File.Exists(libraryPath))
		{
			var looked = libraryPath ?? Path.Combine(OrchestraConfigLoader.ResolveConfigDirectory(), OrchestraConfigLoader.WatchersConfigFileName);
			_console.MarkupLine($"[red]error:[/] no {OrchestraConfigLoader.WatchersConfigFileName} found at {Markup.Escape(looked)}. Pass --config-path <file>.");
			return ConfigErrorExitCode;
		}

		WatcherLibrary library;
		try
		{
			library = WatcherConfigLoader.Load(libraryPath);
		}
		catch (Exception ex) when (ex is WatcherValidationException or IOException or UnauthorizedAccessException or Orchestra.Engine.Serialization.EnvironmentVariableExpansionException)
		{
			_console.MarkupLine($"[red]error:[/] {Markup.Escape(libraryPath)}: {Markup.Escape(ex.Message)}");
			return ConfigErrorExitCode;
		}

		if (!library.TryGet(settings.Watcher!, out var entry))
		{
			var known = library.Watchers.Count == 0 ? "(none)" : string.Join(", ", library.Watchers.Keys.OrderBy(k => k, StringComparer.Ordinal));
			_console.MarkupLine($"[red]error:[/] watcher '{Markup.Escape(settings.Watcher!)}' is not declared in {Markup.Escape(libraryPath)}. Known watchers: {Markup.Escape(known)}");
			return ConfigErrorExitCode;
		}

		WatcherTestState state;
		try
		{
			state = settings.FromState is not null ? WatcherTestState.Load(settings.FromState) : new WatcherTestState();
		}
		catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
		{
			_console.MarkupLine($"[red]error:[/] could not read --from-state {Markup.Escape(settings.FromState!)}: {Markup.Escape(ex.Message)}");
			return ConfigErrorExitCode;
		}

		var subscriberIds = settings.SubscriberIds;
		var fetcher = new WatcherFetcherFactory().Create(entry);
		var validator = new WatcherOutputValidator();
		var dedup = entry.EffectiveDedup;

		_console.MarkupLine($"== orchestra watcher test: [bold]{Markup.Escape(entry.Name)}[/] ==");
		_console.WriteLine();
		WriteField("Resolved from", libraryPath);
		WriteField("Type", entry.TypeName);
		WriteField("Command", DescribeCommand(entry));
		WriteField("Schedule", entry.Schedule.Description);
		WriteField("Timeout", $"{entry.FetchBase.TimeoutSeconds}s");
		WriteField("Dedup", $"{dedup.Mode.ToString().ToLowerInvariant()} key={(dedup.Key ?? "<canonical hash>")}");
		WriteField("Failure policy", entry.FailurePolicy.ToString().ToLowerInvariant());

		var exitCode = SuccessExitCode;
		for (var tick = 1; tick <= settings.Ticks; tick++)
		{
			_console.WriteLine();
			if (settings.Ticks > 1)
				_console.MarkupLine($"[bold]-- tick {tick}/{settings.Ticks} --[/]");

			var request = WatcherFetchRequest.Create(entry, state.Cursor, subscriberIds);
			_console.MarkupLine("[bold]stdin sent:[/]");
			WriteIndented(settings.PrintInput ? request.ToIndentedJson() : Abbreviate(request.ToJson(), 400));

			var result = await fetcher.RunAsync(entry, request, cancellationToken);
			_console.WriteLine();
			_console.MarkupLine($"[bold]stdout received[/] ({result.RawStdout.Length} chars, exit {result.ExitCode}, {result.Elapsed.TotalMilliseconds:0}ms):");
			WriteIndented(Abbreviate(result.RawStdout.Trim(), 800));

			if (!string.IsNullOrWhiteSpace(result.RawStdErr))
			{
				_console.MarkupLine("[bold]stderr:[/]");
				WriteIndented(Abbreviate(result.RawStdErr.Trim(), 800), "grey");
			}

			if (!result.Succeeded)
			{
				_console.WriteLine();
				_console.MarkupLine($"[red]fetch failed:[/] {Markup.Escape(result.FailureReason ?? "unknown reason")}");
				exitCode = FetchFailureExitCode;
				continue;
			}

			_console.WriteLine();
			if (!validator.TryValidate(result.Output, out var errors))
			{
				_console.MarkupLine("[red]schema validation: FAILED[/]");
				foreach (var error in errors)
					_console.MarkupLine($"  [red]{Markup.Escape(error.JsonPointer)}[/]: {Markup.Escape(error.Message)}");
				exitCode = FetchFailureExitCode;
				continue;
			}

			_console.MarkupLine("schema validation: [green]OK[/]");

			var output = new WatcherFetcherOutput(result.Output!.AsObject());
			IReadOnlyList<JsonNode?> items;
			try
			{
				items = output.SelectItems(entry.Items);
			}
			catch (FormatException ex)
			{
				_console.MarkupLine($"[red]items.jsonPath is invalid:[/] {Markup.Escape(ex.Message)}");
				exitCode = FetchFailureExitCode;
				continue;
			}

			var jsonPath = entry.Items?.JsonPath ?? WatcherItemsConfig.DefaultJsonPath;
			_console.WriteLine();
			_console.MarkupLine($"[bold]items extracted[/] (jsonPath={Markup.Escape(jsonPath)}): {items.Count}");

			var keyed = new List<(string Key, JsonNode? Item)>();
			var seenThisTick = new HashSet<string>(StringComparer.Ordinal);
			foreach (var item in items)
			{
				var key = WatcherDedupEngine.KeyFor(item, dedup);
				if (seenThisTick.Add(key))
					keyed.Add((key, item));
			}

			foreach (var (key, item) in keyed.Take(20))
				_console.MarkupLine($"  [grey]{Markup.Escape(key)}[/]  {Markup.Escape(Abbreviate(item?.ToJsonString() ?? "null", 120))}");
			if (keyed.Count > 20)
				_console.MarkupLine($"  [grey]... {keyed.Count - 20} more[/]");
			if (keyed.Count < items.Count)
				_console.MarkupLine($"  [yellow]{items.Count - keyed.Count} item(s) share a key with an earlier item this tick and would dispatch once.[/]");

			_console.WriteLine();
			if (subscriberIds.Count == 0)
			{
				_console.MarkupLine("[bold]simulated subscribers:[/] (none - pass --subscribers to simulate dedup)");
			}
			else
			{
				_console.MarkupLine("[bold]simulated subscribers:[/]");
				foreach (var subscriber in subscriberIds)
				{
					var seenList = state.SeenKeysFor(subscriber);
					var seen = new HashSet<string>(seenList, StringComparer.Ordinal);
					var fresh = keyed.Where(k => !seen.Contains(k.Key)).ToList();
					_console.MarkupLine($"  {Markup.Escape(subscriber)}: [green]{fresh.Count} new[/], {keyed.Count - fresh.Count} seen");
					foreach (var (key, _) in fresh.Take(20))
						_console.MarkupLine($"    dispatch [grey]{Markup.Escape(key)}[/]");
					seenList.AddRange(fresh.Select(f => f.Key));
				}
			}

			_console.WriteLine();
			_console.MarkupLine("[bold]cursor advance preview:[/]");
			var cursorBefore = state.Cursor?.ToJsonString() ?? "null";
			var cursorAfterNode = output.Cursor is { } cursor && cursor.GetValueKind() != JsonValueKind.Null ? cursor.DeepClone() : state.Cursor;
			var cursorAfter = cursorAfterNode?.ToJsonString() ?? "null";
			_console.MarkupLine($"  before: {Markup.Escape(cursorBefore)}");
			_console.MarkupLine($"  after:  {Markup.Escape(cursorAfter)}{(cursorAfter == cursorBefore ? " [grey](unchanged)[/]" : string.Empty)}");
			state.Cursor = cursorAfterNode;

			if (output.Diagnostics is { } diagnostics)
				_console.MarkupLine($"  diagnostics: {Markup.Escape(Abbreviate(diagnostics.ToJsonString(), 300))}");
		}

		if (settings.SaveState is not null)
		{
			try
			{
				state.Save(settings.SaveState);
				_console.WriteLine();
				_console.MarkupLine($"state saved to {Markup.Escape(Path.GetFullPath(settings.SaveState))}");
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				_console.MarkupLine($"[red]error:[/] could not write --save-state {Markup.Escape(settings.SaveState)}: {Markup.Escape(ex.Message)}");
				if (exitCode == SuccessExitCode)
					exitCode = FetchFailureExitCode;
			}
		}

		_console.WriteLine();
		_console.MarkupLine($"exit: {exitCode}");
		return exitCode;
	}

	private void WriteField(string label, string value)
		=> _console.MarkupLine($"{Markup.Escape(label + ":"),-15} {Markup.Escape(value)}");

	private void WriteIndented(string text, string? color = null)
	{
		foreach (var line in text.Split('\n'))
		{
			var escaped = Markup.Escape(line.TrimEnd('\r'));
			_console.MarkupLine(color is null ? $"  {escaped}" : $"  [{color}]{escaped}[/]");
		}
	}

	private static string DescribeCommand(WatcherEntry entry) => entry switch
	{
		PollCommandWatcher c => string.Join(' ', new[] { c.Fetch.Command }.Concat(c.Fetch.Arguments)),
		PollScriptWatcher s => string.Join(' ', new[] { s.Fetch.Shell, s.Fetch.ScriptFile ?? "<inline script>" }.Concat(s.Fetch.Arguments)),
		_ => entry.TypeName,
	};

	private static string Abbreviate(string text, int max)
		=> text.Length <= max ? text : text[..max] + $"... ({text.Length - max} more chars)";

	/// <summary>
	/// The portable state <c>watcher test</c> carries between runs: the cursor plus, per simulated
	/// subscriber, the keys already "dispatched". Deliberately not the host's on-disk state format -
	/// the command must never touch the live state directory.
	/// </summary>
	internal sealed class WatcherTestState
	{
		public JsonNode? Cursor { get; set; }

		public Dictionary<string, List<string>> SeenKeys { get; set; } = new(StringComparer.Ordinal);

		public List<string> SeenKeysFor(string subscriber)
		{
			if (!SeenKeys.TryGetValue(subscriber, out var keys))
			{
				keys = [];
				SeenKeys[subscriber] = keys;
			}

			return keys;
		}

		public static WatcherTestState Load(string path)
		{
			var json = File.ReadAllText(path);
			return JsonSerializer.Deserialize<WatcherTestState>(json, s_stateJson) ?? new WatcherTestState();
		}

		public void Save(string path)
		{
			var full = Path.GetFullPath(path);
			var dir = Path.GetDirectoryName(full);
			if (!string.IsNullOrEmpty(dir))
				Directory.CreateDirectory(dir);
			File.WriteAllText(full, JsonSerializer.Serialize(this, s_stateJson));
		}
	}
}
