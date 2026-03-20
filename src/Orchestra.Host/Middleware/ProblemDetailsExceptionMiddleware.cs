using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Orchestra.Host.Middleware;

/// <summary>
/// Middleware that catches unhandled exceptions and returns RFC 7807 Problem Details responses.
/// </summary>
public sealed partial class ProblemDetailsExceptionMiddleware
{
	private readonly RequestDelegate _next;
	private readonly ILogger<ProblemDetailsExceptionMiddleware> _logger;

	public ProblemDetailsExceptionMiddleware(RequestDelegate next, ILogger<ProblemDetailsExceptionMiddleware> logger)
	{
		_next = next;
		_logger = logger;
	}

	public async Task InvokeAsync(HttpContext context)
	{
		try
		{
			await _next(context);
		}
		catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
		{
			// Client disconnected — don't log as error, just set status
			context.Response.StatusCode = StatusCodes.Status499ClientClosedRequest;
		}
		catch (Exception ex)
		{
			LogUnhandledException(ex, context.Request.Method, context.Request.Path);

			if (!context.Response.HasStarted)
			{
				context.Response.StatusCode = StatusCodes.Status500InternalServerError;

				var problem = new ProblemDetails
				{
					Status = StatusCodes.Status500InternalServerError,
					Title = "An unexpected error occurred.",
					Detail = ex.Message,
					Type = "https://tools.ietf.org/html/rfc7807",
					Instance = context.Request.Path,
				};

				// WriteAsJsonAsync would override ContentType, so serialize manually
				context.Response.ContentType = "application/problem+json; charset=utf-8";
				var json = System.Text.Json.JsonSerializer.Serialize(problem);
				await context.Response.WriteAsync(json, context.RequestAborted);
			}
		}
	}

	[LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception processing {Method} {Path}")]
	private partial void LogUnhandledException(Exception ex, string method, string path);
}
