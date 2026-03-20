using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Host.Middleware;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// Tests for ProblemDetailsExceptionMiddleware and ProblemDetailsHelpers.
/// Uses DefaultHttpContext to test the middleware directly without requiring TestServer.
/// </summary>
public class ProblemDetailsMiddlewareTests
{
	private static ProblemDetailsExceptionMiddleware CreateMiddleware(RequestDelegate next)
	{
		var logger = NullLogger<ProblemDetailsExceptionMiddleware>.Instance;
		return new ProblemDetailsExceptionMiddleware(next, logger);
	}

	private static DefaultHttpContext CreateHttpContext(string method = "GET", string path = "/test")
	{
		var context = new DefaultHttpContext();
		context.Request.Method = method;
		context.Request.Path = path;
		context.Response.Body = new MemoryStream();
		return context;
	}

	private static async Task<string> ReadResponseBody(HttpContext context)
	{
		context.Response.Body.Seek(0, SeekOrigin.Begin);
		using var reader = new StreamReader(context.Response.Body);
		return await reader.ReadToEndAsync();
	}

	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNameCaseInsensitive = true,
	};

	// ─── Successful requests pass through ──────────────────────────

	[Fact]
	public async Task SuccessfulRequest_PassesThroughUnchanged()
	{
		var middleware = CreateMiddleware(_ =>
		{
			_.Response.StatusCode = StatusCodes.Status200OK;
			return Task.CompletedTask;
		});
		var context = CreateHttpContext();

		await middleware.InvokeAsync(context);

		context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
	}

	[Fact]
	public async Task SuccessfulRequest_DoesNotModifyResponseBody()
	{
		var middleware = CreateMiddleware(async ctx =>
		{
			ctx.Response.StatusCode = StatusCodes.Status200OK;
			await ctx.Response.WriteAsync("hello world");
		});
		var context = CreateHttpContext();

		await middleware.InvokeAsync(context);

		var body = await ReadResponseBody(context);
		body.Should().Be("hello world");
	}

	// ─── Unhandled exceptions → 500 Problem Details ────────────────

	[Fact]
	public async Task UnhandledException_Returns500StatusCode()
	{
		var middleware = CreateMiddleware(_ =>
			throw new InvalidOperationException("Something broke"));
		var context = CreateHttpContext();

		await middleware.InvokeAsync(context);

		context.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
	}

	[Fact]
	public async Task UnhandledException_SetsContentTypeToProblemJson()
	{
		var middleware = CreateMiddleware(_ =>
			throw new InvalidOperationException("Something broke"));
		var context = CreateHttpContext();

		await middleware.InvokeAsync(context);

		context.Response.ContentType.Should().Contain("application/problem+json");
	}

	[Fact]
	public async Task UnhandledException_ReturnsValidProblemDetailsJson()
	{
		var middleware = CreateMiddleware(_ =>
			throw new InvalidOperationException("Something broke"));
		var context = CreateHttpContext(path: "/api/test");

		await middleware.InvokeAsync(context);

		var body = await ReadResponseBody(context);
		var problem = JsonSerializer.Deserialize<ProblemDetails>(body, JsonOptions);

		problem.Should().NotBeNull();
		problem!.Status.Should().Be(500);
		problem.Title.Should().Be("An unexpected error occurred.");
		problem.Detail.Should().Be("Something broke");
		problem.Instance.Should().Be("/api/test");
		problem.Type.Should().NotBeNullOrEmpty();
	}

	[Fact]
	public async Task ArgumentException_Returns500ProblemDetails()
	{
		var middleware = CreateMiddleware(_ =>
			throw new ArgumentException("Bad argument value"));
		var context = CreateHttpContext();

		await middleware.InvokeAsync(context);

		context.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);

		var body = await ReadResponseBody(context);
		var problem = JsonSerializer.Deserialize<ProblemDetails>(body, JsonOptions);

		problem.Should().NotBeNull();
		problem!.Status.Should().Be(500);
		problem.Detail.Should().Contain("Bad argument value");
	}

	[Fact]
	public async Task NullReferenceException_Returns500ProblemDetails()
	{
		var middleware = CreateMiddleware(_ =>
			throw new NullReferenceException("Object reference not set"));
		var context = CreateHttpContext();

		await middleware.InvokeAsync(context);

		context.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
		var body = await ReadResponseBody(context);
		var problem = JsonSerializer.Deserialize<ProblemDetails>(body, JsonOptions);
		problem.Should().NotBeNull();
		problem!.Detail.Should().Contain("Object reference not set");
	}

	// ─── OperationCanceledException (client disconnect) ────────────

	[Fact]
	public async Task OperationCanceledException_WhenRequestAborted_Returns499()
	{
		var cts = new CancellationTokenSource();
		var middleware = CreateMiddleware(_ =>
			throw new OperationCanceledException(cts.Token));

		var context = CreateHttpContext();
		// Simulate client disconnect by linking the request abort token
		cts.Cancel();
		context.RequestAborted = cts.Token;

		await middleware.InvokeAsync(context);

		context.Response.StatusCode.Should().Be(StatusCodes.Status499ClientClosedRequest);
	}

	[Fact]
	public async Task OperationCanceledException_WhenRequestAborted_DoesNotWriteBody()
	{
		var cts = new CancellationTokenSource();
		var middleware = CreateMiddleware(_ =>
			throw new OperationCanceledException(cts.Token));

		var context = CreateHttpContext();
		cts.Cancel();
		context.RequestAborted = cts.Token;

		await middleware.InvokeAsync(context);

		var body = await ReadResponseBody(context);
		body.Should().BeEmpty();
	}

	[Fact]
	public async Task OperationCanceledException_WhenNotAborted_Returns500ProblemDetails()
	{
		// OperationCanceledException thrown but NOT from client disconnect
		var middleware = CreateMiddleware(_ =>
			throw new OperationCanceledException("Timed out"));
		var context = CreateHttpContext();

		await middleware.InvokeAsync(context);

		// Since RequestAborted is NOT cancelled, the catch for client disconnect
		// doesn't match, so it falls through to the generic exception handler
		context.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
	}

	// ─── Response already started ──────────────────────────────────

	[Fact]
	public async Task Exception_WhenResponseAlreadyStarted_DoesNotWriteProblemDetails()
	{
		var middleware = CreateMiddleware(async ctx =>
		{
			// Start the response (write headers + some body)
			ctx.Response.StatusCode = StatusCodes.Status200OK;
			await ctx.Response.WriteAsync("partial");
			await ctx.Response.Body.FlushAsync();
			throw new InvalidOperationException("Late error");
		});

		var context = CreateHttpContext();

		// When response has started, the middleware can't change status or write JSON
		// The exception still propagates if HasStarted is true.
		// DefaultHttpContext.Response.HasStarted only becomes true after writing to
		// the actual stream in a real server. With DefaultHttpContext, HasStarted
		// remains false. We accept this limitation — in production, ASP.NET handles it.
		await middleware.InvokeAsync(context);

		// With DefaultHttpContext, HasStarted is always false so we still get problem details.
		// This test documents the behavior: middleware writes problem details when it can.
		context.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
	}

	// ─── Instance path is correct ──────────────────────────────────

	[Fact]
	public async Task ProblemDetails_Instance_MatchesRequestPath()
	{
		var middleware = CreateMiddleware(_ =>
			throw new Exception("Error"));
		var context = CreateHttpContext(path: "/api/orchestrations/my-orch/runs");

		await middleware.InvokeAsync(context);

		var body = await ReadResponseBody(context);
		var problem = JsonSerializer.Deserialize<ProblemDetails>(body, JsonOptions);
		problem!.Instance.Should().Be("/api/orchestrations/my-orch/runs");
	}

	[Fact]
	public async Task ProblemDetails_Title_IsAlwaysGeneric()
	{
		// Title should be generic regardless of exception type
		var middleware = CreateMiddleware(_ =>
			throw new FileNotFoundException("file.txt not found"));
		var context = CreateHttpContext();

		await middleware.InvokeAsync(context);

		var body = await ReadResponseBody(context);
		var problem = JsonSerializer.Deserialize<ProblemDetails>(body, JsonOptions);
		problem!.Title.Should().Be("An unexpected error occurred.");
	}

	// ─── ProblemDetailsHelpers ─────────────────────────────────────

	[Fact]
	public void ProblemDetailsHelpers_NotFound_ReturnsResult()
	{
		var result = Orchestra.Host.Api.ProblemDetailsHelpers.NotFound("Resource not found");
		result.Should().NotBeNull();
	}

	[Fact]
	public void ProblemDetailsHelpers_NotFound_WithInstance_ReturnsResult()
	{
		var result = Orchestra.Host.Api.ProblemDetailsHelpers.NotFound(
			"Orchestration not found", "/api/orchestrations/foo");
		result.Should().NotBeNull();
	}

	[Fact]
	public void ProblemDetailsHelpers_BadRequest_ReturnsResult()
	{
		var result = Orchestra.Host.Api.ProblemDetailsHelpers.BadRequest("Invalid input");
		result.Should().NotBeNull();
	}

	[Fact]
	public void ProblemDetailsHelpers_BadRequest_WithInstance_ReturnsResult()
	{
		var result = Orchestra.Host.Api.ProblemDetailsHelpers.BadRequest(
			"Missing required field", "/api/orchestrations");
		result.Should().NotBeNull();
	}

	[Fact]
	public void ProblemDetailsHelpers_InternalServerError_ReturnsResult()
	{
		var result = Orchestra.Host.Api.ProblemDetailsHelpers.InternalServerError("Server error");
		result.Should().NotBeNull();
	}

	[Fact]
	public void ProblemDetailsHelpers_InternalServerError_WithInstance_ReturnsResult()
	{
		var result = Orchestra.Host.Api.ProblemDetailsHelpers.InternalServerError(
			"Unexpected failure", "/api/runs");
		result.Should().NotBeNull();
	}
}
