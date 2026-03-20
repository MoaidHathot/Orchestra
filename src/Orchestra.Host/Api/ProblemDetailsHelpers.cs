using Microsoft.AspNetCore.Http;

namespace Orchestra.Host.Api;

/// <summary>
/// Helper methods for creating RFC 7807 Problem Details responses.
/// </summary>
public static class ProblemDetailsHelpers
{
	/// <summary>
	/// Creates a 404 Not Found Problem Details result.
	/// </summary>
	public static IResult NotFound(string detail, string? instance = null)
		=> Results.Problem(
			statusCode: StatusCodes.Status404NotFound,
			title: "Not Found",
			detail: detail,
			instance: instance);

	/// <summary>
	/// Creates a 400 Bad Request Problem Details result.
	/// </summary>
	public static IResult BadRequest(string detail, string? instance = null)
		=> Results.Problem(
			statusCode: StatusCodes.Status400BadRequest,
			title: "Bad Request",
			detail: detail,
			instance: instance);

	/// <summary>
	/// Creates a 500 Internal Server Error Problem Details result.
	/// </summary>
	public static IResult InternalServerError(string detail, string? instance = null)
		=> Results.Problem(
			statusCode: StatusCodes.Status500InternalServerError,
			title: "Internal Server Error",
			detail: detail,
			instance: instance);

	/// <summary>
	/// Creates a 503 Service Unavailable Problem Details result.
	/// </summary>
	public static IResult ServiceUnavailable(string detail, string? instance = null)
		=> Results.Problem(
			statusCode: StatusCodes.Status503ServiceUnavailable,
			title: "Service Unavailable",
			detail: detail,
			instance: instance);
}
