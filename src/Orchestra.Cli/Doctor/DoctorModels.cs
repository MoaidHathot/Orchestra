namespace Orchestra.Cli.Doctor;

/// <summary>Outcome of a single prerequisite check.</summary>
public enum DoctorStatus
{
	/// <summary>The prerequisite is satisfied.</summary>
	Ok,

	/// <summary>Usable, but something is worth knowing before the first run.</summary>
	Warning,

	/// <summary>A run will fail until this is fixed.</summary>
	Failed,

	/// <summary>Not applicable, or deliberately not run (for example, a network check while offline).</summary>
	Skipped,
}

/// <summary>
/// One prerequisite check and what to do about it.
/// </summary>
/// <param name="Name">Short stable identifier, also used as the JSON key.</param>
/// <param name="Status">Whether the check passed.</param>
/// <param name="Detail">What was found — a path, URL, version, or error text.</param>
/// <param name="Remedy">Concrete next action when the check did not pass.</param>
public sealed record DoctorCheck(string Name, DoctorStatus Status, string Detail, string? Remedy = null);

/// <summary>
/// Aggregate result of <c>orchestra doctor</c>.
/// </summary>
public sealed record DoctorReport(IReadOnlyList<DoctorCheck> Checks)
{
	/// <summary>True when at least one check will block a run.</summary>
	public bool HasFailures => Checks.Any(c => c.Status is DoctorStatus.Failed);

	/// <summary>True when at least one check produced a non-blocking warning.</summary>
	public bool HasWarnings => Checks.Any(c => c.Status is DoctorStatus.Warning);
}

/// <summary>
/// Inputs for a diagnostic run.
/// </summary>
/// <param name="Provider">Restrict provider checks to <c>copilot</c> or <c>opencode</c>; null checks both.</param>
/// <param name="Fix">Download the Copilot CLI when it is missing instead of only reporting it.</param>
/// <param name="Offline">Skip every check that would touch the network.</param>
/// <param name="ServerUrl">Explicit server URL to probe; null resolves the configured one.</param>
/// <param name="StartDirectory">Directory config discovery walks up from; null uses the working directory.</param>
public sealed record DoctorOptions(
	string? Provider = null,
	bool Fix = false,
	bool Offline = false,
	string? ServerUrl = null,
	string? StartDirectory = null);
