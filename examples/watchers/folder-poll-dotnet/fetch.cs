// Orchestra watcher fetcher (protocol v1) as a .NET 10 file-based program: `dotnet run fetch.cs`.
// stdin:  { protocolVersion, watcher, cursor, subscribers, config }
// stdout: { protocolVersion, items, cursor }
using System.Text.Json.Nodes;

var stdin = await Console.In.ReadToEndAsync();
var request = JsonNode.Parse(stdin)?.AsObject() ?? new JsonObject();
var folder = request["config"]?["folder"]?.GetValue<string>() ?? "./inbox";

var items = new JsonArray();
if (Directory.Exists(folder))
{
	foreach (var file in Directory.EnumerateFiles(folder, "*.txt").OrderBy(f => f, StringComparer.Ordinal))
	{
		var info = new FileInfo(file);
		// Cast to JsonNode so the non-generic Add overload is chosen; the generic Add<T> emits
		// trimming warnings, and "dotnet run" prints build warnings on stdout, corrupting the JSON.
		items.Add((JsonNode)new JsonObject
		{
			["path"] = info.FullName,
			["name"] = info.Name,
			["size"] = info.Length,
		});
	}
}

// No stream position for a folder listing: echo the cursor back and let dedup do the work.
var response = new JsonObject
{
	["protocolVersion"] = "1",
	["items"] = items,
	["cursor"] = request["cursor"]?.DeepClone(),
};

Console.Out.Write(response.ToJsonString());
return 0;
