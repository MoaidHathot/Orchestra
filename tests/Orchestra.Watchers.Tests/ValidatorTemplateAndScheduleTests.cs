using System.Text.Json.Nodes;
using FluentAssertions;
using Orchestra.Watchers.Runtime;
using Xunit;

namespace Orchestra.Watchers.Tests;

public class WatcherOutputValidatorTests
{
	private readonly WatcherOutputValidator _validator = new();

	[Fact]
	public void Valid_V1Output_Passes()
	{
		var output = JsonNode.Parse("""
			{ "protocolVersion": "1", "items": [ { "id": "a" }, 2, "three" ], "cursor": { "lastEventId": 4 }, "diagnostics": { "message": "ok", "details": { "n": 1 } } }
			""");

		_validator.TryValidate(output, out var errors).Should().BeTrue(string.Join("; ", errors));
		errors.Should().BeEmpty();
	}

	[Theory]
	[InlineData("""{ }""")]
	[InlineData("""{ "items": null }""")]
	[InlineData("""{ "items": [] }""")]
	[InlineData("""{ "cursor": null }""")]
	[InlineData("""{ "cursor": "opaque-string" }""")]
	[InlineData("""{ "items": [ {"id":"x"} ] }""")]
	public void MissingOrNullItemsAndCursor_AreOk(string json)
	{
		_validator.TryValidate(JsonNode.Parse(json), out var errors).Should().BeTrue(string.Join("; ", errors));
	}

	[Fact]
	public void WrongTypeOnItems_FailsWithPointer()
	{
		var output = JsonNode.Parse("""{ "protocolVersion": "1", "items": { "not": "an array" } }""");

		_validator.TryValidate(output, out var errors).Should().BeFalse();

		errors.Should().Contain(e => e.JsonPointer == "/items" && e.Keyword == "type");
	}

	[Fact]
	public void UnknownProtocolVersion_ReturnsSpecificError_WithoutSchemaNoise()
	{
		var output = JsonNode.Parse("""{ "protocolVersion": "2", "items": "garbage" }""");

		_validator.TryValidate(output, out var errors).Should().BeFalse();

		errors.Should().ContainSingle().Which.Should().Match<WatcherValidationError>(e =>
			e.JsonPointer == "/protocolVersion" && e.Keyword == "protocolVersion" && e.Message.Contains("unsupported protocolVersion: 2"));
	}

	[Fact]
	public void UnknownTopLevelProperty_IsRejected()
	{
		var output = JsonNode.Parse("""{ "items": [], "hasChanges": false }""");

		_validator.TryValidate(output, out var errors).Should().BeFalse();

		errors.Should().Contain(e => e.JsonPointer == "/hasChanges" && e.Message.Contains("not allowed"));
	}

	[Theory]
	[InlineData("[]")]
	[InlineData("\"text\"")]
	[InlineData("null")]
	public void NonObjectOutput_FailsAtRoot(string json)
	{
		_validator.TryValidate(JsonNode.Parse(json), out var errors).Should().BeFalse();
		errors.Should().ContainSingle().Which.JsonPointer.Should().Be("/");
	}

	[Fact]
	public void EmbeddedSchemas_MatchTheCheckedInFiles()
	{
		var root = TestEntries.RepoRoot();

		Normalize(WatcherOutputValidator.OutputSchemaText).Should().Be(Normalize(File.ReadAllText(Path.Combine(root, "schemas", "watchers", "v1", "fetcher-output.schema.json"))));
		Normalize(WatcherOutputValidator.InputSchemaText).Should().Be(Normalize(File.ReadAllText(Path.Combine(root, "schemas", "watchers", "v1", "fetcher-input.schema.json"))));

		static string Normalize(string text) => text.Replace("\r\n", "\n").Trim();
	}

	[Fact]
	public void FetchRequest_ConformsToTheInputSchema()
	{
		var schema = Json.Schema.JsonSchema.FromText(WatcherOutputValidator.InputSchemaText);
		var entry = TestEntries.Command("github-pr-comments");
		var request = WatcherFetchRequest.Create(entry, cursor: null, ["orch-a", "orch-b"]);

		var results = schema.Evaluate(JsonNode.Parse(request.ToJson()), new Json.Schema.EvaluationOptions { OutputFormat = Json.Schema.OutputFormat.List });

		results.IsValid.Should().BeTrue(string.Join("; ", results.Details.Where(d => d.HasErrors).SelectMany(d => d.Errors!.Values)));
		request.ToJson().Should().Contain("\"cursor\":null").And.Contain("\"config\":null", "nulls are explicit so fetchers can rely on the keys");
	}
}

public class WatcherTemplateRendererTests
{
	private static readonly JsonNode s_item = JsonNode.Parse("""
		{ "id": "c-1", "author": { "login": "octocat", "id": 583231 }, "labels": ["bug", "p1"], "body": "He said \"hi\"", "active": true, "score": 4.5, "nothing": null }
		""")!;

	[Theory]
	[InlineData("{{item.id}}", "c-1")]
	[InlineData("{{ item.author.login }}", "octocat")]
	[InlineData("{{item.author.id}}", "583231")]
	[InlineData("{{item.labels[1]}}", "p1")]
	[InlineData("{{item['author']['login']}}", "octocat")]
	[InlineData("{{item.active}}", "true")]
	[InlineData("{{item.score}}", "4.5")]
	[InlineData("{{item.body}}", "He said \"hi\"")]
	[InlineData("comment {{item.id}} by {{item.author.login}}", "comment c-1 by octocat")]
	[InlineData("no placeholders", "no placeholders")]
	public void Render_SubstitutesNestedFields(string template, string expected)
	{
		WatcherTemplateRenderer.Render(template, s_item).Should().Be(expected);
	}

	[Fact]
	public void Render_WholeItem_ProducesCompactJson()
	{
		var rendered = WatcherTemplateRenderer.Render("{{item}}", s_item);

		JsonNode.Parse(rendered)!.ToJsonString().Should().Be(s_item.ToJsonString());
		WatcherTemplateRenderer.Render("{{item.author}}", s_item).Should().Be("""{"login":"octocat","id":583231}""");
		WatcherTemplateRenderer.Render("{{item.labels}}", s_item).Should().Be("""["bug","p1"]""");
	}

	[Theory]
	[InlineData("{{item.missing}}")]
	[InlineData("{{item.author.missing.deeper}}")]
	[InlineData("{{item.labels[9]}}")]
	[InlineData("{{item.nothing}}")]
	[InlineData("{{other.thing}}")]
	[InlineData("{{}}")]
	[InlineData("{{item.labels.x}}")]
	public void Render_MissingPath_RendersLiteralNull_NeverThrows(string template)
	{
		WatcherTemplateRenderer.Render(template, s_item).Should().Be("null");
		WatcherTemplateRenderer.Render(template, null).Should().Be("null");
	}

	[Fact]
	public void RenderAll_AppliesToEveryParameterValue()
	{
		var parameters = new Dictionary<string, string> { ["comment"] = "{{item}}", ["login"] = "{{item.author.login}}", ["fixed"] = "x" };

		var rendered = WatcherTemplateRenderer.RenderAll(parameters, s_item);

		rendered["login"].Should().Be("octocat");
		rendered["fixed"].Should().Be("x");
		JsonNode.Parse(rendered["comment"])!["id"]!.GetValue<string>().Should().Be("c-1");
		WatcherTemplateRenderer.RenderAll(null, s_item).Should().BeEmpty();
	}

	[Fact]
	public void CanonicalHash_IsOrderIndependent_AndStable()
	{
		var a = JsonNode.Parse("""{ "b": 1, "a": { "y": [1, 2], "x": "s" } }""");
		var b = JsonNode.Parse("""{ "a": { "x": "s", "y": [1, 2] }, "b": 1 }""");
		var c = JsonNode.Parse("""{ "a": { "x": "s", "y": [2, 1] }, "b": 1 }""");

		WatcherTemplateRenderer.CanonicalHash(a).Should().Be(WatcherTemplateRenderer.CanonicalHash(b)).And.HaveLength(16);
		WatcherTemplateRenderer.CanonicalHash(a).Should().NotBe(WatcherTemplateRenderer.CanonicalHash(c), "array order is significant");
		WatcherTemplateRenderer.Canonicalize(a).Should().Be("""{"a":{"x":"s","y":[1,2]},"b":1}""");
	}
}

public class WatcherItemPathTests
{
	private static readonly JsonNode s_doc = JsonNode.Parse("""
		{ "items": [ { "id": 1 }, { "id": 2 } ], "data": { "results": [ "a", "b" ], "single": { "id": 9 }, "none": null }, "top": [1, 2, 3] }
		""")!;

	[Theory]
	[InlineData("$.items[*]", 2)]
	[InlineData("$.items", 2)]
	[InlineData("$['items'][*]", 2)]
	[InlineData("$.data.results[*]", 2)]
	[InlineData("$.data.results[1]", 1)]
	[InlineData("$.data.single", 1)]
	[InlineData("$.data.none", 0)]
	[InlineData("$.missing[*]", 0)]
	[InlineData("$.missing", 0)]
	[InlineData("$.top", 3)]
	[InlineData("$", 1)]
	public void Select_ReturnsExpectedItemCount(string path, int expected)
	{
		WatcherItemPath.Parse(path).Select(s_doc).Should().HaveCount(expected);
	}

	[Fact]
	public void Select_WildcardAfterWildcard_FlattensOneLevel()
	{
		var doc = JsonNode.Parse("""{ "pages": [ { "items": [1, 2] }, { "items": [3] } ] }""");

		var items = WatcherItemPath.Parse("$.pages[*].items[*]").Select(doc);

		items.Select(i => i!.GetValue<int>()).Should().Equal(1, 2, 3);
	}

	[Theory]
	[InlineData("items[*]", "must start with")]
	[InlineData("$..items", "recursive descent")]
	[InlineData("$.items[?(@.id)]", "unsupported bracket")]
	[InlineData("$.items[", "unterminated")]
	[InlineData("$.*", "not supported")]
	[InlineData("$.items.", "expected a property name")]
	public void Parse_RejectsUnsupportedSyntax(string path, string fragment)
	{
		var act = () => WatcherItemPath.Parse(path);

		act.Should().Throw<FormatException>().Which.Message.Should().Contain(fragment);
	}
}

public class WatcherScheduleCalculatorTests
{
	[Theory]
	[InlineData(0, 0)]
	[InlineData(1, 2)]
	[InlineData(2, 4)]
	[InlineData(3, 8)]
	[InlineData(8, 256)]
	[InlineData(9, 300)]
	[InlineData(40, 300)]
	public void Backoff_IsExponentialFrom2s_CappedAt300s(int failures, int expectedSeconds)
	{
		WatcherScheduleCalculator.Backoff(failures).Should().Be(TimeSpan.FromSeconds(expectedSeconds));
	}

	[Fact]
	public void NextTick_Interval_FiresImmediatelyFirst_ThenAnchorsOnPreviousTickStart()
	{
		var schedule = new Models.WatcherSchedule { IntervalSeconds = 15 };
		var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

		WatcherScheduleCalculator.NextTick(schedule, now, lastTickStart: null).Should().Be(now);
		WatcherScheduleCalculator.NextTick(schedule, now, lastTickStart: now.AddSeconds(-5)).Should().Be(now.AddSeconds(10));
		WatcherScheduleCalculator.NextTick(schedule, now, lastTickStart: now.AddSeconds(-50)).Should().Be(now, "overdue ticks fire now, not in the past");
	}

	[Fact]
	public void NextTickWithBackoff_PushesOutByBackoff_ButNeverBeforeSchedule()
	{
		var schedule = new Models.WatcherSchedule { IntervalSeconds = 1 };
		var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

		WatcherScheduleCalculator.NextTickWithBackoff(schedule, now, now, consecutiveFailures: 0).Should().Be(now.AddSeconds(1));
		WatcherScheduleCalculator.NextTickWithBackoff(schedule, now, now, consecutiveFailures: 1).Should().Be(now.AddSeconds(2));
		WatcherScheduleCalculator.NextTickWithBackoff(schedule, now, now, consecutiveFailures: 2).Should().Be(now.AddSeconds(4));
		WatcherScheduleCalculator.NextTickWithBackoff(schedule, now, now, consecutiveFailures: 20).Should().Be(now.AddSeconds(300));

		var slow = new Models.WatcherSchedule { IntervalSeconds = 600 };
		WatcherScheduleCalculator.NextTickWithBackoff(slow, now, now, consecutiveFailures: 1).Should().Be(now.AddSeconds(600), "the schedule is still the floor");
	}

	[Fact]
	public void NextTick_Cron_SupportsFiveAndSixFields_InUtc()
	{
		var now = DateTimeOffset.Parse("2026-01-01T00:00:10Z");

		WatcherScheduleCalculator.NextTick(new Models.WatcherSchedule { Cron = "*/5 * * * *" }, now, null).Should().Be(DateTimeOffset.Parse("2026-01-01T00:05:00Z"));
		WatcherScheduleCalculator.NextTick(new Models.WatcherSchedule { Cron = "*/30 * * * * *" }, now, null).Should().Be(DateTimeOffset.Parse("2026-01-01T00:00:30Z"));
	}

	[Fact]
	public void ParseCron_Invalid_Throws()
	{
		var act = () => WatcherScheduleCalculator.ParseCron("every 5 minutes");

		act.Should().Throw<Cronos.CronFormatException>();
	}
}

public class TrimRateTrackerTests
{
	[Fact]
	public void Record_WarnsOnceWhenRateExceedsTenPercentPerHour_AndNotBefore()
	{
		var tracker = new TrimRateTracker();
		var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

		tracker.Record(5, maxEntries: 100, now, out _).Should().BeFalse("5/hr is below 10 of 100");
		tracker.Record(5, maxEntries: 100, now.AddMinutes(1), out var rate).Should().BeFalse("10/hr equals the threshold, not above");
		rate.Should().Be(10);
		tracker.Record(1, maxEntries: 100, now.AddMinutes(2), out rate).Should().BeTrue();
		rate.Should().Be(11);
		tracker.Record(50, maxEntries: 100, now.AddMinutes(3), out _).Should().BeFalse("at most one warning per hour");
		tracker.Record(50, maxEntries: 100, now.AddMinutes(70), out _).Should().BeTrue("a new hour may warn again");
	}

	[Fact]
	public void Record_ForgetsEvictionsOlderThanAnHour()
	{
		var tracker = new TrimRateTracker();
		var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

		tracker.Record(50, maxEntries: 100, now, out _);
		tracker.Record(0, maxEntries: 100, now.AddMinutes(61), out var rate).Should().BeFalse();
		rate.Should().Be(0);
	}
}
