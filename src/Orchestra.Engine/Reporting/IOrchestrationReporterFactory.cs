namespace Orchestra.Engine;

/// <summary>
/// Factory for creating <see cref="IOrchestrationReporter"/> instances.
/// Register in DI so that all execution paths use the same reporter type.
/// The default implementation creates <c>NullOrchestrationReporter</c> instances;
/// the Host layer overrides this with an <c>SseReporter</c>-based factory.
/// </summary>
public interface IOrchestrationReporterFactory
{
	/// <summary>
	/// Creates a new reporter for an orchestration execution.
	/// Each execution should get its own reporter instance.
	/// </summary>
	IOrchestrationReporter Create();
}

/// <summary>
/// Default factory that creates <see cref="NullOrchestrationReporter"/> instances.
/// Used in tests and when no Host-layer factory is registered.
/// </summary>
public class NullOrchestrationReporterFactory : IOrchestrationReporterFactory
{
	public static readonly NullOrchestrationReporterFactory Instance = new();

	public IOrchestrationReporter Create() => NullOrchestrationReporter.Instance;
}
