using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Orchestra.Exec;

/// <summary>
/// Renders a post-run report from the Orchestra run record returned by
/// <c>GET /api/history/{name}/{runId}</c> — the same data the Portal surfaces. Produces a
/// human-readable text or Markdown digest, or the raw record JSON.
/// </summary>
internal static class RunReport
{
	public static string Render(JsonElement record, ReportFormat format) => format switch
	{
		ReportFormat.Json => JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }),
		ReportFormat.Markdown => RenderMarkdown(record),
		_ => RenderText(record),
	};

	private static string RenderText(JsonElement r)
	{
		var sb = new StringBuilder();
		var name = Str(r, "orchestrationName") ?? "(unknown)";
		var version = Str(r, "version");
		sb.AppendLine("Orchestration Run Report");
		sb.AppendLine("========================");
		sb.AppendLine($"Orchestration : {name}{(version is null ? "" : $" (v{version})")}");
		sb.AppendLine($"Run ID        : {Str(r, "runId")}");
		sb.AppendLine($"Status        : {Str(r, "status")}{Suffix(Str(r, "completionReason"))}");
		AppendIf(sb, "Completed by  : ", Str(r, "completedByStep"));
		sb.AppendLine($"Triggered by  : {Str(r, "triggeredBy")}");
		sb.AppendLine($"Started       : {Str(r, "startedAt")}");
		sb.AppendLine($"Completed     : {Str(r, "completedAt")}");
		sb.AppendLine($"Duration      : {Str(r, "durationSeconds")}s");

		var dataDir = TryGet(r, "context") is { } ctx ? Str(ctx, "dataDirectory") : null;
		AppendIf(sb, "Records dir   : ", dataDir);

		AppendParameters(sb, r, "Parameters    : ");
		AppendUsageLine(sb, TryGet(r, "totalUsage"), "Total usage   : ");

		sb.AppendLine();
		sb.AppendLine("Steps");
		sb.AppendLine("-----");
		if (TryGet(r, "steps") is { ValueKind: JsonValueKind.Array } steps)
		{
			foreach (var step in steps.EnumerateArray())
			{
				var model = Str(step, "actualModel") ?? Str(step, "selectedModel");
				sb.AppendLine($"\u2022 {Str(step, "name")} [{Str(step, "status")}]"
					+ $"{(model is null ? "" : $"  model={model}")}"
					+ $"  {Str(step, "durationSeconds")}s{UsageInline(TryGet(step, "usage"))}");
				var content = Str(step, "content");
				if (!string.IsNullOrWhiteSpace(content))
				{
					foreach (var line in content.Replace("\r\n", "\n").Split('\n'))
					{
						sb.AppendLine($"    {line}");
					}
				}
			}
		}

		AppendSavedFiles(sb, r);

		var final = Str(r, "finalContent");
		if (!string.IsNullOrWhiteSpace(final))
		{
			sb.AppendLine();
			sb.AppendLine("Final output");
			sb.AppendLine("------------");
			sb.AppendLine(final);
		}

		return sb.ToString().TrimEnd() + "\n";
	}

	private static string RenderMarkdown(JsonElement r)
	{
		var sb = new StringBuilder();
		var name = Str(r, "orchestrationName") ?? "(unknown)";
		var version = Str(r, "version");
		sb.AppendLine($"# Run report: {name}{(version is null ? "" : $" (v{version})")}");
		sb.AppendLine();
		sb.AppendLine($"- **Run ID:** `{Str(r, "runId")}`");
		sb.AppendLine($"- **Status:** {Str(r, "status")}{Suffix(Str(r, "completionReason"))}");
		AppendIfMd(sb, "Completed by", Str(r, "completedByStep"));
		sb.AppendLine($"- **Triggered by:** {Str(r, "triggeredBy")}");
		sb.AppendLine($"- **Started:** {Str(r, "startedAt")}");
		sb.AppendLine($"- **Completed:** {Str(r, "completedAt")}");
		sb.AppendLine($"- **Duration:** {Str(r, "durationSeconds")}s");
		var dataDir = TryGet(r, "context") is { } ctx ? Str(ctx, "dataDirectory") : null;
		AppendIfMd(sb, "Records dir", dataDir is null ? null : $"`{dataDir}`");

		if (TryGet(r, "totalUsage") is { } usage)
		{
			sb.AppendLine($"- **Total tokens:** in {Num(usage, "inputTokens")} / out {Num(usage, "outputTokens")} / total {Num(usage, "totalTokens")}{CostSuffix(usage)}");
		}

		sb.AppendLine();
		sb.AppendLine("## Steps");
		sb.AppendLine();
		sb.AppendLine("| Step | Status | Model | Duration | Tokens |");
		sb.AppendLine("| --- | --- | --- | --- | --- |");
		if (TryGet(r, "steps") is { ValueKind: JsonValueKind.Array } steps)
		{
			foreach (var step in steps.EnumerateArray())
			{
				var model = Str(step, "actualModel") ?? Str(step, "selectedModel") ?? "";
				var tokens = TryGet(step, "usage") is { } u ? (Num(u, "totalTokens") ?? "") : "";
				sb.AppendLine($"| {Md(Str(step, "name"))} | {Str(step, "status")} | {Md(model)} | {Str(step, "durationSeconds")}s | {tokens} |");
			}

			// Per-step content blocks.
			foreach (var step in steps.EnumerateArray())
			{
				var content = Str(step, "content");
				if (!string.IsNullOrWhiteSpace(content))
				{
					sb.AppendLine();
					sb.AppendLine($"### {Str(step, "name")}");
					sb.AppendLine();
					sb.AppendLine("```");
					sb.AppendLine(content);
					sb.AppendLine("```");
				}
			}
		}

		var final = Str(r, "finalContent");
		if (!string.IsNullOrWhiteSpace(final))
		{
			sb.AppendLine();
			sb.AppendLine("## Final output");
			sb.AppendLine();
			sb.AppendLine("```");
			sb.AppendLine(final);
			sb.AppendLine("```");
		}

		return sb.ToString().TrimEnd() + "\n";
	}

	// ── helpers ──

	private static void AppendParameters(StringBuilder sb, JsonElement r, string label)
	{
		if (TryGet(r, "parameters") is { ValueKind: JsonValueKind.Object } p)
		{
			var pairs = p.EnumerateObject().Select(kv => $"{kv.Name}={kv.Value}").ToArray();
			if (pairs.Length > 0)
			{
				sb.AppendLine(label + string.Join(", ", pairs));
			}
		}
	}

	private static void AppendUsageLine(StringBuilder sb, JsonElement? usage, string label)
	{
		if (usage is { } u)
		{
			sb.AppendLine($"{label}in {Num(u, "inputTokens")} / out {Num(u, "outputTokens")} / total {Num(u, "totalTokens")}{CostSuffix(u)}");
		}
	}

	private static string UsageInline(JsonElement? usage)
		=> usage is { } u && Num(u, "totalTokens") is { } t ? $"  tokens={t}" : string.Empty;

	private static string CostSuffix(JsonElement usage)
		=> usage.TryGetProperty("cost", out var c) && c.ValueKind == JsonValueKind.Number && c.GetDouble() > 0
			? $", cost ${c.GetDouble().ToString("0.####", CultureInfo.InvariantCulture)}"
			: string.Empty;

	private static void AppendSavedFiles(StringBuilder sb, JsonElement r)
	{
		if (TryGet(r, "savedFiles") is { ValueKind: JsonValueKind.Array } files && files.GetArrayLength() > 0)
		{
			sb.AppendLine();
			sb.AppendLine("Saved files");
			sb.AppendLine("-----------");
			foreach (var f in files.EnumerateArray())
			{
				sb.AppendLine($"\u2022 {(f.ValueKind == JsonValueKind.String ? f.GetString() : f.ToString())}");
			}
		}
	}

	private static void AppendIf(StringBuilder sb, string label, string? value)
	{
		if (!string.IsNullOrWhiteSpace(value)) sb.AppendLine(label + value);
	}

	private static void AppendIfMd(StringBuilder sb, string label, string? value)
	{
		if (!string.IsNullOrWhiteSpace(value)) sb.AppendLine($"- **{label}:** {value}");
	}

	private static string Suffix(string? reason) => string.IsNullOrWhiteSpace(reason) ? "" : $" ({reason})";

	private static JsonElement? TryGet(JsonElement el, string name)
		=> el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
			? v
			: null;

	private static string? Str(JsonElement el, string name)
		=> el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

	private static string? Num(JsonElement el, string name)
		=> el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.ToString() : null;

	private static string Md(string? s) => (s ?? string.Empty).Replace("|", "\\|");
}
