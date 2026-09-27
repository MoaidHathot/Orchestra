namespace Orchestra.Watchers.Models;

/// <summary>
/// Name helpers shared by the state store and diagnostics.
/// </summary>
public static class WatcherNames
{
	/// <summary>
	/// Makes a user-supplied watcher name or orchestration id safe for file names. Identical to
	/// <c>OrchestrationRegistry.SanitizeId</c> in <c>Orchestra.Host</c> (lower-case, every
	/// non-alphanumeric character becomes <c>-</c>, leading/trailing dashes trimmed) so state files
	/// line up with the ids shown elsewhere; that helper is private to the host and the host depends
	/// on this assembly, hence the copy. An all-punctuation name sanitizes to <c>watcher</c>.
	/// </summary>
	public static string Sanitize(string name)
	{
		ArgumentNullException.ThrowIfNull(name);

		var sanitized = new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
		return sanitized.Length == 0 ? "watcher" : sanitized;
	}

	/// <summary>Separator between the watcher and orchestration segments of a per-subscriber state file.</summary>
	public const string SubscriberSeparator = "--";
}
