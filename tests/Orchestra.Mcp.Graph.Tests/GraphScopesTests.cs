using FluentAssertions;
using Orchestra.Mcp.Graph.Configuration;
using Xunit;

namespace Orchestra.Mcp.Graph.Tests;

public class GraphScopesTests
{
    [Fact]
    public void OAuthScopes_ContainsExpectedScopes()
    {
        GraphScopes.OAuthScopes.Should().Contain(GraphScopes.ChatRead);
        GraphScopes.OAuthScopes.Should().Contain(GraphScopes.MailRead);
        GraphScopes.OAuthScopes.Should().Contain(GraphScopes.ChannelMessageReadAll);
        GraphScopes.OAuthScopes.Should().Contain(GraphScopes.OfflineAccess);
        GraphScopes.OAuthScopes.Should().Contain(GraphScopes.OpenId);
        GraphScopes.OAuthScopes.Should().Contain(GraphScopes.Profile);
        GraphScopes.OAuthScopes.Should().Contain(GraphScopes.Email);
    }

    [Fact]
    public void OAuthScopesString_IsSpaceSeparated()
    {
        var scopesString = GraphScopes.OAuthScopesString;

        scopesString.Should().Contain(" ");
        scopesString.Should().Contain(GraphScopes.ChatRead);
        scopesString.Should().Contain(GraphScopes.MailRead);
    }

    [Fact]
    public void OAuthScopes_HasCorrectCount()
    {
        // 7 Graph scopes + offline_access + openid + profile + email = 11
        GraphScopes.OAuthScopes.Should().HaveCount(11);
    }

    [Fact]
    public void AllScopes_AreFullyQualified()
    {
        var graphScopes = GraphScopes.OAuthScopes
            .Where(s => s.StartsWith("https://graph.microsoft.com/"));

        // 7 Graph API scopes
        graphScopes.Should().HaveCount(7);
    }
}
