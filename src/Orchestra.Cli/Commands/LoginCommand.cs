using System.ComponentModel;
using System.Diagnostics;
using Orchestra.Copilot;
using Orchestra.Host.Hosting;
using Orchestra.OpenCode;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Orchestra.Cli.Commands;

/// <summary>
/// Settings for <see cref="LoginCommand"/>.
/// </summary>
public sealed class LoginSettings : CommandSettings
{
	[CommandOption("-p|--provider <PROVIDER>")]
	[Description("Provider to sign in to: copilot | opencode. Defaults to the configured provider.")]
	public string? Provider { get; set; }

	[CommandOption("--device-code")]
	[Description("Copilot only: force the OAuth device-code flow (for SSH, containers, headless machines).")]
	public bool DeviceCode { get; set; }

	public override ValidationResult Validate()
	{
		if (Provider is not null
			&& !InitSettings.KnownProviders.Contains(Provider.Trim(), StringComparer.OrdinalIgnoreCase))
		{
			return ValidationResult.Error(
				$"Invalid --provider '{Provider}' (expected {string.Join(" or ", InitSettings.KnownProviders)}).");
		}

		return ValidationResult.Success();
	}
}

/// <summary>
/// <c>orchestra login</c> - sign in to the agent provider by running its own login flow.
/// </summary>
/// <remarks>
/// <para>
/// Orchestra has no credentials of its own; each provider's CLI owns authentication. The
/// Copilot CLI is downloaded into a cache directory that is not on <c>PATH</c>, so telling a
/// user to "run <c>copilot login</c>" leaves them hunting for a binary. This verb resolves the
/// path (downloading the CLI first if needed) and hands the terminal to the provider's login
/// command with stdio inherited, so browser prompts and device codes work exactly as they would
/// when invoking the CLI directly.
/// </para>
/// <para>
/// For unattended use, set <c>GH_TOKEN</c> / <c>GITHUB_TOKEN</c> (Copilot) instead of logging
/// in interactively; both providers honour their own token variables.
/// </para>
/// </remarks>
public sealed class LoginCommand : AsyncCommand<LoginSettings>
{
	private readonly IAnsiConsole _console;

	public LoginCommand(IAnsiConsole console) => _console = console;

	public override async Task<int> ExecuteAsync(CommandContext context, LoginSettings settings)
	{
		ArgumentNullException.ThrowIfNull(settings);

		var provider = ResolveProvider(settings.Provider);

		return provider switch
		{
			"opencode" => await LoginOpenCodeAsync().ConfigureAwait(false),
			_ => await LoginCopilotAsync(settings.DeviceCode).ConfigureAwait(false),
		};
	}

	/// <summary>Explicit flag, else <c>orchestra.json</c>'s provider, else <c>copilot</c>.</summary>
	private static string ResolveProvider(string? flag)
	{
		if (!string.IsNullOrWhiteSpace(flag))
			return flag.Trim().ToLowerInvariant();

		try
		{
			var config = OrchestraConfigLoader.Load();
			var configured = config?.Provider ?? config?.DefaultProvider;
			if (!string.IsNullOrWhiteSpace(configured))
				return configured.Trim().ToLowerInvariant();
		}
		catch
		{
			// A broken orchestra.json must not stop the user from signing in; fall through.
		}

		return "copilot";
	}

	private async Task<int> LoginCopilotAsync(bool deviceCode)
	{
		var probe = CopilotPreflight.Inspect();
		if (probe.Error is not null)
		{
			_console.MarkupLine($"[red]Error:[/] {Markup.Escape(probe.Error)}");
			return 1;
		}

		string cliPath;
		if (probe.Available)
		{
			cliPath = probe.Path!;
		}
		else
		{
			// Same one-time download the first run would do, but announced rather than looking
			// like a hang.
			_console.MarkupLine($"[dim]Copilot CLI {Markup.Escape(probe.Version)} is not installed yet; downloading it first (~100 MB, once per machine).[/]");
			try
			{
				cliPath = await CopilotPreflight.EnsureAsync().ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				_console.MarkupLine($"[red]Error:[/] could not download the Copilot CLI: {Markup.Escape(ex.Message)}");
				_console.MarkupLine($"[dim]Set {Markup.Escape(CopilotPreflight.ExplicitCliPathEnvVar)} to a pre-installed binary, or {Markup.Escape(CopilotPreflight.NpmRegistryEnvVar)} to an internal npm mirror.[/]");
				return 1;
			}
		}

		var args = new List<string> { "login" };
		if (deviceCode)
			args.Add("--device-code");

		_console.MarkupLine($"[dim]Running: {Markup.Escape(cliPath)} {Markup.Escape(string.Join(' ', args))}[/]");
		_console.WriteLine();

		var exit = await RunInheritingTerminalAsync(cliPath, args).ConfigureAwait(false);

		if (exit == 0)
		{
			_console.WriteLine();
			_console.MarkupLine("[green]Signed in.[/] Verify with:");
			_console.MarkupLine($"  {Markup.Escape(InvocationStyle.Format("doctor"))}");
		}

		return exit;
	}

	private async Task<int> LoginOpenCodeAsync()
	{
		string? configuredPath = null;
		try
		{
			configuredPath = OrchestraConfigLoader.Load()?.OpenCode?.CliPath;
		}
		catch
		{
			// Fall back to PATH resolution below.
		}

		var probe = OpenCodePreflight.Inspect(configuredPath);
		if (!probe.Available)
		{
			_console.MarkupLine("[red]Error:[/] OpenCode is not installed, or is not on PATH.");
			_console.MarkupLine("[dim]Install OpenCode, or set `opencode.cliPath` in orchestra.json / $ORCHESTRA_OPENCODE_PATH.[/]");
			return 1;
		}

		_console.MarkupLine($"[dim]Running: {Markup.Escape(probe.Path)} auth login[/]");
		_console.WriteLine();

		return await RunInheritingTerminalAsync(probe.Path, ["auth", "login"]).ConfigureAwait(false);
	}

	/// <summary>
	/// Runs a child process with the current terminal attached, so interactive prompts and
	/// browser hand-offs behave as if the user had invoked it directly.
	/// </summary>
	private static async Task<int> RunInheritingTerminalAsync(string fileName, IReadOnlyList<string> arguments)
	{
		var startInfo = new ProcessStartInfo
		{
			FileName = fileName,
			UseShellExecute = false,
			RedirectStandardInput = false,
			RedirectStandardOutput = false,
			RedirectStandardError = false,
		};

		foreach (var arg in arguments)
			startInfo.ArgumentList.Add(arg);

		// .cmd / .ps1 shims (npm installs on Windows) need a shell host; a bare exe does not.
		if (OperatingSystem.IsWindows()
			&& (fileName.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
				|| fileName.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)))
		{
			startInfo.FileName = "cmd.exe";
			startInfo.ArgumentList.Clear();
			startInfo.ArgumentList.Add("/c");
			startInfo.ArgumentList.Add(fileName);
			foreach (var arg in arguments)
				startInfo.ArgumentList.Add(arg);
		}
		else if (OperatingSystem.IsWindows() && fileName.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
		{
			startInfo.FileName = "pwsh";
			startInfo.ArgumentList.Clear();
			startInfo.ArgumentList.Add("-NoLogo");
			startInfo.ArgumentList.Add("-File");
			startInfo.ArgumentList.Add(fileName);
			foreach (var arg in arguments)
				startInfo.ArgumentList.Add(arg);
		}

		using var process = Process.Start(startInfo)
			?? throw new InvalidOperationException($"Failed to start '{fileName}'.");

		await process.WaitForExitAsync().ConfigureAwait(false);
		return process.ExitCode;
	}
}
