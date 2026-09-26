using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orchestra.Host.Extensions;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// <see cref="ApplicationBuilderExtensions.UseOrchestraHostPipeline"/> is the single pipeline
/// every Orchestra host (standalone server, embedded portal, tests) installs. These tests pin
/// what it guarantees: unhandled exceptions become RFC 7807 problem documents, and only
/// loopback origins pass CORS.
/// </summary>
public class HostPipelineTests
{
	private static IHost StartHost()
	{
		var host = new HostBuilder()
			.ConfigureWebHost(webHost =>
			{
				webHost.UseTestServer();
				webHost.ConfigureServices(services =>
				{
					services.AddRouting();
					services.AddOrchestraHostCors();
				});
				webHost.Configure(app =>
				{
					app.UseOrchestraHostPipeline();
					app.UseRouting();
					app.UseEndpoints(endpoints =>
					{
						endpoints.MapGet("/boom", () => { throw new InvalidOperationException("kaboom"); });
						endpoints.MapGet("/ok", () => Results.Json(new { ok = true }));
					});
				});
			})
			.Build();

		host.Start();
		return host;
	}

	[Fact]
	public async Task UnhandledException_BecomesProblemDetailsJson_Not_Html()
	{
		using var host = StartHost();
		using var client = host.GetTestClient();

		var response = await client.GetAsync("/boom");

		response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
		response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		body.GetProperty("status").GetInt32().Should().Be(500);
		body.GetProperty("title").GetString().Should().NotBeNullOrWhiteSpace();
	}

	[Theory]
	[InlineData("http://localhost:5173")]
	[InlineData("http://127.0.0.1:5000")]
	[InlineData("http://[::1]:5000")]
	public async Task Cors_AllowsLoopbackOrigins(string origin)
	{
		using var host = StartHost();
		using var client = host.GetTestClient();

		using var request = new HttpRequestMessage(HttpMethod.Options, "/ok");
		request.Headers.Add("Origin", origin);
		request.Headers.Add("Access-Control-Request-Method", "POST");

		var response = await client.SendAsync(request);

		response.StatusCode.Should().Be(HttpStatusCode.NoContent);
		response.Headers.TryGetValues("Access-Control-Allow-Origin", out var allowed).Should().BeTrue();
		allowed!.Single().Should().Be(origin);
		response.Headers.TryGetValues("Access-Control-Allow-Credentials", out var creds).Should().BeTrue();
		creds!.Single().Should().Be("true");
	}

	[Theory]
	[InlineData("http://evil.example.com")]
	[InlineData("https://orchestra.internal:5000")]
	[InlineData("not a url")]
	public async Task Cors_RejectsNonLoopbackOrigins(string origin)
	{
		using var host = StartHost();
		using var client = host.GetTestClient();

		using var request = new HttpRequestMessage(HttpMethod.Options, "/ok");
		request.Headers.Add("Origin", origin);
		request.Headers.Add("Access-Control-Request-Method", "POST");

		var response = await client.SendAsync(request);

		response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse(
			$"'{origin}' is not a loopback origin and must not be allowed");
	}
}
