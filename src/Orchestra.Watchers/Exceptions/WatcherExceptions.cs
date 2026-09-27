namespace Orchestra.Watchers;

/// <summary>
/// A watcher subscription could not be established: unknown library reference, inline/library
/// name collision, disabled watcher, or an invalid inline definition. Registration of the trigger
/// fails loudly; the host stays up.
/// </summary>
public sealed class WatcherInitializationException : Exception
{
	public WatcherInitializationException(string message) : base(message)
	{
	}

	public WatcherInitializationException(string message, Exception innerException) : base(message, innerException)
	{
	}
}

/// <summary>
/// A watcher definition or a persisted state file is structurally invalid. <see cref="Path"/>
/// points at the offending JSON location (e.g. <c>watchers.github-pr-comments.schedule</c>).
/// </summary>
public sealed class WatcherValidationException : Exception
{
	public WatcherValidationException(string path, string message) : base($"{path}: {message}")
	{
		Path = path;
		Detail = message;
	}

	public WatcherValidationException(string path, string message, Exception innerException) : base($"{path}: {message}", innerException)
	{
		Path = path;
		Detail = message;
	}

	/// <summary>Dotted location of the invalid element.</summary>
	public string Path { get; }

	/// <summary>The message without the path prefix.</summary>
	public string Detail { get; }
}
