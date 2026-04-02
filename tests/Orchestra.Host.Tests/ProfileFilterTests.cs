using FluentAssertions;
using Orchestra.Host.Profiles;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// Unit tests for ProfileFilter matching logic.
/// </summary>
public class ProfileFilterTests
{
	[Fact]
	public void Matches_WildcardTag_MatchesAnyOrchestration()
	{
		var filter = new ProfileFilter { Tags = ["*"] };

		filter.Matches("orch-1", ["tag-a"]).Should().BeTrue();
		filter.Matches("orch-2", []).Should().BeTrue();
		filter.Matches("orch-3", ["tag-a", "tag-b"]).Should().BeTrue();
	}

	[Fact]
	public void Matches_WildcardTag_ExcludedIdTakesPrecedence()
	{
		var filter = new ProfileFilter
		{
			Tags = ["*"],
			ExcludeOrchestrationIds = ["orch-excluded"]
		};

		filter.Matches("orch-1", ["tag-a"]).Should().BeTrue();
		filter.Matches("orch-excluded", ["tag-a"]).Should().BeFalse();
	}

	[Fact]
	public void Matches_TagIntersection_MatchesWhenTagOverlaps()
	{
		var filter = new ProfileFilter { Tags = ["production", "monitoring"] };

		filter.Matches("orch-1", ["production"]).Should().BeTrue();
		filter.Matches("orch-2", ["monitoring", "alerts"]).Should().BeTrue();
		filter.Matches("orch-3", ["development"]).Should().BeFalse();
		filter.Matches("orch-4", []).Should().BeFalse();
	}

	[Fact]
	public void Matches_TagIntersection_IsCaseInsensitive()
	{
		var filter = new ProfileFilter { Tags = ["Production"] };

		filter.Matches("orch-1", ["production"]).Should().BeTrue();
		filter.Matches("orch-2", ["PRODUCTION"]).Should().BeTrue();
	}

	[Fact]
	public void Matches_ExplicitOrchestrationIds_MatchesById()
	{
		var filter = new ProfileFilter { OrchestrationIds = ["orch-1", "orch-2"] };

		filter.Matches("orch-1", []).Should().BeTrue();
		filter.Matches("orch-2", ["some-tag"]).Should().BeTrue();
		filter.Matches("orch-3", []).Should().BeFalse();
	}

	[Fact]
	public void Matches_ExplicitOrchestrationIds_CaseInsensitive()
	{
		var filter = new ProfileFilter { OrchestrationIds = ["orch-ABC"] };

		filter.Matches("orch-abc", []).Should().BeTrue();
		filter.Matches("ORCH-ABC", []).Should().BeTrue();
	}

	[Fact]
	public void Matches_ExcludedIdOverridesExplicitInclusion()
	{
		var filter = new ProfileFilter
		{
			OrchestrationIds = ["orch-1"],
			ExcludeOrchestrationIds = ["orch-1"]
		};

		filter.Matches("orch-1", []).Should().BeFalse();
	}

	[Fact]
	public void Matches_ExcludedIdOverridesTagMatch()
	{
		var filter = new ProfileFilter
		{
			Tags = ["production"],
			ExcludeOrchestrationIds = ["orch-1"]
		};

		filter.Matches("orch-1", ["production"]).Should().BeFalse();
		filter.Matches("orch-2", ["production"]).Should().BeTrue();
	}

	[Fact]
	public void Matches_EmptyFilter_MatchesNothing()
	{
		var filter = new ProfileFilter();

		filter.Matches("orch-1", []).Should().BeFalse();
		filter.Matches("orch-2", ["tag-a"]).Should().BeFalse();
	}

	[Fact]
	public void Matches_CombinedTagsAndIds_UnionBehavior()
	{
		var filter = new ProfileFilter
		{
			Tags = ["monitoring"],
			OrchestrationIds = ["orch-special"]
		};

		// Matches by tag
		filter.Matches("orch-1", ["monitoring"]).Should().BeTrue();
		// Matches by explicit ID
		filter.Matches("orch-special", []).Should().BeTrue();
		// No match
		filter.Matches("orch-other", ["production"]).Should().BeFalse();
	}

	[Fact]
	public void IsWildcard_TrueWhenContainsStar()
	{
		new ProfileFilter { Tags = ["*"] }.IsWildcard.Should().BeTrue();
		new ProfileFilter { Tags = ["*", "production"] }.IsWildcard.Should().BeTrue();
		new ProfileFilter { Tags = ["production"] }.IsWildcard.Should().BeFalse();
		new ProfileFilter().IsWildcard.Should().BeFalse();
	}
}
