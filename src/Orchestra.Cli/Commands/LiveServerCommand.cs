using Orchestra.Client;
using Spectre.Console;

namespace Orchestra.Cli.Commands;

/// <summary>
/// Execution shell for the live-runtime Group-B verbs (active / cancel / server-status / pending /
/// respond / triggers fire) that act on a <em>running</em> server's in-memory state and so cannot
/// be served by a throwaway spawned host. Resolves the same server URL the rest of the CLI uses and
/// runs the action; when the connection itself fails (nothing listening), it replaces the raw
/// "connection refused" with a clear, actionable message that names the resolved URL and points at
/// <c>orchestra portal</c>. Genuine HTTP errors (e.g. 404 for an unknown run) keep their descriptive
/// message — only connection-level failures (<see cref="HttpRequestException.StatusCode"/> is null)
/// are reinterpreted.
/// </summary>
internal static class LiveServerCommand
{
	public static async Task<int> RunAsync(GlobalSettings settings, string verb, Func<OrchestraClient, Task<int>> action)
	{
		var url = ClientFactory.ResolveServerUrl(settings.Server);
		using var client = new OrchestraClient(url);
		try
		{
			return await action(client);
		}
		catch (HttpRequestException ex) when (ex.StatusCode is null)
		{
			AnsiConsole.MarkupLine(
				$"[red]Error:[/] couldn't reach a running Orchestra server at {Markup.Escape(url)}. " +
				$"`{Markup.Escape(verb)}` acts on live runtime state, so it needs a running server — start one " +
				"with `orchestra portal`, or point at it with --server <url> or $ORCHESTRA_URL.");
			return 1;
		}
	}

	/// <summary>Convenience overload for the common "do work, write output, exit 0" shape.</summary>
	public static Task<int> RunAsync(GlobalSettings settings, string verb, Func<OrchestraClient, Task> action)
		=> RunAsync(settings, verb, async client =>
		{
			await action(client);
			return 0;
		});
}
