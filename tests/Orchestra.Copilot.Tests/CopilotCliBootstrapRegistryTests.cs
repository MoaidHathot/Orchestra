using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Copilot;

namespace Orchestra.Copilot.Tests;

/// <summary>
/// Tests for the npm-registry side of <see cref="CopilotCliBootstrap"/>: which registries are
/// tried, in what order, how a failed registry falls through to the next one, and what the
/// user is told when all of them fail.
/// </summary>
/// <remarks>
/// Motivated by corporate networks that block <c>registry.npmjs.org</c> at the TLS layer while
/// <c>npm install</c> keeps working through a mirror declared in <c>~/.npmrc</c>. Before this,
/// every run scope died with a bare "SSL connection could not be established" and no hint that
/// either override existed.
/// </remarks>
public class CopilotCliBootstrapRegistryTests
{
	private const string Default = "https://registry.npmjs.org";

	private static Func<string, string?> Env(params (string Name, string Value)[] pairs)
	{
		var map = pairs.ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);
		return name => map.TryGetValue(name, out var value) ? value : null;
	}

	// ---- candidate resolution -------------------------------------------------------------

	[Fact]
	public void ResolveRegistryCandidates_NoConfiguration_IsJustThePublicRegistry()
	{
		var candidates = CopilotCliBootstrap.ResolveRegistryCandidates(Env(), npmrcContent: null);

		candidates.Select(c => c.Url).Should().Equal(Default);
		candidates[0].Source.Should().Be("default");
	}

	[Fact]
	public void ResolveRegistryCandidates_ExplicitOverride_IsTheOnlyCandidate()
	{
		// An explicit override is a statement of intent: no silent fallback to a registry the
		// user has just told us they cannot reach.
		var env = Env((CopilotCliBootstrap.NpmRegistryEnvVar, "https://mirror.example/npm/"));
		const string npmrc = "registry=https://other.example/\n";

		var candidates = CopilotCliBootstrap.ResolveRegistryCandidates(env, npmrc);

		candidates.Should().ContainSingle();
		candidates[0].Url.Should().Be("https://mirror.example/npm", "trailing slash is normalised away so URL joins never produce '//@github'");
		candidates[0].Source.Should().Be(CopilotCliBootstrap.NpmRegistryEnvVar);
	}

	[Fact]
	public void ResolveRegistryCandidates_ExplicitOverride_TrimsWhitespace()
	{
		var env = Env((CopilotCliBootstrap.NpmRegistryEnvVar, "  https://mirror.example  "));

		var candidates = CopilotCliBootstrap.ResolveRegistryCandidates(env, null);

		candidates.Select(c => c.Url).Should().Equal("https://mirror.example");
	}

	[Theory]
	[InlineData("not a url")]
	[InlineData("registry.npmjs.org")]
	[InlineData("file:///C:/mirror")]
	public void ResolveRegistryCandidates_ExplicitOverrideThatIsNotAnHttpUrl_Throws(string value)
	{
		// Silently ignoring a typo in the override would send the user straight back to the
		// blocked default with no explanation.
		var env = Env((CopilotCliBootstrap.NpmRegistryEnvVar, value));

		var act = () => CopilotCliBootstrap.ResolveRegistryCandidates(env, null);

		act.Should().Throw<CopilotCliBootstrapException>()
			.WithMessage($"*{CopilotCliBootstrap.NpmRegistryEnvVar}*")
			.WithMessage($"*{value}*");
	}

	[Fact]
	public void ResolveRegistryCandidates_NpmrcRegistry_IsTriedBeforeTheDefault()
	{
		// The exact file from the machine that motivated this: npm works through the corp
		// proxy, so the bootstrap must try that first.
		const string npmrc = "registry=https://packagefeedproxy.microsoft.io/npm/\n";

		var candidates = CopilotCliBootstrap.ResolveRegistryCandidates(Env(), npmrc);

		candidates.Select(c => c.Url).Should().Equal(
			"https://packagefeedproxy.microsoft.io/npm",
			Default);
		candidates[0].Source.Should().Be("~/.npmrc registry");
	}

	[Fact]
	public void ResolveRegistryCandidates_FollowsNpmPrecedence_ScopedThenEnvThenUnscopedThenDefault()
	{
		// npm resolves "@github:registry" ahead of "registry", and for the same key the
		// environment beats the user config file.
		const string npmrc = """
			registry=https://unscoped.example
			@github:registry=https://scoped.example
			""";
		var env = Env((CopilotCliBootstrap.NpmConfigRegistryEnvVar, "https://env.example"));

		var candidates = CopilotCliBootstrap.ResolveRegistryCandidates(env, npmrc);

		candidates.Select(c => c.Url).Should().Equal(
			"https://scoped.example",
			"https://env.example",
			"https://unscoped.example",
			Default);
		candidates.Select(c => c.Source).Should().Equal(
			"~/.npmrc @github:registry",
			CopilotCliBootstrap.NpmConfigRegistryEnvVar,
			"~/.npmrc registry",
			"default");
	}

	[Fact]
	public void ResolveRegistryCandidates_DropsDuplicatesAndNonHttpValues()
	{
		const string npmrc = """
			@github:registry=https://REGISTRY.npmjs.org/
			registry=not-a-url
			""";
		var env = Env((CopilotCliBootstrap.NpmConfigRegistryEnvVar, "ftp://mirror.example"));

		var candidates = CopilotCliBootstrap.ResolveRegistryCandidates(env, npmrc);

		candidates.Should().ContainSingle("the scoped value is the default registry spelled differently, and the other two are not http(s) URLs");
		candidates[0].Url.Should().Be("https://REGISTRY.npmjs.org");
		candidates[0].Source.Should().Be("~/.npmrc @github:registry", "the first spelling wins so the log says where it came from");
	}

	// ---- .npmrc parsing -----------------------------------------------------------------

	[Fact]
	public void ParseNpmrc_HandlesCommentsQuotesLineEndingsAndOverrides()
	{
		var content = string.Join("\r\n",
			"# comment",
			"; another comment",
			"[section-that-npm-never-uses]",
			"registry = \"https://first.example\"",
			"  registry='https://second.example/'  ",
			"//second.example/:_authToken=abc123",
			"always-auth=true",
			"no-equals-sign",
			"=orphan-value");

		var parsed = CopilotCliBootstrap.ParseNpmrc(content, Env());

		parsed.Should().Equal(new Dictionary<string, string>
		{
			["registry"] = "https://second.example/",
			["//second.example/:_authToken"] = "abc123",
			["always-auth"] = "true",
		});
	}

	[Fact]
	public void ParseNpmrc_ExpandsEnvPlaceholders_AndDropsValuesItCannotResolve()
	{
		const string content = """
			registry=https://${NPM_HOST}/npm
			//${NPM_HOST}/:_authToken=${NPM_TOKEN}
			""";
		var env = Env(("NPM_HOST", "mirror.example"));

		var parsed = CopilotCliBootstrap.ParseNpmrc(content, env);

		parsed.Should().ContainKey("registry").WhoseValue.Should().Be("https://mirror.example/npm");
		parsed.Keys.Should().NotContain(k => k.Contains("_authToken"),
			"a value with an unresolved ${VAR} must not be used half-expanded");
	}

	// ---- URL + message composition -----------------------------------------------------

	[Fact]
	public void BuildDownloadUrl_MatchesTheSdkMsbuildLayout()
	{
		// Mirrors _CopilotDownloadUrl in GitHub.Copilot.SDK.targets, which is what the SDK's own
		// build-time download hits; the two must agree so a mirror that works for one works for both.
		CopilotCliBootstrap.BuildDownloadUrl("https://mirror.example/npm/", "win32-x64", "1.2.3")
			.Should().Be("https://mirror.example/npm/@github/copilot-win32-x64/-/copilot-win32-x64-1.2.3.tgz");
	}

	[Fact]
	public void DescribeFailure_FlattensTheInnerChain()
	{
		var ex = new HttpRequestException(
			"The SSL connection could not be established, see inner exception.",
			new System.Security.Authentication.AuthenticationException(
				"Authentication failed because the remote party sent a TLS alert: 'HandshakeFailure'.",
				new InvalidOperationException("The message received was unexpected or badly formatted.")));

		CopilotCliBootstrap.DescribeFailure(ex).Should().Be(
			"The SSL connection could not be established. -> " +
			"Authentication failed because the remote party sent a TLS alert: 'HandshakeFailure'. -> " +
			"The message received was unexpected or badly formatted.");
	}

	[Fact]
	public void BuildDownloadFailureMessage_ListsEveryAttemptAndBothOverrides()
	{
		var message = CopilotCliBootstrap.BuildDownloadFailureMessage("linux-x64",
		[
			("https://a.example/x.tgz", "~/.npmrc registry", "404 Not Found"),
			("https://b.example/x.tgz", "default", "TLS alert: HandshakeFailure"),
		]);

		message.Should().Contain(CopilotCliBootstrap.CopilotCliVersion).And.Contain("linux-x64");
		message.Should().Contain("https://a.example/x.tgz").And.Contain("~/.npmrc registry").And.Contain("404 Not Found");
		message.Should().Contain("https://b.example/x.tgz").And.Contain("TLS alert: HandshakeFailure");
		message.Should().Contain(CopilotCliBootstrap.NpmRegistryEnvVar);
		message.Should().Contain(CopilotCliBootstrap.ExplicitCliPathEnvVar);
	}

	// ---- download fallback against a real loopback HTTP server --------------------------

	[Fact]
	public async Task DownloadArchiveAsync_FallsThroughToTheNextRegistry_WhenTheFirstOneFails()
	{
		var payload = Encoding.UTF8.GetBytes("not really a tarball, but the bytes must arrive intact");
		await using var blocked = new LoopbackHttpServer(_ => (403, Array.Empty<byte>()));
		await using var mirror = new LoopbackHttpServer(_ => (200, payload));
		var archivePath = Path.Combine(Path.GetTempPath(), $"orchestra-bootstrap-{Guid.NewGuid():N}.tgz");

		try
		{
			var url = await CopilotCliBootstrap.DownloadArchiveAsync(
				[
					new CopilotCliBootstrap.NpmRegistryCandidate(blocked.BaseUrl, "~/.npmrc registry"),
					new CopilotCliBootstrap.NpmRegistryCandidate(mirror.BaseUrl, "default"),
				],
				"linux-x64",
				archivePath,
				NullLogger.Instance,
				CancellationToken.None);

			url.Should().StartWith(mirror.BaseUrl).And.EndWith($"/@github/copilot-linux-x64/-/copilot-linux-x64-{CopilotCliBootstrap.CopilotCliVersion}.tgz");
			(await File.ReadAllBytesAsync(archivePath)).Should().Equal(payload);
			blocked.Requests.Should().ContainSingle("the failing registry is tried exactly once");
			mirror.Requests.Should().ContainSingle();
		}
		finally
		{
			File.Delete(archivePath);
		}
	}

	[Fact]
	public async Task DownloadArchiveAsync_WhenEveryRegistryFails_ThrowsOneActionableException()
	{
		await using var notFound = new LoopbackHttpServer(_ => (404, Array.Empty<byte>()));
		var refusedPort = ReserveClosedPort();
		var archivePath = Path.Combine(Path.GetTempPath(), $"orchestra-bootstrap-{Guid.NewGuid():N}.tgz");

		var act = () => CopilotCliBootstrap.DownloadArchiveAsync(
			[
				new CopilotCliBootstrap.NpmRegistryCandidate(notFound.BaseUrl, "~/.npmrc registry"),
				new CopilotCliBootstrap.NpmRegistryCandidate($"http://127.0.0.1:{refusedPort}", "default"),
			],
			"win32-x64",
			archivePath,
			NullLogger.Instance,
			CancellationToken.None);

		var thrown = (await act.Should().ThrowAsync<CopilotCliBootstrapException>()).Which;

		thrown.Message.Should().Contain(notFound.BaseUrl).And.Contain("404");
		thrown.Message.Should().Contain($"http://127.0.0.1:{refusedPort}");
		thrown.Message.Should().Contain(CopilotCliBootstrap.NpmRegistryEnvVar);
		thrown.Message.Should().Contain(CopilotCliBootstrap.ExplicitCliPathEnvVar);
		thrown.InnerException.Should().BeOfType<AggregateException>()
			.Which.InnerExceptions.Should().HaveCount(2, "one per registry tried");
		File.Exists(archivePath).Should().BeFalse("no partial archive is left behind for the next attempt to trip over");
	}

	[Fact]
	public async Task DownloadArchiveAsync_CallerCancellation_IsNotTreatedAsARegistryFailure()
	{
		await using var server = new LoopbackHttpServer(_ => (200, new byte[16]));
		using var cts = new CancellationTokenSource();
		cts.Cancel();

		var act = () => CopilotCliBootstrap.DownloadArchiveAsync(
			[new CopilotCliBootstrap.NpmRegistryCandidate(server.BaseUrl, "default")],
			"linux-x64",
			Path.Combine(Path.GetTempPath(), $"orchestra-bootstrap-{Guid.NewGuid():N}.tgz"),
			NullLogger.Instance,
			cts.Token);

		await act.Should().ThrowAsync<OperationCanceledException>();
	}

	/// <summary>Binds and immediately releases a loopback port so connecting to it is refused.</summary>
	private static int ReserveClosedPort()
	{
		var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		var port = ((IPEndPoint)listener.LocalEndpoint).Port;
		listener.Stop();
		return port;
	}

	/// <summary>
	/// Smallest HTTP/1.1 server that satisfies <see cref="SocketsHttpHandler"/>: one response
	/// per connection, <c>Connection: close</c>. A raw <see cref="TcpListener"/> rather than
	/// <see cref="HttpListener"/> so the test needs no URL ACL on Windows.
	/// </summary>
	private sealed class LoopbackHttpServer : IAsyncDisposable
	{
		private readonly TcpListener _listener;
		private readonly Func<string, (int Status, byte[] Body)> _handler;
		private readonly CancellationTokenSource _cts = new();
		private readonly Task _acceptLoop;
		private readonly List<string> _requests = [];

		public LoopbackHttpServer(Func<string, (int Status, byte[] Body)> handler)
		{
			_handler = handler;
			_listener = new TcpListener(IPAddress.Loopback, 0);
			_listener.Start();
			BaseUrl = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
			_acceptLoop = AcceptLoopAsync();
		}

		public string BaseUrl { get; }

		public IReadOnlyList<string> Requests
		{
			get { lock (_requests) return _requests.ToArray(); }
		}

		private async Task AcceptLoopAsync()
		{
			while (!_cts.IsCancellationRequested)
			{
				TcpClient client;
				try
				{
					client = await _listener.AcceptTcpClientAsync(_cts.Token);
				}
				catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
				{
					return;
				}

				_ = Task.Run(() => HandleAsync(client));
			}
		}

		private async Task HandleAsync(TcpClient client)
		{
			using (client)
			{
				using var stream = client.GetStream();
				var buffer = new byte[16 * 1024];
				var total = 0;
				while (total < buffer.Length)
				{
					var read = await stream.ReadAsync(buffer.AsMemory(total));
					if (read == 0) break;
					total += read;
					if (Encoding.ASCII.GetString(buffer, 0, total).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
				}

				var requestLine = Encoding.ASCII.GetString(buffer, 0, total).Split("\r\n")[0];
				var path = requestLine.Split(' ').ElementAtOrDefault(1) ?? "/";
				lock (_requests) _requests.Add(path);

				var (status, body) = _handler(path);
				var reason = status switch
				{
					200 => "OK",
					403 => "Forbidden",
					404 => "Not Found",
					_ => "Status",
				};
				var head = Encoding.ASCII.GetBytes(
					$"HTTP/1.1 {status} {reason}\r\nContent-Type: application/octet-stream\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
				await stream.WriteAsync(head);
				await stream.WriteAsync(body);
				await stream.FlushAsync();
			}
		}

		public async ValueTask DisposeAsync()
		{
			_cts.Cancel();
			_listener.Stop();
			try { await _acceptLoop; } catch { /* shutting down */ }
			_cts.Dispose();
		}
	}
}
