using System.Net;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Orchestra.Client.Tests;

/// <summary>
/// Verifies the body shape sent by <see cref="OrchestraClient.CancelExecutionAsync"/>.
/// The CLI must always POST a structured body so the Host can attribute the cancel to the
/// CLI client (vs. Portal/automation) and capture the optional caller reason. Legacy clients
/// sending no body are still accepted server-side, but new code paths should always identify
/// themselves via <c>source</c>.
/// </summary>
public sealed class OrchestraClientCancelTests
{
	[Fact]
	public async Task CancelExecutionAsync_DefaultArgs_SendsCliSourceAndNoReason()
	{
		// Arrange — capture what the client actually puts on the wire.
		HttpRequestMessage? captured = null;
		string? capturedBody = null;
		using var handler = new CapturingHandler(async req =>
		{
			captured = req;
			capturedBody = req.Content is null ? null : await req.Content.ReadAsStringAsync();
			return new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent("""{"cancelled":true,"executionId":"exec-1","status":"Cancelling"}""",
					System.Text.Encoding.UTF8, "application/json"),
			};
		});
		using var http = new HttpClient(handler) { BaseAddress = new Uri("http://test/") };
		using var client = new OrchestraClient(http);

		// Act
		var result = await client.CancelExecutionAsync("exec-1");

		// Assert — URL, method, and structured body.
		captured.Should().NotBeNull();
		captured!.Method.Should().Be(HttpMethod.Post);
		captured.RequestUri!.PathAndQuery.Should().Be("/api/active/exec-1/cancel");
		capturedBody.Should().NotBeNullOrEmpty();

		using var doc = JsonDocument.Parse(capturedBody!);
		doc.RootElement.GetProperty("source").GetString().Should().Be("cli",
			"the CLI must always self-identify so dashboards can distinguish CLI cancels from Portal/automation");
		// `reason` is omitted entirely when null — the client's JsonIgnoreCondition.WhenWritingNull
		// keeps the wire payload tight and lets the server treat "no reason" identically whether
		// the property was absent or explicitly null.
		doc.RootElement.TryGetProperty("reason", out _).Should().BeFalse(
			"null reasons must be omitted, not sent as `\"reason\": null`");

		// Spot-check the response surface so consumers see the cancellation envelope.
		result.GetProperty("cancelled").GetBoolean().Should().BeTrue();
	}

	[Fact]
	public async Task CancelExecutionAsync_WithReasonAndCustomSource_SendsBoth()
	{
		string? capturedBody = null;
		using var handler = new CapturingHandler(async req =>
		{
			capturedBody = req.Content is null ? null : await req.Content.ReadAsStringAsync();
			return new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent("""{"cancelled":true,"executionId":"exec-2","status":"Cancelling"}""",
					System.Text.Encoding.UTF8, "application/json"),
			};
		});
		using var http = new HttpClient(handler) { BaseAddress = new Uri("http://test/") };
		using var client = new OrchestraClient(http);

		await client.CancelExecutionAsync("exec-2", reason: "  superseded by newer scheduled run  ", source: "automation");

		using var doc = JsonDocument.Parse(capturedBody!);
		// Trimmed whitespace — the client normalizes so the server doesn't get noisy " text " values.
		doc.RootElement.GetProperty("reason").GetString().Should().Be("superseded by newer scheduled run");
		doc.RootElement.GetProperty("source").GetString().Should().Be("automation");
	}

	[Fact]
	public async Task CancelExecutionAsync_WithWhitespaceReason_OmitsReasonField()
	{
		// A reason that's only whitespace is treated as "no reason supplied" so the server
		// doesn't persist a blank string.
		string? capturedBody = null;
		using var handler = new CapturingHandler(async req =>
		{
			capturedBody = req.Content is null ? null : await req.Content.ReadAsStringAsync();
			return new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent("""{"cancelled":true,"executionId":"exec-3","status":"Cancelling"}""",
					System.Text.Encoding.UTF8, "application/json"),
			};
		});
		using var http = new HttpClient(handler) { BaseAddress = new Uri("http://test/") };
		using var client = new OrchestraClient(http);

		await client.CancelExecutionAsync("exec-3", reason: "   ");

		using var doc = JsonDocument.Parse(capturedBody!);
		// Whitespace collapses to null on the client (trim + IsNullOrWhiteSpace), and null
		// fields are omitted from the wire. The server-side normalization also collapses
		// missing/null/blank to null, so the two layers agree without overlap.
		doc.RootElement.TryGetProperty("reason", out _).Should().BeFalse(
			"whitespace-only reasons must be omitted, not sent as blank strings");
	}

	/// <summary>
	/// Minimal <see cref="HttpMessageHandler"/> that records the outgoing request and lets
	/// the test return a canned response. Avoids spinning up a TestServer for a pure wire-shape test.
	/// </summary>
	private sealed class CapturingHandler : HttpMessageHandler
	{
		private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

		public CapturingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
		{
			_handler = handler;
		}

		protected override Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request, CancellationToken cancellationToken)
			=> _handler(request);
	}
}
