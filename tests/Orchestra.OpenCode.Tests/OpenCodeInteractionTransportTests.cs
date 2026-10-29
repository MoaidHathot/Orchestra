using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;

namespace Orchestra.OpenCode.Tests;

public class OpenCodeInteractionTransportTests
{
	[Fact]
	public async Task SessionOwnership_WalksAncestorsAndRejectsUnrelatedRoots()
	{
		var requests = new List<string>();
		using var http = new HttpClient(new Handler((request, _) =>
		{
			var path = request.RequestUri!.AbsolutePath;
			requests.Add(path);
			return Task.FromResult(path switch
			{
				"/api/session/grandchild" => Json("""{"data":{"parentID":"child"}}"""),
				"/api/session/child" => Json("""{"data":{"parentID":"root"}}"""),
				"/api/session/unrelated" => Json("""{"data":{}}"""),
				_ => throw new InvalidOperationException(path),
			});
		}));
		await using var client = new OpenCodeHttpClient("http://localhost", null, null, http);
		(await client.IsSessionWithinAsync("root", "root", default)).Should().BeTrue();
		requests.Should().BeEmpty("ownership of the root itself requires no network lookup");
		(await client.IsSessionWithinAsync("grandchild", "root", default)).Should().BeTrue();
		requests.Should().Equal("/api/session/grandchild", "/api/session/child");
		(await client.IsSessionWithinAsync("unrelated", "root", default)).Should().BeFalse();
	}

	[Fact]
	public async Task SessionOwnership_StopsOnCyclesAndDeletedAncestors()
	{
		var requests = new List<string>();
		using var http = new HttpClient(new Handler((request, _) =>
		{
			var path = request.RequestUri!.AbsolutePath;
			requests.Add(path);
			return Task.FromResult(path switch
			{
				"/api/session/a" => Json("""{"data":{"parentID":"b"}}"""),
				"/api/session/b" => Json("""{"data":{"parentID":"a"}}"""),
				"/api/session/deleted" => new HttpResponseMessage(HttpStatusCode.NotFound),
				_ => throw new InvalidOperationException(path),
			});
		}));
		await using var client = new OpenCodeHttpClient("http://localhost", null, null, http);
		(await client.IsSessionWithinAsync("a", "root", default).WaitAsync(TimeSpan.FromSeconds(5))).Should().BeFalse();
		requests.Should().Equal("/api/session/a", "/api/session/b");
		(await client.IsSessionWithinAsync("deleted", "root", default)).Should().BeFalse();
	}

	[Fact]
	public async Task SessionOwnership_DoesNotTreatAuthenticationFailureAsAnUnrelatedSession()
	{
		using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized))));
		await using var client = new OpenCodeHttpClient("http://localhost", null, null, http);
		Func<Task> check = async () => await client.IsSessionWithinAsync("child", "root", default);
		(await check.Should().ThrowAsync<HttpRequestException>()).Which.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
	}

	[Fact]
	public async Task Forms_GetReplyAndCancel_UseTheV2EnvelopeAndPreserveAnswerTypes()
	{
		var requests = new List<(HttpMethod Method, string Path, string? Body)>();
		using var http = new HttpClient(new Handler(async (request, token) =>
		{
			requests.Add((request.Method, request.RequestUri!.AbsolutePath,
				request.Content is null ? null : await request.Content.ReadAsStringAsync(token)));
			return request.Method == HttpMethod.Get
				? Json("""{"data":{"id":"frm/two","title":"Question","fields":[{"key":"answer","type":"string"}]}}""")
				: new HttpResponseMessage(HttpStatusCode.NoContent);
		}));
		await using var client = new OpenCodeHttpClient("http://localhost", null, null, http);
		var form = await client.GetFormAsync("ses/one", "frm/two", default);
		form.GetProperty("id").GetString().Should().Be("frm/two");
		form.GetProperty("fields")[0].GetProperty("key").GetString().Should().Be("answer");
		await client.ReplyFormAsync("ses/one", "frm/two", new { enabled = true, count = 42, targets = new[] { "a", "b" } }, default);
		await client.ReplyFormAsync("ses/one", "frm/two", null, default);
		requests.Select(r => r.Method).Should().Equal(HttpMethod.Get, HttpMethod.Post, HttpMethod.Delete);
		requests.Select(r => r.Path).Should().Equal(
			"/api/session/ses%2Fone/form/frm%2Ftwo", "/api/session/ses%2Fone/form/frm%2Ftwo/reply", "/api/session/ses%2Fone/form/frm%2Ftwo");
		using var body = JsonDocument.Parse(requests[1].Body!);
		var answer = body.RootElement.GetProperty("answer");
		answer.GetProperty("enabled").GetBoolean().Should().BeTrue();
		answer.GetProperty("count").GetInt32().Should().Be(42);
		answer.GetProperty("targets").EnumerateArray().Select(t => t.GetString()).Should().Equal("a", "b");
		requests[2].Body.Should().BeNull();
	}

	[Theory]
	[InlineData("once")]
	[InlineData("reject")]
	public async Task PermissionReplies_UseV2DecisionAndPrivateServerAuthentication(string decision)
	{
		using var http = new HttpClient(new Handler(async (request, token) =>
		{
			request.Method.Should().Be(HttpMethod.Post);
			request.RequestUri!.AbsolutePath.Should().Be("/api/session/ses_root/permission/per_request/reply");
			request.Headers.Authorization!.Scheme.Should().Be("Basic");
			Encoding.UTF8.GetString(Convert.FromBase64String(request.Headers.Authorization.Parameter!)).Should().Be("opencode:test-password");
			using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
			body.RootElement.GetProperty("decision").GetString().Should().Be(decision);
			body.RootElement.TryGetProperty("response", out _).Should().BeFalse("the legacy permission payload is not valid V2");
			return new HttpResponseMessage(HttpStatusCode.NoContent);
		}));
		await using var client = new OpenCodeHttpClient("http://localhost", null, "test-password", http);
		await client.RespondPermissionAsync("ses_root", "per_request", decision, default);
	}

	[Theory]
	[InlineData("low", true)]
	[InlineData("high", false)]
	public async Task ModelReadiness_RequiresAnAdvertisedRequestedVariant(string variant, bool available)
	{
		using var http = new HttpClient(new Handler((request, _) => Task.FromResult(Json(request.RequestUri!.AbsolutePath switch
		{
			"/api/agent" => """{"data":[{"id":"orchestra-primary"}]}""",
			"/api/model" => """{"data":[{"providerID":"provider","id":"model","enabled":true,"variants":[{"id":"low"}]}]}""",
			_ => throw new InvalidOperationException("Unexpected readiness endpoint."),
		}))));
		await using var client = new OpenCodeHttpClient("http://localhost", null, null, http);
		Func<Task> ready = () => client.EnsureReadyAsync(new() { ProviderId = "provider", ModelId = "model", Variant = variant }, "orchestra-primary", TimeSpan.FromSeconds(5), default);
		if (available)
			await ready.Should().NotThrowAsync();
		else
			await ready.Should().ThrowAsync<OpenCodeSessionFailedException>().WithMessage("*does not advertise variant 'high'*");
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ReadinessDeadline_CancelsAnUnresponsiveHttpRequest(bool mcp)
	{
		using var http = new HttpClient(new Handler(async (_, token) =>
		{
			await Task.Delay(Timeout.Infinite, token);
			return new HttpResponseMessage(HttpStatusCode.OK);
		}));
		await using var client = new OpenCodeHttpClient("http://localhost", null, null, http);
		Func<Task> ready = async () =>
		{
			var operation = mcp
				? client.WaitForMcpNamesAsync(["required"], TimeSpan.FromMilliseconds(50), default)
				: client.EnsureReadyAsync(new() { ProviderId = "provider", ModelId = "model" }, null, TimeSpan.FromMilliseconds(50), default);
			await operation.WaitAsync(TimeSpan.FromSeconds(5));
		};
		await ready.Should().ThrowAsync<OperationCanceledException>();
	}

	private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
	private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
	}
}
