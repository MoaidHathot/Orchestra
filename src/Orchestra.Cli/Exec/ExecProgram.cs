using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Orchestra.Cli.Commands;
using Orchestra.Cli.Hosting;
using Orchestra.Cli.Validation;
using Orchestra.Client;
using Orchestra.Client.Run;
using Orchestra.Engine;
using Orchestra.Host.Hosting;
using Spectre.Console;

namespace Orchestra.Exec;

/// <summary>
/// Optional seams for tests / embedding hosts to influence an <see cref="ExecProgram"/> run
/// without going through argv (inject a fake agent, force the test environment, script HITL).
/// </summary>
internal sealed class ExecHooks
{
	/// <summary>Mutate the builder before <c>Build()</c> (e.g. set the Testing environment).</summary>
	public Action<WebApplicationBuilder>? ConfigureBuilder { get; init; }

	/// <summary>Add/override services after host registration (e.g. replace the <c>AgentBuilder</c>).</summary>
	public Action<IServiceCollection>? ConfigureServices { get; init; }

	/// <summary>Override the HITL prompter (e.g. a scripted auto-approver in tests).</summary>
	public IHumanInputPrompter? Prompter { get; init; }

	/// <summary>Invoked once a spawned host has started (Kestrel listening), before the run is driven.
	/// Lets tests inspect the live service provider (e.g. assert scheduling is disabled).</summary>
	public Action<IServiceProvider>? OnHostStarted { get; init; }
}

/// <summary>
/// Core run engine behind <c>orchestra run</c> / <c>orchestra exec</c>. Runs exactly one
/// orchestration and returns a POSIX-style exit code. Depending on <see cref="ExecMode"/> it
/// either connects to an already-running Orchestra instance (leaving it untouched) or spawns an
/// isolated, one-shot in-process host (scheduling/triggers/auto-resume disabled), runs over a
/// loopback connection — reusing the same interactive run stack as the CLI's remote-streaming
/// commands — then shuts it down.
/// </summary>
internal static class ExecProgram
{
	/// <summary>Exit code for usage / launch errors (bad args, missing file, unknown orchestration).</summary>
	public const int LaunchErrorExitCode = 3;

	/// <summary>Tags applied to a <c>--run-file</c> orchestration registered into a running instance,
	/// so these one-shot registrations can be found and pruned later.</summary>
	public static readonly string[] DefaultRegistrationTags = ["ephemeral", "run-once"];

	/// <summary>
	/// Runs a single orchestration from an already-validated <see cref="ExecOptions"/>. This is the
	/// shared core called by both the argv entry above and the Spectre <c>run</c>/<c>exec</c> commands.
	/// </summary>
	public static async Task<int> RunCoreAsync(ExecOptions options, ExecHooks? hooks = null)
	{
		// Resolve --run-file to a concrete orchestration name up front so we fail fast on a
		// missing/unparseable file before paying to boot the host or hit the network.
		string? runFileFullPath = null;
		var runTarget = options.RunId;
		if (options.RunFile is not null)
		{
			runFileFullPath = Path.GetFullPath(options.RunFile);
			if (!File.Exists(runFileFullPath))
			{
				AnsiConsole.MarkupLine($"[red]Error:[/] orchestration file not found: {Markup.Escape(runFileFullPath)}");
				return LaunchErrorExitCode;
			}

			try
			{
				runTarget = OrchestrationParser.ParseOrchestrationFileMetadataOnly(runFileFullPath).Name;
			}
			catch (Exception ex)
			{
				AnsiConsole.MarkupLine($"[red]Error:[/] failed to parse orchestration file: {Markup.Escape(ex.Message)}");
				return LaunchErrorExitCode;
			}
		}

		if (runTarget is null)
		{
			AnsiConsole.MarkupLine("[red]Error:[/] specify the orchestration to run (a name, or --run-file <path>).");
			return LaunchErrorExitCode;
		}

		// --dry-run with a file never needs a host: parse, pre-flight and print the plan locally.
		if (options.DryRun && runFileFullPath is not null)
			return DryRunFile(runFileFullPath, options);

		// Attach to a running instance or spawn a throwaway one-shot host — the shared
		// connect-or-spawn machinery used by the managed verbs too.
		var request = new HostSessionRequest
		{
			ServerUrl = ResolveServerUrl(options),
			Mode = options.Mode,
			NoConfig = options.NoConfig,
			DataPath = options.DataPath,
			OrchestrationsPath = options.OrchestrationsPath,
			SpawnedInstanceNoun = "one-shot instance",
			SpawnOnlyOptionLabels = SpawnOnlyOptionsInEffect(options),
			ConfigureIsolation = ConfigureOneShotHost,
			ConfigureBuilder = hooks?.ConfigureBuilder,
			ConfigureServices = hooks?.ConfigureServices,
			OnHostStarted = hooks?.OnHostStarted,
		};

		var sessionResult = await OrchestraHostSessionFactory.ConnectOrSpawnAsync(request);
		if (!sessionResult.Ok)
		{
			AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(sessionResult.ErrorMessage!)}");
			return LaunchErrorExitCode;
		}

		foreach (var note in sessionResult.Notes)
		{
			AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(note)}[/]");
		}

		await using var session = sessionResult.Session!;

		// --dry-run by name: the registry knows the definition; render its plan and stop.
		if (options.DryRun)
			return await DryRunRegisteredAsync(session.Client, runTarget, options);

		// Only tag/clean up registrations on a shared running instance; a throwaway instance is
		// discarded wholesale, so there is nothing to tag or remove afterward.
		IReadOnlyList<string> registerTags = session.Spawned
			? []
			: DefaultRegistrationTags.Concat(options.Tags).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

		return await DriveRunAsync(
			session.Client,
			runTarget,
			runFileFullPath,
			registerTags,
			removeAfterRun: !session.Spawned && !options.KeepRegistered,
			options,
			hooks);
	}

	/// <summary>Isolation profile for the one-shot exec host: load the registry so <c>--run</c>
	/// resolves, but disable everything that could auto-fire or resume so only the requested
	/// orchestration runs.</summary>
	private static void ConfigureOneShotHost(OrchestrationHostOptions o)
	{
		o.EnableScheduler = false;
		o.RegisterJsonTriggers = false;
		o.LoadPersistedTriggers = false;
		o.AutoResumeCheckpointsOnStartup = false;
		o.LoadPersistedOrchestrations = true;
	}

	/// <summary>Spawn-only option labels in effect, surfaced as an "ignored when using a running
	/// instance" note if we end up attaching to one.</summary>
	private static IReadOnlyList<string> SpawnOnlyOptionsInEffect(ExecOptions options)
	{
		var labels = new List<string>();
		if (options.NoConfig) labels.Add("--no-config");
		if (options.DataPath is not null) labels.Add("--data-path");
		if (options.OrchestrationsPath is not null) labels.Add("--orchestrations-path");
		return labels;
	}

	/// <summary>
	/// Resolves the candidate server URL to attach to (auto/existing modes): explicit
	/// <c>--server</c> → <c>ORCHESTRA_URL</c> → the configured <c>hostBaseUrl</c> (or first
	/// <c>urls</c> entry) from the discovered <c>orchestra.json</c>. Returns null when nothing is
	/// configured, or when <c>--no-config</c> opts out of config discovery. Shared with the CLI's
	/// managed client verbs via <see cref="ClientFactory.ResolveServerUrlOrNull"/> so
	/// <c>run</c>/<c>exec</c> and <c>list</c>/<c>get</c>/… all resolve the same instance.
	/// </summary>
	private static string? ResolveServerUrl(ExecOptions options)
		=> ClientFactory.ResolveServerUrlOrNull(options.ServerUrl, options.NoConfig);

	// ── --dry-run ──────────────────────────────────────────────────────────────

	/// <summary>Exit code when a dry run finds problems that would stop the run.</summary>
	public const int DryRunInvalidExitCode = 1;

	/// <summary>
	/// Local dry run of a file: the executor's own pre-flight (<see cref="RunPlan"/>) plus the
	/// layered plan, without a host. Exit 0 when the run could start, 1 otherwise.
	/// </summary>
	internal static int DryRunFile(string runFileFullPath, ExecOptions options)
	{
		// Same MCP catalog the host would use, so a globally declared MCP does not fail the plan.
		var globalMcps = GlobalMcpCatalog.Load(out var mcpError);
		if (mcpError is not null)
			AnsiConsole.MarkupLine($"[yellow]Warning:[/] {Markup.Escape(mcpError)}");

		Orchestration orchestration;
		try
		{
			orchestration = OrchestrationParser.ParseOrchestrationFile(runFileFullPath, globalMcps);
		}
		catch (Exception ex)
		{
			AnsiConsole.MarkupLine($"[red]Error:[/] failed to parse orchestration file: {Markup.Escape(ex.Message)}");
			return LaunchErrorExitCode;
		}

		var plan = RunPlan.Build(orchestration, options.Parameters);
		RenderPlan(plan.Orchestration.Name, runFileFullPath, plan.Layers.Select(l => l.Select(DryRunStep.From).ToList()).ToList(), plan.EffectiveParameters, plan.Errors, plan.Warnings, options);
		return plan.IsValid ? 0 : DryRunInvalidExitCode;
	}

	/// <summary>
	/// Dry run of a registered orchestration: renders the host's own layered projection of it
	/// (<c>GET /api/orchestrations/{id}</c>) and checks the supplied parameters against the
	/// declared inputs. The host has already parsed and validated the definition to register it.
	/// </summary>
	internal static async Task<int> DryRunRegisteredAsync(OrchestraClient client, string runTarget, ExecOptions options)
	{
		JsonElement detail;
		try
		{
			detail = await client.GetOrchestrationAsync(runTarget);
		}
		catch (HttpRequestException ex)
		{
			AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
			return LaunchErrorExitCode;
		}

		var steps = detail.TryGetProperty("steps", out var stepsEl) && stepsEl.ValueKind == JsonValueKind.Array
			? stepsEl.EnumerateArray().Select(DryRunStep.From).ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase)
			: new Dictionary<string, DryRunStep>(StringComparer.OrdinalIgnoreCase);

		var layers = new List<List<DryRunStep>>();
		if (detail.TryGetProperty("layers", out var layersEl) && layersEl.ValueKind == JsonValueKind.Array)
		{
			foreach (var layer in layersEl.EnumerateArray())
			{
				if (!layer.TryGetProperty("steps", out var names) || names.ValueKind != JsonValueKind.Array)
					continue;
				layers.Add(names.EnumerateArray()
					.Select(n => n.GetString())
					.Where(n => n is not null)
					.Select(n => steps.TryGetValue(n!, out var s) ? s : new DryRunStep(n!, "?", [], null))
					.ToList());
			}
		}

		var errors = new List<string>();
		var warnings = new List<string>();
		var effective = new Dictionary<string, string>(options.Parameters ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
		var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		if (detail.TryGetProperty("inputs", out var inputs) && inputs.ValueKind == JsonValueKind.Object)
		{
			foreach (var input in inputs.EnumerateObject())
			{
				declared.Add(input.Name);
				var required = !input.Value.TryGetProperty("required", out var req) || req.ValueKind != JsonValueKind.False;
				var hasDefault = input.Value.TryGetProperty("default", out var def) && def.ValueKind != JsonValueKind.Null;
				if (!effective.ContainsKey(input.Name))
				{
					if (hasDefault)
						effective[input.Name] = def.ValueKind == JsonValueKind.String ? def.GetString()! : def.ToString();
					else if (required)
						errors.Add($"missing required input '{input.Name}'. Provide it with --param {input.Name}=<value>.");
				}
			}
		}
		else if (detail.TryGetProperty("parameters", out var parameters) && parameters.ValueKind == JsonValueKind.Array)
		{
			foreach (var p in parameters.EnumerateArray().Select(p => p.GetString()).Where(p => p is not null))
			{
				declared.Add(p!);
				if (!effective.ContainsKey(p!))
					errors.Add($"missing required parameter '{p}'. Provide it with --param {p}=<value>.");
			}
		}

		foreach (var key in effective.Keys.Where(k => !declared.Contains(k)))
			warnings.Add($"parameter '{key}' is not declared by the orchestration; it is still available as {{{{param.{key}}}}}.");

		if (detail.TryGetProperty("validationErrors", out var ve) && ve.ValueKind == JsonValueKind.Array)
			errors.AddRange(ve.EnumerateArray().Select(e => e.GetString()).Where(e => !string.IsNullOrWhiteSpace(e))!);

		var name = detail.TryGetProperty("name", out var n) ? n.GetString() ?? runTarget : runTarget;
		var path = detail.TryGetProperty("path", out var p2) ? p2.GetString() : null;
		RenderPlan(name, path, layers, effective, errors, warnings, options);
		return errors.Count == 0 ? 0 : DryRunInvalidExitCode;
	}

	/// <summary>One row of the plan; built from the engine model or from the host's JSON projection.</summary>
	internal sealed record DryRunStep(string Name, string Type, IReadOnlyList<string> DependsOn, string? When)
	{
		public static DryRunStep From(OrchestrationStep step)
			=> new(step.Name, step.Type.ToString(), step.DependsOn, step.When);

		public static DryRunStep From(JsonElement step)
		{
			var name = step.TryGetProperty("name", out var n) ? n.GetString() ?? "?" : "?";
			var type = step.TryGetProperty("type", out var t) ? t.GetString() ?? "?" : "?";
			var deps = step.TryGetProperty("dependsOn", out var d) && d.ValueKind == JsonValueKind.Array
				? d.EnumerateArray().Select(x => x.GetString()).Where(x => x is not null).Select(x => x!).ToList()
				: [];
			var when = step.TryGetProperty("when", out var w) && w.ValueKind == JsonValueKind.String ? w.GetString() : null;
			return new DryRunStep(name, type, deps, when);
		}
	}

	private static void RenderPlan(
		string name,
		string? path,
		IReadOnlyList<List<DryRunStep>> layers,
		IReadOnlyDictionary<string, string> effectiveParameters,
		IReadOnlyList<string> errors,
		IReadOnlyList<string> warnings,
		ExecOptions options)
	{
		var stepCount = layers.Sum(l => l.Count);

		if (options.Report == ReportFormat.Json)
		{
			var json = JsonSerializer.Serialize(new
			{
				dryRun = true,
				valid = errors.Count == 0,
				name,
				path,
				stepCount,
				layers = layers.Select((l, i) => new
				{
					layer = i + 1,
					steps = l.Select(s => new { s.Name, type = s.Type, dependsOn = s.DependsOn, when = s.When }),
				}),
				parameters = effectiveParameters,
				errors,
				warnings,
			}, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
			Console.Out.WriteLine(json);
			return;
		}

		var verdict = errors.Count == 0 ? "[green]plan[/]" : "[red]plan (would not start)[/]";
		AnsiConsole.MarkupLine($"{verdict}  {Markup.Escape(name)} ({stepCount} step{(stepCount == 1 ? "" : "s")}, {layers.Count} layer{(layers.Count == 1 ? "" : "s")}){(path is null ? string.Empty : "  " + Markup.Escape(path))}");

		for (var i = 0; i < layers.Count; i++)
		{
			AnsiConsole.MarkupLine($"  [grey]layer {i + 1}:[/]");
			foreach (var step in layers[i])
			{
				var deps = step.DependsOn.Count > 0 ? $"  [grey]after[/] {Markup.Escape(string.Join(", ", step.DependsOn))}" : string.Empty;
				var when = step.When is not null ? $"  [grey]when[/] {Markup.Escape(step.When)}" : string.Empty;
				AnsiConsole.MarkupLine($"    {Markup.Escape(step.Name)} [grey]({Markup.Escape(step.Type)})[/]{deps}{when}");
			}
		}

		if (effectiveParameters.Count > 0)
		{
			AnsiConsole.MarkupLine("  [grey]parameters:[/]");
			foreach (var (key, value) in effectiveParameters.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
				AnsiConsole.MarkupLine($"    {Markup.Escape(key)} = {Markup.Escape(value)}");
		}

		foreach (var warning in warnings)
			AnsiConsole.MarkupLine($"  [yellow]warning:[/] {Markup.Escape(warning)}");
		foreach (var error in errors)
			AnsiConsole.MarkupLine($"  [red]error:[/] {Markup.Escape(error)}");
	}

	private static async Task<int> DriveRunAsync(
		OrchestraClient client,
		string runTarget,
		string? runFileFullPath,
		IReadOnlyList<string> registerTags,
		bool removeAfterRun,
		ExecOptions options,
		ExecHooks? hooks)
	{
		// The client is owned by the OrchestraHostSession; this method must not dispose it.
		// --run-file: register the file so the run endpoint can resolve it by name. When running
		// against a shared instance, also tag the registration so it can be pruned later.
		var createdByUs = false;
		if (runFileFullPath is not null)
		{
			// Only ever remove what WE add: if the orchestration already exists on the target,
			// leave the user's registration untouched even after the run.
			var existedBefore = removeAfterRun && await ExistsAsync(client, runTarget);
			try
			{
				var registered = await client.RegisterOrchestrationAsync(runFileFullPath);
				await ApplyRegistrationTagsAsync(client, registered, registerTags);
				createdByUs = removeAfterRun && !existedBefore;
			}
			catch (Exception ex)
			{
				AnsiConsole.MarkupLine($"[red]Error:[/] failed to register orchestration: {Markup.Escape(ex.Message)}");
				return LaunchErrorExitCode;
			}
		}

		var ctrlCPressed = false;
		try
		{
			using var cts = new CancellationTokenSource();
			if (options.TimeoutSeconds is { } secs)
			{
				cts.CancelAfter(TimeSpan.FromSeconds(secs));
			}

			void OnCancelKeyPress(object? _, ConsoleCancelEventArgs e)
			{
				// Cancel the run and let the host shut down cleanly rather than hard-killing.
				e.Cancel = true;
				ctrlCPressed = true;
				cts.Cancel();
			}

			Console.CancelKeyPress += OnCancelKeyPress;
			try
			{
				using var response = await client.OpenRunStreamAsync(runTarget, options.Parameters, cts.Token);
				var session = RunSessionFactory.Build(
					client,
					verbose: options.Verbose,
					quiet: options.Quiet,
					noInteractive: options.NoInteractive,
					respondedBy: options.RespondedBy,
					prompterOverride: hooks?.Prompter,
					detailed: options.Detailed);

				var result = await session.RunAsync(response, runTarget, cts.Token);
				await RenderPostRunAsync(client, result, options);
				return RunExitCode.Map(result, ctrlCPressed);
			}
			catch (HttpRequestException ex)
			{
				AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
				return LaunchErrorExitCode;
			}
			finally
			{
				Console.CancelKeyPress -= OnCancelKeyPress;
			}
		}
		finally
		{
			// Remove the transient registration we created — but NOT on Ctrl+C: the run keeps
			// going server-side there, so we leave it registered (and tagged) and findable.
			if (createdByUs && !ctrlCPressed)
			{
				try
				{
					await client.RemoveOrchestrationAsync(runTarget);
				}
				catch (Exception ex)
				{
					AnsiConsole.MarkupLine($"[yellow]Warning:[/] could not remove orchestration after run: {Markup.Escape(ex.Message)}");
				}
			}
		}
	}

	/// <summary>Returns true when an orchestration with the given id/name is registered on the target.</summary>
	private static async Task<bool> ExistsAsync(OrchestraClient client, string nameOrId)
	{
		try
		{
			await client.GetOrchestrationAsync(nameOrId);
			return true;
		}
		catch (HttpRequestException)
		{
			return false;
		}
	}

	/// <summary>
	/// After a server-side terminal run, reads the persisted run record and surfaces: where the
	/// records are stored, and either the run's final output (default) or a full report
	/// (<c>--report</c>) to stdout or a file. Best-effort: failures here never change the exit code.
	/// </summary>
	private static async Task RenderPostRunAsync(OrchestraClient client, RunSessionResult result, ExecOptions options)
	{
		// Only completed server-side runs have a persisted record to read back. Skip when we
		// disconnected (Ctrl+C) or aborted a HITL pause non-interactively.
		if (result.RunId is null || result.OrchestrationName is null
			|| result.Outcome is RunSessionOutcome.Disconnected or RunSessionOutcome.NonInteractiveAbort)
		{
			return;
		}

		JsonElement record;
		try
		{
			record = await client.GetRunAsync(result.OrchestrationName, result.RunId);
		}
		catch (Exception ex)
		{
			AnsiConsole.MarkupLine($"[yellow]Warning:[/] could not retrieve run record: {Markup.Escape(ex.Message)}");
			return;
		}

		await RenderRecordsLocationAsync(client, record);

		if (options.Report != ReportFormat.None)
		{
			RenderReport(record, options);
			return;
		}

		RenderResult(record, options);
	}

	/// <summary>Prints the absolute path where this run's records live, plus the root data path
	/// under which all run records are stored.</summary>
	private static async Task RenderRecordsLocationAsync(OrchestraClient client, JsonElement record)
	{
		string? runDir = null;
		if (record.TryGetProperty("context", out var ctx) && ctx.ValueKind == JsonValueKind.Object
			&& ctx.TryGetProperty("dataDirectory", out var dd) && dd.ValueKind == JsonValueKind.String)
		{
			runDir = dd.GetString();
		}

		string? rootDataPath = null;
		try
		{
			var status = await client.GetStatusAsync();
			if (status.TryGetProperty("dataPath", out var dp) && dp.ValueKind == JsonValueKind.String)
			{
				rootDataPath = dp.GetString();
			}
		}
		catch
		{
			// Status is optional context; ignore failures.
		}

		AnsiConsole.WriteLine();
		if (!string.IsNullOrEmpty(rootDataPath))
		{
			AnsiConsole.MarkupLine($"[grey]Records root:[/] {Markup.Escape(rootDataPath)}");
		}
		if (!string.IsNullOrEmpty(runDir))
		{
			AnsiConsole.MarkupLine($"[grey]This run:    [/] {Markup.Escape(runDir)}");
		}
	}

	private static void RenderResult(JsonElement record, ExecOptions options)
	{
		var finalContent = record.TryGetProperty("finalContent", out var fc) && fc.ValueKind == JsonValueKind.String
			? fc.GetString()
			: null;

		if (string.IsNullOrEmpty(finalContent))
		{
			return;
		}

		if (options.OutputFile is not null)
		{
			WriteToFile(options.OutputFile, finalContent, "Result");
			return;
		}

		AnsiConsole.WriteLine();
		AnsiConsole.MarkupLine("[bold]Result:[/]");
		// Raw content (no markup) so `orchestra run ... | …` pipes the output cleanly.
		Console.Out.WriteLine(finalContent);
	}

	private static void RenderReport(JsonElement record, ExecOptions options)
	{
		string reportText;
		try
		{
			reportText = RunReport.Render(record, options.Report);
		}
		catch (Exception ex)
		{
			AnsiConsole.MarkupLine($"[yellow]Warning:[/] could not build report: {Markup.Escape(ex.Message)}");
			return;
		}

		if (options.ReportOutput is not null)
		{
			WriteToFile(options.ReportOutput, reportText, "Report");
			return;
		}

		AnsiConsole.WriteLine();
		Console.Out.Write(reportText);
	}

	private static void WriteToFile(string path, string content, string label)
	{
		try
		{
			var full = Path.GetFullPath(path);
			File.WriteAllText(full, content);
			AnsiConsole.MarkupLine($"[green]{label} written to[/] {Markup.Escape(full)}");
		}
		catch (Exception ex)
		{
			AnsiConsole.MarkupLine($"[yellow]Warning:[/] could not write {label.ToLowerInvariant()} file: {Markup.Escape(ex.Message)}");
		}
	}

	/// <summary>
	/// Tags the just-registered orchestration (best-effort). Tagging failures never fail the run —
	/// the tags are provenance metadata for later cleanup, not a correctness requirement.
	/// </summary>
	private static async Task ApplyRegistrationTagsAsync(OrchestraClient client, JsonElement registered, IReadOnlyList<string> tags)
	{
		if (tags.Count == 0)
		{
			return;
		}

		if (!registered.TryGetProperty("added", out var added)
			|| added.ValueKind != JsonValueKind.Array
			|| added.GetArrayLength() == 0
			|| !added[0].TryGetProperty("id", out var idProp)
			|| idProp.GetString() is not { Length: > 0 } id)
		{
			return;
		}

		try
		{
			await client.AddTagsAsync(id, tags.ToArray());
		}
		catch (Exception ex)
		{
			AnsiConsole.MarkupLine($"[yellow]Warning:[/] could not tag orchestration: {Markup.Escape(ex.Message)}");
		}
	}
}
