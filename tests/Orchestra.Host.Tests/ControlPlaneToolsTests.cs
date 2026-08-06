using FluentAssertions;
using Orchestra.Engine;
using Orchestra.Host.McpServer;
using Orchestra.Host.Persistence;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// Tests for <see cref="ControlPlaneTools.ListRuns"/> covering the lineage projection and
/// the new <c>parentExecutionId</c> / <c>rootExecutionId</c> filter parameters that allow
/// admin callers to scope the listing to a subtree, mirroring the data-plane
/// <c>list_child_runs</c> tool.
/// </summary>
public class ControlPlaneToolsTests
{
	[Fact]
	public async Task ListRuns_ExposesParentAndRootIds()
	{
		// Without lineage projection, admin views can't walk parent → child chains without
		// fetching each run individually. Asserts the new fields are surfaced verbatim from
		// the RunIndex.
		using var store = await TempRunStore.WithChildrenOfRootAsync("root-LR1",
			(executionId: "kid-1", parent: "root-LR1", root: "root-LR1", orchestrationName: "child-orch"));

		var json = await ControlPlaneTools.ListRuns(store.Store, store.Annotations, limit: 20);
		using var doc = System.Text.Json.JsonDocument.Parse(json);

		var run = doc.RootElement.GetProperty("runs").EnumerateArray()
			.First(r => r.GetProperty("runId").GetString() == "kid-1");
		run.GetProperty("parentExecutionId").GetString().Should().Be("root-LR1");
		run.GetProperty("rootExecutionId").GetString().Should().Be("root-LR1");
		run.GetProperty("nestingDepth").GetInt32().Should().Be(1);
		run.GetProperty("parentStepName").GetString().Should().Be("invoke");
	}

	[Fact]
	public async Task ListRuns_FilterByRoot_ReturnsOnlyMatchingSubtree()
	{
		using var store = await TempRunStore.WithChildrenOfRootAsync("root-LR2",
			(executionId: "in-subtree-1", parent: "root-LR2", root: "root-LR2", orchestrationName: "c1"),
			(executionId: "in-subtree-2", parent: "in-subtree-1", root: "root-LR2", orchestrationName: "c2"),
			(executionId: "unrelated", parent: "other-root", root: "other-root", orchestrationName: "c3"));

		var json = await ControlPlaneTools.ListRuns(store.Store, store.Annotations, rootExecutionId: "root-LR2");
		using var doc = System.Text.Json.JsonDocument.Parse(json);

		doc.RootElement.GetProperty("count").GetInt32().Should().Be(2,
			"only runs whose RootExecutionId matches the filter should be returned");
		var ids = doc.RootElement.GetProperty("runs").EnumerateArray()
			.Select(r => r.GetProperty("runId").GetString()).ToHashSet();
		ids.Should().BeEquivalentTo(new[] { "in-subtree-1", "in-subtree-2" });
	}

	[Fact]
	public async Task ListRuns_FilterByParent_ReturnsOnlyDirectChildren()
	{
		using var store = await TempRunStore.WithChildrenOfRootAsync("root-LR3",
			(executionId: "direct-child", parent: "root-LR3", root: "root-LR3", orchestrationName: "c1"),
			(executionId: "grandchild",   parent: "direct-child", root: "root-LR3", orchestrationName: "c2"));

		var json = await ControlPlaneTools.ListRuns(store.Store, store.Annotations, parentExecutionId: "root-LR3");
		using var doc = System.Text.Json.JsonDocument.Parse(json);

		doc.RootElement.GetProperty("count").GetInt32().Should().Be(1,
			"parentExecutionId filter returns direct children only, not the whole subtree");
		doc.RootElement.GetProperty("runs")[0].GetProperty("runId").GetString().Should().Be("direct-child");
	}
}
