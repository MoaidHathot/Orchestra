namespace Orchestra.Engine;

/// <summary>
/// Triggers orchestration via an external HTTP webhook POST.
/// Suitable for Power Automate, Zapier, GitHub webhooks, etc.
/// </summary>
public class WebhookTriggerConfig : TriggerConfig
{
	/// <summary>
	/// Optional secret used to validate incoming webhook requests (HMAC signature).
	/// If set, incoming requests must include a valid X-Webhook-Signature header.
	/// </summary>
	public string? Secret { get; init; }

	/// <summary>
	/// Maximum number of concurrent executions triggered by webhooks. Defaults to 1.
	/// </summary>
	public int MaxConcurrent { get; init; } = 1;
}
