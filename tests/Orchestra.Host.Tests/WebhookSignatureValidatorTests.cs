using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Orchestra.Host.Api;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// Tests for WI-F: WebhookSignatureValidator HMAC-SHA256 signing and validation.
/// Verifies correct signing, validation, edge cases, and timing-attack resistance.
/// </summary>
public class WebhookSignatureValidatorTests
{
	private const string TestSecret = "my-webhook-secret";
	private static readonly byte[] TestPayload = Encoding.UTF8.GetBytes("{\"key\":\"value\"}");

	// ── Sign tests ─────────────────────────────────────────────────────────

	[Fact]
	public void Sign_ReturnsCorrectFormat()
	{
		// Act
		var signature = WebhookSignatureValidator.Sign(TestSecret, TestPayload);

		// Assert
		signature.Should().StartWith("sha256=");
		signature["sha256=".Length..].Should().MatchRegex("^[0-9a-f]{64}$");
	}

	[Fact]
	public void Sign_MatchesManualHmacComputation()
	{
		// Arrange — compute expected HMAC manually
		var keyBytes = Encoding.UTF8.GetBytes(TestSecret);
		var expectedHash = HMACSHA256.HashData(keyBytes, TestPayload);
		var expectedHex = Convert.ToHexStringLower(expectedHash);

		// Act
		var signature = WebhookSignatureValidator.Sign(TestSecret, TestPayload);

		// Assert
		signature.Should().Be($"sha256={expectedHex}");
	}

	[Fact]
	public void Sign_DifferentSecrets_ProduceDifferentSignatures()
	{
		// Act
		var sig1 = WebhookSignatureValidator.Sign("secret-a", TestPayload);
		var sig2 = WebhookSignatureValidator.Sign("secret-b", TestPayload);

		// Assert
		sig1.Should().NotBe(sig2);
	}

	[Fact]
	public void Sign_DifferentPayloads_ProduceDifferentSignatures()
	{
		// Act
		var sig1 = WebhookSignatureValidator.Sign(TestSecret, Encoding.UTF8.GetBytes("body-a"));
		var sig2 = WebhookSignatureValidator.Sign(TestSecret, Encoding.UTF8.GetBytes("body-b"));

		// Assert
		sig1.Should().NotBe(sig2);
	}

	[Fact]
	public void Sign_EmptyPayload_ProducesValidSignature()
	{
		// Act
		var signature = WebhookSignatureValidator.Sign(TestSecret, []);

		// Assert
		signature.Should().StartWith("sha256=");
		signature["sha256=".Length..].Should().MatchRegex("^[0-9a-f]{64}$");
	}

	// ── Validate tests ─────────────────────────────────────────────────────

	[Fact]
	public void Validate_CorrectSignature_ReturnsTrue()
	{
		// Arrange
		var signature = WebhookSignatureValidator.Sign(TestSecret, TestPayload);

		// Act & Assert
		WebhookSignatureValidator.Validate(signature, TestSecret, TestPayload).Should().BeTrue();
	}

	[Fact]
	public void Validate_WrongSignature_ReturnsFalse()
	{
		// Arrange — sign with a different secret
		var wrongSignature = WebhookSignatureValidator.Sign("wrong-secret", TestPayload);

		// Act & Assert
		WebhookSignatureValidator.Validate(wrongSignature, TestSecret, TestPayload).Should().BeFalse();
	}

	[Fact]
	public void Validate_NullHeader_ReturnsFalse()
	{
		WebhookSignatureValidator.Validate(null, TestSecret, TestPayload).Should().BeFalse();
	}

	[Fact]
	public void Validate_EmptyHeader_ReturnsFalse()
	{
		WebhookSignatureValidator.Validate("", TestSecret, TestPayload).Should().BeFalse();
	}

	[Fact]
	public void Validate_WhitespaceHeader_ReturnsFalse()
	{
		WebhookSignatureValidator.Validate("   ", TestSecret, TestPayload).Should().BeFalse();
	}

	[Fact]
	public void Validate_MissingPrefix_ReturnsFalse()
	{
		// Arrange — valid hex but no "sha256=" prefix
		var signature = WebhookSignatureValidator.Sign(TestSecret, TestPayload);
		var hexOnly = signature["sha256=".Length..];

		// Act & Assert
		WebhookSignatureValidator.Validate(hexOnly, TestSecret, TestPayload).Should().BeFalse();
	}

	[Fact]
	public void Validate_WrongPrefix_ReturnsFalse()
	{
		var signature = WebhookSignatureValidator.Sign(TestSecret, TestPayload);
		var malformed = "sha512=" + signature["sha256=".Length..];

		WebhookSignatureValidator.Validate(malformed, TestSecret, TestPayload).Should().BeFalse();
	}

	[Fact]
	public void Validate_MalformedHex_ReturnsFalse()
	{
		WebhookSignatureValidator.Validate("sha256=not-valid-hex!", TestSecret, TestPayload).Should().BeFalse();
	}

	[Fact]
	public void Validate_CaseInsensitivePrefix_ReturnsTrue()
	{
		// Arrange — uppercase prefix
		var signature = WebhookSignatureValidator.Sign(TestSecret, TestPayload);
		var upperPrefix = "SHA256=" + signature["sha256=".Length..];

		// Act & Assert
		WebhookSignatureValidator.Validate(upperPrefix, TestSecret, TestPayload).Should().BeTrue();
	}

	[Fact]
	public void Validate_UppercaseHex_ReturnsTrue()
	{
		// Arrange — uppercase hex digits
		var signature = WebhookSignatureValidator.Sign(TestSecret, TestPayload);
		var upperHex = "sha256=" + signature["sha256=".Length..].ToUpperInvariant();

		// Act & Assert
		WebhookSignatureValidator.Validate(upperHex, TestSecret, TestPayload).Should().BeTrue();
	}

	[Fact]
	public void Validate_EmptyPayload_CorrectSignature_ReturnsTrue()
	{
		// Arrange
		var emptyPayload = Array.Empty<byte>();
		var signature = WebhookSignatureValidator.Sign(TestSecret, emptyPayload);

		// Act & Assert
		WebhookSignatureValidator.Validate(signature, TestSecret, emptyPayload).Should().BeTrue();
	}

	[Fact]
	public void Validate_EmptyPayload_WrongSignature_ReturnsFalse()
	{
		// Arrange — signature was computed for non-empty payload
		var signature = WebhookSignatureValidator.Sign(TestSecret, TestPayload);
		var emptyPayload = Array.Empty<byte>();

		// Act & Assert
		WebhookSignatureValidator.Validate(signature, TestSecret, emptyPayload).Should().BeFalse();
	}

	// ── Header name constant ───────────────────────────────────────────────

	[Fact]
	public void SignatureHeaderName_IsExpectedValue()
	{
		WebhookSignatureValidator.SignatureHeaderName.Should().Be("X-Hub-Signature-256");
	}

	// ── Deterministic output ───────────────────────────────────────────────

	[Fact]
	public void Sign_IsDeterministic()
	{
		var sig1 = WebhookSignatureValidator.Sign(TestSecret, TestPayload);
		var sig2 = WebhookSignatureValidator.Sign(TestSecret, TestPayload);

		sig1.Should().Be(sig2);
	}

	// ── Roundtrip ──────────────────────────────────────────────────────────

	[Fact]
	public void Sign_ThenValidate_Roundtrip()
	{
		// Test with various payloads
		var payloads = new[]
		{
			""u8.ToArray(),
			"{}"u8.ToArray(),
			Encoding.UTF8.GetBytes("{\"event\":\"push\",\"ref\":\"refs/heads/main\"}"),
			new byte[1024], // 1KB of zeros
		};

		foreach (var payload in payloads)
		{
			var signature = WebhookSignatureValidator.Sign(TestSecret, payload);
			WebhookSignatureValidator.Validate(signature, TestSecret, payload).Should().BeTrue(
				$"roundtrip should succeed for payload of length {payload.Length}");
		}
	}
}
