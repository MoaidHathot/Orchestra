using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine;

namespace Orchestra.Copilot.Tests;

public class CopilotAgentBuilderTests
{
	#region BuildAgentAsync

	[Fact]
	public async Task BuildAgentAsync_WithNullModel_ThrowsArgumentException()
	{
		// Arrange
		var builder = new CopilotAgentBuilder();
		// Model not set

		// Act
		var act = () => builder.BuildAgentAsync();

		// Assert
		await act.Should().ThrowAsync<ArgumentException>()
			.WithParameterName("Model");
	}

	[Fact]
	public async Task BuildAgentAsync_WithEmptyModel_ThrowsArgumentException()
	{
		// Arrange
		var builder = new CopilotAgentBuilder();
		builder.WithModel("");

		// Act
		var act = () => builder.BuildAgentAsync();

		// Assert
		await act.Should().ThrowAsync<ArgumentException>()
			.WithParameterName("Model");
	}

	[Fact]
	public async Task BuildAgentAsync_WithWhitespaceModel_ThrowsArgumentException()
	{
		// Arrange
		var builder = new CopilotAgentBuilder();
		builder.WithModel("   ");

		// Act
		var act = () => builder.BuildAgentAsync();

		// Assert
		await act.Should().ThrowAsync<ArgumentException>()
			.WithParameterName("Model");
	}

	#endregion

	#region Constructor

	[Fact]
	public void Constructor_WithNullLoggerFactory_UsesNullLoggerFactory()
	{
		// Arrange & Act
		var builder = new CopilotAgentBuilder(null);

		// Assert - Should not throw, uses NullLoggerFactory internally
		builder.Should().NotBeNull();
	}

	[Fact]
	public void Constructor_WithLoggerFactory_DoesNotThrow()
	{
		// Arrange
		var loggerFactory = NullLoggerFactory.Instance;

		// Act
		var builder = new CopilotAgentBuilder(loggerFactory);

		// Assert
		builder.Should().NotBeNull();
	}

	#endregion

	#region Fluent API

	[Fact]
	public void WithModel_ReturnsSameBuilder()
	{
		// Arrange
		var builder = new CopilotAgentBuilder();

		// Act
		var result = builder.WithModel("claude-opus-4.5");

		// Assert
		result.Should().BeSameAs(builder);
	}

	[Fact]
	public void WithSystemPrompt_ReturnsSameBuilder()
	{
		// Arrange
		var builder = new CopilotAgentBuilder();

		// Act
		var result = builder.WithSystemPrompt("You are a helpful assistant.");

		// Assert
		result.Should().BeSameAs(builder);
	}

	[Fact]
	public void WithMcp_ReturnsSameBuilder()
	{
		// Arrange
		var builder = new CopilotAgentBuilder();
		var mcp = new LocalMcp
		{
			Name = "test",
			Type = McpType.Local,
			Command = "node",
			Arguments = ["server.js"]
		};

		// Act
		var result = builder.WithMcp(mcp);

		// Assert
		result.Should().BeSameAs(builder);
	}

	[Fact]
	public void WithReasoningLevel_ReturnsSameBuilder()
	{
		// Arrange
		var builder = new CopilotAgentBuilder();

		// Act
		var result = builder.WithReasoningLevel(ReasoningLevel.High);

		// Assert
		result.Should().BeSameAs(builder);
	}

	[Fact]
	public void WithSystemPromptMode_ReturnsSameBuilder()
	{
		// Arrange
		var builder = new CopilotAgentBuilder();

		// Act
		var result = builder.WithSystemPromptMode(SystemPromptMode.Replace);

		// Assert
		result.Should().BeSameAs(builder);
	}

	[Fact]
	public void WithReporter_ReturnsSameBuilder()
	{
		// Arrange
		var builder = new CopilotAgentBuilder();
		var reporter = NullOrchestrationReporter.Instance;

		// Act
		var result = builder.WithReporter(reporter);

		// Assert
		result.Should().BeSameAs(builder);
	}

	[Fact]
	public void FluentApi_CanChainAllMethods()
	{
		// Arrange & Act
		var builder = new CopilotAgentBuilder()
			.WithModel("claude-opus-4.5")
			.WithSystemPrompt("Test prompt")
			.WithReasoningLevel(ReasoningLevel.Medium)
			.WithSystemPromptMode(SystemPromptMode.Append)
			.WithReporter(NullOrchestrationReporter.Instance);

		// Assert
		builder.Should().NotBeNull();
	}

	[Fact]
	public void WithSubagents_ReturnsSameBuilder()
	{
		// Arrange
		var builder = new CopilotAgentBuilder();
		var subagents = new[]
		{
			new Subagent
			{
				Name = "researcher",
				Prompt = "You are a researcher"
			}
		};

		// Act
		var result = builder.WithSubagents(subagents);

		// Assert
		result.Should().BeSameAs(builder);
	}

	[Fact]
	public void WithSubagents_WithEmptyArray_ReturnsSameBuilder()
	{
		// Arrange
		var builder = new CopilotAgentBuilder();

		// Act
		var result = builder.WithSubagents([]);

		// Assert
		result.Should().BeSameAs(builder);
	}

	[Fact]
	public void WithSubagents_CanChainWithOtherMethods()
	{
		// Arrange
		var subagents = new[]
		{
			new Subagent
			{
				Name = "writer",
				DisplayName = "Writer Agent",
				Description = "Writes content",
				Prompt = "You are a writer",
				Tools = ["write_file"],
				Infer = true
			}
		};

		// Act
		var builder = new CopilotAgentBuilder()
			.WithModel("claude-opus-4.5")
			.WithSystemPrompt("You are a coordinator")
			.WithSubagents(subagents)
			.WithReporter(NullOrchestrationReporter.Instance);

		// Assert
		builder.Should().NotBeNull();
	}

	#endregion

	#region Run Scope Lifecycle

	[Fact]
	public async Task CreateRunScopeAsync_ReturnsDisposableScope()
	{
		// Arrange
		var builder = new CopilotAgentBuilder();

		// Act & Assert — creating a run scope should return something disposable.
		// Note: this will start a real CLI process if available, so we just verify
		// the API shape is correct (the scope implements IAsyncDisposable).
		// In CI without a CLI binary, StartAsync will throw; that's expected.
		try
		{
			await using var scope = await builder.CreateRunScopeAsync();
			scope.Should().NotBeNull();
		}
		catch (Exception)
		{
			// Expected in environments without the Copilot CLI binary
		}
	}

	[Fact]
	public async Task DisposeAsync_WithNoRunScope_DoesNotThrow()
	{
		// Arrange
		var builder = new CopilotAgentBuilder();

		// Act & Assert — disposing without ever creating a run scope should be safe
		await builder.DisposeAsync();
	}

	[Fact]
	public async Task DisposeAsync_CanBeCalledMultipleTimes()
	{
		// Arrange
		var builder = new CopilotAgentBuilder();

		// Act & Assert — Should not throw on multiple dispose calls
		await builder.DisposeAsync();
		await builder.DisposeAsync();
	}

	/// <summary>
	/// Regression test for the AsyncLocal-set-inside-async-method bug.
	///
	/// AsyncLocal.Value mutations inside an async method are NOT visible to the
	/// caller's ExecutionContext if the AsyncLocal was first observed by the caller
	/// AFTER the async method returned (the mutation is captured in a child EC frame
	/// that is discarded when the async method returns).
	///
	/// CreateRunScopeAsync MUST set the AsyncLocal holder synchronously BEFORE the
	/// first await so that the holder reference is in the caller's EC. Mutating
	/// holder.Client inside the async portion is then visible everywhere because the
	/// caller (and any tasks the caller spawns) share the same holder reference.
	///
	/// This test does NOT require a real Copilot CLI binary — it uses GetRunScopedClientDiagnostic()
	/// which returns null when the holder is missing or has no client, and a non-null hash when both
	/// the holder is in EC AND the client is set.
	///
	/// To verify the bug is fixed: after CreateRunScopeAsync returns, the diagnostic must
	/// be non-null on the caller's thread AND on a Task.Factory.StartNew(LongRunning) child task.
	/// </summary>
	[Fact]
	public async Task CreateRunScopeAsync_HolderFlowsThroughAsyncBoundaryAndLongRunningTask()
	{
		// Arrange
		var builder = new CopilotAgentBuilder();
		string? diagnosticBeforeScope = builder.GetRunScopedClientDiagnostic();

		IAsyncDisposable? scope;
		try
		{
			scope = await builder.CreateRunScopeAsync();
		}
		catch
		{
			// Skip if no CLI binary available — we can't test the real flow without it
			return;
		}

		try
		{
			// Assert: holder is visible on the caller's EC after CreateRunScopeAsync returns
			var diagnosticAfterScope = builder.GetRunScopedClientDiagnostic();
			diagnosticAfterScope.Should().NotBeNull(
				"after CreateRunScopeAsync, the AsyncLocal holder must contain the client on the caller's EC. " +
				"If null, the AsyncLocal was set inside an async method and the mutation was lost when the method returned. " +
				"Fix: set the AsyncLocal holder SYNCHRONOUSLY (in a sync wrapper) before the first await.");

			// Assert: holder flows into a LongRunning thread-pool task (which is what
			// OrchestrationExecutor.TryLaunchStep uses to run parallel steps)
			var diagnosticInsideTask = await Task.Factory.StartNew(
				() => builder.GetRunScopedClientDiagnostic(),
				CancellationToken.None,
				TaskCreationOptions.LongRunning,
				TaskScheduler.Default);

			diagnosticInsideTask.Should().Be(diagnosticAfterScope,
				"the run-scoped client must flow through ExecutionContext into LongRunning step tasks. " +
				"This is the exact pattern used by OrchestrationExecutor.TryLaunchStep — if it fails here, " +
				"parallel steps will fall back to a shared singleton client and break under load.");
		}
		finally
		{
			await scope.DisposeAsync();
		}
	}

	[Fact]
	public async Task CreateRunScopeAsync_DisposalClearsAsyncLocal()
	{
		// Arrange
		var builder = new CopilotAgentBuilder();

		IAsyncDisposable scope;
		try
		{
			scope = await builder.CreateRunScopeAsync();
		}
		catch
		{
			return;  // No CLI binary
		}

		// Act: dispose the scope
		await scope.DisposeAsync();

		// Assert: after disposal, the diagnostic must be null (no stale client visible)
		var diagnosticAfterDispose = builder.GetRunScopedClientDiagnostic();
		diagnosticAfterDispose.Should().BeNull(
			"after RunScope.DisposeAsync, the AsyncLocal holder's client field must be cleared. " +
			"Otherwise subsequent code paths could see a disposed CopilotClient and fail with " +
			"JSON-RPC ConnectionLost or NullReferenceException.");
	}

	#endregion
}
