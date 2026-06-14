using FluentAssertions;
using Xunit;

namespace Orchestra.Client.Tests.Commands;

/// <summary>
/// Unit tests for <see cref="ParameterParser"/>. Pins the legacy behaviour of the
/// <c>--param key=value</c> flag so scripts using the CLI keep working: tolerant of
/// malformed entries, last-write-wins on duplicates, returns <c>null</c> when nothing
/// useful was provided (so the client query-string omits the <c>?params=...</c> chunk).
/// </summary>
public class ParameterParserTests
{
	[Fact]
	public void Parse_Null_ReturnsNull()
	{
		ParameterParser.Parse(null).Should().BeNull();
	}

	[Fact]
	public void Parse_Empty_ReturnsNull()
	{
		ParameterParser.Parse(Array.Empty<string>()).Should().BeNull();
	}

	[Fact]
	public void Parse_SimpleKeyValue()
	{
		var result = ParameterParser.Parse(new[] { "topic=AI" });

		result.Should().NotBeNull();
		result!.Should().ContainSingle().Which.Should().BeEquivalentTo(new KeyValuePair<string, string>("topic", "AI"));
	}

	[Fact]
	public void Parse_MultipleParams()
	{
		var result = ParameterParser.Parse(new[] { "topic=AI", "length=short" });

		result.Should().NotBeNull();
		result!.Should().HaveCount(2);
		result!["topic"].Should().Be("AI");
		result!["length"].Should().Be("short");
	}

	[Fact]
	public void Parse_ValueContainingEquals_KeepsRemainderAsValue()
	{
		// Common case: secret=foo=bar should produce { secret -> "foo=bar" }, not split into 3 parts.
		var result = ParameterParser.Parse(new[] { "secret=foo=bar=baz" });

		result.Should().NotBeNull();
		result!["secret"].Should().Be("foo=bar=baz");
	}

	[Fact]
	public void Parse_MalformedEntry_Skipped()
	{
		// Bare strings without '=' are not parameters; silently skip rather than fail the run.
		var result = ParameterParser.Parse(new[] { "topic=AI", "garbage", "length=short" });

		result!.Should().HaveCount(2);
	}

	[Fact]
	public void Parse_EmptyEntry_Skipped()
	{
		var result = ParameterParser.Parse(new[] { "", "   ", "topic=AI" });

		result!.Should().ContainSingle();
	}

	[Fact]
	public void Parse_DuplicateKey_LastWins()
	{
		var result = ParameterParser.Parse(new[] { "topic=A", "topic=B" });

		result!["topic"].Should().Be("B");
	}

	[Fact]
	public void Parse_OnlyMalformed_ReturnsNull()
	{
		// If nothing parsed cleanly, returning null lets the CLI omit the param query-string
		// entirely rather than send an empty `?params={}`.
		ParameterParser.Parse(new[] { "garbage", "=novalue" }).Should().BeNull();
	}
}
