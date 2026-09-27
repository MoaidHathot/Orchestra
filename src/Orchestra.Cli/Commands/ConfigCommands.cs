using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Orchestra.Host.Hosting;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Orchestra.Cli.Commands;

/// <summary>
/// Settings for <see cref="ConfigShowCommand"/>.
/// </summary>
public sealed class ConfigShowSettings : CommandSettings
{
	[CommandOption("--format <FORMAT>")]
	[Description("Output format: 'text' (default) or 'json'.")]
	[DefaultValue("text")]
	public string Format { get; set; } = "text";

	public override ValidationResult Validate()
	{
		if (!string.Equals(Format, "text", StringComparison.OrdinalIgnoreCase)
			&& !string.Equals(Format, "json", StringComparison.OrdinalIgnoreCase))
		{
			return ValidationResult.Error($"Invalid --format '{Format}' (expected text or json).");
		}

		return ValidationResult.Success();
	}
}

/// <summary>
/// <c>orchestra config show</c> - which <c>orchestra.json</c> is in effect, where it came from,
/// which sidecars (<c>orchestra.mcp.json</c> / <c>orchestra.services.json</c>) go with it, and
/// the resolved values every host built from this directory would use. Answers "why is the
/// tool not using my settings?" without booting a host.
/// </summary>
public sealed class ConfigShowCommand : Command<ConfigShowSettings>
{
	private readonly IAnsiConsole _console;

	public ConfigShowCommand(IAnsiConsole console) => _console = console;

	public override int Execute(CommandContext context, ConfigShowSettings settings)
	{
		var report = ConfigReport.Build();

		if (string.Equals(settings.Format, "json", StringComparison.OrdinalIgnoreCase))
		{
			_console.WriteLine(JsonSerializer.Serialize(report, ConfigReport.JsonOptions));
			return report.Error is null ? 0 : 1;
		}

		Render(report);
		return report.Error is null ? 0 : 1;
	}

	private void Render(ConfigReport report)
	{
		if (report.ConfigPath is null)
		{
			_console.MarkupLine("[yellow]No orchestra.json found[/] - built-in defaults apply.");
			_console.MarkupLine($"  Create one with [bold]orchestra init[/], or place it at {Markup.Escape(report.DefaultConfigPath)}.");
		}
		else
		{
			_console.MarkupLine($"[bold]orchestra.json[/]  {Markup.Escape(report.ConfigPath)}  [grey]({Markup.Escape(report.SourceDescription)})[/]");
			if (report.Error is not null)
				_console.MarkupLine($"  [red]error:[/] {Markup.Escape(report.Error)}");
		}

		_console.WriteLine();
		_console.MarkupLine("[bold]Sidecars[/]");
		RenderSidecar(report.McpConfig, OrchestraConfigLoader.McpConfigFileName);
		RenderSidecar(report.ServicesConfig, OrchestraConfigLoader.ServiceConfigFileName);

		_console.WriteLine();
		_console.MarkupLine("[bold]Effective settings[/]");
		var table = new Table().Border(TableBorder.None).HideHeaders().AddColumn("k").AddColumn("v");
		foreach (var (key, value) in report.Effective)
			table.AddRow(Markup.Escape(key), Markup.Escape(value));
		_console.Write(table);

		_console.WriteLine();
		_console.MarkupLine("[bold]Discovery order[/]  [grey]ORCHESTRA_CONFIG_PATH -> nearest .orchestra/orchestra.json or orchestra.json walking up from the working directory (stopping at a .git root) -> XDG_CONFIG_HOME -> user profile -> defaults[/]");
		if (report.EnvironmentOverrides.Count > 0)
		{
			_console.MarkupLine("[bold]Environment overrides in effect[/]");
			foreach (var (name, value) in report.EnvironmentOverrides)
				_console.MarkupLine($"  {Markup.Escape(name)} = {Markup.Escape(value)}");
		}
	}

	private void RenderSidecar(ConfigReport.SidecarReport sidecar, string fileName)
	{
		if (sidecar.Path is null)
		{
			_console.MarkupLine($"  {Markup.Escape(fileName)}  [grey]none[/]");
		}
		else
		{
			_console.MarkupLine($"  {Markup.Escape(fileName)}  {Markup.Escape(sidecar.Path)}");
		}

		if (sidecar.ShadowedGlobalPath is not null)
			_console.MarkupLine($"    [yellow]ignoring user-global copy[/] {Markup.Escape(sidecar.ShadowedGlobalPath)} (sidecars are read only from the directory of the orchestra.json in effect)");
	}
}

/// <summary>The data behind <c>orchestra config show</c>; serialised as-is for <c>--format json</c>.</summary>
public sealed class ConfigReport
{
	public static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};

	public sealed record SidecarReport(string? Path, string? ShadowedGlobalPath);

	public string? ConfigPath { get; init; }
	public string Source { get; init; } = "None";
	public string SourceDescription { get; init; } = "defaults";
	public string DefaultConfigPath { get; init; } = string.Empty;
	public string? Error { get; init; }
	public SidecarReport McpConfig { get; init; } = new(null, null);
	public SidecarReport ServicesConfig { get; init; } = new(null, null);
	public IReadOnlyList<KeyValuePair<string, string>> Effective { get; init; } = [];
	public IReadOnlyList<KeyValuePair<string, string>> EnvironmentOverrides { get; init; } = [];

	/// <summary>Resolves everything from the current working directory and environment.</summary>
	public static ConfigReport Build()
	{
		var diagnostic = OrchestraConfigLoader.Describe();
		var options = new OrchestrationHostOptions();
		OrchestraConfigLoader.LoadAndApply(options);

		var mcp = OrchestraConfigLoader.ResolveSidecar(OrchestraConfigLoader.McpConfigFileName);
		var services = OrchestraConfigLoader.ResolveSidecar(OrchestraConfigLoader.ServiceConfigFileName);

		var effective = new List<KeyValuePair<string, string>>
		{
			new("dataPath", options.DataPath),
			new("scan.directory", options.Scan?.Directory ?? "(none - orchestrations are registered explicitly)"),
			new("scan.recursive", options.Scan is null ? "-" : options.Scan.Recursive.ToString().ToLowerInvariant()),
			new("scan.watch", options.Scan is null ? "-" : options.Scan.Watch.ToString().ToLowerInvariant()),
			new("hostBaseUrl", options.HostBaseUrl ?? "(unset)"),
			new("defaultModel", options.DefaultModel ?? $"(unset - engine default {Engine.ModelDefaults.Model})"),
			new("defaultReasoningLevel", options.DefaultReasoningLevel?.ToString() ?? "(provider default)"),
			new("defaultMaxConcurrency", options.DefaultMaxConcurrency?.ToString() ?? "(unbounded)"),
			new("enableScheduler", options.EnableScheduler.ToString().ToLowerInvariant()),
			new("logLevel", options.LogLevel),
			new("logging.file", options.FileLogging.Enabled
				? $"{options.FileLogging.ResolveDirectory(options.DataPath)} ({options.FileLogging.MinimumLevel}, {options.FileLogging.RetainedFiles} days)"
				: "off"),
			new("retention", options.Retention.IsForever
				? "keep every run"
				: $"maxRunsPerOrchestration={options.Retention.MaxRunsPerOrchestration?.ToString() ?? "-"}, maxRunAgeDays={options.Retention.MaxRunAgeDays?.ToString() ?? "-"}"),
			new("shutdownTimeoutSeconds", options.ShutdownTimeoutSeconds.ToString()),
		};

		var env = new List<KeyValuePair<string, string>>();
		foreach (var name in new[] { "ORCHESTRA_CONFIG_PATH", "ORCHESTRA_URL", "ORCHESTRA_DATA_PATH", "ORCHESTRA_ORCHESTRATIONS_PATH", "ORCHESTRA_ENABLE_SCHEDULER", "XDG_CONFIG_HOME", "OTEL_EXPORTER_OTLP_ENDPOINT" })
		{
			var value = Environment.GetEnvironmentVariable(name);
			if (!string.IsNullOrEmpty(value))
				env.Add(new(name, value));
		}

		return new ConfigReport
		{
			ConfigPath = diagnostic.Path,
			Source = diagnostic.Source.ToString(),
			SourceDescription = DescribeSource(diagnostic.Source),
			DefaultConfigPath = OrchestraConfigLoader.GetDefaultConfigPath(),
			Error = diagnostic.Parsed ? null : diagnostic.Error ?? "the file could not be parsed",
			McpConfig = new SidecarReport(mcp.EffectivePath, mcp.ShadowedGlobalPath),
			ServicesConfig = new SidecarReport(services.EffectivePath, services.ShadowedGlobalPath),
			Effective = effective,
			EnvironmentOverrides = env,
		};
	}

	internal static string DescribeSource(OrchestraConfigSource source) => source switch
	{
		OrchestraConfigSource.EnvironmentVariable => "from ORCHESTRA_CONFIG_PATH",
		OrchestraConfigSource.Project => "project-local",
		OrchestraConfigSource.XdgConfigHome => "from XDG_CONFIG_HOME",
		OrchestraConfigSource.PlatformDefault => "user-global",
		_ => "defaults",
	};
}
