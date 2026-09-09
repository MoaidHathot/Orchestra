using FluentAssertions;
using Xunit;

namespace Orchestra.Portal.E2E;

/// <summary>
/// End-to-end coverage of the sidebar's history filter UI.
///
/// The data-driven behaviour (server-side filtering, projection of lineage fields,
/// query parameter parsing) is covered by RunsApiHistoryProjectionTests in
/// Orchestra.Host.Tests. The Vitest component tests cover state transitions and
/// callback contracts. This test closes the loop by asserting the new
/// HistoryFilterSelector mounts and behaves correctly inside a real browser.
/// </summary>
[Trait("Category", "E2E")]
public class HistoryFilterUiTests : PlaywrightTestBase
{
	public HistoryFilterUiTests(PortalServerFixture server) : base(server)
	{
	}

	[Fact]
	public async Task HistoryFilterTrigger_IsPresentInTheSidebar()
	{
		// Arrange + Act
		await NavigateToPortalAsync();

		// The collapsed sidebar may hide the section behind a toggle. Make sure the
		// "Recent Executions" section is expanded by clicking its header if needed.
		var historyHeader = Page.Locator(".history-header").First;
		var historyHeaderCount = await historyHeader.CountAsync();
		historyHeaderCount.Should().BeGreaterThan(0,
			"the Recent Executions panel header is required to host the filter trigger");

		// If collapsed, expand so the filter trigger button is interactable.
		var collapsedSection = Page.Locator(".history-section.collapsed").First;
		if (await collapsedSection.CountAsync() > 0)
		{
			await historyHeader.ClickAsync();
		}

		// Assert: the new filter trigger button replaces the legacy single-button filter
		var filterTrigger = Page.Locator("button[aria-label='History filters']").First;
		(await filterTrigger.CountAsync()).Should().Be(1,
			"the HistoryFilterSelector trigger button must be rendered exactly once");
	}

	[Fact]
	public async Task HistoryFilterTrigger_OpensAndClosesDropdownOnClick()
	{
		// Arrange
		await NavigateToPortalAsync();

		// Expand the section if collapsed
		var collapsedSection = Page.Locator(".history-section.collapsed").First;
		if (await collapsedSection.CountAsync() > 0)
		{
			await Page.Locator(".history-header").First.ClickAsync();
		}

		var filterTrigger = Page.Locator("button[aria-label='History filters']").First;
		await filterTrigger.WaitForAsync(new() { Timeout = 5000 });

		// Act: open
		await filterTrigger.ClickAsync();

		// Assert: dropdown shows expected sections
		var dropdown = Page.Locator(".history-filter-dropdown").First;
		await dropdown.WaitForAsync(new() { Timeout = 3000 });
		(await dropdown.IsVisibleAsync()).Should().BeTrue();

		var sectionHeaders = await Page.Locator(".history-filter-section-header").AllInnerTextsAsync();
		var headerText = string.Join(", ", sectionHeaders);
		headerText.Should().Contain("Scope", "the Scope radio section is required");
		headerText.Should().Contain("Origins", "the Origins multi-select section is required");
		headerText.Should().Contain("Statuses", "the Statuses multi-select section is required");

		// Act: close via Escape
		await Page.Keyboard.PressAsync("Escape");
		await Task.Delay(200);

		// Assert: dropdown is gone
		(await Page.Locator(".history-filter-dropdown").CountAsync()).Should().Be(0,
			"the dropdown should close on Escape");
	}

	[Fact]
	public async Task HistoryFilterTrigger_TogglingScopeUpdatesPersistedState()
	{
		// Arrange
		await NavigateToPortalAsync();

		var collapsedSection = Page.Locator(".history-section.collapsed").First;
		if (await collapsedSection.CountAsync() > 0)
		{
			await Page.Locator(".history-header").First.ClickAsync();
		}

		var filterTrigger = Page.Locator("button[aria-label='History filters']").First;
		await filterTrigger.WaitForAsync(new() { Timeout = 5000 });
		await filterTrigger.ClickAsync();

		// Act: select "Top-level only" radio
		var topLevelRadio = Page.Locator("input[type='radio'][name='history-scope']").Nth(1);
		await topLevelRadio.WaitForAsync(new() { Timeout = 3000 });
		await topLevelRadio.CheckAsync();

		// Allow react state + persistence to flush
		await Task.Delay(200);

		// Assert: localStorage now reflects scope='roots'
		var stored = await Page.EvaluateAsync<string?>(
			"() => localStorage.getItem('orchestra-history-filters.v1')");
		stored.Should().NotBeNullOrEmpty();
		stored!.Should().Contain("\"scope\":\"roots\"",
			"selecting Top-level only must persist scope='roots' to localStorage");
	}
}
