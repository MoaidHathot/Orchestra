using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Orchestra.Host.Hosting;

namespace Orchestra.Host.Logging;

/// <summary>
/// Helpers that make <c>orchestra.json</c>'s <see cref="OrchestraConfigFile.LogLevel"/> the
/// authoritative minimum log level across every Orchestra host (Portal, Server, Terminal).
/// </summary>
public static class OrchestraLogLevelExtensions
{
	/// <summary>
	/// The configuration key that the standard <c>Microsoft.Extensions.Logging</c> filter pipeline
	/// reads for the default minimum level. Writing here overrides any value an
	/// <c>appsettings.json</c> may define.
	/// </summary>
	public const string DefaultLogLevelConfigKey = "Logging:LogLevel:Default";

	/// <summary>
	/// Parses the <c>logLevel</c> string from <c>orchestra.json</c> into a <see cref="LogLevel"/>.
	/// Parsing is case-insensitive (so <c>"warning"</c> and <c>"Warning"</c> both work). When the
	/// value is missing or unrecognized, <paramref name="fallback"/> is returned.
	/// </summary>
	public static LogLevel ResolveLogLevel(this OrchestraConfigFile? config, LogLevel fallback = LogLevel.Information)
		=> Enum.TryParse<LogLevel>(config?.LogLevel, ignoreCase: true, out var level) ? level : fallback;

	/// <summary>
	/// Applies <c>orchestra.json</c>'s <c>logLevel</c> as the default minimum level for the
	/// configuration-driven logging pipeline by writing <see cref="DefaultLogLevelConfigKey"/>.
	/// </summary>
	/// <remarks>
	/// This is the reliable way to make <c>orchestra.json</c> authoritative for web hosts: calling
	/// <c>ILoggingBuilder.SetMinimumLevel</c> is shadowed whenever an <c>appsettings.json</c> defines
	/// <c>Logging:LogLevel:Default</c> (a category-wide rule wins over <c>MinLevel</c>). Writing the
	/// configuration key replaces that rule's value instead.
	/// <para>
	/// Per-category entries in <c>appsettings.json</c> (for example <c>Microsoft.AspNetCore: Warning</c>)
	/// are left untouched, so they continue to act as the framework-noise baseline.
	/// </para>
	/// <para>
	/// The method is a no-op when <c>logLevel</c> is unset or unparseable, leaving the existing
	/// <c>appsettings.json</c> default in place rather than clobbering it on a typo.
	/// </para>
	/// </remarks>
	public static void ApplyOrchestraLogLevel(this IConfigurationManager configuration, OrchestraConfigFile? config)
	{
		ArgumentNullException.ThrowIfNull(configuration);

		if (string.IsNullOrWhiteSpace(config?.LogLevel))
			return;

		// Only override when the value parses to a real level. A typo should fall back to the
		// appsettings default instead of silently dropping the level to Information.
		if (!Enum.TryParse<LogLevel>(config.LogLevel, ignoreCase: true, out var level))
			return;

		configuration[DefaultLogLevelConfigKey] = level.ToString();
	}
}
