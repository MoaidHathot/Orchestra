using Microsoft.Playwright;
using Xunit;

namespace Orchestra.Portal.E2E;

/// <summary>
/// Base class for Playwright E2E tests providing common setup and utilities.
/// </summary>
public abstract class PlaywrightTestBase : IClassFixture<PortalServerFixture>, IAsyncLifetime
{
	protected readonly PortalServerFixture Server;
	protected IPlaywright Playwright { get; private set; } = null!;
	protected IBrowser Browser { get; private set; } = null!;
	protected IPage Page { get; private set; } = null!;
	
	/// <summary>
	/// List of console errors captured during the test.
	/// </summary>
	protected List<string> ConsoleErrors { get; } = new();

	protected PlaywrightTestBase(PortalServerFixture server)
	{
		Server = server;
	}

	public async Task InitializeAsync()
	{
		// Skip initialization if server is not running
		if (!Server.IsRunning)
		{
			// Throw a descriptive exception - xUnit will report this as a failure
			// but it makes clear the issue is server availability, not test logic
			throw new InvalidOperationException($"E2E Test Skipped: Portal server is not running. {Server.StartupError ?? "Server could not be started."}");
		}
		
		Playwright = await Microsoft.Playwright.Playwright.CreateAsync();
		Browser = await Playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
		{
			Headless = true // Set to false for debugging
		});
		Page = await Browser.NewPageAsync();
		
		// Capture console errors for test assertions
		Page.Console += (_, msg) =>
		{
			if (msg.Type == "error")
			{
				ConsoleErrors.Add(msg.Text);
			}
		};
	}

	public async Task DisposeAsync()
	{
		if (Page != null)
		{
			await Page.CloseAsync();
		}
		if (Browser != null)
		{
			await Browser.CloseAsync();
		}
		Playwright?.Dispose();
	}

	/// <summary>
	/// Navigate to the Portal home page and wait for it to load.
	/// </summary>
	protected async Task NavigateToPortalAsync()
	{
		await Page.GotoAsync(Server.BaseUrl);
		// Wait for the React app to render - check for any of these indicators
		// The app renders "Orchestrations" text and has an app-container div
		await Page.WaitForFunctionAsync(
			"() => document.body.innerText.includes('Orchestrations') || document.querySelector('.app-container') !== null",
			null,
			new() { Timeout = 15000 });
	}

	/// <summary>
	/// Wait for a specific text to appear on the page.
	/// </summary>
	protected async Task WaitForTextAsync(string text, int timeoutMs = 5000)
	{
		await Page.WaitForFunctionAsync($"() => document.body.innerText.includes('{text}')", null, new()
		{
			Timeout = timeoutMs
		});
	}

	/// <summary>
	/// Click a button containing specific text.
	/// </summary>
	protected async Task ClickButtonAsync(string buttonText)
	{
		await Page.Locator($"button:has-text('{buttonText}')").First.ClickAsync();
	}

	/// <summary>
	/// Upload an orchestration JSON file.
	/// </summary>
	protected async Task UploadOrchestrationAsync(string jsonContent, string fileName = "test-orchestration.json")
	{
		var tempPath = Path.Combine(Path.GetTempPath(), fileName);
		await File.WriteAllTextAsync(tempPath, jsonContent);

		try
		{
			var fileChooser = await Page.RunAndWaitForFileChooserAsync(async () =>
			{
				await Page.Locator("input[type='file']").First.ClickAsync();
			});
			await fileChooser.SetFilesAsync(tempPath);
		}
		finally
		{
			if (File.Exists(tempPath))
				File.Delete(tempPath);
		}
	}
	
	/// <summary>
	/// Asserts that no React rendering errors occurred.
	/// Checks for common React error patterns in console output.
	/// </summary>
	protected void AssertNoReactErrors()
	{
		var reactErrors = ConsoleErrors
			.Where(e => e.Contains("Element type is invalid") ||
			            e.Contains("is not defined") ||
			            e.Contains("Cannot read property") ||
			            e.Contains("undefined is not") ||
			            e.Contains("got: undefined"))
			.ToList();
		
		if (reactErrors.Any())
		{
			throw new Exception($"React rendering errors detected:\n{string.Join("\n", reactErrors)}");
		}
	}
}
