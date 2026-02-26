namespace Orchestra.Outlook;

/// <summary>
/// Configuration options for authenticating with Microsoft Graph.
/// Uses InteractiveBrowserCredential which opens the default browser for authentication.
/// This satisfies Token Protection / Conditional Access policies.
/// </summary>
public class GraphAuthOptions
{
	/// <summary>
	/// Application (client) ID from the Entra app registration.
	/// Required for authentication.
	/// </summary>
	public required string ClientId { get; init; }

	/// <summary>
	/// Azure AD/Entra tenant ID (Directory ID).
	/// Optional - if not specified, uses the common endpoint for multi-tenant apps.
	/// Can be the tenant GUID, "common", "organizations", or "consumers".
	/// </summary>
	public string? TenantId { get; init; }
}
