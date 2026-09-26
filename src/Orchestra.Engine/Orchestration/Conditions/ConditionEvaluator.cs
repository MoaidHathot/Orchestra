using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Orchestra.Engine;

/// <summary>
/// Thrown when a <c>when:</c> expression cannot be parsed. The message includes the 1-based
/// character position so <c>orchestra validate</c> and the run log point at the mistake.
/// </summary>
public sealed class ConditionSyntaxException : Exception
{
	public ConditionSyntaxException(string message, int position)
		: base($"{message} (at position {position})")
	{
		Position = position;
	}

	/// <summary>1-based character offset into the expression.</summary>
	public int Position { get; }
}

/// <summary>
/// Facts about the step being gated that outcome functions need.
/// </summary>
/// <param name="AnyDependencyFailed">
/// True when at least one direct dependency ended in a state that would normally block the
/// step (failed, cancelled, no-action, or skipped because of its own failed dependency).
/// </param>
public sealed record ConditionContext(bool AnyDependencyFailed)
{
	public static ConditionContext None { get; } = new(AnyDependencyFailed: false);
}

/// <summary>
/// Evaluates step <c>when:</c> conditions.
/// </summary>
/// <remarks>
/// <para>
/// Grammar (lowest precedence first):
/// <code>
/// or      := and ( '||' and )*
/// and     := not ( '&amp;&amp;' not )*
/// not     := '!' not | compare
/// compare := operand ( ( '==' | '!=' | '&lt;' | '&lt;=' | '&gt;' | '&gt;=' ) operand )?
/// operand := string | number | true | false | null | '{{' template '}}'
///          | function '(' [ or ( ',' or )* ] ')' | '(' or ')'
/// </code>
/// <c>and</c>/<c>or</c>/<c>not</c> are accepted as aliases for <c>&amp;&amp;</c>/<c>||</c>/<c>!</c>.
/// Functions: <c>contains(text, needle)</c>, <c>startsWith(text, prefix)</c>,
/// <c>endsWith(text, suffix)</c> (all case-insensitive), <c>matches(text, regex)</c>,
/// <c>empty(x)</c>, <c>lower(x)</c>, <c>trim(x)</c>, <c>length(x)</c>, and the outcome
/// functions <c>always()</c>, <c>success()</c>, <c>failure()</c>.
/// </para>
/// <para>
/// Template references (<c>{{param.x}}</c>, <c>{{step.output}}</c>, <c>{{step.status}}</c>) are
/// tokens of the grammar, resolved through the callback passed to <see cref="Evaluate"/>. They
/// are never spliced into the expression text, so a step output containing quotes or operators
/// cannot change what the condition means, and no quoting is needed around them.
/// </para>
/// <para>
/// <b>Dependency failures.</b> By default a step whose dependency failed is skipped before its
/// condition is consulted (failures cascade). A condition that <em>reasons about outcomes</em>
/// — one that calls <c>always()</c>, <c>success()</c> or <c>failure()</c>, or references some
/// step's <c>status</c>/<c>errorMessage</c> — suspends that cascade for its step so the
/// condition alone decides. This mirrors GitHub Actions' <c>if: failure()</c> rule and is what
/// makes on-failure handlers expressible: <c>when: "failure()"</c>. See
/// <see cref="ReferencesDependencyOutcome"/>.
/// </para>
/// <para>
/// Values are dynamically typed: template results and string literals are strings, numeric
/// literals are numbers. <c>==</c>/<c>!=</c> compare numerically when both sides parse as
/// numbers and as case-insensitive text otherwise (so <c>{{x.status}} == 'Succeeded'</c> and
/// <c>== 'succeeded'</c> both work); ordering operators compare numerically when possible and
/// ordinally otherwise. <c>null</c> equals <c>null</c> and the empty string. Truthiness: null is
/// false; a string is true unless empty/whitespace or, ignoring case, <c>false</c>, <c>0</c>,
/// <c>no</c>, <c>off</c>, <c>null</c>; a number is true unless zero.
/// </para>
/// </remarks>
public static class ConditionEvaluator
{
	private static readonly TimeSpan s_regexTimeout = TimeSpan.FromSeconds(1);

	private static readonly HashSet<string> s_functions = new(StringComparer.OrdinalIgnoreCase)
	{
		"contains", "startsWith", "endsWith", "matches", "empty", "lower", "trim", "length",
		"always", "success", "failure",
	};

	private static readonly HashSet<string> s_outcomeFunctions = new(StringComparer.OrdinalIgnoreCase)
	{
		"always", "success", "failure",
	};

	/// <summary>
	/// Evaluates <paramref name="expression"/> to a boolean.
	/// </summary>
	/// <param name="expression">The condition text.</param>
	/// <param name="resolveTemplate">
	/// Resolves a template token (the full <c>{{...}}</c> text) to its string value, or returns
	/// null when the reference cannot be resolved.
	/// </param>
	/// <param name="context">Dependency outcome facts for <c>success()</c>/<c>failure()</c>.</param>
	/// <exception cref="ConditionSyntaxException">The expression is malformed.</exception>
	public static bool Evaluate(string expression, Func<string, string?> resolveTemplate, ConditionContext? context = null)
	{
		ArgumentNullException.ThrowIfNull(expression);
		ArgumentNullException.ThrowIfNull(resolveTemplate);

		var parser = new Parser(Tokenizer.Tokenize(expression), resolveTemplate, context ?? ConditionContext.None);
		var value = parser.ParseExpression();
		parser.ExpectEnd();
		return IsTruthy(value);
	}

	/// <summary>
	/// Checks the expression for syntax errors without evaluating it. Template tokens are
	/// accepted without being resolved. Returns null when the expression is well-formed.
	/// </summary>
	public static string? Validate(string expression)
	{
		if (string.IsNullOrWhiteSpace(expression))
			return "Condition is empty.";

		try
		{
			var parser = new Parser(Tokenizer.Tokenize(expression), _ => null, ConditionContext.None, validateOnly: true);
			parser.ParseExpression();
			parser.ExpectEnd();
			return null;
		}
		catch (ConditionSyntaxException ex)
		{
			return ex.Message;
		}
	}

	/// <summary>
	/// Whether the condition reasons about dependency outcomes — it calls <c>always()</c>,
	/// <c>success()</c> or <c>failure()</c>, or references a step's <c>status</c> or
	/// <c>errorMessage</c>. Such a step must have its condition evaluated even when a
	/// dependency failed; otherwise the condition could never be true. Returns false for an
	/// unparseable expression (the evaluator will report the syntax error).
	/// </summary>
	public static bool ReferencesDependencyOutcome(string expression)
	{
		if (string.IsNullOrWhiteSpace(expression))
			return false;

		try
		{
			foreach (var token in Tokenizer.Tokenize(expression))
			{
				if (token.Kind == TokenKind.Identifier && s_outcomeFunctions.Contains(token.Text))
					return true;

				if (token.Kind == TokenKind.Template)
				{
					var inner = token.Text[2..^2].Trim();
					var lastDot = inner.LastIndexOf('.');
					if (lastDot > 0)
					{
						var property = inner[(lastDot + 1)..];
						if (property.Equals("status", StringComparison.OrdinalIgnoreCase)
							|| property.Equals("errorMessage", StringComparison.OrdinalIgnoreCase))
						{
							return true;
						}
					}
				}
			}
		}
		catch (ConditionSyntaxException)
		{
			return false;
		}

		return false;
	}

	/// <summary>
	/// Returns every template token (<c>{{...}}</c>) that appears in the expression, so the
	/// template-expression validator can check the references inside a condition.
	/// </summary>
	public static IReadOnlyList<string> ExtractTemplates(string expression)
	{
		if (string.IsNullOrWhiteSpace(expression))
			return [];

		try
		{
			return Tokenizer.Tokenize(expression)
				.Where(t => t.Kind == TokenKind.Template)
				.Select(t => t.Text)
				.ToArray();
		}
		catch (ConditionSyntaxException)
		{
			return [];
		}
	}

	/// <summary>The truthiness rule described in the type remarks.</summary>
	public static bool IsTruthy(object? value)
	{
		return value switch
		{
			null => false,
			bool b => b,
			double d => d != 0 && !double.IsNaN(d),
			string s => !string.IsNullOrWhiteSpace(s)
				&& !s.Trim().Equals("false", StringComparison.OrdinalIgnoreCase)
				&& !s.Trim().Equals("0", StringComparison.Ordinal)
				&& !s.Trim().Equals("no", StringComparison.OrdinalIgnoreCase)
				&& !s.Trim().Equals("off", StringComparison.OrdinalIgnoreCase)
				&& !s.Trim().Equals("null", StringComparison.OrdinalIgnoreCase),
			_ => true,
		};
	}

	#region Tokenizer

	private enum TokenKind
	{
		String,
		Number,
		True,
		False,
		Null,
		Template,
		Identifier,
		LParen,
		RParen,
		Comma,
		Not,
		And,
		Or,
		Eq,
		Ne,
		Lt,
		Le,
		Gt,
		Ge,
		End,
	}

	private readonly record struct Token(TokenKind Kind, string Text, int Position);

	private static class Tokenizer
	{
		public static List<Token> Tokenize(string input)
		{
			var tokens = new List<Token>();
			var i = 0;

			while (i < input.Length)
			{
				var c = input[i];
				if (char.IsWhiteSpace(c))
				{
					i++;
					continue;
				}

				var start = i + 1; // 1-based for messages

				// Template token: {{ ... }}
				if (c == '{' && i + 1 < input.Length && input[i + 1] == '{')
				{
					var close = input.IndexOf("}}", i + 2, StringComparison.Ordinal);
					if (close < 0)
						throw new ConditionSyntaxException("Unterminated template reference '{{'", start);
					tokens.Add(new Token(TokenKind.Template, input.Substring(i, close + 2 - i), start));
					i = close + 2;
					continue;
				}

				if (c is '\'' or '"')
				{
					tokens.Add(new Token(TokenKind.String, ReadString(input, ref i), start));
					continue;
				}

				if (char.IsDigit(c) || (c == '-' && i + 1 < input.Length && char.IsDigit(input[i + 1])))
				{
					var j = i + 1;
					while (j < input.Length && (char.IsDigit(input[j]) || input[j] == '.'))
						j++;
					var text = input[i..j];
					if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
						throw new ConditionSyntaxException($"Invalid number '{text}'", start);
					tokens.Add(new Token(TokenKind.Number, text, start));
					i = j;
					continue;
				}

				if (char.IsLetter(c) || c == '_')
				{
					var j = i + 1;
					while (j < input.Length && (char.IsLetterOrDigit(input[j]) || input[j] == '_'))
						j++;
					var word = input[i..j];
					var kind = word.ToLowerInvariant() switch
					{
						"true" => TokenKind.True,
						"false" => TokenKind.False,
						"null" => TokenKind.Null,
						"and" => TokenKind.And,
						"or" => TokenKind.Or,
						"not" => TokenKind.Not,
						_ => TokenKind.Identifier,
					};
					tokens.Add(new Token(kind, word, start));
					i = j;
					continue;
				}

				switch (c)
				{
					case '(': tokens.Add(new Token(TokenKind.LParen, "(", start)); i++; continue;
					case ')': tokens.Add(new Token(TokenKind.RParen, ")", start)); i++; continue;
					case ',': tokens.Add(new Token(TokenKind.Comma, ",", start)); i++; continue;
					case '!':
						if (Peek(input, i + 1) == '=') { tokens.Add(new Token(TokenKind.Ne, "!=", start)); i += 2; }
						else { tokens.Add(new Token(TokenKind.Not, "!", start)); i++; }
						continue;
					case '=':
						if (Peek(input, i + 1) == '=') { tokens.Add(new Token(TokenKind.Eq, "==", start)); i += 2; continue; }
						throw new ConditionSyntaxException("Use '==' for equality", start);
					case '<':
						if (Peek(input, i + 1) == '=') { tokens.Add(new Token(TokenKind.Le, "<=", start)); i += 2; }
						else { tokens.Add(new Token(TokenKind.Lt, "<", start)); i++; }
						continue;
					case '>':
						if (Peek(input, i + 1) == '=') { tokens.Add(new Token(TokenKind.Ge, ">=", start)); i += 2; }
						else { tokens.Add(new Token(TokenKind.Gt, ">", start)); i++; }
						continue;
					case '&':
						if (Peek(input, i + 1) == '&') { tokens.Add(new Token(TokenKind.And, "&&", start)); i += 2; continue; }
						throw new ConditionSyntaxException("Use '&&' for logical and", start);
					case '|':
						if (Peek(input, i + 1) == '|') { tokens.Add(new Token(TokenKind.Or, "||", start)); i += 2; continue; }
						throw new ConditionSyntaxException("Use '||' for logical or", start);
				}

				throw new ConditionSyntaxException($"Unexpected character '{c}'", start);
			}

			tokens.Add(new Token(TokenKind.End, string.Empty, input.Length + 1));
			return tokens;
		}

		private static char Peek(string input, int index) => index < input.Length ? input[index] : '\0';

		private static string ReadString(string input, ref int i)
		{
			var quote = input[i];
			var start = i + 1;
			var sb = new StringBuilder();
			i++;
			while (i < input.Length)
			{
				var c = input[i];

				// A backslash escapes only the active quote character or another backslash.
				// Every other backslash is literal, so regular expressions such as '\d+' or
				// '^\s*ok' can be written without doubling.
				if (c == '\\' && i + 1 < input.Length && (input[i + 1] == quote || input[i + 1] == '\\'))
				{
					sb.Append(input[i + 1]);
					i += 2;
					continue;
				}

				if (c == quote)
				{
					i++;
					return sb.ToString();
				}

				sb.Append(c);
				i++;
			}

			throw new ConditionSyntaxException("Unterminated string literal", start);
		}
	}

	#endregion

	#region Parser / evaluator

	private sealed class Parser
	{
		private readonly List<Token> _tokens;
		private readonly Func<string, string?> _resolve;
		private readonly ConditionContext _context;
		private readonly bool _validateOnly;
		private int _pos;

		public Parser(List<Token> tokens, Func<string, string?> resolve, ConditionContext context, bool validateOnly = false)
		{
			_tokens = tokens;
			_resolve = resolve;
			_context = context;
			_validateOnly = validateOnly;
		}

		private Token Current => _tokens[_pos];

		private Token Advance() => _tokens[_pos++];

		public void ExpectEnd()
		{
			if (Current.Kind != TokenKind.End)
				throw new ConditionSyntaxException($"Unexpected '{Current.Text}'", Current.Position);
		}

		public object? ParseExpression() => ParseOr();

		private object? ParseOr()
		{
			var left = ParseAnd();
			while (Current.Kind == TokenKind.Or)
			{
				Advance();
				var right = ParseAnd();
				left = IsTruthy(left) || IsTruthy(right);
			}
			return left;
		}

		private object? ParseAnd()
		{
			var left = ParseNot();
			while (Current.Kind == TokenKind.And)
			{
				Advance();
				var right = ParseNot();
				left = IsTruthy(left) && IsTruthy(right);
			}
			return left;
		}

		private object? ParseNot()
		{
			if (Current.Kind == TokenKind.Not)
			{
				Advance();
				return !IsTruthy(ParseNot());
			}
			return ParseComparison();
		}

		private object? ParseComparison()
		{
			var left = ParseOperand();
			var op = Current.Kind;
			if (op is TokenKind.Eq or TokenKind.Ne or TokenKind.Lt or TokenKind.Le or TokenKind.Gt or TokenKind.Ge)
			{
				Advance();
				var right = ParseOperand();
				return Compare(op, left, right);
			}
			return left;
		}

		private object? ParseOperand()
		{
			var token = Current;
			switch (token.Kind)
			{
				case TokenKind.String:
					Advance();
					return token.Text;
				case TokenKind.Number:
					Advance();
					return double.Parse(token.Text, NumberStyles.Float, CultureInfo.InvariantCulture);
				case TokenKind.True:
					Advance();
					return true;
				case TokenKind.False:
					Advance();
					return false;
				case TokenKind.Null:
					Advance();
					return null;
				case TokenKind.Template:
					Advance();
					return _validateOnly ? null : _resolve(token.Text);
				case TokenKind.LParen:
				{
					Advance();
					var inner = ParseOr();
					Expect(TokenKind.RParen, "')'");
					return inner;
				}
				case TokenKind.Identifier:
				{
					if (!s_functions.Contains(token.Text))
						throw new ConditionSyntaxException(
							$"Unknown identifier '{token.Text}'. Quote string values ('{token.Text}') or use a template reference ({{{{param.{token.Text}}}}})",
							token.Position);
					Advance();
					Expect(TokenKind.LParen, "'(' after function name");
					var args = new List<object?>();
					if (Current.Kind != TokenKind.RParen)
					{
						args.Add(ParseOr());
						while (Current.Kind == TokenKind.Comma)
						{
							Advance();
							args.Add(ParseOr());
						}
					}
					Expect(TokenKind.RParen, "')'");
					return CallFunction(token, args);
				}
				case TokenKind.End:
					throw new ConditionSyntaxException("Unexpected end of expression", token.Position);
				default:
					throw new ConditionSyntaxException($"Unexpected '{token.Text}'", token.Position);
			}
		}

		private void Expect(TokenKind kind, string what)
		{
			if (Current.Kind != kind)
				throw new ConditionSyntaxException($"Expected {what} but found '{(Current.Kind == TokenKind.End ? "end of expression" : Current.Text)}'", Current.Position);
			Advance();
		}

		private object? CallFunction(Token name, List<object?> args)
		{
			void Arity(int n)
			{
				if (args.Count != n)
					throw new ConditionSyntaxException($"{name.Text}() takes {n} argument{(n == 1 ? "" : "s")} but got {args.Count}", name.Position);
			}

			switch (name.Text.ToLowerInvariant())
			{
				// Outcome functions (GitHub Actions vocabulary). `failure()` is true when any
				// direct dependency did not succeed; `success()` is its complement; `always()`
				// is true and exists to spell out "run regardless" (its presence also suspends
				// the dependency-failure cascade, see ReferencesDependencyOutcome).
				case "always":
					Arity(0);
					return true;
				case "success":
					Arity(0);
					return !_context.AnyDependencyFailed;
				case "failure":
					Arity(0);
					return _context.AnyDependencyFailed;
				case "contains":
					Arity(2);
					return Str(args[0]).Contains(Str(args[1]), StringComparison.OrdinalIgnoreCase);
				case "startswith":
					Arity(2);
					return Str(args[0]).StartsWith(Str(args[1]), StringComparison.OrdinalIgnoreCase);
				case "endswith":
					Arity(2);
					return Str(args[0]).EndsWith(Str(args[1]), StringComparison.OrdinalIgnoreCase);
				case "matches":
				{
					Arity(2);
					if (_validateOnly)
						return false;
					try
					{
						return Regex.IsMatch(Str(args[0]), Str(args[1]), RegexOptions.None, s_regexTimeout);
					}
					catch (ArgumentException ex)
					{
						throw new ConditionSyntaxException($"matches(): invalid regular expression: {ex.Message}", name.Position);
					}
				}
				case "empty":
					Arity(1);
					return args[0] is null || (args[0] is string s && string.IsNullOrWhiteSpace(s));
				case "lower":
					Arity(1);
					return args[0] is null ? null : Str(args[0]).ToLowerInvariant();
				case "trim":
					Arity(1);
					return args[0] is null ? null : Str(args[0]).Trim();
				case "length":
					Arity(1);
					return (double)Str(args[0]).Length;
				default:
					throw new ConditionSyntaxException($"Unknown function '{name.Text}'", name.Position);
			}
		}

		private static string Str(object? value) => value switch
		{
			null => string.Empty,
			string s => s,
			double d => d.ToString("R", CultureInfo.InvariantCulture),
			bool b => b ? "true" : "false",
			_ => value.ToString() ?? string.Empty,
		};

		private static bool Compare(TokenKind op, object? left, object? right)
		{
			// Numeric comparison when both sides are numbers (or numeric text).
			if (TryNumber(left, out var l) && TryNumber(right, out var r))
			{
				return op switch
				{
					TokenKind.Eq => l == r,
					TokenKind.Ne => l != r,
					TokenKind.Lt => l < r,
					TokenKind.Le => l <= r,
					TokenKind.Gt => l > r,
					TokenKind.Ge => l >= r,
					_ => throw new InvalidOperationException(),
				};
			}

			var ls = left is null ? null : Str(left);
			var rs = right is null ? null : Str(right);

			if (op is TokenKind.Eq or TokenKind.Ne)
			{
				// null equals null and the empty string; text compares case-insensitively.
				var equal = (ls, rs) switch
				{
					(null, null) => true,
					(null, var b) => b.Length == 0,
					(var a, null) => a.Length == 0,
					var (a, b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase),
				};
				return op == TokenKind.Eq ? equal : !equal;
			}

			var cmp = string.CompareOrdinal(ls ?? string.Empty, rs ?? string.Empty);
			return op switch
			{
				TokenKind.Lt => cmp < 0,
				TokenKind.Le => cmp <= 0,
				TokenKind.Gt => cmp > 0,
				TokenKind.Ge => cmp >= 0,
				_ => throw new InvalidOperationException(),
			};
		}

		private static bool TryNumber(object? value, out double number)
		{
			switch (value)
			{
				case double d:
					number = d;
					return true;
				case bool:
					number = 0;
					return false;
				case string s when double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed):
					number = parsed;
					return true;
				default:
					number = 0;
					return false;
			}
		}
	}

	#endregion
}
