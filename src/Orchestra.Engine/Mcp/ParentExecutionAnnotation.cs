namespace Orchestra.Engine;

/// <summary>
/// Identifies the orchestration run and step that owns an outbound MCP connection. Passed
/// from <see cref="OrchestrationExecutor"/> via <see cref="IMcpResolver.Resolve(Mcp[], ParentExecutionAnnotation?)"/>
/// so that <see cref="RemoteMcp"/> entries pointing at Orchestra's own data-plane endpoint can carry
/// HTTP headers that let server-side MCP tool handlers identify their caller.
/// </summary>
/// <remarks>
/// This is what makes <c>invoke_orchestration</c>'s <c>parentExecutionId</c> work
/// "automatically when called from within an orchestration": the engine stamps the parent's
/// run/step on the outbound HTTP request, and the data-plane tool handler reads them back.
/// Header names are defined on <see cref="OrchestraHeaders"/>.
/// </remarks>
public sealed class ParentExecutionAnnotation
{
	/// <summary>
	/// Run ID of the orchestration making the outbound MCP call.
	/// </summary>
	public required string ExecutionId { get; init; }

	/// <summary>
	/// Name of the orchestration making the outbound MCP call.
	/// </summary>
	public required string OrchestrationName { get; init; }

	/// <summary>
	/// Name of the step inside the orchestration whose agent owns the connection.
	/// </summary>
	public required string StepName { get; init; }
}

/// <summary>
/// Canonical names for HTTP headers Orchestra adds to outbound MCP connections that target
/// its own server endpoints. Keeping these in one place avoids string-literal drift between
/// the engine (which writes them) and host-side MCP tool handlers (which read them).
/// </summary>
public static class OrchestraHeaders
{
	/// <summary>Run ID of the orchestration making the call. Maps to <see cref="ParentExecutionAnnotation.ExecutionId"/>.</summary>
	public const string ParentExecutionId = "X-Orchestra-Parent-Execution-Id";

	/// <summary>Name of the orchestration making the call. Maps to <see cref="ParentExecutionAnnotation.OrchestrationName"/>.</summary>
	public const string ParentOrchestrationName = "X-Orchestra-Parent-Orchestration-Name";

	/// <summary>Name of the step whose agent opened the connection. Maps to <see cref="ParentExecutionAnnotation.StepName"/>.</summary>
	public const string ParentStepName = "X-Orchestra-Parent-Step-Name";
}
