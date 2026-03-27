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

	// Help overlay
	private bool _showingHelp;

	// Search/filter
	private bool _isSearching;
	private string _searchQuery = "";

	// History pagination
	private int _historyPageSize = 20;
	private int _historyOffset;

	// Navigation stack for hierarchical back navigation
	private readonly Stack<(TuiView View, int SelectedIndex)> _navigationStack = new();

	// Execution detail view state
	private ExecutionDetailTab _executionDetailTab = ExecutionDetailTab.Summary;
	private int _executionDetailScrollOffset;
	private int _selectedStepIndex;
	private OrchestrationRunRecord? _cachedRunRecord;

	// Inline confirmation state
	private bool _pendingConfirmation;
	private string _confirmationMessage = "";
	private Action? _confirmationAction;

	// Transient message (replaces ShowMessage blocking)
	private string? _transientMessage;
	private DateTime _transientMessageExpiry;

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
					// Clear transient messages after expiry
					if (_transientMessage != null && DateTime.Now > _transientMessageExpiry)
					{
						_transientMessage = null;
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

	#region Navigation Helpers

	/// <summary>
	/// Push the current view onto the navigation stack and navigate to a new view.
	/// </summary>
	private void NavigateTo(TuiView view, int selectedIndex = 0)
	{
		_navigationStack.Push((_currentView, _selectedIndex));
		_currentView = view;
		_selectedIndex = selectedIndex;
	}

	/// <summary>
	/// Navigate back to the previous view using the navigation stack.
	/// Falls back to Dashboard if the stack is empty.
	/// </summary>
	private void NavigateBack()
	{
		if (_navigationStack.Count > 0)
		{
			var (view, index) = _navigationStack.Pop();
			_currentView = view;
			_selectedIndex = index;
		}
		else
		{
			_currentView = TuiView.Dashboard;
			_selectedIndex = 0;
		}
	}

	/// <summary>
	/// Navigate directly to a top-level view, clearing the navigation stack.
	/// </summary>
	private void NavigateToTopLevel(TuiView view)
	{
		_navigationStack.Clear();
		_currentView = view;
		_selectedIndex = 0;
		_searchQuery = "";
		_isSearching = false;
	}

	/// <summary>
	/// Gets the breadcrumb path for the current navigation state.
	/// </summary>
	private string GetBreadcrumb()
	{
		var parts = new List<string>();
		foreach (var (view, _) in _navigationStack.Reverse())
		{
			parts.Add(GetViewName(view));
		}
		parts.Add(GetViewName(_currentView));
		return string.Join(" > ", parts.Select((p, i) =>
			i == parts.Count - 1 ? $"[bold cyan]{p}[/]" : $"[dim]{p}[/]"));
	}

	private static string GetViewName(TuiView view) => view switch
	{
		TuiView.Dashboard => "Dashboard",
		TuiView.Orchestrations => "Orchestrations",
		TuiView.Triggers => "Triggers",
		TuiView.History => "History",
		TuiView.Active => "Active",
		TuiView.OrchestrationDetail => "Detail",
		TuiView.ExecutionDetail => "Execution",
		TuiView.EventLog => "Event Log",
		_ => view.ToString()
	};

	#endregion

	#region Inline Confirmation

	/// <summary>
	/// Shows an inline confirmation prompt. The action executes on 'y', cancelled on any other key.
	/// </summary>
	private void RequestConfirmation(string message, Action onConfirm)
	{
		_pendingConfirmation = true;
		_confirmationMessage = message;
		_confirmationAction = onConfirm;
	}

	private void HandleConfirmationInput(ConsoleKeyInfo key)
	{
		_pendingConfirmation = false;
		if (key.Key == ConsoleKey.Y)
		{
			_confirmationAction?.Invoke();
		}
		_confirmationAction = null;
		_confirmationMessage = "";
	}

	#endregion

	#region Transient Messages

	/// <summary>
	/// Shows a non-blocking message that auto-dismisses.
	/// </summary>
	private void ShowTransientMessage(string markup, int durationMs = 2000)
	{
		_transientMessage = markup;
		_transientMessageExpiry = DateTime.Now.AddMilliseconds(durationMs);
	}

	#endregion

	private void HandleKeyPress(ConsoleKeyInfo key)
	{
		// Handle confirmation prompts first
		if (_pendingConfirmation)
		{
			HandleConfirmationInput(key);
			return;
		}

		// Handle help overlay
		if (_showingHelp)
		{
			_showingHelp = false;
			return;
		}

		// Handle search mode input
		if (_isSearching)
		{
			HandleSearchInput(key);
			return;
		}

		// Global shortcuts
		switch (key.Key)
		{
			case ConsoleKey.Q:
				if (key.Modifiers == 0 && _currentView == TuiView.Dashboard)
				{
					_running = false;
					return;
				}
				else if (key.Modifiers == 0)
				{
					// Q in non-dashboard views goes back to dashboard
					NavigateToTopLevel(TuiView.Dashboard);
					return;
				}
				break;
			case ConsoleKey.D1 when !IsDetailView():
				NavigateToTopLevel(TuiView.Dashboard);
				return;
			case ConsoleKey.D2 when !IsDetailView():
				NavigateToTopLevel(TuiView.Orchestrations);
				return;
			case ConsoleKey.D3 when !IsDetailView():
				NavigateToTopLevel(TuiView.Triggers);
				return;
			case ConsoleKey.D4 when !IsDetailView():
				NavigateToTopLevel(TuiView.History);
				return;
			case ConsoleKey.D5 when !IsDetailView():
				NavigateToTopLevel(TuiView.Active);
				return;
			case ConsoleKey.D6 when !IsDetailView():
				NavigateToTopLevel(TuiView.EventLog);
				return;
			case ConsoleKey.Escape:
				NavigateBack();
				return;
			case ConsoleKey.Oem2 when key.Modifiers.HasFlag(ConsoleModifiers.Shift): // '?' key
				_showingHelp = true;
				return;
			case ConsoleKey.Divide: // '/' key for search (on numpad)
			case ConsoleKey.Oem2 when !key.Modifiers.HasFlag(ConsoleModifiers.Shift): // '/' key
				if (SupportsSearch(_currentView))
				{
					_isSearching = true;
					_searchQuery = "";
					return;
				}
				break;
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
			case TuiView.EventLog:
				HandleEventLogInput(key);
				break;
		}
	}

	/// <summary>
	/// Returns true if the current view is a detail/sub-view where number keys
	/// should NOT switch top-level views.
	/// </summary>
	private bool IsDetailView() =>
		_currentView is TuiView.OrchestrationDetail
			or TuiView.ExecutionDetail;

	private static bool SupportsSearch(TuiView view) =>
		view is TuiView.Orchestrations or TuiView.History or TuiView.Triggers;

	private void HandleSearchInput(ConsoleKeyInfo key)
	{
		switch (key.Key)
		{
			case ConsoleKey.Escape:
				_isSearching = false;
				_searchQuery = "";
				_selectedIndex = 0;
				break;
			case ConsoleKey.Enter:
				_isSearching = false;
				// Keep the filter active
				break;
			case ConsoleKey.Backspace:
				if (_searchQuery.Length > 0)
				{
					_searchQuery = _searchQuery[..^1];
					_selectedIndex = 0;
				}
				break;
			default:
				if (!char.IsControl(key.KeyChar))
				{
					_searchQuery += key.KeyChar;
					_selectedIndex = 0;
				}
				break;
		}
	}

	private void HandleDashboardInput(ConsoleKeyInfo key)
	{
		var options = new[] { "Orchestrations", "Triggers", "History", "Active Executions", "Event Log", "Quit" };
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
					case 0: NavigateTo(TuiView.Orchestrations); break;
					case 1: NavigateTo(TuiView.Triggers); break;
					case 2: NavigateTo(TuiView.History); break;
					case 3: NavigateTo(TuiView.Active); break;
					case 4: NavigateTo(TuiView.EventLog); break;
					case 5: _running = false; break;
				}
				break;
		}
	}

	private void HandleOrchestrationsInput(ConsoleKeyInfo key)
	{
		var items = GetFilteredOrchestrations();
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
					NavigateTo(TuiView.OrchestrationDetail);
				}
				break;
			case ConsoleKey.R:
				// Run the selected orchestration
				if (items.Length > 0 && _selectedIndex < items.Length)
				{
					var entry = items[_selectedIndex];
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
				// Delete/remove selected orchestration with confirmation
				if (items.Length > 0 && _selectedIndex < items.Length)
				{
					var entry = items[_selectedIndex];
					RequestConfirmation(
						$"Remove [cyan]{Markup.Escape(entry.Orchestration.Name)}[/]? [dim](y/n)[/]",
						() =>
						{
							var trigger = _triggerManager.GetAllTriggers().FirstOrDefault(t => t.Id == entry.Id);
							if (trigger != null)
								_triggerManager.RemoveTrigger(trigger.Id);
							_registry.Remove(entry.Id);
							_selectedIndex = Math.Max(0, _selectedIndex - 1);
							ShowTransientMessage($"[green]Removed:[/] {Markup.Escape(entry.Orchestration.Name)}");
						});
				}
				break;
		}
	}

	private void HandleTriggersInput(ConsoleKeyInfo key)
	{
		var triggers = GetFilteredTriggers();
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
					var newState = !trigger.Config.Enabled;
					_triggerManager.SetTriggerEnabled(trigger.Id, newState);
					ShowTransientMessage(newState
						? $"[green]Enabled[/] trigger for {Markup.Escape(trigger.OrchestrationName ?? trigger.Id)}"
						: $"[yellow]Disabled[/] trigger for {Markup.Escape(trigger.OrchestrationName ?? trigger.Id)}");
				}
				break;
			case ConsoleKey.R:
				// Run the trigger
				if (triggers.Length > 0 && _selectedIndex < triggers.Length)
				{
					var trigger = triggers[_selectedIndex];
					_ = _triggerManager.FireTriggerAsync(trigger.Id);
					ShowTransientMessage($"[green]Fired[/] trigger for {Markup.Escape(trigger.OrchestrationName ?? trigger.Id)}");
				}
				break;
			case ConsoleKey.D or ConsoleKey.Delete:
				// Delete trigger with confirmation
				if (triggers.Length > 0 && _selectedIndex < triggers.Length)
				{
					var trigger = triggers[_selectedIndex];
					RequestConfirmation(
						$"Remove trigger for [cyan]{Markup.Escape(trigger.OrchestrationName ?? trigger.Id)}[/]? [dim](y/n)[/]",
						() =>
						{
							_triggerManager.RemoveTrigger(trigger.Id);
							_selectedIndex = Math.Max(0, _selectedIndex - 1);
							ShowTransientMessage($"[green]Removed[/] trigger");
						});
				}
				break;
		}
	}

	private void HandleHistoryInput(ConsoleKeyInfo key)
	{
		var runs = GetFilteredHistory();
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
					NavigateTo(TuiView.ExecutionDetail);
				}
				break;
			case ConsoleKey.D:
				// Delete run with confirmation
				if (runs.Count > 0 && _selectedIndex < runs.Count)
				{
					var run = runs[_selectedIndex];
					RequestConfirmation(
						$"Delete run [cyan]{Markup.Escape(run.OrchestrationName)}[/] ({run.RunId[..8]}...)? [dim](y/n)[/]",
						() =>
						{
							_ = _runStore.DeleteRunAsync(run.OrchestrationName, run.RunId);
							_selectedIndex = Math.Max(0, _selectedIndex - 1);
							ShowTransientMessage("[green]Run deleted[/]");
						});
				}
				break;
			case ConsoleKey.N or ConsoleKey.PageDown:
				// Next page
				_historyOffset += _historyPageSize;
				_selectedIndex = 0;
				break;
			case ConsoleKey.P or ConsoleKey.PageUp:
				// Previous page
				_historyOffset = Math.Max(0, _historyOffset - _historyPageSize);
				_selectedIndex = 0;
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
					NavigateTo(TuiView.ExecutionDetail);
				}
				break;
			case ConsoleKey.C:
				// Cancel execution with confirmation
				if (active.Length > 0 && _selectedIndex < active.Length)
				{
					var exec = active[_selectedIndex];
					RequestConfirmation(
						$"Cancel execution [cyan]{Markup.Escape(exec.OrchestrationName)}[/]? [dim](y/n)[/]",
						() =>
						{
							_triggerManager.CancelExecution(exec.ExecutionId);
							ShowTransientMessage($"[yellow]Cancelled[/] {Markup.Escape(exec.OrchestrationName)}");
						});
				}
				break;
		}
	}

	private void HandleOrchestrationDetailInput(ConsoleKeyInfo key)
	{
		switch (key.Key)
		{
			case ConsoleKey.Backspace or ConsoleKey.Escape:
				NavigateBack();
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
				_cachedRunRecord = null;
				NavigateBack();
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
				// Show URL in a transient message
				ShowRunUrl();
				break;
		}
	}

	private void HandleEventLogInput(ConsoleKeyInfo key)
	{
		switch (key.Key)
		{
			case ConsoleKey.C:
				_reporter.Clear();
				ShowTransientMessage("[green]Event log cleared[/]");
				break;
			case ConsoleKey.Escape:
				NavigateBack();
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
			ShowTransientMessage("[yellow]No URL configured or run not loaded[/]");
			return;
		}

		var url = $"{_hostOptions.HostBaseUrl.TrimEnd('/')}/#/history/{Uri.EscapeDataString(_cachedRunRecord.OrchestrationName)}/{_cachedRunRecord.RunId}";
		ShowTransientMessage($"[cyan]URL:[/] {url}", 5000);
	}

	#region Filtered Data Helpers

	private OrchestrationEntry[] GetFilteredOrchestrations()
	{
		var all = _registry.GetAll().ToArray();
		if (string.IsNullOrEmpty(_searchQuery))
			return all;

		return all.Where(e =>
			e.Orchestration.Name.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase) ||
			(e.Orchestration.Description?.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase) ?? false)
		).ToArray();
	}

	private TriggerRegistration[] GetFilteredTriggers()
	{
		var all = _triggerManager.GetAllTriggers().ToArray();
		if (string.IsNullOrEmpty(_searchQuery))
			return all;

		return all.Where(t =>
			(t.OrchestrationName?.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase) ?? false) ||
			t.Config.Type.ToString().Contains(_searchQuery, StringComparison.OrdinalIgnoreCase)
		).ToArray();
	}

	private IReadOnlyList<RunIndex> GetFilteredHistory()
	{
		// Fetch a larger set for filtering, then apply offset/limit
		var allRuns = _runStore.GetRunSummariesAsync(200).GetAwaiter().GetResult();

		IEnumerable<RunIndex> filtered = allRuns;
		if (!string.IsNullOrEmpty(_searchQuery))
		{
			filtered = filtered.Where(r =>
				r.OrchestrationName.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase) ||
				r.Status.ToString().Contains(_searchQuery, StringComparison.OrdinalIgnoreCase) ||
				r.TriggeredBy.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase)
			);
		}

		return filtered.Skip(_historyOffset).Take(_historyPageSize).ToList();
	}

	private int GetTotalHistoryCount()
	{
		var allRuns = _runStore.GetRunSummariesAsync(1000).GetAwaiter().GetResult();
		if (!string.IsNullOrEmpty(_searchQuery))
		{
			return allRuns.Count(r =>
				r.OrchestrationName.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase) ||
				r.Status.ToString().Contains(_searchQuery, StringComparison.OrdinalIgnoreCase) ||
				r.TriggeredBy.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase));
		}
		return allRuns.Count;
	}

	#endregion

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
			NavigateTo(TuiView.Active);
			ShowTransientMessage($"[green]Started:[/] {Markup.Escape(entry.Orchestration.Name)}");
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
			ShowTransientMessage($"[red]File not found:[/] {Markup.Escape(path)}", 3000);
			Render();
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

			ShowTransientMessage($"[green]Added:[/] {Markup.Escape(entry.Orchestration.Name)} (v{entry.Orchestration.Version})");
		}
		catch (Exception ex)
		{
			ShowTransientMessage($"[red]Error:[/] {Markup.Escape(ex.Message)}", 3000);
		}

		Render();
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
			ShowTransientMessage($"[red]Directory not found:[/] {Markup.Escape(dirPath)}", 3000);
			Render();
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

			ShowTransientMessage($"[green]Scanned:[/] Found {added} new orchestration(s)");
		}
		catch (Exception ex)
		{
			ShowTransientMessage($"[red]Error:[/] {Markup.Escape(ex.Message)}", 3000);
		}

		Render();
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
		AnsiConsole.MarkupLine($"[bold cyan]Run Orchestration: {Markup.Escape(entry.Orchestration.Name)}[/]");
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
					new Layout("StatusBar").Size(3),
					new Layout("Footer").Size(3)
				);

			layout["Header"].Update(RenderHeader());

			if (_showingHelp)
			{
				layout["Main"].Update(RenderHelpOverlay());
			}
			else
			{
				layout["Main"].Update(RenderMainContent());
			}

			layout["StatusBar"].Update(RenderStatusBar());
			layout["Footer"].Update(RenderFooter());

			AnsiConsole.Write(layout);
		}
	}

	private Panel RenderHeader()
	{
		var activeCount = _activeExecutionInfos.Count;
		var orchestrationCount = _registry.Count;
		var triggerCount = _triggerManager.GetAllTriggers().Count;
		var breadcrumb = GetBreadcrumb();

		var headerText = new Markup(
			$"[bold blue]Orchestra Terminal[/] | " +
			$"Orchestrations: [green]{orchestrationCount}[/] | " +
			$"Triggers: [yellow]{triggerCount}[/] | " +
			$"Active: [cyan]{activeCount}[/]   " +
			breadcrumb);

		return new Panel(headerText)
			.Border(BoxBorder.Rounded)
			.BorderColor(Color.Blue);
	}

	private Panel RenderStatusBar()
	{
		// Show confirmation prompt if pending
		if (_pendingConfirmation)
		{
			return new Panel(new Markup(_confirmationMessage))
				.Border(BoxBorder.Rounded)
				.BorderColor(Color.Yellow);
		}

		// Show transient message if active
		if (_transientMessage != null && DateTime.Now < _transientMessageExpiry)
		{
			return new Panel(new Markup(_transientMessage))
				.Border(BoxBorder.Rounded)
				.BorderColor(Color.Green);
		}

		// Show search bar if searching
		if (_isSearching)
		{
			return new Panel(new Markup($"[yellow]Search:[/] {Markup.Escape(_searchQuery)}[blink]|[/]"))
				.Border(BoxBorder.Rounded)
				.BorderColor(Color.Yellow);
		}

		// Show active filter if set
		if (!string.IsNullOrEmpty(_searchQuery))
		{
			return new Panel(new Markup($"[dim]Filter:[/] [yellow]{Markup.Escape(_searchQuery)}[/] [dim](/ to search, Esc clears in search mode)[/]"))
				.Border(BoxBorder.Rounded)
				.BorderColor(Color.Grey);
		}

		// Show the last reporter event
		var events = _reporter.GetEvents();
		if (events.Count > 0)
		{
			var last = events[^1];
			var timeAgo = (DateTimeOffset.Now - last.Timestamp).TotalSeconds;
			var age = timeAgo < 60 ? $"{timeAgo:F0}s ago" : $"{timeAgo / 60:F0}m ago";
			var typeColor = last.Type switch
			{
				"step-started" => "green",
				"step-completed" => "cyan",
				"step-error" => "red",
				"tool-started" => "yellow",
				"tool-completed" => "blue",
				_ => "dim"
			};
			return new Panel(new Markup($"[{typeColor}]{Markup.Escape(last.Type)}[/] {Markup.Escape(last.Message)} [dim]({age})[/]"))
				.Border(BoxBorder.Rounded)
				.BorderColor(Color.Grey);
		}

		return new Panel(new Markup("[dim]Ready[/]"))
			.Border(BoxBorder.Rounded)
			.BorderColor(Color.Grey);
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
			TuiView.EventLog => RenderEventLog(),
			_ => new Panel("Unknown view")
		};
	}

	private Panel RenderDashboard()
	{
		// Richer dashboard with summary information
		var dashboardLayout = new Layout("DashboardRoot")
			.SplitColumns(
				new Layout("Menu").Size(35),
				new Layout("Summary")
			);

		// Menu
		var options = new[] { "Orchestrations", "Triggers", "History", "Active Executions", "Event Log", "Quit" };
		var icons = new[] { "[green]2[/]", "[yellow]3[/]", "[blue]4[/]", "[cyan]5[/]", "[magenta]6[/]", "[red]Q[/]" };
		var menuTable = new Table().HideHeaders().NoBorder().AddColumn("").AddColumn("");

		for (int i = 0; i < options.Length; i++)
		{
			var prefix = i == _selectedIndex ? "[bold cyan]> [/]" : "  ";
			var style = i == _selectedIndex ? "[bold]" : "[dim]";
			menuTable.AddRow(
				new Markup($"{prefix}{style}{options[i]}[/]"),
				new Markup($"  {icons[i]}")
			);
		}

		dashboardLayout["Menu"].Update(new Panel(menuTable).Header("[bold]Menu[/]").Border(BoxBorder.Rounded));

		// Summary panel with recent activity
		var summaryRows = new List<IRenderable>();

		var orchestrationCount = _registry.Count;
		var activeCount = _activeExecutionInfos.Count;
		var triggerCount = _triggerManager.GetAllTriggers().Count;

		summaryRows.Add(new Markup($"[bold]Orchestrations:[/] [green]{orchestrationCount}[/]"));
		summaryRows.Add(new Markup($"[bold]Active Triggers:[/] [yellow]{triggerCount}[/]"));
		summaryRows.Add(new Markup($"[bold]Running Now:[/] [cyan]{activeCount}[/]"));

		// Show recent runs summary
		summaryRows.Add(new Rule("[dim]Recent Activity[/]") { Style = Style.Parse("dim") });
		var recentRuns = _runStore.GetRunSummariesAsync(5).GetAwaiter().GetResult();
		if (recentRuns.Count > 0)
		{
			foreach (var run in recentRuns)
			{
				var statusColor = run.Status switch
				{
					ExecutionStatus.Succeeded => "green",
					ExecutionStatus.Failed => "red",
					ExecutionStatus.Cancelled => "yellow",
					_ => "dim"
				};
				var statusIcon = run.Status switch
				{
					ExecutionStatus.Succeeded => "+",
					ExecutionStatus.Failed => "x",
					ExecutionStatus.Cancelled => "-",
					_ => "?"
				};
				summaryRows.Add(new Markup(
					$"  [{statusColor}]{statusIcon}[/] {Markup.Escape(run.OrchestrationName)} " +
					$"[dim]{run.StartedAt:HH:mm}[/] [{statusColor}]{run.Status}[/] [dim]{run.Duration.TotalSeconds:F1}s[/]"));
			}
		}
		else
		{
			summaryRows.Add(new Markup("  [dim]No recent runs[/]"));
		}

		// Show active executions if any
		if (activeCount > 0)
		{
			summaryRows.Add(new Rule("[dim]Running[/]") { Style = Style.Parse("dim") });
			foreach (var exec in _activeExecutionInfos.Values.Take(3))
			{
				var progress = exec.TotalSteps > 0
					? $"{exec.CompletedSteps}/{exec.TotalSteps}"
					: "...";
				summaryRows.Add(new Markup(
					$"  [green bold]>[/] {Markup.Escape(exec.OrchestrationName)} " +
					$"[cyan]{exec.CurrentStep ?? "starting"}[/] [{(exec.TotalSteps > 0 ? "green" : "dim")}]{progress}[/]"));
			}
		}

		dashboardLayout["Summary"].Update(new Panel(new Rows(summaryRows)).Header("[bold]Overview[/]").Border(BoxBorder.Rounded));

		return new Panel(dashboardLayout)
			.Header("[bold]Dashboard[/]")
			.Border(BoxBorder.None);
	}

	private Panel RenderOrchestrations()
	{
		var items = GetFilteredOrchestrations();
		var table = new Table()
			.AddColumn("Name")
			.AddColumn("Version")
			.AddColumn("Steps")
			.AddColumn("Trigger")
			.AddColumn("Description")
			.Border(TableBorder.Rounded)
			.Expand();

		for (int i = 0; i < items.Length; i++)
		{
			var entry = items[i];
			var o = entry.Orchestration;
			var triggerType = o.Trigger?.Type.ToString() ?? "Manual";
			var selected = i == _selectedIndex;
			var style = selected ? "bold cyan" : "";
			var desc = o.Description ?? "";
			if (desc.Length > 40) desc = desc[..40] + "...";

			table.AddRow(
				new Markup($"[{style}]{Markup.Escape(o.Name)}[/]"),
				new Markup($"[{style}]{Markup.Escape(o.Version)}[/]"),
				new Markup($"[{style}]{o.Steps.Length}[/]"),
				new Markup($"[{style}]{triggerType}[/]"),
				new Markup($"[dim]{Markup.Escape(desc)}[/]")
			);
		}

		if (items.Length == 0)
		{
			var msg = string.IsNullOrEmpty(_searchQuery)
				? "[dim]No orchestrations loaded. Press [bold]a[/] to add or [bold]s[/] to scan.[/]"
				: $"[dim]No orchestrations matching [yellow]{Markup.Escape(_searchQuery)}[/][/]";
			table.AddRow(new Markup(msg));
		}

		return new Panel(table)
			.Header("[bold]Orchestrations[/] [dim]|[/] [dim]r[/]=Run [dim]a[/]=Add [dim]s[/]=Scan [dim]d[/]=Delete [dim]Enter[/]=Details [dim]/[/]=Search")
			.Border(BoxBorder.Rounded);
	}

	private Panel RenderTriggers()
	{
		var triggers = GetFilteredTriggers();
		var table = new Table()
			.AddColumn("Orchestration")
			.AddColumn("Type")
			.AddColumn("Status")
			.AddColumn("Enabled")
			.AddColumn("Runs")
			.AddColumn("Next Fire")
			.Border(TableBorder.Rounded)
			.Expand();

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

			var enabledText = trigger.Config.Enabled
				? "[green]Yes[/]"
				: "[red]No[/]";
			var nextFire = trigger.NextFireTime?.ToString("HH:mm:ss") ?? "-";

			table.AddRow(
				new Markup($"[{style}]{Markup.Escape(trigger.OrchestrationName ?? trigger.Id)}[/]"),
				new Markup($"[{style}]{trigger.Config.Type}[/]"),
				new Markup($"[{statusColor}]{trigger.Status}[/]"),
				new Markup(enabledText),
				new Markup($"[{style}]{trigger.RunCount}[/]"),
				new Markup($"[{style}]{nextFire}[/]")
			);
		}

		if (triggers.Length == 0)
		{
			var msg = string.IsNullOrEmpty(_searchQuery)
				? "[dim]No triggers registered[/]"
				: $"[dim]No triggers matching [yellow]{Markup.Escape(_searchQuery)}[/][/]";
			table.AddRow(new Markup(msg));
		}

		return new Panel(table)
			.Header("[bold]Triggers[/] [dim]|[/] [dim]e[/]=Enable/Disable [dim]r[/]=Run [dim]d[/]=Delete [dim]/[/]=Search")
			.Border(BoxBorder.Rounded);
	}

	private Panel RenderHistory()
	{
		var runs = GetFilteredHistory();
		var totalCount = GetTotalHistoryCount();
		var currentPage = (_historyOffset / _historyPageSize) + 1;
		var totalPages = Math.Max(1, (int)Math.Ceiling((double)totalCount / _historyPageSize));

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
				"loop" => "[magenta]loop[/]",
				_ => $"[dim]{Markup.Escape(run.TriggeredBy)}[/]"
			};

			table.AddRow(
				new Markup($"[{style}]{Markup.Escape(run.OrchestrationName)}[/]"),
				new Markup($"[dim]{Markup.Escape(run.OrchestrationVersion)}[/]"),
				new Markup($"[{statusColor}]{run.Status}[/]"),
				new Markup($"[{style}]{run.StartedAt:HH:mm:ss}[/]"),
				new Markup($"[{style}]{run.Duration.TotalSeconds:F1}s[/]"),
				new Markup(triggerText)
			);
		}

		if (runs.Count == 0)
		{
			var msg = string.IsNullOrEmpty(_searchQuery)
				? "[dim]No execution history[/]"
				: $"[dim]No runs matching [yellow]{Markup.Escape(_searchQuery)}[/][/]";
			table.AddRow(new Markup(msg));
		}

		var pageInfo = totalCount > _historyPageSize
			? $" [dim]Page {currentPage}/{totalPages} ({totalCount} total)[/]"
			: "";

		return new Panel(table)
			.Header($"[bold]History[/]{pageInfo} [dim]|[/] [dim]Enter[/]=Details [dim]d[/]=Delete [dim]n/p[/]=Page [dim]/[/]=Search")
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
			.AddColumn("Triggered By")
			.Border(TableBorder.Rounded)
			.Expand();

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
				new Markup($"[{style}]{Markup.Escape(exec.OrchestrationName)}[/]"),
				new Markup($"[green]{Markup.Escape(exec.CurrentStep ?? "-")}[/]"),
				new Markup($"[{style}]{progress}[/]"),
				new Markup($"[{style}]{duration:F1}s[/]"),
				new Markup($"[dim]{Markup.Escape(exec.TriggeredBy)}[/]")
			);
		}

		if (active.Length == 0)
		{
			table.AddRow(new Markup("[dim]No active executions[/]"));
		}

		return new Panel(table)
			.Header("[bold]Active Executions[/] [dim]|[/] [dim]c[/]=Cancel [dim]Enter[/]=Details")
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

		// Info section
		var infoRows = new List<IRenderable>
		{
			new Markup($"[bold]Name:[/]        {Markup.Escape(o.Name)}"),
			new Markup($"[bold]Description:[/] {Markup.Escape(o.Description ?? "(none)")}"),
			new Markup($"[bold]Version:[/]     {Markup.Escape(o.Version)}"),
			new Markup($"[bold]Path:[/]        [dim]{Markup.Escape(entry.Path)}[/]"),
		};

		if (entry.McpPath != null)
		{
			infoRows.Add(new Markup($"[bold]MCP Config:[/]  [dim]{Markup.Escape(entry.McpPath)}[/]"));
		}

		if (o.Trigger != null)
		{
			infoRows.Add(new Markup($"[bold]Trigger:[/]     {o.Trigger.Type} [dim](enabled: {o.Trigger.Enabled})[/]"));
		}

		var parameters = GetOrchestrationParameters(entry);
		if (parameters.Length > 0)
		{
			infoRows.Add(new Markup($"[bold]Parameters:[/]  {string.Join(", ", parameters.Select(p => $"[cyan]{Markup.Escape(p)}[/]"))}"));
		}

		infoRows.Add(new Rule("[dim]Steps[/]") { Style = Style.Parse("dim") });

		// Steps table
		var stepsTable = new Table()
			.AddColumn("Name")
			.AddColumn("Type")
			.AddColumn("Model")
			.AddColumn("Depends On")
			.Border(TableBorder.Simple)
			.Expand();

		foreach (var step in o.Steps)
		{
			var model = step is PromptOrchestrationStep ps ? ps.Model : "-";
			var deps = step.DependsOn.Length > 0 ? string.Join(", ", step.DependsOn) : "-";
			stepsTable.AddRow(
				Markup.Escape(step.Name),
				step.Type.ToString(),
				Markup.Escape(model),
				Markup.Escape(deps));
		}

		infoRows.Add(stepsTable);

		return new Panel(new Rows(infoRows))
			.Header($"[bold]{Markup.Escape(o.Name)}[/] [dim]|[/] [dim]r[/]=Run [dim]Esc[/]=Back")
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
			new Markup($"[bold cyan]Orchestration:[/] {Markup.Escape(active.OrchestrationName)}"),
			new Markup($"[bold cyan]Status:[/] [green bold]{Markup.Escape(active.Status)}[/]"),
			new Markup($"[bold cyan]Current Step:[/] [yellow]{Markup.Escape(active.CurrentStep ?? "-")}[/]"),
			new Rule("[dim]Progress[/]") { Style = Style.Parse("dim") },
			new Markup(progressBarText),
			new Markup($"[dim]{active.CompletedSteps} of {active.TotalSteps} steps completed[/]"),
			new Rule("[dim]Details[/]") { Style = Style.Parse("dim") },
			new Markup($"[bold cyan]Duration:[/] {duration:F1}s"),
			new Markup($"[bold cyan]Triggered By:[/] {Markup.Escape(active.TriggeredBy)}"),
			new Markup($"[bold cyan]Execution ID:[/] [dim]{active.ExecutionId}[/]"),
		};

		// Show recent events for this execution
		var recentEvents = _reporter.GetEvents().TakeLast(5).ToList();
		if (recentEvents.Count > 0)
		{
			rows.Add(new Rule("[dim]Recent Events[/]") { Style = Style.Parse("dim") });
			foreach (var evt in recentEvents)
			{
				var typeColor = evt.Type switch
				{
					"step-started" => "green",
					"step-completed" => "cyan",
					"step-error" => "red",
					"tool-started" => "yellow",
					"tool-completed" => "blue",
					_ => "dim"
				};
				rows.Add(new Markup($"  [{typeColor}]{Markup.Escape(evt.Type)}[/] {Markup.Escape(evt.Message)}"));
			}
		}

		return new Panel(new Rows(rows))
			.Header("[bold green]Active Execution[/] [dim]|[/] [dim]Esc[/]=Back")
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

		var headerText = $"[bold]{Markup.Escape(record.OrchestrationName)}[/] [{statusColor}]{record.Status}[/]";
		return new Panel(new Rows(rows))
			.Header($"{headerText} [dim]|[/] [dim]Tab[/]=Switch [dim]u[/]=URL [dim]Esc[/]=Back")
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
		yield return new Markup($"[bold]Orchestration:[/] {Markup.Escape(record.OrchestrationName)}");
		yield return new Markup($"[bold]Version:[/] {Markup.Escape(record.OrchestrationVersion)}");
		yield return new Markup($"[bold]Status:[/] [{statusColor}]{record.Status}[/]");
		yield return new Markup($"[bold]Started:[/] {record.StartedAt:yyyy-MM-dd HH:mm:ss}");
		yield return new Markup($"[bold]Completed:[/] {record.CompletedAt:yyyy-MM-dd HH:mm:ss}");
		yield return new Markup($"[bold]Duration:[/] {record.Duration.TotalSeconds:F2}s");
		yield return new Markup($"[bold]Triggered By:[/] {Markup.Escape(record.TriggeredBy)}");
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
				yield return new Markup($"  [cyan]{Markup.Escape(param.Key)}[/]: {Markup.Escape(value)}");
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
				new Markup($"[{style}]{Markup.Escape(step.StepName)}[/]"),
				new Markup($"[{statusColor}]{step.Status}[/]"),
				new Markup($"[{style}]{step.Duration.TotalSeconds:F2}s[/]"),
				new Markup($"[{style}]{Markup.Escape(step.ActualModel ?? "-")}[/]"),
				new Markup($"[{style}]{tokens}[/]"),
				new Markup($"[{style}]{toolCount}[/]")
			);
		}

		yield return table;

		// Show selected step details
		if (_selectedStepIndex >= 0 && _selectedStepIndex < steps.Count)
		{
			var step = steps[_selectedStepIndex];
			yield return new Rule($"[cyan]{Markup.Escape(step.StepName)}[/] Details") { Style = Style.Parse("cyan") };

			if (!string.IsNullOrEmpty(step.ErrorMessage))
			{
				yield return new Markup($"[red bold]Error:[/] {Markup.Escape(step.ErrorMessage)}");
			}

			if (step.Trace?.ToolCalls.Count > 0)
			{
				yield return new Markup($"[bold]Tool Calls ({step.Trace.ToolCalls.Count}):[/]");
				foreach (var tc in step.Trace.ToolCalls.Take(10))
				{
					var statusIcon = tc.Success ? "[green]+[/]" : "[red]x[/]";
					var server = tc.McpServer != null ? $"[dim]{Markup.Escape(tc.McpServer)}/[/]" : "";
					yield return new Markup($"  {statusIcon} {server}[yellow]{Markup.Escape(tc.ToolName)}[/]");
				}
				if (step.Trace.ToolCalls.Count > 10)
				{
					yield return new Markup($"  [dim]... and {step.Trace.ToolCalls.Count - 10} more[/]");
				}
			}

			// Show content preview with word wrapping
			if (!string.IsNullOrEmpty(step.Content))
			{
				yield return new Markup($"[bold]Output Preview:[/]");
				var preview = step.Content.Length > 500 ? step.Content[..500] + "..." : step.Content;
				// Word-wrap at terminal width boundaries rather than truncating
				var wrappedLines = WordWrap(preview, Math.Max(40, GetSafeConsoleWidth() - 10));
				foreach (var line in wrappedLines.Take(8))
				{
					yield return new Markup($"  [dim]{Markup.Escape(line)}[/]");
				}
				if (wrappedLines.Count > 8)
				{
					yield return new Markup($"  [dim]... ({wrappedLines.Count - 8} more lines)[/]");
				}
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

		// Word-wrap content for better readability
		var contentWidth = Math.Max(40, GetSafeConsoleWidth() - 8);
		var allLines = new List<string>();
		foreach (var rawLine in record.FinalContent.Split('\n'))
		{
			var cleanLine = rawLine.TrimEnd('\r');
			if (cleanLine.Length <= contentWidth)
			{
				allLines.Add(cleanLine);
			}
			else
			{
				allLines.AddRange(WordWrap(cleanLine, contentWidth));
			}
		}

		var visibleLines = 20;
		var maxOffset = Math.Max(0, allLines.Count - visibleLines);
		_executionDetailScrollOffset = Math.Min(_executionDetailScrollOffset, maxOffset);

		var displayLines = allLines
			.Skip(_executionDetailScrollOffset)
			.Take(visibleLines)
			.ToArray();

		foreach (var line in displayLines)
		{
			yield return new Markup(Markup.Escape(line));
		}

		if (allLines.Count > visibleLines)
		{
			yield return new Rule { Style = Style.Parse("dim") };
			yield return new Markup($"[dim]Lines {_executionDetailScrollOffset + 1}-{Math.Min(_executionDetailScrollOffset + visibleLines, allLines.Count)} of {allLines.Count} (use j/k to scroll)[/]");
		}
	}

	private Panel RenderEventLog()
	{
		var events = _reporter.GetEvents();
		var rows = new List<IRenderable>();

		if (events.Count == 0)
		{
			rows.Add(new Markup("[dim]No events recorded. Events will appear here as orchestrations execute.[/]"));
		}
		else
		{
			// Show events in reverse chronological order (newest first)
			var displayEvents = events.Reverse().Take(30).ToList();
			foreach (var evt in displayEvents)
			{
				var typeColor = evt.Type switch
				{
					"step-started" => "green",
					"step-completed" => "cyan",
					"step-error" or "step-cancelled" or "subagent-failed" => "red",
					"tool-started" => "yellow",
					"tool-completed" => "blue",
					"loop-iteration" or "step-retry" => "magenta",
					"checkpoint-saved" => "green",
					"subagent-started" or "subagent-completed" or "subagent-selected" => "cyan",
					_ => "dim"
				};
				rows.Add(new Markup(
					$"[dim]{evt.Timestamp:HH:mm:ss.fff}[/] [{typeColor}]{Markup.Escape(evt.Type),-20}[/] {Markup.Escape(evt.Message)}"));
			}
		}

		return new Panel(new Rows(rows))
			.Header($"[bold]Event Log[/] ({events.Count} events) [dim]|[/] [dim]c[/]=Clear [dim]Esc[/]=Back")
			.Border(BoxBorder.Rounded);
	}

	private Panel RenderHelpOverlay()
	{
		var rows = new List<IRenderable>
		{
			new Markup("[bold cyan]Keyboard Shortcuts[/]"),
			new Rule { Style = Style.Parse("dim") },
			new Markup(""),
			new Markup("[bold]Global:[/]"),
			new Markup("  [cyan]1-6[/]       Switch views (Dashboard, Orchestrations, Triggers, History, Active, Event Log)"),
			new Markup("  [cyan]?[/]         Show this help"),
			new Markup("  [cyan]/[/]         Search / filter (in list views)"),
			new Markup("  [cyan]Esc[/]       Go back (hierarchical navigation)"),
			new Markup("  [cyan]q[/]         Quit (from Dashboard) or go to Dashboard"),
			new Markup(""),
		};

		// Context-sensitive help based on the view we were on before help was shown
		var contextView = _currentView;
		if (_navigationStack.Count > 0)
		{
			// We just pushed help, so the top of stack is where we came from
		}

		switch (contextView)
		{
			case TuiView.Dashboard:
				rows.Add(new Markup("[bold]Dashboard:[/]"));
				rows.Add(new Markup("  [cyan]j/k[/] or [cyan]Up/Down[/]  Navigate menu"));
				rows.Add(new Markup("  [cyan]Enter[/]              Select item"));
				break;
			case TuiView.Orchestrations:
				rows.Add(new Markup("[bold]Orchestrations:[/]"));
				rows.Add(new Markup("  [cyan]j/k[/] or [cyan]Up/Down[/]  Navigate list"));
				rows.Add(new Markup("  [cyan]Enter[/]              View orchestration details"));
				rows.Add(new Markup("  [cyan]r[/]                  Run selected orchestration"));
				rows.Add(new Markup("  [cyan]a[/]                  Add orchestration from file path"));
				rows.Add(new Markup("  [cyan]s[/]                  Scan directory for orchestrations"));
				rows.Add(new Markup("  [cyan]d[/] / [cyan]Delete[/]       Remove orchestration"));
				break;
			case TuiView.Triggers:
				rows.Add(new Markup("[bold]Triggers:[/]"));
				rows.Add(new Markup("  [cyan]j/k[/] or [cyan]Up/Down[/]  Navigate list"));
				rows.Add(new Markup("  [cyan]e[/]                  Enable/disable trigger"));
				rows.Add(new Markup("  [cyan]r[/]                  Fire trigger manually"));
				rows.Add(new Markup("  [cyan]d[/] / [cyan]Delete[/]       Remove trigger"));
				break;
			case TuiView.History:
				rows.Add(new Markup("[bold]History:[/]"));
				rows.Add(new Markup("  [cyan]j/k[/] or [cyan]Up/Down[/]  Navigate list"));
				rows.Add(new Markup("  [cyan]Enter[/]              View execution details"));
				rows.Add(new Markup("  [cyan]d[/]                  Delete run"));
				rows.Add(new Markup("  [cyan]n[/] / [cyan]PageDown[/]     Next page"));
				rows.Add(new Markup("  [cyan]p[/] / [cyan]PageUp[/]       Previous page"));
				break;
			case TuiView.Active:
				rows.Add(new Markup("[bold]Active Executions:[/]"));
				rows.Add(new Markup("  [cyan]j/k[/] or [cyan]Up/Down[/]  Navigate list"));
				rows.Add(new Markup("  [cyan]Enter[/]              View execution details"));
				rows.Add(new Markup("  [cyan]c[/]                  Cancel execution"));
				break;
			case TuiView.ExecutionDetail:
				rows.Add(new Markup("[bold]Execution Detail:[/]"));
				rows.Add(new Markup("  [cyan]1/2/3[/] or [cyan]Tab[/]    Switch tabs (Summary, Steps, Output)"));
				rows.Add(new Markup("  [cyan]j/k[/] or [cyan]Up/Down[/]  Navigate steps / scroll content"));
				rows.Add(new Markup("  [cyan]u[/]                  Show run URL"));
				break;
			case TuiView.EventLog:
				rows.Add(new Markup("[bold]Event Log:[/]"));
				rows.Add(new Markup("  [cyan]c[/]                  Clear event log"));
				break;
		}

		rows.Add(new Markup(""));
		rows.Add(new Markup("[dim]Press any key to dismiss[/]"));

		return new Panel(new Rows(rows))
			.Header("[bold yellow]Help[/]")
			.Border(BoxBorder.Double)
			.BorderColor(Color.Yellow);
	}

	private Panel RenderFooter()
	{
		var shortcuts = _currentView switch
		{
			TuiView.Dashboard => "[dim]j/k[/] Navigate  [dim]Enter[/] Select  [dim]1-6[/] Views  [dim]?[/] Help  [dim]q[/] Quit",
			TuiView.Orchestrations => "[dim]j/k[/] Navigate  [dim]Enter[/] Details  [dim]r[/] Run  [dim]a[/] Add  [dim]s[/] Scan  [dim]d[/] Delete  [dim]/[/] Search  [dim]?[/] Help",
			TuiView.Triggers => "[dim]j/k[/] Navigate  [dim]e[/] Enable/Disable  [dim]r[/] Run  [dim]d[/] Delete  [dim]/[/] Search  [dim]?[/] Help",
			TuiView.History => "[dim]j/k[/] Navigate  [dim]Enter[/] Details  [dim]d[/] Delete  [dim]n/p[/] Page  [dim]/[/] Search  [dim]?[/] Help",
			TuiView.Active => "[dim]j/k[/] Navigate  [dim]Enter[/] Details  [dim]c[/] Cancel  [dim]?[/] Help",
			TuiView.OrchestrationDetail => "[dim]r[/] Run  [dim]Esc[/] Back  [dim]?[/] Help",
			TuiView.ExecutionDetail => "[dim]1-3[/] Tabs  [dim]Tab[/] Switch  [dim]j/k[/] Navigate  [dim]u[/] URL  [dim]Esc[/] Back  [dim]?[/] Help",
			TuiView.EventLog => "[dim]c[/] Clear  [dim]Esc[/] Back  [dim]?[/] Help",
			_ => ""
		};

		return new Panel(new Markup(shortcuts))
			.Border(BoxBorder.Rounded)
			.BorderColor(Color.Grey);
	}

	#region Utility

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

	private static int GetSafeConsoleWidth()
	{
		try
		{
			return Console.WindowWidth;
		}
		catch
		{
			return 120;
		}
	}

	/// <summary>
	/// Word-wraps text at the specified width, breaking at word boundaries when possible.
	/// </summary>
	internal static List<string> WordWrap(string text, int maxWidth)
	{
		if (maxWidth <= 0) maxWidth = 80;
		var lines = new List<string>();
		if (string.IsNullOrEmpty(text))
		{
			lines.Add("");
			return lines;
		}

		var remaining = text;
		while (remaining.Length > maxWidth)
		{
			// Try to break at a space near the max width
			var breakAt = remaining.LastIndexOf(' ', maxWidth);
			if (breakAt <= 0)
			{
				// No space found; hard break
				breakAt = maxWidth;
			}
			lines.Add(remaining[..breakAt]);
			remaining = remaining[breakAt..].TrimStart();
		}
		if (remaining.Length > 0)
		{
			lines.Add(remaining);
		}

		return lines;
	}

	#endregion
}

public enum TuiView
{
	Dashboard,
	Orchestrations,
	Triggers,
	History,
	Active,
	OrchestrationDetail,
	ExecutionDetail,
	EventLog
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
