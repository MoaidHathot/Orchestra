using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Orchestra.Engine;
using Orchestra.Host.Persistence;
using Orchestra.Host.Profiles;
using Orchestra.Host.Registry;
using Orchestra.Host.Triggers;

namespace Orchestra.Host.McpServer;

/// <summary>
/// MCP tools for the Orchestra control plane.
/// Provides management capabilities: orchestration CRUD, tag management,
/// profile management, trigger management, and run history.
/// Disabled by default — opt-in via <see cref="McpServerOptions.ControlPlaneEnabled"/>.
/// </summary>
[McpServerToolType]
public sealed class ControlPlaneTools
{
	// ── Orchestration Management ──

	[McpServerTool, Description(
		"Gets the full details of a registered orchestration by its ID. " +
		"Returns name, description, version, steps, parameters, inputs, tags, and trigger configuration.")]
	public static string GetOrchestrationDetails(
		OrchestrationRegistry registry,
		OrchestrationTagStore tagStore,
		[Description("The orchestration ID.")] string orchestrationId)
	{
		var entry = registry.Get(orchestrationId);
		if (entry is null)
			return Error($"Orchestration '{orchestrationId}' not found.");

		var o = entry.Orchestration;
		var parameterNames = o.Steps.SelectMany(s => s.Parameters).Distinct().ToArray();

		return Json(new
		{
			id = entry.Id,
			path = entry.Path,
			mcpPath = entry.McpPath,
			name = o.Name,
			description = o.Description,
			version = o.Version,
			tags = tagStore.GetEffectiveTags(entry.Id, o.Tags),
			parameters = parameterNames,
			inputs = o.Inputs?.ToDictionary(
				kvp => kvp.Key,
				kvp => new
				{
					type = kvp.Value.Type.ToString().ToLowerInvariant(),
					description = kvp.Value.Description,
					required = kvp.Value.Required,
					@default = kvp.Value.Default,
					@enum = kvp.Value.Enum.Length > 0 ? kvp.Value.Enum : null,
				}),
			stepCount = o.Steps.Length,
			steps = o.Steps.Select(s => new
			{
				name = s.Name,
				type = s.Type.ToString(),
				dependsOn = s.DependsOn,
				parameters = s.Parameters,
				enabled = s.Enabled,
			}).ToArray(),
			trigger = new
			{
				type = o.Trigger.Type.ToString().ToLowerInvariant(),
				enabled = o.Trigger.Enabled,
			},
			registeredAt = entry.RegisteredAt,
			contentHash = entry.ContentHash,
		});
	}

	[McpServerTool, Description(
		"Registers an orchestration from a file path. " +
		"The file must be a valid orchestration JSON file.")]
	public static string RegisterOrchestration(
		OrchestrationRegistry registry,
		TriggerManager triggerManager,
		[Description("Absolute path to the orchestration JSON file.")] string path,
		[Description("Optional path to an MCP configuration JSON file.")] string? mcpPath = null)
	{
		if (!File.Exists(path))
			return Error($"File not found: {path}");

		try
		{
			var entry = registry.Register(path, mcpPath);

			// Register trigger if enabled
			if (entry.Orchestration.Trigger.Enabled)
			{
				triggerManager.RegisterTrigger(
					entry.Path, entry.McpPath, entry.Orchestration.Trigger,
					null, TriggerSource.Json, entry.Id, entry.Orchestration);
			}

			return Json(new
			{
				id = entry.Id,
				name = entry.Orchestration.Name,
				status = "registered",
				triggerEnabled = entry.Orchestration.Trigger.Enabled,
			});
		}
		catch (Exception ex)
		{
			return Error($"Failed to register orchestration: {ex.Message}");
		}
	}

	[McpServerTool, Description(
		"Removes a registered orchestration by its ID. " +
		"Also removes any associated triggers.")]
	public static string RemoveOrchestration(
		OrchestrationRegistry registry,
		TriggerManager triggerManager,
		[Description("The orchestration ID to remove.")] string orchestrationId)
	{
		var entry = registry.Get(orchestrationId);
		if (entry is null)
			return Error($"Orchestration '{orchestrationId}' not found.");

		triggerManager.RemoveTrigger(orchestrationId);
		registry.Remove(orchestrationId);

		return Json(new
		{
			orchestrationId,
			name = entry.Orchestration.Name,
			status = "removed",
		});
	}

	[McpServerTool, Description(
		"Scans a directory for orchestration JSON files and returns metadata. " +
		"Does not register them — use register_orchestration for that.")]
	public static string ScanDirectory(
		[Description("Absolute path to the directory to scan.")] string directory)
	{
		if (!Directory.Exists(directory))
			return Error($"Directory not found: {directory}");

		var files = Directory.GetFiles(directory, "*.json");
		var results = new List<object>();

		foreach (var file in files)
		{
			try
			{
				var metadata = OrchestrationParser.ParseOrchestrationFileMetadataOnly(file);
				results.Add(new
				{
					path = file,
					name = metadata.Name,
					description = metadata.Description,
					version = metadata.Version,
					stepCount = metadata.Steps.Length,
				});
			}
			catch
			{
				// Not a valid orchestration file — skip
			}
		}

		return Json(new { directory, count = results.Count, orchestrations = results });
	}

	// ── Tag Management ──

	[McpServerTool, Description(
		"Lists all tags in use across all orchestrations with their counts.")]
	public static string ListTags(
		OrchestrationTagStore tagStore,
		OrchestrationRegistry registry)
	{
		var orchestrations = registry.GetAll()
			.Select(e => (e.Id, e.Orchestration.Tags));
		var tagCounts = tagStore.GetAllTagsWithCounts(orchestrations);

		return Json(new { count = tagCounts.Count, tags = tagCounts });
	}

	[McpServerTool, Description(
		"Adds tags to an orchestration. Merges with existing tags.")]
	public static string AddTags(
		OrchestrationTagStore tagStore,
		OrchestrationRegistry registry,
		ProfileManager profileManager,
		[Description("The orchestration ID.")] string orchestrationId,
		[Description("Comma-separated tags to add.")] string tags)
	{
		var entry = registry.Get(orchestrationId);
		if (entry is null)
			return Error($"Orchestration '{orchestrationId}' not found.");

		var tagList = tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		tagStore.AddTags(orchestrationId, tagList);
		profileManager.RefreshEffectiveActiveSet("tags-changed");

		return Json(new
		{
			orchestrationId,
			effectiveTags = tagStore.GetEffectiveTags(orchestrationId, entry.Orchestration.Tags),
		});
	}

	[McpServerTool, Description(
		"Removes a tag from an orchestration.")]
	public static string RemoveTag(
		OrchestrationTagStore tagStore,
		OrchestrationRegistry registry,
		ProfileManager profileManager,
		[Description("The orchestration ID.")] string orchestrationId,
		[Description("The tag to remove.")] string tag)
	{
		var entry = registry.Get(orchestrationId);
		if (entry is null)
			return Error($"Orchestration '{orchestrationId}' not found.");

		var removed = tagStore.RemoveTag(orchestrationId, tag);
		if (!removed)
			return Error($"Tag '{tag}' not found on orchestration '{orchestrationId}'.");

		profileManager.RefreshEffectiveActiveSet("tags-changed");

		return Json(new
		{
			orchestrationId,
			removedTag = tag,
			effectiveTags = tagStore.GetEffectiveTags(orchestrationId, entry.Orchestration.Tags),
		});
	}

	// ── Profile Management ──

	[McpServerTool, Description(
		"Lists all profiles with their activation status.")]
	public static string ListProfiles(
		ProfileManager profileManager)
	{
		var profiles = profileManager.GetAllProfiles();
		return Json(new
		{
			count = profiles.Count,
			profiles = profiles.Select(p => new
			{
				id = p.Id,
				name = p.Name,
				description = p.Description,
				isActive = p.IsActive,
				activatedAt = p.ActivatedAt,
				filterTags = p.Filter.Tags,
				filterOrchestrationIds = p.Filter.OrchestrationIds.Length > 0 ? p.Filter.OrchestrationIds : null,
			}).ToArray(),
		});
	}

	[McpServerTool, Description(
		"Creates a new profile with tag-based filtering. " +
		"Profiles define which orchestrations are active based on their tags.")]
	public static string CreateProfile(
		ProfileManager profileManager,
		[Description("Profile name (must be unique).")] string name,
		[Description("Optional description.")] string? description = null,
		[Description("Comma-separated tags to match orchestrations. Use '*' for wildcard (match all).")] string? tags = null,
		[Description("Comma-separated orchestration IDs to explicitly include.")] string? includeIds = null,
		[Description("Comma-separated orchestration IDs to explicitly exclude.")] string? excludeIds = null)
	{
		var filter = new ProfileFilter
		{
			Tags = ParseCommaSeparated(tags),
			OrchestrationIds = ParseCommaSeparated(includeIds),
			ExcludeOrchestrationIds = ParseCommaSeparated(excludeIds),
		};

		var profile = profileManager.CreateProfile(name, description, filter);
		if (profile is null)
			return Error($"Profile with name '{name}' already exists.");

		return Json(new
		{
			id = profile.Id,
			name = profile.Name,
			status = "created",
		});
	}

	[McpServerTool, Description(
		"Deletes a profile by its ID.")]
	public static string DeleteProfile(
		ProfileManager profileManager,
		[Description("The profile ID to delete.")] string profileId)
	{
		var deleted = profileManager.DeleteProfile(profileId);
		if (!deleted)
			return Error($"Profile '{profileId}' not found.");

		return Json(new { profileId, status = "deleted" });
	}

	[McpServerTool, Description(
		"Activates a profile, making its matched orchestrations available.")]
	public static string ActivateProfile(
		ProfileManager profileManager,
		[Description("The profile ID to activate.")] string profileId)
	{
		var activated = profileManager.ActivateProfile(profileId);
		if (!activated)
			return Error($"Profile '{profileId}' not found.");

		return Json(new { profileId, status = "activated" });
	}

	[McpServerTool, Description(
		"Deactivates a profile.")]
	public static string DeactivateProfile(
		ProfileManager profileManager,
		[Description("The profile ID to deactivate.")] string profileId)
	{
		var deactivated = profileManager.DeactivateProfile(profileId);
		if (!deactivated)
			return Error($"Profile '{profileId}' not found.");

		return Json(new { profileId, status = "deactivated" });
	}

	// ── Trigger Management ──

	[McpServerTool, Description(
		"Lists all registered triggers with their status and configuration.")]
	public static string ListTriggers(
		TriggerManager triggerManager)
	{
		var triggers = triggerManager.GetAllTriggers();
		return Json(new
		{
			count = triggers.Count(),
			triggers = triggers.Select(t => new
			{
				id = t.Id,
				orchestrationPath = t.OrchestrationPath,
				type = t.Config.Type.ToString().ToLowerInvariant(),
				enabled = t.Config.Enabled,
				status = t.Status.ToString().ToLowerInvariant(),
				runCount = t.RunCount,
				lastFireTime = t.LastFireTime,
				lastError = t.LastError,
			}).ToArray(),
		});
	}

	[McpServerTool, Description(
		"Enables a trigger by its ID.")]
	public static string EnableTrigger(
		TriggerManager triggerManager,
		[Description("The trigger ID to enable.")] string triggerId)
	{
		var enabled = triggerManager.SetTriggerEnabled(triggerId, true);
		if (!enabled)
			return Error($"Trigger '{triggerId}' not found.");

		return Json(new { triggerId, status = "enabled" });
	}

	[McpServerTool, Description(
		"Disables a trigger by its ID.")]
	public static string DisableTrigger(
		TriggerManager triggerManager,
		[Description("The trigger ID to disable.")] string triggerId)
	{
		var disabled = triggerManager.SetTriggerEnabled(triggerId, false);
		if (!disabled)
			return Error($"Trigger '{triggerId}' not found.");

		return Json(new { triggerId, status = "disabled" });
	}

	// ── Run History ──

	[McpServerTool, Description(
		"Lists recent orchestration runs from history. " +
		"Returns run summaries with status, duration, and error information.")]
	public static async Task<string> ListRuns(
		FileSystemRunStore runStore,
		[Description("Maximum number of runs to return. Default: 20.")] int limit = 20,
		[Description("Optional orchestration name to filter runs.")] string? orchestrationName = null)
	{
		IReadOnlyList<RunIndex> runs;
		if (!string.IsNullOrWhiteSpace(orchestrationName))
		{
			runs = await runStore.GetRunSummariesAsync(orchestrationName, limit);
		}
		else
		{
			runs = await runStore.GetRunSummariesAsync(limit);
		}

		return Json(new
		{
			count = runs.Count,
			runs = runs.Select(r => new
			{
				runId = r.RunId,
				orchestrationName = r.OrchestrationName,
				status = r.Status.ToString().ToLowerInvariant(),
				startedAt = r.StartedAt,
				completedAt = r.CompletedAt,
				duration = r.Duration.TotalSeconds,
				triggeredBy = r.TriggeredBy,
				errorMessage = r.ErrorMessage,
				failedStepName = r.FailedStepName,
			}).ToArray(),
		});
	}

	[McpServerTool, Description(
		"Gets the full details of a specific run including all step results.")]
	public static async Task<string> GetRun(
		FileSystemRunStore runStore,
		[Description("The orchestration name.")] string orchestrationName,
		[Description("The run ID.")] string runId)
	{
		var run = await runStore.GetRunAsync(orchestrationName, runId);
		if (run is null)
			return Error($"Run '{runId}' not found for orchestration '{orchestrationName}'.");

		return Json(new
		{
			runId = run.RunId,
			orchestrationName = run.OrchestrationName,
			status = run.Status.ToString().ToLowerInvariant(),
			startedAt = run.StartedAt,
			completedAt = run.CompletedAt,
			triggeredBy = run.TriggeredBy,
			parameters = run.Parameters,
			stepResults = run.StepRecords.ToDictionary(
				kvp => kvp.Key,
				kvp => new
				{
					status = kvp.Value.Status.ToString().ToLowerInvariant(),
					content = TruncateContent(kvp.Value.Content, 2000),
					errorMessage = kvp.Value.ErrorMessage,
				}),
		});
	}

	// ── Helpers ──

	private static string? TruncateContent(string? content, int maxLength)
	{
		if (content is null) return null;
		if (content.Length <= maxLength) return content;
		return content[..maxLength] + "... (truncated)";
	}

	private static string[] ParseCommaSeparated(string? value)
	{
		if (string.IsNullOrWhiteSpace(value)) return [];
		return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
	}

	private static string Error(string message) =>
		JsonSerializer.Serialize(new { error = message }, s_jsonOptions);

	private static string Json(object value) =>
		JsonSerializer.Serialize(value, s_jsonOptions);

	private static readonly JsonSerializerOptions s_jsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
		WriteIndented = false,
	};
}
