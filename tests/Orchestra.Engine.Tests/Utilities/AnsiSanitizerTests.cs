using FluentAssertions;

namespace Orchestra.Engine.Tests.Utilities;

public class AnsiSanitizerTests
{
	[Fact]
	public void Strip_NullInput_ReturnsNull()
	{
		AnsiSanitizer.Strip(null).Should().BeNull();
	}

	[Fact]
	public void Strip_EmptyInput_ReturnsEmpty()
	{
		AnsiSanitizer.Strip(string.Empty).Should().BeEmpty();
	}

	[Fact]
	public void Strip_PlainText_ReturnsUnchanged()
	{
		const string input = "Hello, world! No escape sequences here.";

		var result = AnsiSanitizer.Strip(input);

		result.Should().BeSameAs(input, "the fast path should return the same instance when there is no ESC byte");
	}

	[Fact]
	public void Strip_TextWithBracketsButNoEscByte_ReturnsUnchanged()
	{
		// Brackets and digits without a leading ESC byte must NOT be matched.
		const string input = "values: [31;1m and [0m are array indices, not ANSI codes";

		var result = AnsiSanitizer.Strip(input);

		result.Should().Be(input);
	}

	[Theory]
	[InlineData("\x1B[31;1mred\x1B[0m", "red")]
	[InlineData("\x1B[1mbold\x1B[22m", "bold")]
	[InlineData("plain \x1B[32mgreen\x1B[0m plain", "plain green plain")]
	[InlineData("\x1B[38;5;208m256-color\x1B[0m", "256-color")]
	[InlineData("\x1B[38;2;255;100;0mtruecolor\x1B[0m", "truecolor")]
	public void Strip_SgrColorSequences_RemovesAllOfThem(string input, string expected)
	{
		var result = AnsiSanitizer.Strip(input);

		result.Should().Be(expected);
	}

	[Theory]
	[InlineData("\x1B[2J", "")]                     // erase entire screen
	[InlineData("\x1B[H", "")]                      // cursor home
	[InlineData("\x1B[10;20H", "")]                 // cursor position
	[InlineData("\x1B[K", "")]                      // erase to end of line
	[InlineData("foo\x1B[1Abar", "foobar")]         // cursor up between text
	public void Strip_NonSgrCsiSequences_AreAlsoRemoved(string input, string expected)
	{
		var result = AnsiSanitizer.Strip(input);

		result.Should().Be(expected);
	}

	[Fact]
	public void Strip_OscBelTerminated_RemovesSequence()
	{
		// OSC sequence: terminal title set, BEL-terminated.
		// Note: use \u0007 (not \x07) and \u001B (not \x1B) before any hex-character so the
		// C# compiler's variable-length \x escape doesn't greedily consume following digits.
		const string input = "before\u001B]0;Window Title\u0007after";

		var result = AnsiSanitizer.Strip(input);

		result.Should().Be("beforeafter");
	}

	[Fact]
	public void Strip_OscStTerminated_RemovesSequence()
	{
		// OSC sequence: terminal title set, ST-terminated (ESC \).
		const string input = "before\u001B]0;Window Title\u001B\\after";

		var result = AnsiSanitizer.Strip(input);

		result.Should().Be("beforeafter");
	}

	[Fact]
	public void Strip_DanglingEscByte_LeavesItAlone()
	{
		// A bare ESC at end-of-stream is not a complete sequence; we deliberately
		// leave it alone rather than risk eating subsequent unrelated bytes if
		// the pattern were more aggressive.
		const string input = "data\u001B";

		var result = AnsiSanitizer.Strip(input);

		result.Should().Be("data\u001B");
	}

	[Fact]
	public void Strip_PowerShellConciseViewError_ProducesReadableText()
	{
		// This is the exact pattern PowerShell 7's ConciseView emits when an
		// error is thrown via `-File`, which is what the script step launches.
		// The leading ESC bytes are reconstructed; what users see on the wire is
		// the rest. This fixture asserts the noise is stripped completely while
		// the human-readable error message survives.
		const string esc = "\x1B";
		var input =
			$"{esc}[31;1mException: {esc}[0mC:\\Users\\moaid\\AppData\\Local\\Temp\\orchestra.ps1:49{esc}[0m\n" +
			$"{esc}[31;1m{esc}[0m{esc}[36;1mLine |{esc}[0m\n" +
			$"{esc}[31;1m{esc}[0m{esc}[36;1m{esc}[36;1m 49 | {esc}[0m {esc}[36;1mthrow \"PowerReview open failed with exit code $exitCode\"{esc}[0m";

		var result = AnsiSanitizer.Strip(input);

		result.Should().NotBeNull();
		result!.Should().NotContain(esc);
		result.Should().NotContain("[31;1m");
		result.Should().NotContain("[36;1m");
		result.Should().NotContain("[0m");
		result.Should().Contain("Exception:");
		result.Should().Contain("PowerReview open failed with exit code");
		result.Should().Contain("Line |");
		result.Should().Contain(" 49 | ");
	}

	[Fact]
	public void Strip_LongMixedContent_StripsOnlyEscapeSequences()
	{
		const string esc = "\x1B";
		var input = string.Join('\n',
		[
			$"{esc}[32mok{esc}[0m line 1",
			"plain line 2",
			$"line 3 with {esc}[1;33mbold yellow{esc}[0m middle",
			"plain line 4",
			$"{esc}[2K{esc}[31merror{esc}[0m end",
		]);

		var result = AnsiSanitizer.Strip(input);

		result.Should().Be(
			"ok line 1\n" +
			"plain line 2\n" +
			"line 3 with bold yellow middle\n" +
			"plain line 4\n" +
			"error end");
	}

	[Fact]
	public void Strip_TwoByteEscape_RemovesIt()
	{
		// ESC c is a two-byte "reset terminal" sequence with no parameters.
		// Use \u001B explicitly because "\x1Bc" would be parsed by the C# compiler
		// as the single Unicode code point \u01BC (\x consumes 1-4 hex digits).
		const string input = "before\u001Bcafter";

		var result = AnsiSanitizer.Strip(input);

		result.Should().Be("beforeafter");
	}

	[Fact]
	public void Strip_OnlyEscapeSequences_ReturnsEmpty()
	{
		const string input = "\x1B[0m\x1B[31m\x1B[1m\x1B[0m";

		var result = AnsiSanitizer.Strip(input);

		result.Should().BeEmpty();
	}
}
