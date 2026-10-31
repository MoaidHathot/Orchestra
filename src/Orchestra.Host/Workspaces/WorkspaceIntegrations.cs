using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using Orchestra.Engine;
using Orchestra.Host.Hosting;

namespace Orchestra.Host.Workspaces;

public sealed record IntegrationUpdate(string Revision, JsonObject Definition);

/// <summary>Manages the existing MCP sidecar; runtime activation is explicitly restart-scoped.</summary>
public sealed class WorkspaceIntegrations(PortalWorkspaceOptions options)
{
	private readonly SemaphoreSlim _gate = new(1, 1);
	private string PathName => Path.Combine(Path.GetDirectoryName(options.ConfigPath)!, OrchestraConfigLoader.McpConfigFileName);
	private string Read() => File.Exists(PathName) ? File.ReadAllText(PathName) : "{\"mcps\":[]}";
	public object Get()
	{
		var text = Read();
		var json = WorkspaceSettingsStore.ReadObject(text);
		return new { path = PathName, revision = WorkspaceSettingsStore.Revision(text), definitions = Redact(json["mcps"] ?? new JsonArray()), restartRequired = true };
	}
	public async Task<object> SaveAsync(IntegrationUpdate update, CancellationToken token)
	{
		await _gate.WaitAsync(token);
		try
		{
			var text = Read();
			if (WorkspaceSettingsStore.Revision(text) != update.Revision) throw new WorkspaceConflictException("MCP configuration changed. Reload before saving.");
			var json = WorkspaceSettingsStore.ReadObject(text);
			var list = json["mcps"]?.AsArray() ?? new JsonArray();
			var name = update.Definition["name"]?.GetValue<string>();
			if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("MCP name is required.");
			var index = Enumerable.Range(0, list.Count).FirstOrDefault(i => list[i]?["name"]?.GetValue<string>() == name, -1);
			var merged = RestoreRedactions(update.Definition, index >= 0 ? list[index] : null).AsObject();
			var candidate = new JsonObject { ["mcps"] = new JsonArray(merged.DeepClone()) }.ToJsonString();
			var errors = OrchestrationSchemaValidator.ValidateJson(candidate, Path.Combine(options.AssetsDirectory, "schemas", "orchestra.mcp.schema.json"));
			if (errors.Count > 0) throw new ArgumentException(string.Join("\n", errors));
			_ = OrchestrationParser.ParseMcps(candidate);
			if (index >= 0) list[index] = merged; else list.Add(merged);
			await WorkspaceSettingsStore.WriteAtomicAsync(PathName, JsoncEditor.Patch(text, new() { ["mcps"] = list.DeepClone() }), token);
			return Get();
		}
		finally { _gate.Release(); }
	}

	public async Task<object> TestAsync(string name, CancellationToken token)
	{
		using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
		deadline.CancelAfter(TimeSpan.FromSeconds(45));
		var definition = OrchestrationParser.ParseMcpFile(PathName).SingleOrDefault(m => m.Name == name) ?? throw new ArgumentException("MCP not found.");
		IClientTransport transport = definition switch
		{
			LocalMcp local => new StdioClientTransport(new()
			{
				Name = local.Name, Command = local.Command, Arguments = local.Arguments, WorkingDirectory = local.WorkingDirectory,
				EnvironmentVariables = local.Environment?.ToDictionary(p => p.Key, p => (string?)p.Value),
			}),
			RemoteMcp remote => new HttpClientTransport(new() { Name = remote.Name, Endpoint = new Uri(remote.Endpoint), AdditionalHeaders = remote.Headers }),
			_ => throw new ArgumentException("Unsupported MCP transport."),
		};
		await using var client = await McpClient.CreateAsync(transport, cancellationToken: deadline.Token);
		var tools = await client.ListToolsAsync(cancellationToken: deadline.Token);
		return new { connected = true, toolCount = tools.Count, restartRequired = true };
	}

	private static JsonNode Redact(JsonNode node, bool sensitive = false)
	{
		if (node is JsonObject obj)
		{
			var result = new JsonObject();
			foreach (var (key, value) in obj)
				result[key] = value is null ? null : Redact(value, sensitive || key is "environment" or "headers" || IsSensitive(key));
			return result;
		}
		if (node is JsonArray array)
		{
			var result = new JsonArray();
			var previousSensitive = false;
			foreach (var item in array)
			{
				result.Add(item is null ? null : Redact(item, sensitive || previousSensitive));
				previousSensitive = item is JsonValue v && v.TryGetValue<string>(out var text) && IsSensitive(text);
			}
			return result;
		}
		if (node is JsonValue scalar && scalar.TryGetValue<string>(out var content))
		{
			if (content.StartsWith("${") || content.StartsWith("env:")) return node.DeepClone();
			if (sensitive || content.Contains("Bearer ", StringComparison.OrdinalIgnoreCase) || content.StartsWith("ghp_") || content.StartsWith("github_pat_") || content.Contains("--token=")) return JsonValue.Create("[redacted]")!;
		}
		return node.DeepClone();
	}
	private static bool IsSensitive(string key) => new[] { "token", "secret", "password", "authorization", "api-key", "apikey", "api_key" }.Any(k => key.Contains(k, StringComparison.OrdinalIgnoreCase));
	private static JsonNode RestoreRedactions(JsonNode input, JsonNode? original)
	{
		if (input is JsonValue value && value.TryGetValue<string>(out var text) && text == "[redacted]")
			return original?.DeepClone() ?? throw new ArgumentException("Replace redacted placeholders in new connections with environment references.");
		if (input is JsonObject obj)
		{
			var result = original is JsonObject old ? (JsonObject)old.DeepClone() : new JsonObject();
			foreach (var (key, item) in obj) result[key] = item is null ? null : RestoreRedactions(item, (original as JsonObject)?[key]);
			return result;
		}
		if (input is JsonArray array) return new JsonArray(array.Select((n, i) => n is null ? null : RestoreRedactions(n, original is JsonArray old && i < old.Count ? old[i] : null)).ToArray());
		return input.DeepClone();
	}
}
