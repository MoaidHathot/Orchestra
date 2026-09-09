using System.Reflection;
using NSubstitute;
using Orchestra.Engine;

namespace Orchestra.Engine.Tests.TestHelpers;

/// <summary>
/// Wraps an <see cref="IOrchestrationReporter"/> so every call is serialized.
/// </summary>
/// <remarks>
/// <para>
/// Test reporters are NSubstitute substitutes, and NSubstitute's received-call recording is not
/// thread-safe. The executor reports step lifecycle events from the worker threads it uses to
/// run independent DAG branches, so an orchestration with parallel steps has several threads
/// calling into the same substitute at once. A concurrent record can be lost, and then a
/// <c>Received()</c> assertion fails for a step that really did run — intermittently, under
/// load, with nothing wrong in the product. That reads as CI flakiness and costs far more to
/// diagnose than it does to prevent.
/// </para>
/// <para>
/// <see cref="DispatchProxy"/> forwards every member reflectively, so this stays correct as
/// <see cref="IOrchestrationReporter"/> grows. A hand-written decorator would silently stop
/// forwarding any member added later — the interface has default implementations, so the
/// compiler would not complain.
/// </para>
/// </remarks>
public class SerializingReporterProxy : DispatchProxy
{
	private IOrchestrationReporter _inner = null!;
	private Lock _gate = null!;

	/// <summary>
	/// Creates a serialized reporter backed by a fresh NSubstitute substitute. Pass the result
	/// to the code under test and use <see cref="Recorded"/> for assertions.
	/// </summary>
	public static IOrchestrationReporter CreateRecording()
		=> Wrap(Substitute.For<IOrchestrationReporter>());

	/// <summary>
	/// Returns the recording substitute behind a proxy from <see cref="CreateRecording"/>, so
	/// <c>Received()</c> / <c>DidNotReceive()</c> can be asserted against it.
	/// </summary>
	/// <remarks>
	/// Asserting on the proxy itself would not work: NSubstitute's extension methods need the
	/// substitute, and the proxy is an ordinary object as far as it is concerned. Passing a
	/// plain substitute through is harmless, so callers do not have to know which they hold.
	/// </remarks>
	public static IOrchestrationReporter Recorded(IOrchestrationReporter reporter)
	{
		ArgumentNullException.ThrowIfNull(reporter);

		return reporter is SerializingReporterProxy proxy ? proxy._inner : reporter;
	}

	/// <summary>
	/// Returns a reporter that forwards to <paramref name="inner"/> under a lock. Assertions
	/// must be made against <paramref name="inner"/>, not the returned proxy.
	/// </summary>
	public static IOrchestrationReporter Wrap(IOrchestrationReporter inner)
	{
		ArgumentNullException.ThrowIfNull(inner);

		var proxy = Create<IOrchestrationReporter, SerializingReporterProxy>();
		var self = (SerializingReporterProxy)(object)proxy;
		self._inner = inner;
		self._gate = new Lock();
		return proxy;
	}

	protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
	{
		ArgumentNullException.ThrowIfNull(targetMethod);

		lock (_gate)
		{
			try
			{
				return targetMethod.Invoke(_inner, args);
			}
			catch (TargetInvocationException ex) when (ex.InnerException is not null)
			{
				// Surface what the reporter actually threw; a test asserting on reporter
				// failure should not have to unwrap reflection plumbing.
				throw ex.InnerException;
			}
		}
	}
}
