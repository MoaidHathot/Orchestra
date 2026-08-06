namespace Orchestra.Host.Persistence;

/// <summary>
/// Filter criteria for a paged run-history query, evaluated entirely in SQL.
/// </summary>
/// <remarks>
/// <para>
/// Every property is optional; <see langword="null"/> means "no constraint on this dimension".
/// Non-null values combine with <b>AND</b>, except <see cref="AlsoMatchRunIds"/>, which widens
/// the <see cref="NameOrIdContains"/> text match with <b>OR</b>.
/// </para>
/// <para>
/// <b>Why annotations arrive as run-id sets.</b> Favorites, tags, titles and notes are user data
/// and live on disk under <c>annotations/</c>, deliberately outside the index — the index is
/// derived and deletable, and user data must never be either. Rather than mirroring annotations
/// into SQLite and taking on the job of keeping the copy honest, the caller resolves an
/// annotation filter against the in-memory annotation map (which is small and sparse, since only
/// runs a human acted on appear in it) and passes the resulting run ids down as a set. SQL then
/// does the joining and paging, and there is still exactly one copy of the user's data.
/// </para>
/// </remarks>
public sealed record RunIndexQuery
{
	/// <summary>Allow-list of origin wire values ("manual", "scheduler", "orchestration", ...).</summary>
	public IReadOnlyCollection<string>? Origins { get; init; }

	/// <summary>
	/// <see langword="true"/> = only runs without a parent; <see langword="false"/> = only runs
	/// with one; <see langword="null"/> = no scope constraint.
	/// </summary>
	public bool? RootsOnly { get; init; }

	/// <summary>Allow-list of <c>ExecutionStatus</c> names, matched case-insensitively.</summary>
	public IReadOnlyCollection<string>? Statuses { get; init; }

	/// <summary>
	/// Run ids the result is restricted to (AND). Set by annotation-backed filters such as
	/// <c>?favorites=true</c> or <c>?tags=connect</c>. An empty set matches nothing, which is
	/// the correct answer when a filter selected no runs.
	/// </summary>
	public IReadOnlyCollection<string>? RunIdAllowList { get; init; }

	/// <summary>
	/// Run ids excluded from the result (AND NOT). Used by <c>?favorites=false</c>, which asks
	/// for the complement of a set and so cannot be expressed as an allow-list.
	/// </summary>
	public IReadOnlyCollection<string>? RunIdDenyList { get; init; }

	/// <summary>
	/// Case-insensitive substring matched against the orchestration name and the run id.
	/// Wildcards are escaped, so a query of <c>50%</c> is a literal search.
	/// </summary>
	public string? NameOrIdContains { get; init; }

	/// <summary>
	/// Run ids that satisfy the text search by some means SQL cannot see — currently a match
	/// against the run's annotation title, note or tags. ORed into the
	/// <see cref="NameOrIdContains"/> match.
	/// </summary>
	public IReadOnlyCollection<string>? AlsoMatchRunIds { get; init; }

	/// <summary>
	/// An FTS5 <c>MATCH</c> expression run against the indexed run output, ORed into the same
	/// text-match group as <see cref="NameOrIdContains"/>. Build it with
	/// <see cref="ToFtsQuery"/> rather than passing user input directly.
	/// </summary>
	public string? ContentMatch { get; init; }

	/// <summary>
	/// Converts free text into an FTS5 <c>MATCH</c> expression, or <see langword="null"/> when it
	/// contains nothing searchable.
	/// </summary>
	/// <remarks>
	/// <para>
	/// User input cannot reach <c>MATCH</c> unescaped: FTS5 has its own query grammar, so a bare
	/// <c>AND</c>, an unbalanced quote or a stray <c>*</c> is at best a surprise and at worst a
	/// syntax error thrown at the user. Each word is therefore quoted into a literal phrase and
	/// the phrases are ANDed, giving "runs whose output contains all of these words".
	/// </para>
	/// <para>
	/// Words are matched as prefixes, so typing <c>conn</c> finds <c>connect</c>. That keeps the
	/// content search close to the substring behaviour of the name and id search beside it,
	/// though FTS5 indexes whole tokens and so cannot match inside a word.
	/// </para>
	/// <para>
	/// Terms with no letter or digit are dropped. They tokenize to nothing, which FTS5 rejects as
	/// a syntax error, and a search for <c>%</c> should return no content matches rather than
	/// failing the request.
	/// </para>
	/// </remarks>
	public static string? ToFtsQuery(string? text)
	{
		if (string.IsNullOrWhiteSpace(text))
			return null;

		var phrases = new List<string>();
		foreach (var term in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			if (!term.Any(char.IsLetterOrDigit))
				continue;

			// Inside a phrase everything is literal except the quote, which doubles to escape.
			phrases.Add($"\"{term.Replace("\"", "\"\"", StringComparison.Ordinal)}\"*");
		}

		return phrases.Count == 0 ? null : string.Join(" AND ", phrases);
	}
}
