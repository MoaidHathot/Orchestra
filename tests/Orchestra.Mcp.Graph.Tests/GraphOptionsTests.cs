using FluentAssertions;
using Orchestra.Mcp.Graph.Configuration;
using Xunit;

namespace Orchestra.Mcp.Graph.Tests;

public class GraphOptionsTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var options = new GraphOptions();

        options.ClientId.Should().Be(GraphOptions.DefaultClientId);
        options.TenantId.Should().Be(GraphOptions.DefaultTenantId);
        options.RedirectUri.Should().Be(GraphOptions.DefaultRedirectUri);
        options.OAuthPort.Should().Be(8400);
        options.GraphBaseUrl.Should().Be("https://graph.microsoft.com/v1.0");
        options.GraphBetaUrl.Should().Be("https://graph.microsoft.com/beta");
        options.OAuthTimeoutSeconds.Should().Be(120);
    }

    [Fact]
    public void AuthorizeEndpoint_ContainsTenantId()
    {
        var options = new GraphOptions();

        options.AuthorizeEndpoint.Should().Contain(options.TenantId);
        options.AuthorizeEndpoint.Should().EndWith("/oauth2/v2.0/authorize");
    }

    [Fact]
    public void TokenEndpoint_ContainsTenantId()
    {
        var options = new GraphOptions();

        options.TokenEndpoint.Should().Contain(options.TenantId);
        options.TokenEndpoint.Should().EndWith("/oauth2/v2.0/token");
    }

    [Fact]
    public void TokenCachePath_DefaultsToUserProfile()
    {
        var options = new GraphOptions();

        options.TokenCachePath.Should().Contain(".graph_token.json");
        options.TokenCachePath.Should().Contain(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    [Fact]
    public void OAuthPort_ExtractsFromRedirectUri()
    {
        var options = new GraphOptions { RedirectUri = "http://localhost:9999" };

        options.OAuthPort.Should().Be(9999);
    }
}
