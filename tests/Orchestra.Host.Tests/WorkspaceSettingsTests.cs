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
		if (original.Contains("// trailing"))
			text.Should().Contain("// trailing, comment");
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

	[Fact]
	public async Task ClearingRetention_ResetsLiveDefaultsAndPersistsTheReset()
	{
		var host = new OrchestrationHostOptions { DataPath = _root };
		host.Retention.MaxRunAgeDays = 30;
		var path = Path.Combine(_root, "orchestra.json");
		await File.WriteAllTextAsync(path, "{\"retention\":{\"maxRunAgeDays\":30}}");
		var store = new WorkspaceSettingsStore(new() { ConfigPath = path }, host, NullLogger<WorkspaceSettingsStore>.Instance);
		await store.SaveAsync(new(store.Get().Revision, new() { ["retention"] = null }), CancellationToken.None);
		host.Retention.MaxRunAgeDays.Should().BeNull();
	}

	[Theory]
	[InlineData("{\"urls\":\"ftp://localhost:5000\"}")]
	[InlineData("{\"urls\":\"http://localhost:5000/private\"}")]
	[InlineData("{\"defaultMaxConcurrency\":2147483648}")]
	[InlineData("{\"defaultReasoningLevel\":\"199\"}")]
	[InlineData("{\"scan\":{\"watch\":\"true\"}}")]
	[InlineData("{\"retention\":{\"maxRunAgeDays\":-1}}")]
	public async Task InvalidSettings_FailPreviewAndSaveWithoutChangingTheFile(string patch)
	{
		var path = Path.Combine(_root, "orchestra.json");
		await File.WriteAllTextAsync(path, "{\"defaultModel\":\"original\"}");
		var store = new WorkspaceSettingsStore(new() { ConfigPath = path }, new() { DataPath = _root }, NullLogger<WorkspaceSettingsStore>.Instance);
		var before = await File.ReadAllTextAsync(path);
		var update = new SettingsUpdate(store.Get().Revision, JsonNode.Parse(patch)!.AsObject());
		var preview = () => store.Preview(update);
		preview.Should().Throw<ArgumentException>();
		var save = () => store.SaveAsync(update, CancellationToken.None);
		await save.Should().ThrowAsync<ArgumentException>();
		(await File.ReadAllTextAsync(path)).Should().Be(before);
	}

	[Fact]
	public async Task EnvironmentModel_IsResolvedLiveAndRetainedAsAReferenceOnDisk()
	{
		var variable = "ORCHESTRA_TEST_MODEL_" + Guid.NewGuid().ToString("N");
		Environment.SetEnvironmentVariable(variable, "chosen-model");
		try
		{
			var path = Path.Combine(_root, "orchestra.json");
			var host = new OrchestrationHostOptions { DataPath = _root };
			var store = new WorkspaceSettingsStore(new() { ConfigPath = path }, host, NullLogger<WorkspaceSettingsStore>.Instance);
			var update = new SettingsUpdate(store.Get().Revision, new() { ["defaultModel"] = "env:" + variable });
			store.Preview(update);
			var saved = await store.SaveAsync(update, CancellationToken.None);
			host.DefaultModel.Should().Be("chosen-model");
			saved.Values["defaultModel"]!.GetValue<string>().Should().Be("env:" + variable);
			saved.Sources!["defaultModel"].Should().Contain("environment");
		}
		finally { Environment.SetEnvironmentVariable(variable, null); }
	}

	[Fact]
	public async Task EnablingWatch_InAnEmptyConfig_IncludesTheActiveWorkspaceRoot()
	{
		var path = Path.Combine(_root, "orchestra.json");
		await File.WriteAllTextAsync(path, "{}");
		var store = new WorkspaceSettingsStore(new() { ConfigPath = path }, new() { DataPath = _root }, NullLogger<WorkspaceSettingsStore>.Instance);
		var result = await store.SaveAsync(new(store.Get().Revision, new() { ["scan"] = new JsonObject { ["watch"] = true } }), CancellationToken.None);
		result.Values["scan"]!["directory"]!.GetValue<string>().Should().Be(store.WorkspaceDirectory);
		result.RestartRequired.Should().Contain("scan");
	}

	[Fact]
	public async Task TwoStoresEditingTheSameRevision_HaveOneWinner()
	{
		var path = Path.Combine(_root, "orchestra.json");
		await File.WriteAllTextAsync(path, "{}");
		var stores = Enumerable.Range(0, 2).Select(_ => new WorkspaceSettingsStore(new() { ConfigPath = path }, new() { DataPath = _root }, NullLogger<WorkspaceSettingsStore>.Instance)).ToArray();
		var revision = stores[0].Get().Revision;
		var outcomes = await Task.WhenAll(stores.Select(async (store, i) =>
		{
			try
			{ await store.SaveAsync(new(revision, new() { ["defaultModel"] = "model-" + i }), CancellationToken.None); return true; }
			catch (WorkspaceConflictException) { return false; }
		}));
		outcomes.Should().ContainSingle(success => success);
	}
}
