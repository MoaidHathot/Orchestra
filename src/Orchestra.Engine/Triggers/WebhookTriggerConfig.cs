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

	/// <summary>
	/// Configuration for synchronous webhook responses.
	/// When set with <see cref="WebhookResponseConfig.WaitForResult"/> = true, the webhook
	/// HTTP request will block until the orchestration completes and return the result inline.
	/// </summary>
	public WebhookResponseConfig? Response { get; init; }
}

/// <summary>
/// Controls whether a webhook trigger returns the orchestration result synchronously
/// in the HTTP response, and how to format that response.
/// </summary>
public class WebhookResponseConfig
{
	/// <summary>
	/// If true, the webhook HTTP handler will await the orchestration result and return it
	/// in the response body. If false (default), the webhook fires and forgets.
	/// </summary>
	public bool WaitForResult { get; init; }

	/// <summary>
	/// Optional Handlebars-style template for formatting the response body.
	/// Supports <c>{{stepName.Content}}</c> placeholders that are replaced with terminal step outputs.
	/// If null, the raw <see cref="OrchestrationResult"/> is serialized as JSON.
	/// </summary>
	public string? ResponseTemplate { get; init; }

	/// <summary>
	/// Maximum seconds to wait for the orchestration to complete before returning a 504 timeout.
	/// Defaults to 120 seconds. Only used when <see cref="WaitForResult"/> is true.
	/// </summary>
	public int TimeoutSeconds { get; init; } = 120;
}
