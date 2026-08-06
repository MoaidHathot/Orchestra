using FluentAssertions;
using Orchestra.Engine;
using Orchestra.Host.Api;
using Orchestra.Host.Persistence;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// Tests for the annotation-backed history filters (<c>?favorites=</c>, <c>?tags=</c>).
/// </summary>
public class HistoryFilterAnnotationTests
{
	private static RunIndex Index(string runId = "run1") => new()
	{
		RunId = runId,
		OrchestrationName = "test-orchestration",
		StartedAt = DateTimeOffset.UtcNow,
		CompletedAt = DateTimeOffset.UtcNow,
		Status = ExecutionStatus.Succeeded,
		FolderPath = "/tmp/x",
	};

	private static RunAnnotation Annotation(bool favorite = false, string[]? tags = null) => new()
	{
		Favorite = favorite,
		Tags = tags ?? [],
		AnnotatedAt = DateTimeOffset.UtcNow,
	};

	// ── Parse ──

	[Fact]
	public void Parse_NoAnnotationParams_LeavesFiltersNull()
	{
		var filters = HistoryFilterParser.Parse(null, null, null);

		filters.Favorites.Should().BeNull();
		filters.Tags.Should().BeNull();
		filters.HasAnyFilter.Should().BeFalse();
	}

	[Fact]
	public void Parse_Favorites_SetsFilterAndFlag()
	{
		var filters = HistoryFilterParser.Parse(null, null, null, favorites: true);

		filters.Favorites.Should().BeTrue();
		filters.HasAnyFilter.Should().BeTrue();
	}

	[Fact]
	public void Parse_Tags_SplitsAndTrims()
	{
		var filters = HistoryFilterParser.Parse(null, null, null, tags: " connect , keep ");

		filters.Tags.Should().BeEquivalentTo(["connect", "keep"]);
		filters.HasAnyFilter.Should().BeTrue();
	}

	[Fact]
	public void Parse_EmptyTagsString_TreatedAsNoFilter()
	{
		var filters = HistoryFilterParser.Parse(null, null, null, tags: "  ");

		filters.Tags.Should().BeNull();
		filters.HasAnyFilter.Should().BeFalse();
	}

	// ── Favorites ──

	[Fact]
	public void FavoritesTrue_MatchesOnlyFavorited()
	{
		var filters = HistoryFilterParser.Parse(null, null, null, favorites: true);

		HistoryFilterParser.Matches(Index(), filters, Annotation(favorite: true)).Should().BeTrue();
		HistoryFilterParser.Matches(Index(), filters, Annotation(favorite: false)).Should().BeFalse();
		HistoryFilterParser.Matches(Index(), filters, annotation: null).Should().BeFalse();
	}

	[Fact]
	public void FavoritesFalse_MatchesOnlyNonFavorited()
	{
		var filters = HistoryFilterParser.Parse(null, null, null, favorites: false);

		HistoryFilterParser.Matches(Index(), filters, Annotation(favorite: true)).Should().BeFalse();
		HistoryFilterParser.Matches(Index(), filters, annotation: null).Should().BeTrue();
	}

	// ── Tags: OR semantics ──

	[Fact]
	public void Tags_MatchWhenAnyRequestedTagIsPresent()
	{
		var filters = HistoryFilterParser.Parse(null, null, null, tags: "connect,other");

		HistoryFilterParser.Matches(Index(), filters, Annotation(tags: ["connect"]))
			.Should().BeTrue("tag filtering is OR, not AND");
	}

	[Fact]
	public void Tags_DoNotMatchWhenNoRequestedTagIsPresent()
	{
		var filters = HistoryFilterParser.Parse(null, null, null, tags: "connect");

		HistoryFilterParser.Matches(Index(), filters, Annotation(tags: ["unrelated"])).Should().BeFalse();
		HistoryFilterParser.Matches(Index(), filters, Annotation(tags: [])).Should().BeFalse();
		HistoryFilterParser.Matches(Index(), filters, annotation: null).Should().BeFalse();
	}

	[Fact]
	public void Tags_AreCaseInsensitive()
	{
		var filters = HistoryFilterParser.Parse(null, null, null, tags: "CONNECT");

		HistoryFilterParser.Matches(Index(), filters, Annotation(tags: ["connect"])).Should().BeTrue();
	}

	// ── Combination ──

	[Fact]
	public void FavoritesAndTags_BothMustMatch()
	{
		var filters = HistoryFilterParser.Parse(null, null, null, favorites: true, tags: "connect");

		HistoryFilterParser.Matches(Index(), filters, Annotation(favorite: true, tags: ["connect"]))
			.Should().BeTrue();
		HistoryFilterParser.Matches(Index(), filters, Annotation(favorite: false, tags: ["connect"]))
			.Should().BeFalse("dimensions combine with AND even though tags are internally OR");
		HistoryFilterParser.Matches(Index(), filters, Annotation(favorite: true, tags: ["other"]))
			.Should().BeFalse();
	}

	[Fact]
	public void AnnotationFilters_ComposeWithStatusFilter()
	{
		var filters = HistoryFilterParser.Parse(null, null, "Failed", favorites: true);

		HistoryFilterParser.Matches(Index(), filters, Annotation(favorite: true))
			.Should().BeFalse("the run succeeded, so the status filter excludes it");
	}

	[Fact]
	public void NoAnnotationFilter_IgnoresAnnotationEntirely()
	{
		var filters = HistoryFilterParser.Parse(null, null, null);

		HistoryFilterParser.Matches(Index(), filters, annotation: null).Should().BeTrue();
		HistoryFilterParser.Matches(Index(), filters, Annotation(favorite: true)).Should().BeTrue();
	}
}
