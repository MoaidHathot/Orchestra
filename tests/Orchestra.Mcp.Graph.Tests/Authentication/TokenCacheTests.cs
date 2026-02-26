using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orchestra.Mcp.Graph.Authentication;
using Orchestra.Mcp.Graph.Configuration;
using Xunit;

namespace Orchestra.Mcp.Graph.Tests.Authentication;

public class TokenCacheTests : IDisposable
{
    private readonly string _testCachePath;
    private readonly GraphOptions _options;
    private readonly ILogger<TokenCache> _logger;

    public TokenCacheTests()
    {
        _testCachePath = Path.Combine(Path.GetTempPath(), $"test_token_cache_{Guid.NewGuid()}.json");
        _options = new GraphOptions { TokenCachePath = _testCachePath };
        _logger = Substitute.For<ILogger<TokenCache>>();
    }

    public void Dispose()
    {
        if (File.Exists(_testCachePath))
        {
            File.Delete(_testCachePath);
        }
    }

    [Fact]
    public void Load_WhenFileDoesNotExist_ReturnsNull()
    {
        var cache = new TokenCache(_options, _logger);

        var result = cache.Load();

        result.Should().BeNull();
    }

    [Fact]
    public void Save_ThenLoad_ReturnsSavedData()
    {
        var cache = new TokenCache(_options, _logger);
        var data = new CachedTokenData
        {
            AccessToken = "test-access-token",
            RefreshToken = "test-refresh-token"
        };

        cache.Save(data);
        var result = cache.Load();

        result.Should().NotBeNull();
        result!.AccessToken.Should().Be("test-access-token");
        result.RefreshToken.Should().Be("test-refresh-token");
    }

    [Fact]
    public void Clear_RemovesTokenFile()
    {
        var cache = new TokenCache(_options, _logger);
        cache.Save(new CachedTokenData { AccessToken = "test" });

        File.Exists(_testCachePath).Should().BeTrue();

        cache.Clear();

        File.Exists(_testCachePath).Should().BeFalse();
    }

    [Fact]
    public void Load_WithInvalidJson_ReturnsNull()
    {
        File.WriteAllText(_testCachePath, "not valid json");
        var cache = new TokenCache(_options, _logger);

        var result = cache.Load();

        result.Should().BeNull();
    }

    [Fact]
    public void Save_CreatesDirectoryIfNotExists()
    {
        var nestedPath = Path.Combine(Path.GetTempPath(), $"nested_{Guid.NewGuid()}", "subdir", "tokens.json");
        var options = new GraphOptions { TokenCachePath = nestedPath };
        var cache = new TokenCache(options, _logger);

        cache.Save(new CachedTokenData { AccessToken = "test" });

        File.Exists(nestedPath).Should().BeTrue();

        // Cleanup
        var dir = Path.GetDirectoryName(Path.GetDirectoryName(nestedPath));
        if (dir != null && Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
