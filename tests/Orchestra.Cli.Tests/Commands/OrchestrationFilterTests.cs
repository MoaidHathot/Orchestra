using System.Text.Json;
using FluentAssertions;
using Orchestra.Cli.Commands;
using Xunit;

namespace Orchestra.Cli.Tests.Commands;

/// <summary>
/// Unit tests for the client-side <see cref="OrchestrationFilter"/>. We do this client-side
/// because the Host's <c>GET /api/orchestrations</c> returns the full registry every time
/// (no server-side filter parameters today). These tests pin the matching rules so future
/// edits don't silently widen or narrow user-visible behaviour.
/// </summary>
public class OrchestrationFilterTests
{
	private static JsonElement Envelope(params string[] jsonObjects)
	{
		var combined = "{\"count\":" + jsonObjects.Length + ",\"orchestrations\":[" + string.Join(",", jsonObjects) + "]}";
		return JsonSerializer.Deserialize<JsonElement>(combined);
	}

	[Fact]
	public void Apply_EmptyCriteria_ReturnsOriginal()
	{
		var input = Envelope("""{"id":"a","name":"alpha"}""");

		var output = OrchestrationFilter.Apply(input, new OrchestrationFilter.Criteria());

		// When nothing to filter, must not allocate a new envelope (preserves any extra
		// envelope properties the server might add later).
		output.GetRawText().Should().Be(input.GetRawText());
	}

	[Fact]
	public void Apply_FilterByName_CaseInsensitiveSubstring()
	{
		var input = Envelope(
			"""{"id":"a","name":"DeployPipeline","description":"prod release"}""",
			"""{"id":"b","name":"research","description":"data prep"}""",
			"""{"id":"c","name":"other","description":"deploy hook"}""");

		var output = OrchestrationFilter.Apply(input, new OrchestrationFilter.Criteria(Filter: "deploy"));

		output.GetProperty("count").GetInt32().Should().Be(2);
		var ids = output.GetProperty("orchestrations").EnumerateArray()
			.Select(o => o.GetProperty("id").GetString()).ToArray();
		ids.Should().BeEquivalentTo(new[] { "a", "c" },
			"the substring matches the name field of 'a' and the description of 'c'");
	}

	[Fact]
	public void Apply_FilterByPath_AlsoMatches()
	{
		var input = Envelope(
			"""{"id":"a","name":"x","path":"./orchestrations/nightly.yaml"}""",
			"""{"id":"b","name":"y","path":"./orchestrations/adhoc.json"}""");

		var output = OrchestrationFilter.Apply(input, new OrchestrationFilter.Criteria(Filter: "nightly"));

		output.GetProperty("count").GetInt32().Should().Be(1);
		output.GetProperty("orchestrations")[0].GetProperty("id").GetString().Should().Be("a");
	}

	[Fact]
	public void Apply_FilterByTags_RequiresAll()
	{
		var input = Envelope(
			"""{"id":"a","tags":["prod","nightly"]}""",
			"""{"id":"b","tags":["prod"]}""",
			"""{"id":"c","tags":["staging","nightly"]}""");

		var output = OrchestrationFilter.Apply(input, new OrchestrationFilter.Criteria(Tags: new[] { "prod", "nightly" }));

		output.GetProperty("count").GetInt32().Should().Be(1);
		output.GetProperty("orchestrations")[0].GetProperty("id").GetString().Should().Be("a",
			"only 'a' carries BOTH tags; --tag is AND, not OR");
	}

	[Fact]
	public void Apply_FilterByTags_IsCaseInsensitiveAndTolerantOfWhitespace()
	{
		var input = Envelope("""{"id":"a","tags":["Prod","nightly"]}""");

		var output = OrchestrationFilter.Apply(input, new OrchestrationFilter.Criteria(Tags: new[] { "  PROD  " }));

		output.GetProperty("count").GetInt32().Should().Be(1);
	}

	[Fact]
	public void Apply_FilterByEnabled_True_KeepsOnlyEnabled()
	{
		var input = Envelope(
			"""{"id":"a","enabled":true}""",
			"""{"id":"b","enabled":false}""",
			"""{"id":"c","enabled":true}""");

		var output = OrchestrationFilter.Apply(input, new OrchestrationFilter.Criteria(Enabled: true));

		output.GetProperty("count").GetInt32().Should().Be(2);
	}

	[Fact]
	public void Apply_FilterByEnabled_False_KeepsOnlyDisabled()
	{
		var input = Envelope(
			"""{"id":"a","enabled":true}""",
			"""{"id":"b","enabled":false}""");

		var output = OrchestrationFilter.Apply(input, new OrchestrationFilter.Criteria(Enabled: false));

		output.GetProperty("count").GetInt32().Should().Be(1);
		output.GetProperty("orchestrations")[0].GetProperty("id").GetString().Should().Be("b");
	}

	[Fact]
	public void Apply_FilterByEnabled_MissingField_ExcludesItem()
	{
		// Defensive: if the server stops emitting `enabled` for some entries, we must not
		// silently include them when the user asked for a specific state.
		var input = Envelope(
			"""{"id":"a"}""",
			"""{"id":"b","enabled":true}""");

		var output = OrchestrationFilter.Apply(input, new OrchestrationFilter.Criteria(Enabled: true));

		output.GetProperty("count").GetInt32().Should().Be(1);
		output.GetProperty("orchestrations")[0].GetProperty("id").GetString().Should().Be("b");
	}

	[Fact]
	public void Apply_Combined_AllPredicatesAnded()
	{
		var input = Envelope(
			"""{"id":"a","name":"deploy-prod","tags":["prod"],"enabled":true}""",
			"""{"id":"b","name":"deploy-stage","tags":["prod"],"enabled":false}""",
			"""{"id":"c","name":"deploy-prod","tags":["staging"],"enabled":true}""");

		var output = OrchestrationFilter.Apply(input,
			new OrchestrationFilter.Criteria(Filter: "deploy", Tags: new[] { "prod" }, Enabled: true));

		output.GetProperty("count").GetInt32().Should().Be(1);
		output.GetProperty("orchestrations")[0].GetProperty("id").GetString().Should().Be("a",
			"only 'a' satisfies name~deploy AND tag=prod AND enabled=true");
	}

	[Fact]
	public void Apply_AcceptsBareArrayResponse()
	{
		// Some legacy/test shapes return the array directly without an envelope. The filter
		// must still work and re-wrap into the canonical envelope so downstream renderers
		// (jq pipelines, the table writer) behave consistently.
		var input = JsonSerializer.Deserialize<JsonElement>("""[{"id":"a","name":"alpha"},{"id":"b","name":"beta"}]""");

		var output = OrchestrationFilter.Apply(input, new OrchestrationFilter.Criteria(Filter: "alpha"));

		output.GetProperty("count").GetInt32().Should().Be(1);
		output.GetProperty("orchestrations")[0].GetProperty("id").GetString().Should().Be("a");
	}

	[Fact]
	public void Apply_UnknownShape_ReturnsAsIs()
	{
		// If the server response doesn't look like an envelope or array (e.g. a typed error),
		// we must not eat it — return it unchanged so the user sees it.
		var input = JsonSerializer.Deserialize<JsonElement>("""{"error":"boom"}""");

		var output = OrchestrationFilter.Apply(input, new OrchestrationFilter.Criteria(Filter: "x"));

		output.GetRawText().Should().Be(input.GetRawText());
	}
}
