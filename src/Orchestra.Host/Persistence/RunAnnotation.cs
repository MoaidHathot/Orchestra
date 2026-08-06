namespace Orchestra.Host.Persistence;

/// <summary>
/// User-curated metadata attached to a single orchestration run.
/// </summary>
/// <remarks>
/// <para>
/// Run records themselves are immutable: <c>OrchestrationRunRecord</c> is entirely
/// <c>init</c>-only and <c>IRunStore</c> exposes no update operation. Re-saving a record to
/// mutate it would also append a duplicate <c>RunIndex</c> entry. Annotations therefore live
/// out-of-band, in their own store, and are merged into API projections at read time — the
/// same shape as host-managed orchestration tags.
/// </para>
/// <para>
/// Annotations are <b>sparse</b>: a record exists only for runs a user has explicitly acted on.
/// An annotation that becomes empty is deleted rather than persisted as an empty shell.
/// </para>
/// </remarks>
public sealed class RunAnnotation
{
	/// <summary>Marks the run as a favorite. Favorites are exempt from retention deletion.</summary>
	public bool Favorite { get; init; }

	/// <summary>
	/// Human-readable name for the run. Machine-generated orchestration names (ephemeral and
	/// self-healing runs especially) carry no meaning; this is what makes a run findable later.
	/// </summary>
	public string? Title { get; init; }

	/// <summary>Free-form labels for grouping and bulk selection. Normalized to lower-case.</summary>
	public string[] Tags { get; init; } = [];

	/// <summary>Free-form note — caveats, findings, or why the run was kept.</summary>
	public string? Note { get; init; }

	/// <summary>
	/// Owning orchestration, denormalized so the annotation can be rendered and located
	/// without an index scan, and still displayed if the underlying run has been deleted.
	/// </summary>
	public string? OrchestrationName { get; init; }

	/// <summary>Timestamp of the most recent mutation.</summary>
	public DateTimeOffset AnnotatedAt { get; init; }

	/// <summary>
	/// <see langword="true"/> when the annotation carries no user content and should be deleted
	/// rather than persisted.
	/// </summary>
	[System.Text.Json.Serialization.JsonIgnore]
	public bool IsEmpty =>
		!Favorite
		&& Tags.Length == 0
		&& string.IsNullOrWhiteSpace(Title)
		&& string.IsNullOrWhiteSpace(Note);
}
