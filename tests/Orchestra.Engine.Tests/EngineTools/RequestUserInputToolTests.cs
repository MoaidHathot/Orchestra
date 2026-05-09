using FluentAssertions;

namespace Orchestra.Engine.Tests.EngineTools;

/// <summary>
/// Unit tests for <see cref="RequestUserInputTool"/> verifying tool metadata, argument
/// parsing, and the wait-then-resolve flow against an in-memory waiter stub.
/// </summary>
public class RequestUserInputToolTests
{
	[Fact]
	public void Name_ReturnsExpectedName()
	{
		var tool = new RequestUserInputTool();

		tool.Name.Should().Be("orchestra_request_user_input");
	}

	[Fact]
	public void OptInName_IsRequestUserInput()
	{
		RequestUserInputTool.OptInName.Should().Be("request_user_input");
	}

	[Fact]
	public void Description_IsNotEmpty()
	{
		var tool = new RequestUserInputTool();

		tool.Description.Should().NotBeNullOrWhiteSpace();
	}

	[Fact]
	public void ParametersSchema_IsValidJson()
	{
		var tool = new RequestUserInputTool();

		var act = () => System.Text.Json.JsonDocument.Parse(tool.ParametersSchema);

		act.Should().NotThrow();
	}

	[Fact]
	public async Task ExecuteAsync_ReturnsResolvedReply_WhenWaiterCompletes()
	{
		var tool = new RequestUserInputTool();
		var waiter = new StubWaiter();
		var store = new InMemoryStore();
		var context = new EngineToolContext
		{
			OrchestrationName = "test-orch",
			RunId = "run-123",
			StepName = "writer",
			HumanInputWaiter = waiter,
			PendingInputStore = store,
		};

		// Schedule a response after the wait registers.
		var responseTask = Task.Run(async () =>
		{
			await waiter.WaitForRegistration();
			waiter.Complete(new UserInputResponse
			{
				Reply = "use the friendly tone",
				RespondedAt = DateTimeOffset.UtcNow,
			});
		});

		var result = await tool.ExecuteAsync("""{"prompt": "What tone should I use?"}""", context, CancellationToken.None);

		await responseTask;
		result.Should().Be("use the friendly tone");
	}

	[Fact]
	public async Task ExecuteAsync_PrefersChoice_WhenReplyIsNull()
	{
		var tool = new RequestUserInputTool();
		var waiter = new StubWaiter();
		var context = new EngineToolContext
		{
			OrchestrationName = "test",
			RunId = "r1",
			StepName = "s1",
			HumanInputWaiter = waiter,
			PendingInputStore = new InMemoryStore(),
		};

		_ = Task.Run(async () =>
		{
			await waiter.WaitForRegistration();
			waiter.Complete(new UserInputResponse
			{
				Choice = "approve",
				RespondedAt = DateTimeOffset.UtcNow,
			});
		});

		var result = await tool.ExecuteAsync("""{"prompt": "OK?", "choices": ["approve", "reject"]}""", context, CancellationToken.None);

		result.Should().Be("approve");
	}

	[Fact]
	public async Task ExecuteAsync_PersistsRecord_ThenDeletesOnResponse()
	{
		var tool = new RequestUserInputTool();
		var waiter = new StubWaiter();
		var store = new InMemoryStore();
		var context = new EngineToolContext
		{
			OrchestrationName = "orch",
			RunId = "run",
			StepName = "step",
			HumanInputWaiter = waiter,
			PendingInputStore = store,
		};

		_ = Task.Run(async () =>
		{
			await waiter.WaitForRegistration();
			// Verify record was persisted before resolving the wait.
			var saved = store.GetAll();
			saved.Should().ContainSingle()
				.Which.Kind.Should().Be(PendingInputKind.EngineTool);
			waiter.Complete(new UserInputResponse { Reply = "go", RespondedAt = DateTimeOffset.UtcNow });
		});

		await tool.ExecuteAsync("""{"prompt": "Do it?"}""", context, CancellationToken.None);

		// After resolution the record is cleaned up.
		store.GetAll().Should().BeEmpty();
	}

	[Fact]
	public async Task ExecuteAsync_FiresOnAwaitingInputCallback()
	{
		var tool = new RequestUserInputTool();
		var waiter = new StubWaiter();
		PendingInputRecord? captured = null;
		var context = new EngineToolContext
		{
			OrchestrationName = "orch",
			RunId = "run",
			StepName = "step",
			HumanInputWaiter = waiter,
			PendingInputStore = new InMemoryStore(),
			OnAwaitingInput = r => captured = r,
		};

		_ = Task.Run(async () =>
		{
			await waiter.WaitForRegistration();
			waiter.Complete(new UserInputResponse { Reply = "ok", RespondedAt = DateTimeOffset.UtcNow });
		});

		await tool.ExecuteAsync("""{"prompt": "Confirm?"}""", context, CancellationToken.None);

		captured.Should().NotBeNull();
		captured!.Prompt.Should().Be("Confirm?");
		captured.Kind.Should().Be(PendingInputKind.EngineTool);
	}

	[Fact]
	public async Task ExecuteAsync_ReturnsErrorMessage_WhenPromptMissing()
	{
		var tool = new RequestUserInputTool();
		var context = new EngineToolContext
		{
			OrchestrationName = "orch",
			RunId = "run",
			StepName = "step",
			HumanInputWaiter = new StubWaiter(),
			PendingInputStore = new InMemoryStore(),
		};

		var result = await tool.ExecuteAsync("""{}""", context, CancellationToken.None);

		result.Should().Contain("Missing 'prompt'");
	}

	[Fact]
	public async Task ExecuteAsync_ReturnsErrorMessage_WhenInvalidJson()
	{
		var tool = new RequestUserInputTool();
		var context = new EngineToolContext
		{
			OrchestrationName = "orch",
			RunId = "run",
			StepName = "step",
			HumanInputWaiter = new StubWaiter(),
			PendingInputStore = new InMemoryStore(),
		};

		var result = await tool.ExecuteAsync("not json", context, CancellationToken.None);

		result.Should().Contain("Invalid arguments");
	}

	[Fact]
	public async Task ExecuteAsync_ReturnsUnavailableMessage_WhenContextLacksRunIdentity()
	{
		var tool = new RequestUserInputTool();
		var context = new EngineToolContext
		{
			OrchestrationName = null,
			RunId = null,
			StepName = "step",
			HumanInputWaiter = new StubWaiter(),
		};

		var result = await tool.ExecuteAsync("""{"prompt": "Hi?"}""", context, CancellationToken.None);

		result.Should().Contain("not available");
	}

	private sealed class StubWaiter : IHumanInputWaiter
	{
		private TaskCompletionSource<UserInputResponse>? _pending;
		private readonly TaskCompletionSource _registered = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public Task WaitForRegistration() => _registered.Task;

		public void Complete(UserInputResponse response)
		{
			_pending?.TrySetResult(response);
		}

		public Task<UserInputResponse> WaitAsync(string orchestrationName, string runId, string stepName, CancellationToken cancellationToken)
		{
			_pending = new TaskCompletionSource<UserInputResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
			cancellationToken.Register(() => _pending.TrySetCanceled(cancellationToken));
			_registered.TrySetResult();
			return _pending.Task;
		}

		public bool TryComplete(string orchestrationName, string runId, string stepName, UserInputResponse response)
		{
			Complete(response);
			return true;
		}

		public bool TryCancel(string orchestrationName, string runId, string stepName) => false;
		public void BeginWait(string runId, string stepName) { }
		public void EndWait(string runId, string stepName) { }
	}

	private sealed class InMemoryStore : IPendingInputStore
	{
		private readonly Dictionary<string, PendingInputRecord> _records = new();

		public IReadOnlyList<PendingInputRecord> GetAll() => _records.Values.ToList();

		private static string Key(string o, string r, string s) => $"{o}|{r}|{s}";

		public Task SaveAsync(PendingInputRecord record, CancellationToken cancellationToken = default)
		{
			_records[Key(record.OrchestrationName, record.RunId, record.StepName)] = record;
			return Task.CompletedTask;
		}

		public Task<PendingInputRecord?> GetAsync(string orchestrationName, string runId, string stepName, CancellationToken cancellationToken = default)
		{
			_records.TryGetValue(Key(orchestrationName, runId, stepName), out var record);
			return Task.FromResult(record);
		}

		public Task<IReadOnlyList<PendingInputRecord>> ListAsync(string? orchestrationName = null, CancellationToken cancellationToken = default)
		{
			IReadOnlyList<PendingInputRecord> all = orchestrationName is null
				? _records.Values.ToList()
				: _records.Values.Where(r => r.OrchestrationName == orchestrationName).ToList();
			return Task.FromResult(all);
		}

		public Task DeleteAsync(string orchestrationName, string runId, string stepName, CancellationToken cancellationToken = default)
		{
			_records.Remove(Key(orchestrationName, runId, stepName));
			return Task.CompletedTask;
		}
	}
}
