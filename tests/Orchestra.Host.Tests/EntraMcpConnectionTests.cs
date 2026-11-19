using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine;
using Orchestra.Host.Mcp;
using Xunit;

namespace Orchestra.Host.Tests;

public class EntraMcpConnectionTests : IDisposable
{
	private readonly string _directory = Directory.CreateTempSubdirectory("orchestra-entra-test-").FullName;
	private const string Entry = """
		{"name":"work","type":"remote","endpoint":"https://example.test/mcp","allowedTools":["ask","retrieve"],
		 "auth":{"type":"entra","clientId":"10000000-0000-0000-0000-000000000001","tenantId":"11111111-2222-3333-4444-555555555555","scopes":["resource/read"]}}
		""";
	private static RemoteMcp Parse(string entry = Entry) => (RemoteMcp)OrchestrationParser.ParseMcps("{\"mcps\":[" + entry + "]}").Single();

	[Fact]
	public async Task DetachedAgentCannotIgnoreAuth_AndHostCanStartBeforeFirstSignIn()
	{
		var server = Parse();
		var require = () => server.EnsureAgentReady();
		require.Should().Throw<InvalidOperationException>();
		await using var manager = new McpManager(NullLogger<McpManager>.Instance);
		await manager.InitializeAsync([server]); // example.test is deliberately unreachable
		var route = (RemoteMcp)manager.Resolve([server]).Single();
		route.RequiresHost.Should().BeFalse();
		new Uri(route.Endpoint).IsLoopback.Should().BeTrue();
		route.Headers.Should().BeEmpty();
	}

	[Fact]
	public void InlineAuthIsRejectedRatherThanIgnored()
	{
		var read = () => OrchestrationParser.ParseOrchestration("{\"name\":\"test\",\"description\":\"fixture\",\"mcps\":[" + Entry + "],\"steps\":[]}", []);
		read.Should().Throw<JsonException>().WithMessage("*orchestra.mcp.json*");
	}

	[Theory]
	[InlineData("https://example.test/mcp", "http://example.test/mcp")]
	[InlineData("https://example.test/mcp", "https://example.test/mcp?token=anything")]
	[InlineData("https://example.test/mcp", "https://{{param.host}}/mcp")]
	[InlineData("\"type\":\"entra\"", "\"type\":\"unknown\"")]
	[InlineData("\"type\":\"entra\"", "\"type\":\"entra\",\"clientSecret\":\"unused\"")]
	[InlineData("\"type\":\"entra\"", "\"type\":\"entra\",\"redirectUri\":\"https://example.test/\"")]
	[InlineData("\"type\":\"remote\"", "\"type\":\"local\"")]
	[InlineData("\"type\":\"remote\"", "\"type\":\"remote\",\"headers\":{\"Authorization\":\"conflict\"}")]
	[InlineData("[\"ask\",\"retrieve\"]", "[\"*\"]")]
	[InlineData("[\"ask\",\"retrieve\"]", "[]")]
	public void InvalidManagedDefinitionsFailClosed(string before, string after)
	{
		var parse = () => Parse(Entry.Replace(before, after));
		parse.Should().Throw<JsonException>();
	}

	[Fact]
	public async Task FreshCredentialRestoresAccount_SilentRequestsGetFreshTokens_AndTargetChangeRequiresConnect()
	{
		var server = Parse();
		var requested = new TokenRequestContext(server.Auth!.Scopes);
		var mock = new RecordingCredential();
		var creations = new List<InteractiveBrowserCredentialOptions>();
		EntraMcpCredentials NewStore() => new(_directory, o => { creations.Add(o); return mock; });
		var notConnected = NewStore().OpenSilent(server);
		Func<Task> missing = async () => await notConnected.GetTokenAsync(requested, default);
		await missing.Should().ThrowAsync<InvalidOperationException>().WithMessage("*orchestra mcp connect work*");
		creations.Should().BeEmpty();
		await NewStore().ConnectAsync(server, default);
		var first = await notConnected.GetTokenAsync(requested, default);
		var restarted = NewStore().OpenSilent(server);
		var next = await restarted.GetTokenAsync(requested, default);
		next.Token.Should().NotBe(first.Token, "each request must delegate refresh to the SDK");
		mock.InteractiveStarts.Should().Be(1);
		creations.Should().OnlyContain(o => o.DisableAutomaticAuthentication && !o.TokenCachePersistenceOptions.UnsafeAllowUnencryptedStorage);
		creations.Last().AuthenticationRecord.Username.Should().Be("fixture@example.test");
		creations.Select(o => o.TokenCachePersistenceOptions.Name).Distinct().Should().ContainSingle();
		var record = await File.ReadAllTextAsync(Directory.GetFiles(_directory).Single());
		record.Should().NotContain(first.Token).And.NotContain(next.Token);
		foreach (var changed in new[] { Entry.Replace("example.test", "other.test"), Entry.Replace("/read", "/write"), Entry.Replace("555555555555", "666666666666"), Entry.Replace("10000000", "20000000") })
		{
			var separate = NewStore().OpenSilent(Parse(changed));
			Func<Task> use = async () => await separate.GetTokenAsync(requested, default);
			await use.Should().ThrowAsync<InvalidOperationException>().WithMessage("*orchestra mcp connect work*");
		}
		mock.InteractiveStarts.Should().Be(1);
	}

	[Fact]
	public async Task CancelledReconnectKeepsThePreviousAccountAndCorruptRecordFailsWithoutPrompting()
	{
		var mock = new RecordingCredential();
		var store = new EntraMcpCredentials(_directory, _ => mock);
		var server = Parse();
		await store.ConnectAsync(server, default);
		var path = Directory.GetFiles(_directory).Single();
		var saved = await File.ReadAllTextAsync(path);
		Func<Task> cancelled = () => store.ConnectAsync(server, new CancellationToken(canceled: true));
		await cancelled.Should().ThrowAsync<OperationCanceledException>();
		(await File.ReadAllTextAsync(path)).Should().Be(saved);
		await File.WriteAllTextAsync(path, "not valid JSON");
		Func<Task> silent = async () => await store.OpenSilent(server).GetTokenAsync(new(server.Auth!.Scopes), default);
		await silent.Should().ThrowAsync<InvalidOperationException>().WithMessage("*orchestra mcp connect work*");
		mock.InteractiveStarts.Should().Be(1);
	}

	private sealed class RecordingCredential : InteractiveBrowserCredential
	{
		public int InteractiveStarts { get; private set; }
		private int _requests;
		public override Task<AuthenticationRecord> AuthenticateAsync(TokenRequestContext request, CancellationToken token)
		{
			token.ThrowIfCancellationRequested();
			InteractiveStarts++;
			using var stream = new MemoryStream(Encoding.UTF8.GetBytes("""
				{"version":"1.0","username":"fixture@example.test","authority":"https://login.microsoftonline.com/","homeAccountId":"fixture.tenant","clientId":"10000000-0000-0000-0000-000000000001","tenantId":"11111111-2222-3333-4444-555555555555"}
				"""));
			return Task.FromResult(AuthenticationRecord.Deserialize(stream, token));
		}
		public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext request, CancellationToken token) =>
			ValueTask.FromResult(new AccessToken($"fake-access-token-{Interlocked.Increment(ref _requests)}", DateTimeOffset.UtcNow.AddMinutes(5)));
	}
	public void Dispose() => Directory.Delete(_directory, recursive: true);
}
