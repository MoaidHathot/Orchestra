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

		// POST /api/profiles/scan - Scan a directory for profile JSON files
		group.MapPost("/scan", (ProfileScanRequest request) =>
		{
			try
			{
				if (string.IsNullOrWhiteSpace(request.Directory))
					return ProblemDetailsHelpers.BadRequest("Directory path is required.");

				if (!Directory.Exists(request.Directory))
					return ProblemDetailsHelpers.BadRequest($"Directory not found: {request.Directory}");

				var files = Directory.GetFiles(request.Directory, "*.json", SearchOption.TopDirectoryOnly);
				var profiles = new List<object>();

				foreach (var file in files.OrderBy(f => f))
				{
					try
					{
						var json = File.ReadAllText(file);
						var profile = JsonSerializer.Deserialize<Profile>(json, ProfileStore.JsonOptions);

						if (profile is null || string.IsNullOrWhiteSpace(profile.Name))
						{
							profiles.Add(new
							{
								path = file,
								fileName = Path.GetFileName(file),
								name = (string?)null,
								description = (string?)null,
								tags = Array.Empty<string>(),
								hasSchedule = false,
								valid = false,
								error = "Not a valid profile: missing name"
							});
							continue;
						}

						profiles.Add(new
						{
							path = file,
							fileName = Path.GetFileName(file),
							name = profile.Name,
							description = profile.Description,
							tags = profile.Filter?.Tags ?? [],
							hasSchedule = profile.Schedule is not null,
							valid = true,
							error = (string?)null
						});
					}
					catch (Exception ex)
					{
						profiles.Add(new
						{
							path = file,
							fileName = Path.GetFileName(file),
							name = (string?)null,
							description = (string?)null,
							tags = Array.Empty<string>(),
							hasSchedule = false,
							valid = false,
							error = ex.Message
						});
					}
				}

				return Results.Json(new
				{
					directory = request.Directory,
					count = profiles.Count,
					profiles
				}, jsonOptions);
			}
			catch (Exception ex)
			{
				return ProblemDetailsHelpers.BadRequest(ex.Message);
			}
		});

		// POST /api/profiles/import - Import profiles from file paths
		group.MapPost("/import", (ImportProfilesRequest request, ProfileManager profileManager) =>
		{
			if (request.Paths is null || request.Paths.Length == 0)
				return ProblemDetailsHelpers.BadRequest("At least one file path is required.");

			var imported = new List<object>();
			var skipped = new List<object>();
			var errors = new List<object>();

			foreach (var path in request.Paths)
			{
				try
				{
					if (!File.Exists(path))
					{
						errors.Add(new { path, error = "File not found" });
						continue;
					}

					var json = File.ReadAllText(path);
					var profile = JsonSerializer.Deserialize<Profile>(json, ProfileStore.JsonOptions);

					if (profile is null || string.IsNullOrWhiteSpace(profile.Name))
					{
						errors.Add(new { path, error = "Not a valid profile: missing name" });
						continue;
					}

					// Ensure the ID is regenerated from the name for consistency
					var id = ProfileStore.GenerateId(profile.Name);
					var importProfile = new Profile
					{
						Id = id,
						Name = profile.Name,
						Description = profile.Description,
						IsActive = false,
						Filter = profile.Filter ?? new ProfileFilter { Tags = ["*"] },
						Schedule = profile.Schedule,
						CreatedAt = profile.CreatedAt != default ? profile.CreatedAt : DateTimeOffset.UtcNow,
						UpdatedAt = DateTimeOffset.UtcNow,
					};

					var result = profileManager.ImportProfile(importProfile, request.OverwriteExisting);
					if (result.Imported)
						imported.Add(new { id = result.Id, name = result.Name });
					else
						skipped.Add(new { id = result.Id, name = result.Name, reason = result.SkipReason });
				}
				catch (Exception ex)
				{
					errors.Add(new { path, error = ex.Message });
				}
			}

			return Results.Json(new
			{
				importedCount = imported.Count,
				imported,
				skipped,
				errors
			}, jsonOptions);
		});

		// POST /api/profiles/import-json - Import a profile from raw JSON
		group.MapPost("/import-json", (ImportProfileJsonRequest request, ProfileManager profileManager) =>
		{
			try
			{
				if (string.IsNullOrWhiteSpace(request.Json))
					return ProblemDetailsHelpers.BadRequest("JSON content is required.");

				var profile = JsonSerializer.Deserialize<Profile>(request.Json, ProfileStore.JsonOptions);
				if (profile is null || string.IsNullOrWhiteSpace(profile.Name))
					return ProblemDetailsHelpers.BadRequest("Not a valid profile: missing name.");

				var id = ProfileStore.GenerateId(profile.Name);
				var importProfile = new Profile
				{
					Id = id,
					Name = profile.Name,
					Description = profile.Description,
					IsActive = false,
					Filter = profile.Filter ?? new ProfileFilter { Tags = ["*"] },
					Schedule = profile.Schedule,
					CreatedAt = profile.CreatedAt != default ? profile.CreatedAt : DateTimeOffset.UtcNow,
					UpdatedAt = DateTimeOffset.UtcNow,
				};

				var result = profileManager.ImportProfile(importProfile, request.OverwriteExisting);
				if (result.Imported)
					return Results.Json(new { id = result.Id, name = result.Name, imported = true }, jsonOptions, statusCode: 201);
				else
					return Results.Json(new { id = result.Id, name = result.Name, imported = false, reason = result.SkipReason }, jsonOptions);
			}
			catch (Exception ex)
			{
				return ProblemDetailsHelpers.BadRequest(ex.Message);
			}
		});

		// POST /api/profiles/export - Export profiles to a directory
		group.MapPost("/export", (ExportProfilesRequest request, ProfileManager profileManager) =>
		{
			try
			{
				if (string.IsNullOrWhiteSpace(request.Directory))
					return ProblemDetailsHelpers.BadRequest("Directory path is required.");

				var results = profileManager.ExportProfiles(request.Directory, request.ProfileIds, request.OverwriteExisting);
				var exported = results.Where(r => r.Exported).Select(r => new { id = r.Id, name = r.Name, path = r.Path }).ToArray();
				var exportSkipped = results.Where(r => !r.Exported).Select(r => new { id = r.Id, name = r.Name, reason = r.SkipReason }).ToArray();

				return Results.Json(new
				{
					exportedCount = exported.Length,
					exported,
					skipped = exportSkipped
				}, jsonOptions);
			}
			catch (Exception ex)
			{
				return ProblemDetailsHelpers.BadRequest(ex.Message);
			}
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

// ── Profile Import/Export Request DTOs ──

public record ProfileScanRequest(string? Directory);
public record ImportProfilesRequest(string[]? Paths, bool OverwriteExisting = false);
public record ImportProfileJsonRequest(string? Json, bool OverwriteExisting = false);
public record ExportProfilesRequest(string? Directory, string[]? ProfileIds, bool OverwriteExisting = false);
