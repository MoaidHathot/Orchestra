using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Orchestra.Client;
using Orchestra.Client.Run;
using Orchestra.Composition;
using Orchestra.Engine;
using Orchestra.Host.Extensions;
using Orchestra.Host.Hosting;
using Orchestra.Host.McpServer;
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
/// Entry logic for <c>orchestra-exec</c>. Runs exactly one orchestration and returns a
/// POSIX-style exit code. Depending on <see cref="ExecMode"/> it either connects to an
/// already-running Orchestra instance (leaving it untouched) or spawns an isolated, one-shot
/// in-process host (scheduling/triggers/auto-resume disabled), runs over a loopback connection
/// — reusing the same interactive run stack as the <c>orchestra</c> CLI — then shuts it down.
/// </summary>
internal static class ExecProgram
{
	/// <summary>Exit code for usage / launch errors (bad args, missing file, unknown orchestration).</summary>
	public const int LaunchErrorExitCode = 3;

	/// <summary>Tags applied to a <c>--run-file</c> orchestration registered into a running instance,
	/// so these one-shot registrations can be found and pruned later.</summary>
	public static readonly string[] DefaultRegistrationTags = ["ephemeral", "run-once"];

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

		var serverUrl = (options.ServerUrl ?? Environment.GetEnvironmentVariable("ORCHESTRA_URL"))?.Trim();
		if (string.IsNullOrWhiteSpace(serverUrl))
		{
			serverUrl = null;
		}

		// ── Connect to a running instance? ────────────────────────────────────────────
		// Detection is conservative: we only probe when the user pointed us at a server
		// (via --server or ORCHESTRA_URL). 'isolated' skips detection entirely.
		if (options.Mode != ExecMode.Isolated && serverUrl is not null)
		{
			if (await ProbeServerHealthyAsync(serverUrl, TimeSpan.FromSeconds(2)))
			{
				WarnIgnoredSpawnOptions(options, usingExisting: true);
				var registerTags = DefaultRegistrationTags.Concat(options.Tags).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
				return await DriveRunAsync(serverUrl, runTarget!, runFileFullPath, registerTags, removeAfterRun: !options.KeepRegistered, options, hooks);
			}

			if (options.Mode == ExecMode.Existing)
			{
				AnsiConsole.MarkupLine($"[red]Error:[/] no healthy Orchestra instance reachable at {Markup.Escape(serverUrl)}.");
				return LaunchErrorExitCode;
			}

			// auto: configured server is down — fall back to spawning a throwaway instance.
			AnsiConsole.MarkupLine($"[yellow]No running Orchestra at {Markup.Escape(serverUrl)}; spawning a one-shot instance.[/]");
		}
		else if (options.Mode == ExecMode.Existing)
		{
			AnsiConsole.MarkupLine("[red]Error:[/] --mode existing requires a server URL (set --server or ORCHESTRA_URL).");
			return LaunchErrorExitCode;
		}

		// ── Spawn an isolated, one-shot instance ───────────────────────────────────────
		if (options.Mode == ExecMode.Isolated && serverUrl is not null)
		{
			AnsiConsole.MarkupLine("[yellow]--server is ignored in isolated mode.[/]");
		}

		return await RunSpawnedAsync(runTarget!, runFileFullPath, options, hooks);
	}

	private static async Task<int> RunSpawnedAsync(
		string runTarget,
		string? runFileFullPath,
		ExecOptions options,
		ExecHooks? hooks)
	{
		// Pick a free loopback port BEFORE building so the host's HostBaseUrl (used for
		// self-referential /mcp/data callbacks) resolves to the real listening address.
		var url = $"http://127.0.0.1:{GetFreeLoopbackPort()}";

		var builder = WebApplication.CreateBuilder();
		builder.Logging.AddSimpleConsole(o =>
		{
			o.SingleLine = true;
			o.IncludeScopes = false;
			o.TimestampFormat = "HH:mm:ss ";
			o.ColorBehavior = LoggerColorBehavior.Enabled;
		});
		// Keep the console focused on the orchestration's own streamed output; host
		// chatter stays at Warning+ unless the operator opts into more.
		builder.Logging.SetMinimumLevel(LogLevel.Warning);
		builder.WebHost.UseUrls(url);

		// --no-config: also skip the co-located orchestra.services.json / orchestra.mcp.json so
		// the spawned instance is fully reproducible (orchestra.json itself is skipped via the
		// loadConfigurationFile:false argument to AddOrchestraHost below).
		if (options.NoConfig)
		{
			builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["skip-services"] = "true" });
		}

		builder.Services.AddOrchestraHost(o =>
		{
			if (options.DataPath is not null)
			{
				o.DataPath = Path.GetFullPath(options.DataPath);
			}

			if (options.OrchestrationsPath is not null)
			{
				o.Scan = new ScanConfig
				{
					Directory = Path.GetFullPath(options.OrchestrationsPath),
					Watch = false,
					Recursive = true,
				};
			}

			// Isolated one-shot: nothing auto-fires, nothing auto-resumes; only the requested
			// orchestration runs. The registry is still loaded so --run <id|name> resolves.
			o.EnableScheduler = false;
			o.RegisterJsonTriggers = false;
			o.LoadPersistedTriggers = false;
			o.AutoResumeCheckpointsOnStartup = false;
			o.LoadPersistedOrchestrations = true;
		}, loadConfigurationFile: !options.NoConfig);

		// Register agent providers (copilot + opencode) keyed + the provider registry for
		// per-step / per-orchestration selection. Tests override this via hooks.ConfigureServices.
		builder.Services.AddOrchestraAgentProviders();

		builder.Services.AddOrchestraMcpServer();

		hooks?.ConfigureBuilder?.Invoke(builder);
		hooks?.ConfigureServices?.Invoke(builder.Services);

		var app = builder.Build();

		await app.Services.InitializeOrchestraHostAsync();

		app.UseOrchestraHostProblemDetails();
		app.MapOrchestraHostEndpoints();
		app.MapOrchestraMcpEndpoints();

		await app.StartAsync();

		try
		{
			hooks?.OnHostStarted?.Invoke(app.Services);
			// A throwaway instance is discarded after the run, so there is nothing to tag or clean up.
			return await DriveRunAsync(url, runTarget, runFileFullPath, registerTags: [], removeAfterRun: false, options, hooks);
		}
		finally
		{
			await app.StopAsync();
		}
	}

	private static async Task<int> DriveRunAsync(
		string url,
		string runTarget,
		string? runFileFullPath,
		IReadOnlyList<string> registerTags,
		bool removeAfterRun,
		ExecOptions options,
		ExecHooks? hooks)
	{
		using var http = new HttpClient { BaseAddress = new Uri(url.TrimEnd('/') + "/") };
		using var client = new OrchestraClient(http);

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
		// Raw content (no markup) so `orchestra-exec ... | …` pipes the output cleanly.
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

	/// <summary>
	/// Probes <c>GET {baseUrl}/api/health</c> with a short timeout. Returns true only on a 2xx
	/// response; any error (connection refused, timeout, non-Orchestra service) is treated as
	/// "not running".
	/// </summary>
	private static async Task<bool> ProbeServerHealthyAsync(string baseUrl, TimeSpan timeout)
	{
		try
		{
			using var http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"), Timeout = timeout };
			using var response = await http.GetAsync("api/health");
			return response.IsSuccessStatusCode;
		}
		catch
		{
			return false;
		}
	}

	private static void WarnIgnoredSpawnOptions(ExecOptions options, bool usingExisting)
	{
		if (!usingExisting)
		{
			return;
		}

		var ignored = new List<string>();
		if (options.NoConfig) ignored.Add("--no-config");
		if (options.DataPath is not null) ignored.Add("--data-path");
		if (options.OrchestrationsPath is not null) ignored.Add("--orchestrations-path");

		if (ignored.Count > 0)
		{
			AnsiConsole.MarkupLine(
				$"[yellow]Note:[/] {Markup.Escape(string.Join(", ", ignored))} only apply to a spawned instance and are ignored when using a running one.");
		}
	}

	/// <summary>
	/// Reserves an ephemeral loopback TCP port by binding a listener to port 0 and reading the
	/// assigned port. There is a small TOCTOU window before Kestrel binds, but on loopback it is
	/// effectively immediate and collisions are vanishingly rare.
	/// </summary>
	private static int GetFreeLoopbackPort()
	{
		var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		try
		{
			return ((IPEndPoint)listener.LocalEndpoint).Port;
		}
		finally
		{
			listener.Stop();
		}
	}
}
