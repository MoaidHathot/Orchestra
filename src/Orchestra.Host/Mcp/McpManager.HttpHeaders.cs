using McpProxy.Sdk.Configuration;
using McpProxy.Sdk.Debugging;
using McpProxy.Sdk.Proxy;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Orchestra.Engine;

namespace Orchestra.Host.Mcp;

public partial class McpManager
{
	// McpProxy.Sdk 1.22 accepts WithHeaders but omits them from HttpClientTransportOptions.
	// Register the header-bearing transport directly; the same client manager owns and disposes it.
	private static async Task ConnectWithHeadersAsync(IServiceProvider services, McpClientManager clients,
		RemoteMcp remote, CancellationToken token)
	{
		var handlers = services.GetRequiredService<ProxyClientHandlers>();
		var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
		var transport = new HttpClientTransport(new()
		{
			Name = remote.Name, Endpoint = new Uri(remote.Endpoint), TransportMode = HttpTransportMode.StreamableHttp,
			AdditionalHeaders = remote.Headers,
		}, http, ownsHttpClient: true);
		McpClient client;
		try
		{
			client = await McpClient.CreateAsync(transport, new McpClientOptions
			{
				Handlers = new() { SamplingHandler = handlers.HandleSamplingAsync, ElicitationHandler = handlers.HandleElicitationAsync, RootsHandler = handlers.HandleRootsAsync },
			}, cancellationToken: token);
		}
		catch { http.Dispose(); throw; }
		clients.RegisterClient(remote.Name, new McpClientWrapper(client), services.GetRequiredService<ProxyConfiguration>().Mcp[remote.Name]);
		var notifications = services.GetRequiredService<NotificationForwarder>();
		client.RegisterNotificationHandler(NotificationMethods.ProgressNotification, notifications.CreateProgressNotificationHandler(remote.Name));
		foreach (var method in new[] { "notifications/tools/list_changed", "notifications/resources/list_changed", "notifications/prompts/list_changed", "notifications/resources/updated" })
			client.RegisterNotificationHandler(method, notifications.CreateNotificationHandler(remote.Name, method));
		var health = services.GetRequiredService<IHealthTracker>();
		health.RecordConnectionState(remote.Name, connected: true);
		_ = client.Completion.ContinueWith(_ => health.RecordConnectionState(remote.Name, connected: false), TaskScheduler.Default);
	}
}
