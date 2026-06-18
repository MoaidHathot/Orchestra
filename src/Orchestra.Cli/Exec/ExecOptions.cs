namespace Orchestra.Exec;

/// <summary>
/// How a one-shot run obtains the Orchestra host it runs against.
/// </summary>
internal enum ExecMode
{
	/// <summary>Use an already-running instance when a server URL is configured and healthy;
	/// otherwise spawn a throwaway isolated instance and terminate it afterward.</summary>
	Auto,

	/// <summary>Always spawn a throwaway isolated instance (never reuse a running one).</summary>
	Isolated,

	/// <summary>Require a configured, healthy running instance; error if none is reachable.</summary>
	Existing,
}

/// <summary>
/// Post-run report format for <c>--report</c>.
/// </summary>
internal enum ReportFormat
{
	/// <summary>No report (default): just the result + records path.</summary>
	None,

	/// <summary>Human-readable plain-text digest.</summary>
	Text,

	/// <summary>Structured Markdown (headings + tables).</summary>
	Markdown,

	/// <summary>The raw run record JSON (the same data the Portal consumes).</summary>
	Json,
}

/// <summary>
/// Parsed options for a one-shot run (<c>orchestra run</c> / <c>orchestra exec</c>). Constructed
/// either by the Spectre command (from typed settings) or by <see cref="Parse"/> (argv / tests).
/// </summary>
internal sealed class ExecOptions
{
	/// <summary>How to obtain the host (connect to a running instance vs spawn a throwaway one).</summary>
	public ExecMode Mode { get; init; } = ExecMode.Auto;

	/// <summary>Explicit Orchestra server URL to connect to. Falls back to <c>ORCHESTRA_URL</c> then orchestra.json.</summary>
	public string? ServerUrl { get; init; }

	/// <summary>Ignore the user's <c>orchestra.json</c> / services / global MCP for the spawned instance.</summary>
	public bool NoConfig { get; init; }

	/// <summary>Extra tags applied (in addition to the defaults) when registering into a running instance.</summary>
	public string[] Tags { get; init; } = [];

	/// <summary>Keep a <c>--run-file</c> orchestration registered in a running instance after the run
	/// (default is to remove what we registered, leaving the shared instance unchanged).</summary>
	public bool KeepRegistered { get; init; }

	/// <summary>Registered orchestration id or declared name to run (mutually exclusive with <see cref="RunFile"/>).</summary>
	public string? RunId { get; init; }

	/// <summary>Path to an orchestration file to register-then-run (mutually exclusive with <see cref="RunId"/>).</summary>
	public string? RunFile { get; init; }

	/// <summary>Runtime parameters (repeated <c>--param key=value</c>).</summary>
	public Dictionary<string, string>? Parameters { get; init; }

	/// <summary>Optional hard wall-clock timeout for the run, in seconds.</summary>
	public int? TimeoutSeconds { get; init; }

	/// <summary>Print every SSE event (firehose).</summary>
	public bool Verbose { get; init; }

	/// <summary>Suppress per-step chatter; show only HITL prompts and the final summary.</summary>
	public bool Quiet { get; init; }

	/// <summary>Don't prompt on HITL pauses; print the pending-input message and exit 2.</summary>
	public bool NoInteractive { get; init; }

	/// <summary>Audit identifier recorded with any HITL responses submitted.</summary>
	public string? RespondedBy { get; init; }

	/// <summary>Write the run's final content to this file instead of printing it to stdout.</summary>
	public string? OutputFile { get; init; }

	/// <summary>Surface a richer set of real-time events (model, MCP/tool calls, sub-agents, retries).</summary>
	public bool Detailed { get; init; }

	/// <summary>Post-run report format (default <see cref="ReportFormat.None"/>).</summary>
	public ReportFormat Report { get; init; } = ReportFormat.None;

	/// <summary>Write the report to this file instead of stdout.</summary>
	public string? ReportOutput { get; init; }

	/// <summary>Root data path for run history / registry (overrides config / env).</summary>
	public string? DataPath { get; init; }

	/// <summary>Workspace directory scanned for orchestrations (so <c>--run name</c> resolves).</summary>
	public string? OrchestrationsPath { get; init; }

	/// <summary><c>--help</c> requested.</summary>
	public bool ShowHelp { get; init; }

	/// <summary>Parse error message, when the arguments were invalid.</summary>
	public string? Error { get; init; }

	public const string HelpText = """
		orchestra run / orchestra exec — run a single orchestration to completion.

		Connects to a running Orchestra instance when one is configured and healthy (auto),
		otherwise boots an in-process host with scheduling/triggers/auto-resume disabled, runs
		exactly one orchestration (streaming progress; prompts inline on HITL pauses), then shuts
		the host down and exits with a status code.

		USAGE:
		  orchestra run <name> [options]
		  orchestra run --run-file <path> [options]
		  orchestra exec ...                (= run --mode isolated)

		TARGET (one required):
		  <name>                 Run a registered orchestration (resolved from --orchestrations-path
		                         when isolated, or from the running instance's registry).
		  --run-file <path>      Register the orchestration file, then run it.

		HOST SELECTION:
		  --mode <auto|isolated|existing>
		                         auto (default): use a running instance when one is configured
		                           (via --server, ORCHESTRA_URL, or orchestra.json hostBaseUrl/urls)
		                           and healthy; otherwise spawn a throwaway isolated one.
		                         isolated: always spawn a throwaway instance; never reuse.
		                         existing: require a configured, healthy running instance.
		  --server <url>         Orchestra server URL to connect to (or set ORCHESTRA_URL).

		OPTIONS:
		  --param <key=value>    Runtime parameter (repeatable).
		  --run-timeout <secs>   Hard wall-clock timeout for the run.
		  --output <file>        Write the run's final output to a file instead of stdout.
		  --detailed             Show richer real-time progress (selected model, MCP/tool calls,
		                         sub-agents, retries) without the full --verbose firehose.
		  --report <text|markdown|json>
		                         After the run, print a detailed report (steps, models, token
		                         usage, saved files, output). 'json' is the raw run record.
		  --report-output <file> Write the --report to a file instead of stdout.
		  --orchestrations-path <dir>  Workspace dir scanned for orchestrations (spawned instance).
		  --data-path <dir>      Root data path for run history / registry (spawned instance).
		  --no-config            Ignore orchestra.json / services / global MCP (spawned instance).
		  --tag <name>           Extra tag for --run-file registered into a running instance
		                         (repeatable; added to the defaults 'ephemeral','run-once').
		  --keep-registered      Leave a --run-file orchestration registered in a running
		                         instance after the run (default: remove what we registered).
		  --by <name>            Audit identifier recorded with any HITL responses.
		  -V, --verbose          Print every SSE event.
		  -q, --quiet            Show only HITL prompts and the final summary.
		  --no-interactive       Never prompt on HITL pauses; exit 2 instead.
		  -h, --help             Show this help.

		EXIT CODES:
		  0  succeeded     1  failed / errored     2  non-interactive HITL abort
		  3  usage / launch error                  130  cancelled (Ctrl+C)
		""";

	/// <summary>
	/// Parses argv into an <see cref="ExecOptions"/>. Never throws; invalid input is reported
	/// via <see cref="Error"/> (and <see cref="ShowHelp"/> for <c>-h/--help</c>).
	/// </summary>
	public static ExecOptions Parse(string[] args)
	{
		string? runId = null;
		string? runFile = null;
		var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
		int? timeout = null;
		bool verbose = false, quiet = false, noInteractive = false;
		string? respondedBy = null;
		string? dataPath = null;
		string? orchestrationsPath = null;
		string? outputFile = null;
		var mode = ExecMode.Auto;
		string? serverUrl = null;
		var noConfig = false;
		var tags = new List<string>();
		var keepRegistered = false;
		var detailed = false;
		var report = ReportFormat.None;
		string? reportOutput = null;

		string? Need(string flag, ref int i)
		{
			if (i + 1 >= args.Length)
			{
				return null;
			}
			return args[++i];
		}

		for (var i = 0; i < args.Length; i++)
		{
			var arg = args[i];
			switch (arg)
			{
				case "-h" or "--help":
					return new ExecOptions { ShowHelp = true };

				case "--run":
				{
					var v = Need(arg, ref i);
					if (v is null) return Err($"'{arg}' requires a value.");
					runId = v;
					break;
				}
				case "--run-file":
				{
					var v = Need(arg, ref i);
					if (v is null) return Err($"'{arg}' requires a value.");
					runFile = v;
					break;
				}
				case "--param":
				{
					var v = Need(arg, ref i);
					if (v is null) return Err($"'{arg}' requires a key=value.");
					var idx = v.IndexOf('=');
					if (idx <= 0) return Err($"Invalid --param '{v}' (expected key=value).");
					parameters[v[..idx]] = v[(idx + 1)..];
					break;
				}
				case "--run-timeout":
				{
					var v = Need(arg, ref i);
					if (v is null) return Err($"'{arg}' requires a number of seconds.");
					if (!int.TryParse(v, out var secs) || secs <= 0) return Err($"Invalid --run-timeout '{v}' (expected a positive integer).");
					timeout = secs;
					break;
				}
				case "--orchestrations-path":
				{
					var v = Need(arg, ref i);
					if (v is null) return Err($"'{arg}' requires a directory.");
					orchestrationsPath = v;
					break;
				}
				case "--data-path":
				{
					var v = Need(arg, ref i);
					if (v is null) return Err($"'{arg}' requires a directory.");
					dataPath = v;
					break;
				}
				case "--by":
				{
					var v = Need(arg, ref i);
					if (v is null) return Err($"'{arg}' requires a name.");
					respondedBy = v;
					break;
				}
				case "--output":
				{
					var v = Need(arg, ref i);
					if (v is null) return Err($"'{arg}' requires a file path.");
					outputFile = v;
					break;
				}
				case "--mode":
				{
					var v = Need(arg, ref i);
					if (v is null) return Err($"'{arg}' requires a value (auto|isolated|existing).");
					mode = v.Trim().ToLowerInvariant() switch
					{
						"auto" => ExecMode.Auto,
						"isolated" => ExecMode.Isolated,
						"existing" => ExecMode.Existing,
						_ => (ExecMode)(-1),
					};
					if ((int)mode == -1) return Err($"Invalid --mode '{v}' (expected auto, isolated, or existing).");
					break;
				}
				case "--server":
				{
					var v = Need(arg, ref i);
					if (v is null) return Err($"'{arg}' requires a URL.");
					serverUrl = v;
					break;
				}
				case "--tag":
				{
					var v = Need(arg, ref i);
					if (v is null) return Err($"'{arg}' requires a tag name.");
					if (!string.IsNullOrWhiteSpace(v)) tags.Add(v.Trim());
					break;
				}
				case "--no-config":
					noConfig = true;
					break;
				case "--keep-registered":
					keepRegistered = true;
					break;
				case "--detailed":
					detailed = true;
					break;
				case "--report":
				{
					var v = Need(arg, ref i);
					if (v is null) return Err($"'{arg}' requires a value (text|markdown|json).");
					report = v.Trim().ToLowerInvariant() switch
					{
						"text" => ReportFormat.Text,
						"markdown" or "md" => ReportFormat.Markdown,
						"json" => ReportFormat.Json,
						_ => (ReportFormat)(-1),
					};
					if ((int)report == -1) return Err($"Invalid --report '{v}' (expected text, markdown, or json).");
					break;
				}
				case "--report-output":
				{
					var v = Need(arg, ref i);
					if (v is null) return Err($"'{arg}' requires a file path.");
					reportOutput = v;
					break;
				}
				case "-V" or "--verbose":
					verbose = true;
					break;
				case "-q" or "--quiet":
					quiet = true;
					break;
				case "--no-interactive":
					noInteractive = true;
					break;
				default:
					return Err($"Unknown argument '{arg}'. Use --help for usage.");
			}
		}

		if (runId is null && runFile is null)
		{
			return Err("Specify the orchestration to run with --run <id|name> or --run-file <path>.");
		}
		if (runId is not null && runFile is not null)
		{
			return Err("--run and --run-file are mutually exclusive.");
		}

		return new ExecOptions
		{
			RunId = runId,
			RunFile = runFile,
			Parameters = parameters.Count > 0 ? parameters : null,
			TimeoutSeconds = timeout,
			Verbose = verbose,
			Quiet = quiet,
			NoInteractive = noInteractive,
			RespondedBy = respondedBy,
			DataPath = dataPath,
			OrchestrationsPath = orchestrationsPath,
			OutputFile = outputFile,
			Mode = mode,
			ServerUrl = serverUrl,
			NoConfig = noConfig,
			Tags = tags.ToArray(),
			KeepRegistered = keepRegistered,
			Detailed = detailed,
			Report = report,
			ReportOutput = reportOutput,
		};

		static ExecOptions Err(string message) => new() { Error = message };
	}
}
