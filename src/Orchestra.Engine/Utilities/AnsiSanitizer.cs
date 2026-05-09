using System.Text.RegularExpressions;

namespace Orchestra.Engine;

/// <summary>
/// Removes ANSI escape sequences (color/cursor/OSC) from text captured from
/// child-process stdout or stderr.
/// </summary>
/// <remarks>
/// Many shells and tools (notably PowerShell 7's <c>ConciseView</c> error formatter,
/// <c>git</c>, <c>gh</c>, <c>npm</c>) emit ANSI control sequences even when stdout is
/// redirected. When that text is captured into a <see cref="System.Text.StringBuilder"/>
/// instead of rendered by a terminal, the sequences appear as literal noise such as
/// <c>[31;1m</c>, <c>[0m</c>, <c>[36;1m</c>. This sanitizer strips them so downstream
/// orchestration steps and trace viewers receive clean text.
///
/// <para>Alternation order matters: the engine tries the longer/specific patterns first
/// so that, for example, <c>ESC ]</c> binds as the OSC introducer rather than as a
/// two-byte fallback.</para>
///
/// <para>The pattern matches:</para>
/// <list type="bullet">
///   <item><description>CSI sequences: <c>ESC [ &lt;params&gt; &lt;intermediate&gt; &lt;final&gt;</c>
///     (covers SGR colors, cursor movement, line erase, etc.).</description></item>
///   <item><description>OSC sequences: <c>ESC ] ... BEL</c> or <c>ESC ] ... ESC \</c>
///     (e.g., terminal title updates).</description></item>
///   <item><description>String sequences: <c>ESC P|X|^|_ ... ESC \</c>
///     (DCS, SOS, PM, APC).</description></item>
///   <item><description>Single-byte ESC sequences: <c>ESC X</c> for X in the printable
///     range <c>@</c>–<c>~</c> (covers Fe codes like <c>ESC c</c> reset, <c>ESC E</c>,
///     <c>ESC H</c>, and Fs codes like <c>ESC =</c>, <c>ESC &gt;</c>).</description></item>
/// </list>
///
/// <para>The pattern is conservative: it only matches well-formed escape sequences,
/// so a stray <c>\x1B</c> at end-of-stream is left in place rather than potentially
/// eating subsequent unrelated text. Malformed OSC/DCS without a terminator are
/// also left intact (only the introducer pair is consumed by the two-byte fallback).</para>
/// </remarks>
internal static partial class AnsiSanitizer
{
	[GeneratedRegex(
		@"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\)|[PX^_][^\x1B]*\x1B\\|[@-Z\\-~])",
		RegexOptions.Compiled | RegexOptions.CultureInvariant)]
	private static partial Regex AnsiPattern();

	/// <summary>
	/// Returns a copy of <paramref name="input"/> with all ANSI escape sequences removed.
	/// </summary>
	/// <param name="input">The text to sanitize. May be <see langword="null"/> or empty.</param>
	/// <returns>
	/// The input unchanged if it is <see langword="null"/>, empty, or contains no ESC byte;
	/// otherwise a new string with every recognized ANSI sequence stripped.
	/// </returns>
	public static string? Strip(string? input)
	{
		if (string.IsNullOrEmpty(input))
			return input;

		// Fast path: skip the regex entirely when there is no ESC byte to find.
		if (input.IndexOf('\x1B') < 0)
			return input;

		return AnsiPattern().Replace(input, string.Empty);
	}
}
