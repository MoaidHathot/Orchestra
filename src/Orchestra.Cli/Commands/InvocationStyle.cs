using System.Text.Json;

namespace Orchestra.Cli.Commands;

/// <summary>
/// Detects how the tool was launched so "next step" hints print a command the user can
/// actually paste back.
/// </summary>
/// <remarks>
/// <para>
/// Three install styles produce three different command prefixes:
/// </para>
/// <list type="bullet">
///   <item><c>orchestra</c> — a global tool, resolved under the dotnet tool store.</item>
///   <item><c>dotnet orchestra</c> — a local tool declared in a <c>dotnet-tools.json</c> manifest.</item>
///   <item><c>dnx Orchestra --yes --</c> — run straight out of the NuGet package cache without installing.</item>
/// </list>
/// <para>
/// Local tools and <c>dnx</c> both execute from the package cache, so the path alone cannot tell
/// them apart. The manifest is the tie-breaker: if one up the tree lists this tool, the user
/// installed it locally and <c>dotnet orchestra</c> is the form that works for them.
/// </para>
/// </remarks>
internal static class InvocationStyle
{
	internal const string GlobalPrefix = "orchestra";
	internal const string LocalToolPrefix = "dotnet orchestra";
	internal const string DnxPrefix = "dnx Orchestra --yes --";

	/// <summary>Command prefix for user-facing hints.</summary>
	internal static string CommandPrefix { get; } = Detect(AppContext.BaseDirectory, Directory.GetCurrentDirectory());

	/// <summary>Formats <paramref name="commandLine"/> with the detected launcher prefix.</summary>
	internal static string Format(string commandLine) => $"{CommandPrefix} {commandLine}";

	/// <summary>
	/// Classifies an install from the executable's base directory and, for the package-cache
	/// case, the working directory to search for a tool manifest.
	/// </summary>
	internal static string Detect(string baseDirectory, string? workingDirectory = null)
	{
		if (string.IsNullOrWhiteSpace(baseDirectory))
			return GlobalPrefix;

		var normalized = baseDirectory.Replace('\\', '/');

		// A global install always lands under the tool store, even when the user has
		// relocated DOTNET_TOOLS_PATH — check it first so it wins over any cache-shaped path.
		if (normalized.Contains("/.store/", StringComparison.OrdinalIgnoreCase))
			return GlobalPrefix;

		var fromPackageCache = normalized.Contains("/.nuget/packages/", StringComparison.OrdinalIgnoreCase)
			|| normalized.Contains("/packages/orchestra/", StringComparison.OrdinalIgnoreCase);

		if (!fromPackageCache)
			return GlobalPrefix;

		return workingDirectory is not null && HasLocalToolManifest(workingDirectory)
			? LocalToolPrefix
			: DnxPrefix;
	}

	/// <summary>
	/// True when a <c>dotnet-tools.json</c> (at the root or under <c>.config/</c>) that declares
	/// this tool exists in <paramref name="startDirectory"/> or any parent.
	/// </summary>
	/// <remarks>
	/// Mirrors how the SDK itself locates a manifest. Any I/O or parse problem is treated as
	/// "no manifest": this only decides the wording of a hint, so it must never throw.
	/// </remarks>
	internal static bool HasLocalToolManifest(string startDirectory)
	{
		try
		{
			for (var dir = new DirectoryInfo(startDirectory); dir is not null; dir = dir.Parent)
			{
				foreach (var candidate in new[]
				{
					Path.Combine(dir.FullName, "dotnet-tools.json"),
					Path.Combine(dir.FullName, ".config", "dotnet-tools.json"),
				})
				{
					if (File.Exists(candidate) && ManifestDeclaresOrchestra(candidate))
						return true;
				}
			}
		}
		catch
		{
			// Fall through: wording only.
		}

		return false;
	}

	private static bool ManifestDeclaresOrchestra(string path)
	{
		try
		{
			using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
			{
				CommentHandling = JsonCommentHandling.Skip,
				AllowTrailingCommas = true,
			});

			if (!doc.RootElement.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Object)
				return false;

			foreach (var tool in tools.EnumerateObject())
			{
				if (string.Equals(tool.Name, "orchestra", StringComparison.OrdinalIgnoreCase))
					return true;
			}
		}
		catch
		{
			// Unreadable or malformed manifest: not ours to diagnose here.
		}

		return false;
	}
}
