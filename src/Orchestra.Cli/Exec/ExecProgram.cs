using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Orchestra.Cli.Commands;
using Orchestra.Cli.Hosting;
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
	/// argv entry used by tests and the legacy parser. Parses, handles <c>--help</c>/errors, then
	/// delegates to <see cref="RunCoreAsync"/>.
	/// </summary>
	public static async Task<int> RunAsync(string[] args, ExecHooks? hooks = null)
	{
		var options = ExecOptions.Parse(args);

		if (options.ShowHelp)
		{
			AnsiConsole.WriteLine(ExecOptions.HelpText);
			return 0;
		}

		if (options.Error is not null)
		{
			AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(options.Error)}");
			return LaunchErrorExitCode;
		}

		return await RunCoreAsync(options, hooks);
	}

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
