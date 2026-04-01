using System.Security.Cryptography;
using System.Text;

namespace Orchestra.Host.Api;

/// <summary>
/// Validates webhook request signatures using HMAC-SHA256.
/// Follows the GitHub-style webhook signature scheme where the sender computes
/// <c>HMAC-SHA256(secret, rawBody)</c> and sends it in the <c>X-Hub-Signature-256</c>
/// header as <c>sha256=&lt;hex&gt;</c>.
/// </summary>
public static class WebhookSignatureValidator
{
	/// <summary>
	/// The HTTP header name used to transmit the HMAC-SHA256 signature.
	/// </summary>
	public const string SignatureHeaderName = "X-Hub-Signature-256";

	/// <summary>
	/// Prefix for the signature value (e.g., "sha256=abcdef...").
	/// </summary>
	private const string SignaturePrefix = "sha256=";

	/// <summary>
	/// Computes the HMAC-SHA256 signature of a payload using the given secret.
	/// Returns the result in the format <c>sha256=&lt;hex&gt;</c>.
	/// </summary>
	/// <param name="secret">The shared secret key.</param>
	/// <param name="payload">The raw request body bytes.</param>
	/// <returns>The signature string in <c>sha256=&lt;hex&gt;</c> format.</returns>
	public static string Sign(string secret, byte[] payload)
	{
		var keyBytes = Encoding.UTF8.GetBytes(secret);
		var hash = HMACSHA256.HashData(keyBytes, payload);
		return $"{SignaturePrefix}{Convert.ToHexStringLower(hash)}";
	}

	/// <summary>
	/// Validates a webhook request signature using constant-time comparison
	/// to prevent timing attacks.
	/// </summary>
	/// <param name="signatureHeader">
	/// The value of the <c>X-Hub-Signature-256</c> header from the incoming request.
	/// Expected format: <c>sha256=&lt;hex&gt;</c>.
	/// </param>
	/// <param name="secret">The shared secret key configured for this webhook trigger.</param>
	/// <param name="payload">The raw request body bytes.</param>
	/// <returns>
	/// <c>true</c> if the signature is valid; <c>false</c> if the header is missing,
	/// malformed, or does not match the expected signature.
	/// </returns>
	public static bool Validate(string? signatureHeader, string secret, byte[] payload)
	{
		if (string.IsNullOrWhiteSpace(signatureHeader))
			return false;

		if (!signatureHeader.StartsWith(SignaturePrefix, StringComparison.OrdinalIgnoreCase))
			return false;

		var providedHex = signatureHeader[SignaturePrefix.Length..];

		// Compute expected signature
		var keyBytes = Encoding.UTF8.GetBytes(secret);
		var expectedHash = HMACSHA256.HashData(keyBytes, payload);
		var expectedHex = Convert.ToHexStringLower(expectedHash);

		// Constant-time comparison to prevent timing attacks
		return CryptographicOperations.FixedTimeEquals(
			Encoding.UTF8.GetBytes(expectedHex),
			Encoding.UTF8.GetBytes(providedHex.ToLowerInvariant()));
	}
}
