using Microsoft.Extensions.Configuration;

namespace Orchestra.Host.Hosting;

/// <summary>
/// Makes <c>orchestra.json</c>'s <c>urls</c> value bind Kestrel across every Orchestra web host.
/// </summary>
/// <remarks>
/// Historically only the Portal honoured <c>urls</c>; <c>Orchestra.Server</c> read its binding
/// solely from <c>appsettings.json</c>/<c>ASPNETCORE_URLS</c>, so an operator who set <c>urls</c>
/// in <c>orchestra.json</c> got a server listening somewhere else, while the CLI's
/// <see cref="OrchestraConfigFile.Urls"/>-derived client URL pointed at the address they
/// configured. Nothing errored — the CLI just could not reach the server.
/// </remarks>
public static class OrchestraUrlExtensions
{
	/// <summary>The configuration key ASP.NET Core reads for the Kestrel URL binding.</summary>
	public const string UrlsConfigKey = "Urls";

	/// <summary>
	/// Applies <c>orchestra.json</c>'s <c>urls</c> as the Kestrel binding, unless the caller has
	/// already specified one.
	/// </summary>
	/// <remarks>
	/// Deliberately the lowest-priority source: an explicit <c>--urls</c> argument,
	/// <c>ASPNETCORE_URLS</c>, or <c>DOTNET_URLS</c> all win, so this only fills the gap when
	/// nothing else has an opinion. No-op when <c>urls</c> is unset.
	/// </remarks>
	public static void ApplyOrchestraUrls(this IConfigurationManager configuration, OrchestraConfigFile? config)
	{
		ArgumentNullException.ThrowIfNull(configuration);

		if (string.IsNullOrWhiteSpace(config?.Urls))
			return;

		var hasExplicitUrls = !string.IsNullOrWhiteSpace(configuration[UrlsConfigKey])
			|| !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS"))
			|| !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOTNET_URLS"));

		if (hasExplicitUrls)
			return;

		configuration[UrlsConfigKey] = config.Urls;
	}
}
