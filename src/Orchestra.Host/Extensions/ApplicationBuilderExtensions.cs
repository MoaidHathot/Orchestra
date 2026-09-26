using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
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

	/// <summary>
	/// Registers the CORS policy every Orchestra host uses: only loopback origins
	/// (<c>localhost</c>, <c>127.0.0.1</c>, <c>::1</c>) on any port may call the API from a
	/// browser, with credentials. Local tools and the Vite dev server get in; a page served from
	/// any other host does not.
	/// </summary>
	public static IServiceCollection AddOrchestraHostCors(this IServiceCollection services)
	{
		services.AddCors(options =>
		{
			options.AddDefaultPolicy(policy =>
			{
				policy.SetIsOriginAllowed(origin =>
				{
					if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
						return false;
					return uri.Host is "localhost" or "127.0.0.1" or "::1" || uri.IsLoopback;
				})
				.AllowAnyMethod()
				.AllowAnyHeader()
				.AllowCredentials();
			});
		});

		return services;
	}

	/// <summary>
	/// Installs the middleware every Orchestra host must share, in the right order:
	/// RFC 7807 problem-details for unhandled exceptions, then the loopback CORS policy from
	/// <see cref="AddOrchestraHostCors"/>. Call it first in the pipeline, before static files
	/// and endpoint mapping, so API errors from any host (the standalone server, the CLI's
	/// embedded portal, tests) have the same shape.
	/// </summary>
	public static IApplicationBuilder UseOrchestraHostPipeline(this IApplicationBuilder app)
	{
		app.UseOrchestraHostProblemDetails();
		app.UseCors();
		return app;
	}
}
