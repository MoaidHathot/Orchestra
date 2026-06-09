using FluentAssertions;
using Orchestra.Engine.Serialization;
using Xunit;

namespace Orchestra.Engine.Tests.Serialization;

/// <summary>
/// Unit tests for <see cref="EnvironmentVariableExpander"/>. Each test
/// allocates a unique variable name keyed on the test method so parallel
/// runners don't see each other's state. The fixture handles restore on
/// disposal.
/// </summary>
public class EnvironmentVariableExpanderTests : IDisposable
{
	private readonly Dictionary<string, string?> _savedEnvVars = new();

	public void Dispose()
	{
		foreach (var kv in _savedEnvVars)
			Environment.SetEnvironmentVariable(kv.Key, kv.Value);
	}

	private void Set(string name, string? value)
	{
		if (!_savedEnvVars.ContainsKey(name))
			_savedEnvVars[name] = Environment.GetEnvironmentVariable(name);
		Environment.SetEnvironmentVariable(name, value);
	}

	[Fact]
	public void Expand_NoReferences_ReturnsInputUnchanged()
	{
		// Arrange — JSON with no env-var syntax.
		var json = """{ "foo": "bar", "n": 42 }""";

		// Act
		var result = EnvironmentVariableExpander.Expand(json);

		// Assert — bytes are returned verbatim.
		result.Should().Be(json);
	}

	[Fact]
	public void Expand_EmptyString_ReturnsEmptyString()
	{
		EnvironmentVariableExpander.Expand(string.Empty).Should().Be(string.Empty);
	}

	[Fact]
	public void Expand_DollarBraceReference_InlineSubstitutionInsideString()
	{
		// Arrange — the canonical case: a URL with an inline ${TENANT_ID} segment.
		Set("EXP_TEST_TENANT", "72f988bf-86f1-41af-91ab-2d7cd011db47");
		var json = """{ "url": "https://api/${EXP_TEST_TENANT}/x" }""";

		// Act
		var result = EnvironmentVariableExpander.Expand(json);

		// Assert
		result.Should().Be("""{ "url": "https://api/72f988bf-86f1-41af-91ab-2d7cd011db47/x" }""");
	}

	[Fact]
	public void Expand_MultipleDollarBraceReferences_AllExpand()
	{
		// Arrange — same variable used multiple times, plus a second variable.
		Set("EXP_TEST_HOST", "api.example.com");
		Set("EXP_TEST_TENANT", "tenant-1234");
		var json = """{ "a": "https://${EXP_TEST_HOST}/${EXP_TEST_TENANT}/x", "b": "${EXP_TEST_TENANT}" }""";

		// Act
		var result = EnvironmentVariableExpander.Expand(json);

		// Assert
		result.Should().Be("""{ "a": "https://api.example.com/tenant-1234/x", "b": "tenant-1234" }""");
	}

	[Fact]
	public void Expand_EnvColonReference_WholeValueSubstitutionWithQuotes()
	{
		// Arrange — secret-style value where the entire string is the indirection.
		Set("EXP_TEST_SECRET", "shh-its-a-secret");
		var json = """{ "clientSecret": "env:EXP_TEST_SECRET" }""";

		// Act
		var result = EnvironmentVariableExpander.Expand(json);

		// Assert — the quotes stay, only the value is rewritten.
		result.Should().Be("""{ "clientSecret": "shh-its-a-secret" }""");
	}

	[Fact]
	public void Expand_MixedDollarBraceAndEnvColon_BothExpand()
	{
		// Arrange — a config that uses both syntaxes in the same document.
		Set("EXP_TEST_TENANT", "tenant-xyz");
		Set("EXP_TEST_SECRET", "secret-abc");
		var json = """{ "url": "https://api/${EXP_TEST_TENANT}/x", "secret": "env:EXP_TEST_SECRET" }""";

		// Act
		var result = EnvironmentVariableExpander.Expand(json);

		// Assert
		result.Should().Be("""{ "url": "https://api/tenant-xyz/x", "secret": "secret-abc" }""");
	}

	[Fact]
	public void Expand_DollarBraceReference_MissingVar_ThrowsWithVariableName()
	{
		// Arrange — variable intentionally not set.
		const string varName = "EXP_TEST_MISSING_DOLLAR";
		Set(varName, null);
		var json = $$"""{ "url": "https://api/${{{varName}}}/x" }""";

		// Act
		var act = () => EnvironmentVariableExpander.Expand(json, sourcePath: "test.json");

		// Assert — caller can pluck VariableName / SourcePath / Syntax from the typed exception.
		var ex = act.Should().Throw<EnvironmentVariableExpansionException>().Which;
		ex.VariableName.Should().Be(varName);
		ex.SourcePath.Should().Be("test.json");
		ex.Syntax.Should().Be("${VAR}");
		ex.Message.Should().Contain(varName);
		ex.Message.Should().Contain("test.json");
	}

	[Fact]
	public void Expand_EnvColonReference_MissingVar_ThrowsWithVariableName()
	{
		// Arrange
		const string varName = "EXP_TEST_MISSING_ENVCOLON";
		Set(varName, null);
		var json = $$"""{ "secret": "env:{{varName}}" }""";

		// Act
		var act = () => EnvironmentVariableExpander.Expand(json, sourcePath: "secrets.json");

		// Assert
		var ex = act.Should().Throw<EnvironmentVariableExpansionException>().Which;
		ex.VariableName.Should().Be(varName);
		ex.SourcePath.Should().Be("secrets.json");
		ex.Syntax.Should().Be("env:VAR");
	}

	[Fact]
	public void Expand_MissingVar_WithoutSourcePath_OmitsLocationFromMessage()
	{
		// Arrange — when the caller is expanding a string literal there's no on-disk path.
		const string varName = "EXP_TEST_NO_SOURCE";
		Set(varName, null);
		var json = $$"""{ "x": "${{{varName}}}" }""";

		// Act
		var act = () => EnvironmentVariableExpander.Expand(json);

		// Assert
		var ex = act.Should().Throw<EnvironmentVariableExpansionException>().Which;
		ex.SourcePath.Should().BeNull();
		ex.Message.Should().NotContain(" in '");
		ex.Message.Should().Contain(varName);
	}

	[Fact]
	public void Expand_DollarBraceValue_WithJsonSpecialChars_EscapesProperly()
	{
		// Arrange — env-var value carries characters that would break the surrounding JSON string.
		Set("EXP_TEST_TRICKY", "a\"b\\c\nd");
		var json = """{ "v": "${EXP_TEST_TRICKY}" }""";

		// Act
		var result = EnvironmentVariableExpander.Expand(json);

		// Assert — the inline substitution must produce parseable JSON.
		result.Should().Be("""{ "v": "a\"b\\c\nd" }""");

		// And it should round-trip through System.Text.Json without error.
		var doc = System.Text.Json.JsonDocument.Parse(result);
		doc.RootElement.GetProperty("v").GetString().Should().Be("a\"b\\c\nd");
	}

	[Fact]
	public void Expand_EnvColonValue_WithJsonSpecialChars_EscapesProperly()
	{
		// Arrange
		Set("EXP_TEST_TRICKY_COLON", "x\"y\\z");
		var json = """{ "v": "env:EXP_TEST_TRICKY_COLON" }""";

		// Act
		var result = EnvironmentVariableExpander.Expand(json);

		// Assert — round-trip must work.
		var doc = System.Text.Json.JsonDocument.Parse(result);
		doc.RootElement.GetProperty("v").GetString().Should().Be("x\"y\\z");
	}

	[Fact]
	public void Expand_DollarBraceReference_DoesNotMatchEnvColonSyntax()
	{
		// Arrange — a value that happens to contain "env:" inline must not be touched.
		var json = """{ "x": "see env:DOCS for details" }""";

		// Act
		var result = EnvironmentVariableExpander.Expand(json);

		// Assert — the ${ regex requires ${ delimiters, the env: regex requires the value to
		// be the entire quoted string. Neither should match this content.
		result.Should().Be(json);
	}

	[Fact]
	public void Expand_VariableNameWithDigitsAndUnderscores_IsMatched()
	{
		// Arrange — common naming styles.
		Set("EXP_TEST_2024_V1", "ok");
		var json = """{ "x": "${EXP_TEST_2024_V1}" }""";

		// Act
		var result = EnvironmentVariableExpander.Expand(json);

		// Assert
		result.Should().Be("""{ "x": "ok" }""");
	}

	[Fact]
	public void Expand_VariableNameStartingWithDigit_DoesNotMatch()
	{
		// Arrange — POSIX-style env names start with a letter or underscore. The regex
		// enforces that, so `${1ST_VAR}` should be left as a literal.
		var json = """{ "x": "${1ST_VAR}" }""";

		// Act
		var result = EnvironmentVariableExpander.Expand(json);

		// Assert — left untouched (and therefore no exception).
		result.Should().Be(json);
	}

	[Fact]
	public void Expand_NullJson_Throws()
	{
		var act = () => EnvironmentVariableExpander.Expand(null!);
		act.Should().Throw<ArgumentNullException>();
	}
}
