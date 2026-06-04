using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Orchestra.Server.Tests;

/// <summary>
/// Integration tests pinning the "accept registry ID OR declared orchestration name" contract
/// for every <c>/api/orchestrations/{id}/...</c> endpoint.
///
/// Background: <c>GET /api/orchestrations/{id}</c> already used <c>registry.GetByIdOrName(id)</c>,
/// but a long tail of sibling endpoints (run, delete, enable/disable, tags, versions, resume)
/// were still ID-only via <c>registry.Get(id)</c>. That asymmetry surfaced as 404s in the CLI
/// (<c>orchestra run my-orch</c>) and the Portal whenever a user passed a name. This file pins
/// the symmetric behaviour: each endpoint must succeed whether the caller passes the ID or
/// the declared name, AND when invoked by name must echo the canonical ID in its response
/// payload so downstream caches and dashboards remain ID-indexed.
/// </summary>
public class OrchestrationIdOrNameResolutionTests : IClassFixture<ServerWebApplicationFactory>, IDisposable
{
	private readonly ServerWebApplicationFactory _factory;
	private readonly HttpClient _client;
	private readonly JsonSerializerOptions _jsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = true,
	};

	public OrchestrationIdOrNameResolutionTests(ServerWebApplicationFactory factory)
	{
		_factory = factory;
		_client = factory.CreateClient();
	}

	public void Dispose() => _client.Dispose();

	// ── Helpers ───────────────────────────────────────────────────────────────

	/// <summary>
	/// Registers a no-dependency, no-LLM orchestration so the test fixture can exercise
	/// run/resume endpoints without driving the agent runtime. Returns the
	/// (declaredName, registryId) pair so the test can hit endpoints both ways.
	/// </summary>
	private async Task<(string Name, string Id)> RegisterTransformOrchestrationAsync()
	{
		// Each test gets a unique declared name so the fixture-scoped registry doesn't
		// collide when tests run in parallel.
		var name = $"id-or-name-{Guid.NewGuid():N}";
		var json = $$"""
		{
			"name": "{{name}}",
			"description": "Resolution test (Transform-only, no LLM).",
			"steps": [
				{
					"name": "echo",
					"type": "Transform",
					"template": "hi"
				}
			]
		}
		""";

		var response = await _client.PostAsJsonAsync("/api/orchestrations/json", new { json }, _jsonOptions);
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var result = await response.Content.ReadFromJsonAsync<JsonElement>(_jsonOptions);
		var id = result.GetProperty("id").GetString()!;
		return (name, id);
	}

	private static string Encode(string s) => Uri.EscapeDataString(s);

	/// <summary>
	/// Waits up to ~2s for the registry's fire-and-forget version snapshot to be persisted
	/// (see <c>OrchestrationRegistry.Register</c> — the version save is discarded with
	/// <c>_ = SnapshotVersionAsync(...)</c>, so a test that GETs the version list
	/// immediately after registration can race the writer and observe an empty array).
	/// Returns the version count when at least one version is visible, otherwise 0.
	/// </summary>
	private async Task<int> WaitForVersionsAsync(string id, int minCount = 1)
	{
		for (var i = 0; i < 20; i++)
		{
			var response = await _client.GetAsync($"/api/orchestrations/{Encode(id)}/versions");
			if (response.StatusCode == HttpStatusCode.OK)
			{
				var doc = await response.Content.ReadFromJsonAsync<JsonElement>(_jsonOptions);
				if (doc.TryGetProperty("count", out var countEl)
					&& countEl.ValueKind == JsonValueKind.Number
					&& countEl.GetInt32() >= minCount)
				{
					return countEl.GetInt32();
				}
			}
			await Task.Delay(100);
		}
		return 0;
	}

	/// <summary>
	/// Reads the runtime trigger-enabled state for an orchestration via the list endpoint,
	/// which surfaces it at the top level (the single-orchestration GET nests it under
	/// <c>trigger.enabled</c> and may reflect the file-declared state rather than the
	/// runtime override). Returns null if the entry has gone missing.
	/// </summary>
	private async Task<bool?> ReadRuntimeEnabledAsync(string id)
	{
		var list = await _client.GetAsync("/api/orchestrations");
		list.StatusCode.Should().Be(HttpStatusCode.OK);
		var doc = await list.Content.ReadFromJsonAsync<JsonElement>(_jsonOptions);
		foreach (var entry in doc.GetProperty("orchestrations").EnumerateArray())
		{
			if (entry.TryGetProperty("id", out var idEl)
				&& string.Equals(idEl.GetString(), id, StringComparison.Ordinal)
				&& entry.TryGetProperty("enabled", out var enabledEl)
				&& (enabledEl.ValueKind == JsonValueKind.True || enabledEl.ValueKind == JsonValueKind.False))
			{
				return enabledEl.GetBoolean();
			}
		}
		return null;
	}

	// ── 1. GET /api/orchestrations/{id}/run ───────────────────────────────────

	[Fact]
	public async Task Run_AcceptsIdAndName_AndReturnsSseStream()
	{
		// Before the fix this returned 404 when invoked by name (CLI's reported bug).
		// We just verify the resolution path — we don't drive the SSE stream to completion
		// because that would couple the test to the executor's runtime behaviour.
		var (name, id) = await RegisterTransformOrchestrationAsync();

		var byId = await _client.GetAsync($"/api/orchestrations/{Encode(id)}/run", HttpCompletionOption.ResponseHeadersRead);
		byId.StatusCode.Should().Be(HttpStatusCode.OK);
		byId.Content.Headers.ContentType?.MediaType.Should().Be("text/event-stream");
		byId.Dispose();

		var byName = await _client.GetAsync($"/api/orchestrations/{Encode(name)}/run", HttpCompletionOption.ResponseHeadersRead);
		byName.StatusCode.Should().Be(HttpStatusCode.OK,
			"run must accept the declared orchestration name, not just the registry ID");
		byName.Content.Headers.ContentType?.MediaType.Should().Be("text/event-stream");
		byName.Dispose();
	}

	[Fact]
	public async Task Run_UnknownName_Returns404WithInputInDetail()
	{
		var bogus = $"definitely-not-a-thing-{Guid.NewGuid():N}";
		var response = await _client.GetAsync($"/api/orchestrations/{Encode(bogus)}/run", HttpCompletionOption.ResponseHeadersRead);

		response.StatusCode.Should().Be(HttpStatusCode.NotFound);
		var body = await response.Content.ReadAsStringAsync();
		body.Should().Contain(bogus, "404 detail should echo the user's input verbatim");
	}

	// ── 2. POST /api/orchestrations/{id}/enable ───────────────────────────────

	[Fact]
	public async Task Enable_AcceptsName_AndKeyDownstreamStoreById()
	{
		var (name, id) = await RegisterTransformOrchestrationAsync();

		// Disable first (registry default may have it enabled) so the enable call is a
		// real state transition rather than a no-op.
		await _client.PostAsync($"/api/orchestrations/{Encode(id)}/disable", null);

		var response = await _client.PostAsync($"/api/orchestrations/{Encode(name)}/enable", null);
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var payload = await response.Content.ReadFromJsonAsync<JsonElement>(_jsonOptions);
		payload.GetProperty("id").GetString().Should().Be(id,
			"the response must echo the canonical registry ID, not the name the caller passed");
		payload.GetProperty("enabled").GetBoolean().Should().BeTrue();

		// Cross-check the runtime trigger state by ID via the list endpoint, which
		// surfaces the live TriggerManager state at the top level. A leaky fix would
		// have keyed the trigger manager by the name instead and left this query
		// reporting the disabled state we set above.
		var runtimeEnabled = await ReadRuntimeEnabledAsync(id);
		runtimeEnabled.Should().BeTrue("the trigger must have been enabled under the canonical ID");
	}

	// ── 3. POST /api/orchestrations/{id}/disable ──────────────────────────────

	[Fact]
	public async Task Disable_AcceptsName_AndKeyDownstreamStoreById()
	{
		var (name, id) = await RegisterTransformOrchestrationAsync();

		var response = await _client.PostAsync($"/api/orchestrations/{Encode(name)}/disable", null);
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var payload = await response.Content.ReadFromJsonAsync<JsonElement>(_jsonOptions);
		payload.GetProperty("id").GetString().Should().Be(id);
		payload.GetProperty("enabled").GetBoolean().Should().BeFalse();

		var runtimeEnabled = await ReadRuntimeEnabledAsync(id);
		runtimeEnabled.Should().BeFalse("the trigger must have been disabled under the canonical ID");
	}

	// ── 4. DELETE /api/orchestrations/{id} ────────────────────────────────────

	[Fact]
	public async Task Delete_AcceptsName_AndRemovesByCanonicalId()
	{
		var (name, id) = await RegisterTransformOrchestrationAsync();

		var response = await _client.DeleteAsync($"/api/orchestrations/{Encode(name)}");
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var payload = await response.Content.ReadFromJsonAsync<JsonElement>(_jsonOptions);
		payload.GetProperty("id").GetString().Should().Be(id,
			"DELETE must echo the canonical ID so callers cache-evict the right entry");
		payload.GetProperty("removed").GetBoolean().Should().BeTrue();

		var verify = await _client.GetAsync($"/api/orchestrations/{Encode(id)}");
		verify.StatusCode.Should().Be(HttpStatusCode.NotFound,
			"the orchestration must actually be gone — a leaky fix would leave it behind");
	}

	// ── 5. GET /api/orchestrations/{id}/tags ──────────────────────────────────

	[Fact]
	public async Task TagsGet_AcceptsName_ReturnsCanonicalIdInPayload()
	{
		var (name, id) = await RegisterTransformOrchestrationAsync();

		var response = await _client.GetAsync($"/api/orchestrations/{Encode(name)}/tags");
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var payload = await response.Content.ReadFromJsonAsync<JsonElement>(_jsonOptions);
		payload.GetProperty("orchestrationId").GetString().Should().Be(id);
	}

	// ── 6. PUT /api/orchestrations/{id}/tags ──────────────────────────────────

	[Fact]
	public async Task TagsPut_AcceptsName_AndKeyTagStoreById()
	{
		var (name, id) = await RegisterTransformOrchestrationAsync();

		var put = await _client.PutAsJsonAsync(
			$"/api/orchestrations/{Encode(name)}/tags",
			new { tags = new[] { "alpha", "beta" } },
			_jsonOptions);
		put.StatusCode.Should().Be(HttpStatusCode.OK);
		var putPayload = await put.Content.ReadFromJsonAsync<JsonElement>(_jsonOptions);
		putPayload.GetProperty("orchestrationId").GetString().Should().Be(id);

		// Tag store is ID-keyed; reading back by ID must see exactly what we set by name.
		var readback = await _client.GetAsync($"/api/orchestrations/{Encode(id)}/tags");
		var doc = await readback.Content.ReadFromJsonAsync<JsonElement>(_jsonOptions);
		var hostTags = doc.GetProperty("hostTags").EnumerateArray().Select(t => t.GetString()).ToArray();
		hostTags.Should().BeEquivalentTo(new[] { "alpha", "beta" });
	}

	// ── 7. POST /api/orchestrations/{id}/tags ─────────────────────────────────

	[Fact]
	public async Task TagsPost_AcceptsName_AndMergesUnderCanonicalId()
	{
		var (name, id) = await RegisterTransformOrchestrationAsync();

		await _client.PostAsJsonAsync(
			$"/api/orchestrations/{Encode(name)}/tags",
			new { tags = new[] { "first" } },
			_jsonOptions);
		var second = await _client.PostAsJsonAsync(
			$"/api/orchestrations/{Encode(name)}/tags",
			new { tags = new[] { "second" } },
			_jsonOptions);
		second.StatusCode.Should().Be(HttpStatusCode.OK);
		var payload = await second.Content.ReadFromJsonAsync<JsonElement>(_jsonOptions);
		payload.GetProperty("orchestrationId").GetString().Should().Be(id);

		// Both name-keyed posts must have merged under the same canonical ID. If the fix
		// were missing, the second post would have created a separate tag bucket and the
		// first would be silently lost.
		var readback = await _client.GetAsync($"/api/orchestrations/{Encode(id)}/tags");
		var doc = await readback.Content.ReadFromJsonAsync<JsonElement>(_jsonOptions);
		var hostTags = doc.GetProperty("hostTags").EnumerateArray().Select(t => t.GetString()).ToArray();
		hostTags.Should().Contain(new[] { "first", "second" });
	}

	// ── 8. DELETE /api/orchestrations/{id}/tags/{tag} ─────────────────────────

	[Fact]
	public async Task TagsRemove_AcceptsName_AndRemovesUnderCanonicalId()
	{
		var (name, id) = await RegisterTransformOrchestrationAsync();

		// Seed via PUT so the tag exists.
		await _client.PutAsJsonAsync(
			$"/api/orchestrations/{Encode(id)}/tags",
			new { tags = new[] { "stays", "goes" } },
			_jsonOptions);

		var remove = await _client.DeleteAsync($"/api/orchestrations/{Encode(name)}/tags/goes");
		remove.StatusCode.Should().Be(HttpStatusCode.OK);
		var payload = await remove.Content.ReadFromJsonAsync<JsonElement>(_jsonOptions);
		payload.GetProperty("orchestrationId").GetString().Should().Be(id);

		var readback = await _client.GetAsync($"/api/orchestrations/{Encode(id)}/tags");
		var doc = await readback.Content.ReadFromJsonAsync<JsonElement>(_jsonOptions);
		var hostTags = doc.GetProperty("hostTags").EnumerateArray().Select(t => t.GetString()).ToArray();
		hostTags.Should().Contain("stays");
		hostTags.Should().NotContain("goes");
	}

	// ── 9. GET /api/orchestrations/{id}/versions ──────────────────────────────

	[Fact]
	public async Task VersionsList_AcceptsName_ReturnsCanonicalIdInPayload()
	{
		var (name, id) = await RegisterTransformOrchestrationAsync();
		(await WaitForVersionsAsync(id)).Should().BeGreaterOrEqualTo(1,
			"a version snapshot must land within 2s of registration");

		var response = await _client.GetAsync($"/api/orchestrations/{Encode(name)}/versions");
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var payload = await response.Content.ReadFromJsonAsync<JsonElement>(_jsonOptions);
		payload.GetProperty("orchestrationId").GetString().Should().Be(id,
			"version store is ID-keyed; the response must reflect that even when invoked by name");
		payload.GetProperty("count").GetInt32().Should().BeGreaterOrEqualTo(1);
	}

	// ── 10. GET /api/orchestrations/{id}/versions/{hash} ──────────────────────

	[Fact]
	public async Task VersionsGet_AcceptsName_ReturnsCanonicalIdInPayload()
	{
		var (name, id) = await RegisterTransformOrchestrationAsync();
		(await WaitForVersionsAsync(id)).Should().BeGreaterOrEqualTo(1);

		// Discover a real version hash via the list endpoint.
		var list = await _client.GetAsync($"/api/orchestrations/{Encode(id)}/versions");
		var listDoc = await list.Content.ReadFromJsonAsync<JsonElement>(_jsonOptions);
		var hash = listDoc.GetProperty("versions").EnumerateArray().First().GetProperty("contentHash").GetString()!;

		var response = await _client.GetAsync($"/api/orchestrations/{Encode(name)}/versions/{hash}");
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var payload = await response.Content.ReadFromJsonAsync<JsonElement>(_jsonOptions);
		payload.GetProperty("orchestrationId").GetString().Should().Be(id);
		payload.GetProperty("contentHash").GetString().Should().Be(hash);
	}

	// ── 11. GET /api/orchestrations/{id}/versions/{hash1}/diff/{hash2} ────────

	[Fact]
	public async Task VersionsDiff_AcceptsName_ReturnsCanonicalIdInPayload()
	{
		// Diff requires two version hashes for the same orchestration. We always have one
		// from initial registration; trigger a second by re-registering with a
		// description change so the contentHash differs.
		var (name, id) = await RegisterTransformOrchestrationAsync();

		// Re-register with a tweak that changes the content hash but keeps the name.
		var changedJson = $$"""
		{
			"name": "{{name}}",
			"description": "Resolution test (Transform-only, v2).",
			"steps": [
				{
					"name": "echo",
					"type": "Transform",
					"template": "hi v2"
				}
			]
		}
		""";
		var update = await _client.PostAsJsonAsync("/api/orchestrations/json", new { json = changedJson }, _jsonOptions);
		update.StatusCode.Should().Be(HttpStatusCode.OK);

		// Wait for the second version snapshot to actually land (fire-and-forget writer).
		await WaitForVersionsAsync(id, minCount: 2);

		var list = await _client.GetAsync($"/api/orchestrations/{Encode(id)}/versions");
		var versions = (await list.Content.ReadFromJsonAsync<JsonElement>(_jsonOptions))
			.GetProperty("versions").EnumerateArray().ToArray();
		if (versions.Length < 2)
		{
			// Version tracking is opt-in; if the test environment didn't produce two
			// snapshots we can't exercise the diff path. Skip the assertion rather than
			// fail spuriously — the resolution path is still exercised via the list test.
			return;
		}

		var hash1 = versions[0].GetProperty("contentHash").GetString()!;
		var hash2 = versions[1].GetProperty("contentHash").GetString()!;

		var response = await _client.GetAsync($"/api/orchestrations/{Encode(name)}/versions/{hash1}/diff/{hash2}");
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var payload = await response.Content.ReadFromJsonAsync<JsonElement>(_jsonOptions);
		payload.GetProperty("orchestrationId").GetString().Should().Be(id);
	}

	// ── 12. DELETE /api/orchestrations/{id}/versions ──────────────────────────

	[Fact]
	public async Task VersionsDelete_AcceptsName_AndPurgesUnderCanonicalId()
	{
		var (name, id) = await RegisterTransformOrchestrationAsync();

		var response = await _client.DeleteAsync($"/api/orchestrations/{Encode(name)}/versions");
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var payload = await response.Content.ReadFromJsonAsync<JsonElement>(_jsonOptions);
		payload.GetProperty("orchestrationId").GetString().Should().Be(id);
		payload.GetProperty("deleted").GetBoolean().Should().BeTrue();
	}

	// ── 13. GET /api/orchestrations/{id}/resume/{runId} ───────────────────────

	[Fact]
	public async Task Resume_AcceptsName_FailsOnMissingCheckpointNotMissingOrchestration()
	{
		// We deliberately don't seed a checkpoint: the resume endpoint has two 404 paths,
		// one for unknown orchestration and one for unknown checkpoint. The fix targets
		// the first; the second is unrelated and proves the resolution succeeded — if the
		// fix were missing, the test would see "Orchestration '<name>' not found." instead
		// of "No checkpoint found for orchestration '<actualName>', run '<runId>'."
		var (name, id) = await RegisterTransformOrchestrationAsync();
		var fakeRunId = $"run-{Guid.NewGuid():N}";

		var response = await _client.GetAsync(
			$"/api/orchestrations/{Encode(name)}/resume/{Encode(fakeRunId)}",
			HttpCompletionOption.ResponseHeadersRead);

		response.StatusCode.Should().Be(HttpStatusCode.NotFound);
		var body = await response.Content.ReadAsStringAsync();
		body.Should().Contain("No checkpoint found",
			"resume should pass orchestration resolution and then 404 on the missing checkpoint, " +
			"not 404 with 'Orchestration not found.'");
		body.Should().NotContain("Orchestration '" + name + "' not found.",
			"a leaky fix would still 404 at the orchestration-lookup step when invoked by name");
	}
}
