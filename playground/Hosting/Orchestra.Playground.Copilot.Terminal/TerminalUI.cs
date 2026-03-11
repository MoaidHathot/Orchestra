using System.Collections.Concurrent;
using Orchestra.Engine;
using Orchestra.Host.Hosting;
using Orchestra.Host.Triggers;
using Orchestra.Host.Persistence;
using Orchestra.Host.Registry;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Orchestra.Playground.Copilot.Terminal;

/// <summary>
/// The main Terminal User Interface for Orchestra.
/// Similar to LazyGit, with keyboard navigation and panels.
/// </summary>
public class TerminalUI
{
	private readonly OrchestrationRegistry _registry;
	private readonly TriggerManager _triggerManager;
	private readonly FileSystemRunStore _runStore;
	private readonly ConcurrentDictionary<string, ActiveExecutionInfo> _activeExecutionInfos;
	private readonly TerminalOrchestrationReporter _reporter;
	private readonly TerminalExecutionCallback _executionCallback;
	private readonly OrchestrationHostOptions _hostOptions;

	private TuiView _currentView = TuiView.Dashboard;
	private int _selectedIndex;
	private string? _selectedOrchestrationId;
	private string? _selectedExecutionId;
	private bool _running = true;
	private readonly object _renderLock = new();
	private DateTime _lastRender = DateTime.MinValue;

	// Execution detail view state
	private ExecutionDetailTab _executionDetailTab = ExecutionDetailTab.Summary;
	private int _executionDetailScrollOffset;
	private int _selectedStepIndex;
	private OrchestrationRunRecord? _cachedRunRecord;

	public TerminalUI(
		OrchestrationRegistry registry,
		TriggerManager triggerManager,
		FileSystemRunStore runStore,
		ConcurrentDictionary<string, ActiveExecutionInfo> activeExecutionInfos,
		TerminalOrchestrationReporter reporter,
		ITriggerExecutionCallback executionCallback,
		OrchestrationHostOptions hostOptions)
	{
		_registry = registry;
		_triggerManager = triggerManager;
		_runStore = runStore;
		_activeExecutionInfos = activeExecutionInfos;
		_reporter = reporter;
		_executionCallback = (TerminalExecutionCallback)executionCallback;
		_hostOptions = hostOptions;
	}

	public async Task RunAsync(CancellationToken cancellationToken)
	{
		// Check if we're running in an interactive terminal
		if (!IsInteractiveConsole())
		{
			Console.WriteLine("Error: Orchestra Terminal requires an interactive terminal.");
			Console.WriteLine("Run with --help for usage information.");
			return;
		}

		Console.CursorVisible = false;
		Console.Clear();

		// Subscribe to reporter events for auto-refresh
		_reporter.OnUpdate += RequestRedraw;
		_executionCallback.OnUpdate += RequestRedraw;

		try
		{
			// Initial render
			Render();

			// Input loop
			while (_running && !cancellationToken.IsCancellationRequested)
			{
				if (Console.KeyAvailable)
				{
					var key = Console.ReadKey(intercept: true);
					HandleKeyPress(key);
					Render();
				}
				else
				{
					// Auto-refresh periodically if there are active executions
					if (_activeExecutionInfos.Count > 0 && (DateTime.Now - _lastRender).TotalMilliseconds > 500)
					{
						Render();
					}
					await Task.Delay(50, cancellationToken);
				}
			}
		}
		finally
		{
			_reporter.OnUpdate -= RequestRedraw;
			_executionCallback.OnUpdate -= RequestRedraw;
			Console.CursorVisible = true;
			Console.Clear();
		}
	}

	private void RequestRedraw()
	{
		// Throttle redraws
		if ((DateTime.Now - _lastRender).TotalMilliseconds > 100)
		{
			Render();
		}
	}

	private void HandleKeyPress(ConsoleKeyInfo key)
	{
		// Global shortcuts
		switch (key.Key)
		{
			case ConsoleKey.Q:
				_running = false;
				return;
			case ConsoleKey.D1:
				_currentView = TuiView.Dashboard;
				_selectedIndex = 0;
				return;
			case ConsoleKey.D2:
				_currentView = TuiView.Orchestrations;
				_selectedIndex = 0;
				return;
			case ConsoleKey.D3:
				_currentView = TuiView.Triggers;
				_selectedIndex = 0;
				return;
			case ConsoleKey.D4:
				_currentView = TuiView.History;
				_selectedIndex = 0;
				return;
			case ConsoleKey.D5:
				_currentView = TuiView.Active;
				_selectedIndex = 0;
				return;
			case ConsoleKey.Escape:
				if (_currentView != TuiView.Dashboard)
				{
					_currentView = TuiView.Dashboard;
					_selectedIndex = 0;
				}
				return;
		}

		// View-specific shortcuts
		switch (_currentView)
		{
			case TuiView.Dashboard:
				HandleDashboardInput(key);
				break;
			case TuiView.Orchestrations:
				HandleOrchestrationsInput(key);
				break;
			case TuiView.Triggers:
				HandleTriggersInput(key);
				break;
			case TuiView.History:
				HandleHistoryInput(key);
				break;
			case TuiView.Active:
				HandleActiveInput(key);
				break;
			case TuiView.OrchestrationDetail:
				HandleOrchestrationDetailInput(key);
				break;
			case TuiView.ExecutionDetail:
				HandleExecutionDetailInput(key);
				break;
		}
	}

	private void HandleDashboardInput(ConsoleKeyInfo key)
	{
		var options = new[] { "Orchestrations", "Triggers", "History", "Active Executions", "Quit" };
		switch (key.Key)
		{
			case ConsoleKey.UpArrow or ConsoleKey.K:
				_selectedIndex = Math.Max(0, _selectedIndex - 1);
				break;
			case ConsoleKey.DownArrow or ConsoleKey.J:
				_selectedIndex = Math.Min(options.Length - 1, _selectedIndex + 1);
				break;
			case ConsoleKey.Enter:
				switch (_selectedIndex)
				{
					case 0: _currentView = TuiView.Orchestrations; _selectedIndex = 0; break;
					case 1: _currentView = TuiView.Triggers; _selectedIndex = 0; break;
					case 2: _currentView = TuiView.History; _selectedIndex = 0; break;
					case 3: _currentView = TuiView.Active; _selectedIndex = 0; break;
					case 4: _running = false; break;
				}
				break;
		}
	}

	private void HandleOrchestrationsInput(ConsoleKeyInfo key)
	{
		var items = _registry.GetAll().ToArray();
		switch (key.Key)
		{
			case ConsoleKey.UpArrow or ConsoleKey.K:
				_selectedIndex = Math.Max(0, _selectedIndex - 1);
				break;
			case ConsoleKey.DownArrow or ConsoleKey.J:
				_selectedIndex = Math.Min(Math.Max(0, items.Length - 1), _selectedIndex + 1);
				break;
			case ConsoleKey.Enter:
				if (items.Length > 0 && _selectedIndex < items.Length)
				{
					_selectedOrchestrationId = items[_selectedIndex].Id;
					_currentView = TuiView.OrchestrationDetail;
				}
				break;
			case ConsoleKey.R:
				// Run the selected orchestration
				if (items.Length > 0 && _selectedIndex < items.Length)
				{
					var entry = items[_selectedIndex];
					// Try firing registered trigger first, otherwise run directly
					_ = RunOrchestrationAsync(entry);
				}
				break;
			case ConsoleKey.A:
				// Add orchestration file
				PromptAddOrchestration();
				break;
			case ConsoleKey.S:
				// Scan directory for orchestrations
				PromptScanDirectory();
				break;
			case ConsoleKey.D or ConsoleKey.Delete:
				// Delete/remove selected orchestration
				if (items.Length > 0 && _selectedIndex < items.Length)
				{
					var entry = items[_selectedIndex];
					RemoveOrchestration(entry.Id);
				}
				break;
		}
	}

	private void HandleTriggersInput(ConsoleKeyInfo key)
	{
		var triggers = _triggerManager.GetAllTriggers().ToArray();
		switch (key.Key)
		{
			case ConsoleKey.UpArrow or ConsoleKey.K:
				_selectedIndex = Math.Max(0, _selectedIndex - 1);
				break;
			case ConsoleKey.DownArrow or ConsoleKey.J:
				_selectedIndex = Math.Min(Math.Max(0, triggers.Length - 1), _selectedIndex + 1);
				break;
			case ConsoleKey.E:
				// Enable/disable trigger
				if (triggers.Length > 0 && _selectedIndex < triggers.Length)
				{
					var trigger = triggers[_selectedIndex];
					_triggerManager.SetTriggerEnabled(trigger.Id, !trigger.Config.Enabled);
				}
				break;
			case ConsoleKey.R:
				// Run the trigger
				if (triggers.Length > 0 && _selectedIndex < triggers.Length)
				{
					var trigger = triggers[_selectedIndex];
					_ = _triggerManager.FireTriggerAsync(trigger.Id);
				}
				break;
		}
	}

	private void HandleHistoryInput(ConsoleKeyInfo key)
	{
		var runs = _runStore.GetRunSummariesAsync(20).GetAwaiter().GetResult();
		switch (key.Key)
		{
			case ConsoleKey.UpArrow or ConsoleKey.K:
				_selectedIndex = Math.Max(0, _selectedIndex - 1);
				break;
			case ConsoleKey.DownArrow or ConsoleKey.J:
				_selectedIndex = Math.Min(Math.Max(0, runs.Count - 1), _selectedIndex + 1);
				break;
			case ConsoleKey.Enter:
				if (runs.Count > 0 && _selectedIndex < runs.Count)
				{
					_selectedExecutionId = runs[_selectedIndex].RunId;
					ResetExecutionDetailState();
					_currentView = TuiView.ExecutionDetail;
				}
				break;
			case ConsoleKey.D:
				// Delete run
				if (runs.Count > 0 && _selectedIndex < runs.Count)
				{
					var run = runs[_selectedIndex];
					_ = _runStore.DeleteRunAsync(run.OrchestrationName, run.RunId);
				}
				break;
		}
	}

	private void HandleActiveInput(ConsoleKeyInfo key)
	{
		var active = _activeExecutionInfos.Values.ToArray();
		switch (key.Key)
		{
			case ConsoleKey.UpArrow or ConsoleKey.K:
				_selectedIndex = Math.Max(0, _selectedIndex - 1);
				break;
			case ConsoleKey.DownArrow or ConsoleKey.J:
				_selectedIndex = Math.Min(Math.Max(0, active.Length - 1), _selectedIndex + 1);
				break;
			case ConsoleKey.Enter:
				if (active.Length > 0 && _selectedIndex < active.Length)
				{
					_selectedExecutionId = active[_selectedIndex].ExecutionId;
					ResetExecutionDetailState();
					_currentView = TuiView.ExecutionDetail;
				}
				break;
			case ConsoleKey.C:
				// Cancel execution
				if (active.Length > 0 && _selectedIndex < active.Length)
				{
					var exec = active[_selectedIndex];
					_triggerManager.CancelExecution(exec.ExecutionId);
				}
				break;
		}
	}

	private void HandleOrchestrationDetailInput(ConsoleKeyInfo key)
	{
		switch (key.Key)
		{
			case ConsoleKey.Backspace or ConsoleKey.Escape:
				_currentView = TuiView.Orchestrations;
				break;
			case ConsoleKey.R:
				// Run orchestration
				if (_selectedOrchestrationId != null)
				{
					var entry = _registry.Get(_selectedOrchestrationId);
					if (entry != null)
					{
						_ = RunOrchestrationAsync(entry);
					}
				}
				break;
		}
	}

	private void HandleExecutionDetailInput(ConsoleKeyInfo key)
	{
		switch (key.Key)
		{
			case ConsoleKey.Backspace or ConsoleKey.Escape:
				_currentView = TuiView.History;
				_cachedRunRecord = null;
				break;
			// Tab navigation with number keys
			case ConsoleKey.D1:
				_executionDetailTab = ExecutionDetailTab.Summary;
				_executionDetailScrollOffset = 0;
				break;
			case ConsoleKey.D2:
				_executionDetailTab = ExecutionDetailTab.Steps;
				_executionDetailScrollOffset = 0;
				break;
			case ConsoleKey.D3:
				_executionDetailTab = ExecutionDetailTab.Output;
				_executionDetailScrollOffset = 0;
				break;
			// Tab navigation with Tab key
			case ConsoleKey.Tab:
				_executionDetailTab = _executionDetailTab switch
				{
					ExecutionDetailTab.Summary => ExecutionDetailTab.Steps,
					ExecutionDetailTab.Steps => ExecutionDetailTab.Output,
					ExecutionDetailTab.Output => ExecutionDetailTab.Summary,
					_ => ExecutionDetailTab.Summary
				};
				_executionDetailScrollOffset = 0;
				break;
			// Navigation within tabs
			case ConsoleKey.UpArrow or ConsoleKey.K:
				if (_executionDetailTab == ExecutionDetailTab.Steps)
				{
					_selectedStepIndex = Math.Max(0, _selectedStepIndex - 1);
				}
				else
				{
					_executionDetailScrollOffset = Math.Max(0, _executionDetailScrollOffset - 1);
				}
				break;
			case ConsoleKey.DownArrow or ConsoleKey.J:
				if (_executionDetailTab == ExecutionDetailTab.Steps)
				{
					var stepCount = _cachedRunRecord?.StepRecords.Count ?? 0;
					_selectedStepIndex = Math.Min(Math.Max(0, stepCount - 1), _selectedStepIndex + 1);
				}
				else
				{
					_executionDetailScrollOffset++;
				}
				break;
			// Copy URL to clipboard hint
			case ConsoleKey.U:
				// Show URL in a popup-like message
				ShowRunUrl();
				break;
		}
	}

	private void ResetExecutionDetailState()
	{
		_executionDetailTab = ExecutionDetailTab.Summary;
		_executionDetailScrollOffset = 0;
		_selectedStepIndex = 0;
		_cachedRunRecord = null;
	}

	private void ShowRunUrl()
	{
		if (string.IsNullOrEmpty(_hostOptions.HostBaseUrl) || _cachedRunRecord == null)
		{
			ShowMessage("[yellow]No URL configured or run not loaded[/]", 1500);
			return;
		}

		var url = $"{_hostOptions.HostBaseUrl.TrimEnd('/')}/#/history/{Uri.EscapeDataString(_cachedRunRecord.OrchestrationName)}/{_cachedRunRecord.RunId}";
		ShowMessage($"[cyan]URL:[/] {url}", 3000);
	}

	#region Orchestration Management

	private async Task RunOrchestrationAsync(OrchestrationEntry entry)
	{
		string? executionId = null;

		// Check if orchestration has parameters that need to be collected
		var parameterNames = GetOrchestrationParameters(entry);
		Dictionary<string, string>? parameters = null;

		if (parameterNames.Length > 0)
		{
			// Prompt user for parameter values
			parameters = PromptForParameters(entry);

			if (parameters == null)
			{
				// User cancelled - return to normal view
				Render();
				return;
			}
		}

		// Try firing registered trigger first
		var (found, execId) = await _triggerManager.FireTriggerAsync(entry.Id, extraParameters: parameters);
		if (found)
		{
			executionId = execId;
		}
		else
		{
			// No registered trigger, run orchestration directly
			executionId = await _triggerManager.RunOrchestrationAsync(
				entry.Path,
				entry.McpPath,
				parameters,
				entry.Id);
		}

		// If execution started, switch to Active view
		if (executionId != null)
		{
			_currentView = TuiView.Active;
			_selectedIndex = 0;
			Render();
		}
	}

	private void PromptAddOrchestration()
	{
		Console.Clear();
		AnsiConsole.MarkupLine("[bold cyan]Add Orchestration[/]");
		AnsiConsole.MarkupLine("[dim]Enter the path to an orchestration JSON file (or press Esc to cancel)[/]\n");

		var path = PromptForPath("Orchestration file path");
		if (string.IsNullOrWhiteSpace(path))
		{
			Render();
			return;
		}

		// Normalize path
		path = Path.GetFullPath(path.Trim('"', ' '));

		if (!File.Exists(path))
		{
			ShowMessage($"[red]File not found:[/] {path}", 2000);
			return;
		}

		try
		{
			// Auto-detect mcp.json in same directory
			string? mcpPath = null;
			var dir = Path.GetDirectoryName(path)!;
			var candidateMcp = Path.Combine(dir, "mcp.json");
			if (File.Exists(candidateMcp))
			{
				mcpPath = candidateMcp;
			}

			var entry = _registry.Register(path, mcpPath);

			// Register trigger if orchestration has one
			if (entry.Orchestration.Trigger is { } trigger && trigger.Enabled)
			{
				_triggerManager.RegisterTrigger(
					entry.Path,
					entry.McpPath,
					trigger,
					null,
					TriggerSource.Json,
					entry.Id);
			}

			ShowMessage($"[green]Added:[/] {entry.Orchestration.Name} (v{entry.Orchestration.Version})", 2000);
		}
		catch (Exception ex)
		{
			ShowMessage($"[red]Error:[/] {ex.Message}", 3000);
		}
	}

	private void PromptScanDirectory()
	{
		Console.Clear();
		AnsiConsole.MarkupLine("[bold cyan]Scan Directory for Orchestrations[/]");
		AnsiConsole.MarkupLine("[dim]Enter a directory path to scan for .json orchestration files (or press Esc to cancel)[/]\n");

		var dirPath = PromptForPath("Directory path");
		if (string.IsNullOrWhiteSpace(dirPath))
		{
			Render();
			return;
		}

		// Normalize path
		dirPath = Path.GetFullPath(dirPath.Trim('"', ' '));

		if (!Directory.Exists(dirPath))
		{
			ShowMessage($"[red]Directory not found:[/] {dirPath}", 2000);
			return;
		}

		try
		{
			// Look for mcp.json in directory
			string? mcpPath = null;
			var candidateMcp = Path.Combine(dirPath, "mcp.json");
			if (File.Exists(candidateMcp))
			{
				mcpPath = candidateMcp;
			}

			var countBefore = _registry.Count;
			_registry.ScanDirectory(dirPath, mcpPath);
			var added = _registry.Count - countBefore;

			// Register triggers for newly added orchestrations
			foreach (var entry in _registry.GetAll())
			{
				if (entry.Orchestration.Trigger is { } trigger && trigger.Enabled)
				{
					// Only register if not already registered (trigger ID matches orchestration ID)
					if (!_triggerManager.GetAllTriggers().Any(t => t.Id == entry.Id))
					{
						_triggerManager.RegisterTrigger(
							entry.Path,
							entry.McpPath,
							trigger,
							null,
							TriggerSource.Json,
							entry.Id);
					}
				}
			}

			ShowMessage($"[green]Scanned:[/] Found {added} new orchestration(s)", 2000);
		}
		catch (Exception ex)
		{
			ShowMessage($"[red]Error:[/] {ex.Message}", 3000);
		}
	}

	private void RemoveOrchestration(string orchestrationId)
	{
		var entry = _registry.Get(orchestrationId);
		if (entry == null)
			return;

		// Confirm deletion
		Console.Clear();
		AnsiConsole.MarkupLine($"[bold yellow]Remove Orchestration[/]");
		AnsiConsole.MarkupLine($"\nAre you sure you want to remove [cyan]{entry.Orchestration.Name}[/]?");
		AnsiConsole.MarkupLine("[dim]Press Y to confirm, any other key to cancel[/]");

		var key = Console.ReadKey(intercept: true);
		if (key.Key != ConsoleKey.Y)
		{
			Render();
			return;
		}

		// Unregister trigger if exists (trigger ID matches orchestration ID when registered)
		var trigger = _triggerManager.GetAllTriggers().FirstOrDefault(t => t.Id == orchestrationId);
		if (trigger != null)
		{
			_triggerManager.RemoveTrigger(trigger.Id);
		}

		// Remove from registry
		_registry.Remove(orchestrationId);
		_selectedIndex = Math.Max(0, _selectedIndex - 1);

		ShowMessage($"[green]Removed:[/] {entry.Orchestration.Name}", 1500);
	}

	private string? PromptForPath(string prompt)
	{
		AnsiConsole.Markup($"[cyan]{prompt}:[/] ");

		var input = new System.Text.StringBuilder();
		while (true)
		{
			var key = Console.ReadKey(intercept: true);

			if (key.Key == ConsoleKey.Escape)
			{
				return null;
			}
			else if (key.Key == ConsoleKey.Enter)
			{
				Console.WriteLine();
				return input.ToString();
			}
			else if (key.Key == ConsoleKey.Backspace)
			{
				if (input.Length > 0)
				{
					input.Remove(input.Length - 1, 1);
					Console.Write("\b \b");
				}
			}
			else if (key.Key == ConsoleKey.Tab)
			{
				// Tab completion for paths
				var currentPath = input.ToString();
				var completed = TryTabComplete(currentPath);
				if (completed != null && completed != currentPath)
				{
					// Clear current input display
					for (int i = 0; i < input.Length; i++)
						Console.Write("\b \b");
					input.Clear();
					input.Append(completed);
					Console.Write(completed);
				}
			}
			else if (!char.IsControl(key.KeyChar))
			{
				input.Append(key.KeyChar);
				Console.Write(key.KeyChar);
			}
		}
	}

	/// <summary>
	/// Prompts the user to enter a value for a single parameter.
	/// Returns null if the user cancels (Escape), otherwise returns the entered value.
	/// </summary>
	private string? PromptForParameter(string parameterName)
	{
		AnsiConsole.Markup($"  [cyan]{parameterName}:[/] ");

		var input = new System.Text.StringBuilder();
		while (true)
		{
			var key = Console.ReadKey(intercept: true);

			if (key.Key == ConsoleKey.Escape)
			{
				return null;
			}
			else if (key.Key == ConsoleKey.Enter)
			{
				Console.WriteLine();
				return input.ToString();
			}
			else if (key.Key == ConsoleKey.Backspace)
			{
				if (input.Length > 0)
				{
					input.Remove(input.Length - 1, 1);
					Console.Write("\b \b");
				}
			}
			else if (!char.IsControl(key.KeyChar))
			{
				input.Append(key.KeyChar);
				Console.Write(key.KeyChar);
			}
		}
	}

	/// <summary>
	/// Gets all unique parameter names required by an orchestration's steps.
	/// </summary>
	private static string[] GetOrchestrationParameters(OrchestrationEntry entry)
	{
		return entry.Orchestration.Steps
			.SelectMany(s => s.Parameters)
			.Distinct()
			.ToArray();
	}

	/// <summary>
	/// Prompts the user to enter values for all required parameters.
	/// Returns null if the user cancels, otherwise returns a dictionary of parameter values.
	/// </summary>
	private Dictionary<string, string>? PromptForParameters(OrchestrationEntry entry)
	{
		var parameterNames = GetOrchestrationParameters(entry);

		if (parameterNames.Length == 0)
		{
			return null; // No parameters needed
		}

		Console.Clear();
		AnsiConsole.MarkupLine($"[bold cyan]Run Orchestration: {entry.Orchestration.Name}[/]");
		AnsiConsole.MarkupLine($"[dim]This orchestration requires {parameterNames.Length} parameter(s). Press Esc to cancel.[/]\n");

		var parameters = new Dictionary<string, string>();

		foreach (var paramName in parameterNames)
		{
			var value = PromptForParameter(paramName);
			if (value == null)
			{
				// User cancelled
				return null;
			}
			parameters[paramName] = value;
		}

		return parameters;
	}

	private string? TryTabComplete(string partial)
	{
		if (string.IsNullOrEmpty(partial))
			return null;

		try
		{
			var dir = Path.GetDirectoryName(partial);
			var prefix = Path.GetFileName(partial);

			if (string.IsNullOrEmpty(dir))
				dir = ".";

			if (!Directory.Exists(dir))
				return partial;

			// Find matching files and directories
			var matches = Directory.GetFileSystemEntries(dir, prefix + "*")
				.OrderBy(p => p)
				.ToArray();

			if (matches.Length == 1)
			{
				var match = matches[0];
				if (Directory.Exists(match))
					return match + Path.DirectorySeparatorChar;
				return match;
			}
			else if (matches.Length > 1)
			{
				// Find common prefix among matches
				var common = matches[0];
				foreach (var m in matches.Skip(1))
				{
					var len = 0;
					while (len < common.Length && len < m.Length && common[len] == m[len])
						len++;
					common = common[..len];
				}
				return common.Length > partial.Length ? common : partial;
			}
		}
		catch
		{
			// Ignore errors during tab completion
		}

		return partial;
	}

	private void ShowMessage(string markup, int delayMs)
	{
		Console.Clear();
		AnsiConsole.MarkupLine($"\n{markup}");
		Thread.Sleep(delayMs);
		Render();
	}

	#endregion

	private void Render()
	{
		lock (_renderLock)
		{
			_lastRender = DateTime.Now;
			Console.SetCursorPosition(0, 0);

			var layout = new Layout("Root")
				.SplitRows(
					new Layout("Header").Size(3),
					new Layout("Main"),
					new Layout("Footer").Size(3)
				);

			layout["Header"].Update(RenderHeader());
			layout["Main"].Update(RenderMainContent());
			layout["Footer"].Update(RenderFooter());

			AnsiConsole.Write(layout);
		}
	}

	private Panel RenderHeader()
	{
		var activeCount = _activeExecutionInfos.Count;
		var orchestrationCount = _registry.Count;
		var triggerCount = _triggerManager.GetAllTriggers().Count;

		var headerText = new Markup(
			$"[bold blue]Orchestra Terminal[/] | " +
			$"Orchestrations: [green]{orchestrationCount}[/] | " +
			$"Triggers: [yellow]{triggerCount}[/] | " +
			$"Active: [cyan]{activeCount}[/]");

		return new Panel(headerText)
			.Border(BoxBorder.Rounded)
			.BorderColor(Color.Blue);
	}

	private IRenderable RenderMainContent()
	{
		return _currentView switch
		{
			TuiView.Dashboard => RenderDashboard(),
			TuiView.Orchestrations => RenderOrchestrations(),
			TuiView.Triggers => RenderTriggers(),
			TuiView.History => RenderHistory(),
			TuiView.Active => RenderActive(),
			TuiView.OrchestrationDetail => RenderOrchestrationDetail(),
			TuiView.ExecutionDetail => RenderExecutionDetail(),
			_ => new Panel("Unknown view")
		};
	}

	private Panel RenderDashboard()
	{
		var options = new[] { "Orchestrations", "Triggers", "History", "Active Executions", "Quit" };
		var table = new Table().HideHeaders().NoBorder().AddColumn("");

		for (int i = 0; i < options.Length; i++)
		{
			var prefix = i == _selectedIndex ? "[bold cyan]> [/]" : "  ";
			var style = i == _selectedIndex ? "[bold]" : "[dim]";
			table.AddRow(new Markup($"{prefix}{style}{options[i]}[/]"));
		}

		return new Panel(table)
			.Header("[bold]Dashboard[/]")
			.Border(BoxBorder.Rounded);
	}

	private Panel RenderOrchestrations()
	{
		var items = _registry.GetAll().ToArray();
		var table = new Table()
			.AddColumn("Name")
			.AddColumn("Version")
			.AddColumn("Steps")
			.AddColumn("Trigger")
			.Border(TableBorder.Rounded);

		for (int i = 0; i < items.Length; i++)
		{
			var entry = items[i];
			var o = entry.Orchestration;
			var triggerType = o.Trigger?.Type.ToString() ?? "Manual";
			var selected = i == _selectedIndex;
			var style = selected ? "bold cyan" : "";

			table.AddRow(
				new Markup($"[{style}]{o.Name}[/]"),
				new Markup($"[{style}]{o.Version}[/]"),
				new Markup($"[{style}]{o.Steps.Length}[/]"),
				new Markup($"[{style}]{triggerType}[/]")
			);
		}

		if (items.Length == 0)
		{
			table.AddRow(new Markup("[dim]No orchestrations loaded[/]"));
		}

		return new Panel(table)
			.Header("[bold]Orchestrations[/] - [dim]R[/]=Run [dim]A[/]=Add [dim]S[/]=Scan [dim]D[/]=Delete [dim]Enter[/]=Details")
			.Border(BoxBorder.Rounded);
	}

	private Panel RenderTriggers()
	{
		var triggers = _triggerManager.GetAllTriggers().ToArray();
		var table = new Table()
			.AddColumn("Orchestration")
			.AddColumn("Type")
			.AddColumn("Status")
			.AddColumn("Runs")
			.AddColumn("Next Fire")
			.Border(TableBorder.Rounded);

		for (int i = 0; i < triggers.Length; i++)
		{
			var trigger = triggers[i];
			var selected = i == _selectedIndex;
			var style = selected ? "bold cyan" : "";
			var statusColor = trigger.Status switch
			{
				TriggerStatus.Running => "green",
				TriggerStatus.Waiting => "yellow",
				TriggerStatus.Paused => "dim",
				TriggerStatus.Error => "red",
				_ => ""
			};

			var nextFire = trigger.NextFireTime?.ToString("HH:mm:ss") ?? "-";

			table.AddRow(
				new Markup($"[{style}]{trigger.OrchestrationName ?? trigger.Id}[/]"),
				new Markup($"[{style}]{trigger.Config.Type}[/]"),
				new Markup($"[{statusColor}]{trigger.Status}[/]"),
				new Markup($"[{style}]{trigger.RunCount}[/]"),
				new Markup($"[{style}]{nextFire}[/]")
			);
		}

		if (triggers.Length == 0)
		{
			table.AddRow(new Markup("[dim]No triggers registered[/]"));
		}

		return new Panel(table)
			.Header("[bold]Triggers[/] - [dim]E[/]=Enable/Disable [dim]R[/]=Run")
			.Border(BoxBorder.Rounded);
	}

	private Panel RenderHistory()
	{
		var runs = _runStore.GetRunSummariesAsync(20).GetAwaiter().GetResult();
		var table = new Table()
			.AddColumn("Orchestration")
			.AddColumn("Ver")
			.AddColumn("Status")
			.AddColumn("Started")
			.AddColumn("Duration")
			.AddColumn("Trigger")
			.Border(TableBorder.Rounded)
			.Expand();

		for (int i = 0; i < runs.Count; i++)
		{
			var run = runs[i];
			var selected = i == _selectedIndex;
			var style = selected ? "bold cyan" : "";
			var statusColor = run.Status switch
			{
				ExecutionStatus.Succeeded => "green",
				ExecutionStatus.Failed => "red",
				ExecutionStatus.Cancelled => "yellow",
				_ => ""
			};

			// Format trigger source
			var triggerText = run.TriggeredBy switch
			{
				"manual" => "[dim]manual[/]",
				"scheduler" => "[yellow]sched[/]",
				"webhook" => "[cyan]webhook[/]",
				_ => $"[dim]{run.TriggeredBy}[/]"
			};

			table.AddRow(
				new Markup($"[{style}]{run.OrchestrationName}[/]"),
				new Markup($"[dim]{run.OrchestrationVersion}[/]"),
				new Markup($"[{statusColor}]{run.Status}[/]"),
				new Markup($"[{style}]{run.StartedAt:HH:mm:ss}[/]"),
				new Markup($"[{style}]{run.Duration.TotalSeconds:F1}s[/]"),
				new Markup(triggerText)
			);
		}

		if (runs.Count == 0)
		{
			table.AddRow(new Markup("[dim]No execution history[/]"));
		}

		return new Panel(table)
			.Header("[bold]History[/] - [dim]Enter[/]=Details [dim]D[/]=Delete")
			.Border(BoxBorder.Rounded);
	}

	private Panel RenderActive()
	{
		var active = _activeExecutionInfos.Values.ToArray();
		var table = new Table()
			.AddColumn("Orchestration")
			.AddColumn("Step")
			.AddColumn("Progress")
			.AddColumn("Duration")
			.Border(TableBorder.Rounded);

		for (int i = 0; i < active.Length; i++)
		{
			var exec = active[i];
			var selected = i == _selectedIndex;
			var style = selected ? "bold cyan" : "";
			var duration = (DateTimeOffset.UtcNow - exec.StartedAt).TotalSeconds;
			var progress = exec.TotalSteps > 0
				? $"{exec.CompletedSteps}/{exec.TotalSteps}"
				: "-";

			table.AddRow(
				new Markup($"[{style}]{exec.OrchestrationName}[/]"),
				new Markup($"[green]{exec.CurrentStep ?? "-"}[/]"),
				new Markup($"[{style}]{progress}[/]"),
				new Markup($"[{style}]{duration:F1}s[/]")
			);
		}

		if (active.Length == 0)
		{
			table.AddRow(new Markup("[dim]No active executions[/]"));
		}

		return new Panel(table)
			.Header("[bold]Active Executions[/] - [dim]C[/]=Cancel [dim]Enter[/]=Details")
			.Border(BoxBorder.Rounded);
	}

	private Panel RenderOrchestrationDetail()
	{
		var entry = _selectedOrchestrationId != null ? _registry.Get(_selectedOrchestrationId) : null;
		if (entry == null)
		{
			return new Panel(new Markup("[red]Orchestration not found[/]"));
		}

		var o = entry.Orchestration;
		var content = new Rows(
			new Markup($"[bold]Name:[/] {o.Name}"),
			new Markup($"[bold]Description:[/] {o.Description ?? "(none)"}"),
			new Markup($"[bold]Version:[/] {o.Version}"),
			new Markup($"[bold]Path:[/] {entry.Path}"),
			new Markup(""),
			new Markup("[bold]Steps:[/]")
		);

		var stepsTable = new Table()
			.AddColumn("Name")
			.AddColumn("Type")
			.AddColumn("Model")
			.AddColumn("Depends On")
			.Border(TableBorder.Simple);

		foreach (var step in o.Steps)
		{
			var model = step is PromptOrchestrationStep ps ? ps.Model : "-";
			var deps = step.DependsOn.Length > 0 ? string.Join(", ", step.DependsOn) : "-";
			stepsTable.AddRow(step.Name, step.Type.ToString(), model, deps);
		}

		var combined = new Rows(content, stepsTable);

		return new Panel(combined)
			.Header($"[bold]{o.Name}[/] - [dim]R[/]=Run [dim]Esc[/]=Back")
			.Border(BoxBorder.Rounded);
	}

	private Panel RenderExecutionDetail()
	{
		// Try to get active execution first
		var active = _selectedExecutionId != null
			? _activeExecutionInfos.GetValueOrDefault(_selectedExecutionId)
			: null;

		if (active != null)
		{
			return RenderActiveExecutionDetail(active);
		}

		// Try to load full run record (cached for performance)
		if (_cachedRunRecord == null || _cachedRunRecord.RunId != _selectedExecutionId)
		{
			var runs = _runStore.GetRunSummariesAsync(100).GetAwaiter().GetResult();
			var runIndex = runs.FirstOrDefault(r => r.RunId == _selectedExecutionId);
			if (runIndex != null)
			{
				_cachedRunRecord = _runStore.GetRunAsync(runIndex.OrchestrationName, runIndex.RunId).GetAwaiter().GetResult();
			}
		}

		if (_cachedRunRecord != null)
		{
			return RenderCompletedExecutionDetail(_cachedRunRecord);
		}

		return new Panel(new Markup("[red]Execution not found[/]"))
			.Header("[bold]Execution Details[/]")
			.Border(BoxBorder.Rounded);
	}

	private Panel RenderActiveExecutionDetail(ActiveExecutionInfo active)
	{
		var duration = (DateTimeOffset.UtcNow - active.StartedAt).TotalSeconds;

		// Build progress bar
		var progress = active.TotalSteps > 0
			? (double)active.CompletedSteps / active.TotalSteps
			: 0;
		var progressBarText = RenderProgressBar(progress, 30);

		var rows = new List<IRenderable>
		{
			new Markup($"[bold cyan]Orchestration:[/] {active.OrchestrationName}"),
			new Markup($"[bold cyan]Status:[/] [green bold]{active.Status}[/]"),
			new Markup($"[bold cyan]Current Step:[/] [yellow]{active.CurrentStep ?? "-"}[/]"),
			new Rule("[dim]Progress[/]") { Style = Style.Parse("dim") },
			new Markup(progressBarText),
			new Markup($"[dim]{active.CompletedSteps} of {active.TotalSteps} steps completed[/]"),
			new Rule("[dim]Details[/]") { Style = Style.Parse("dim") },
			new Markup($"[bold cyan]Duration:[/] {duration:F1}s"),
			new Markup($"[bold cyan]Triggered By:[/] {active.TriggeredBy}"),
			new Markup($"[bold cyan]Execution ID:[/] [dim]{active.ExecutionId}[/]"),
		};

		return new Panel(new Rows(rows))
			.Header("[bold green]Active Execution[/] - [dim]Esc[/]=Back")
			.Border(BoxBorder.Rounded)
			.BorderColor(Color.Green);
	}

	private static string RenderProgressBar(double progress, int width)
	{
		var filledWidth = (int)(progress * width);
		var emptyWidth = width - filledWidth;
		var filled = new string('=', filledWidth);
		var empty = new string('-', emptyWidth);
		var percentage = (progress * 100).ToString("F0");
		return $"[green][[[/][green bold]{filled}[/][dim]{empty}[/][green]]][/] {percentage}%";
	}

	private Panel RenderCompletedExecutionDetail(OrchestrationRunRecord record)
	{
		var statusColor = record.Status switch
		{
			ExecutionStatus.Succeeded => "green",
			ExecutionStatus.Failed => "red",
			ExecutionStatus.Cancelled => "yellow",
			_ => "white"
		};

		// Tab headers
		var tabLine = new Markup(
			$"[{(_executionDetailTab == ExecutionDetailTab.Summary ? "bold underline cyan" : "dim")}]1:Summary[/]  " +
			$"[{(_executionDetailTab == ExecutionDetailTab.Steps ? "bold underline cyan" : "dim")}]2:Steps[/]  " +
			$"[{(_executionDetailTab == ExecutionDetailTab.Output ? "bold underline cyan" : "dim")}]3:Output[/]");

		var rows = new List<IRenderable> { tabLine, new Rule { Style = Style.Parse("dim") } };

		// Calculate total tokens
		var totalInputTokens = record.StepRecords.Values
			.Where(s => s.Usage != null)
			.Sum(s => s.Usage!.InputTokens);
		var totalOutputTokens = record.StepRecords.Values
			.Where(s => s.Usage != null)
			.Sum(s => s.Usage!.OutputTokens);
		var totalToolCalls = record.StepRecords.Values
			.Where(s => s.Trace != null)
			.Sum(s => s.Trace!.ToolCalls.Count);

		switch (_executionDetailTab)
		{
			case ExecutionDetailTab.Summary:
				rows.AddRange(RenderSummaryTab(record, statusColor, totalInputTokens, totalOutputTokens, totalToolCalls));
				break;
			case ExecutionDetailTab.Steps:
				rows.AddRange(RenderStepsTab(record));
				break;
			case ExecutionDetailTab.Output:
				rows.AddRange(RenderOutputTab(record));
				break;
		}

		// URL hint at bottom if configured
		if (!string.IsNullOrEmpty(_hostOptions.HostBaseUrl))
		{
			rows.Add(new Rule { Style = Style.Parse("dim") });
			var url = $"{_hostOptions.HostBaseUrl.TrimEnd('/')}/#/history/{Uri.EscapeDataString(record.OrchestrationName)}/{record.RunId}";
			rows.Add(new Markup($"[dim]URL:[/] [link={url}]{url}[/]"));
		}

		var headerText = $"[bold]{record.OrchestrationName}[/] [{statusColor}]{record.Status}[/]";
		return new Panel(new Rows(rows))
			.Header($"{headerText} - [dim]Tab[/]=Switch [dim]U[/]=URL [dim]Esc[/]=Back")
			.Border(BoxBorder.Rounded)
			.BorderColor(record.Status == ExecutionStatus.Failed ? Color.Red : Color.Blue);
	}

	private IEnumerable<IRenderable> RenderSummaryTab(
		OrchestrationRunRecord record,
		string statusColor,
		int totalInputTokens,
		int totalOutputTokens,
		int totalToolCalls)
	{
		yield return new Markup($"[bold]Orchestration:[/] {record.OrchestrationName}");
		yield return new Markup($"[bold]Version:[/] {record.OrchestrationVersion}");
		yield return new Markup($"[bold]Status:[/] [{statusColor}]{record.Status}[/]");
		yield return new Markup($"[bold]Started:[/] {record.StartedAt:yyyy-MM-dd HH:mm:ss}");
		yield return new Markup($"[bold]Completed:[/] {record.CompletedAt:yyyy-MM-dd HH:mm:ss}");
		yield return new Markup($"[bold]Duration:[/] {record.Duration.TotalSeconds:F2}s");
		yield return new Markup($"[bold]Triggered By:[/] {record.TriggeredBy}");
		yield return new Markup($"[bold]Run ID:[/] [dim]{record.RunId}[/]");
		yield return new Rule("[dim]Usage[/]") { Style = Style.Parse("dim") };
		yield return new Markup($"[bold]Steps:[/] {record.StepRecords.Count}");
		yield return new Markup($"[bold]Total Tokens:[/] {totalInputTokens + totalOutputTokens:N0} [dim](in: {totalInputTokens:N0}, out: {totalOutputTokens:N0})[/]");
		yield return new Markup($"[bold]Tool Calls:[/] {totalToolCalls}");

		// Show parameters if any
		if (record.Parameters.Count > 0)
		{
			yield return new Rule("[dim]Parameters[/]") { Style = Style.Parse("dim") };
			foreach (var param in record.Parameters)
			{
				var value = param.Value.Length > 50 ? param.Value[..50] + "..." : param.Value;
				yield return new Markup($"  [cyan]{param.Key}[/]: {Markup.Escape(value)}");
			}
		}
	}

	private IEnumerable<IRenderable> RenderStepsTab(OrchestrationRunRecord record)
	{
		var steps = record.StepRecords.Values.OrderBy(s => s.StartedAt).ToList();

		var table = new Table()
			.AddColumn("Step")
			.AddColumn("Status")
			.AddColumn("Duration")
			.AddColumn("Model")
			.AddColumn("Tokens")
			.AddColumn("Tools")
			.Border(TableBorder.Simple)
			.Expand();

		for (int i = 0; i < steps.Count; i++)
		{
			var step = steps[i];
			var selected = i == _selectedStepIndex;
			var style = selected ? "bold cyan" : "";
			var statusColor = step.Status switch
			{
				ExecutionStatus.Succeeded => "green",
				ExecutionStatus.Failed => "red",
				_ => "yellow"
			};

			var tokens = step.Usage != null
				? $"{step.Usage.TotalTokens:N0}"
				: "-";
			var toolCount = step.Trace?.ToolCalls.Count.ToString() ?? "0";

			table.AddRow(
				new Markup($"[{style}]{step.StepName}[/]"),
				new Markup($"[{statusColor}]{step.Status}[/]"),
				new Markup($"[{style}]{step.Duration.TotalSeconds:F2}s[/]"),
				new Markup($"[{style}]{step.ActualModel ?? "-"}[/]"),
				new Markup($"[{style}]{tokens}[/]"),
				new Markup($"[{style}]{toolCount}[/]")
			);
		}

		yield return table;

		// Show selected step details
		if (_selectedStepIndex >= 0 && _selectedStepIndex < steps.Count)
		{
			var step = steps[_selectedStepIndex];
			yield return new Rule($"[cyan]{step.StepName}[/] Details") { Style = Style.Parse("cyan") };

			if (!string.IsNullOrEmpty(step.ErrorMessage))
			{
				yield return new Markup($"[red bold]Error:[/] {Markup.Escape(step.ErrorMessage)}");
			}

			if (step.Trace?.ToolCalls.Count > 0)
			{
				yield return new Markup($"[bold]Tool Calls:[/]");
				foreach (var tc in step.Trace.ToolCalls.Take(5))
				{
					var statusIcon = tc.Success ? "[green]+[/]" : "[red]x[/]";
					var server = tc.McpServer != null ? $"[dim]{tc.McpServer}/[/]" : "";
					yield return new Markup($"  {statusIcon} {server}[yellow]{tc.ToolName}[/]");
				}
				if (step.Trace.ToolCalls.Count > 5)
				{
					yield return new Markup($"  [dim]... and {step.Trace.ToolCalls.Count - 5} more[/]");
				}
			}

			// Show truncated content preview
			if (!string.IsNullOrEmpty(step.Content))
			{
				yield return new Markup($"[bold]Output Preview:[/]");
				var preview = step.Content.Length > 200 ? step.Content[..200] + "..." : step.Content;
				preview = preview.Replace("\r\n", " ").Replace("\n", " ");
				yield return new Markup($"  [dim]{Markup.Escape(preview)}[/]");
			}
		}
	}

	private IEnumerable<IRenderable> RenderOutputTab(OrchestrationRunRecord record)
	{
		yield return new Markup("[bold]Final Output:[/]");
		yield return new Rule { Style = Style.Parse("dim") };

		if (string.IsNullOrEmpty(record.FinalContent))
		{
			yield return new Markup("[dim]No output[/]");
			yield break;
		}

		// Split content into lines for scrolling
		var lines = record.FinalContent.Split('\n');
		var visibleLines = 20; // Number of lines visible in the panel
		var maxOffset = Math.Max(0, lines.Length - visibleLines);
		_executionDetailScrollOffset = Math.Min(_executionDetailScrollOffset, maxOffset);

		var displayLines = lines
			.Skip(_executionDetailScrollOffset)
			.Take(visibleLines)
			.ToArray();

		foreach (var line in displayLines)
		{
			// Escape markup characters and truncate long lines
			var safeLine = line.Length > 100 ? line[..100] + "..." : line;
			yield return new Markup(Markup.Escape(safeLine));
		}

		if (lines.Length > visibleLines)
		{
			yield return new Rule { Style = Style.Parse("dim") };
			yield return new Markup($"[dim]Lines {_executionDetailScrollOffset + 1}-{Math.Min(_executionDetailScrollOffset + visibleLines, lines.Length)} of {lines.Length} (use j/k to scroll)[/]");
		}
	}

	private Panel RenderFooter()
	{
		var shortcuts = _currentView switch
		{
			TuiView.Dashboard => "[dim]↑↓[/] Navigate [dim]Enter[/] Select [dim]1-5[/] Views [dim]Q[/] Quit",
			TuiView.Orchestrations => "[dim]↑↓/JK[/] Navigate [dim]Enter[/] Details [dim]R[/] Run [dim]A[/] Add [dim]S[/] Scan [dim]D[/] Delete [dim]Esc[/] Back",
			TuiView.Triggers => "[dim]↑↓/JK[/] Navigate [dim]E[/] Enable/Disable [dim]R[/] Run [dim]Esc[/] Back",
			TuiView.History => "[dim]↑↓/JK[/] Navigate [dim]Enter[/] Details [dim]D[/] Delete [dim]Esc[/] Back",
			TuiView.Active => "[dim]↑↓/JK[/] Navigate [dim]Enter[/] Details [dim]C[/] Cancel [dim]Esc[/] Back",
			TuiView.OrchestrationDetail => "[dim]R[/] Run [dim]Esc[/] Back",
			TuiView.ExecutionDetail => "[dim]1-3[/] Tabs [dim]Tab[/] Switch [dim]↑↓/JK[/] Navigate [dim]U[/] URL [dim]Esc[/] Back",
			_ => ""
		};

		return new Panel(new Markup(shortcuts))
			.Border(BoxBorder.Rounded)
			.BorderColor(Color.Grey);
	}

	private static bool IsInteractiveConsole()
	{
		try
		{
			// Try to access console properties that require an interactive terminal
			_ = Console.WindowWidth;
			// CursorVisible getter is Windows-only, so we skip that check on non-Windows
			if (OperatingSystem.IsWindows())
			{
				_ = Console.CursorVisible;
			}
			return !Console.IsInputRedirected && !Console.IsOutputRedirected;
		}
		catch
		{
			return false;
		}
	}
}

public enum TuiView
{
	Dashboard,
	Orchestrations,
	Triggers,
	History,
	Active,
	OrchestrationDetail,
	ExecutionDetail
}

/// <summary>
/// Tabs within the ExecutionDetail view.
/// </summary>
public enum ExecutionDetailTab
{
	Summary,
	Steps,
	Output
}
