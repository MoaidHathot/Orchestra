using System.Text.Json;
using Spectre.Console;

namespace Orchestra.Cli;

public class Program
{
	private static readonly JsonSerializerOptions s_jsonOptions = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
	};

	public static async Task<int> Main(string[] args)
	{
		if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
		{
			PrintHelp();
			return 0;
		}

		var serverUrl = Environment.GetEnvironmentVariable("ORCHESTRA_URL") ?? "http://localhost:5000";

		// Parse --server flag
		var argsList = args.ToList();
		for (var i = 0; i < argsList.Count - 1; i++)
		{
			if (argsList[i] is "--server" or "-s")
			{
				serverUrl = argsList[i + 1];
				argsList.RemoveAt(i + 1);
				argsList.RemoveAt(i);
				break;
			}
		}
		args = argsList.ToArray();

		using var client = new OrchestraClient(serverUrl);

		try
		{
			var result = args[0] switch
			{
				"list" => await client.ListOrchestrationsAsync(),
				"get" => await RunWithArg(args, 1, "orchestration ID", id => client.GetOrchestrationAsync(id)),
				"register" => await RunWithArg(args, 1, "file path", path =>
				{
					var mcpPath = GetFlag(args, "--mcp");
					return client.RegisterOrchestrationAsync(path, mcpPath);
				}),
				"remove" => await RunWithArg(args, 1, "orchestration ID", id => client.RemoveOrchestrationAsync(id)),
				"scan" => await RunWithArg(args, 1, "directory", dir => client.ScanDirectoryAsync(dir)),
				"enable" => await RunWithArg(args, 1, "orchestration ID", id => client.EnableOrchestrationAsync(id)),
				"disable" => await RunWithArg(args, 1, "orchestration ID", id => client.DisableOrchestrationAsync(id)),

				"run" => await RunWithArg(args, 1, "orchestration ID", id =>
				{
					var parameters = ParseParams(args);
					return client.RunOrchestrationAsync(id, parameters);
				}),
				"active" => await client.GetActiveExecutionsAsync(),
				"cancel" => await RunWithArg(args, 1, "execution ID", id => client.CancelExecutionAsync(id)),

				"runs" => await HandleRunsCommand(args, client),
				"triggers" => await HandleTriggersCommand(args, client),
				"profiles" => await HandleProfilesCommand(args, client),
				"tags" => await HandleTagsCommand(args, client),

				"server-status" => await client.GetStatusAsync(),

				_ => throw new ArgumentException($"Unknown command: {args[0]}"),
			};

			var json = JsonSerializer.Serialize(result, s_jsonOptions);

			if (HasFlag(args, "--format", "table"))
			{
				PrintAsTable(result, args[0]);
			}
			else
			{
				Console.WriteLine(json);
			}

			return 0;
		}
		catch (HttpRequestException ex)
		{
			AnsiConsole.MarkupLine($"[red]Error:[/] Cannot connect to Orchestra server at {serverUrl}");
			AnsiConsole.MarkupLine($"[dim]{Markup.Escape(ex.Message)}[/]");
			return 1;
		}
		catch (ArgumentException ex)
		{
			AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
			return 1;
		}
		catch (Exception ex)
		{
			AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
			return 1;
		}
	}

	private static async Task<JsonElement> HandleRunsCommand(string[] args, OrchestraClient client)
	{
		if (args.Length < 2) return await client.ListRunsAsync(20);

		return args[1] switch
		{
			"list" => await client.ListRunsAsync(GetIntFlag(args, "--limit") ?? 20),
			"get" when args.Length >= 4 => await client.GetRunAsync(args[2], args[3]),
			"delete" when args.Length >= 4 => await client.DeleteRunAsync(args[2], args[3]),
			_ => await client.ListRunsAsync(20),
		};
	}

	private static async Task<JsonElement> HandleTriggersCommand(string[] args, OrchestraClient client)
	{
		if (args.Length < 2) return await client.ListTriggersAsync();

		return args[1] switch
		{
			"list" => await client.ListTriggersAsync(),
			"enable" when args.Length >= 3 => await client.EnableTriggerAsync(args[2]),
			"disable" when args.Length >= 3 => await client.DisableTriggerAsync(args[2]),
			"fire" when args.Length >= 3 => await client.FireTriggerAsync(args[2], ParseParams(args)),
			_ => throw new ArgumentException($"Unknown triggers subcommand: {args[1]}. Use: list, enable, disable, fire"),
		};
	}

	private static async Task<JsonElement> HandleProfilesCommand(string[] args, OrchestraClient client)
	{
		if (args.Length < 2) return await client.ListProfilesAsync();

		return args[1] switch
		{
			"list" => await client.ListProfilesAsync(),
			"get" when args.Length >= 3 => await client.GetProfileAsync(args[2]),
			"activate" when args.Length >= 3 => await client.ActivateProfileAsync(args[2]),
			"deactivate" when args.Length >= 3 => await client.DeactivateProfileAsync(args[2]),
			"delete" when args.Length >= 3 => await client.DeleteProfileAsync(args[2]),
			_ => throw new ArgumentException($"Unknown profiles subcommand: {args[1]}. Use: list, get, activate, deactivate, delete"),
		};
	}

	private static async Task<JsonElement> HandleTagsCommand(string[] args, OrchestraClient client)
	{
		if (args.Length < 2) return await client.ListTagsAsync();

		return args[1] switch
		{
			"list" => await client.ListTagsAsync(),
			"get" when args.Length >= 3 => await client.GetOrchestrationTagsAsync(args[2]),
			"add" when args.Length >= 4 => await client.AddTagsAsync(args[2],
				args[3].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
			"remove" when args.Length >= 4 => await client.RemoveTagAsync(args[2], args[3]),
			_ => throw new ArgumentException($"Unknown tags subcommand: {args[1]}. Use: list, get, add, remove"),
		};
	}

	private static async Task<JsonElement> RunWithArg(string[] args, int index, string argName, Func<string, Task<JsonElement>> action)
	{
		if (args.Length <= index)
			throw new ArgumentException($"Missing required argument: <{argName}>");
		return await action(args[index]);
	}

	private static Dictionary<string, string>? ParseParams(string[] args)
	{
		var parameters = new Dictionary<string, string>();
		for (var i = 0; i < args.Length; i++)
		{
			if (args[i] == "--param" && i + 1 < args.Length)
			{
				var parts = args[i + 1].Split('=', 2);
				if (parts.Length == 2)
					parameters[parts[0]] = parts[1];
				i++;
			}
		}
		return parameters.Count > 0 ? parameters : null;
	}

	private static string? GetFlag(string[] args, string flag)
	{
		for (var i = 0; i < args.Length - 1; i++)
			if (args[i] == flag) return args[i + 1];
		return null;
	}

	private static int? GetIntFlag(string[] args, string flag)
	{
		var value = GetFlag(args, flag);
		return value is not null && int.TryParse(value, out var result) ? result : null;
	}

	private static bool HasFlag(string[] args, string flag, string value)
	{
		for (var i = 0; i < args.Length - 1; i++)
			if (args[i] == flag && args[i + 1].Equals(value, StringComparison.OrdinalIgnoreCase))
				return true;
		return false;
	}

	private static void PrintAsTable(JsonElement result, string command)
	{
		var table = new Table();
		table.Border(TableBorder.Rounded);

		JsonElement? arrayToRender = null;

		if (result.ValueKind == JsonValueKind.Array)
			arrayToRender = result;
		else if (result.TryGetProperty("orchestrations", out var o) && o.ValueKind == JsonValueKind.Array)
			arrayToRender = o;
		else if (result.TryGetProperty("runs", out var r) && r.ValueKind == JsonValueKind.Array)
			arrayToRender = r;
		else if (result.TryGetProperty("triggers", out var t) && t.ValueKind == JsonValueKind.Array)
			arrayToRender = t;
		else if (result.TryGetProperty("profiles", out var p) && p.ValueKind == JsonValueKind.Array)
			arrayToRender = p;

		if (arrayToRender.HasValue)
		{
			RenderArrayAsTable(arrayToRender.Value, table);
		}
		else
		{
			table.AddColumn("Property");
			table.AddColumn("Value");
			foreach (var prop in result.EnumerateObject())
			{
				table.AddRow(
					Markup.Escape(prop.Name),
					Markup.Escape(FormatValue(prop.Value)));
			}
		}

		AnsiConsole.Write(table);
	}

	private static void RenderArrayAsTable(JsonElement array, Table table)
	{
		var items = array.EnumerateArray().ToList();
		if (items.Count == 0)
		{
			table.AddColumn("(empty)");
			return;
		}

		var columns = items[0].EnumerateObject().Select(p => p.Name).ToList();
		foreach (var col in columns)
			table.AddColumn(Markup.Escape(col));

		foreach (var item in items)
		{
			var values = columns.Select(col =>
			{
				if (item.TryGetProperty(col, out var value))
					return Markup.Escape(FormatValue(value));
				return "";
			}).ToArray();
			table.AddRow(values);
		}
	}

	private static string FormatValue(JsonElement value) => value.ValueKind switch
	{
		JsonValueKind.Array => $"[{value.GetArrayLength()} items]",
		JsonValueKind.Object => "{...}",
		JsonValueKind.Null => "",
		_ => value.ToString() ?? "",
	};

	private static void PrintHelp()
	{
		Console.WriteLine("Orchestra CLI");
		Console.WriteLine("A command-line interface for managing Orchestra orchestrations.");
		Console.WriteLine();
		Console.WriteLine("Usage: orchestra <command> [args] [options]");
		Console.WriteLine();
		Console.WriteLine("Global Options:");
		Console.WriteLine("  --server, -s <url>    Server URL (default: ORCHESTRA_URL env var or http://localhost:5000)");
		Console.WriteLine("  --format table        Output as table instead of JSON");
		Console.WriteLine();
		Console.WriteLine("Orchestrations:");
		Console.WriteLine("  list                          List all orchestrations");
		Console.WriteLine("  get <id>                      Get orchestration details");
		Console.WriteLine("  register <path> [--mcp path]  Register from file");
		Console.WriteLine("  remove <id>                   Remove an orchestration");
		Console.WriteLine("  scan <directory>              Scan directory for orchestrations");
		Console.WriteLine("  enable <id>                   Enable orchestration trigger");
		Console.WriteLine("  disable <id>                  Disable orchestration trigger");
		Console.WriteLine();
		Console.WriteLine("Execution:");
		Console.WriteLine("  run <id> [--param k=v ...]    Execute an orchestration");
		Console.WriteLine("  active                        List active executions");
		Console.WriteLine("  cancel <execution-id>         Cancel a running execution");
		Console.WriteLine();
		Console.WriteLine("Run History:");
		Console.WriteLine("  runs [list] [--limit N]             List recent runs");
		Console.WriteLine("  runs get <name> <run-id>            Get run details");
		Console.WriteLine("  runs delete <name> <run-id>         Delete a run");
		Console.WriteLine();
		Console.WriteLine("Triggers:");
		Console.WriteLine("  triggers [list]                     List all triggers");
		Console.WriteLine("  triggers enable <id>                Enable a trigger");
		Console.WriteLine("  triggers disable <id>               Disable a trigger");
		Console.WriteLine("  triggers fire <id> [--param k=v]    Fire a trigger");
		Console.WriteLine();
		Console.WriteLine("Profiles:");
		Console.WriteLine("  profiles [list]                     List all profiles");
		Console.WriteLine("  profiles get <id>                   Get profile details");
		Console.WriteLine("  profiles activate <id>              Activate a profile");
		Console.WriteLine("  profiles deactivate <id>            Deactivate a profile");
		Console.WriteLine("  profiles delete <id>                Delete a profile");
		Console.WriteLine();
		Console.WriteLine("Tags:");
		Console.WriteLine("  tags [list]                         List all tags with counts");
		Console.WriteLine("  tags get <id>                       Get tags for an orchestration");
		Console.WriteLine("  tags add <id> <tag1,tag2,...>        Add tags to an orchestration");
		Console.WriteLine("  tags remove <id> <tag>              Remove a tag");
		Console.WriteLine();
		Console.WriteLine("Server:");
		Console.WriteLine("  server-status                       Get server status");
	}
}
