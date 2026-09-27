using FluentAssertions;
using System.Reflection;
using Orchestra.Host.Hosting;
using System.Text.Json;
using Xunit;

namespace Orchestra.Tool.Tests;

/// <summary>
/// Pins schema facts that the authoring skill and generated orchestrations depend on.
/// </summary>
/// <remarks>
/// Every assertion here corresponds to a documented behaviour that had drifted from the schema:
/// a hook event the runtime supports but the schema rejected, a config key the loader honours
/// but the schema forbade, and defaults the skill stated incorrectly. Divergence between these
/// three is invisible until someone's editor flags a valid file, so it is worth pinning.
/// </remarks>
public class SchemaContractTests
{
	private static JsonDocument Load(string fileName)
	{
		var path = Path.Combine(SchemaDirectory(), fileName);
		File.Exists(path).Should().BeTrue($"schema '{fileName}' must ship in the tool output");
		return JsonDocument.Parse(File.ReadAllText(path));
	}

	[Theory]
	[InlineData("orchestration.schema.json")]
	[InlineData("orchestra.schema.json")]
	[InlineData("orchestra.mcp.schema.json")]
	[InlineData("orchestra.services.schema.json")]
	[InlineData("orchestra.watchers.schema.json")]
	[InlineData("watchers/v1/fetcher-input.schema.json")]
	[InlineData("watchers/v1/fetcher-output.schema.json")]
	public void Schema_IsWellFormedJson(string fileName)
	{
		// Guards against a hand-edit dropping a brace: the schemas are edited as text far more
		// often than they are parsed by anything in the build.
		var act = () => Load(fileName).Dispose();

		act.Should().NotThrow();
	}

	[Fact]
	public void TriggerSchema_DeclaresWatcherTriggerType_WithReferenceOrInlineDefinition()
	{
		// Every TriggerType the parser accepts must be schema-valid, or an editor flags a working file.
		using var doc = Load("orchestration.schema.json");
		var trigger = doc.RootElement.GetProperty("$defs").GetProperty("trigger");
		var typeEnum = trigger.GetProperty("properties").GetProperty("type").GetProperty("enum")
			.EnumerateArray().Select(e => e.GetString()).ToHashSet(StringComparer.Ordinal);

		foreach (var name in Enum.GetNames<Orchestra.Engine.TriggerType>())
			typeEnum.Should().Contain(JsonNamingPolicy.CamelCase.ConvertName(name), $"TriggerType.{name} is parsed by the engine but missing from the schema enum");

		var watcherBranch = trigger.GetProperty("allOf").EnumerateArray()
			.Single(b => b.GetProperty("if").GetProperty("properties").GetProperty("type").GetProperty("enum").EnumerateArray().Any(e => e.GetString() == "watcher"))
			.GetProperty("then");
		var props = watcherBranch.GetProperty("properties");
		foreach (var key in new[] { "watcher", "definition", "maxConcurrent", "catchup", "parameters" })
			props.TryGetProperty(key, out _).Should().BeTrue($"watcher trigger property '{key}' must be declared");

		watcherBranch.TryGetProperty("oneOf", out _).Should().BeTrue("exactly one of watcher / definition is required");
		doc.RootElement.GetProperty("$defs").GetProperty("watcherInlineDefinition").GetProperty("required")
			.EnumerateArray().Select(e => e.GetString()).Should().Contain("name", "inline definitions need a stable identity for state files");
	}

	[Fact]
	public void WatchersSchema_DeclaresBothWatcherKinds_AndMutualExclusions()
	{
		using var doc = Load("orchestra.watchers.schema.json");
		var defs = doc.RootElement.GetProperty("$defs");

		defs.GetProperty("PollCommandWatcher").GetRawText().Should().Contain("poll-command");
		defs.GetProperty("PollScriptWatcher").GetRawText().Should().Contain("poll-script");
		defs.GetProperty("WatcherSchedule").TryGetProperty("oneOf", out _).Should().BeTrue("intervalSeconds xor cron");
		defs.GetProperty("WatcherDedup").GetProperty("properties").GetProperty("mode").GetProperty("enum")
			.EnumerateArray().Select(e => e.GetString()).Should().BeEquivalentTo("fifo", "presence");
		defs.GetProperty("WatcherCommonProperties").GetProperty("properties").GetProperty("failurePolicy").GetProperty("enum")
			.EnumerateArray().Select(e => e.GetString()).Should().BeEquivalentTo("warn", "ignore", "fail");
	}

	[Fact]
	public void FetcherOutputSchema_RejectsUnknownTopLevelKeys()
	{
		// The protocol is closed on purpose: a fetcher that emits "hasChanges" or a typo must fail loudly.
		using var doc = Load("watchers/v1/fetcher-output.schema.json");

		doc.RootElement.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
		doc.RootElement.GetProperty("properties").EnumerateObject().Select(p => p.Name)
			.Should().BeEquivalentTo("protocolVersion", "items", "cursor", "diagnostics");
	}

	[Fact]
	public void HostSchema_DeclaresEveryPropertyTheLoaderReads()
	{
		// orchestra.schema.json sets additionalProperties:false, so a key the loader honours but
		// the schema omits shows up in the user's editor as an error on a perfectly valid file.
		// That exact drift is what made the services schema reject defaultReadinessTimeoutSeconds.
		using var doc = Load("orchestra.schema.json");
		var declared = doc.RootElement.GetProperty("properties")
			.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

		var loaderKeys = typeof(OrchestraConfigFile)
			.GetProperties(BindingFlags.Public | BindingFlags.Instance)
			.Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name));

		foreach (var key in loaderKeys)
			declared.Should().Contain(key, $"orchestra.json's '{key}' is read by OrchestraConfigFile but not declared in the schema");
	}

	[Fact]
	public void HostSchema_AllowsTheSchemaKeyItself()
	{
		// `orchestra init` writes a "$schema" line so editors bind the file. With
		// additionalProperties:false the schema has to permit its own reference.
		using var doc = Load("orchestra.schema.json");

		doc.RootElement.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
		doc.RootElement.GetProperty("properties").TryGetProperty("$schema", out _).Should().BeTrue();
	}

	[Fact]
	public void HostSchema_ScanDescriptionWarnsThatDirectoryIsTheWorkspaceRoot()
	{
		// Pointing scan.directory at ./orchestrations makes the host look for
		// ./orchestrations/orchestrations and silently register nothing.
		using var doc = Load("orchestra.schema.json");

		var description = doc.RootElement
			.GetProperty("definitions").GetProperty("scan").GetProperty("description").GetString();

		description.Should().Contain("ROOT");
	}

	[Fact]
	public void HookEventEnum_IncludesStepAwaitingInput()
	{
		// HookDefinition parses "step.awaitingInput" and the Approval step fires it, but the
		// schema's enum omitted it — so binding the schema (which the skill tells you to do)
		// flagged the skill's own examples as invalid.
		using var doc = Load("orchestration.schema.json");

		var events = doc.RootElement
			.GetProperty("$defs").GetProperty("hook").GetProperty("properties").GetProperty("on")
			.GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToArray();

		events.Should().Contain("step.awaitingInput");
		events.Should().Contain(["orchestration.success", "orchestration.failure", "step.success", "step.failure"]);
	}

	[Fact]
	public void OrchestrationTimeout_DefaultsToDisabled()
	{
		// The skill claimed 3600. Orchestration.TimeoutSeconds initialises to 0 (no timeout).
		using var doc = Load("orchestration.schema.json");

		doc.RootElement.GetProperty("properties").GetProperty("timeoutSeconds")
			.GetProperty("default").GetInt32().Should().Be(0);
	}

	[Fact]
	public void OrchestrationStep_ExposesForEachFanOut()
	{
		// The skill told authors "there is no forEach property". There is, on Orchestration steps.
		using var doc = Load("orchestration.schema.json");

		var props = doc.RootElement
			.GetProperty("$defs").GetProperty("orchestrationStepProperties").GetProperty("properties");

		foreach (var name in new[] { "forEach", "forEachPath", "itemParameter", "maxConcurrency", "continueOnItemFailure" })
			props.TryGetProperty(name, out _).Should().BeTrue($"'{name}' is part of the fan-out contract");
	}

	[Theory]
	[InlineData("commandStepProperties")]
	[InlineData("scriptStepProperties")]
	[InlineData("httpStepProperties")]
	public void OutputCapturingSteps_DeclareMaxOutputBytes(string stepDef)
	{
		// The step definition ends with unevaluatedProperties=false, so a property the engine
		// accepts but the schema omits would fail validation for every author who uses it.
		using var doc = Load("orchestration.schema.json");

		var prop = doc.RootElement
			.GetProperty("$defs").GetProperty(stepDef).GetProperty("properties").GetProperty("maxOutputBytes");

		prop.GetProperty("type").GetString().Should().Be("integer");
		prop.GetProperty("minimum").GetInt32().Should().Be(0, "0 disables the cap; negatives are rejected");
		prop.GetProperty("description").GetString().Should().Contain("10485760").And.Contain("0 disables");
	}

	[Fact]
	public void TopLevel_DeclaresConcurrencyAndFailurePolicy()
	{
		using var doc = Load("orchestration.schema.json");
		var props = doc.RootElement.GetProperty("properties");

		props.GetProperty("maxConcurrency").GetProperty("minimum").GetInt32().Should().Be(1);
		props.GetProperty("onStepFailure").GetProperty("enum").EnumerateArray().Select(e => e.GetString())
			.Should().Contain(["continue", "stop"]);
		props.GetProperty("onStepFailure").GetProperty("default").GetString().Should().Be("continue");
	}

	[Fact]
	public void RetryPolicy_DeclaresMaxDelayAndJitter()
	{
		// retryPolicy has additionalProperties=false, so a property the engine reads but the
		// schema omits would be rejected by editors and `orchestra validate --schema`.
		using var doc = Load("orchestration.schema.json");
		var props = doc.RootElement.GetProperty("$defs").GetProperty("retryPolicy").GetProperty("properties");

		props.GetProperty("maxDelaySeconds").GetProperty("minimum").GetDouble().Should().Be(0);
		props.GetProperty("jitterSeconds").GetProperty("default").GetDouble().Should().Be(0);
		doc.RootElement.GetProperty("$defs").GetProperty("retryPolicy").GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
	}

	[Fact]
	public void LoopExitPattern_DocumentsTheSubstringTrap()
	{
		// The exit check is a substring match, so "VALID" matches "INVALID". Anyone reading the
		// schema in an editor should learn that before they hit it.
		using var doc = Load("orchestration.schema.json");

		var description = doc.RootElement
			.GetProperty("$defs").GetProperty("loopConfig").GetProperty("properties")
			.GetProperty("exitPattern").GetProperty("description").GetString();

		description.Should().Contain("SUBSTRING");
		description.Should().Contain("INVALID");
	}

	[Fact]
	public void ServicesSchema_AllowsDefaultReadinessTimeout()
	{
		// The loader honours a top-level defaultReadinessTimeoutSeconds, but the schema set
		// additionalProperties:false without declaring it, so a valid config failed validation.
		using var doc = Load("orchestra.services.schema.json");

		var root = doc.RootElement;
		root.GetProperty("additionalProperties").GetBoolean().Should().BeFalse(
			"the services schema is deliberately strict");

		root.GetProperty("properties").TryGetProperty("defaultReadinessTimeoutSeconds", out _)
			.Should().BeTrue("the loader reads this key, so a strict schema must declare it");
	}

	private static string SchemaDirectory()
	{
		var current = new DirectoryInfo(AppContext.BaseDirectory);
		while (current is not null)
		{
			if (File.Exists(Path.Combine(current.FullName, "OrchestrationEngine.slnx")))
				return Path.Combine(current.FullName, "schemas");

			current = current.Parent;
		}

		throw new InvalidOperationException("test must run inside the Orchestra repository");
	}
}
