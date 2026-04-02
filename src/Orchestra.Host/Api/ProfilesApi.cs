using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Orchestra.Host.Profiles;
using Orchestra.Host.Registry;

namespace Orchestra.Host.Api;

/// <summary>
/// API endpoints for profile management.
/// </summary>
public static class ProfilesApi
{
	/// <summary>
	/// Maps profile management endpoints.
	/// </summary>
	public static IEndpointRouteBuilder MapProfilesApi(this IEndpointRouteBuilder endpoints, JsonSerializerOptions jsonOptions)
	{
		var group = endpoints.MapGroup("/api/profiles");

		// GET /api/profiles - List all profiles
		group.MapGet("", (ProfileManager profileManager) =>
		{
			var profiles = profileManager.GetAllProfiles().Select(p => FormatProfile(p)).ToArray();
			return Results.Json(new { count = profiles.Length, profiles }, jsonOptions);
		});

		// POST /api/profiles - Create a new profile
		group.MapPost("", async Task<IResult> (HttpContext ctx, ProfileManager profileManager) =>
		{
			var body = await JsonSerializer.DeserializeAsync<CreateProfileRequest>(ctx.Request.Body, jsonOptions);
			if (body is null || string.IsNullOrWhiteSpace(body.Name))
				return ProblemDetailsHelpers.BadRequest("Name is required.");

			var filter = body.Filter ?? new ProfileFilter { Tags = ["*"] };
			var profile = profileManager.CreateProfile(body.Name, body.Description, filter, body.Schedule);

			if (profile is null)
				return ProblemDetailsHelpers.Conflict("A profile with this name already exists.");

			return Results.Json(FormatProfile(profile), jsonOptions, statusCode: 201);
		});

		// GET /api/profiles/{id} - Get a specific profile
		group.MapGet("/{id}", (string id, ProfileManager profileManager) =>
		{
			var profile = profileManager.GetProfile(id);
			if (profile is null)
				return ProblemDetailsHelpers.NotFound($"Profile '{id}' not found.");

			return Results.Json(FormatProfile(profile), jsonOptions);
		});

		// PUT /api/profiles/{id} - Update a profile
		group.MapPut("/{id}", async Task<IResult> (string id, HttpContext ctx, ProfileManager profileManager) =>
		{
			var body = await JsonSerializer.DeserializeAsync<UpdateProfileRequest>(ctx.Request.Body, jsonOptions);
			if (body is null)
				return ProblemDetailsHelpers.BadRequest("Request body is required.");

			var updated = profileManager.UpdateProfile(id, body.Name, body.Description, body.Filter, body.Schedule);
			if (updated is null)
				return ProblemDetailsHelpers.NotFound($"Profile '{id}' not found.");

			return Results.Json(FormatProfile(updated), jsonOptions);
		});

		// DELETE /api/profiles/{id} - Delete a profile
		group.MapDelete("/{id}", (string id, ProfileManager profileManager) =>
		{
			if (!profileManager.DeleteProfile(id))
				return ProblemDetailsHelpers.NotFound($"Profile '{id}' not found.");

			return Results.Ok(new { deleted = true, id });
		});

		// POST /api/profiles/{id}/activate - Activate a profile
		group.MapPost("/{id}/activate", (string id, ProfileManager profileManager) =>
		{
			if (!profileManager.ActivateProfile(id))
				return ProblemDetailsHelpers.NotFound($"Profile '{id}' not found.");

			return Results.Ok(new { id, isActive = true });
		});

		// POST /api/profiles/{id}/deactivate - Deactivate a profile
		group.MapPost("/{id}/deactivate", (string id, ProfileManager profileManager) =>
		{
			if (!profileManager.DeactivateProfile(id))
				return ProblemDetailsHelpers.NotFound($"Profile '{id}' not found.");

			return Results.Ok(new { id, isActive = false });
		});

		// GET /api/profiles/{id}/orchestrations - Get orchestrations matching this profile
		group.MapGet("/{id}/orchestrations", (string id, ProfileManager profileManager, OrchestrationTagStore tagStore) =>
		{
			var profile = profileManager.GetProfile(id);
			if (profile is null)
				return ProblemDetailsHelpers.NotFound($"Profile '{id}' not found.");

			var orchestrations = profileManager.GetOrchestrationsByProfile(id)
				.Select(e => new
				{
					id = e.Id,
					name = e.Orchestration.Name,
					description = e.Orchestration.Description,
					version = e.Orchestration.Version,
					tags = tagStore.GetEffectiveTags(e.Id, e.Orchestration.Tags),
				}).ToArray();

			return Results.Json(new { count = orchestrations.Length, orchestrations }, jsonOptions);
		});

		// GET /api/profiles/{id}/history - Get activation history for a profile
		group.MapGet("/{id}/history", (string id, ProfileManager profileManager) =>
		{
			var profile = profileManager.GetProfile(id);
			if (profile is null)
				return ProblemDetailsHelpers.NotFound($"Profile '{id}' not found.");

			var history = profileManager.GetProfileHistory(id);
			return Results.Json(new { profileId = id, count = history.Count, history }, jsonOptions);
		});

		// GET /api/profiles/effective - Get the unified effective active orchestration set
		group.MapGet("/effective", (ProfileManager profileManager, OrchestrationRegistry registry, OrchestrationTagStore tagStore) =>
		{
			var activeIds = profileManager.GetEffectiveActiveOrchestrationIds();
			var orchestrations = registry.GetAll()
				.Where(e => activeIds.Contains(e.Id))
				.Select(e => new
				{
					id = e.Id,
					name = e.Orchestration.Name,
					description = e.Orchestration.Description,
					version = e.Orchestration.Version,
					tags = tagStore.GetEffectiveTags(e.Id, e.Orchestration.Tags),
				}).ToArray();

			return Results.Json(new { count = orchestrations.Length, orchestrations }, jsonOptions);
		});

		return endpoints;
	}

	private static object FormatProfile(Profile profile)
	{
		return new
		{
			id = profile.Id,
			name = profile.Name,
			description = profile.Description,
			isActive = profile.IsActive,
			activatedAt = profile.ActivatedAt?.ToString("o"),
			deactivatedAt = profile.DeactivatedAt?.ToString("o"),
			filter = new
			{
				tags = profile.Filter.Tags,
				orchestrationIds = profile.Filter.OrchestrationIds,
				excludeOrchestrationIds = profile.Filter.ExcludeOrchestrationIds,
			},
			schedule = profile.Schedule is not null ? new
			{
				timezone = profile.Schedule.Timezone,
				windows = profile.Schedule.Windows.Select(w => new
				{
					days = w.Days,
					startTime = w.StartTime,
					endTime = w.EndTime,
				}).ToArray(),
			} : null,
			createdAt = profile.CreatedAt.ToString("o"),
			updatedAt = profile.UpdatedAt.ToString("o"),
		};
	}

	// ── Request Models ──

	private class CreateProfileRequest
	{
		public string? Name { get; set; }
		public string? Description { get; set; }
		public ProfileFilter? Filter { get; set; }
		public ProfileSchedule? Schedule { get; set; }
	}

	private class UpdateProfileRequest
	{
		public string? Name { get; set; }
		public string? Description { get; set; }
		public ProfileFilter? Filter { get; set; }
		public ProfileSchedule? Schedule { get; set; }
	}
}
