using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Orchestra.Copilot;
using Orchestra.Engine;
using Orchestra.Host.Hosting;
using Orchestra.Host.Workspaces;
using Orchestra.OpenCode;

namespace Orchestra.Portal;

internal sealed class ProviderSetupBackend(OrchestrationHostOptions options, PortalWorkspaceService workspace,
	IAgentProviderRegistry providers, ILogger<ProviderSetupBackend> logger) : IProviderSetupBackend
{
	private readonly ConcurrentDictionary<string, ProviderReadiness> _last = new();
	public ProviderReadiness[] Inspect() => new[] { "copilot", "opencode" }.Select(provider =>
	{
		if (_last.TryGetValue(provider, out var cached))
			return cached;
		try
		{
			var installed = provider == "copilot" ? CopilotPreflight.Inspect().Available : OpenCodePreflight.Inspect(options.OpenCode.CliPath).Available;
			return new ProviderReadiness(provider, installed, false, installed ? "Ready to check credentials and available models." : "Runtime not installed. Install it here to get started.", [], []);
		}
		catch (Exception ex) { return new ProviderReadiness(provider, false, false, ex.Message, [], []); }
	}).ToArray();

	public async Task<ProviderReadiness> ExecuteAsync(string provider, ProviderOperation operation, Action<SetupProgress> progress,
		Func<CancellationToken, Task<string>> readCode, CancellationToken token)
	{
		_last.TryRemove(provider, out _);
		ProviderReadiness result;
		if (provider == "copilot")
		{
			var probe = CopilotPreflight.Inspect();
			if (!probe.Available && operation.Action == "check")
				return Inspect().Single(p => p.Provider == provider);
			progress(new(probe.Available ? "Checking Copilot…" : "Downloading Copilot CLI (first use, approximately 100 MB)…"));
			var path = await CopilotPreflight.EnsureAsync(logger, token);
			if (operation.Action == "install")
			{
				result = new(provider, true, false, "Copilot runtime installed. Sign in or check your existing credentials.", [], []);
				_last[provider] = result;
				return result;
			}
			if (operation.Action == "login")
				await LoginCopilotAsync(path, progress, token);
			progress(new("Checking Copilot account and available models…"));
			var models = await CopilotPreflight.ListModelsAsync(options.Copilot.GitHubToken, options.Copilot.UseLoggedInUser, token);
			var choices = models.Where(m => !string.Equals(m.PolicyState, "disabled", StringComparison.OrdinalIgnoreCase)).Select(m => new SetupModel(m.Id, m.Name ?? m.Id)).ToArray();
			result = new(provider, true, choices.Length > 0, choices.Length > 0 ? $"Copilot connected: {choices.Length} models available." : "No models available. Sign in with an account that has Copilot access.", choices, []);
		}
		else
		{
			var probe = OpenCodePreflight.Inspect(options.OpenCode.CliPath);
			if (!probe.Available && probe.Source != "PATH")
				throw new InvalidOperationException($"OpenCode was not found at '{probe.Path}' ({probe.Source}). Correct that override before installing or testing a runtime.");
			if (!probe.Available && operation.Action == "check")
				return Inspect().Single(p => p.Provider == provider);
			var path = probe.Available ? probe.Path : await OpenCodeCliInstaller.EnsureAsync(m => progress(new(m)), token);
			progress(new("Starting a private OpenCode V2 setup connection…"));
			await using var session = await OpenCodeSetupSession.StartAsync(path, workspace.Root, logger, token);
			JsonElement integrations = default, modelData = default;
			for (var i = 0; i < 50; i++)
			{
				integrations = await session.RequestAsync(HttpMethod.Get, "api/integration", null, token);
				modelData = await session.RequestAsync(HttpMethod.Get, "api/model", null, token);
				if (integrations.GetArrayLength() > 0)
					break;
				await Task.Delay(200, token);
			}
			if (operation.Action == "login")
				await LoginOpenCodeAsync(session, integrations, operation, progress, readCode, token);
			integrations = await session.RequestAsync(HttpMethod.Get, "api/integration", null, token);
			modelData = await session.RequestAsync(HttpMethod.Get, "api/model", null, token);
			var catalog = integrations.EnumerateArray().Select(i => new SetupIntegration(i.GetProperty("id").GetString()!, i.GetProperty("name").GetString()!,
				i.GetProperty("methods").EnumerateArray().Select(m => new SetupMethod(m.TryGetProperty("id", out var id) ? id.GetString()! : m.GetProperty("type").GetString()!,
					m.GetProperty("type").GetString()!, m.TryGetProperty("label", out var label) ? label.GetString()! : m.GetProperty("type").GetString()!,
					m.TryGetProperty("form", out var form) ? form.Clone() : null)).ToArray(),
				i.GetProperty("connections").EnumerateArray().Any(c => !c.TryGetProperty("status", out var s) || s.ValueKind == JsonValueKind.Null))).ToArray();
			var choices = modelData.EnumerateArray().Where(m => m.GetProperty("enabled").GetBoolean())
				.Select(m => new SetupModel(m.GetProperty("providerID").GetString() + "/" + m.GetProperty("id").GetString(), m.GetProperty("name").GetString()!)).ToArray();
			var connected = catalog.Any(i => i.Connected);
			result = new(provider, true, connected && choices.Length > 0, connected ? $"OpenCode credentials detected; {choices.Length} enabled models. Test your selected model to verify generation." : "Select an OpenCode integration and sign in.", choices, catalog);
		}
		if (operation.Action == "test")
		{
			if (string.IsNullOrWhiteSpace(operation.Model))
				throw new ArgumentException("Choose a model to test.");
			progress(new("Testing the selected model with a short request…"));
			var builder = providers.Resolve(provider);
			var scopeTask = builder.CreateRunScopeAsync(cancellationToken: token);
			await using var scope = await scopeTask;
			var agent = await builder.BuildAgentAsync(new AgentBuildConfig { Model = operation.Model, SystemPrompt = "Reply with READY.", WorkingDirectory = workspace.Root, CapturePlans = false }, token);
			var task = agent.SendAsync("Reply with READY.", token);
			await foreach (var _ in task.WithCancellation(token))
			{ }
			var response = await task.GetResultAsync().WaitAsync(token);
			if (string.IsNullOrWhiteSpace(response.Content))
				throw new InvalidOperationException("The model returned an empty response.");
			result = result with { Ready = true, Detail = $"Model test succeeded: {operation.Model}." };
		}
		_last[provider] = result;
		return result;
	}

	private static async Task LoginCopilotAsync(string path, Action<SetupProgress> progress, CancellationToken token)
	{
		using var process = new Process { StartInfo = new(path) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, CreateNoWindow = true } };
		process.StartInfo.ArgumentList.Add("login");
		process.StartInfo.ArgumentList.Add("--device-code");
		process.StartInfo.Environment["BROWSER"] = "echo";
		process.StartInfo.Environment["GH_BROWSER"] = "echo";
		process.Start();
		process.StandardInput.Close();
		string? url = null, code = null;
		var outputGate = new Lock();
		async Task Drain(StreamReader reader)
		{
			while (await reader.ReadLineAsync(token) is { } line)
			{
				lock (outputGate)
				{
					line = Regex.Replace(line, @"\x1B\[[0-?]*[ -/]*[@-~]", "");
					var link = Regex.Match(line, @"https?://[^\s<>]+", RegexOptions.None, TimeSpan.FromSeconds(1));
					var match = Regex.Match(line, @"\b[A-Z0-9]{4}-[A-Z0-9]{4}\b", RegexOptions.None, TimeSpan.FromSeconds(1));
					if (link.Success)
						url = link.Value.TrimEnd('.', ',', ')');
					if (match.Success)
						code = match.Value;
					progress(new(url is null ? "Waiting for Copilot sign-in instructions…" : "Open the authorization link and enter the code. Waiting for sign-in…", url, code));
				}
			}
		}
		try
		{
			await Task.WhenAll(Drain(process.StandardOutput), Drain(process.StandardError), process.WaitForExitAsync(token));
			if (process.ExitCode != 0)
				throw new InvalidOperationException("Copilot sign-in did not complete. Retry or check the configured credential overrides.");
		}
		finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); } }
	}

	private static async Task LoginOpenCodeAsync(OpenCodeSetupSession session, JsonElement integrations, ProviderOperation operation,
		Action<SetupProgress> progress, Func<CancellationToken, Task<string>> readCode, CancellationToken token)
	{
		var integration = integrations.EnumerateArray().FirstOrDefault(i => i.GetProperty("id").GetString() == operation.IntegrationId);
		if (integration.ValueKind != JsonValueKind.Object)
			throw new ArgumentException("Select an integration from the OpenCode list.");
		var method = integration.GetProperty("methods").EnumerateArray().FirstOrDefault(m =>
			(m.TryGetProperty("id", out var id) ? id.GetString() : m.GetProperty("type").GetString()) == operation.MethodId);
		if (method.ValueKind != JsonValueKind.Object)
			throw new ArgumentException("Select an authentication method.");
		var type = method.GetProperty("type").GetString();
		var route = $"api/integration/{Uri.EscapeDataString(operation.IntegrationId!)}/connect/";
		if (type == "key")
		{
			if (string.IsNullOrWhiteSpace(operation.Key))
				throw new ArgumentException("An API key is required for this integration.");
			await session.RequestAsync(HttpMethod.Post, route + "key", new { key = operation.Key, answer = operation.Answer, label = "Orchestra" }, token);
			return;
		}
		if (type == "external")
		{
			await session.RequestAsync(HttpMethod.Post, route + "external", new { methodID = operation.MethodId, answer = operation.Answer, label = "Orchestra" }, token);
			return;
		}
		if (type != "oauth")
			throw new ArgumentException("Choose an OAuth, key, or external-credential method.");
		var attempt = await session.RequestAsync(HttpMethod.Post, route + "oauth", new { methodID = operation.MethodId, answer = operation.Answer, label = "Orchestra" }, token);
		var attemptRoute = route + "oauth/" + Uri.EscapeDataString(attempt.GetProperty("attemptID").GetString()!);
		try
		{
			var needsCode = attempt.GetProperty("mode").GetString() == "code";
			progress(new(attempt.GetProperty("instructions").GetString()!, attempt.GetProperty("url").GetString(), RequiresCode: needsCode));
			if (needsCode)
				await session.RequestAsync(HttpMethod.Post, attemptRoute + "/complete", new { code = await readCode(token) }, token);
			while (true)
			{
				var status = await session.RequestAsync(HttpMethod.Get, attemptRoute, null, token);
				var state = status.GetProperty("status").GetString();
				if (state == "complete")
					return;
				if (state != "pending")
					throw new InvalidOperationException("OpenCode sign-in " + state + ". Retry the connection.");
				await Task.Delay(1000, token);
			}
		}
		finally
		{
			using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
			try
			{ await session.RequestAsync(HttpMethod.Delete, attemptRoute, null, cleanup.Token); }
			catch (Exception) { }
		}
	}
}
