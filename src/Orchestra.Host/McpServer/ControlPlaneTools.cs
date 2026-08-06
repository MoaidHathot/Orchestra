using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Orchestra.Engine;
using Orchestra.Host.Export;
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

	[McpServerTool(Name = "get_orchestration_details"), Description(
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
					multiline = kvp.Value.Multiline ? true : (bool?)null,
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

	[McpServerTool(Name = "register_orchestration"), Description(
		"Registers an orchestration from a file path. " +
		"The file must be a valid orchestration JSON or YAML file.")]
	public static string RegisterOrchestration(
		OrchestrationRegistry registry,
		TriggerManager triggerManager,
		[Description("Absolute path to the orchestration file (JSON or YAML).")] string path)
	{
		if (!File.Exists(path))
			return Error($"File not found: {path}");

		try
		{
			var entry = registry.Register(path);

			// Register trigger if enabled
			if (entry.Orchestration.Trigger.Enabled)
			{
				triggerManager.RegisterTrigger(
					entry.Path, entry.Orchestration.Trigger,
					null, TriggerSource.Json, entry.Id, entry.Orchestration,
					entry.SourcePath);
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

	[McpServerTool(Name = "remove_orchestration"), Description(
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

	[McpServerTool(Name = "scan_directory"), Description(
		"Scans a directory for orchestration files (JSON and YAML) and returns metadata. " +
		"Does not register them — use register_orchestration for that.")]
	public static string ScanDirectory(
		[Description("Absolute path to the directory to scan.")] string directory)
	{
		if (!Directory.Exists(directory))
			return Error($"Directory not found: {directory}");

		var files = OrchestrationParser.GetOrchestrationFiles(directory);
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

	[McpServerTool(Name = "list_tags"), Description(
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

	[McpServerTool(Name = "add_tags"), Description(
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

	[McpServerTool(Name = "remove_tag"), Description(
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

	[McpServerTool(Name = "list_profiles"), Description(
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

	[McpServerTool(Name = "create_profile"), Description(
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

	[McpServerTool(Name = "delete_profile"), Description(
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

	[McpServerTool(Name = "activate_profile"), Description(
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

	[McpServerTool(Name = "deactivate_profile"), Description(
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

	[McpServerTool(Name = "list_triggers"), Description(
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

	[McpServerTool(Name = "enable_trigger"), Description(
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

	[McpServerTool(Name = "disable_trigger"), Description(
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

	[McpServerTool(Name = "list_runs"), Description(
		"Lists recent orchestration runs from history. " +
		"Returns run summaries with status, duration, lineage (parent/root/depth), and error information. " +
		"Optionally filter by orchestration name, parent execution id (direct children only), or root execution id (whole subtree).")]
	public static async Task<string> ListRuns(
		FileSystemRunStore runStore,
		RunAnnotationStore annotations,
		[Description("Maximum number of runs to return. Default: 20.")] int limit = 20,
		[Description("Optional orchestration name to filter runs.")] string? orchestrationName = null,
		[Description("Optional parent execution id. When set, returns only direct children of that execution. Mutually exclusive with rootExecutionId.")] string? parentExecutionId = null,
		[Description("Optional root execution id. When set (and parentExecutionId is not), returns every run in the named execution subtree.")] string? rootExecutionId = null,
		[Description("When true, returns only runs marked as favorites.")] bool favoritesOnly = false,
		[Description("Optional comma-separated annotation tags. Returns runs carrying ANY of them (OR).")] string? tags = null)
	{
		IReadOnlyList<RunIndex> runs;
		if (!string.IsNullOrWhiteSpace(parentExecutionId) || !string.IsNullOrWhiteSpace(rootExecutionId))
		{
			// Delegate to the same scan path the data-plane list_child_runs tool uses so
			// both surfaces stay consistent in their parent/root semantics.
			runs = await runStore.FindChildRunsAsync(parentExecutionId, rootExecutionId, statusFilter: null, limit);
		}
		else if (!string.IsNullOrWhiteSpace(orchestrationName))
		{
			runs = await runStore.GetRunSummariesAsync(orchestrationName, limit);
		}
		else
		{
			runs = await runStore.GetRunSummariesAsync(limit);
		}

		// Annotation filters mirror the REST history endpoint: favorites is a narrowing
		// switch, tags are OR.
		if (favoritesOnly || !string.IsNullOrWhiteSpace(tags))
		{
			var wanted = string.IsNullOrWhiteSpace(tags)
				? null
				: new HashSet<string>(
					tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
					StringComparer.OrdinalIgnoreCase);

			runs = [.. runs.Where(r =>
			{
				var a = annotations.Get(r.RunId);
				if (a is null) return false;
				if (favoritesOnly && !a.Favorite) return false;
				return wanted is null || a.Tags.Any(wanted.Contains);
			})];
		}

		return Json(new
		{
			count = runs.Count,
			runs = runs.Select(r => new
			{
				runId = r.RunId,
				orchestrationName = r.OrchestrationName,
				orchestrationVersion = r.OrchestrationVersion,
				status = r.Status.ToString().ToLowerInvariant(),
				startedAt = r.StartedAt,
				completedAt = r.CompletedAt,
				duration = r.Duration.TotalSeconds,
				triggeredBy = r.TriggeredBy,
				errorMessage = r.ErrorMessage,
				failedStepName = r.FailedStepName,
				// Lineage fields are persisted on RunIndex but were previously elided from
				// this projection — surface them so admin views and follow-up calls can
				// walk parent → child chains without separate lookups.
				parentExecutionId = r.ParentExecutionId,
				parentStepName = r.ParentStepName,
				rootExecutionId = r.RootExecutionId,
				nestingDepth = r.NestingDepth,
				isIncomplete = r.IsIncomplete ? true : (bool?)null,
				completionReason = r.CompletionReason,
				cancellation = r.Cancellation is null ? null : new
				{
					kind = r.Cancellation.Kind.ToString(),
					detail = r.Cancellation.Detail,
				},
				// User curation. A title is often the only human-meaningful identifier a run
				// has, since orchestration names are frequently machine-generated.
				favorite = annotations.Get(r.RunId)?.Favorite ?? false,
				title = annotations.Get(r.RunId)?.Title,
				tags = annotations.Get(r.RunId)?.Tags ?? [],
				note = annotations.Get(r.RunId)?.Note,
			}).ToArray(),
		});
	}

	[McpServerTool(Name = "get_run"), Description(
		"Gets the full details of a specific run including all step results.")]
	public static async Task<string> GetRun(
		FileSystemRunStore runStore,
		[Description("The orchestration name.")] string orchestrationName,
		[Description("The run ID.")] string runId,
		[Description("Response detail level: 'summary' (status + metadata only, no per-step content), 'compact' (default, content truncated to ~8000 chars per step), 'full' (untruncated; responses may be large).")] string detail = "compact")
	{
		var run = await runStore.GetRunAsync(orchestrationName, runId);
		if (run is null)
			return Error($"Run '{runId}' not found for orchestration '{orchestrationName}'.");

		if (!DataPlaneTools.TryParseDetailLevel(detail, out var detailParsed))
		{
			return Error($"Invalid detail level '{detail}'. Valid values: 'summary', 'compact', 'full'.");
		}

		return Json(new
		{
			runId = run.RunId,
			orchestrationName = run.OrchestrationName,
			status = run.Status.ToString().ToLowerInvariant(),
			startedAt = run.StartedAt,
			completedAt = run.CompletedAt,
			triggeredBy = run.TriggeredBy,
			parameters = run.Parameters,
			savedFiles = run.SavedFiles,
			stepResults = run.StepRecords.ToDictionary(
				kvp => kvp.Key,
				kvp => DataPlaneTools.BuildStepProjection(
					kvp.Value.Status,
					kvp.Value.Content,
					kvp.Value.RawContent,
					kvp.Value.ErrorMessage,
					kvp.Value.SavedFiles,
					detailParsed,
					perStepLimitChars: 8000)),
			detail = detailParsed.ToString().ToLowerInvariant(),
			responseHint = detailParsed == DataPlaneTools.DetailLevel.Full
				? "detail=full returned untruncated content; responses may be large."
				: null,
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

	// ── Run Annotations & Export ──

	[McpServerTool(Name = "annotate_run"), Description(
		"Sets a run's curation: favorite, title, tags and note. " +
		"Run records are immutable, so this metadata is stored separately and merged into run listings. " +
		"A title is the main way to make a run findable later - orchestration names are often machine-generated " +
		"and carry no meaning. Favorited runs are also exempt from retention deletion. " +
		"Only the fields you supply are changed; omit a field to leave it untouched, or pass an empty string to clear it.")]
	public static async Task<string> AnnotateRun(
		FileSystemRunStore runStore,
		RunAnnotationStore annotations,
		[Description("The orchestration name.")] string orchestrationName,
		[Description("The run ID.")] string runId,
		[Description("Mark or unmark as a favorite. Omit to leave unchanged.")] bool? favorite = null,
		[Description("Human-readable title for the run. Empty string clears it.")] string? title = null,
		[Description("Comma-separated tags. Replaces the existing tag set. Empty string clears all tags.")] string? tags = null,
		[Description("Free-form note - caveats, findings, or why the run was kept. Empty string clears it.")] string? note = null)
	{
		if (await runStore.GetRunAsync(orchestrationName, runId) is null)
			return Error($"Run '{runId}' not found for orchestration '{orchestrationName}'.");

		string[]? tagList = tags is null
			? null
			: tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

		var saved = annotations.Patch(runId, favorite, title, tagList, note, orchestrationName);

		return Json(new
		{
			runId,
			orchestrationName,
			favorite = saved?.Favorite ?? false,
			title = saved?.Title,
			tags = saved?.Tags ?? [],
			note = saved?.Note,
			annotatedAt = saved?.AnnotatedAt,
		});
	}

	[McpServerTool(Name = "list_run_annotations"), Description(
		"Lists every annotated run with its favorite flag, title, tags and note, plus tag usage counts. " +
		"Use this to discover which runs have been deliberately kept and how they are labelled.")]
	public static async Task<string> ListRunAnnotations(
		FileSystemRunStore runStore,
		RunAnnotationStore annotations,
		[Description("When true, returns only annotations whose run no longer exists.")] bool orphansOnly = false)
	{
		var summaries = await runStore.GetRunSummariesAsync();
		var live = new HashSet<string>(summaries.Select(s => s.RunId), StringComparer.OrdinalIgnoreCase);
		var orphanIds = new HashSet<string>(annotations.FindOrphans(live), StringComparer.OrdinalIgnoreCase);

		var items = annotations.GetAll()
			.Where(kvp => !orphansOnly || orphanIds.Contains(kvp.Key))
			.OrderByDescending(kvp => kvp.Value.AnnotatedAt)
			.Select(kvp => new
			{
				runId = kvp.Key,
				orchestrationName = kvp.Value.OrchestrationName,
				favorite = kvp.Value.Favorite,
				title = kvp.Value.Title,
				tags = kvp.Value.Tags,
				note = kvp.Value.Note,
				annotatedAt = kvp.Value.AnnotatedAt,
				orphaned = orphanIds.Contains(kvp.Key),
			})
			.ToArray();

		// Same shape as the REST GET /api/tags-style listing: an array of {tag,count}
		// objects rather than a map, so both surfaces deserialize identically.
		var tagCounts = annotations.GetAllTagsWithCounts()
			.OrderByDescending(kvp => kvp.Value)
			.ThenBy(kvp => kvp.Key, StringComparer.Ordinal)
			.Select(kvp => new { tag = kvp.Key, count = kvp.Value })
			.ToArray();

		return Json(new
		{
			count = items.Length,
			orphanCount = orphanIds.Count,
			annotations = items,
			tags = tagCounts,
		});
	}

	[McpServerTool(Name = "export_run"), Description(
		"Exports a run to a directory on the host, gathering both the execution record and the files the run " +
		"saved via orchestra_save_file. Those saved files live outside the execution folder and are usually the " +
		"run's real deliverable, so a plain copy of the run folder would miss them. " +
		"Formats: 'bundle' (default - README, run record, definition, step payloads, saved artifacts), " +
		"'report' (a single markdown file), 'data' (step payloads as JSON only).")]
	public static async Task<string> ExportRun(
		RunExporter exporter,
		[Description("The orchestration name.")] string orchestrationName,
		[Description("The run ID.")] string runId,
		[Description("Directory to write the export into.")] string outputDirectory,
		[Description("Export shape: bundle (default), report, or data.")] string format = "bundle",
		[Description("When true, compresses the export into a .zip and removes the directory.")] bool zip = false)
	{
		if (!Enum.TryParse<RunExportFormat>(format, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
			return Error($"Unknown export format '{format}'. Use bundle, report, or data.");

		if (string.IsNullOrWhiteSpace(outputDirectory))
			return Error("outputDirectory is required.");

		try
		{
			var result = await exporter.ExportAsync(orchestrationName, runId, parsed, outputDirectory);
			var path = zip && parsed != RunExportFormat.Report
				? RunExporter.CompressExport(result.Path)
				: result.Path;

			return Json(new
			{
				runId = result.RunId,
				orchestrationName = result.OrchestrationName,
				path,
				fileCount = result.FileCount,
				totalBytes = result.TotalBytes,
				warnings = result.Warnings,
			});
		}
		catch (FileNotFoundException)
		{
			return Error($"Run '{runId}' not found for orchestration '{orchestrationName}'.");
		}
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
