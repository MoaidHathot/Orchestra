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
	private readonly ICheckpointStore _checkpointStore;
	private readonly ConcurrentDictionary<string, ActiveExecutionInfo> _activeExecutionInfos;
	private readonly TerminalOrchestrationReporter _reporter;
	private readonly TerminalExecutionCallback _executionCallback;
	private readonly OrchestrationHostOptions _hostOptions;

	private TuiView _currentView = TuiView.Dashboard;
	private int _selectedIndex;
	private string? _selectedOrchestrationId;
	private string? _selectedExecutionId;
	private string? _selectedMcpName;
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
	private bool _streamAutoScroll = true;

	// Inline confirmation state
	private bool _pendingConfirmation;
	private string _confirmationMessage = "";
	private Action? _confirmationAction;

	// Version history view state
	private IReadOnlyList<OrchestrationVersionEntry>? _cachedVersions;
	private string? _versionDiffOldHash;
	private string? _versionDiffNewHash;
	private IReadOnlyList<DiffLine>? _cachedDiffLines;
	private int _versionDiffScrollOffset;

	// Checkpoint view state
	private IReadOnlyList<CheckpointData>? _cachedCheckpoints;

	// Trigger creation wizard state
	private TriggerCreateStep _triggerCreateStep;
	private int _triggerCreateSelectedIndex;
	private TriggerType _triggerCreateType;
	private string? _triggerCreateOrchestrationId;
	private bool _triggerCreateEnabled = true;
	// Scheduler fields
	private string _triggerCreateCron = "";
	private string _triggerCreateIntervalSeconds = "";
	private string _triggerCreateMaxRuns = "";
	// Loop fields
	private string _triggerCreateDelaySeconds = "0";
	private string _triggerCreateMaxIterations = "";
	private bool _triggerCreateContinueOnFailure;
	// Webhook fields
	private string _triggerCreateSecret = "";
	private string _triggerCreateMaxConcurrent = "1";
	// Email fields
	private string _triggerCreateFolderPath = "Inbox";
	private string _triggerCreatePollInterval = "60";
	private string _triggerCreateMaxItems = "10";
	private string _triggerCreateSubjectContains = "";
	private string _triggerCreateSenderContains = "";
	// Common
	private string _triggerCreateInputHandlerPrompt = "";
	private bool _triggerCreateEditingField;
	private int _triggerCreateEditFieldIndex;
	private System.Text.StringBuilder _triggerCreateFieldBuffer = new();

	// Transient message (replaces ShowMessage blocking)
	private string? _transientMessage;
	private DateTime _transientMessageExpiry;

	public TerminalUI(
		OrchestrationRegistry registry,
		TriggerManager triggerManager,
		FileSystemRunStore runStore,
		ICheckpointStore checkpointStore,
		ConcurrentDictionary<string, ActiveExecutionInfo> activeExecutionInfos,
		TerminalOrchestrationReporter reporter,
		ITriggerExecutionCallback executionCallback,
		OrchestrationHostOptions hostOptions)
	{
		_registry = registry;
		_triggerManager = triggerManager;
		_runStore = runStore;
		_checkpointStore = checkpointStore;
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
		_reporter.OnStreamingUpdate += RequestStreamingRedraw;
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
			_reporter.OnStreamingUpdate -= RequestStreamingRedraw;
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

	/// <summary>
	/// Called when streaming deltas arrive. Only triggers a redraw if we're
	/// actually showing the stream tab or active execution detail (to avoid
	/// unnecessary full redraws). Throttled to ~20fps.
	/// </summary>
	private void RequestStreamingRedraw()
	{
		var shouldRedraw = false;

		// Redraw if viewing stream tab
		if (_currentView == TuiView.ExecutionDetail && _executionDetailTab == ExecutionDetailTab.Stream)
		{
			shouldRedraw = true;
		}

		// Redraw if viewing active execution detail (which shows streaming preview)
		if (_currentView == TuiView.ExecutionDetail && _selectedExecutionId != null
			&& _activeExecutionInfos.ContainsKey(_selectedExecutionId))
		{
			shouldRedraw = true;
		}

		if (shouldRedraw && (DateTime.Now - _lastRender).TotalMilliseconds > 50)
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
		TuiView.McpServers => "MCP Servers",
		TuiView.McpDetail => "MCP Detail",
		TuiView.VersionHistory => "Versions",
		TuiView.VersionDiff => "Diff",
		TuiView.DagView => "DAG",
		TuiView.RawJsonView => "Raw JSON",
		TuiView.Checkpoints => "Checkpoints",
		TuiView.TriggerCreate => "Create Trigger",
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
			case ConsoleKey.D7 when !IsDetailView():
				NavigateToTopLevel(TuiView.McpServers);
				return;
			case ConsoleKey.D8 when !IsDetailView():
				NavigateToTopLevel(TuiView.Checkpoints);
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
			case TuiView.McpServers:
				HandleMcpServersInput(key);
				break;
			case TuiView.McpDetail:
				HandleMcpDetailInput(key);
				break;
			case TuiView.VersionHistory:
				HandleVersionHistoryInput(key);
				break;
			case TuiView.VersionDiff:
				HandleVersionDiffInput(key);
				break;
			case TuiView.DagView:
				HandleDagViewInput(key);
				break;
		case TuiView.RawJsonView:
			HandleRawJsonViewInput(key);
			break;
		case TuiView.Checkpoints:
			HandleCheckpointsInput(key);
			break;
		case TuiView.TriggerCreate:
			HandleTriggerCreateInput(key);
			break;
		}
	}

	/// <summary>
	/// Returns true if the current view is a detail/sub-view where number keys
	/// should NOT switch top-level views.
	/// </summary>
	private bool IsDetailView() =>
		_currentView is TuiView.OrchestrationDetail
			or TuiView.ExecutionDetail
			or TuiView.McpDetail
			or TuiView.VersionHistory
			or TuiView.VersionDiff
			or TuiView.DagView
			or TuiView.RawJsonView
			or TuiView.TriggerCreate;

	private static bool SupportsSearch(TuiView view) =>
		view is TuiView.Orchestrations or TuiView.History or TuiView.Triggers or TuiView.McpServers;

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
		var options = new[] { "Orchestrations", "Triggers", "History", "Active Executions", "Event Log", "MCP Servers", "Checkpoints", "Quit" };
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
					case 5: NavigateTo(TuiView.McpServers); break;
					case 6: NavigateTo(TuiView.Checkpoints); break;
					case 7: _running = false; break;
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
			case ConsoleKey.N:
				// Create new trigger
				ResetTriggerCreateState();
				NavigateTo(TuiView.TriggerCreate);
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
			case ConsoleKey.V:
				// View version history
				if (_selectedOrchestrationId != null)
				{
					var versionStore = _registry.VersionStore;
					if (versionStore is null)
					{
						ShowTransientMessage("[yellow]Version tracking is not configured[/]");
					}
					else
					{
						_cachedVersions = null; // Force reload
						NavigateTo(TuiView.VersionHistory);
					}
				}
				break;
			case ConsoleKey.G:
				// View DAG visualization
				if (_selectedOrchestrationId != null)
				{
					NavigateTo(TuiView.DagView);
				}
				break;
			case ConsoleKey.J:
				// View raw JSON
				if (_selectedOrchestrationId != null)
				{
					_rawJsonScrollOffset = 0;
					_cachedRawJsonLines = null;
					NavigateTo(TuiView.RawJsonView);
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
			case ConsoleKey.D4:
				_executionDetailTab = ExecutionDetailTab.Stream;
				_executionDetailScrollOffset = 0;
				_streamAutoScroll = true;
				break;
			// Tab navigation with Tab key
			case ConsoleKey.Tab:
				_executionDetailTab = _executionDetailTab switch
				{
					ExecutionDetailTab.Summary => ExecutionDetailTab.Steps,
					ExecutionDetailTab.Steps => ExecutionDetailTab.Output,
					ExecutionDetailTab.Output => ExecutionDetailTab.Stream,
					ExecutionDetailTab.Stream => ExecutionDetailTab.Summary,
					_ => ExecutionDetailTab.Summary
				};
				_executionDetailScrollOffset = 0;
				if (_executionDetailTab == ExecutionDetailTab.Stream)
				{
					_streamAutoScroll = true;
				}
				break;
			// Navigation within tabs
			case ConsoleKey.UpArrow or ConsoleKey.K:
				if (_executionDetailTab == ExecutionDetailTab.Steps)
				{
					_selectedStepIndex = Math.Max(0, _selectedStepIndex - 1);
				}
				else if (_executionDetailTab == ExecutionDetailTab.Stream)
				{
					_streamAutoScroll = false; // Disable auto-scroll when user scrolls manually
					_executionDetailScrollOffset = Math.Max(0, _executionDetailScrollOffset - 1);
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
				else if (_executionDetailTab == ExecutionDetailTab.Stream)
				{
					_streamAutoScroll = false; // Disable auto-scroll when user scrolls manually
					_executionDetailScrollOffset++;
				}
				else
				{
					_executionDetailScrollOffset++;
				}
				break;
			// Auto-scroll toggle for streaming tab
			case ConsoleKey.F:
				if (_executionDetailTab == ExecutionDetailTab.Stream)
				{
					_streamAutoScroll = !_streamAutoScroll;
					ShowTransientMessage(_streamAutoScroll
						? "[green]Auto-scroll enabled[/] — following new content"
						: "[yellow]Auto-scroll paused[/] — use j/k to scroll, f to resume");
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
		_streamAutoScroll = true;
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
			TuiView.McpServers => RenderMcpServers(),
			TuiView.McpDetail => RenderMcpDetail(),
			TuiView.VersionHistory => RenderVersionHistory(),
			TuiView.VersionDiff => RenderVersionDiff(),
			TuiView.DagView => RenderDagView(),
			TuiView.RawJsonView => RenderRawJsonView(),
			TuiView.Checkpoints => RenderCheckpoints(),
			TuiView.TriggerCreate => RenderTriggerCreate(),
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
		var options = new[] { "Orchestrations", "Triggers", "History", "Active Executions", "Event Log", "MCP Servers", "Checkpoints", "Quit" };
		var icons = new[] { "[green]2[/]", "[yellow]3[/]", "[blue]4[/]", "[cyan]5[/]", "[magenta]6[/]", "[aqua]7[/]", "[olive]8[/]", "[red]Q[/]" };
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

		var mcpCount = GetAllMcps().Length;
		summaryRows.Add(new Markup($"[bold]MCP Servers:[/] [aqua]{mcpCount}[/]"));

		// Show recent runs summary
		summaryRows.Add(new Rule("[dim]Recent Activity[/]") { Style = Style.Parse("dim") });
		var recentRuns = _runStore.GetRunSummariesAsync(5).GetAwaiter().GetResult();
		if (recentRuns.Count > 0)
		{
			foreach (var run in recentRuns)
			{
			var isEarlyCompletion = run.Status == ExecutionStatus.Succeeded && run.CompletionReason is not null;
				var statusColor = isEarlyCompletion ? "cyan" : run.Status switch
				{
					ExecutionStatus.Succeeded => "green",
					ExecutionStatus.Failed => "red",
					ExecutionStatus.Cancelled => "yellow",
					ExecutionStatus.NoAction => "dim",
					_ => "dim"
				};
				var statusIcon = isEarlyCompletion ? ">" : run.Status switch
				{
					ExecutionStatus.Succeeded => "+",
					ExecutionStatus.Failed => "x",
					ExecutionStatus.Cancelled => "-",
					ExecutionStatus.NoAction => "—",
					_ => "?"
				};
				var statusLabel = isEarlyCompletion ? "Completed Early" : run.Status.ToString();
				summaryRows.Add(new Markup(
					$"  [{statusColor}]{statusIcon}[/] {Markup.Escape(run.OrchestrationName)} " +
					$"[dim]{run.StartedAt:HH:mm}[/] [{statusColor}]{statusLabel}[/] [dim]{run.Duration.TotalSeconds:F1}s[/]"));
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
			.Header("[bold]Triggers[/] [dim]|[/] [dim]n[/]=New [dim]e[/]=Enable/Disable [dim]r[/]=Run [dim]d[/]=Delete [dim]/[/]=Search")
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
			.AddColumn("Error")
			.Border(TableBorder.Rounded)
			.Expand();

		for (int i = 0; i < runs.Count; i++)
		{
			var run = runs[i];
			var selected = i == _selectedIndex;
			var style = selected ? "bold cyan" : "";
			var isRunEarlyCompletion = run.Status == ExecutionStatus.Succeeded && run.CompletionReason is not null;
			var statusColor = isRunEarlyCompletion ? "cyan" : run.Status switch
			{
				ExecutionStatus.Succeeded => "green",
				ExecutionStatus.Failed => "red",
				ExecutionStatus.Cancelled => "yellow",
				ExecutionStatus.NoAction => "dim",
				_ => ""
			};
			var statusLabel = isRunEarlyCompletion ? "Completed Early" : run.Status.ToString();

			// Format trigger source
			var triggerText = run.TriggeredBy switch
			{
				"manual" => "[dim]manual[/]",
				"scheduler" => "[yellow]sched[/]",
				"webhook" => "[cyan]webhook[/]",
				"loop" => "[magenta]loop[/]",
				_ => $"[dim]{Markup.Escape(run.TriggeredBy)}[/]"
			};

			// Format error preview for failed/cancelled runs
			var errorText = "";
			if (run.Status is ExecutionStatus.Failed or ExecutionStatus.Cancelled && !string.IsNullOrEmpty(run.ErrorMessage))
			{
				var errorPreview = run.ErrorMessage.ReplaceLineEndings(" ");
				if (errorPreview.Length > 40) errorPreview = errorPreview[..40] + "...";
				var stepPrefix = !string.IsNullOrEmpty(run.FailedStepName)
					? $"{Markup.Escape(run.FailedStepName)}: "
					: "";
				var errorColor = run.Status == ExecutionStatus.Failed ? "red" : "yellow";
				errorText = $"[{errorColor}]{stepPrefix}{Markup.Escape(errorPreview)}[/]";
			}

			table.AddRow(
				new Markup($"[{style}]{Markup.Escape(run.OrchestrationName)}[/]"),
				new Markup($"[dim]{Markup.Escape(run.OrchestrationVersion)}[/]"),
				new Markup($"[{statusColor}]{statusLabel}[/]"),
				new Markup($"[{style}]{run.StartedAt:HH:mm:ss}[/]"),
				new Markup($"[{style}]{run.Duration.TotalSeconds:F1}s[/]"),
				new Markup(triggerText),
				new Markup(errorText)
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

		// Show MCP servers used by this orchestration
		var orchMcps = new List<Mcp>();
		if (o.Mcps.Length > 0)
		{
			foreach (var m in o.Mcps)
				orchMcps.Add(m);
		}
		foreach (var step in o.Steps)
		{
			if (step is PromptOrchestrationStep ps2 && ps2.Mcps.Length > 0)
			{
				foreach (var m in ps2.Mcps)
				{
					if (!orchMcps.Any(existing => string.Equals(existing.Name, m.Name, StringComparison.OrdinalIgnoreCase)))
						orchMcps.Add(m);
				}
			}
		}
		if (entry.McpPath != null)
		{
			try
			{
				var externalMcps = OrchestrationParser.ParseMcpFile(entry.McpPath);
				foreach (var m in externalMcps)
				{
					if (!orchMcps.Any(existing => string.Equals(existing.Name, m.Name, StringComparison.OrdinalIgnoreCase)))
						orchMcps.Add(m);
				}
			}
			catch { /* ignore parse errors */ }
		}

		if (orchMcps.Count > 0)
		{
			infoRows.Add(new Rule("[dim]MCP Servers[/]") { Style = Style.Parse("dim") });
			foreach (var mcp in orchMcps.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
			{
				var typeColor = mcp.Type == McpType.Local ? "green" : "blue";
				var endpoint = mcp switch
				{
					LocalMcp local => $"[dim]{Markup.Escape(local.Command)}[/]",
					RemoteMcp remote => $"[dim]{Markup.Escape(remote.Endpoint)}[/]",
					_ => ""
				};
				infoRows.Add(new Markup($"  [{typeColor}]{Markup.Escape(mcp.Name)}[/] [dim]({mcp.Type})[/] {endpoint}"));
			}
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
			.Header($"[bold]{Markup.Escape(o.Name)}[/] [dim]|[/] [dim]r[/]=Run [dim]v[/]=Versions [dim]g[/]=DAG [dim]j[/]=JSON [dim]Esc[/]=Back")
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
		};

		// Show live streaming content if available
		var currentStreamingStep = _reporter.CurrentStreamingStep;
		if (currentStreamingStep != null)
		{
			var timeSinceLastDelta = (DateTime.Now - _reporter.LastDeltaTime).TotalSeconds;
			var indicator = timeSinceLastDelta < 1 ? "[green bold]STREAMING[/]" : "[yellow]IDLE[/]";
			rows.Add(new Rule($"[dim]Live Content[/] {indicator}") { Style = Style.Parse("green") });

			// Show reasoning snippet if present
			var reasoning = _reporter.GetStreamingReasoning(currentStreamingStep);
			if (!string.IsNullOrEmpty(reasoning))
			{
				var reasoningPreview = reasoning.Length > 100 ? "..." + reasoning[^100..] : reasoning;
				var reasoningLine = reasoningPreview.Replace('\n', ' ').Replace('\r', ' ');
				rows.Add(new Markup($"  [italic dim yellow]Reasoning: {Markup.Escape(reasoningLine)}[/]"));
			}

			// Show content snippet (last few lines)
			var content = _reporter.GetStreamingContent(currentStreamingStep);
			if (!string.IsNullOrEmpty(content))
			{
				var contentWidth = Math.Max(40, GetSafeConsoleWidth() - 12);
				var contentLines = new List<string>();
				foreach (var rawLine in content.Split('\n'))
				{
					var cleanLine = rawLine.TrimEnd('\r');
					if (cleanLine.Length <= contentWidth)
					{
						contentLines.Add(cleanLine);
					}
					else
					{
						contentLines.AddRange(WordWrap(cleanLine, contentWidth));
					}
				}

				// Show last 4 lines
				foreach (var line in contentLines.TakeLast(4))
				{
					rows.Add(new Markup($"  {Markup.Escape(line)}"));
				}
				if (contentLines.Count > 4)
				{
					rows.Add(new Markup($"  [dim]... ({contentLines.Count - 4} earlier lines, press Enter then 4 for full stream view)[/]"));
				}

				// Show cursor
				if (timeSinceLastDelta < 2)
				{
					rows.Add(new Markup("  [green bold]|[/]"));
				}
			}
			else
			{
				rows.Add(new Markup("  [dim]Waiting for content...[/]"));
			}
		}
		else
		{
			rows.Add(new Rule("[dim]Details[/]") { Style = Style.Parse("dim") });
		}

		rows.Add(new Markup($"[bold cyan]Duration:[/] {duration:F1}s"));
		rows.Add(new Markup($"[bold cyan]Triggered By:[/] {Markup.Escape(active.TriggeredBy)}"));
		rows.Add(new Markup($"[bold cyan]Execution ID:[/] [dim]{active.ExecutionId}[/]"));

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
		var isRecordEarlyCompletion = record.Status == ExecutionStatus.Succeeded && record.CompletionReason is not null;
		var statusColor = isRecordEarlyCompletion ? "cyan" : record.Status switch
		{
			ExecutionStatus.Succeeded => "green",
			ExecutionStatus.Failed => "red",
			ExecutionStatus.Cancelled => "yellow",
			ExecutionStatus.NoAction => "dim",
			_ => "white"
		};

		// Tab headers
		var tabLine = new Markup(
			$"[{(_executionDetailTab == ExecutionDetailTab.Summary ? "bold underline cyan" : "dim")}]1:Summary[/]  " +
			$"[{(_executionDetailTab == ExecutionDetailTab.Steps ? "bold underline cyan" : "dim")}]2:Steps[/]  " +
			$"[{(_executionDetailTab == ExecutionDetailTab.Output ? "bold underline cyan" : "dim")}]3:Output[/]  " +
			$"[{(_executionDetailTab == ExecutionDetailTab.Stream ? "bold underline cyan" : "dim")}]4:Stream[/]");

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
			case ExecutionDetailTab.Stream:
				rows.AddRange(RenderStreamTab());
				break;
		}

		// URL hint at bottom if configured
		if (!string.IsNullOrEmpty(_hostOptions.HostBaseUrl))
		{
			rows.Add(new Rule { Style = Style.Parse("dim") });
			var url = $"{_hostOptions.HostBaseUrl.TrimEnd('/')}/#/history/{Uri.EscapeDataString(record.OrchestrationName)}/{record.RunId}";
			rows.Add(new Markup($"[dim]URL:[/] [link={url}]{url}[/]"));
		}

		var headerStatusLabel = isRecordEarlyCompletion ? "Completed Early" : record.Status.ToString();
		var headerText = $"[bold]{Markup.Escape(record.OrchestrationName)}[/] [{statusColor}]{headerStatusLabel}[/]";
		return new Panel(new Rows(rows))
			.Header($"{headerText} [dim]|[/] [dim]Tab[/]=Switch [dim]f[/]=Follow [dim]u[/]=URL [dim]Esc[/]=Back")
			.Border(BoxBorder.Rounded)
			.BorderColor(isRecordEarlyCompletion ? Color.Cyan1 : record.Status == ExecutionStatus.Failed ? Color.Red : record.Status == ExecutionStatus.Cancelled ? Color.Yellow : Color.Blue);
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
		var summaryStatusLabel = record.Status == ExecutionStatus.Succeeded && record.CompletionReason is not null
			? "Completed Early" : record.Status.ToString();
		yield return new Markup($"[bold]Status:[/] [{statusColor}]{summaryStatusLabel}[/]");
		if (record.CompletionReason is not null)
		{
			yield return new Markup($"[bold]Reason:[/] [{statusColor}]{Markup.Escape(record.CompletionReason)}[/]");
		}

		// Show error banner for failed/cancelled runs
		if (record.Status is ExecutionStatus.Failed or ExecutionStatus.Cancelled)
		{
			var failedSteps = record.AllStepRecords.Values
				.Where(s => s.Status == ExecutionStatus.Failed && !string.IsNullOrEmpty(s.ErrorMessage))
				.OrderBy(s => s.StartedAt)
				.ToList();
			var cancelledSteps = record.AllStepRecords.Values
				.Where(s => s.Status == ExecutionStatus.Cancelled)
				.OrderBy(s => s.StartedAt)
				.ToList();

			if (failedSteps.Count > 0)
			{
				yield return new Rule("[red bold]Error[/]") { Style = Style.Parse("red") };
				foreach (var failedStep in failedSteps)
				{
					yield return new Markup($"  [red bold]{Markup.Escape(failedStep.StepName)}:[/]");
					// Word-wrap the error message for readability
					var errorLines = WordWrap(failedStep.ErrorMessage!, 80);
					foreach (var line in errorLines)
					{
						yield return new Markup($"  [red]{Markup.Escape(line)}[/]");
					}
				}
				yield return new Rule { Style = Style.Parse("red dim") };
			}
			if (cancelledSteps.Count > 0)
			{
				yield return new Rule("[yellow bold]Cancelled[/]") { Style = Style.Parse("yellow") };
				foreach (var cancelledStep in cancelledSteps)
				{
					yield return new Markup($"  [yellow]{Markup.Escape(cancelledStep.StepName)}[/]");
				}
				yield return new Rule { Style = Style.Parse("yellow dim") };
			}
		}

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
				ExecutionStatus.NoAction => "dim",
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

	private IEnumerable<IRenderable> RenderStreamTab()
	{
		var currentStep = _reporter.CurrentStreamingStep;
		var stepNames = _reporter.GetStreamingStepNames();

		// Header showing streaming state
		if (currentStep != null)
		{
			var timeSinceLastDelta = (DateTime.Now - _reporter.LastDeltaTime).TotalSeconds;
			var indicator = timeSinceLastDelta < 1 ? "[green bold]STREAMING[/]" : "[yellow]PAUSED[/]";
			yield return new Markup($"{indicator}  Step: [cyan]{Markup.Escape(currentStep)}[/]  " +
				$"[dim]({(_streamAutoScroll ? "auto-scroll ON" : "auto-scroll OFF, press f to follow")})[/]");
		}
		else if (stepNames.Count > 0)
		{
			yield return new Markup($"[dim]Stream ended.[/] {stepNames.Count} step(s) streamed content.");
		}
		else
		{
			yield return new Markup("[dim]No streaming content yet. Content will appear here as the LLM generates tokens.[/]");
			yield return new Markup("[dim]Start an execution and switch to this tab to see real-time output.[/]");
			yield break;
		}

		yield return new Rule { Style = Style.Parse("dim") };

		// Show the content from the most relevant step: current streaming step,
		// or if none is actively streaming, the last step that had content.
		var displayStep = currentStep ?? stepNames.LastOrDefault();
		if (displayStep == null)
		{
			yield return new Markup("[dim]No content available[/]");
			yield break;
		}

		// Show reasoning if present
		var reasoning = _reporter.GetStreamingReasoning(displayStep);
		if (!string.IsNullOrEmpty(reasoning))
		{
			yield return new Markup("[bold yellow]Reasoning:[/]");
			var reasoningWidth = Math.Max(40, GetSafeConsoleWidth() - 10);
			var reasoningLines = new List<string>();
			foreach (var rawLine in reasoning.Split('\n'))
			{
				var cleanLine = rawLine.TrimEnd('\r');
				if (cleanLine.Length <= reasoningWidth)
				{
					reasoningLines.Add(cleanLine);
				}
				else
				{
					reasoningLines.AddRange(WordWrap(cleanLine, reasoningWidth));
				}
			}

			// Show last few lines of reasoning
			foreach (var line in reasoningLines.TakeLast(5))
			{
				yield return new Markup($"  [italic dim]{Markup.Escape(line)}[/]");
			}
			if (reasoningLines.Count > 5)
			{
				yield return new Markup($"  [dim]... ({reasoningLines.Count - 5} earlier lines)[/]");
			}
			yield return new Rule { Style = Style.Parse("dim") };
		}

		// Show content
		var content = _reporter.GetStreamingContent(displayStep);
		if (string.IsNullOrEmpty(content))
		{
			if (currentStep != null)
			{
				yield return new Markup("[dim]Waiting for content...[/]");
			}
			else
			{
				yield return new Markup("[dim]No content from this step[/]");
			}
			yield break;
		}

		yield return new Markup("[bold]Content:[/]");

		// Word-wrap and paginate
		var contentWidth = Math.Max(40, GetSafeConsoleWidth() - 8);
		var allLines = new List<string>();
		foreach (var rawLine in content.Split('\n'))
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

		var visibleLines = 18; // Slightly less than Output tab since we have the header

		if (_streamAutoScroll)
		{
			// Auto-scroll to bottom: show the last N lines
			_executionDetailScrollOffset = Math.Max(0, allLines.Count - visibleLines);
		}
		else
		{
			var maxOffset = Math.Max(0, allLines.Count - visibleLines);
			_executionDetailScrollOffset = Math.Min(_executionDetailScrollOffset, maxOffset);
		}

		var displayLines = allLines
			.Skip(_executionDetailScrollOffset)
			.Take(visibleLines)
			.ToArray();

		foreach (var line in displayLines)
		{
			yield return new Markup(Markup.Escape(line));
		}

		// Show cursor indicator on the last line if actively streaming
		if (currentStep != null)
		{
			var timeSinceLastDelta = (DateTime.Now - _reporter.LastDeltaTime).TotalSeconds;
			if (timeSinceLastDelta < 2)
			{
				yield return new Markup("[green bold]|[/]");
			}
		}

		if (allLines.Count > visibleLines)
		{
			yield return new Rule { Style = Style.Parse("dim") };
			yield return new Markup($"[dim]Lines {_executionDetailScrollOffset + 1}-{Math.Min(_executionDetailScrollOffset + visibleLines, allLines.Count)} of {allLines.Count} (j/k to scroll, f to toggle follow)[/]");
		}

		// Show which steps have content if there are multiple
		if (stepNames.Count > 1)
		{
			yield return new Rule("[dim]Steps with streamed content[/]") { Style = Style.Parse("dim") };
			foreach (var name in stepNames)
			{
				var isCurrentDisplay = name == displayStep;
				var isStreaming = name == currentStep;
				var indicator = isStreaming ? "[green]>[/]" : (isCurrentDisplay ? "[cyan]>[/]" : " ");
				var streamContent = _reporter.GetStreamingContent(name);
				var charCount = streamContent?.Length ?? 0;
				yield return new Markup($"  {indicator} {Markup.Escape(name)} [dim]({charCount:N0} chars)[/]");
			}
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

	#region Version History

	private void HandleVersionHistoryInput(ConsoleKeyInfo key)
	{
		var versions = GetCachedVersions();
		switch (key.Key)
		{
			case ConsoleKey.Backspace or ConsoleKey.Escape:
				_cachedVersions = null;
				NavigateBack();
				break;
			case ConsoleKey.UpArrow or ConsoleKey.K:
				_selectedIndex = Math.Max(0, _selectedIndex - 1);
				break;
			case ConsoleKey.DownArrow or ConsoleKey.J:
				_selectedIndex = Math.Min(Math.Max(0, versions.Count - 1), _selectedIndex + 1);
				break;
			case ConsoleKey.D:
				// Diff: select this version to compare against the next/previous
				if (versions.Count >= 2 && _selectedIndex < versions.Count)
				{
					// Diff the selected version against the one after it (older)
					var selected = versions[_selectedIndex];
					var compareWith = _selectedIndex < versions.Count - 1
						? versions[_selectedIndex + 1] // Compare with previous (older) version
						: versions[_selectedIndex - 1]; // If at the end, compare with newer

					_versionDiffNewHash = selected.ContentHash;
					_versionDiffOldHash = compareWith.ContentHash;
					_cachedDiffLines = null; // Force reload
					_versionDiffScrollOffset = 0;
					NavigateTo(TuiView.VersionDiff);
				}
				else if (versions.Count < 2)
				{
					ShowTransientMessage("[yellow]Need at least 2 versions to diff[/]");
				}
				break;
			case ConsoleKey.X:
				// Delete all version history with confirmation
				if (versions.Count > 0 && _selectedOrchestrationId != null)
				{
					var entry = _registry.Get(_selectedOrchestrationId);
					var name = entry?.Orchestration.Name ?? _selectedOrchestrationId;
					RequestConfirmation(
						$"Delete all version history for [cyan]{Markup.Escape(name)}[/]? [dim](y/n)[/]",
						() =>
						{
							var versionStore = _registry.VersionStore;
							if (versionStore != null && _selectedOrchestrationId != null)
							{
								versionStore.DeleteAllVersionsAsync(_selectedOrchestrationId).GetAwaiter().GetResult();
								_cachedVersions = null; // Force reload
								ShowTransientMessage("[green]Version history deleted[/]");
							}
						});
				}
				break;
		}
	}

	private void HandleVersionDiffInput(ConsoleKeyInfo key)
	{
		switch (key.Key)
		{
			case ConsoleKey.Backspace or ConsoleKey.Escape:
				_cachedDiffLines = null;
				NavigateBack();
				break;
			case ConsoleKey.UpArrow or ConsoleKey.K:
				_versionDiffScrollOffset = Math.Max(0, _versionDiffScrollOffset - 1);
				break;
			case ConsoleKey.DownArrow or ConsoleKey.J:
				_versionDiffScrollOffset++;
				break;
			case ConsoleKey.PageUp:
				_versionDiffScrollOffset = Math.Max(0, _versionDiffScrollOffset - 20);
				break;
			case ConsoleKey.PageDown:
				_versionDiffScrollOffset += 20;
				break;
		}
	}

	/// <summary>
	/// Gets cached version list for the current orchestration, loading if needed.
	/// </summary>
	private IReadOnlyList<OrchestrationVersionEntry> GetCachedVersions()
	{
		if (_cachedVersions != null)
			return _cachedVersions;

		var versionStore = _registry.VersionStore;
		if (versionStore is null || _selectedOrchestrationId is null)
			return Array.Empty<OrchestrationVersionEntry>();

		_cachedVersions = versionStore.ListVersionsAsync(_selectedOrchestrationId).GetAwaiter().GetResult();
		return _cachedVersions;
	}

	private Panel RenderVersionHistory()
	{
		var entry = _selectedOrchestrationId != null ? _registry.Get(_selectedOrchestrationId) : null;
		if (entry == null)
		{
			return new Panel(new Markup("[red]Orchestration not found[/]"))
				.Header("[bold]Version History[/]")
				.Border(BoxBorder.Rounded);
		}

		var versions = GetCachedVersions();

		var rows = new List<IRenderable>
		{
			new Markup($"[bold]Orchestration:[/] {Markup.Escape(entry.Orchestration.Name)}"),
			new Markup($"[bold]Current Hash:[/] [dim]{Markup.Escape(entry.ContentHash ?? "(unknown)")}[/]"),
			new Rule { Style = Style.Parse("dim") }
		};

		if (versions.Count == 0)
		{
			rows.Add(new Markup("[dim]No version history available. Versions are recorded when orchestrations are registered or modified.[/]"));
		}
		else
		{
			var table = new Table()
				.AddColumn("#")
				.AddColumn("Version")
				.AddColumn("Hash")
				.AddColumn("Timestamp")
				.AddColumn("Steps")
				.AddColumn("Change")
				.AddColumn("Current")
				.Border(TableBorder.Rounded)
				.Expand();

			for (int i = 0; i < versions.Count; i++)
			{
				var ver = versions[i];
				var selected = i == _selectedIndex;
				var style = selected ? "bold cyan" : "";
				var isCurrent = ver.ContentHash == entry.ContentHash;
				var currentMarker = isCurrent ? "[green bold]*[/]" : "";
				var hashShort = ver.ContentHash.Length > 8 ? ver.ContentHash[..8] : ver.ContentHash;

				table.AddRow(
					new Markup($"[{style}]{i + 1}[/]"),
					new Markup($"[{style}]{Markup.Escape(ver.DeclaredVersion)}[/]"),
					new Markup($"[dim]{Markup.Escape(hashShort)}[/]"),
					new Markup($"[{style}]{ver.Timestamp:yyyy-MM-dd HH:mm}[/]"),
					new Markup($"[{style}]{ver.StepCount}[/]"),
					new Markup($"[dim]{Markup.Escape(ver.ChangeDescription ?? "")}[/]"),
					new Markup(currentMarker)
				);
			}

			rows.Add(table);
		}

		return new Panel(new Rows(rows))
			.Header($"[bold]Version History[/] ({versions.Count} versions) [dim]|[/] [dim]d[/]=Diff [dim]x[/]=Delete All [dim]Esc[/]=Back")
			.Border(BoxBorder.Rounded);
	}

	private Panel RenderVersionDiff()
	{
		var entry = _selectedOrchestrationId != null ? _registry.Get(_selectedOrchestrationId) : null;
		if (entry == null)
		{
			return new Panel(new Markup("[red]Orchestration not found[/]"))
				.Header("[bold]Version Diff[/]")
				.Border(BoxBorder.Rounded);
		}

		var versionStore = _registry.VersionStore;
		if (versionStore is null || _versionDiffOldHash is null || _versionDiffNewHash is null)
		{
			return new Panel(new Markup("[red]Cannot compute diff: version store not available[/]"))
				.Header("[bold]Version Diff[/]")
				.Border(BoxBorder.Rounded);
		}

		// Load diff lines (cached)
		if (_cachedDiffLines == null)
		{
			var oldSnapshot = versionStore.GetSnapshotAsync(_selectedOrchestrationId!, _versionDiffOldHash).GetAwaiter().GetResult();
			var newSnapshot = versionStore.GetSnapshotAsync(_selectedOrchestrationId!, _versionDiffNewHash).GetAwaiter().GetResult();

			if (oldSnapshot == null || newSnapshot == null)
			{
				return new Panel(new Markup("[red]Could not load version snapshots for diff[/]"))
					.Header("[bold]Version Diff[/]")
					.Border(BoxBorder.Rounded);
			}

			_cachedDiffLines = FileSystemOrchestrationVersionStore.ComputeDiff(oldSnapshot, newSnapshot);
		}

		// Stats
		var added = _cachedDiffLines.Count(d => d.Type == DiffLineType.Added);
		var removed = _cachedDiffLines.Count(d => d.Type == DiffLineType.Removed);
		var unchanged = _cachedDiffLines.Count(d => d.Type == DiffLineType.Unchanged);

		var rows = new List<IRenderable>
		{
			new Markup($"[bold]Comparing:[/] [dim]{_versionDiffOldHash[..Math.Min(8, _versionDiffOldHash.Length)]}[/] -> [dim]{_versionDiffNewHash[..Math.Min(8, _versionDiffNewHash.Length)]}[/]"),
			new Markup($"[green]+{added}[/] [red]-{removed}[/] [dim]{unchanged} unchanged[/]"),
			new Rule { Style = Style.Parse("dim") }
		};

		// Show diff lines with scroll
		var visibleLines = 25;
		var maxOffset = Math.Max(0, _cachedDiffLines.Count - visibleLines);
		_versionDiffScrollOffset = Math.Min(_versionDiffScrollOffset, maxOffset);

		var displayLines = _cachedDiffLines
			.Skip(_versionDiffScrollOffset)
			.Take(visibleLines)
			.ToArray();

		foreach (var line in displayLines)
		{
			var (prefix, color) = line.Type switch
			{
				DiffLineType.Added => ("+", "green"),
				DiffLineType.Removed => ("-", "red"),
				_ => (" ", "dim")
			};
			// Truncate long lines
			var content = line.Content;
			var maxWidth = Math.Max(40, GetSafeConsoleWidth() - 10);
			if (content.Length > maxWidth)
				content = content[..maxWidth] + "...";
			rows.Add(new Markup($"[{color}]{prefix} {Markup.Escape(content)}[/]"));
		}

		if (_cachedDiffLines.Count > visibleLines)
		{
			rows.Add(new Rule { Style = Style.Parse("dim") });
			rows.Add(new Markup($"[dim]Lines {_versionDiffScrollOffset + 1}-{Math.Min(_versionDiffScrollOffset + visibleLines, _cachedDiffLines.Count)} of {_cachedDiffLines.Count} (j/k to scroll, PgUp/PgDn for pages)[/]"));
		}

		return new Panel(new Rows(rows))
			.Header($"[bold]Version Diff[/] [dim]|[/] [dim]j/k[/]=Scroll [dim]Esc[/]=Back")
			.Border(BoxBorder.Rounded);
	}

	#endregion

	#region DAG Visualization

	private int _dagScrollOffset;

	private void HandleDagViewInput(ConsoleKeyInfo key)
	{
		switch (key.Key)
		{
			case ConsoleKey.Backspace or ConsoleKey.Escape:
				_dagScrollOffset = 0;
				NavigateBack();
				break;
			case ConsoleKey.UpArrow or ConsoleKey.K:
				_dagScrollOffset = Math.Max(0, _dagScrollOffset - 1);
				break;
			case ConsoleKey.DownArrow or ConsoleKey.J:
				_dagScrollOffset++;
				break;
			case ConsoleKey.PageUp:
				_dagScrollOffset = Math.Max(0, _dagScrollOffset - 20);
				break;
			case ConsoleKey.PageDown:
				_dagScrollOffset += 20;
				break;
		}
	}

	private Panel RenderDagView()
	{
		var entry = _selectedOrchestrationId != null ? _registry.Get(_selectedOrchestrationId) : null;
		if (entry == null)
		{
			return new Panel(new Markup("[red]Orchestration not found[/]"))
				.Header("[bold]DAG Visualization[/]")
				.Border(BoxBorder.Rounded);
		}

		var o = entry.Orchestration;
		var dagLines = BuildDagAscii(o.Steps);

		var rows = new List<IRenderable>
		{
			new Markup($"[bold]Orchestration:[/] {Markup.Escape(o.Name)}"),
			new Markup($"[bold]Steps:[/] {o.Steps.Length}  [bold]Version:[/] {Markup.Escape(o.Version)}"),
			new Rule { Style = Style.Parse("dim") }
		};

		if (dagLines.Count == 0)
		{
			rows.Add(new Markup("[dim]No steps defined[/]"));
		}
		else
		{
			// Scrollable DAG
			var visibleLines = 30;
			var maxOffset = Math.Max(0, dagLines.Count - visibleLines);
			_dagScrollOffset = Math.Min(_dagScrollOffset, maxOffset);

			var displayLines = dagLines
				.Skip(_dagScrollOffset)
				.Take(visibleLines)
				.ToArray();

			foreach (var line in displayLines)
			{
				rows.Add(new Markup(line));
			}

			if (dagLines.Count > visibleLines)
			{
				rows.Add(new Rule { Style = Style.Parse("dim") });
				rows.Add(new Markup($"[dim]Lines {_dagScrollOffset + 1}-{Math.Min(_dagScrollOffset + visibleLines, dagLines.Count)} of {dagLines.Count} (j/k to scroll)[/]"));
			}
		}

		return new Panel(new Rows(rows))
			.Header("[bold]DAG Visualization[/] [dim]|[/] [dim]j/k[/]=Scroll [dim]Esc[/]=Back")
			.Border(BoxBorder.Rounded);
	}

	/// <summary>
	/// Builds an ASCII DAG visualization of orchestration step dependencies.
	/// Steps are arranged in layers (parallel groups) using topological sort.
	/// Returns Spectre.Console markup-formatted lines.
	/// </summary>
	internal static List<string> BuildDagAscii(OrchestrationStep[] steps)
	{
		var lines = new List<string>();

		if (steps.Length == 0)
			return lines;

		// Build dependency graph
		var stepsByName = new Dictionary<string, OrchestrationStep>();
		var dependents = new Dictionary<string, List<string>>();
		var inDegree = new Dictionary<string, int>();

		foreach (var step in steps)
		{
			stepsByName[step.Name] = step;
			inDegree[step.Name] = 0;
			dependents[step.Name] = [];
		}

		foreach (var step in steps)
		{
			foreach (var dep in step.DependsOn)
			{
				if (dependents.ContainsKey(dep))
				{
					dependents[dep].Add(step.Name);
					inDegree[step.Name]++;
				}
			}
		}

		// Kahn's algorithm for layers
		var layers = new List<List<OrchestrationStep>>();
		var ready = new Queue<string>();

		foreach (var (name, degree) in inDegree)
		{
			if (degree == 0)
				ready.Enqueue(name);
		}

		while (ready.Count > 0)
		{
			var layer = new List<OrchestrationStep>();
			var count = ready.Count;
			for (var i = 0; i < count; i++)
			{
				var name = ready.Dequeue();
				layer.Add(stepsByName[name]);
			}
			layers.Add(layer);

			foreach (var step in layer)
			{
				foreach (var dependent in dependents[step.Name])
				{
					inDegree[dependent]--;
					if (inDegree[dependent] == 0)
						ready.Enqueue(dependent);
				}
			}
		}

		// Build step type color map
		static string StepColor(OrchestrationStepType type) => type switch
		{
			OrchestrationStepType.Prompt => "cyan",
			OrchestrationStepType.Http => "yellow",
			OrchestrationStepType.Transform => "green",
			OrchestrationStepType.Command => "magenta",
			_ => "white"
		};

		static string StepIcon(OrchestrationStepType type) => type switch
		{
			OrchestrationStepType.Prompt => "P",
			OrchestrationStepType.Http => "H",
			OrchestrationStepType.Transform => "T",
			OrchestrationStepType.Command => "C",
			_ => "?"
		};

		// Legend
		lines.Add("[dim]Legend:[/] [cyan]P[/]=Prompt [yellow]H[/]=Http [green]T[/]=Transform [magenta]C[/]=Command");
		lines.Add("");

		// Render layers
		for (int layerIdx = 0; layerIdx < layers.Count; layerIdx++)
		{
			var layer = layers[layerIdx];
			var isParallel = layer.Count > 1;

			// Layer header
			lines.Add($"[bold]Layer {layerIdx + 1}[/] {(isParallel ? "[dim](parallel)[/]" : "[dim](sequential)[/]")}");

			// Render each step in the layer as a box
			foreach (var step in layer)
			{
				var color = StepColor(step.Type);
				var icon = StepIcon(step.Type);
				var escapedName = Markup.Escape(step.Name);
				var deps = step.DependsOn.Length > 0
					? $" [dim]<- {Markup.Escape(string.Join(", ", step.DependsOn))}[/]"
					: "";

				lines.Add($"  [{color}][[{icon}]] {escapedName}[/]{deps}");
			}

			// Draw arrows to next layer
			if (layerIdx < layers.Count - 1)
			{
				var nextLayer = layers[layerIdx + 1];
				var hasConnections = false;

				foreach (var step in layer)
				{
					foreach (var dep in dependents[step.Name])
					{
						if (nextLayer.Any(s => s.Name == dep))
						{
							if (!hasConnections)
							{
								lines.Add("  [dim]|[/]");
								lines.Add("  [dim]v[/]");
								hasConnections = true;
							}
						}
					}
				}

				if (!hasConnections)
				{
					// No direct connections but layers still flow
					lines.Add("  [dim]|[/]");
					lines.Add("  [dim]v[/]");
				}
			}
		}

		// Summary
		lines.Add("");
		lines.Add($"[dim]Total: {steps.Length} steps in {layers.Count} layers[/]");

		return lines;
	}

	#endregion

	#region MCP Servers

	/// <summary>
	/// Represents an MCP server with usage information, collected from all registered orchestrations.
	/// </summary>
	internal record McpUsageInfo(Mcp Mcp, string[] UsedByOrchestrationIds, string[] UsedByOrchestrationNames);

	/// <summary>
	/// Collects all unique MCP servers from all registered orchestrations, along with usage info.
	/// Same logic as the /api/mcps endpoint in UtilityApi.
	/// </summary>
	internal McpUsageInfo[] GetAllMcps()
	{
		return CollectMcpUsage(_registry.GetAll(), (string name) =>
		{
			// Try to load external MCPs from mcp.json files
			foreach (var entry in _registry.GetAll())
			{
				if (entry.McpPath != null && File.Exists(entry.McpPath))
				{
					try
					{
						return OrchestrationParser.ParseMcpFile(entry.McpPath);
					}
					catch
					{
						// Ignore parse errors
					}
				}
			}
			return Array.Empty<Mcp>();
		});
	}

	/// <summary>
	/// Core MCP collection logic, extracted for testability.
	/// Collects all unique MCPs from orchestration entries, optionally loading
	/// additional MCPs from external config files.
	/// </summary>
	internal static McpUsageInfo[] CollectMcpUsage(
		IEnumerable<OrchestrationEntry> entries,
		Func<string, Mcp[]>? externalMcpLoader = null)
	{
		var mcpUsage = new Dictionary<string, (Mcp Mcp, List<string> Ids, List<string> Names)>(StringComparer.OrdinalIgnoreCase);
		var processedMcpPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		foreach (var entry in entries)
		{
			// Collect orchestration-level MCPs
			foreach (var mcp in entry.Orchestration.Mcps)
			{
				AddMcpUsage(mcpUsage, mcp, entry);
			}

			// Collect step-level MCPs
			foreach (var step in entry.Orchestration.Steps.OfType<PromptOrchestrationStep>())
			{
				foreach (var mcp in step.Mcps)
				{
					AddMcpUsage(mcpUsage, mcp, entry);
				}
			}

			// Also load MCPs from external mcp.json files
			if (externalMcpLoader != null && entry.McpPath != null && !processedMcpPaths.Contains(entry.McpPath))
			{
				processedMcpPaths.Add(entry.McpPath);
				try
				{
					var externalMcps = externalMcpLoader(entry.McpPath);
					foreach (var mcp in externalMcps)
					{
						AddMcpUsage(mcpUsage, mcp, entry);
					}
				}
				catch
				{
					// Ignore parse errors for external MCP files
				}
			}
		}

		return mcpUsage.Values
			.Select(u => new McpUsageInfo(u.Mcp, u.Ids.ToArray(), u.Names.ToArray()))
			.OrderBy(m => m.Mcp.Name, StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}

	private static void AddMcpUsage(
		Dictionary<string, (Mcp Mcp, List<string> Ids, List<string> Names)> mcpUsage,
		Mcp mcp,
		OrchestrationEntry entry)
	{
		if (!mcpUsage.TryGetValue(mcp.Name, out var usage))
		{
			usage = (mcp, new List<string>(), new List<string>());
			mcpUsage[mcp.Name] = usage;
		}
		if (!usage.Ids.Contains(entry.Id))
		{
			usage.Ids.Add(entry.Id);
			usage.Names.Add(entry.Orchestration.Name);
		}
	}

	/// <summary>
	/// Returns MCPs filtered by the current search query.
	/// </summary>
	private McpUsageInfo[] GetFilteredMcps()
	{
		var all = GetAllMcps();
		if (string.IsNullOrEmpty(_searchQuery))
			return all;

		return all.Where(m =>
			m.Mcp.Name.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase) ||
			m.Mcp.Type.ToString().Contains(_searchQuery, StringComparison.OrdinalIgnoreCase) ||
			(m.Mcp is LocalMcp local && local.Command.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase)) ||
			(m.Mcp is RemoteMcp remote && remote.Endpoint.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase)) ||
			m.UsedByOrchestrationNames.Any(n => n.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase))
		).ToArray();
	}

	private void HandleMcpServersInput(ConsoleKeyInfo key)
	{
		var items = GetFilteredMcps();
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
					_selectedMcpName = items[_selectedIndex].Mcp.Name;
					NavigateTo(TuiView.McpDetail);
				}
				break;
		}
	}

	private void HandleMcpDetailInput(ConsoleKeyInfo key)
	{
		switch (key.Key)
		{
			case ConsoleKey.Backspace or ConsoleKey.Escape:
				NavigateBack();
				break;
			case ConsoleKey.UpArrow or ConsoleKey.K:
				_executionDetailScrollOffset = Math.Max(0, _executionDetailScrollOffset - 1);
				break;
			case ConsoleKey.DownArrow or ConsoleKey.J:
				_executionDetailScrollOffset++;
				break;
		}
	}

	private Panel RenderMcpServers()
	{
		var items = GetFilteredMcps();
		var table = new Table()
			.AddColumn("Name")
			.AddColumn("Type")
			.AddColumn("Endpoint / Command")
			.AddColumn("Used By")
			.Border(TableBorder.Rounded)
			.Expand();

		for (int i = 0; i < items.Length; i++)
		{
			var info = items[i];
			var mcp = info.Mcp;
			var selected = i == _selectedIndex;
			var style = selected ? "bold cyan" : "";
			var typeColor = mcp.Type == McpType.Remote ? "aqua" : "green";

			var connectionInfo = mcp switch
			{
				RemoteMcp remote => remote.Endpoint,
				LocalMcp local => $"{local.Command} {string.Join(' ', local.Arguments.Take(3))}{(local.Arguments.Length > 3 ? " ..." : "")}",
				_ => "-"
			};
			// Truncate if too long
			if (connectionInfo.Length > 50) connectionInfo = connectionInfo[..47] + "...";

			var usedBy = info.UsedByOrchestrationNames.Length == 1
				? info.UsedByOrchestrationNames[0]
				: $"{info.UsedByOrchestrationNames.Length} orchestrations";

			table.AddRow(
				new Markup($"[{style}]{Markup.Escape(mcp.Name)}[/]"),
				new Markup($"[{typeColor}]{mcp.Type}[/]"),
				new Markup($"[{style}]{Markup.Escape(connectionInfo)}[/]"),
				new Markup($"[dim]{Markup.Escape(usedBy)}[/]")
			);
		}

		if (items.Length == 0)
		{
			var msg = string.IsNullOrEmpty(_searchQuery)
				? "[dim]No MCP servers configured. MCPs are defined in orchestration JSON or mcp.json files.[/]"
				: $"[dim]No MCPs matching [yellow]{Markup.Escape(_searchQuery)}[/][/]";
			table.AddRow(new Markup(msg));
		}

		return new Panel(table)
			.Header("[bold]MCP Servers[/] [dim]|[/] [dim]Enter[/]=Details [dim]/[/]=Search [dim]Esc[/]=Back")
			.Border(BoxBorder.Rounded);
	}

	private Panel RenderMcpDetail()
	{
		var allMcps = GetAllMcps();
		var mcpInfo = allMcps.FirstOrDefault(m =>
			string.Equals(m.Mcp.Name, _selectedMcpName, StringComparison.OrdinalIgnoreCase));

		if (mcpInfo == null)
		{
			return new Panel(new Markup("[red]MCP server not found[/]"))
				.Header("[bold]MCP Detail[/]")
				.Border(BoxBorder.Rounded);
		}

		var mcp = mcpInfo.Mcp;
		var rows = new List<IRenderable>
		{
			new Markup($"[bold]Name:[/]  {Markup.Escape(mcp.Name)}"),
			new Markup($"[bold]Type:[/]  [{(mcp.Type == McpType.Remote ? "aqua" : "green")}]{mcp.Type}[/]"),
		};

		switch (mcp)
		{
			case RemoteMcp remote:
				rows.Add(new Markup($"[bold]Endpoint:[/]  [cyan]{Markup.Escape(remote.Endpoint)}[/]"));
				if (remote.Headers.Count > 0)
				{
					rows.Add(new Markup($"[bold]Headers:[/]  {remote.Headers.Count} configured"));
					foreach (var header in remote.Headers)
					{
						// Mask header values for security (they may contain tokens)
						var maskedValue = header.Value.Length > 8
							? header.Value[..4] + "****" + header.Value[^4..]
							: "****";
						rows.Add(new Markup($"  [dim]{Markup.Escape(header.Key)}:[/] {Markup.Escape(maskedValue)}"));
					}
				}
				break;

			case LocalMcp local:
				rows.Add(new Markup($"[bold]Command:[/]  [yellow]{Markup.Escape(local.Command)}[/]"));
				if (local.Arguments.Length > 0)
				{
					rows.Add(new Markup($"[bold]Arguments:[/]"));
					// Word-wrap long argument lists
					var argStr = string.Join(" ", local.Arguments);
					if (argStr.Length > GetSafeConsoleWidth() - 12)
					{
						foreach (var arg in local.Arguments)
						{
							rows.Add(new Markup($"  [dim]{Markup.Escape(arg)}[/]"));
						}
					}
					else
					{
						rows.Add(new Markup($"  [dim]{Markup.Escape(argStr)}[/]"));
					}
				}
				if (local.WorkingDirectory != null)
				{
					rows.Add(new Markup($"[bold]Working Dir:[/]  [dim]{Markup.Escape(local.WorkingDirectory)}[/]"));
				}
				break;
		}

		// Show which orchestrations use this MCP
		rows.Add(new Rule("[dim]Used By[/]") { Style = Style.Parse("dim") });

		if (mcpInfo.UsedByOrchestrationNames.Length == 0)
		{
			rows.Add(new Markup("[dim]Not currently used by any orchestration[/]"));
		}
		else
		{
			for (int i = 0; i < mcpInfo.UsedByOrchestrationNames.Length; i++)
			{
				var orchName = mcpInfo.UsedByOrchestrationNames[i];
				var orchId = mcpInfo.UsedByOrchestrationIds[i];
				var entry = _registry.Get(orchId);

				rows.Add(new Markup($"  [cyan]{Markup.Escape(orchName)}[/]"));

				// Show which steps in this orchestration use the MCP
				if (entry != null)
				{
					var stepsUsingMcp = entry.Orchestration.Steps
						.OfType<PromptOrchestrationStep>()
						.Where(s => s.Mcps.Any(m => string.Equals(m.Name, mcp.Name, StringComparison.OrdinalIgnoreCase)))
						.Select(s => s.Name)
						.ToArray();

					if (stepsUsingMcp.Length > 0)
					{
						rows.Add(new Markup($"    [dim]Steps: {Markup.Escape(string.Join(", ", stepsUsingMcp))}[/]"));
					}
					else
					{
						rows.Add(new Markup("    [dim]Defined at orchestration level[/]"));
					}
				}
			}
		}

		// Show MCP config files that reference this MCP
		var mcpPaths = _registry.GetAll()
			.Where(e => e.McpPath != null)
			.Select(e => e.McpPath!)
			.Distinct()
			.ToArray();

		if (mcpPaths.Length > 0)
		{
			rows.Add(new Rule("[dim]Config Files[/]") { Style = Style.Parse("dim") });
			foreach (var path in mcpPaths)
			{
				try
				{
					var externalMcps = OrchestrationParser.ParseMcpFile(path);
					if (externalMcps.Any(m => string.Equals(m.Name, mcp.Name, StringComparison.OrdinalIgnoreCase)))
					{
						rows.Add(new Markup($"  [dim]{Markup.Escape(path)}[/]"));
					}
				}
				catch
				{
					// Skip unparseable files
				}
			}
		}

		return new Panel(new Rows(rows))
			.Header($"[bold]{Markup.Escape(mcp.Name)}[/] [dim]|[/] [dim]Esc[/]=Back")
			.Border(BoxBorder.Rounded);
	}

	#endregion

	private Panel RenderHelpOverlay()
	{
		var rows = new List<IRenderable>
		{
			new Markup("[bold cyan]Keyboard Shortcuts[/]"),
			new Rule { Style = Style.Parse("dim") },
			new Markup(""),
			new Markup("[bold]Global:[/]"),
			new Markup("  [cyan]1-7[/]       Switch views (Dashboard, Orchestrations, Triggers, History, Active, Event Log, MCP Servers)"),
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
				rows.Add(new Markup("  [cyan]n[/]                  Create new trigger"));
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
				rows.Add(new Markup("  [cyan]1/2/3/4[/] or [cyan]Tab[/]  Switch tabs (Summary, Steps, Output, Stream)"));
				rows.Add(new Markup("  [cyan]j/k[/] or [cyan]Up/Down[/]  Navigate steps / scroll content"));
				rows.Add(new Markup("  [cyan]f[/]                  Toggle auto-scroll in Stream tab"));
				rows.Add(new Markup("  [cyan]u[/]                  Show run URL"));
				break;
			case TuiView.EventLog:
				rows.Add(new Markup("[bold]Event Log:[/]"));
				rows.Add(new Markup("  [cyan]c[/]                  Clear event log"));
				break;
			case TuiView.McpServers:
				rows.Add(new Markup("[bold]MCP Servers:[/]"));
				rows.Add(new Markup("  [cyan]j/k[/] or [cyan]Up/Down[/]  Navigate list"));
				rows.Add(new Markup("  [cyan]Enter[/]              View MCP server details"));
				break;
			case TuiView.McpDetail:
				rows.Add(new Markup("[bold]MCP Detail:[/]"));
				rows.Add(new Markup("  [cyan]j/k[/] or [cyan]Up/Down[/]  Scroll content"));
				break;
			case TuiView.OrchestrationDetail:
				rows.Add(new Markup("[bold]Orchestration Detail:[/]"));
				rows.Add(new Markup("  [cyan]r[/]                  Run orchestration"));
				rows.Add(new Markup("  [cyan]v[/]                  View version history"));
				rows.Add(new Markup("  [cyan]g[/]                  View dependency graph (DAG)"));
				rows.Add(new Markup("  [cyan]j[/]                  View raw JSON source"));
				rows.Add(new Markup("  [cyan]Esc[/]               Go back"));
				break;
			case TuiView.VersionHistory:
				rows.Add(new Markup("[bold]Version History:[/]"));
				rows.Add(new Markup("  [cyan]j/k[/] or [cyan]Up/Down[/]  Navigate versions"));
				rows.Add(new Markup("  [cyan]d[/]                  Diff selected vs previous version"));
				rows.Add(new Markup("  [cyan]x[/]                  Delete all version history"));
				rows.Add(new Markup("  [cyan]Esc[/]               Go back"));
				break;
			case TuiView.VersionDiff:
				rows.Add(new Markup("[bold]Version Diff:[/]"));
				rows.Add(new Markup("  [cyan]j/k[/] or [cyan]Up/Down[/]  Scroll diff"));
				rows.Add(new Markup("  [cyan]PgUp/PgDn[/]         Scroll by page"));
				rows.Add(new Markup("  [cyan]Esc[/]               Go back"));
				break;
			case TuiView.DagView:
				rows.Add(new Markup("[bold]DAG Visualization:[/]"));
				rows.Add(new Markup("  [cyan]j/k[/] or [cyan]Up/Down[/]  Scroll graph"));
				rows.Add(new Markup("  [cyan]PgUp/PgDn[/]         Scroll by page"));
				rows.Add(new Markup("  [cyan]Esc[/]               Go back"));
				break;
			case TuiView.RawJsonView:
				rows.Add(new Markup("[bold]Raw JSON View:[/]"));
				rows.Add(new Markup("  [cyan]j/k[/] or [cyan]Up/Down[/]  Scroll content"));
				rows.Add(new Markup("  [cyan]PgUp/PgDn[/]         Scroll by page"));
				rows.Add(new Markup("  [cyan]Esc[/]               Go back"));
				break;
			case TuiView.Checkpoints:
				rows.Add(new Markup("[bold]Checkpoints:[/]"));
				rows.Add(new Markup("  [cyan]j/k[/] or [cyan]Up/Down[/]  Navigate list"));
				rows.Add(new Markup("  [cyan]Enter/r[/]           Resume from checkpoint"));
				rows.Add(new Markup("  [cyan]d[/]                 Delete checkpoint"));
				rows.Add(new Markup("  [cyan]x[/]                 Delete all checkpoints"));
				rows.Add(new Markup("  [cyan]F5[/]                Refresh list"));
				rows.Add(new Markup("  [cyan]Esc[/]               Go back"));
				break;
			case TuiView.TriggerCreate:
				rows.Add(new Markup("[bold]Create Trigger:[/]"));
				rows.Add(new Markup("  [cyan]j/k[/] or [cyan]Up/Down[/]  Navigate options"));
				rows.Add(new Markup("  [cyan]Enter[/]              Select / Edit field"));
				rows.Add(new Markup("  [cyan]Space[/]              Toggle boolean fields"));
				rows.Add(new Markup("  [cyan]Tab[/]                Next wizard step"));
				rows.Add(new Markup("  [cyan]Shift+Tab[/]          Previous wizard step"));
				rows.Add(new Markup("  [cyan]Esc[/]               Cancel / Go back"));
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
			TuiView.Dashboard => "[dim]j/k[/] Navigate  [dim]Enter[/] Select  [dim]1-8[/] Views  [dim]?[/] Help  [dim]q[/] Quit",
			TuiView.Orchestrations => "[dim]j/k[/] Navigate  [dim]Enter[/] Details  [dim]r[/] Run  [dim]a[/] Add  [dim]s[/] Scan  [dim]d[/] Delete  [dim]/[/] Search  [dim]?[/] Help",
			TuiView.Triggers => "[dim]j/k[/] Navigate  [dim]n[/] New  [dim]e[/] Enable/Disable  [dim]r[/] Run  [dim]d[/] Delete  [dim]/[/] Search  [dim]?[/] Help",
			TuiView.History => "[dim]j/k[/] Navigate  [dim]Enter[/] Details  [dim]d[/] Delete  [dim]n/p[/] Page  [dim]/[/] Search  [dim]?[/] Help",
			TuiView.Active => "[dim]j/k[/] Navigate  [dim]Enter[/] Details  [dim]c[/] Cancel  [dim]?[/] Help",
			TuiView.OrchestrationDetail => "[dim]r[/] Run  [dim]v[/] Versions  [dim]g[/] DAG  [dim]j[/] JSON  [dim]Esc[/] Back  [dim]?[/] Help",
			TuiView.ExecutionDetail => "[dim]1-4[/] Tabs  [dim]Tab[/] Switch  [dim]j/k[/] Navigate  [dim]f[/] Follow  [dim]u[/] URL  [dim]Esc[/] Back  [dim]?[/] Help",
			TuiView.EventLog => "[dim]c[/] Clear  [dim]Esc[/] Back  [dim]?[/] Help",
			TuiView.McpServers => "[dim]j/k[/] Navigate  [dim]Enter[/] Details  [dim]/[/] Search  [dim]Esc[/] Back  [dim]?[/] Help",
			TuiView.McpDetail => "[dim]j/k[/] Scroll  [dim]Esc[/] Back  [dim]?[/] Help",
			TuiView.VersionHistory => "[dim]j/k[/] Navigate  [dim]d[/] Diff  [dim]x[/] Delete All  [dim]Esc[/] Back  [dim]?[/] Help",
			TuiView.VersionDiff => "[dim]j/k[/] Scroll  [dim]PgUp/PgDn[/] Page  [dim]Esc[/] Back  [dim]?[/] Help",
			TuiView.DagView => "[dim]j/k[/] Scroll  [dim]PgUp/PgDn[/] Page  [dim]Esc[/] Back  [dim]?[/] Help",
			TuiView.RawJsonView => "[dim]j/k[/] Scroll  [dim]PgUp/PgDn[/] Page  [dim]Esc[/] Back  [dim]?[/] Help",
			TuiView.Checkpoints => "[dim]j/k[/] Navigate  [dim]Enter/r[/] Resume  [dim]d[/] Delete  [dim]x[/] Delete All  [dim]F5[/] Refresh  [dim]?[/] Help",
			TuiView.TriggerCreate => "[dim]j/k[/] Navigate  [dim]Enter[/] Select/Edit  [dim]Tab[/] Next Step  [dim]Esc[/] Back/Cancel  [dim]?[/] Help",
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

	#region Raw JSON View

	private int _rawJsonScrollOffset;
	private string[]? _cachedRawJsonLines;

	private void HandleRawJsonViewInput(ConsoleKeyInfo key)
	{
		var totalLines = _cachedRawJsonLines?.Length ?? 0;
		var visibleLines = Math.Max(1, Console.WindowHeight - 10);

		switch (key.Key)
		{
			case ConsoleKey.Backspace or ConsoleKey.Escape:
				_cachedRawJsonLines = null;
				NavigateBack();
				break;
			case ConsoleKey.J or ConsoleKey.DownArrow:
				if (_rawJsonScrollOffset < Math.Max(0, totalLines - visibleLines))
					_rawJsonScrollOffset++;
				break;
			case ConsoleKey.K or ConsoleKey.UpArrow:
				if (_rawJsonScrollOffset > 0)
					_rawJsonScrollOffset--;
				break;
			case ConsoleKey.PageDown:
				_rawJsonScrollOffset = Math.Min(_rawJsonScrollOffset + visibleLines, Math.Max(0, totalLines - visibleLines));
				break;
			case ConsoleKey.PageUp:
				_rawJsonScrollOffset = Math.Max(0, _rawJsonScrollOffset - visibleLines);
				break;
			case ConsoleKey.Home:
				_rawJsonScrollOffset = 0;
				break;
			case ConsoleKey.End:
				_rawJsonScrollOffset = Math.Max(0, totalLines - visibleLines);
				break;
		}
	}

	private Panel RenderRawJsonView()
	{
		var entry = _selectedOrchestrationId != null ? _registry.Get(_selectedOrchestrationId) : null;
		if (entry == null)
		{
			return new Panel(new Markup("[red]Orchestration not found[/]"));
		}

		if (_cachedRawJsonLines == null)
		{
			try
			{
				var rawContent = File.ReadAllText(entry.Path);
				// Pretty-print if it's compact JSON
				try
				{
					var jsonDoc = System.Text.Json.JsonDocument.Parse(rawContent);
					rawContent = System.Text.Json.JsonSerializer.Serialize(jsonDoc, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
				}
				catch
				{
					// Not valid JSON or already formatted, use as-is
				}
				_cachedRawJsonLines = rawContent.Split('\n');
			}
			catch (Exception ex)
			{
				return new Panel(new Markup($"[red]Error reading file:[/] {Markup.Escape(ex.Message)}"))
					.Header("[bold]Raw JSON[/]")
					.Border(BoxBorder.Rounded);
			}
		}

		var visibleLines = Math.Max(1, Console.WindowHeight - 10);
		var totalLines = _cachedRawJsonLines.Length;
		var rows = new List<IRenderable>();

		// Line number gutter width
		var gutterWidth = totalLines.ToString().Length;

		for (var i = _rawJsonScrollOffset; i < Math.Min(_rawJsonScrollOffset + visibleLines, totalLines); i++)
		{
			var lineNum = (i + 1).ToString().PadLeft(gutterWidth);
			var line = _cachedRawJsonLines[i].TrimEnd('\r');
			var coloredLine = ColorizeJsonLine(line);
			rows.Add(new Markup($"[dim]{lineNum}[/] {coloredLine}"));
		}

		if (rows.Count == 0)
		{
			rows.Add(new Markup("[dim](empty file)[/]"));
		}

		// Scroll indicator
		var scrollInfo = totalLines > visibleLines
			? $" [dim]({_rawJsonScrollOffset + 1}-{Math.Min(_rawJsonScrollOffset + visibleLines, totalLines)} of {totalLines})[/]"
			: "";

		var name = Markup.Escape(entry.Orchestration.Name);
		return new Panel(new Rows(rows))
			.Header($"[bold]{name}[/] [dim]|[/] [bold]Raw JSON[/]{scrollInfo} [dim]|[/] [dim]Esc[/]=Back")
			.Border(BoxBorder.Rounded);
	}

	/// <summary>
	/// Applies basic JSON syntax highlighting to a line of text.
	/// </summary>
	internal static string ColorizeJsonLine(string line)
	{
		if (string.IsNullOrWhiteSpace(line))
			return "";

		var sb = new System.Text.StringBuilder(line.Length * 2);
		var i = 0;

		// Preserve leading whitespace
		while (i < line.Length && (line[i] == ' ' || line[i] == '\t'))
		{
			sb.Append(line[i]);
			i++;
		}

		while (i < line.Length)
		{
			var ch = line[i];

			if (ch == '"')
			{
				// Find the end of the string
				var start = i;
				i++;
				while (i < line.Length && line[i] != '"')
				{
					if (line[i] == '\\' && i + 1 < line.Length)
						i++; // Skip escaped char
					i++;
				}
				if (i < line.Length) i++; // Include closing quote

				var str = line[start..i];
				var escapedStr = Markup.Escape(str);

				// Check if this is a key (followed by colon)
				var afterStr = i;
				while (afterStr < line.Length && line[afterStr] == ' ')
					afterStr++;

				if (afterStr < line.Length && line[afterStr] == ':')
				{
					// JSON key
					sb.Append($"[cyan]{escapedStr}[/]");
				}
				else
				{
					// JSON string value
					sb.Append($"[green]{escapedStr}[/]");
				}
			}
			else if (ch == ':')
			{
				sb.Append("[dim]:[/]");
				i++;
			}
			else if (ch is '{' or '}' or '[' or ']')
			{
				sb.Append($"[bold]{Markup.Escape(ch.ToString())}[/]");
				i++;
			}
			else if (ch is ',' or ' ')
			{
				sb.Append(ch);
				i++;
			}
			else if (char.IsDigit(ch) || ch == '-' || ch == '.')
			{
				// Number
				var start = i;
				while (i < line.Length && (char.IsDigit(line[i]) || line[i] == '.' || line[i] == '-' || line[i] == '+' || line[i] == 'e' || line[i] == 'E'))
					i++;
				var num = line[start..i];
				sb.Append($"[yellow]{Markup.Escape(num)}[/]");
			}
			else if (i + 4 <= line.Length && line[i..(i + 4)] == "true")
			{
				sb.Append("[magenta]true[/]");
				i += 4;
			}
			else if (i + 5 <= line.Length && line[i..(i + 5)] == "false")
			{
				sb.Append("[magenta]false[/]");
				i += 5;
			}
			else if (i + 4 <= line.Length && line[i..(i + 4)] == "null")
			{
				sb.Append("[magenta]null[/]");
				i += 4;
			}
			else
			{
				sb.Append(Markup.Escape(ch.ToString()));
				i++;
			}
		}

		return sb.ToString();
	}

	#endregion

	#region Checkpoints

	private void HandleCheckpointsInput(ConsoleKeyInfo key)
	{
		var checkpoints = _cachedCheckpoints ??= _checkpointStore.ListCheckpointsAsync().GetAwaiter().GetResult();
		switch (key.Key)
		{
			case ConsoleKey.UpArrow or ConsoleKey.K:
				_selectedIndex = Math.Max(0, _selectedIndex - 1);
				break;
			case ConsoleKey.DownArrow or ConsoleKey.J:
				_selectedIndex = Math.Min(Math.Max(0, checkpoints.Count - 1), _selectedIndex + 1);
				break;
			case ConsoleKey.Enter or ConsoleKey.R:
				// Resume from selected checkpoint
				if (checkpoints.Count > 0 && _selectedIndex < checkpoints.Count)
				{
					var cp = checkpoints[_selectedIndex];
					var entry = _registry.GetAll().FirstOrDefault(e => e.Orchestration.Name == cp.OrchestrationName);
					if (entry == null)
					{
						ShowTransientMessage($"[red]Orchestration '{Markup.Escape(cp.OrchestrationName)}' not found in registry[/]");
						break;
					}
					RequestConfirmation(
						$"Resume [cyan]{Markup.Escape(cp.OrchestrationName)}[/] run [dim]{cp.RunId[..Math.Min(8, cp.RunId.Length)]}[/]? [dim](y/n)[/]",
						() =>
						{
							_ = Task.Run(async () =>
							{
								try
								{
									await _triggerManager.ResumeFromCheckpointAsync(entry, cp);
									_cachedCheckpoints = null; // Refresh list
									ShowTransientMessage($"[green]Resumed:[/] {Markup.Escape(cp.OrchestrationName)}");
								}
								catch (Exception ex)
								{
									ShowTransientMessage($"[red]Resume failed:[/] {Markup.Escape(ex.Message)}");
								}
							});
						});
				}
				break;
			case ConsoleKey.D or ConsoleKey.Delete:
				// Delete selected checkpoint
				if (checkpoints.Count > 0 && _selectedIndex < checkpoints.Count)
				{
					var cp = checkpoints[_selectedIndex];
					RequestConfirmation(
						$"Delete checkpoint for [cyan]{Markup.Escape(cp.OrchestrationName)}[/] run [dim]{cp.RunId[..Math.Min(8, cp.RunId.Length)]}[/]? [dim](y/n)[/]",
						() =>
						{
							_checkpointStore.DeleteCheckpointAsync(cp.OrchestrationName, cp.RunId).GetAwaiter().GetResult();
							_cachedCheckpoints = null; // Refresh
							_selectedIndex = Math.Max(0, _selectedIndex - 1);
							ShowTransientMessage($"[green]Deleted checkpoint[/] for {Markup.Escape(cp.OrchestrationName)}");
						});
				}
				break;
			case ConsoleKey.X:
				// Delete all checkpoints
				if (checkpoints.Count > 0)
				{
					RequestConfirmation(
						$"Delete [red]ALL {checkpoints.Count}[/] checkpoints? [dim](y/n)[/]",
						() =>
						{
							foreach (var cp in checkpoints)
								_checkpointStore.DeleteCheckpointAsync(cp.OrchestrationName, cp.RunId).GetAwaiter().GetResult();
							_cachedCheckpoints = null;
							_selectedIndex = 0;
							ShowTransientMessage("[green]All checkpoints deleted[/]");
						});
				}
				break;
			case ConsoleKey.F5:
				// Force refresh
				_cachedCheckpoints = null;
				_selectedIndex = 0;
				break;
		}
	}

	private Panel RenderCheckpoints()
	{
		var checkpoints = _cachedCheckpoints ??= _checkpointStore.ListCheckpointsAsync().GetAwaiter().GetResult();

		if (checkpoints.Count == 0)
		{
			var emptyRows = new List<IRenderable>
			{
				new Markup(""),
				new Markup("[dim]No checkpoints found.[/]"),
				new Markup(""),
				new Markup("[dim]Checkpoints are created automatically when an orchestration[/]"),
				new Markup("[dim]is interrupted or fails mid-execution. You can resume[/]"),
				new Markup("[dim]from a checkpoint to continue where it left off.[/]"),
			};
			return new Panel(new Rows(emptyRows))
				.Header("[bold]Checkpoints[/] [dim](0)[/]")
				.Border(BoxBorder.Rounded);
		}

		var table = new Table()
			.Border(TableBorder.Rounded)
			.AddColumn(new TableColumn("[bold]#[/]").Width(4))
			.AddColumn(new TableColumn("[bold]Orchestration[/]"))
			.AddColumn(new TableColumn("[bold]Run ID[/]").Width(12))
			.AddColumn(new TableColumn("[bold]Started[/]").Width(20))
			.AddColumn(new TableColumn("[bold]Checkpointed[/]").Width(20))
			.AddColumn(new TableColumn("[bold]Steps[/]").Width(10))
			.AddColumn(new TableColumn("[bold]Params[/]").Width(8));

		for (var i = 0; i < checkpoints.Count; i++)
		{
			var cp = checkpoints[i];
			var isSelected = i == _selectedIndex;
			var prefix = isSelected ? "[bold cyan]> [/]" : "  ";
			var style = isSelected ? "[bold]" : "[dim]";
			var endStyle = "[/]";

			var shortRunId = cp.RunId.Length > 8 ? cp.RunId[..8] + "..." : cp.RunId;
			var started = cp.StartedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
			var checkpointed = cp.CheckpointedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
			var stepInfo = $"{cp.CompletedSteps.Count} done";
			var paramCount = cp.Parameters.Count > 0 ? cp.Parameters.Count.ToString() : "-";

			table.AddRow(
				new Markup($"{prefix}{style}{i + 1}{endStyle}"),
				new Markup($"{prefix}{style}{Markup.Escape(cp.OrchestrationName)}{endStyle}"),
				new Markup($"{style}{Markup.Escape(shortRunId)}{endStyle}"),
				new Markup($"{style}{started}{endStyle}"),
				new Markup($"{style}{checkpointed}{endStyle}"),
				new Markup($"[green]{style}{Markup.Escape(stepInfo)}{endStyle}[/]"),
				new Markup($"{style}{paramCount}{endStyle}")
			);
		}

		var rows = new List<IRenderable> { table };

		// Show detail for selected checkpoint
		if (_selectedIndex < checkpoints.Count)
		{
			var selected = checkpoints[_selectedIndex];
			var detailRows = new List<IRenderable>
			{
				new Markup($"[bold]Selected Checkpoint Detail[/]"),
				new Markup($"  [cyan]Run ID:[/]         {Markup.Escape(selected.RunId)}"),
				new Markup($"  [cyan]Orchestration:[/]  {Markup.Escape(selected.OrchestrationName)}"),
				new Markup($"  [cyan]Started:[/]        {selected.StartedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}"),
				new Markup($"  [cyan]Checkpointed:[/]   {selected.CheckpointedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}"),
			};

			if (selected.TriggerId != null)
				detailRows.Add(new Markup($"  [cyan]Trigger:[/]        {Markup.Escape(selected.TriggerId)}"));

			if (selected.Parameters.Count > 0)
			{
				detailRows.Add(new Markup($"  [cyan]Parameters:[/]"));
				foreach (var (paramKey, paramValue) in selected.Parameters)
					detailRows.Add(new Markup($"    [dim]{Markup.Escape(paramKey)}[/] = [green]{Markup.Escape(paramValue)}[/]"));
			}

			detailRows.Add(new Markup($"  [cyan]Completed Steps ({selected.CompletedSteps.Count}):[/]"));
			foreach (var (stepName, stepResult) in selected.CompletedSteps)
			{
				var statusIcon = stepResult.Status == ExecutionStatus.Succeeded ? "[green]\u2713[/]" : "[red]\u2717[/]";
				var model = stepResult.ActualModel != null ? $" [dim]({Markup.Escape(stepResult.ActualModel)})[/]" : "";
				detailRows.Add(new Markup($"    {statusIcon} {Markup.Escape(stepName)}{model}"));
			}

			rows.Add(new Panel(new Rows(detailRows)).Border(BoxBorder.Rounded).BorderColor(Color.Cyan1));
		}

		return new Panel(new Rows(rows))
			.Header($"[bold]Checkpoints[/] [dim]({checkpoints.Count})[/] [dim]|[/] [dim]Enter/r[/]=Resume [dim]d[/]=Delete [dim]x[/]=Delete All")
			.Border(BoxBorder.Rounded);
	}

	#endregion

	#region Trigger Creation Wizard

	/// <summary>
	/// Resets all trigger creation wizard state to defaults.
	/// </summary>
	internal void ResetTriggerCreateState()
	{
		_triggerCreateStep = TriggerCreateStep.SelectOrchestration;
		_triggerCreateSelectedIndex = 0;
		_triggerCreateType = TriggerType.Scheduler;
		_triggerCreateOrchestrationId = null;
		_triggerCreateEnabled = true;
		_triggerCreateCron = "";
		_triggerCreateIntervalSeconds = "";
		_triggerCreateMaxRuns = "";
		_triggerCreateDelaySeconds = "0";
		_triggerCreateMaxIterations = "";
		_triggerCreateContinueOnFailure = false;
		_triggerCreateSecret = "";
		_triggerCreateMaxConcurrent = "1";
		_triggerCreateFolderPath = "Inbox";
		_triggerCreatePollInterval = "60";
		_triggerCreateMaxItems = "10";
		_triggerCreateSubjectContains = "";
		_triggerCreateSenderContains = "";
		_triggerCreateInputHandlerPrompt = "";
		_triggerCreateEditingField = false;
		_triggerCreateEditFieldIndex = -1;
		_triggerCreateFieldBuffer.Clear();
	}

	/// <summary>
	/// Gets the list of editable fields for the current trigger type in the Configure step.
	/// Returns tuples of (fieldName, currentValue, isBoolean).
	/// </summary>
	internal List<(string Name, string Value, bool IsBoolean)> GetTriggerConfigFields()
	{
		var fields = new List<(string, string, bool)>();

		// Enabled is common to all types
		fields.Add(("Enabled", _triggerCreateEnabled ? "Yes" : "No", true));

		switch (_triggerCreateType)
		{
			case TriggerType.Scheduler:
				fields.Add(("Cron Expression", _triggerCreateCron, false));
				fields.Add(("Interval (seconds)", _triggerCreateIntervalSeconds, false));
				fields.Add(("Max Runs", _triggerCreateMaxRuns, false));
				break;
			case TriggerType.Loop:
				fields.Add(("Delay (seconds)", _triggerCreateDelaySeconds, false));
				fields.Add(("Max Iterations", _triggerCreateMaxIterations, false));
				fields.Add(("Continue on Failure", _triggerCreateContinueOnFailure ? "Yes" : "No", true));
				break;
			case TriggerType.Webhook:
				fields.Add(("Secret", _triggerCreateSecret, false));
				fields.Add(("Max Concurrent", _triggerCreateMaxConcurrent, false));
				break;
			case TriggerType.Email:
				fields.Add(("Folder Path", _triggerCreateFolderPath, false));
				fields.Add(("Poll Interval (seconds)", _triggerCreatePollInterval, false));
				fields.Add(("Max Items per Poll", _triggerCreateMaxItems, false));
				fields.Add(("Subject Contains", _triggerCreateSubjectContains, false));
				fields.Add(("Sender Contains", _triggerCreateSenderContains, false));
				break;
		}

		fields.Add(("Input Handler Prompt", _triggerCreateInputHandlerPrompt, false));

		return fields;
	}

	/// <summary>
	/// Sets a trigger config field value by its index (matching GetTriggerConfigFields order).
	/// </summary>
	internal void SetTriggerConfigFieldValue(int fieldIndex, string value)
	{
		// Field 0 is always "Enabled" (boolean, handled separately)
		// Subsequent fields depend on type
		var adjustedIndex = fieldIndex - 1; // Subtract 1 for the "Enabled" field

		switch (_triggerCreateType)
		{
			case TriggerType.Scheduler:
				switch (adjustedIndex)
				{
					case 0: _triggerCreateCron = value; break;
					case 1: _triggerCreateIntervalSeconds = value; break;
					case 2: _triggerCreateMaxRuns = value; break;
					case 3: _triggerCreateInputHandlerPrompt = value; break; // Last field
				}
				break;
			case TriggerType.Loop:
				switch (adjustedIndex)
				{
					case 0: _triggerCreateDelaySeconds = value; break;
					case 1: _triggerCreateMaxIterations = value; break;
					// index 2 is ContinueOnFailure (boolean, handled separately)
					case 3: _triggerCreateInputHandlerPrompt = value; break;
				}
				break;
			case TriggerType.Webhook:
				switch (adjustedIndex)
				{
					case 0: _triggerCreateSecret = value; break;
					case 1: _triggerCreateMaxConcurrent = value; break;
					case 2: _triggerCreateInputHandlerPrompt = value; break;
				}
				break;
			case TriggerType.Email:
				switch (adjustedIndex)
				{
					case 0: _triggerCreateFolderPath = value; break;
					case 1: _triggerCreatePollInterval = value; break;
					case 2: _triggerCreateMaxItems = value; break;
					case 3: _triggerCreateSubjectContains = value; break;
					case 4: _triggerCreateSenderContains = value; break;
					case 5: _triggerCreateInputHandlerPrompt = value; break;
				}
				break;
		}
	}

	/// <summary>
	/// Toggles a boolean trigger config field by index.
	/// </summary>
	private void ToggleTriggerConfigBoolField(int fieldIndex)
	{
		if (fieldIndex == 0)
		{
			_triggerCreateEnabled = !_triggerCreateEnabled;
			return;
		}

		// Only Loop has a second boolean field (ContinueOnFailure) at adjusted index 2
		if (_triggerCreateType == TriggerType.Loop && fieldIndex == 3) // Enabled=0, Delay=1, MaxIter=2, ContinueOnFailure=3
		{
			_triggerCreateContinueOnFailure = !_triggerCreateContinueOnFailure;
		}
	}

	/// <summary>
	/// Builds a TriggerConfig from the current wizard state.
	/// </summary>
	internal TriggerConfig BuildTriggerConfig()
	{
		return _triggerCreateType switch
		{
			TriggerType.Scheduler => new SchedulerTriggerConfig
			{
				Type = TriggerType.Scheduler,
				Enabled = _triggerCreateEnabled,
				Cron = string.IsNullOrWhiteSpace(_triggerCreateCron) ? null : _triggerCreateCron.Trim(),
				IntervalSeconds = int.TryParse(_triggerCreateIntervalSeconds, out var interval) ? interval : null,
				MaxRuns = int.TryParse(_triggerCreateMaxRuns, out var maxRuns) ? maxRuns : null,
				InputHandlerPrompt = string.IsNullOrWhiteSpace(_triggerCreateInputHandlerPrompt) ? null : _triggerCreateInputHandlerPrompt.Trim(),
			},
			TriggerType.Loop => new LoopTriggerConfig
			{
				Type = TriggerType.Loop,
				Enabled = _triggerCreateEnabled,
				DelaySeconds = int.TryParse(_triggerCreateDelaySeconds, out var delay) ? delay : 0,
				MaxIterations = int.TryParse(_triggerCreateMaxIterations, out var maxIter) ? maxIter : null,
				ContinueOnFailure = _triggerCreateContinueOnFailure,
				InputHandlerPrompt = string.IsNullOrWhiteSpace(_triggerCreateInputHandlerPrompt) ? null : _triggerCreateInputHandlerPrompt.Trim(),
			},
			TriggerType.Webhook => new WebhookTriggerConfig
			{
				Type = TriggerType.Webhook,
				Enabled = _triggerCreateEnabled,
				Secret = string.IsNullOrWhiteSpace(_triggerCreateSecret) ? null : _triggerCreateSecret.Trim(),
				MaxConcurrent = int.TryParse(_triggerCreateMaxConcurrent, out var maxConc) ? maxConc : 1,
				InputHandlerPrompt = string.IsNullOrWhiteSpace(_triggerCreateInputHandlerPrompt) ? null : _triggerCreateInputHandlerPrompt.Trim(),
			},
			TriggerType.Email => new EmailTriggerConfig
			{
				Type = TriggerType.Email,
				Enabled = _triggerCreateEnabled,
				FolderPath = string.IsNullOrWhiteSpace(_triggerCreateFolderPath) ? "Inbox" : _triggerCreateFolderPath.Trim(),
				PollIntervalSeconds = int.TryParse(_triggerCreatePollInterval, out var pollSec) ? pollSec : 60,
				MaxItemsPerPoll = int.TryParse(_triggerCreateMaxItems, out var maxItems) ? maxItems : 10,
				SubjectContains = string.IsNullOrWhiteSpace(_triggerCreateSubjectContains) ? null : _triggerCreateSubjectContains.Trim(),
				SenderContains = string.IsNullOrWhiteSpace(_triggerCreateSenderContains) ? null : _triggerCreateSenderContains.Trim(),
				InputHandlerPrompt = string.IsNullOrWhiteSpace(_triggerCreateInputHandlerPrompt) ? null : _triggerCreateInputHandlerPrompt.Trim(),
			},
			_ => new SchedulerTriggerConfig { Type = TriggerType.Scheduler, Enabled = _triggerCreateEnabled }
		};
	}

	/// <summary>
	/// Builds a review summary of the trigger configuration.
	/// </summary>
	internal List<(string Label, string Value)> BuildTriggerReviewSummary()
	{
		var summary = new List<(string, string)>();

		// Orchestration info
		var entry = _triggerCreateOrchestrationId != null ? _registry.Get(_triggerCreateOrchestrationId) : null;
		summary.Add(("Orchestration", entry?.Orchestration.Name ?? _triggerCreateOrchestrationId ?? "(none)"));
		summary.Add(("Trigger Type", _triggerCreateType.ToString()));
		summary.Add(("Enabled", _triggerCreateEnabled ? "Yes" : "No"));

		switch (_triggerCreateType)
		{
			case TriggerType.Scheduler:
				if (!string.IsNullOrWhiteSpace(_triggerCreateCron))
					summary.Add(("Cron", _triggerCreateCron));
				if (!string.IsNullOrWhiteSpace(_triggerCreateIntervalSeconds))
					summary.Add(("Interval", $"{_triggerCreateIntervalSeconds}s"));
				if (!string.IsNullOrWhiteSpace(_triggerCreateMaxRuns))
					summary.Add(("Max Runs", _triggerCreateMaxRuns));
				break;
			case TriggerType.Loop:
				summary.Add(("Delay", $"{_triggerCreateDelaySeconds}s"));
				if (!string.IsNullOrWhiteSpace(_triggerCreateMaxIterations))
					summary.Add(("Max Iterations", _triggerCreateMaxIterations));
				summary.Add(("Continue on Failure", _triggerCreateContinueOnFailure ? "Yes" : "No"));
				break;
			case TriggerType.Webhook:
				summary.Add(("Secret", string.IsNullOrWhiteSpace(_triggerCreateSecret) ? "(none)" : "(set)"));
				summary.Add(("Max Concurrent", _triggerCreateMaxConcurrent));
				break;
			case TriggerType.Email:
				summary.Add(("Folder", _triggerCreateFolderPath));
				summary.Add(("Poll Interval", $"{_triggerCreatePollInterval}s"));
				summary.Add(("Max Items", _triggerCreateMaxItems));
				if (!string.IsNullOrWhiteSpace(_triggerCreateSubjectContains))
					summary.Add(("Subject Filter", _triggerCreateSubjectContains));
				if (!string.IsNullOrWhiteSpace(_triggerCreateSenderContains))
					summary.Add(("Sender Filter", _triggerCreateSenderContains));
				break;
		}

		if (!string.IsNullOrWhiteSpace(_triggerCreateInputHandlerPrompt))
			summary.Add(("Input Handler", "(set)"));

		return summary;
	}

	/// <summary>
	/// Validates the current trigger configuration.
	/// Returns null if valid, or an error message.
	/// </summary>
	internal string? ValidateTriggerConfig()
	{
		if (_triggerCreateOrchestrationId == null)
			return "No orchestration selected";

		if (_registry.Get(_triggerCreateOrchestrationId) == null)
			return "Selected orchestration not found in registry";

		switch (_triggerCreateType)
		{
			case TriggerType.Scheduler:
				if (string.IsNullOrWhiteSpace(_triggerCreateCron) && string.IsNullOrWhiteSpace(_triggerCreateIntervalSeconds))
					return "Scheduler trigger requires either a cron expression or interval";
				if (!string.IsNullOrWhiteSpace(_triggerCreateIntervalSeconds) && (!int.TryParse(_triggerCreateIntervalSeconds, out var intSec) || intSec <= 0))
					return "Interval must be a positive integer (seconds)";
				break;
			case TriggerType.Webhook:
				if (!string.IsNullOrWhiteSpace(_triggerCreateMaxConcurrent) && (!int.TryParse(_triggerCreateMaxConcurrent, out var maxC) || maxC <= 0))
					return "Max concurrent must be a positive integer";
				break;
			case TriggerType.Email:
				if (!string.IsNullOrWhiteSpace(_triggerCreatePollInterval) && (!int.TryParse(_triggerCreatePollInterval, out var poll) || poll <= 0))
					return "Poll interval must be a positive integer (seconds)";
				break;
		}

		return null;
	}

	private void HandleTriggerCreateInput(ConsoleKeyInfo key)
	{
		// If editing a text field inline, handle text input
		if (_triggerCreateEditingField)
		{
			HandleTriggerFieldEdit(key);
			return;
		}

		switch (_triggerCreateStep)
		{
			case TriggerCreateStep.SelectOrchestration:
				HandleTriggerCreateSelectOrchestration(key);
				break;
			case TriggerCreateStep.SelectType:
				HandleTriggerCreateSelectType(key);
				break;
			case TriggerCreateStep.Configure:
				HandleTriggerCreateConfigure(key);
				break;
			case TriggerCreateStep.Review:
				HandleTriggerCreateReview(key);
				break;
		}
	}

	private void HandleTriggerCreateSelectOrchestration(ConsoleKeyInfo key)
	{
		var entries = _registry.GetAll().ToArray();
		switch (key.Key)
		{
			case ConsoleKey.UpArrow or ConsoleKey.K:
				_triggerCreateSelectedIndex = Math.Max(0, _triggerCreateSelectedIndex - 1);
				break;
			case ConsoleKey.DownArrow or ConsoleKey.J:
				_triggerCreateSelectedIndex = Math.Min(Math.Max(0, entries.Length - 1), _triggerCreateSelectedIndex + 1);
				break;
			case ConsoleKey.Enter:
				if (entries.Length > 0 && _triggerCreateSelectedIndex < entries.Length)
				{
					_triggerCreateOrchestrationId = entries[_triggerCreateSelectedIndex].Id;
					_triggerCreateStep = TriggerCreateStep.SelectType;
					_triggerCreateSelectedIndex = 0;
				}
				break;
			case ConsoleKey.Escape:
				NavigateBack();
				break;
		}
	}

	private void HandleTriggerCreateSelectType(ConsoleKeyInfo key)
	{
		var types = Enum.GetValues<TriggerType>();
		switch (key.Key)
		{
			case ConsoleKey.UpArrow or ConsoleKey.K:
				_triggerCreateSelectedIndex = Math.Max(0, _triggerCreateSelectedIndex - 1);
				break;
			case ConsoleKey.DownArrow or ConsoleKey.J:
				_triggerCreateSelectedIndex = Math.Min(types.Length - 1, _triggerCreateSelectedIndex + 1);
				break;
			case ConsoleKey.Enter:
				_triggerCreateType = types[_triggerCreateSelectedIndex];
				_triggerCreateStep = TriggerCreateStep.Configure;
				_triggerCreateSelectedIndex = 0;
				break;
			case ConsoleKey.Tab when !key.Modifiers.HasFlag(ConsoleModifiers.Shift):
				_triggerCreateType = types[_triggerCreateSelectedIndex];
				_triggerCreateStep = TriggerCreateStep.Configure;
				_triggerCreateSelectedIndex = 0;
				break;
			case ConsoleKey.Tab when key.Modifiers.HasFlag(ConsoleModifiers.Shift):
			case ConsoleKey.Escape:
				_triggerCreateStep = TriggerCreateStep.SelectOrchestration;
				_triggerCreateSelectedIndex = 0;
				break;
		}
	}

	private void HandleTriggerCreateConfigure(ConsoleKeyInfo key)
	{
		var fields = GetTriggerConfigFields();
		switch (key.Key)
		{
			case ConsoleKey.UpArrow or ConsoleKey.K:
				_triggerCreateSelectedIndex = Math.Max(0, _triggerCreateSelectedIndex - 1);
				break;
			case ConsoleKey.DownArrow or ConsoleKey.J:
				_triggerCreateSelectedIndex = Math.Min(fields.Count - 1, _triggerCreateSelectedIndex + 1);
				break;
			case ConsoleKey.Spacebar:
				// Toggle boolean fields
				if (_triggerCreateSelectedIndex < fields.Count && fields[_triggerCreateSelectedIndex].IsBoolean)
				{
					ToggleTriggerConfigBoolField(_triggerCreateSelectedIndex);
				}
				break;
			case ConsoleKey.Enter:
				if (_triggerCreateSelectedIndex < fields.Count)
				{
					var field = fields[_triggerCreateSelectedIndex];
					if (field.IsBoolean)
					{
						ToggleTriggerConfigBoolField(_triggerCreateSelectedIndex);
					}
					else
					{
						// Start inline editing
						_triggerCreateEditingField = true;
						_triggerCreateEditFieldIndex = _triggerCreateSelectedIndex;
						_triggerCreateFieldBuffer.Clear();
						_triggerCreateFieldBuffer.Append(field.Value);
					}
				}
				break;
			case ConsoleKey.Tab when !key.Modifiers.HasFlag(ConsoleModifiers.Shift):
				_triggerCreateStep = TriggerCreateStep.Review;
				_triggerCreateSelectedIndex = 0;
				break;
			case ConsoleKey.Tab when key.Modifiers.HasFlag(ConsoleModifiers.Shift):
			case ConsoleKey.Escape:
				_triggerCreateStep = TriggerCreateStep.SelectType;
				_triggerCreateSelectedIndex = 0;
				break;
		}
	}

	private void HandleTriggerCreateReview(ConsoleKeyInfo key)
	{
		switch (key.Key)
		{
			case ConsoleKey.Enter or ConsoleKey.Y:
				// Submit / confirm creation
				var validationError = ValidateTriggerConfig();
				if (validationError != null)
				{
					ShowTransientMessage($"[red]Validation error:[/] {Markup.Escape(validationError)}");
					return;
				}

				var entry = _registry.Get(_triggerCreateOrchestrationId!);
				if (entry == null)
				{
					ShowTransientMessage("[red]Orchestration not found[/]");
					return;
				}

				var config = BuildTriggerConfig();
				_triggerManager.RegisterTrigger(
					entry.Path,
					entry.McpPath,
					config,
					null,
					TriggerSource.User,
					entry.Id);

				ShowTransientMessage($"[green]Created[/] {_triggerCreateType} trigger for {Markup.Escape(entry.Orchestration.Name)}");
				NavigateBack(); // Return to Triggers view
				break;

			case ConsoleKey.Tab when key.Modifiers.HasFlag(ConsoleModifiers.Shift):
				_triggerCreateStep = TriggerCreateStep.Configure;
				_triggerCreateSelectedIndex = 0;
				break;

			case ConsoleKey.Escape:
				_triggerCreateStep = TriggerCreateStep.Configure;
				_triggerCreateSelectedIndex = 0;
				break;
		}
	}

	private void HandleTriggerFieldEdit(ConsoleKeyInfo key)
	{
		switch (key.Key)
		{
			case ConsoleKey.Escape:
				// Cancel editing, discard changes
				_triggerCreateEditingField = false;
				_triggerCreateEditFieldIndex = -1;
				_triggerCreateFieldBuffer.Clear();
				break;
			case ConsoleKey.Enter:
				// Commit the edited value
				var fields = GetTriggerConfigFields();
				if (_triggerCreateEditFieldIndex >= 0 && _triggerCreateEditFieldIndex < fields.Count)
				{
					SetTriggerConfigFieldValue(_triggerCreateEditFieldIndex, _triggerCreateFieldBuffer.ToString());
				}
				_triggerCreateEditingField = false;
				_triggerCreateEditFieldIndex = -1;
				_triggerCreateFieldBuffer.Clear();
				break;
			case ConsoleKey.Backspace:
				if (_triggerCreateFieldBuffer.Length > 0)
					_triggerCreateFieldBuffer.Remove(_triggerCreateFieldBuffer.Length - 1, 1);
				break;
			default:
				if (!char.IsControl(key.KeyChar))
					_triggerCreateFieldBuffer.Append(key.KeyChar);
				break;
		}
	}

	private Panel RenderTriggerCreate()
	{
		var rows = new List<IRenderable>();

		// Progress indicator
		var steps = new[] { "Orchestration", "Type", "Configure", "Review" };
		var stepParts = new List<string>();
		for (int i = 0; i < steps.Length; i++)
		{
			var stepEnum = (TriggerCreateStep)i;
			if (stepEnum == _triggerCreateStep)
				stepParts.Add($"[bold cyan]({i + 1}) {steps[i]}[/]");
			else if (stepEnum < _triggerCreateStep)
				stepParts.Add($"[green]({i + 1}) {steps[i]}[/]");
			else
				stepParts.Add($"[dim]({i + 1}) {steps[i]}[/]");
		}
		rows.Add(new Markup(string.Join(" [dim]>[/] ", stepParts)));
		rows.Add(new Markup(""));

		switch (_triggerCreateStep)
		{
			case TriggerCreateStep.SelectOrchestration:
				rows.AddRange(RenderTriggerCreateSelectOrchestration());
				break;
			case TriggerCreateStep.SelectType:
				rows.AddRange(RenderTriggerCreateSelectType());
				break;
			case TriggerCreateStep.Configure:
				rows.AddRange(RenderTriggerCreateConfigure());
				break;
			case TriggerCreateStep.Review:
				rows.AddRange(RenderTriggerCreateReview());
				break;
		}

		return new Panel(new Rows(rows))
			.Header("[bold]Create Trigger[/] [dim]|[/] [dim]Tab[/]=Next [dim]Shift+Tab[/]=Back [dim]Esc[/]=Cancel")
			.Border(BoxBorder.Rounded);
	}

	private List<IRenderable> RenderTriggerCreateSelectOrchestration()
	{
		var rows = new List<IRenderable>();
		rows.Add(new Markup("[bold]Select an orchestration to attach the trigger to:[/]"));
		rows.Add(new Markup(""));

		var entries = _registry.GetAll().ToArray();
		if (entries.Length == 0)
		{
			rows.Add(new Markup("[dim]No orchestrations registered. Add orchestrations first.[/]"));
			return rows;
		}

		var table = new Table()
			.Border(TableBorder.Simple)
			.HideHeaders()
			.AddColumn("")
			.AddColumn("")
			.AddColumn("");

		for (int i = 0; i < entries.Length; i++)
		{
			var entry = entries[i];
			var isSelected = i == _triggerCreateSelectedIndex;
			var prefix = isSelected ? "[bold cyan]> [/]" : "  ";
			var style = isSelected ? "bold" : "dim";
			var hasTrigger = entry.Orchestration.Trigger != null;
			var triggerInfo = hasTrigger ? "[yellow](has trigger)[/]" : "";

			table.AddRow(
				new Markup($"{prefix}[{style}]{Markup.Escape(entry.Orchestration.Name)}[/]"),
				new Markup($"[dim]v{Markup.Escape(entry.Orchestration.Version ?? "?")}[/]"),
				new Markup(triggerInfo)
			);
		}

		rows.Add(table);
		rows.Add(new Markup(""));
		rows.Add(new Markup("[dim]Press Enter to select, Esc to cancel[/]"));
		return rows;
	}

	private List<IRenderable> RenderTriggerCreateSelectType()
	{
		var rows = new List<IRenderable>();

		// Show selected orchestration
		var entry = _triggerCreateOrchestrationId != null ? _registry.Get(_triggerCreateOrchestrationId) : null;
		rows.Add(new Markup($"[dim]Orchestration:[/] [cyan]{Markup.Escape(entry?.Orchestration.Name ?? "(unknown)")}[/]"));
		rows.Add(new Markup(""));
		rows.Add(new Markup("[bold]Select trigger type:[/]"));
		rows.Add(new Markup(""));

		var types = Enum.GetValues<TriggerType>();
		var descriptions = new Dictionary<TriggerType, string>
		{
			[TriggerType.Scheduler] = "Run on a cron schedule or at fixed intervals",
			[TriggerType.Loop] = "Re-run automatically after each completion",
			[TriggerType.Webhook] = "Trigger via HTTP POST from external services",
			[TriggerType.Email] = "Poll an Outlook folder for new emails"
		};

		for (int i = 0; i < types.Length; i++)
		{
			var type = types[i];
			var isSelected = i == _triggerCreateSelectedIndex;
			var prefix = isSelected ? "[bold cyan]> [/]" : "  ";
			var style = isSelected ? "bold" : "";
			var desc = descriptions.GetValueOrDefault(type, "");

			rows.Add(new Markup($"{prefix}[{style}]{type}[/]  [dim]{Markup.Escape(desc)}[/]"));
		}

		rows.Add(new Markup(""));
		rows.Add(new Markup("[dim]Press Enter to select, Tab to advance, Esc to go back[/]"));
		return rows;
	}

	private List<IRenderable> RenderTriggerCreateConfigure()
	{
		var rows = new List<IRenderable>();

		// Show context
		var entry = _triggerCreateOrchestrationId != null ? _registry.Get(_triggerCreateOrchestrationId) : null;
		rows.Add(new Markup($"[dim]Orchestration:[/] [cyan]{Markup.Escape(entry?.Orchestration.Name ?? "(unknown)")}[/]  [dim]Type:[/] [yellow]{_triggerCreateType}[/]"));
		rows.Add(new Markup(""));
		rows.Add(new Markup("[bold]Configure trigger settings:[/]"));
		rows.Add(new Markup(""));

		var fields = GetTriggerConfigFields();
		for (int i = 0; i < fields.Count; i++)
		{
			var (name, value, isBoolean) = fields[i];
			var isSelected = i == _triggerCreateSelectedIndex;
			var isEditing = _triggerCreateEditingField && i == _triggerCreateEditFieldIndex;
			var prefix = isSelected ? "[bold cyan]> [/]" : "  ";

			if (isEditing)
			{
				// Show editing state with cursor
				var editValue = _triggerCreateFieldBuffer.ToString();
				rows.Add(new Markup($"{prefix}[bold]{Markup.Escape(name)}:[/] [green]{Markup.Escape(editValue)}[/][blink]|[/]"));
			}
			else if (isBoolean)
			{
				var boolStyle = value == "Yes" ? "[green]Yes[/]" : "[red]No[/]";
				var toggle = isSelected ? " [dim](Space/Enter to toggle)[/]" : "";
				rows.Add(new Markup($"{prefix}[bold]{Markup.Escape(name)}:[/] {boolStyle}{toggle}"));
			}
			else
			{
				var displayValue = string.IsNullOrWhiteSpace(value) ? "[dim](empty)[/]" : $"[green]{Markup.Escape(value)}[/]";
				var edit = isSelected ? " [dim](Enter to edit)[/]" : "";
				rows.Add(new Markup($"{prefix}[bold]{Markup.Escape(name)}:[/] {displayValue}{edit}"));
			}
		}

		rows.Add(new Markup(""));
		rows.Add(new Markup("[dim]Tab to continue to review, Esc/Shift+Tab to go back[/]"));
		return rows;
	}

	private List<IRenderable> RenderTriggerCreateReview()
	{
		var rows = new List<IRenderable>();
		rows.Add(new Markup("[bold]Review trigger configuration:[/]"));
		rows.Add(new Markup(""));

		var summary = BuildTriggerReviewSummary();
		var table = new Table()
			.Border(TableBorder.Rounded)
			.AddColumn(new TableColumn("[bold]Setting[/]"))
			.AddColumn(new TableColumn("[bold]Value[/]"));

		foreach (var (label, value) in summary)
		{
			table.AddRow(
				new Markup($"[cyan]{Markup.Escape(label)}[/]"),
				new Markup($"[green]{Markup.Escape(value)}[/]")
			);
		}

		rows.Add(table);

		// Validation
		var validationError = ValidateTriggerConfig();
		if (validationError != null)
		{
			rows.Add(new Markup($"[red]Validation error: {Markup.Escape(validationError)}[/]"));
			rows.Add(new Markup("[dim]Go back (Shift+Tab) to fix the configuration[/]"));
		}
		else
		{
			rows.Add(new Markup(""));
			rows.Add(new Markup("[bold green]Configuration is valid.[/]"));
			rows.Add(new Markup("[dim]Press Enter or Y to create the trigger, Esc/Shift+Tab to go back[/]"));
		}

		return rows;
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
	EventLog,
	McpServers,
	McpDetail,
	VersionHistory,
	VersionDiff,
	DagView,
	RawJsonView,
	Checkpoints,
	TriggerCreate
}

/// <summary>
/// Steps in the trigger creation wizard.
/// </summary>
public enum TriggerCreateStep
{
	SelectOrchestration,
	SelectType,
	Configure,
	Review
}

/// <summary>
/// Tabs within the ExecutionDetail view.
/// </summary>
public enum ExecutionDetailTab
{
	Summary,
	Steps,
	Output,
	Stream
}
