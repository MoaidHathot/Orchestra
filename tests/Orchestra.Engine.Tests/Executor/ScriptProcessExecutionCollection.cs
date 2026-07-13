using Xunit;

namespace Orchestra.Engine.Tests.Executor;

/// <summary>
/// Serializes the process-spawning script-executor integration tests — they launch real
/// <c>pwsh</c> child processes — so they do not run concurrently (adding CPU/process pressure)
/// with the timing-sensitive mock-agent scheduler tests in this assembly. Keeps per-run
/// contention bounded so a busy CI host cannot tip a scheduler test into a spurious timeout.
/// </summary>
[CollectionDefinition("ScriptProcessExecution", DisableParallelization = true)]
public sealed class ScriptProcessExecutionCollection
{
	public const string Name = "ScriptProcessExecution";
}
