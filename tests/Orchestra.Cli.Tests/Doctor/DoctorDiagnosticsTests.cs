using FluentAssertions;
using Orchestra.Cli.Doctor;
using Orchestra.Host.Hosting;
using Xunit;

namespace Orchestra.Cli.Tests.Doctor;

/// <summary>
/// Tests for <see cref="DoctorDiagnostics"/>.
/// </summary>
/// <remarks>
/// Runs with <c>Offline</c> set so no check touches the network or spawns an agent CLI; the
/// value being pinned here is the report's shape and the classification rules (what counts as a
/// failure versus a warning), not the live provider round-trips.
/// </remarks>
[Collection(OrchestraEnvironmentCollection.Name)]
public sealed class DoctorDiagnosticsTests : IDisposable
{
	private readonly string _tempDir;
	private readonly string? _savedConfigPath;
	private readonly string? _savedXdg;

	public DoctorDiagnosticsTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-doctor-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_tempDir);

		_savedConfigPath = Environment.GetEnvironmentVariable("ORCHESTRA_CONFIG_PATH");
		_savedXdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
	}

	public void Dispose()
	{
		Environment.SetEnvironmentVariable("ORCHESTRA_CONFIG_PATH", _savedConfigPath);
		Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _savedXdg);

		try { Directory.Delete(_tempDir, recursive: true); }
		catch { /* best-effort cleanup */ }
	}

	private string NewProject(string? configJson)
	{
		var dir = Path.Combine(_tempDir, Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		Directory.CreateDirectory(Path.Combine(dir, ".git"));

		if (configJson is not null)
		{
			var path = Path.Combine(dir, OrchestraConfigLoader.ConfigFileName);
			File.WriteAllText(path, configJson);
			Environment.SetEnvironmentVariable("ORCHESTRA_CONFIG_PATH", path);
		}

		return dir;
	}

	private static DoctorCheck Find(DoctorReport report, string name)
		=> report.Checks.Single(c => c.Name == name);

	[Fact]
	public async Task RunAsync_ProducesAStableSetOfChecks()
	{
		var report = await DoctorDiagnostics.RunAsync(
			new DoctorOptions(Offline: true, StartDirectory: NewProject(null)));

		report.Checks.Select(c => c.Name).Should().Contain(
			["installation", "configuration", "data path", "copilot cli", "copilot auth", "opencode cli", "server"]);
	}

	[Fact]
	public async Task RunAsync_BundledAssetsArePresentInTheBuildOutput()
	{
		var report = await DoctorDiagnostics.RunAsync(
			new DoctorOptions(Offline: true, StartDirectory: NewProject(null)));

		Find(report, "installation").Status.Should().Be(DoctorStatus.Ok);
	}

	[Fact]
	public async Task RunAsync_ProviderFilter_LimitsProviderChecks()
	{
		var report = await DoctorDiagnostics.RunAsync(
			new DoctorOptions(Provider: "opencode", Offline: true, StartDirectory: NewProject(null)));

		var names = report.Checks.Select(c => c.Name).ToArray();
		names.Should().Contain("opencode cli");
		names.Should().NotContain("copilot cli");
		names.Should().NotContain("copilot auth");
	}

	[Fact]
	public async Task RunAsync_ProjectConfig_IsReportedAsProjectLocal()
	{
		var project = NewProject("""{ "shutdownTimeoutSeconds": 30 }""");

		var report = await DoctorDiagnostics.RunAsync(
			new DoctorOptions(Offline: true, StartDirectory: project));

		var configuration = Find(report, "configuration");
		configuration.Status.Should().Be(DoctorStatus.Ok);
		configuration.Detail.Should().Contain(OrchestraConfigLoader.ConfigFileName);
	}

	[Fact]
	public async Task RunAsync_MalformedConfig_IsAFailureWithARemedy()
	{
		// The host logs a warning and silently uses defaults here, so doctor is the only place
		// a user finds out their settings are being ignored.
		var project = NewProject("{ not valid json");

		var report = await DoctorDiagnostics.RunAsync(
			new DoctorOptions(Offline: true, StartDirectory: project));

		var configuration = Find(report, "configuration");
		configuration.Status.Should().Be(DoctorStatus.Failed);
		configuration.Remedy.Should().NotBeNullOrWhiteSpace();
		report.HasFailures.Should().BeTrue();
	}

	[Fact]
	public async Task RunAsync_DataPath_ReportsWritability()
	{
		var project = NewProject($$"""{ "dataPath": "./data" }""");

		var report = await DoctorDiagnostics.RunAsync(
			new DoctorOptions(Offline: true, StartDirectory: project));

		var dataPath = Find(report, "data path");
		dataPath.Status.Should().Be(DoctorStatus.Ok);
		dataPath.Detail.Should().Contain("writable");
		Directory.Exists(Path.Combine(project, "data")).Should().BeTrue();
	}

	[Fact]
	public async Task RunAsync_Offline_SkipsNetworkChecks()
	{
		var report = await DoctorDiagnostics.RunAsync(
			new DoctorOptions(Offline: true, StartDirectory: NewProject(null)));

		Find(report, "copilot auth").Status.Should().Be(DoctorStatus.Skipped);
	}

	[Fact]
	public async Task RunAsync_NoServerConfigured_IsNotAFailure()
	{
		// run/exec spawn a temporary host, so "no server" is normal rather than broken.
		var savedUrl = Environment.GetEnvironmentVariable("ORCHESTRA_URL");
		Environment.SetEnvironmentVariable("ORCHESTRA_URL", null);
		try
		{
			var report = await DoctorDiagnostics.RunAsync(
				new DoctorOptions(Offline: true, StartDirectory: NewProject(null)));

			Find(report, "server").Status.Should().NotBe(DoctorStatus.Failed);
		}
		finally
		{
			Environment.SetEnvironmentVariable("ORCHESTRA_URL", savedUrl);
		}
	}

	[Fact]
	public async Task RunAsync_UnreachableServer_IsAWarningNotAFailure()
	{
		var report = await DoctorDiagnostics.RunAsync(
			new DoctorOptions(
				ServerUrl: "http://127.0.0.1:1",
				StartDirectory: NewProject(null)));

		var server = Find(report, "server");
		server.Status.Should().Be(DoctorStatus.Warning);
		server.Remedy.Should().NotBeNullOrWhiteSpace();
	}

	[Fact]
	public async Task RunAsync_EveryNonOkCheckExplainsWhatToDo()
	{
		var report = await DoctorDiagnostics.RunAsync(
			new DoctorOptions(Offline: true, StartDirectory: NewProject("{ broken")));

		foreach (var check in report.Checks.Where(c => c.Status is DoctorStatus.Failed or DoctorStatus.Warning))
			check.Remedy.Should().NotBeNullOrWhiteSpace($"check '{check.Name}' failed without telling the user what to do");
	}

	[Theory]
	[InlineData("1.0.67", "1.0.67", "CLI 1.0.67")]
	[InlineData("1.0.85", "1.0.67", "CLI 1.0.85, Orchestra pins 1.0.67")]
	[InlineData(null, "1.0.67", "pinned CLI 1.0.67")]
	[InlineData("", "1.0.67", "pinned CLI 1.0.67")]
	public void DescribeCliVersion_NeverPresentsThePinAsTheInstalledVersion(string? installed, string pinned, string expected)
	{
		// The binary on disk is not necessarily the pinned one: ORCHESTRA_COPILOT_CLI_PATH can
		// point anywhere, and the CLI self-updates in place. Doctor must say which is which.
		DoctorDiagnostics.DescribeCliVersion(installed, pinned).Should().Be(expected);
	}

	[Fact]
	public async Task RunAsync_Offline_DoesNotClaimAnInstalledCliVersion()
	{
		// --offline promises not to spawn agent CLIs, so the version can only be the pin --
		// and it has to be labelled as such.
		var report = await DoctorDiagnostics.RunAsync(
			new DoctorOptions(Offline: true, StartDirectory: NewProject(null)));

		var cli = Find(report, "copilot cli");
		if (cli.Status == DoctorStatus.Ok)
			cli.Detail.Should().Contain("pinned CLI ");
	}
}
