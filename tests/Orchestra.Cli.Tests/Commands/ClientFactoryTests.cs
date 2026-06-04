using FluentAssertions;
using Orchestra.Cli.Commands;
using Xunit;

namespace Orchestra.Cli.Tests.Commands;

/// <summary>
/// Unit tests for <see cref="ClientFactory.ResolveServerUrl"/>. We pin the precedence order
/// (explicit flag > env var > default) because it's a documented part of the CLI's UX —
/// users build automation around <c>$ORCHESTRA_URL</c> and expect it to be honored.
/// </summary>
public class ClientFactoryTests
{
	[Fact]
	public void ResolveServerUrl_ExplicitFlag_Wins()
	{
		var url = ClientFactory.ResolveServerUrl(
			explicitFlag: "http://flag:1234",
			envReader: _ => "http://env:9999");

		url.Should().Be("http://flag:1234");
	}

	[Fact]
	public void ResolveServerUrl_ExplicitFlag_TrimsWhitespace()
	{
		var url = ClientFactory.ResolveServerUrl(
			explicitFlag: "  http://trimmed:5000  ",
			envReader: _ => null);

		url.Should().Be("http://trimmed:5000");
	}

	[Fact]
	public void ResolveServerUrl_NoFlag_UsesEnvVar()
	{
		var url = ClientFactory.ResolveServerUrl(
			explicitFlag: null,
			envReader: name => name == ClientFactory.ServerUrlEnvVar ? "http://env:5050" : null);

		url.Should().Be("http://env:5050");
	}

	[Fact]
	public void ResolveServerUrl_EmptyFlag_FallsThroughToEnv()
	{
		// Empty/whitespace flag should not "win" — treat it as if not supplied so the env var
		// can take over. This is what users hit when they pass `--server ""` from a shell var.
		var url = ClientFactory.ResolveServerUrl(
			explicitFlag: "   ",
			envReader: _ => "http://env:5050");

		url.Should().Be("http://env:5050");
	}

	[Fact]
	public void ResolveServerUrl_NoFlagNoEnv_UsesDefault()
	{
		var url = ClientFactory.ResolveServerUrl(
			explicitFlag: null,
			envReader: _ => null);

		url.Should().Be(ClientFactory.DefaultServerUrl);
	}

	[Fact]
	public void ResolveServerUrl_NoArgs_ReadsRealEnvironment()
	{
		// Smoke test the parameterless overload: without explicit flag, with the real
		// environment, we should get either the env var or the default — never throw.
		var url = ClientFactory.ResolveServerUrl(explicitFlag: null);

		url.Should().NotBeNullOrWhiteSpace();
		url.Should().StartWith("http");
	}
}
