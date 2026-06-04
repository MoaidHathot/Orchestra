using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Orchestra.Engine;
using Orchestra.Host.Persistence;
using Orchestra.Host.Registry;

namespace Orchestra.Host.Api;

/// <summary>
/// API endpoints for orchestration version history.
/// </summary>
public static class VersionsApi
{
	/// <summary>
	/// Maps orchestration version history endpoints.
	/// </summary>
	public static IEndpointRouteBuilder MapVersionsApi(this IEndpointRouteBuilder endpoints, JsonSerializerOptions jsonOptions)
	{
		var group = endpoints.MapGroup("/api/orchestrations");

		// GET /api/orchestrations/{id}/versions - List version history for an orchestration
		group.MapGet("/{id}/versions", async (string id, OrchestrationRegistry registry) =>
		{
			// Accept ID or declared name. The version store is keyed by orchestration ID
			// (see OrchestrationRegistry.Register), so we always pass entry.Id to it after
			// resolving — a name-input would otherwise look up an empty (or wrong) version
			// history bucket.
			var entry = registry.GetByIdOrName(id);
			if (entry is null)
				return ProblemDetailsHelpers.NotFound($"Orchestration '{id}' not found.");

			var versionStore = registry.VersionStore;
			if (versionStore is null)
				return ProblemDetailsHelpers.ServiceUnavailable("Version tracking is not configured.");

			var resolvedId = entry.Id;
			var versions = await versionStore.ListVersionsAsync(resolvedId);

			return Results.Json(new
			{
				orchestrationId = resolvedId,
				orchestrationName = entry.Orchestration.Name,
				currentContentHash = entry.ContentHash,
				count = versions.Count,
				versions = versions.Select(v => new
				{
					contentHash = v.ContentHash,
					declaredVersion = v.DeclaredVersion,
					timestamp = v.Timestamp.ToString("o"),
					orchestrationName = v.OrchestrationName,
					stepCount = v.StepCount,
					changeDescription = v.ChangeDescription,
					isCurrent = v.ContentHash == entry.ContentHash
				}).ToArray()
			}, jsonOptions);
		});

		// GET /api/orchestrations/{id}/versions/{hash} - Get a specific version snapshot
		group.MapGet("/{id}/versions/{hash}", async (string id, string hash, OrchestrationRegistry registry) =>
		{
			var entry = registry.GetByIdOrName(id);
			if (entry is null)
				return ProblemDetailsHelpers.NotFound($"Orchestration '{id}' not found.");

			var versionStore = registry.VersionStore;
			if (versionStore is null)
				return ProblemDetailsHelpers.ServiceUnavailable("Version tracking is not configured.");

			var resolvedId = entry.Id;
			var snapshot = await versionStore.GetSnapshotAsync(resolvedId, hash);
			if (snapshot is null)
				return ProblemDetailsHelpers.NotFound($"Version '{hash}' not found for orchestration '{id}'.");

			// Also get the version metadata
			var versions = await versionStore.ListVersionsAsync(resolvedId);
			var versionEntry = versions.FirstOrDefault(v => v.ContentHash == hash);

			return Results.Json(new
			{
				orchestrationId = resolvedId,
				contentHash = hash,
				declaredVersion = versionEntry?.DeclaredVersion,
				timestamp = versionEntry?.Timestamp.ToString("o"),
				orchestrationName = versionEntry?.OrchestrationName,
				stepCount = versionEntry?.StepCount,
				changeDescription = versionEntry?.ChangeDescription,
				isCurrent = hash == entry.ContentHash,
				snapshot
			}, jsonOptions);
		});

		// GET /api/orchestrations/{id}/versions/{hash1}/diff/{hash2} - Compare two versions
		group.MapGet("/{id}/versions/{hash1}/diff/{hash2}", async (string id, string hash1, string hash2, OrchestrationRegistry registry) =>
		{
			var entry = registry.GetByIdOrName(id);
			if (entry is null)
				return ProblemDetailsHelpers.NotFound($"Orchestration '{id}' not found.");

			var versionStore = registry.VersionStore;
			if (versionStore is null)
				return ProblemDetailsHelpers.ServiceUnavailable("Version tracking is not configured.");

			var resolvedId = entry.Id;
			var oldSnapshot = await versionStore.GetSnapshotAsync(resolvedId, hash1);
			if (oldSnapshot is null)
				return ProblemDetailsHelpers.NotFound($"Version '{hash1}' not found for orchestration '{id}'.");

			var newSnapshot = await versionStore.GetSnapshotAsync(resolvedId, hash2);
			if (newSnapshot is null)
				return ProblemDetailsHelpers.NotFound($"Version '{hash2}' not found for orchestration '{id}'.");

			var diffLines = FileSystemOrchestrationVersionStore.ComputeDiff(oldSnapshot, newSnapshot);

			var stats = new
			{
				added = diffLines.Count(d => d.Type == DiffLineType.Added),
				removed = diffLines.Count(d => d.Type == DiffLineType.Removed),
				unchanged = diffLines.Count(d => d.Type == DiffLineType.Unchanged)
			};

			return Results.Json(new
			{
				orchestrationId = resolvedId,
				oldHash = hash1,
				newHash = hash2,
				stats,
				diff = diffLines.Select(d => new
				{
					type = d.Type.ToString().ToLowerInvariant(),
					content = d.Content
				}).ToArray()
			}, jsonOptions);
		});

		// DELETE /api/orchestrations/{id}/versions - Delete all version history
		group.MapDelete("/{id}/versions", async (string id, OrchestrationRegistry registry) =>
		{
			var entry = registry.GetByIdOrName(id);
			if (entry is null)
				return ProblemDetailsHelpers.NotFound($"Orchestration '{id}' not found.");

			var versionStore = registry.VersionStore;
			if (versionStore is null)
				return ProblemDetailsHelpers.ServiceUnavailable("Version tracking is not configured.");

			var resolvedId = entry.Id;
			await versionStore.DeleteAllVersionsAsync(resolvedId);

			return Results.Ok(new { orchestrationId = resolvedId, deleted = true });
		});

		return endpoints;
	}
}
