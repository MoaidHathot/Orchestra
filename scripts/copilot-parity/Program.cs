using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using GitHub.Copilot;
using Microsoft.Extensions.Logging;
using Orchestra.Copilot;
using Orchestra.Engine;

// This executable is only an experiment driver. The Orchestra arm uses the real
// parser, scheduler, PromptExecutor, provider, engine tools, and completion logic.
// It does not substitute a test agent or implement an agent loop.
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
	WriteIndented = true,
	Converters = { new JsonStringEnumConverter() },
};
var config = JsonSerializer.Deserialize<RunConfig>(await File.ReadAllTextAsync(args.Single()), json)!;
Directory.CreateDirectory(config.EvidenceDirectory);
Directory.SetCurrentDirectory(config.WorkingDirectory);
Environment.SetEnvironmentVariable("ORCHESTRA_COPILOT_CLI_PATH", config.CliPath);
using var loggerFactory = LoggerFactory.Create(b => b.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace).SetMinimumLevel(LogLevel.Warning));
var logger = loggerFactory.CreateLogger("CopilotParity");
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(config.TimeoutSeconds));
using var events = new JsonLines(Path.Combine(config.EvidenceDirectory, "driver-events.jsonl"));
var clock = Stopwatch.StartNew();
object result;
var exitCode = 0;
try
{
	result = config.Arm switch
	{
		"orchestra" => await RunOrchestraAsync(),
		"lifecycle-probe" => RunLifecycleProbe(),
		_ => await RunSdkAsync(),
	};
}
catch (Exception ex)
{
	ParityLog.RunFailed(logger, ex, config.Arm);
	result = new { status = "error", error = ex.ToString(), elapsedSeconds = clock.Elapsed.TotalSeconds };
	exitCode = 2;
}
await File.WriteAllTextAsync(Path.Combine(config.EvidenceDirectory, "driver-result.json"), JsonSerializer.Serialize(result, json));
return exitCode;

object RunLifecycleProbe()
{
	// This is explicitly a synthetic protocol/adapter probe, not an LLM run.
	// Reflection reaches the existing internal handler without changing production
	// visibility or copying its implementation into the experiment.
	var type = typeof(CopilotAgent).Assembly.GetType("Orchestra.Copilot.CopilotSessionHandler", throwOnError: true)!;
	var channel = Channel.CreateUnbounded<AgentEvent>();
	var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
	var constructor = type.GetConstructors().Single();
	object?[] handlerArguments = [channel.Writer, NullOrchestrationReporter.Instance, config.Model, done, null];
	// Preserve the standard-mode probe across optional handler controls added after the study.
	var optionalArguments = constructor.GetParameters().Skip(handlerArguments.Length)
		.Select(parameter => parameter.HasDefaultValue ? parameter.DefaultValue
			: throw new InvalidOperationException($"The lifecycle probe must supply '{parameter.Name}'."));
	var handler = constructor.Invoke([.. handlerArguments, .. optionalArguments]);
	var handle = type.GetMethod("HandleEvent")!;
	Guid? previousEventId = null;
	void Send(string kind, object data, string? agentId = null)
	{
		var eventId = Guid.NewGuid();
		var evt = SessionEvent.FromJson(JsonSerializer.Serialize(new
		{
			type = kind,
			data,
			agentId,
			id = eventId,
			parentId = previousEventId,
			timestamp = DateTimeOffset.UtcNow,
		}));
		previousEventId = eventId;
		events.Write(evt);
		handle.Invoke(handler, [evt]);
	}
	Send("subagent.started", new { toolCallId = "child-call", agentName = "child", agentDisplayName = "Child", agentDescription = "Protocol fixture" });
	Send("assistant.idle", new { aborted = false }, agentId: "child-agent");
	var completedWhileChildTracked = done.Task.IsCompleted;
	Send("subagent.completed", new { toolCallId = "child-call", agentName = "child", agentDisplayName = "Child" });
	Send("assistant.idle", new { aborted = false }, agentId: "child-agent");
	var completedAfterLateChildIdle = done.Task.IsCompleted;
	exitCode = completedWhileChildTracked || completedAfterLateChildIdle ? 1 : 0;
	var translated = new List<AgentEvent>();
	while (channel.Reader.TryRead(out var evt))
		translated.Add(evt);
	return new
	{
		status = "protocol-probed",
		synthetic = true,
		liveModelCalls = 0,
		completedWhileChildTracked,
		completedAfterLateChildIdle,
		expectedCompletedAfterChildIdle = false,
		defectReproduced = !completedWhileChildTracked && completedAfterLateChildIdle,
		translatedEvents = translated,
	};
}

async Task<object> RunOrchestraAsync()
{
	await using var builder = new CopilotAgentBuilder(loggerFactory);
	var reporter = DispatchProxy.Create<IOrchestrationReporter, EvidenceReporter>();
	((EvidenceReporter)reporter).Events = events;
	var orchestration = OrchestrationParser.ParseOrchestrationFile(config.OrchestrationFile!, []);
	var executor = new OrchestrationExecutor(new OrchestrationScheduler(), builder, reporter, loggerFactory);
	var outcome = await executor.ExecuteAsync(orchestration, cancellationToken: timeout.Token);
	await File.WriteAllTextAsync(Path.Combine(config.EvidenceDirectory, "orchestration-result.json"), JsonSerializer.Serialize(outcome, json));
	var step = outcome.StepResults["task"];
	if (outcome.Status != ExecutionStatus.Succeeded)
		exitCode = 1;
	return new
	{
		status = outcome.Status.ToString(),
		content = step.Content,
		elapsedSeconds = clock.Elapsed.TotalSeconds,
		step.ErrorMessage,
		step.ActualModel,
		outcome.IsIncomplete,
	};
}

async Task<object> RunSdkAsync()
{
	await using var client = new CopilotClient(new CopilotClientOptions
	{
		Connection = RuntimeConnection.ForStdio(config.CliPath),
		WorkingDirectory = config.WorkingDirectory,
		Logger = loggerFactory.CreateLogger<CopilotClient>(),
	});
	await client.StartAsync(timeout.Token);
	var runtime = await client.GetStatusAsync(timeout.Token);
	var auth = await client.GetAuthStatusAsync(timeout.Token);
	await File.WriteAllTextAsync(Path.Combine(config.EvidenceDirectory, "runtime.json"), JsonSerializer.Serialize(new
	{
		runtime,
		auth.IsAuthenticated,
		auth.AuthType,
		sdk = typeof(CopilotClient).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
		clientMode = new CopilotClientOptions().Mode.ToString(),
	}, json));
	if (config.Arm == "probe")
	{
		var models = await client.ListModelsAsync(timeout.Token);
		return new { status = "probed", runtime, auth.IsAuthenticated, models };
	}

	var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
	string? content = null;
	string? error = null;
	var sessionConfig = new SessionConfig
	{
		Model = config.Model,
		ReasoningEffort = config.ReasoningEffort,
		ContextTier = GitHub.Copilot.ContextTier.Default,
		WorkingDirectory = config.WorkingDirectory,
		Streaming = true,
		OnPermissionRequest = PermissionHandler.ApproveAll,
		EnableConfigDiscovery = config.EnableConfigDiscovery,
		EnableOnDemandInstructionDiscovery = config.EnableOnDemandInstructions,
		EnableSessionStore = config.EnableSessionStore,
		Memory = config.MemoryEnabled is { } memoryEnabled ? new MemoryConfiguration { Enabled = memoryEnabled } : null,
		OnEvent = evt =>
		{
			events.Write(evt);
			if (evt is AssistantMessageEvent message && evt.AgentId is null)
				content = message.Data.Content;
			if (evt is SessionErrorEvent failure)
			{
				error = failure.Data.Message;
				done.TrySetException(new InvalidOperationException(error));
			}
			if (evt is SessionIdleEvent && evt.AgentId is null)
				done.TrySetResult();
		},
	};
	if (config.SystemPrompt is not null)
		sessionConfig.SystemMessage = new SystemMessageConfig
		{
			Content = config.SystemPrompt,
			Mode = config.SystemPromptMode == "replace" ? SystemMessageMode.Replace : SystemMessageMode.Append,
		};
	if (config.SkillDirectory is not null)
		sessionConfig.SkillDirectories = [config.SkillDirectory];
	if (config.McpCommand is not null)
		sessionConfig.McpServers = new Dictionary<string, McpServerConfig>
		{
			["fixture"] = new McpStdioServerConfig
			{
				Command = config.McpCommand,
				Args = config.McpArgs ?? [],
				Tools = ["*"],
			},
		};
	await using var session = await client.CreateSessionAsync(sessionConfig, timeout.Token);
	await File.WriteAllTextAsync(Path.Combine(config.EvidenceDirectory, "session-id.txt"), session.SessionId);
	// These are diagnostic RPCs, not extra model calls. Experimental API usage is
	// confined to the experiment; failures are recorded rather than hidden.
#pragma warning disable GHCP001
	if (config.AgentContext is not null || config.RunningInInteractiveMode is not null)
		await session.Rpc.Options.UpdateAsync(agentContext: config.AgentContext, runningInInteractiveMode: config.RunningInInteractiveMode, cancellationToken: timeout.Token);
	await CaptureAsync("initial-mode", async () => await session.Rpc.Mode.GetAsync(timeout.Token));
	if (config.SessionMode is not null)
		await session.Rpc.Mode.SetAsync(new SessionMode(config.SessionMode), cancellationToken: timeout.Token);
	await CaptureAsync("mode", async () => await session.Rpc.Mode.GetAsync(timeout.Token));
	await CaptureAsync("model", async () => await session.Rpc.Model.GetCurrentAsync(timeout.Token));
	await CaptureAsync("skills", async () => await session.Rpc.Skills.ListAsync(cancellationToken: timeout.Token));
	if (config.Arm == "inspect")
	{
		if (config.SessionMode == "plan")
		{
			await session.Rpc.Plan.UpdateAsync("# SDK plan probe\n\n- [ ] Verify runtime-owned plan storage.\n", timeout.Token);
			await CaptureAsync("plan", async () => await session.Rpc.Plan.ReadAsync(timeout.Token));
		}
		return new { status = "inspected", session.SessionId, runtime };
	}
#pragma warning restore GHCP001
	try
	{
		await session.SendAsync(new MessageOptions { Prompt = config.Prompt }, timeout.Token);
		await done.Task.WaitAsync(timeout.Token);
	}
	catch (OperationCanceledException)
	{
		using var abortTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		await session.AbortAsync(abortTimeout.Token);
		throw;
	}
	return new { status = "completed", content, error, session.SessionId, elapsedSeconds = clock.Elapsed.TotalSeconds };
}

async Task CaptureAsync(string name, Func<Task<object>> action)
{
	object value;
	try
	{ value = await action(); }
	catch (Exception ex) { value = new { diagnosticError = ex.Message }; }
	await File.WriteAllTextAsync(Path.Combine(config.EvidenceDirectory, name + ".json"), JsonSerializer.Serialize(value, json));
}

internal sealed class RunConfig
{
	public required string Arm { get; init; }
	public required string CliPath { get; init; }
	public required string WorkingDirectory { get; init; }
	public required string EvidenceDirectory { get; init; }
	public string Model { get; init; } = "claude-opus-4.8";
	public string ReasoningEffort { get; init; } = "high";
	public string Prompt { get; init; } = "";
	public int TimeoutSeconds { get; init; } = 480;
	public string? OrchestrationFile { get; init; }
	public bool? EnableConfigDiscovery { get; init; }
	public bool? EnableOnDemandInstructions { get; init; }
	public bool? EnableSessionStore { get; init; }
	public bool? MemoryEnabled { get; init; }
	public string? AgentContext { get; init; }
	public bool? RunningInInteractiveMode { get; init; }
	public string? SessionMode { get; init; }
	public string? SkillDirectory { get; init; }
	public string? SystemPrompt { get; init; }
	public string? SystemPromptMode { get; init; }
	public string? McpCommand { get; init; }
	public List<string>? McpArgs { get; init; }
}

internal sealed class JsonLines(string path) : IDisposable
{
	private readonly StreamWriter _writer = new(path) { AutoFlush = true, NewLine = "\n" };
	private readonly Lock _gate = new();
	private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
	{
		Converters = { new JsonStringEnumConverter() },
	};

	public void Write(object value)
	{
		lock (_gate)
			_writer.WriteLine(value is SessionEvent evt ? evt.ToJson() : JsonSerializer.Serialize(value, value.GetType(), Options));
	}

	public void Dispose() => _writer.Dispose();
}

// Capture the real reporter contract, including actor-aware overloads, without
// maintaining a second implementation of every evolving reporter method.
public class EvidenceReporter : DispatchProxy
{
	internal JsonLines Events { get; set; } = null!;
	protected override object? Invoke(MethodInfo? method, object?[]? args)
	{
		Events.Write(new { method = method!.Name, args });
		return null;
	}
}

internal static partial class ParityLog
{
	[LoggerMessage(Level = LogLevel.Error, Message = "Parity arm {Arm} failed")]
	internal static partial void RunFailed(ILogger logger, Exception exception, string arm);
}
