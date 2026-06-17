using FluentAssertions;

namespace Orchestra.OpenCode.Tests;

public class OpenCodeModelRefTests
{
	[Theory]
	[InlineData("github-copilot/claude-opus-4.8", "github-copilot", "claude-opus-4.8")]
	[InlineData("anthropic/claude-3-5-sonnet-20241022", "anthropic", "claude-3-5-sonnet-20241022")]
	[InlineData("  openai/gpt-5  ", "openai", "gpt-5")]
	public void Parse_QualifiedModel_SplitsOnFirstSlash(string model, string provider, string id)
	{
		var result = OpenCodeModelRef.Parse(model, fallbackProvider: "github-copilot");
		result.ProviderId.Should().Be(provider);
		result.ModelId.Should().Be(id);
		result.ToString().Should().Be($"{provider}/{id}");
	}

	[Fact]
	public void Parse_PathLikeModelId_KeepsRemainderAsModel()
	{
		// Only the FIRST slash separates provider from model id.
		var result = OpenCodeModelRef.Parse("openrouter/anthropic/claude-3.5", fallbackProvider: null);
		result.ProviderId.Should().Be("openrouter");
		result.ModelId.Should().Be("anthropic/claude-3.5");
	}

	[Fact]
	public void Parse_BareModel_UsesFallbackProvider()
	{
		var result = OpenCodeModelRef.Parse("claude-opus-4.8", fallbackProvider: "github-copilot");
		result.ProviderId.Should().Be("github-copilot");
		result.ModelId.Should().Be("claude-opus-4.8");
	}

	[Fact]
	public void Parse_BareModel_NoFallback_Throws()
	{
		var act = () => OpenCodeModelRef.Parse("claude-opus-4.8", fallbackProvider: null);
		act.Should().Throw<InvalidOperationException>().WithMessage("*no 'provider/' prefix*");
	}

	[Fact]
	public void Parse_Empty_Throws()
	{
		var act = () => OpenCodeModelRef.Parse("   ", fallbackProvider: "github-copilot");
		act.Should().Throw<ArgumentException>();
	}
}
