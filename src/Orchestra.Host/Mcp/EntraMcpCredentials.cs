using System.Security.Cryptography;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Orchestra.Engine;

namespace Orchestra.Host.Mcp;

/// <summary>One protected SDK token cache per endpoint and delegated identity. Only Connect can open a browser.</summary>
public sealed class EntraMcpCredentials
{
	private readonly string _directory;
	private readonly Func<InteractiveBrowserCredentialOptions, InteractiveBrowserCredential> _create;

	public EntraMcpCredentials() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"Orchestra", "mcp-auth"), options => new(options)) { }

	internal EntraMcpCredentials(string directory, Func<InteractiveBrowserCredentialOptions, InteractiveBrowserCredential> create)
	{
		_directory = directory;
		_create = create;
	}

	private (InteractiveBrowserCredentialOptions Options, string RecordPath) Settings(RemoteMcp server)
	{
		EntraMcpAuth.ValidateManagedConnection(server);
		var auth = server.Auth ?? throw new ArgumentException($"MCP '{server.Name}' has no Entra configuration.");
		var identity = JsonSerializer.SerializeToUtf8Bytes(new { Endpoint = new Uri(server.Endpoint).AbsoluteUri,
			auth.TenantId, auth.ClientId, auth.RedirectUri, Scopes = auth.Scopes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() });
		var key = "Orchestra-MCP-" + Convert.ToHexStringLower(SHA256.HashData(identity));
		return (new InteractiveBrowserCredentialOptions
		{
			TenantId = auth.TenantId.ToString(), ClientId = auth.ClientId.ToString(), RedirectUri = new Uri(auth.RedirectUri),
			DisableAutomaticAuthentication = true,
			TokenCachePersistenceOptions = new() { Name = key, UnsafeAllowUnencryptedStorage = false },
		}, Path.Combine(_directory, key + ".account.json"));
	}

	public async Task ConnectAsync(RemoteMcp server, CancellationToken token)
	{
		var (options, path) = Settings(server);
		var credential = _create(options);
		var account = await credential.AuthenticateAsync(new TokenRequestContext(server.Auth!.Scopes), token);
		// AuthenticationRecord is SDK account selection metadata, not a token. Tokens never go in this file.
		Directory.CreateDirectory(_directory);
		var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
			if (!OperatingSystem.IsWindows()) fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
			await using (var stream = new FileStream(temp, fileOptions)) await account.SerializeAsync(stream, token);
			File.Move(temp, path, overwrite: true);
		}
		finally { File.Delete(temp); }
	}

	internal TokenCredential OpenSilent(RemoteMcp server)
		=> new SilentCredential(() => Restore(server), $"Sign in with 'orchestra mcp connect {server.Name}' and retry.");

	private InteractiveBrowserCredential Restore(RemoteMcp server)
	{
		var (options, path) = Settings(server);
		try
		{
			using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
			options.AuthenticationRecord = AuthenticationRecord.Deserialize(stream);
			if (!Guid.TryParse(options.AuthenticationRecord.ClientId, out var client) || client != server.Auth!.ClientId ||
				!Guid.TryParse(options.AuthenticationRecord.TenantId, out var tenant) || tenant != server.Auth.TenantId ||
				string.IsNullOrEmpty(options.AuthenticationRecord.HomeAccountId))
				throw new InvalidDataException("The selected account does not match this MCP connection.");
		}
		catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or JsonException or InvalidDataException or FormatException or InvalidOperationException)
		{
			throw new InvalidOperationException($"Sign in with 'orchestra mcp connect {server.Name}' and retry.", ex);
		}
		return _create(options);
	}

	private sealed class SilentCredential(Func<TokenCredential> restore, string instruction) : TokenCredential
	{
		// PublicationOnly retries a failed restore: an explicit Connect can repair a missing record after host startup.
		private readonly Lazy<TokenCredential> _credential = new(restore, LazyThreadSafetyMode.PublicationOnly);

		public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
		{
			try { return _credential.Value.GetToken(requestContext, cancellationToken); }
			catch (AuthenticationRequiredException ex) { throw new InvalidOperationException(instruction, ex); }
		}
		public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
		{
			try { return await _credential.Value.GetTokenAsync(requestContext, cancellationToken); }
			catch (AuthenticationRequiredException ex) { throw new InvalidOperationException(instruction, ex); }
		}
	}
}
