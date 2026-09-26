using FluentAssertions;

namespace Orchestra.Engine.Tests.Conditions;

public class ConditionEvaluatorTests
{
	private static bool Eval(string expression, Dictionary<string, string?>? templates = null)
		=> ConditionEvaluator.Evaluate(expression, token => templates is not null && templates.TryGetValue(token, out var v) ? v : null);

	#region Literals and truthiness

	[Theory]
	[InlineData("true", true)]
	[InlineData("false", false)]
	[InlineData("TRUE", true)]
	[InlineData("null", false)]
	[InlineData("1", true)]
	[InlineData("0", false)]
	[InlineData("-2.5", true)]
	[InlineData("'yes'", true)]
	[InlineData("''", false)]
	[InlineData("'   '", false)]
	[InlineData("'false'", false)]
	[InlineData("'FALSE'", false)]
	[InlineData("'0'", false)]
	[InlineData("'no'", false)]
	[InlineData("'off'", false)]
	[InlineData("'null'", false)]
	[InlineData("\"double quoted\"", true)]
	public void Literals_HaveDocumentedTruthiness(string expression, bool expected)
	{
		Eval(expression).Should().Be(expected);
	}

	[Fact]
	public void TemplateToken_IsResolvedThroughTheCallback_NotSpliced()
	{
		// The output contains quotes and an operator; because templates are tokens, none of
		// that can alter the expression's structure.
		var templates = new Dictionary<string, string?> { ["{{review.output}}"] = "it's 'fine' && done == yes" };

		Eval("{{review.output}} == \"it's 'fine' && done == yes\"", templates).Should().BeTrue();
		Eval("contains({{review.output}}, '&&')", templates).Should().BeTrue();
	}

	[Fact]
	public void UnresolvedTemplate_IsNull_AndFalsy()
	{
		Eval("{{missing.output}}").Should().BeFalse();
		Eval("!{{missing.output}}").Should().BeTrue();
		Eval("{{missing.output}} == null").Should().BeTrue();
		Eval("{{missing.output}} == ''").Should().BeTrue("null and empty compare equal");
		Eval("empty({{missing.output}})").Should().BeTrue();
	}

	#endregion

	#region Comparisons

	[Theory]
	[InlineData("'a' == 'a'", true)]
	[InlineData("'a' == 'A'", true)]        // case-insensitive text equality
	[InlineData("' a ' == 'a'", true)]      // trimmed
	[InlineData("'a' != 'b'", true)]
	[InlineData("1 == 1.0", true)]
	[InlineData("'10' == 10", true)]        // numeric text compares numerically
	[InlineData("'10' > '9'", true)]        // numeric, not lexical
	[InlineData("'abc' > 'abd'", false)]    // lexical when not numeric
	[InlineData("2 < 3", true)]
	[InlineData("3 <= 3", true)]
	[InlineData("3 >= 4", false)]
	[InlineData("'apple' < 'banana'", true)]
	[InlineData("null == null", true)]
	[InlineData("null != 'x'", true)]
	[InlineData("true == 'true'", true)]
	[InlineData("false == 'no'", false)]    // 'no' is falsy but not the text "false"
	public void Comparisons(string expression, bool expected)
	{
		Eval(expression).Should().Be(expected);
	}

	[Fact]
	public void StatusComparison_IsCaseInsensitive_SoBothSpellingsWork()
	{
		var templates = new Dictionary<string, string?> { ["{{tests.status}}"] = "succeeded" };
		Eval("{{tests.status}} == 'succeeded'", templates).Should().BeTrue();
		Eval("{{tests.status}} == 'Succeeded'", templates).Should().BeTrue();
		Eval("{{tests.status}} != 'failed'", templates).Should().BeTrue();
	}

	#endregion

	#region Boolean operators and precedence

	[Theory]
	[InlineData("true && false", false)]
	[InlineData("true || false", true)]
	[InlineData("!true", false)]
	[InlineData("!!true", true)]
	[InlineData("true and false", false)]
	[InlineData("true or false", true)]
	[InlineData("not true", false)]
	[InlineData("true || false && false", true)]   // && binds tighter than ||
	[InlineData("(true || false) && false", false)]
	[InlineData("!false && 1 == 1", true)]
	[InlineData("'a' == 'a' && 2 > 1 || false", true)]
	public void BooleanOperators_RespectPrecedence(string expression, bool expected)
	{
		Eval(expression).Should().Be(expected);
	}

	#endregion

	#region Functions

	[Theory]
	[InlineData("contains('Hello World', 'world')", true)]
	[InlineData("contains('Hello', 'xyz')", false)]
	[InlineData("startsWith('Hello', 'he')", true)]
	[InlineData("endsWith('Hello', 'LLO')", true)]
	[InlineData("matches('abc123', '^[a-z]+\\d+$')", true)]
	[InlineData("matches('abc', '^\\d+$')", false)]
	[InlineData("empty('')", true)]
	[InlineData("empty('  ')", true)]
	[InlineData("empty('x')", false)]
	[InlineData("empty(null)", true)]
	[InlineData("lower('ABC') == 'abc'", true)]
	[InlineData("trim('  x  ') == 'x'", true)]
	[InlineData("length('abcd') == 4", true)]
	[InlineData("length('') == 0", true)]
	[InlineData("length(null) == 0", true)]
	[InlineData("StartsWith('x', 'x')", true)]     // function names are case-insensitive
	public void Functions(string expression, bool expected)
	{
		Eval(expression).Should().Be(expected);
	}

	[Fact]
	public void Functions_ComposeWithTemplatesAndOperators()
	{
		var templates = new Dictionary<string, string?>
		{
			["{{review.output}}"] = "  APPROVED: ship it  ",
			["{{param.env}}"] = "prod",
		};

		Eval("startsWith(trim({{review.output}}), 'approved') && {{param.env}} == 'prod'", templates).Should().BeTrue();
		Eval("length({{review.output}}) > 100", templates).Should().BeFalse();
	}

	[Fact]
	public void StringLiterals_EscapeOnlyTheQuoteAndBackslash_SoRegexesNeedNoDoubling()
	{
		Eval(@"matches('a1', '^\w\d$')").Should().BeTrue(@"'\w' and '\d' must reach the regex engine intact");
		Eval(@"'it\'s' == ""it's""").Should().BeTrue(@"\' escapes the active quote");
		Eval(@"'back\\slash' == 'back\\slash'").Should().BeTrue(@"\\ is a single backslash");
		Eval(@"length('a\\b') == 3").Should().BeTrue();
		Eval(@"length('a\nb') == 4").Should().BeTrue(@"\n is not an escape: backslash and n are both literal");
	}

	#endregion

	#region Syntax errors

	[Theory]
	[InlineData("", "empty")]
	[InlineData("succeeded", "Unknown identifier")]           // bareword: hint to quote or use a template
	[InlineData("'unterminated", "Unterminated string")]
	[InlineData("{{a.output", "Unterminated template")]
	[InlineData("1 = 1", "Use '=='")]
	[InlineData("true & false", "Use '&&'")]
	[InlineData("true | false", "Use '||'")]
	[InlineData("(true", "Expected ')'")]
	[InlineData("true)", "Unexpected ')'")]
	[InlineData("contains('a')", "takes 2 arguments")]
	[InlineData("frobnicate('a')", "Unknown identifier")]
	[InlineData("1 ==", "Unexpected end")]
	[InlineData("@", "Unexpected character")]
	public void Validate_ReportsSyntaxErrors(string expression, string expectedFragment)
	{
		var error = ConditionEvaluator.Validate(expression);
		error.Should().NotBeNull();
		error.Should().Contain(expectedFragment);
	}

	[Theory]
	[InlineData("true")]
	[InlineData("{{a.status}} == 'succeeded' && !empty({{b.output}})")]
	[InlineData("matches({{x.output}}, '^ok') || {{param.force}}")]
	public void Validate_AcceptsWellFormedExpressions_WithoutResolvingTemplates(string expression)
	{
		ConditionEvaluator.Validate(expression).Should().BeNull();
	}

	[Fact]
	public void Evaluate_ThrowsConditionSyntaxException_WithPosition()
	{
		var act = () => Eval("true &&& false");
		act.Should().Throw<ConditionSyntaxException>().Which.Position.Should().BeGreaterThan(0);
	}

	[Fact]
	public void Evaluate_InvalidRegex_IsASyntaxError()
	{
		var act = () => Eval("matches('abc', '(unclosed')");
		act.Should().Throw<ConditionSyntaxException>().WithMessage("*invalid regular expression*");
	}

	#endregion

	#region Outcome functions and cascade detection

	[Theory]
	[InlineData("always()", false, true)]
	[InlineData("always()", true, true)]
	[InlineData("success()", false, true)]
	[InlineData("success()", true, false)]
	[InlineData("failure()", false, false)]
	[InlineData("failure()", true, true)]
	[InlineData("failure() || {{param.force}}", false, false)]
	[InlineData("!failure()", true, false)]
	public void OutcomeFunctions_ReflectTheDependencyContext(string expression, bool anyDependencyFailed, bool expected)
	{
		ConditionEvaluator.Evaluate(expression, _ => null, new ConditionContext(anyDependencyFailed)).Should().Be(expected);
	}

	[Fact]
	public void OutcomeFunctions_TakeNoArguments()
	{
		ConditionEvaluator.Validate("failure('x')").Should().Contain("takes 0 arguments");
	}

	[Theory]
	[InlineData("failure()", true)]
	[InlineData("success() && {{param.x}}", true)]
	[InlineData("always()", true)]
	[InlineData("{{tests.status}} == 'failed'", true)]
	[InlineData("{{tests.STATUS}} == 'failed'", true)]
	[InlineData("!empty({{build.errorMessage}})", true)]
	[InlineData("{{param.deploy}} == 'yes'", false)]
	[InlineData("{{build.output}} == 'ok'", false)]
	[InlineData("contains({{build.output}}, 'status')", false)]   // 'status' as text is not a reference
	[InlineData("'{{x.status}}' == 'failed'", false)]              // inside a string literal: not a reference
	[InlineData("true &&& broken", false)]                         // unparseable: false, evaluator reports the error
	public void ReferencesDependencyOutcome_DetectsOutcomeAwareConditions(string expression, bool expected)
	{
		ConditionEvaluator.ReferencesDependencyOutcome(expression).Should().Be(expected);
	}

	#endregion

	#region ExtractTemplates

	[Fact]
	public void ExtractTemplates_ReturnsEveryTemplateToken()
	{
		var templates = ConditionEvaluator.ExtractTemplates("{{a.status}} == 'ok' && contains({{b.output}}, {{param.needle}})");
		templates.Should().Equal("{{a.status}}", "{{b.output}}", "{{param.needle}}");
	}

	[Fact]
	public void ExtractTemplates_IgnoresTemplatesInsideStringLiterals()
	{
		// Inside quotes the braces are literal text, not a reference.
		ConditionEvaluator.ExtractTemplates("'{{not.a.ref}}' == 'x'").Should().BeEmpty();
	}

	#endregion
}
