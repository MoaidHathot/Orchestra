using FluentAssertions;
using Orchestra.Cli.Commands;
using Xunit;

namespace Orchestra.Cli.Tests.Commands;

/// <summary>
/// Unit tests for <see cref="ClientFactory.ResolveServerUrl"/> and the orchestra.json reader
/// behind it. We pin the precedence order (explicit flag > <c>$ORCHESTRA_URL</c> > configured
/// <c>orchestra.json</c> > built-in default) because it's a documented part of the CLI's UX:
/// users build automation around <c>$ORCHESTRA_URL</c> and expect every verb — not just
/// <c>run</c>/<c>exec</c> — to honor the server URL configured in <c>orchestra.json</c>.
///
/// Env-var-sensitive tests follow the same isolation pattern as
/// <c>OrchestraConfigLoaderTests</c>: save and clear the discovery env vars in the constructor,
/// restore them on dispose, and point <c>ORCHESTRA_CONFIG_PATH</c> at a throwaway temp file so
/// the real loader is exercised without depending on (or disturbing) a developer's own config.
/// </summary>
public class ClientFactoryTests : IDisposable
{
	private readonly string _tempDir;
	private readonly Dictionary<string, string?> _savedEnvVars = new();

	public ClientFactoryTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-clientfactory-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_tempDir);

		// Isolate config discovery from the host machine: the platform fallback (%APPDATA% /
		// ~/.config) can't be cleared, so tests that must see "no config" use the seam instead.
		SaveAndClear("ORCHESTRA_CONFIG_PATH");
		SaveAndClear("XDG_CONFIG_HOME");
	}

	public void Dispose()
	{
		foreach (var kv in _savedEnvVars)
			Environment.SetEnvironmentVariable(kv.Key, kv.Value);

		if (Directory.Exists(_tempDir))
		{
			try { Directory.Delete(_tempDir, recursive: true); }
			catch { /* best-effort cleanup */ }
		}

		GC.SuppressFinalize(this);
	}

	private void SaveAndClear(string name)
	{
		_savedEnvVars[name] = Environment.GetEnvironmentVariable(name);
		Environment.SetEnvironmentVariable(name, null);
	}

	/// <summary>Writes an orchestra.json to the temp dir and points ORCHESTRA_CONFIG_PATH at it.</summary>
	private string WriteConfig(string json)
	{
		var path = Path.Combine(_tempDir, "orchestra.json");
		File.WriteAllText(path, json);
		Environment.SetEnvironmentVariable("ORCHESTRA_CONFIG_PATH", path);
		return path;
	}

	// ── Precedence (using the pure env/config seams) ──────────────────────────────

	[Fact]
	public void ResolveServerUrl_ExplicitFlag_Wins()
	{
		var url = ClientFactory.ResolveServerUrl(
			explicitFlag: "http://flag:1234",
			envReader: _ => "http://env:9999",
			configuredUrlReader: () => "http://config:5200");

		url.Should().Be("http://flag:1234");
	}

	[Fact]
	public void ResolveServerUrl_ExplicitFlag_TrimsWhitespace()
	{
		var url = ClientFactory.ResolveServerUrl(
			explicitFlag: "  http://trimmed:5000  ",
			envReader: _ => null,
			configuredUrlReader: () => null);

		url.Should().Be("http://trimmed:5000");
	}

	[Fact]
	public void ResolveServerUrl_NoFlag_UsesEnvVar()
	{
		var url = ClientFactory.ResolveServerUrl(
			explicitFlag: null,
			envReader: name => name == ClientFactory.ServerUrlEnvVar ? "http://env:5050" : null,
			configuredUrlReader: () => "http://config:5200");

		url.Should().Be("http://env:5050");
	}

	[Fact]
	public void ResolveServerUrl_EnvVar_WinsOverConfig()
	{
		// $ORCHESTRA_URL is the operator's explicit per-invocation override; it must beat the
		// persisted orchestra.json value.
		var url = ClientFactory.ResolveServerUrl(
			explicitFlag: null,
			envReader: _ => "http://env:5050",
			configuredUrlReader: () => "http://config:5200");

		url.Should().Be("http://env:5050");
	}

	[Fact]
	public void ResolveServerUrl_EmptyFlag_FallsThroughToEnv()
	{
		// Empty/whitespace flag should not "win" — treat it as if not supplied so the env var
		// can take over. This is what users hit when they pass `--server ""` from a shell var.
		var url = ClientFactory.ResolveServerUrl(
			explicitFlag: "   ",
			envReader: _ => "http://env:5050",
			configuredUrlReader: () => null);

		url.Should().Be("http://env:5050");
	}

	[Fact]
	public void ResolveServerUrl_NoFlagNoEnv_UsesConfiguredUrl()
	{
		// The crux of the fix: with no flag and no env var, the URL configured in orchestra.json
		// is used instead of blindly assuming localhost:5000.
		var url = ClientFactory.ResolveServerUrl(
			explicitFlag: null,
			envReader: _ => null,
			configuredUrlReader: () => "http://config:5273");

		url.Should().Be("http://config:5273");
	}

	[Fact]
	public void ResolveServerUrl_ConfiguredUrl_TrimsWhitespace()
	{
		var url = ClientFactory.ResolveServerUrl(
			explicitFlag: null,
			envReader: _ => null,
			configuredUrlReader: () => "  http://config:5273  ");

		url.Should().Be("http://config:5273");
	}

	[Fact]
	public void ResolveServerUrl_NoFlagNoEnvNoConfig_UsesDefault()
	{
		var url = ClientFactory.ResolveServerUrl(
			explicitFlag: null,
			envReader: _ => null,
			configuredUrlReader: () => null);

		url.Should().Be(ClientFactory.DefaultServerUrl);
	}

	[Fact]
	public void ResolveServerUrl_ConfigReturnsWhitespace_UsesDefault()
	{
		var url = ClientFactory.ResolveServerUrl(
			explicitFlag: null,
			envReader: _ => null,
			configuredUrlReader: () => "   ");

		url.Should().Be(ClientFactory.DefaultServerUrl);
	}

	[Fact]
	public void ResolveServerUrl_NoArgs_ReadsRealEnvironment()
	{
		// Smoke test the production overload: without explicit flag, with the real environment
		// and config reader, we should get a usable URL — never throw.
		var url = ClientFactory.ResolveServerUrl(explicitFlag: null);

		url.Should().NotBeNullOrWhiteSpace();
		url.Should().StartWith("http");
	}

	// ── Real orchestra.json reader (the actual bug: client verbs ignored config) ───

	[Fact]
	public void ReadConfiguredServerUrl_ReadsHostBaseUrlFromConfigFile()
	{
		WriteConfig(/*lang=json,strict*/ """{ "hostBaseUrl": "http://localhost:5273" }""");

		ClientFactory.ReadConfiguredServerUrl().Should().Be("http://localhost:5273");
	}

	[Fact]
	public void ResolveServerUrl_NoFlagNoEnv_PicksUpConfiguredPort_EndToEnd()
	{
		// End-to-end proof of the fix: a bare `orchestra list` (no --server, no $ORCHESTRA_URL)
		// now resolves to the port configured in orchestra.json instead of localhost:5000.
		WriteConfig(/*lang=json,strict*/ """{ "hostBaseUrl": "http://localhost:5273" }""");

		var url = ClientFactory.ResolveServerUrl(explicitFlag: null, envReader: _ => null);

		url.Should().Be("http://localhost:5273");
	}

	[Fact]
	public void ReadConfiguredServerUrl_FallsBackToFirstUrl_WhenNoHostBaseUrl()
	{
		WriteConfig(/*lang=json,strict*/ """{ "urls": "http://127.0.0.1:5299;http://127.0.0.1:5300" }""");

		ClientFactory.ReadConfiguredServerUrl().Should().Be("http://127.0.0.1:5299");
	}

	[Fact]
	public void ReadConfiguredServerUrl_HostBaseUrl_WinsOverUrls()
	{
		WriteConfig(/*lang=json,strict*/ """{ "hostBaseUrl": "http://localhost:5273", "urls": "http://127.0.0.1:5299" }""");

		ClientFactory.ReadConfiguredServerUrl().Should().Be("http://localhost:5273");
	}

	[Fact]
	public void ReadConfiguredServerUrl_NoServerKeys_ReturnsNull()
	{
		WriteConfig(/*lang=json,strict*/ """{ "dataPath": "/tmp/data" }""");

		ClientFactory.ReadConfiguredServerUrl().Should().BeNull();
	}

	[Fact]
	public void ReadConfiguredServerUrl_MalformedFile_ReturnsNull()
	{
		// A broken config file must never throw out of a plain client command.
		WriteConfig("{ this is not valid json ");

		ClientFactory.ReadConfiguredServerUrl().Should().BeNull();
	}

	// ── FirstUrl helper ───────────────────────────────────────────────────────────

	[Theory]
	[InlineData(null, null)]
	[InlineData("", null)]
	[InlineData("   ", null)]
	[InlineData("http://a:1", "http://a:1")]
	[InlineData("http://a:1;http://b:2", "http://a:1")]
	[InlineData("http://a:1,http://b:2", "http://a:1")]
	[InlineData("  http://a:1  ;http://b:2", "http://a:1")]
	public void FirstUrl_ReturnsFirstEntryOrNull(string? urls, string? expected)
	{
		ClientFactory.FirstUrl(urls).Should().Be(expected);
	}
}
