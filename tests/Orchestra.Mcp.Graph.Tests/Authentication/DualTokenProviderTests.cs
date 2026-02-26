using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orchestra.Mcp.Graph.Authentication;
using Orchestra.Mcp.Graph.Configuration;
using Xunit;

namespace Orchestra.Mcp.Graph.Tests.Authentication;

public class DualTokenProviderTests
{
    [Fact]
    public void HasToken_InvalidTokenType_ReturnsFalse()
    {
        // Arrange
        var options = new GraphOptions();
        var logger = Substitute.For<ILogger<DualTokenProvider>>();
        var azureCliLogger = Substitute.For<ILogger<AzureCliTokenProvider>>();
        var oauthLogger = Substitute.For<ILogger<OAuthTokenProvider>>();
        var tokenCacheLogger = Substitute.For<ILogger<TokenCache>>();

        var azureCliProvider = new AzureCliTokenProvider(azureCliLogger);
        var tokenCache = new TokenCache(options, tokenCacheLogger);
        var oauthProvider = new OAuthTokenProvider(options, tokenCache, new HttpClient(), oauthLogger);

        var provider = new DualTokenProvider(azureCliProvider, oauthProvider, logger);

        // Act
        var result = provider.HasToken((TokenType)999);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void AzureCliProvider_ReturnsSameInstance()
    {
        // Arrange
        var options = new GraphOptions();
        var logger = Substitute.For<ILogger<DualTokenProvider>>();
        var azureCliLogger = Substitute.For<ILogger<AzureCliTokenProvider>>();
        var oauthLogger = Substitute.For<ILogger<OAuthTokenProvider>>();
        var tokenCacheLogger = Substitute.For<ILogger<TokenCache>>();

        var azureCliProvider = new AzureCliTokenProvider(azureCliLogger);
        var tokenCache = new TokenCache(options, tokenCacheLogger);
        var oauthProvider = new OAuthTokenProvider(options, tokenCache, new HttpClient(), oauthLogger);

        var provider = new DualTokenProvider(azureCliProvider, oauthProvider, logger);

        // Assert
        provider.AzureCliProvider.Should().BeSameAs(azureCliProvider);
    }

    [Fact]
    public void OAuthProvider_ReturnsSameInstance()
    {
        // Arrange
        var options = new GraphOptions();
        var logger = Substitute.For<ILogger<DualTokenProvider>>();
        var azureCliLogger = Substitute.For<ILogger<AzureCliTokenProvider>>();
        var oauthLogger = Substitute.For<ILogger<OAuthTokenProvider>>();
        var tokenCacheLogger = Substitute.For<ILogger<TokenCache>>();

        var azureCliProvider = new AzureCliTokenProvider(azureCliLogger);
        var tokenCache = new TokenCache(options, tokenCacheLogger);
        var oauthProvider = new OAuthTokenProvider(options, tokenCache, new HttpClient(), oauthLogger);

        var provider = new DualTokenProvider(azureCliProvider, oauthProvider, logger);

        // Assert
        provider.OAuthProvider.Should().BeSameAs(oauthProvider);
    }

    [Fact]
    public void HasToken_OAuth_WhenNoTokenCached_ReturnsFalse()
    {
        // Arrange
        var options = new GraphOptions
        {
            TokenCachePath = Path.Combine(Path.GetTempPath(), $"test_no_token_{Guid.NewGuid()}.json")
        };
        var logger = Substitute.For<ILogger<DualTokenProvider>>();
        var azureCliLogger = Substitute.For<ILogger<AzureCliTokenProvider>>();
        var oauthLogger = Substitute.For<ILogger<OAuthTokenProvider>>();
        var tokenCacheLogger = Substitute.For<ILogger<TokenCache>>();

        var azureCliProvider = new AzureCliTokenProvider(azureCliLogger);
        var tokenCache = new TokenCache(options, tokenCacheLogger);
        var oauthProvider = new OAuthTokenProvider(options, tokenCache, new HttpClient(), oauthLogger);

        var provider = new DualTokenProvider(azureCliProvider, oauthProvider, logger);

        // Act
        var result = provider.HasToken(TokenType.OAuth);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void HasToken_AzureCli_WhenNotAuthenticated_ReturnsFalse()
    {
        // Arrange
        var options = new GraphOptions();
        var logger = Substitute.For<ILogger<DualTokenProvider>>();
        var azureCliLogger = Substitute.For<ILogger<AzureCliTokenProvider>>();
        var oauthLogger = Substitute.For<ILogger<OAuthTokenProvider>>();
        var tokenCacheLogger = Substitute.For<ILogger<TokenCache>>();

        var azureCliProvider = new AzureCliTokenProvider(azureCliLogger);
        var tokenCache = new TokenCache(options, tokenCacheLogger);
        var oauthProvider = new OAuthTokenProvider(options, tokenCache, new HttpClient(), oauthLogger);

        var provider = new DualTokenProvider(azureCliProvider, oauthProvider, logger);

        // Act - Azure CLI provider starts without token until GetTokenAsync is called
        var result = provider.HasToken(TokenType.AzureCli);

        // Assert
        result.Should().BeFalse();
    }
}
