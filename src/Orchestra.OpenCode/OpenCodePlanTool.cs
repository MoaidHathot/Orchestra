using System.Text.Json;
using System.Threading.Channels;
using Orchestra.Engine;

namespace Orchestra.OpenCode;

/// <summary>Explicit shared-step plan publisher for V2 runtimes without native todos.</summary>
internal sealed class OpenCodePlanTool(ChannelWriter<AgentEvent> writer) : IEngineTool
{
	private string? _sessionId;
	private string? _markdown;
	private readonly object _gate = new();
	public string Name => "orchestra_update_plan";
	public string Instructions => $"Plan tracking is available through the directly exposed MCP tool `{OpenCodeEngineToolBridge.ServerName}_{Name}`. " +
		"Use that exact tool name through the regular tool interface to publish and update a task list when a plan is useful or requested. " +
		"It is outside the Code Mode search catalog, so searching that catalog does not determine its availability. " +
		"Send the complete task list on each update. Simple tasks do not require a plan. Plan progress does not complete the orchestration step.";
	public string Description => "Record or update your work plan for this orchestration step. For multi-step work, publish the task list and update statuses as work progresses. Replace the complete task list each call. Optionally publish a Markdown plan document. This reports progress only; it does not complete the step. Simple tasks do not require a plan.";
	public string ParametersSchema => """
		{"type":"object","properties":{"tasks":{"type":"array","items":{"type":"object","properties":{"id":{"type":"string"},"title":{"type":"string"},"description":{"type":"string"},"status":{"type":"string","enum":["pending","in_progress","completed","blocked","cancelled","failed"]}},"required":["title","status"]}},"markdown":{"type":["string","null"]}},"required":["tasks"]}
		""";

	public void Bind(string sessionId, bool resumed)
	{
		lock (_gate)
		{
			if (_sessionId != sessionId)
				_markdown = null;
			_sessionId = sessionId;
		}
	}

	public string Execute(string arguments, EngineToolContext context)
	{
		try
		{
			using var doc = JsonDocument.Parse(arguments);
			var tasks = ParseTasks(doc.RootElement.GetProperty("tasks"));
			if (tasks.Any(t => t.RawStatus is not ("pending" or "in_progress" or "completed" or "blocked" or "cancelled" or "failed")))
				throw new FormatException("Every task requires a supported status: pending, in_progress, completed, blocked, cancelled, or failed.");
			lock (_gate)
			{
				if (doc.RootElement.TryGetProperty("markdown", out var markdown))
					_markdown = markdown.ValueKind == JsonValueKind.Null ? null : markdown.GetString();
				Publish(tasks);
			}
			return "Plan recorded. Continue the task and update the plan as progress changes.";
		}
		catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
		{ return "Invalid plan: " + ex.Message; }
	}

	private void Publish(IReadOnlyList<AgentPlanTask> tasks)
	{
		if (_sessionId is null)
			return;
		writer.TryWrite(new()
		{
			Type = AgentEventType.PlanUpdated,
			Plan = new()
			{
				Provider = "opencode",
				SessionId = _sessionId,
				Source = "orchestra-plan-tool",
				Shared = true,
				Title = "Shared step plan",
				Tasks = tasks,
				Markdown = _markdown,
			}
		});
	}

	internal static AgentPlanTask[] ParseTasks(JsonElement values)
	{
		if (values.ValueKind != JsonValueKind.Array)
			throw new FormatException("tasks must be an array.");
		return values.EnumerateArray().Select(task =>
		{
			var title = Text(task, "title") ?? Text(task, "content");
			if (string.IsNullOrWhiteSpace(title))
				throw new FormatException("Every task requires a title.");
			var status = Text(task, "status");
			return new AgentPlanTask
			{
				Id = Text(task, "id"),
				Title = title,
				Description = Text(task, "description"),
				RawStatus = status,
				Status = AgentPlanTask.NormalizeStatus(status),
				Priority = Text(task, "priority")
			};
		}).ToArray();
	}
	private static string? Text(JsonElement obj, string key) => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
