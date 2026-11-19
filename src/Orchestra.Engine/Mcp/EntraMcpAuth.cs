using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orchestra.Engine;

/// <summary>Public-client delegated authentication for a host-owned remote MCP. No credentials in configuration.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class EntraMcpAuth
{
	public required string Type { get; init; }
	public required Guid ClientId { get; init; }
	public required Guid TenantId { get; init; }
	public required string[] Scopes { get; init; }
	public string RedirectUri { get; init; } = "http://localhost/";

	public static void ValidateManagedConnection(RemoteMcp server)
	{
		if (!server.RequiresHost) return;
		static bool ValidName(string? s) => s is { Length: > 0 and <= 128 } && s is not ("." or "..") &&
			s.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');
		if (!ValidName(server.Name)) throw new JsonException("Invalid managed MCP server name.");
		if (server.AllowedTools is {} tools && (tools.Length == 0 || !tools.All(ValidName) || tools.Distinct(StringComparer.Ordinal).Count() != tools.Length))
			throw new JsonException($"MCP '{server.Name}': allowedTools must contain unique, exact tool names (no wildcards).");
		if (server.Auth is not {} auth) return;
		if (auth.Type != "entra" || auth.ClientId == Guid.Empty || auth.TenantId == Guid.Empty ||
			auth.Scopes is not { Length: > 0 } || auth.Scopes.Any(s => string.IsNullOrWhiteSpace(s) || s.Any(char.IsWhiteSpace)))
			throw new JsonException($"MCP '{server.Name}': specify auth.type 'entra', clientId, tenantId and explicit delegated scopes.");
		if (!Uri.TryCreate(server.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps ||
			endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 ||
			server.Endpoint.Contains('{') || server.Headers.Count != 0)
			throw new JsonException($"MCP '{server.Name}': Entra authentication requires a fixed HTTPS endpoint without user info, query, fragment or custom headers.");
		if (!Uri.TryCreate(auth.RedirectUri, UriKind.Absolute, out var redirect) || redirect.Scheme != Uri.UriSchemeHttp ||
			!redirect.IsLoopback || redirect.UserInfo.Length != 0 || redirect.Query.Length != 0 || redirect.Fragment.Length != 0)
			throw new JsonException($"MCP '{server.Name}': redirectUri must be a registered HTTP loopback callback.");
	}
}
