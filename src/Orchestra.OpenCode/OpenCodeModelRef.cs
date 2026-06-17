namespace Orchestra.OpenCode;

/// <summary>
/// OpenCode addresses models as <c>providerID/modelID</c> (e.g. <c>github-copilot/claude-opus-4.8</c>,
/// <c>anthropic/claude-3-5-sonnet</c>). Orchestra's <c>model</c> field is a single string, so this
/// helper splits it on the first <c>/</c>. A bare model id with no provider prefix (e.g.
/// <c>claude-opus-4.8</c>) is paired with a configurable fallback provider so existing Copilot-style
/// model ids keep working when a step is routed to OpenCode.
/// </summary>
public readonly record struct OpenCodeModelRef(string ProviderId, string ModelId)
{
	/// <summary>
	/// Parses an Orchestra model string into an OpenCode <c>providerID/modelID</c> pair.
	/// </summary>
	/// <param name="model">The Orchestra model string (e.g. <c>github-copilot/claude-opus-4.8</c> or <c>claude-opus-4.8</c>).</param>
	/// <param name="fallbackProvider">
	/// Provider applied when <paramref name="model"/> has no <c>provider/</c> prefix. When the
	/// fallback is also empty, a bare model id throws so the misconfiguration is loud rather than
	/// silently sent to OpenCode's default provider.
	/// </param>
	public static OpenCodeModelRef Parse(string model, string? fallbackProvider)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(model);

		var trimmed = model.Trim();
		var slash = trimmed.IndexOf('/');
		if (slash > 0 && slash < trimmed.Length - 1)
		{
			var provider = trimmed[..slash].Trim();
			var modelId = trimmed[(slash + 1)..].Trim();
			if (provider.Length > 0 && modelId.Length > 0)
				return new OpenCodeModelRef(provider, modelId);
		}

		if (string.IsNullOrWhiteSpace(fallbackProvider))
		{
			throw new InvalidOperationException(
				$"OpenCode model '{model}' has no 'provider/' prefix and no fallback provider is configured. " +
				"Use a qualified model id such as 'github-copilot/claude-opus-4.8', or set the OpenCode provider's " +
				"fallback provider (orchestra.json opencode.fallbackProvider).");
		}

		return new OpenCodeModelRef(fallbackProvider.Trim(), trimmed);
	}

	public override string ToString() => $"{ProviderId}/{ModelId}";
}
