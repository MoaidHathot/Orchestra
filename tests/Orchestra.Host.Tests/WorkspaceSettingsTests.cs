using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Host.Hosting;
using Orchestra.Host.Workspaces;
using Xunit;

namespace Orchestra.Host.Tests;

public class WorkspaceSettingsTests : IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("orchestra-settings-").FullName;
	public void Dispose() => Directory.Delete(_root, true);

	[Theory]
	[InlineData("{ // top\n \"future\": {\"unchanged\":true}, /* trailing, comment */\n}")]
	[InlineData("{\"future\": {\"unchanged\":true} // trailing, comment\n}")]
	[InlineData("{\"future\":{\"unchanged\":true}}")]
	public async Task Save_PreservesCommentsUnknownFieldsAndEnvironmentReferences(string original)
	{
		var path = Path.Combine(_root, "orchestra.json");
		original = JsoncEditor.Patch(original, new() { ["copilot"] = new JsonObject { ["gitHubToken"] = "${TOKEN}" }, ["retention"] = new JsonObject { ["maxRunAgeDays"] = 30, ["futureOption"] = true } });
		await File.WriteAllTextAsync(path, original);
		var host = new OrchestrationHostOptions { DataPath = _root };
		var store = new WorkspaceSettingsStore(new() { ConfigPath = path }, host, NullLogger<WorkspaceSettingsStore>.Instance);
		var result = await store.SaveAsync(new(store.Get().Revision, new() { ["defaultModel"] = "test-model", ["retention"] = new JsonObject { ["maxRunAgeDays"] = 10 } }), CancellationToken.None);
		var text = await File.ReadAllTextAsync(path);
		text.Should().Contain("${TOKEN}").And.Contain("futureOption").And.Contain("unchanged");
		if (original.Contains("// trailing")) text.Should().Contain("// trailing, comment");
		result.Values.ToJsonString().Should().NotContain("TOKEN");
		host.DefaultModel.Should().Be("test-model");
		host.Retention.MaxRunAgeDays.Should().Be(10);
	}

	[Fact]
	public async Task ExternalEdits_ConflictInsteadOfOverwriting()
	{
		var path = Path.Combine(_root, "orchestra.json");
		await File.WriteAllTextAsync(path, "{}");
		var store = new WorkspaceSettingsStore(new() { ConfigPath = path }, new() { DataPath = _root }, NullLogger<WorkspaceSettingsStore>.Instance);
		var revision = store.Get().Revision;
		await File.WriteAllTextAsync(path, "{\"future\":42}");
		var act = () => store.SaveAsync(new(revision, new() { ["defaultModel"] = "new" }), CancellationToken.None);
		await act.Should().ThrowAsync<WorkspaceConflictException>();
		(await File.ReadAllTextAsync(path)).Should().Be("{\"future\":42}");
	}

	[Fact]
	public async Task RestartSettings_ArePersistedWithoutChangingLiveStorage()
	{
		var path = Path.Combine(_root, "orchestra.json");
		await File.WriteAllTextAsync(path, "{}");
		var host = new OrchestrationHostOptions { DataPath = _root };
		var store = new WorkspaceSettingsStore(new() { ConfigPath = path, Overrides = new() { ["dataPath"] = "--data-path" } }, host, NullLogger<WorkspaceSettingsStore>.Instance);
		var updated = await store.SaveAsync(new(store.Get().Revision, new() { ["dataPath"] = Path.Combine(_root, "next") }), CancellationToken.None);
		updated.RestartRequired.Should().Contain("dataPath");
		updated.Overrides.Should().ContainKey("dataPath");
		host.DataPath.Should().Be(_root);
	}
}
