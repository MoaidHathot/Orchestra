using Microsoft.AspNetCore.Builder;
using Orchestra.Host.Middleware;

namespace Orchestra.Host.Extensions;

/// <summary>
/// Extension methods for configuring the Orchestra Host middleware pipeline.
/// </summary>
public static class ApplicationBuilderExtensions
{
	/// <summary>
	/// Adds the Orchestra Host global exception handler middleware that returns RFC 7807 Problem Details
	/// for any unhandled exceptions. Should be registered early in the middleware pipeline.
	/// </summary>
	public static IApplicationBuilder UseOrchestraHostProblemDetails(this IApplicationBuilder app)
	{
		return app.UseMiddleware<ProblemDetailsExceptionMiddleware>();
	}
}
