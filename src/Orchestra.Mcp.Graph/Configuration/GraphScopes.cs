namespace Orchestra.Mcp.Graph.Configuration;

/// <summary>
/// Microsoft Graph API permission scopes.
/// </summary>
public static class GraphScopes
{
    /// <summary>
    /// Read user's chats.
    /// </summary>
    public const string ChatRead = "https://graph.microsoft.com/Chat.Read";

    /// <summary>
    /// Read user's mail.
    /// </summary>
    public const string MailRead = "https://graph.microsoft.com/Mail.Read";

    /// <summary>
    /// Read all channel messages.
    /// </summary>
    public const string ChannelMessageReadAll = "https://graph.microsoft.com/ChannelMessage.Read.All";

    /// <summary>
    /// Read all SharePoint sites.
    /// </summary>
    public const string SitesReadAll = "https://graph.microsoft.com/Sites.Read.All";

    /// <summary>
    /// Read people data.
    /// </summary>
    public const string PeopleReadAll = "https://graph.microsoft.com/People.Read.All";

    /// <summary>
    /// Read online meeting transcripts.
    /// </summary>
    public const string OnlineMeetingTranscriptReadAll = "https://graph.microsoft.com/OnlineMeetingTranscript.Read.All";

    /// <summary>
    /// Read external items.
    /// </summary>
    public const string ExternalItemReadAll = "https://graph.microsoft.com/ExternalItem.Read.All";

    /// <summary>
    /// Offline access (refresh tokens).
    /// </summary>
    public const string OfflineAccess = "offline_access";

    /// <summary>
    /// OpenID Connect scope.
    /// </summary>
    public const string OpenId = "openid";

    /// <summary>
    /// Profile scope.
    /// </summary>
    public const string Profile = "profile";

    /// <summary>
    /// Email scope.
    /// </summary>
    public const string Email = "email";

    /// <summary>
    /// All scopes required for the OAuth token (WorkIQ scopes).
    /// </summary>
    public static readonly string[] OAuthScopes =
    [
        ChatRead,
        MailRead,
        ChannelMessageReadAll,
        SitesReadAll,
        PeopleReadAll,
        OnlineMeetingTranscriptReadAll,
        ExternalItemReadAll,
        OfflineAccess,
        OpenId,
        Profile,
        Email
    ];

    /// <summary>
    /// Scopes as a space-separated string for OAuth requests.
    /// </summary>
    public static string OAuthScopesString => string.Join(" ", OAuthScopes);
}
