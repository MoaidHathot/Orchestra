using System.Net;
using FluentAssertions;
using Xunit;

namespace Orchestra.Server.Tests;

/// <summary>
/// Integration tests for the new <c>GET /api/orchestrations/{name}/runs/{runId}/attach</c>
/// endpoint that surfaces an SSE attachment by user-visible IDs (not internal executionId).
/// </summary>
public class RunIdAttachApiTests : IClassFixture<ServerWebApplicationFactory>, IDisposable
{
	private readonly HttpClient _client;

	public RunIdAttachApiTests(ServerWebApplicationFactory factory)
	{
		_client = factory.CreateClient();
	}

	public void Dispose() => _client.Dispose();

	[Fact]
	public async Task Attach_UnknownRun_Returns404()
	{
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		using var request = new HttpRequestMessage(HttpMethod.Get, "/api/orchestrations/nope/runs/nope-run/attach");
		using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

		response.StatusCode.Should().Be(HttpStatusCode.NotFound);
		response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
	}
}
