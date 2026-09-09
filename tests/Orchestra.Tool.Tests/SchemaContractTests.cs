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
	public void Schema_IsWellFormedJson(string fileName)
	{
		// Guards against a hand-edit dropping a brace: the schemas are edited as text far more
		// often than they are parsed by anything in the build.
		var act = () => Load(fileName).Dispose();

		act.Should().NotThrow();
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
