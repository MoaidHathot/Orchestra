using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orchestra.OpenCode;

/// <summary>
/// A raw OpenCode server-sent event from the <c>GET /event</c> bus: a discriminator
/// (<see cref="Type"/>, e.g. <c>message.part.updated</c>, <c>session.idle</c>,
/// <c>session.error</c>, <c>permission.updated</c>) plus the untyped <see cref="Properties"/>
/// payload. All wire-shape interpretation lives in <c>OpenCodeSessionHandler</c> so it can be
/// unit-tested from JSON fixtures and the transport stays a thin pass-through.
/// </summary>
public sealed record OpenCodeServerEvent
{
	public required string Type { get; init; }
	public JsonElement Properties { get; init; }
}

/// <summary>OpenCode <c>providerID/modelID</c> pair as sent on a prompt request.</summary>
internal sealed record OpenCodeModelDto
{
	[JsonPropertyName("providerID")] public required string ProviderId { get; init; }
	[JsonPropertyName("modelID")] public required string ModelId { get; init; }
}

/// <summary>A message part sent to OpenCode (text or file).</summary>
internal sealed record OpenCodePartDto
{
	[JsonPropertyName("type")] public required string Type { get; init; }
	[JsonPropertyName("text")] public string? Text { get; init; }
	[JsonPropertyName("mime")] public string? Mime { get; init; }
	[JsonPropertyName("filename")] public string? Filename { get; init; }
	[JsonPropertyName("url")] public string? Url { get; init; }

	public static OpenCodePartDto TextPart(string text) => new() { Type = "text", Text = text };
}

/// <summary>Body for <c>POST /session/:id/prompt_async</c> (and <c>/message</c>).</summary>
internal sealed record OpenCodePromptRequest
{
	[JsonPropertyName("model")] public required OpenCodeModelDto Model { get; init; }
	[JsonPropertyName("agent")] public string? Agent { get; init; }
	[JsonPropertyName("system")] public string? System { get; init; }
	[JsonPropertyName("parts")] public required IReadOnlyList<OpenCodePartDto> Parts { get; init; }
}

/// <summary>Body for <c>POST /session</c>.</summary>
internal sealed record OpenCodeCreateSessionRequest
{
	[JsonPropertyName("title")] public string? Title { get; init; }
}

/// <summary>Body for <c>POST /session/:id/permissions/:permissionID</c>.</summary>
internal sealed record OpenCodePermissionResponse
{
	[JsonPropertyName("response")] public required string Response { get; init; }
}

internal static class OpenCodeJson
{
	/// <summary>
	/// Shared options. OpenCode's wire casing is irregular (<c>providerID</c>, <c>sessionID</c>,
	/// <c>callID</c>), so DTOs pin names explicitly via <see cref="JsonPropertyNameAttribute"/>
	/// rather than relying on a naming policy.
	/// </summary>
	public static readonly JsonSerializerOptions Options = new()
	{
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		PropertyNameCaseInsensitive = true,
	};
}
