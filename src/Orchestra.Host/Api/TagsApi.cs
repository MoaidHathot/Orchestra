using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Orchestra.Host.Profiles;
using Orchestra.Host.Registry;

namespace Orchestra.Host.Api;

/// <summary>
/// API endpoints for tag management and orchestration browsing.
/// </summary>
public static class TagsApi
{
	/// <summary>
	/// Maps tag management and orchestration browse endpoints.
	/// </summary>
	public static IEndpointRouteBuilder MapTagsApi(this IEndpointRouteBuilder endpoints, JsonSerializerOptions jsonOptions)
	{
		// ── Tag endpoints ──

		var tagGroup = endpoints.MapGroup("/api/tags");

		// GET /api/tags - List all known tags with counts
		tagGroup.MapGet("", (OrchestrationTagStore tagStore, OrchestrationRegistry registry) =>
		{
			var orchestrations = registry.GetAll()
				.Select(e => (e.Id, e.Orchestration.Tags))
				.ToArray();

			var tagCounts = tagStore.GetAllTagsWithCounts(orchestrations);
			var tags = tagCounts
				.OrderByDescending(kvp => kvp.Value)
				.ThenBy(kvp => kvp.Key)
				.Select(kvp => new { tag = kvp.Key, count = kvp.Value })
				.ToArray();

			return Results.Json(new { count = tags.Length, tags }, jsonOptions);
		});

		// ── Per-orchestration tag endpoints ──

		var orchTagGroup = endpoints.MapGroup("/api/orchestrations");

		// GET /api/orchestrations/{id}/tags - Get effective tags for an orchestration
		orchTagGroup.MapGet("/{id}/tags", (string id, OrchestrationTagStore tagStore, OrchestrationRegistry registry) =>
		{
			var entry = registry.Get(id);
			if (entry is null)
				return ProblemDetailsHelpers.NotFound($"Orchestration '{id}' not found.");

			var effectiveTags = tagStore.GetEffectiveTags(id, entry.Orchestration.Tags);
			var hostTags = tagStore.GetTags(id);
			var authorTags = entry.Orchestration.Tags;

			return Results.Json(new
			{
				orchestrationId = id,
				effectiveTags,
				authorTags,
				hostTags,
			}, jsonOptions);
		});

		// PUT /api/orchestrations/{id}/tags - Set host-managed tags (replaces)
		orchTagGroup.MapPut("/{id}/tags", async (string id, HttpContext ctx,
			OrchestrationTagStore tagStore, OrchestrationRegistry registry, ProfileManager profileManager) =>
		{
			var entry = registry.Get(id);
			if (entry is null)
				return ProblemDetailsHelpers.NotFound($"Orchestration '{id}' not found.");

			var body = await JsonSerializer.DeserializeAsync<TagsRequest>(ctx.Request.Body, jsonOptions);
			if (body?.Tags is null)
				return ProblemDetailsHelpers.BadRequest("Tags array is required.");

			tagStore.SetTags(id, body.Tags);

			// Recompute effective active set since tag changes may affect profile matching
			profileManager.RefreshEffectiveActiveSet("tags-changed");

			var effectiveTags = tagStore.GetEffectiveTags(id, entry.Orchestration.Tags);
			return Results.Json(new { orchestrationId = id, effectiveTags }, jsonOptions);
		});

		// POST /api/orchestrations/{id}/tags - Add host-managed tags (merges)
		orchTagGroup.MapPost("/{id}/tags", async (string id, HttpContext ctx,
			OrchestrationTagStore tagStore, OrchestrationRegistry registry, ProfileManager profileManager) =>
		{
			var entry = registry.Get(id);
			if (entry is null)
				return ProblemDetailsHelpers.NotFound($"Orchestration '{id}' not found.");

			var body = await JsonSerializer.DeserializeAsync<TagsRequest>(ctx.Request.Body, jsonOptions);
			if (body?.Tags is null)
				return ProblemDetailsHelpers.BadRequest("Tags array is required.");

			tagStore.AddTags(id, body.Tags);

			// Recompute effective active set since tag changes may affect profile matching
			profileManager.RefreshEffectiveActiveSet("tags-changed");

			var effectiveTags = tagStore.GetEffectiveTags(id, entry.Orchestration.Tags);
			return Results.Json(new { orchestrationId = id, effectiveTags }, jsonOptions);
		});

		// DELETE /api/orchestrations/{id}/tags/{tag} - Remove a host-managed tag
		orchTagGroup.MapDelete("/{id}/tags/{tag}", (string id, string tag,
			OrchestrationTagStore tagStore, OrchestrationRegistry registry, ProfileManager profileManager) =>
		{
			var entry = registry.Get(id);
			if (entry is null)
				return ProblemDetailsHelpers.NotFound($"Orchestration '{id}' not found.");

			var removed = tagStore.RemoveTag(id, tag);
			if (!removed)
				return ProblemDetailsHelpers.NotFound($"Tag '{tag}' not found on orchestration '{id}'.");

			// Recompute effective active set since tag changes may affect profile matching
			profileManager.RefreshEffectiveActiveSet("tags-changed");

			var effectiveTags = tagStore.GetEffectiveTags(id, entry.Orchestration.Tags);
			return Results.Json(new { orchestrationId = id, effectiveTags }, jsonOptions);
		});

		// ── Orchestration Browse endpoint ──

		// GET /api/orchestrations/browse - Search/filter all registered orchestrations
		orchTagGroup.MapGet("/browse", (
			HttpContext ctx,
			OrchestrationRegistry registry,
			OrchestrationTagStore tagStore,
			ProfileManager profileManager,
			Triggers.TriggerManager triggerManager) =>
		{
			var query = ctx.Request.Query;
			var searchTerm = query["search"].FirstOrDefault();
			var tagFilter = query["tags"].FirstOrDefault()?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
			var profileId = query["profileId"].FirstOrDefault();
			var activeFilter = query["active"].FirstOrDefault();

			var activeIds = profileManager.GetEffectiveActiveOrchestrationIds();

			var results = registry.GetAll()
				.Select(e =>
				{
					var effectiveTags = tagStore.GetEffectiveTags(e.Id, e.Orchestration.Tags);
					var trigger = triggerManager.GetTrigger(e.Id);
					var profiles = profileManager.GetProfilesForOrchestration(e.Id);
					var isActive = activeIds.Contains(e.Id);

					return new
					{
						entry = e,
						effectiveTags,
						trigger,
						profiles,
						isActive,
					};
				})
				.Where(x =>
				{
					// Search filter (name or description)
					if (!string.IsNullOrWhiteSpace(searchTerm))
					{
						if (!x.entry.Orchestration.Name.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) &&
							!x.entry.Orchestration.Description.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
							return false;
					}

					// Tag filter
					if (tagFilter is { Length: > 0 })
					{
						if (!tagFilter.Any(t => x.effectiveTags.Contains(t, StringComparer.OrdinalIgnoreCase)))
							return false;
					}

					// Profile filter
					if (!string.IsNullOrWhiteSpace(profileId))
					{
						if (!x.profiles.Any(p => string.Equals(p.Id, profileId, StringComparison.OrdinalIgnoreCase)))
							return false;
					}

					// Active filter
					if (!string.IsNullOrWhiteSpace(activeFilter))
					{
						if (bool.TryParse(activeFilter, out var wantActive))
						{
							if (x.isActive != wantActive)
								return false;
						}
					}

					return true;
				})
				.Select(x => new
				{
					id = x.entry.Id,
					name = x.entry.Orchestration.Name,
					description = x.entry.Orchestration.Description,
					version = x.entry.Orchestration.Version,
					tags = x.effectiveTags,
					isActive = x.isActive,
					stepCount = x.entry.Orchestration.Steps.Length,
					trigger = x.trigger is not null ? new
					{
						type = x.trigger.Config.Type.ToString().ToLowerInvariant(),
						enabled = x.trigger.Config.Enabled,
						status = x.trigger.Status.ToString(),
					} : null,
					profiles = x.profiles.Select(p => new
					{
						id = p.Id,
						name = p.Name,
						isActive = p.IsActive,
					}).ToArray(),
					registeredAt = x.entry.RegisteredAt.ToString("o"),
					lastRun = x.trigger?.LastFireTime?.ToString("o"),
				}).ToArray();

			return Results.Json(new { count = results.Length, orchestrations = results }, jsonOptions);
		});

		return endpoints;
	}

	private class TagsRequest
	{
		public string[]? Tags { get; set; }
	}
}
