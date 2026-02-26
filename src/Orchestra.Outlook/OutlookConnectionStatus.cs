namespace Orchestra.Outlook;

/// <summary>
/// Represents the connection status of the Outlook Graph API service.
/// </summary>
public enum OutlookConnectionStatus
{
	/// <summary>Not connected/authenticated.</summary>
	Disconnected,

	/// <summary>Currently attempting to authenticate.</summary>
	Authenticating,

	/// <summary>Successfully authenticated and connected.</summary>
	Connected,

	/// <summary>Authentication failed - check credentials.</summary>
	AuthenticationFailed,

	/// <summary>Token expired, needs refresh.</summary>
	TokenExpired,

	/// <summary>Insufficient permissions - check app registration scopes.</summary>
	InsufficientPermissions,

	/// <summary>An error occurred.</summary>
	Error
}
