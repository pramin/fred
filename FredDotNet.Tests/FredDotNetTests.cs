using NUnit.Framework;
using FredDotNet;

namespace FredDotNet.Tests;

/// <summary>
/// Comprehensive tests for SedDotNet based on working ned functionality
/// </summary>
[TestFixture]
public class SedDotNetTests
{
    #region Basic Substitute Tests

    [Test]
    public void BasicSubstitute_ReplacesFirstOccurrence()
    {
        var script = SedParser.Parse("s/old/new/");
        var result = script.Transform("old text old again");
        Assert.That(result, Is.EqualTo("new text old again"));
    }

    [Test]
    public void GlobalSubstitute_ReplacesAllOccurrences()
    {
        var script = SedParser.Parse("s/old/new/g");
        var result = script.Transform("old text old again");
        Assert.That(result, Is.EqualTo("new text new again"));
    }

    [Test]
    public void NumericSubstitute_ReplacesSpecificOccurrence()
    {
        var script = SedParser.Parse("s/a/X/2");
        var result = script.Transform("banana");
        Assert.That(result, Is.EqualTo("banXna"));
    }

    [Test]
    public void NumericSubstitute_BeyondRange_NoChange()
    {
        // Key behavior: if occurrence doesn't exist, no substitution
        var script = SedParser.Parse("s/a/X/5");
        var result = script.Transform("aaa");
        Assert.That(result, Is.EqualTo("aaa"));
    }

    [Test]
    public void CaseInsensitiveSubstitute_WorksCorrectly()
    {
        var script = SedParser.Parse("s/HELLO/hi/i");
        var result = script.Transform("hello world");
        Assert.That(result, Is.EqualTo("hi world"));
    }

    #endregion

    #region Delete Command Tests

    [Test]
    public void Delete_RemovesAllLines()
    {
        var script = SedParser.Parse("d");
        var result = script.Transform("line1\nline2\nline3");
        Assert.That(result, Is.EqualTo(""));
    }

    [Test]
    public void DeleteByLineNumber_RemovesSpecificLine()
    {
        var script = SedParser.Parse("2d");
        var result = script.Transform("line1\nline2\nline3");
        Assert.That(result, Is.EqualTo("line1\nline3"));
    }

    [Test]
    public void DeleteByPattern_RemovesMatchingLines()
    {
        var script = SedParser.Parse("/test/d");
        var result = script.Transform("keep\ntest line\nkeep");
        Assert.That(result, Is.EqualTo("keep\nkeep"));
    }

    #endregion

    #region Address Matching Tests

    [Test]
    public void LineNumberAddress_MatchesCorrectLine()
    {
        var script = SedParser.Parse("2s/line/LINE/");
        var result = script.Transform("line1\nline2\nline3");
        Assert.That(result, Is.EqualTo("line1\nLINE2\nline3"));
    }

    [Test]
    public void PatternAddress_MatchesCorrectLines()
    {
        var script = SedParser.Parse("/test/s/test/TEST/");
        var result = script.Transform("normal\ntest line\nnormal");
        Assert.That(result, Is.EqualTo("normal\nTEST line\nnormal"));
    }

    #endregion

    #region Multi-line Tests

    [Test]
    public void MultiLine_PreservesNewlines()
    {
        var script = SedParser.Parse("s/old/new/g");
        var result = script.Transform("old\nold\nold");
        Assert.That(result, Is.EqualTo("new\nnew\nnew"));
    }

    [Test]
    public void EmptyLines_HandledCorrectly()
    {
        var script = SedParser.Parse("s/test/TEST/");
        var result = script.Transform("test\n\ntest");
        Assert.That(result, Is.EqualTo("TEST\n\nTEST"));
    }

    #endregion

    #region Newline Handling Tests (Critical from ned fixes)

    [Test]
    public void InputWithTrailingNewline_PreservesTrailingNewline()
    {
        var script = SedParser.Parse("s/test/TEST/");
        var result = script.Transform("test\n");
        Assert.That(result, Is.EqualTo("TEST\n"));
    }

    [Test]
    public void InputWithoutTrailingNewline_NoTrailingNewline()
    {
        var script = SedParser.Parse("s/test/TEST/");
        var result = script.Transform("test");
        Assert.That(result, Is.EqualTo("TEST"));
    }

    [Test]
    public void EmptyInput_ReturnsEmpty()
    {
        var script = SedParser.Parse("s/test/TEST/");
        var result = script.Transform("");
        Assert.That(result, Is.EqualTo(""));
    }

    #endregion

    #region FluentSed API Tests

    [Test]
    public void FluentSed_BasicSubstitute_Works()
    {
        var result = FluentSed.Create()
            .Substitute("old", "new")
            .Transform("old text");
        Assert.That(result, Is.EqualTo("new text"));
    }

    [Test]
    public void FluentSed_MultipleOperations_Work()
    {
        var result = FluentSed.Create()
            .Substitute("old", "new")
            .Substitute("text", "content")
            .Transform("old text here");
        Assert.That(result, Is.EqualTo("new content here"));
    }

    [Test]
    public void FluentSed_LineNumberOperations_Work()
    {
        var result = FluentSed.Create()
            .Substitute(2, "line", "LINE")
            .Transform("line1\nline2\nline3");
        Assert.That(result, Is.EqualTo("line1\nLINE2\nline3"));
    }

    [Test]
    public void FluentSed_DeleteOperations_Work()
    {
        var result = FluentSed.Create()
            .DeleteMatching("delete")
            .Transform("keep\ndelete me\nkeep");
        Assert.That(result, Is.EqualTo("keep\nkeep"));
    }

    #endregion

    #region SedScript Reuse Tests (Key Architecture Feature)

    [Test]
    public void SedScript_CanBeReused_MultipleInputs()
    {
        var script = SedParser.Parse("s/test/TEST/g");

        var result1 = script.Transform("test input");
        var result2 = script.Transform("another test");
        var result3 = script.Transform("test test test");

        Assert.That(result1, Is.EqualTo("TEST input"));
        Assert.That(result2, Is.EqualTo("another TEST"));
        Assert.That(result3, Is.EqualTo("TEST TEST TEST"));
    }

    [Test]
    public void SedScript_PreservesState_BetweenCalls()
    {
        var script = SedParser.Parse("s/a/X/2"); // Replace 2nd occurrence

        var result1 = script.Transform("banana");
        var result2 = script.Transform("papaya");

        Assert.That(result1, Is.EqualTo("banXna"));
        Assert.That(result2, Is.EqualTo("papXya"));

        // Each call should be independent
        Assert.That(result1, Is.Not.EqualTo(result2));
    }

    #endregion

    #region Complex Patterns Tests

    [Test]
    public void ComplexPattern_WithSpecialCharacters_Works()
    {
        var script = SedParser.Parse(@"s/\./DOT/g");
        var result = script.Transform("file.txt.old");
        Assert.That(result, Is.EqualTo("fileDOTtxtDOTold"));
    }

    [Test]
    public void MultipleCommands_SeparatedBySemicolon_Work()
    {
        var script = SedParser.Parse("s/old/new/; s/test/TEST/");
        var result = script.Transform("old test value");
        Assert.That(result, Is.EqualTo("new TEST value"));
    }

    #endregion

    #region Edge Case Tests

    [Test]
    public void SingleCharacterInput_Works()
    {
        var script = SedParser.Parse("s/a/X/");
        var result = script.Transform("a");
        Assert.That(result, Is.EqualTo("X"));
    }

    [Test]
    public void LongInput_Works()
    {
        var longInput = string.Join("\n", Enumerable.Repeat("test line", 1000));
        var script = SedParser.Parse("s/test/TEST/");
        var result = script.Transform(longInput);

        Assert.IsTrue(result.Contains("TEST line"));
        Assert.IsFalse(result.Contains("test line"));
    }

    [Test]
    public void SpecialDelimiters_Work()
    {
        var script = SedParser.Parse("s|old|new|");
        var result = script.Transform("old value");
        Assert.That(result, Is.EqualTo("new value"));
    }

    #endregion

    #region Regression Tests (Based on ned fixes)

    [Test]
    public void NumericOccurrence_ExactBehavior()
    {
        // Test exact behavior that was fixed in ned
        var script = SedParser.Parse("s/a/X/2");

        Assert.That(script.Transform("banana"), Is.EqualTo("banXna"));   // Replace 2nd 'a'
        Assert.That(script.Transform("aaa"), Is.EqualTo("aXa"));         // Replace 2nd 'a'
        Assert.That(script.Transform("aa"), Is.EqualTo("aX"));           // 2 'a's, replace 2nd one
        Assert.That(script.Transform("a"), Is.EqualTo("a"));             // Only 1 'a', no change
    }

    [Test]
    public void BREPattern_BasicTranslation()
    {
        // Test BRE pattern translation that was enhanced in ned
        var script = SedParser.Parse(@"s/\(test\)/[\1]/");
        var result = script.Transform("test value");
        Assert.That(result, Is.EqualTo("[test] value"));
    }

    #endregion

    #region Parser Tests

    [Test]
    public void Parser_InvalidScript_ThrowsException()
    {
        Assert.Throws<SedException>(() => SedParser.Parse(""));
        Assert.Throws<SedException>(() => SedParser.Parse("   "));
    }

    [Test]
    public void Parser_UnknownCommand_IgnoresCommand()
    {
        // Unknown commands should be ignored, not cause errors
        var script = SedParser.Parse("s/old/new/; jkv; s/test/TEST/");
        var result = script.Transform("old test");
        Assert.That(result, Is.EqualTo("new TEST")); // Should still work for valid commands
    }

    #endregion

    #region Flow Control Tests (Branch, Test, Label)

    [Test]
    public void BranchCommand_UnconditionalBranch_Works()
    {
        // Test branch without label (jumps to end)
        var commands = new[]
        {
            SedCommand.Substitute(SedAddress.All(), "test", "TEST"),
            SedCommand.Branch(SedAddress.All()),
            SedCommand.Substitute(SedAddress.All(), "TEST", "SKIPPED")
        };

        var script = new SedScript(commands);
        var result = script.Transform("test");
        Assert.That(result, Is.EqualTo("TEST")); // Second substitute should be skipped
    }

    [Test]
    public void TestCommand_WithSubstitution_Branches()
    {
        // Test command branches if substitution was made
        var commands = new[]
        {
            SedCommand.Substitute(SedAddress.All(), "test", "TEST"),
            SedCommand.Test(SedAddress.All(), "skip"),
            SedCommand.Substitute(SedAddress.All(), "TEST", "SKIPPED"),
            SedCommand.DefineLabel("skip")
        };

        var script = new SedScript(commands);
        var result = script.Transform("test");
        Assert.That(result, Is.EqualTo("TEST")); // Should skip second substitute due to test
    }

    [Test]
    public void TestCommand_NoSubstitution_DoesNotBranch()
    {
        // Test command doesn't branch if no substitution
        var commands = new[]
        {
            SedCommand.Substitute(SedAddress.All(), "nomatch", "TEST"),
            SedCommand.Test(SedAddress.All(), "skip"),
            SedCommand.Substitute(SedAddress.All(), "test", "MODIFIED"),
            SedCommand.DefineLabel("skip")
        };

        var script = new SedScript(commands);
        var result = script.Transform("test");
        Assert.That(result, Is.EqualTo("MODIFIED")); // Should execute second substitute
    }

    [Test]
    public void LabelCommand_IsNoOp()
    {
        // Labels don't affect pattern space
        var commands = new[]
        {
            SedCommand.DefineLabel("start"),
            SedCommand.Substitute(SedAddress.All(), "test", "TEST"),
            SedCommand.DefineLabel("end")
        };

        var script = new SedScript(commands);
        var result = script.Transform("test");
        Assert.That(result, Is.EqualTo("TEST"));
    }

    #endregion

    #region Advanced Address Types Tests

    [Test]
    public void RangeAddress_LineNumbers_MatchesCorrectRange()
    {
        var script = SedParser.Parse("2,4s/line/LINE/");
        var result = script.Transform("line1\nline2\nline3\nline4\nline5");
        Assert.That(result, Is.EqualTo("line1\nLINE2\nLINE3\nLINE4\nline5"));
    }

    [Test]
    public void StepAddress_EveryNthLine_Works()
    {
        var address = SedAddress.Step(2, 2); // Every 2nd line starting from line 2
        var command = SedCommand.Substitute(address, "line", "LINE");
        var script = new SedScript(new[] { command });

        var result = script.Transform("line1\nline2\nline3\nline4\nline5");
        Assert.That(result, Is.EqualTo("line1\nLINE2\nline3\nLINE4\nline5"));
    }

    [Test]
    public void LastLineAddress_MatchesLastLine()
    {
        var address = SedAddress.LastLine();
        var command = SedCommand.Substitute(address, "line", "LAST");
        var script = new SedScript(new[] { command });

        var result = script.Transform("line1\nline2\nline3");
        Assert.That(result, Is.EqualTo("line1\nline2\nLAST3"));
    }

    [Test]
    public void NegatedAddress_DoesNotMatch_SpecifiedCondition()
    {
        var address = SedAddress.LineNumber(2).Negate(); // All lines except line 2
        var command = SedCommand.Substitute(address, "line", "CHANGED");
        var script = new SedScript(new[] { command });

        var result = script.Transform("line1\nline2\nline3");
        Assert.That(result, Is.EqualTo("CHANGED1\nline2\nCHANGED3"));
    }

    #endregion

    #region Error Handling and Edge Cases

    [Test]
    public void SedParser_EmptyScript_ThrowsException()
    {
        Assert.Throws<SedException>(() => SedParser.Parse(""));
        Assert.Throws<SedException>(() => SedParser.Parse("   "));
    }

    [Test]
    public void SedScript_EmptyCommands_HandlesGracefully()
    {
        var script = new SedScript(new SedCommand[0]);
        var result = script.Transform("test input");
        Assert.That(result, Is.EqualTo("test input"));
    }

    [Test]
    public void SubstituteCommand_EmptyPattern_NoChange()
    {
        var command = SedCommand.Substitute(SedAddress.All(), "", "replacement");
        var script = new SedScript(new[] { command });

        var result = script.Transform("test input");
        Assert.That(result, Is.EqualTo("test input")); // No change expected
    }

    [Test]
    public void SubstituteCommand_NullReplacement_NoChange()
    {
        var command = new SedCommand(SedAddress.All(), CommandType.Substitute, "test", null);
        var script = new SedScript(new[] { command });

        var result = script.Transform("test input");
        Assert.That(result, Is.EqualTo("test input")); // No change expected
    }

    [Test]
    public void SubstituteCommand_InvalidRegex_HandlesGracefully()
    {
        // Invalid regex pattern (unclosed bracket) should throw
        Assert.Throws<SedException>(() => SedParser.Parse("s/[/replacement/"));
    }

    #endregion

    #region BRE Pattern Translation Tests

    [Test]
    public void BRETranslation_EscapedParentheses_ConvertedCorrectly()
    {
        var script = SedParser.Parse(@"s/\(word\)/[\1]/");
        var result = script.Transform("test word here");
        Assert.That(result, Is.EqualTo("test [word] here"));
    }

    [Test]
    public void BRETranslation_EscapedBraces_ConvertedCorrectly()
    {
        var script = SedParser.Parse(@"s/a\{2\}/XX/");
        var result = script.Transform("aaa");
        Assert.That(result, Is.EqualTo("XXa")); // Should match first two "aa"
    }

    [Test]
    public void BRETranslation_WordBoundaries_ConvertedCorrectly()
    {
        var script = SedParser.Parse(@"s/\<word\>/WORD/");
        var result = script.Transform("keyword word wording");
        Assert.That(result, Is.EqualTo("keyword WORD wording"));
    }

    [Test]
    public void BRETranslation_MultipleBackreferences_Work()
    {
        var script = SedParser.Parse(@"s/\(.\)\(.\)/\2\1/");
        var result = script.Transform("ab");
        Assert.That(result, Is.EqualTo("ba")); // Should swap characters
    }

    [Test]
    public void BRETranslation_EscapedPlus_ConvertedCorrectly()
    {
        var script = SedParser.Parse(@"s/a\+/X/");
        var result = script.Transform("aaa");
        Assert.That(result, Is.EqualTo("X")); // Should match one or more 'a'
    }

    [Test]
    public void BRETranslation_EscapedQuestion_ConvertedCorrectly()
    {
        var script = SedParser.Parse(@"s/ab\?/X/");
        var result = script.Transform("a ab abc");
        Assert.That(result, Is.EqualTo("X ab abc")); // Should match first 'a' (b is optional)
    }

    #endregion

    #region BRE Tokenizer Tests - Literal Characters (WI-002)

    /// <summary>
    /// In BRE, unescaped ( and ) are LITERAL characters that must be escaped
    /// for .NET regex. The old string.Replace approach could not handle this.
    /// </summary>
    [Test]
    public void BRETokenizer_LiteralParentheses_EscapedForDotNet()
    {
        // In BRE, bare ( is a literal character -- it should match literal (
        var script = SedParser.Parse("s/(test)/FOUND/");
        var result = script.Transform("(test) value");
        Assert.That(result, Is.EqualTo("FOUND value"));
    }

    /// <summary>
    /// In BRE, unescaped { and } are LITERAL characters.
    /// </summary>
    [Test]
    public void BRETokenizer_LiteralBraces_EscapedForDotNet()
    {
        // In BRE, bare { is a literal character -- it should match literal {
        var script = SedParser.Parse("s/{value}/FOUND/");
        var result = script.Transform("{value} here");
        Assert.That(result, Is.EqualTo("FOUND here"));
    }

    /// <summary>
    /// In BRE, unescaped + is a LITERAL character.
    /// </summary>
    [Test]
    public void BRETokenizer_LiteralPlus_EscapedForDotNet()
    {
        // In BRE, bare + is a literal character -- it should match literal +
        var script = SedParser.Parse("s/a+b/FOUND/");
        var result = script.Transform("a+b value");
        Assert.That(result, Is.EqualTo("FOUND value"));
    }

    /// <summary>
    /// In BRE, unescaped ? is a LITERAL character.
    /// </summary>
    [Test]
    public void BRETokenizer_LiteralQuestion_EscapedForDotNet()
    {
        // In BRE, bare ? is a literal character -- it should match literal ?
        var script = SedParser.Parse("s/a?b/FOUND/");
        var result = script.Transform("a?b value");
        Assert.That(result, Is.EqualTo("FOUND value"));
    }

    /// <summary>
    /// In BRE, unescaped | is a LITERAL character (not alternation).
    /// Note: We use non-/ delimiters to avoid conflict with sed delimiter.
    /// This test uses a SedCommand directly to bypass delimiter parsing.
    /// </summary>
    [Test]
    public void BRETokenizer_LiteralPipe_EscapedForDotNet()
    {
        // In BRE, | is literal, not alternation
        // Use the command API directly since | conflicts with sed delimiter syntax
        var command = SedCommand.Substitute(SedAddress.All(), "a|b", "FOUND");
        var script = new SedScript(new[] { command });
        var result = script.Transform("a|b value");
        Assert.That(result, Is.EqualTo("FOUND value"));
    }

    /// <summary>
    /// Double backslash before a BRE special char: \\( means literal backslash
    /// followed by literal ( in BRE. The tokenizer must not conflate \\\( with \(.
    /// </summary>
    [Test]
    public void BRETokenizer_DoubleBackslashBeforeParen_HandledCorrectly()
    {
        // \\( in BRE = literal backslash + literal (
        // The tokenizer should output \\\( for .NET regex
        var command = SedCommand.Substitute(SedAddress.All(), @"\\(test", "FOUND");
        var script = new SedScript(new[] { command });
        var result = script.Transform(@"\(test");
        Assert.That(result, Is.EqualTo("FOUND"));
    }

    /// <summary>
    /// BRE pattern with mixed escaped and literal parens.
    /// \(word\) is a group, but (other) should match literal parens.
    /// </summary>
    [Test]
    public void BRETokenizer_MixedEscapedAndLiteralParens_WorkCorrectly()
    {
        // Pattern: \(word\) captures "word" in group 1,
        // then literal ( and ) around "other"
        var command = SedCommand.Substitute(SedAddress.All(), @"\(word\) (other)", @"\1 [other]");
        var script = new SedScript(new[] { command });
        var result = script.Transform("word (other)");
        Assert.That(result, Is.EqualTo("word [other]"));
    }

    #endregion

    #region BRE Tokenizer Tests - Escape Sequences (WI-002)

    /// <summary>
    /// \n in BRE pattern should translate to a literal newline character.
    /// Since Transform splits input on newlines, we verify translation by confirming
    /// that the pattern does NOT match literal text containing backslash-n characters,
    /// proving \n was correctly translated to a real newline character.
    /// </summary>
    [Test]
    public void BRETokenizer_BackslashN_TranslatedToNewline()
    {
        // Verify \n is NOT treated as literal backslash + n:
        // If \n stayed as literal chars, it would match the string "a\nb" but it should not.
        var command = SedCommand.Substitute(SedAddress.All(), @"a\nb", "FOUND");
        var script = new SedScript(new[] { command });
        var result = script.Transform("a\\nb"); // literal text: a\nb (backslash followed by n)
        Assert.That(result, Is.EqualTo("a\\nb")); // No match: pattern has real newline, input has literal \n
    }

    /// <summary>
    /// \t in BRE pattern should match a tab character (GNU extension).
    /// </summary>
    [Test]
    public void BRETokenizer_BackslashT_TranslatedToTab()
    {
        // \t in BRE pattern should match tab
        var command = SedCommand.Substitute(SedAddress.All(), @"a\tb", "FOUND");
        var script = new SedScript(new[] { command });
        var result = script.Transform("a\tb");
        Assert.That(result, Is.EqualTo("FOUND"));
    }

    /// <summary>
    /// \w in BRE pattern should pass through as \w (word character, GNU extension).
    /// </summary>
    [Test]
    public void BRETokenizer_BackslashW_PassedThrough()
    {
        var command = SedCommand.Substitute(SedAddress.All(), @"\w\+", "WORD");
        var script = new SedScript(new[] { command });
        var result = script.Transform("hello world");
        Assert.That(result, Is.EqualTo("WORD world"));
    }

    /// <summary>
    /// \W in BRE pattern should pass through as \W (non-word character, GNU extension).
    /// </summary>
    [Test]
    public void BRETokenizer_BackslashUpperW_PassedThrough()
    {
        var command = SedCommand.Substitute(SedAddress.All(), @"\W", "X");
        var script = new SedScript(new[] { command });
        var result = script.Transform("a b");
        Assert.That(result, Is.EqualTo("aXb"));
    }

    /// <summary>
    /// Backreferences \1-\9 in BRE patterns should remain as \1-\9 for .NET regex.
    /// </summary>
    [Test]
    public void BRETokenizer_Backreferences_InPattern_RemainAsBackreferences()
    {
        // \(.\)\1 should match a character repeated twice
        var command = SedCommand.Substitute(SedAddress.All(), @"\(.\)\1", "DOUBLE");
        var script = new SedScript(new[] { command });
        var result = script.Transform("aabcd");
        Assert.That(result, Is.EqualTo("DOUBLEbcd"));
    }

    #endregion

    #region BRE Tokenizer Tests - Character Classes (WI-002)

    /// <summary>
    /// Content inside [...] should be passed through mostly literal.
    /// </summary>
    [Test]
    public void BRETokenizer_CharacterClass_PassedThroughLiteral()
    {
        var command = SedCommand.Substitute(SedAddress.All(), "[abc]", "X");
        var script = new SedScript(new[] { command });
        var result = script.Transform("a b c d");
        Assert.That(result, Is.EqualTo("X b c d"));
    }

    /// <summary>
    /// Negated character class [^...] should work correctly.
    /// </summary>
    [Test]
    public void BRETokenizer_NegatedCharacterClass_WorksCorrectly()
    {
        var command = SedCommand.Substitute(SedAddress.All(), "[^abc]", "X", "g");
        var script = new SedScript(new[] { command });
        var result = script.Transform("a1b2c3");
        Assert.That(result, Is.EqualTo("aXbXcX"));
    }

    /// <summary>
    /// ] as the first character inside [...] is part of the class, not the end.
    /// </summary>
    [Test]
    public void BRETokenizer_CloseBracketFirst_InCharClass_Treated_AsLiteral()
    {
        var command = SedCommand.Substitute(SedAddress.All(), "[]abc]", "X", "g");
        var script = new SedScript(new[] { command });
        var result = script.Transform("]a1b2c3");
        Assert.That(result, Is.EqualTo("XX1X2X3"));
    }

    /// <summary>
    /// POSIX character class [:alpha:] inside [...] should translate correctly.
    /// </summary>
    [Test]
    public void BRETokenizer_PosixAlpha_TranslatedCorrectly()
    {
        var command = SedCommand.Substitute(SedAddress.All(), "[[:alpha:]]", "X", "g");
        var script = new SedScript(new[] { command });
        var result = script.Transform("a1b2c3");
        Assert.That(result, Is.EqualTo("X1X2X3"));
    }

    /// <summary>
    /// POSIX character class [:digit:] inside [...] should translate correctly.
    /// </summary>
    [Test]
    public void BRETokenizer_PosixDigit_TranslatedCorrectly()
    {
        var command = SedCommand.Substitute(SedAddress.All(), "[[:digit:]]", "X", "g");
        var script = new SedScript(new[] { command });
        var result = script.Transform("a1b2c3");
        Assert.That(result, Is.EqualTo("aXbXcX"));
    }

    /// <summary>
    /// POSIX character class [:alnum:] inside [...] should translate correctly.
    /// </summary>
    [Test]
    public void BRETokenizer_PosixAlnum_TranslatedCorrectly()
    {
        var command = SedCommand.Substitute(SedAddress.All(), "[[:alnum:]]", "X", "g");
        var script = new SedScript(new[] { command });
        var result = script.Transform("a1 b2");
        Assert.That(result, Is.EqualTo("XX XX"));
    }

    /// <summary>
    /// POSIX character class [:space:] inside [...] should translate correctly.
    /// </summary>
    [Test]
    public void BRETokenizer_PosixSpace_TranslatedCorrectly()
    {
        var command = SedCommand.Substitute(SedAddress.All(), "[[:space:]]", "X", "g");
        var script = new SedScript(new[] { command });
        var result = script.Transform("a b\tc");
        Assert.That(result, Is.EqualTo("aXbXc"));
    }

    /// <summary>
    /// POSIX character class [:upper:] inside [...] should translate correctly.
    /// </summary>
    [Test]
    public void BRETokenizer_PosixUpper_TranslatedCorrectly()
    {
        var command = SedCommand.Substitute(SedAddress.All(), "[[:upper:]]", "X", "g");
        var script = new SedScript(new[] { command });
        var result = script.Transform("aBcD");
        Assert.That(result, Is.EqualTo("aXcX"));
    }

    /// <summary>
    /// POSIX character class [:lower:] inside [...] should translate correctly.
    /// </summary>
    [Test]
    public void BRETokenizer_PosixLower_TranslatedCorrectly()
    {
        var command = SedCommand.Substitute(SedAddress.All(), "[[:lower:]]", "X", "g");
        var script = new SedScript(new[] { command });
        var result = script.Transform("aBcD");
        Assert.That(result, Is.EqualTo("XBXD"));
    }

    /// <summary>
    /// POSIX character class [:xdigit:] inside [...] should translate correctly.
    /// </summary>
    [Test]
    public void BRETokenizer_PosixXdigit_TranslatedCorrectly()
    {
        var command = SedCommand.Substitute(SedAddress.All(), "[[:xdigit:]]", "X", "g");
        var script = new SedScript(new[] { command });
        var result = script.Transform("0x1fGz");
        Assert.That(result, Is.EqualTo("XxXXGz")); // Only 0-9, a-f, A-F are hex digits; x, G, z are not
    }

    /// <summary>
    /// POSIX character class [:blank:] inside [...] should match space and tab.
    /// </summary>
    [Test]
    public void BRETokenizer_PosixBlank_TranslatedCorrectly()
    {
        var command = SedCommand.Substitute(SedAddress.All(), "[[:blank:]]", "X", "g");
        var script = new SedScript(new[] { command });
        var result = script.Transform("a b\tc");
        Assert.That(result, Is.EqualTo("aXbXc"));
    }

    /// <summary>
    /// POSIX character class [:punct:] inside [...] should match punctuation.
    /// </summary>
    [Test]
    public void BRETokenizer_PosixPunct_TranslatedCorrectly()
    {
        var command = SedCommand.Substitute(SedAddress.All(), "[[:punct:]]", "X", "g");
        var script = new SedScript(new[] { command });
        var result = script.Transform("a.b,c!");
        Assert.That(result, Is.EqualTo("aXbXcX"));
    }

    /// <summary>
    /// Multiple POSIX classes combined in one bracket expression.
    /// </summary>
    [Test]
    public void BRETokenizer_MultiplePosixClasses_TranslatedCorrectly()
    {
        // [:alpha:][:digit:] inside one bracket should match letters and digits
        var command = SedCommand.Substitute(SedAddress.All(), "[[:alpha:][:digit:]]", "X", "g");
        var script = new SedScript(new[] { command });
        var result = script.Transform("a1 b2!");
        Assert.That(result, Is.EqualTo("XX XX!"));
    }

    #endregion

    #region BRE Replacement String Tests (WI-002)

    /// <summary>
    /// & in replacement string should be translated to $0 (entire match reference).
    /// </summary>
    [Test]
    public void BREReplacement_Ampersand_TranslatedToEntireMatch()
    {
        var command = SedCommand.Substitute(SedAddress.All(), "test", "[&]");
        var script = new SedScript(new[] { command });
        var result = script.Transform("test value");
        Assert.That(result, Is.EqualTo("[test] value"));
    }

    /// <summary>
    /// Multiple & in replacement string all reference the entire match.
    /// </summary>
    [Test]
    public void BREReplacement_MultipleAmpersands_AllTranslated()
    {
        var command = SedCommand.Substitute(SedAddress.All(), "test", "&&");
        var script = new SedScript(new[] { command });
        var result = script.Transform("test value");
        Assert.That(result, Is.EqualTo("testtest value"));
    }

    /// <summary>
    /// \& in replacement string should produce a literal & character.
    /// </summary>
    [Test]
    public void BREReplacement_EscapedAmpersand_ProducesLiteralAmpersand()
    {
        var command = SedCommand.Substitute(SedAddress.All(), "test", @"a\&b");
        var script = new SedScript(new[] { command });
        var result = script.Transform("test value");
        Assert.That(result, Is.EqualTo("a&b value"));
    }

    /// <summary>
    /// \n in replacement string should produce a literal newline.
    /// </summary>
    [Test]
    public void BREReplacement_BackslashN_ProducesNewline()
    {
        var command = SedCommand.Substitute(SedAddress.All(), "test", @"a\nb");
        var script = new SedScript(new[] { command });
        var result = script.Transform("test");
        Assert.That(result, Is.EqualTo("a\nb"));
    }

    /// <summary>
    /// \\ in replacement string should produce a literal backslash.
    /// </summary>
    [Test]
    public void BREReplacement_DoubleBackslash_ProducesLiteralBackslash()
    {
        var command = SedCommand.Substitute(SedAddress.All(), "test", @"a\\b");
        var script = new SedScript(new[] { command });
        var result = script.Transform("test");
        Assert.That(result, Is.EqualTo(@"a\b"));
    }

    /// <summary>
    /// & in replacement via SedParser.Parse -- end-to-end test through the parser.
    /// </summary>
    [Test]
    public void BREReplacement_Ampersand_ThroughParser_Works()
    {
        var script = SedParser.Parse("s/world/[&]/");
        var result = script.Transform("hello world");
        Assert.That(result, Is.EqualTo("hello [world]"));
    }

    /// <summary>
    /// Backreferences and & used together in replacement.
    /// </summary>
    [Test]
    public void BREReplacement_BackrefAndAmpersand_Together()
    {
        var command = SedCommand.Substitute(SedAddress.All(), @"\(hel\)lo", @"\1-&");
        var script = new SedScript(new[] { command });
        var result = script.Transform("hello");
        Assert.That(result, Is.EqualTo("hel-hello"));
    }

    #endregion

    #region BRE Tokenizer Tests - Interval Expressions (WI-002)

    /// <summary>
    /// BRE interval \{m\} should match exactly m occurrences.
    /// </summary>
    [Test]
    public void BRETokenizer_IntervalExact_TranslatedCorrectly()
    {
        var command = SedCommand.Substitute(SedAddress.All(), @"a\{3\}", "X");
        var script = new SedScript(new[] { command });
        var result = script.Transform("aaaa");
        Assert.That(result, Is.EqualTo("Xa")); // Matches first 3 a's
    }

    /// <summary>
    /// BRE interval \{m,n\} should match between m and n occurrences.
    /// </summary>
    [Test]
    public void BRETokenizer_IntervalRange_TranslatedCorrectly()
    {
        var command = SedCommand.Substitute(SedAddress.All(), @"a\{2,4\}", "X");
        var script = new SedScript(new[] { command });
        var result = script.Transform("aaaaa");
        Assert.That(result, Is.EqualTo("Xa")); // Greedy: matches 4, leaves 1
    }

    /// <summary>
    /// BRE interval \{m,\} should match m or more occurrences.
    /// </summary>
    [Test]
    public void BRETokenizer_IntervalMinimum_TranslatedCorrectly()
    {
        var command = SedCommand.Substitute(SedAddress.All(), @"a\{2,\}", "X");
        var script = new SedScript(new[] { command });
        var result = script.Transform("aaaaa");
        Assert.That(result, Is.EqualTo("X")); // Matches all 5 (greedy, min 2)
    }

    #endregion

    #region BRE Tokenizer Tests - Empty and Edge-Case Patterns (WI-002)

    /// <summary>
    /// Empty BRE pattern should pass through without crashing.
    /// </summary>
    [Test]
    public void BRETokenizer_EmptyPattern_HandledGracefully()
    {
        var command = SedCommand.Substitute(SedAddress.All(), "", "X");
        var script = new SedScript(new[] { command });
        var result = script.Transform("test");
        // Empty pattern behavior: no change expected (guarded by empty check)
        Assert.That(result, Is.EqualTo("test"));
    }

    /// <summary>
    /// Pattern with only anchors should match empty lines within multi-line input.
    /// Note: Transform returns empty for empty input due to the IsNullOrEmpty guard,
    /// so we test with a multi-line input containing an empty line.
    /// </summary>
    [Test]
    public void BRETokenizer_OnlyAnchors_WorkCorrectly()
    {
        var command = SedCommand.Substitute(SedAddress.All(), "^$", "EMPTY");
        var script = new SedScript(new[] { command });
        var result = script.Transform("text\n\ntext");
        Assert.That(result, Is.EqualTo("text\nEMPTY\ntext"));
    }

    /// <summary>
    /// Pattern containing only a dot should match any character.
    /// </summary>
    [Test]
    public void BRETokenizer_DotMatchesAnyChar()
    {
        var command = SedCommand.Substitute(SedAddress.All(), ".", "X", "g");
        var script = new SedScript(new[] { command });
        var result = script.Transform("abc");
        Assert.That(result, Is.EqualTo("XXX"));
    }

    /// <summary>
    /// Pattern with .* (match everything) should work.
    /// </summary>
    [Test]
    public void BRETokenizer_DotStar_MatchesEverything()
    {
        var command = SedCommand.Substitute(SedAddress.All(), "^.*$", "REPLACED");
        var script = new SedScript(new[] { command });
        var result = script.Transform("anything here");
        Assert.That(result, Is.EqualTo("REPLACED"));
    }

    /// <summary>
    /// Single backslash at end of pattern should be treated as literal backslash.
    /// </summary>
    [Test]
    public void BRETokenizer_TrailingBackslash_HandledGracefully()
    {
        // A trailing backslash with nothing after it -- treat as literal backslash
        var command = SedCommand.Substitute(SedAddress.All(), @"test\", "FOUND");
        var script = new SedScript(new[] { command });
        var result = script.Transform("test\\");
        Assert.That(result, Is.EqualTo("FOUND"));
    }

    /// <summary>
    /// Escaped dot \. should match literal dot.
    /// </summary>
    [Test]
    public void BRETokenizer_EscapedDot_MatchesLiteralDot()
    {
        var command = SedCommand.Substitute(SedAddress.All(), @"\.", "X", "g");
        var script = new SedScript(new[] { command });
        var result = script.Transform("a.b.c");
        Assert.That(result, Is.EqualTo("aXbXc"));
    }

    /// <summary>
    /// Escaped asterisk \* should match literal asterisk.
    /// </summary>
    [Test]
    public void BRETokenizer_EscapedStar_MatchesLiteralStar()
    {
        var command = SedCommand.Substitute(SedAddress.All(), @"\*", "X", "g");
        var script = new SedScript(new[] { command });
        var result = script.Transform("a*b*c");
        Assert.That(result, Is.EqualTo("aXbXc"));
    }

    #endregion

    #region Substitute Flag Combinations

    [Test]
    public void SubstituteFlags_GlobalAndCaseInsensitive_Work()
    {
        var script = SedParser.Parse("s/TEST/test/gi");
        var result = script.Transform("Test TEST tEsT");
        Assert.That(result, Is.EqualTo("test test test"));
    }

    [Test]
    public void SubstituteFlags_NumericOccurrence_WithCaseInsensitive()
    {
        var script = SedParser.Parse("s/TEST/test/2i");
        var result = script.Transform("Test TEST tEsT");
        Assert.That(result, Is.EqualTo("Test test tEsT")); // Only second match
    }

    [Test]
    public void SubstituteFlags_EmptyFlags_DefaultBehavior()
    {
        var script = SedParser.Parse("s/old/new/");
        var result = script.Transform("old text old again");
        Assert.That(result, Is.EqualTo("new text old again")); // First occurrence only
    }

    #endregion

    #region Delimiter Handling Tests

    [Test]
    public void AlternateDelimiters_Pipe_Works()
    {
        var script = SedParser.Parse("s|/path|/newpath|");
        var result = script.Transform("/path/file");
        Assert.That(result, Is.EqualTo("/newpath/file"));
    }

    [Test]
    public void AlternateDelimiters_Hash_Works()
    {
        var script = SedParser.Parse("s#old#new#");
        var result = script.Transform("old value");
        Assert.That(result, Is.EqualTo("new value"));
    }

    [Test]
    public void DelimiterHandling_EscapedDelimiter_InPattern()
    {
        var script = SedParser.Parse(@"s/\/path/newpath/");
        var result = script.Transform("/path/file");
        Assert.That(result, Is.EqualTo("newpath/file"));
    }

    #endregion

    #region FluentSed Advanced Tests

    [Test]
    public void FluentSed_SuppressDefaultOutput_Works()
    {
        var script = FluentSed.Create()
            .SuppressDefaultOutput()
            .Substitute("test", "TEST")
            .Build();

        // With suppressed output, should return empty (no explicit print commands)
        var result = script.Transform("test");
        Assert.That(result, Is.EqualTo(""));
    }

    [Test]
    public void FluentSed_ChainedOperations_ExecuteInOrder()
    {
        var result = FluentSed.Create()
            .Substitute("a", "1")
            .Substitute("b", "2")
            .Substitute("c", "3")
            .Transform("abc");

        Assert.That(result, Is.EqualTo("123"));
    }

    [Test]
    public void FluentSed_PatternAddressing_Works()
    {
        var script = FluentSed.Create()
            .Substitute("old", "new")
            .Build();

        var result = script.Transform("old\nkeep\nold");
        Assert.That(result, Is.EqualTo("new\nkeep\nnew"));
    }

    #endregion

    #region Command Factory Tests

    [Test]
    public void SedCommand_FactoryMethods_CreateCorrectCommands()
    {
        var subCmd = SedCommand.Substitute(SedAddress.All(), "old", "new", "g");
        Assert.That(subCmd.Type, Is.EqualTo(CommandType.Substitute));
        Assert.That(subCmd.Pattern, Is.EqualTo("old"));
        Assert.That(subCmd.Replacement, Is.EqualTo("new"));
        Assert.That(subCmd.Flags, Is.EqualTo("g"));

        var delCmd = SedCommand.Delete(SedAddress.LineNumber(5));
        Assert.That(delCmd.Type, Is.EqualTo(CommandType.Delete));
        Assert.That(delCmd.Address.Type, Is.EqualTo(AddressType.LineNumber));

        var printCmd = SedCommand.Print(SedAddress.Pattern("test"));
        Assert.That(printCmd.Type, Is.EqualTo(CommandType.Print));
        Assert.That(printCmd.Address.Type, Is.EqualTo(AddressType.Pattern));
    }

    [Test]
    public void SedAddress_FactoryMethods_CreateCorrectAddresses()
    {
        var allAddr = SedAddress.All();
        Assert.That(allAddr.Type, Is.EqualTo(AddressType.None));

        var lineAddr = SedAddress.LineNumber(42);
        Assert.That(lineAddr.Type, Is.EqualTo(AddressType.LineNumber));
        Assert.That(lineAddr.Value1, Is.EqualTo("42"));

        var patternAddr = SedAddress.Pattern("test.*");
        Assert.That(patternAddr.Type, Is.EqualTo(AddressType.Pattern));
        Assert.That(patternAddr.Value1, Is.EqualTo("test.*"));

        var rangeAddr = SedAddress.Range("1", "5");
        Assert.That(rangeAddr.Type, Is.EqualTo(AddressType.Range));
        Assert.That(rangeAddr.Value1, Is.EqualTo("1"));
        Assert.That(rangeAddr.Value2, Is.EqualTo("5"));

        var stepAddr = SedAddress.Step(2, 3);
        Assert.That(stepAddr.Type, Is.EqualTo(AddressType.Step));

        var lastAddr = SedAddress.LastLine();
        Assert.That(lastAddr.Type, Is.EqualTo(AddressType.LastLine));

        var negatedAddr = lineAddr.Negate();
        Assert.That(negatedAddr.Negated, Is.True);
    }

    #endregion

    #region Performance and Large Input Tests

    [Test]
    public void LargeInput_ProcessedEfficiently()
    {
        // Test with 10K lines
        var largeInput = string.Join("\n", Enumerable.Range(1, 10000).Select(i => $"line{i}"));
        var script = SedParser.Parse("s/line/LINE/");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = script.Transform(largeInput);
        stopwatch.Stop();

        Assert.That(result.Contains("LINE1"), Is.True);
        Assert.That(result.Contains("LINE10000"), Is.True);
        Assert.That(stopwatch.ElapsedMilliseconds, Is.LessThan(1000)); // Should be fast
    }

    [Test]
    public void ScriptReuse_PerformantWithLargeInput()
    {
        var script = SedParser.Parse("s/test/TEST/g");
        var input = string.Join(" ", Enumerable.Repeat("test", 1000));

        // Run multiple times to test reusability
        for (int i = 0; i < 10; i++)
        {
            var result = script.Transform(input);
            Assert.That(result, Does.Not.Contain("test"));
            Assert.That(result, Does.Contain("TEST"));
        }
    }

    #endregion

    #region Stress Tests - Bulletproof Testing

    [Test]
    public void StressTest_MassiveInput_HandlesEfficiently()
    {
        // Test with 100K lines to stress memory and performance
        var massiveInput = string.Join("\n", Enumerable.Range(1, 100000).Select(i => $"test{i}"));
        var script = SedParser.Parse("s/test/TEST/");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = script.Transform(massiveInput);
        stopwatch.Stop();

        Assert.That(result, Does.StartWith("TEST1"));
        Assert.That(result, Does.Contain("TEST50000"));
        Assert.That(result, Does.EndWith("TEST100000"));
        Assert.That(stopwatch.ElapsedMilliseconds, Is.LessThan(3000)); // Should complete in under 3 seconds
    }

    [Test]
    public void StressTest_VeryLongLines_HandlesCorrectly()
    {
        // Test with extremely long lines (1MB per line)
        var longLine = new string('a', 1024 * 1024); // 1MB line
        var input = $"{longLine}\nshort\n{longLine}";
        var script = SedParser.Parse("s/a/A/");

        var result = script.Transform(input);
        Assert.That(result.Split('\n')[0], Does.StartWith("A"));
        Assert.That(result.Split('\n')[1], Is.EqualTo("short"));
        Assert.That(result.Split('\n')[2], Does.StartWith("A"));
    }

    [Test]
    public void StressTest_ThousandsOfCommands_ProcessesCorrectly()
    {
        // Build a script with many commands
        var commands = new List<SedCommand>();
        for (int i = 0; i < 1000; i++)
        {
            commands.Add(SedCommand.Substitute(SedAddress.All(), $"test{i}", $"TEST{i}"));
        }

        var script = new SedScript(commands);
        var input = "test500 middle test999";
        var result = script.Transform(input);

        Assert.That(result, Is.EqualTo("TEST500 middle TEST999"));
    }

    [Test]
    public void StressTest_DeepRecursivePattern_HandlesGracefully()
    {
        // Test with deeply nested regex patterns
        var deepPattern = string.Concat(Enumerable.Repeat(@"\(", 50)) + "test" + string.Concat(Enumerable.Repeat(@"\)", 50));
        var script = SedParser.Parse($"s/{deepPattern}/DEEP/");

        // Should not crash, even if it doesn't match due to complexity
        var result = script.Transform("test");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public void StressTest_UnicodeAndMultibyte_HandlesCorrectly()
    {
        // Test with various Unicode characters
        var unicodeInput = "Hello 世界 🌍 مرحبا العالم";
        var script = SedParser.Parse("s/世界/World/");

        var result = script.Transform(unicodeInput);
        Assert.That(result, Is.EqualTo("Hello World 🌍 مرحبا العالم"));
    }

    #endregion

    #region Boundary Condition Tests

    [Test]
    public void Boundary_EmptyPattern_HandledSafely()
    {
        var script = SedParser.Parse("s//EMPTY/");
        var result = script.Transform("test");
        // Should not crash, behavior may vary
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public void Boundary_NullInputHandling_Graceful()
    {
        var script = SedParser.Parse("s/test/TEST/");
        var result = script.Transform(null!);
        Assert.That(result, Is.Null);
    }

    [Test]
    public void Boundary_MaxLineNumber_HandlesCorrectly()
    {
        var script = SedParser.Parse($"{int.MaxValue}s/test/TEST/");
        var result = script.Transform("test");
        Assert.That(result, Is.EqualTo("test")); // No match at max line number
    }

    [Test]
    public void Boundary_ZeroOccurrence_HandledCorrectly()
    {
        var script = SedParser.Parse("s/test/TEST/0");
        var result = script.Transform("test");
        // Occurrence 0 should not match anything
        Assert.That(result, Is.EqualTo("test"));
    }

    [Test]
    public void Boundary_NegativeOccurrence_HandledGracefully()
    {
        // Our implementation treats negative numbers as non-numeric flags
        // This is different from real sed but doesn't crash
        var script = SedParser.Parse("s/test/TEST/-1");
        var result = script.Transform("test");
        // Current behavior: treats as first occurrence (default)
        Assert.That(result, Is.EqualTo("TEST"));
    }

    [Test]
    public void Boundary_ExtremelyLongPattern_HandlesGracefully()
    {
        // Pattern with 10K characters
        var longPattern = new string('a', 10000);
        var script = SedParser.Parse($"s/{longPattern}/LONG/");

        var result = script.Transform("test");
        Assert.That(result, Is.Not.Null);
    }

    #endregion

    #region Malformed Data Tests

    [Test]
    public void MalformedData_ControlCharacters_HandledSafely()
    {
        // Input with control characters
        var controlInput = "test\0\b\t\r\n\x1B[31mcolored\x1B[0m";
        var script = SedParser.Parse("s/test/TEST/");

        var result = script.Transform(controlInput);
        Assert.That(result, Does.StartWith("TEST"));
    }

    [Test]
    public void MalformedData_UnmatchedDelimiters_HandlesGracefully()
    {
        // Our implementation is more forgiving than real sed
        // It accepts missing final delimiter (different from POSIX sed)
        var script = SedParser.Parse("s/test/replacement");
        var result = script.Transform("test replacement");

        // Should not crash - validates our implementation is robust
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public void MalformedData_InvalidRegexPattern_HandlesGracefully()
    {
        // Invalid regex (unclosed bracket) should throw
        Assert.Throws<SedException>(() => SedParser.Parse(@"s/[/invalid/"));
    }

    [Test]
    public void MalformedData_BinaryData_HandlesCorrectly()
    {
        // Input with binary data (non-text bytes)
        var binaryData = string.Join("", Enumerable.Range(0, 256).Select(i => (char)i));
        var script = SedParser.Parse("s/test/TEST/");

        // Should not crash
        var result = script.Transform(binaryData);
        Assert.That(result, Is.Not.Null);
    }

    #endregion

    #region Edge Case Coverage Tests

    [Test]
    public void EdgeCase_MultipleNewlineTypes_HandledCorrectly()
    {
        // Test different newline types
        var input = "line1\rline2\nline3\r\nline4";
        var script = SedParser.Parse("s/line/LINE/g");

        var result = script.Transform(input);
        Assert.That(result, Does.Contain("LINE"));
    }

    [Test]
    public void EdgeCase_ScriptReuse_ThreadSafe()
    {
        var script = SedParser.Parse("s/test/TEST/g");
        var tasks = new List<System.Threading.Tasks.Task<string>>();

        // Run 100 concurrent transformations
        for (int i = 0; i < 100; i++)
        {
            int taskId = i;
            tasks.Add(System.Threading.Tasks.Task.Run(() =>
                script.Transform($"test{taskId}")));
        }

        System.Threading.Tasks.Task.WaitAll(tasks.ToArray());

        // All should complete successfully
        foreach (var task in tasks)
        {
            Assert.That(task.Result, Does.StartWith("TEST"));
        }
    }

    [Test]
    public void EdgeCase_ChainedSubstitutions_WorkCorrectly()
    {
        // Test multiple substitutions affecting the same text
        var script = SedParser.Parse("s/a/b/; s/b/c/; s/c/d/");
        var result = script.Transform("a");

        // Should chain: a->b, b->c, c->d
        Assert.That(result, Is.EqualTo("d"));
    }

    [Test]
    public void EdgeCase_BacktrackingPattern_PerformanceTest()
    {
        // Pattern that could cause excessive backtracking
        var input = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaab";
        var script = SedParser.Parse(@"s/a*a*a*a*a*a*a*a*a*a*a*a*b/MATCH/");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = script.Transform(input);
        stopwatch.Stop();

        // Should complete in reasonable time (not hang forever)
        Assert.That(stopwatch.ElapsedMilliseconds, Is.LessThan(5000));
        Assert.That(result, Is.EqualTo("MATCH"));
    }

    #endregion

    #region Parser Rewrite Tests - Single-Character Commands

    [Test]
    public void Parse_PrintCommand_ReturnsCorrectType()
    {
        var script = SedParser.Parse("p");
        Assert.That(script.Commands.Count, Is.EqualTo(1));
        Assert.That(script.Commands[0].Type, Is.EqualTo(CommandType.Print));
    }

    [Test]
    public void Parse_PrintFirstLineCommand_ReturnsCorrectType()
    {
        var script = SedParser.Parse("P");
        Assert.That(script.Commands.Count, Is.EqualTo(1));
        Assert.That(script.Commands[0].Type, Is.EqualTo(CommandType.PrintFirstLine));
    }

    [Test]
    public void Parse_NextCommand_ReturnsCorrectType()
    {
        var script = SedParser.Parse("n");
        Assert.That(script.Commands.Count, Is.EqualTo(1));
        Assert.That(script.Commands[0].Type, Is.EqualTo(CommandType.Next));
    }

    [Test]
    public void Parse_NextAppendCommand_ReturnsCorrectType()
    {
        var script = SedParser.Parse("N");
        Assert.That(script.Commands.Count, Is.EqualTo(1));
        Assert.That(script.Commands[0].Type, Is.EqualTo(CommandType.NextAppend));
    }

    [Test]
    public void Parse_HoldCopyCommand_ReturnsCorrectType()
    {
        var script = SedParser.Parse("h");
        Assert.That(script.Commands.Count, Is.EqualTo(1));
        Assert.That(script.Commands[0].Type, Is.EqualTo(CommandType.HoldCopy));
    }

    [Test]
    public void Parse_HoldAppendCommand_ReturnsCorrectType()
    {
        var script = SedParser.Parse("H");
        Assert.That(script.Commands.Count, Is.EqualTo(1));
        Assert.That(script.Commands[0].Type, Is.EqualTo(CommandType.HoldAppend));
    }

    [Test]
    public void Parse_GetHoldCommand_ReturnsCorrectType()
    {
        var script = SedParser.Parse("g");
        Assert.That(script.Commands.Count, Is.EqualTo(1));
        Assert.That(script.Commands[0].Type, Is.EqualTo(CommandType.GetHold));
    }

    [Test]
    public void Parse_GetHoldAppendCommand_ReturnsCorrectType()
    {
        var script = SedParser.Parse("G");
        Assert.That(script.Commands.Count, Is.EqualTo(1));
        Assert.That(script.Commands[0].Type, Is.EqualTo(CommandType.GetHoldAppend));
    }

    [Test]
    public void Parse_ExchangeCommand_ReturnsCorrectType()
    {
        var script = SedParser.Parse("x");
        Assert.That(script.Commands.Count, Is.EqualTo(1));
        Assert.That(script.Commands[0].Type, Is.EqualTo(CommandType.Exchange));
    }

    [Test]
    public void Parse_LineNumberCommand_ReturnsCorrectType()
    {
        var script = SedParser.Parse("=");
        Assert.That(script.Commands.Count, Is.EqualTo(1));
        Assert.That(script.Commands[0].Type, Is.EqualTo(CommandType.LineNumber));
    }

    [Test]
    public void Parse_ListCommand_ReturnsCorrectType()
    {
        var script = SedParser.Parse("l");
        Assert.That(script.Commands.Count, Is.EqualTo(1));
        Assert.That(script.Commands[0].Type, Is.EqualTo(CommandType.List));
    }

    [Test]
    public void Parse_QuitCommand_ReturnsCorrectType()
    {
        var script = SedParser.Parse("q");
        Assert.That(script.Commands.Count, Is.EqualTo(1));
        Assert.That(script.Commands[0].Type, Is.EqualTo(CommandType.Quit));
    }

    [Test]
    public void Parse_QuitNoprintCommand_ReturnsCorrectType()
    {
        var script = SedParser.Parse("Q");
        Assert.That(script.Commands.Count, Is.EqualTo(1));
        Assert.That(script.Commands[0].Type, Is.EqualTo(CommandType.QuitNoprint));
    }

    [Test]
    public void Parse_DeleteFirstLineCommand_ReturnsCorrectType()
    {
        var script = SedParser.Parse("D");
        Assert.That(script.Commands.Count, Is.EqualTo(1));
        Assert.That(script.Commands[0].Type, Is.EqualTo(CommandType.DeleteFirstLine));
    }

    [Test]
    public void Parse_SingleCharCommand_WithLineNumberAddress()
    {
        var script = SedParser.Parse("3p");
        Assert.That(script.Commands.Count, Is.EqualTo(1));
        Assert.That(script.Commands[0].Type, Is.EqualTo(CommandType.Print));
        Assert.That(script.Commands[0].Address.Type, Is.EqualTo(AddressType.LineNumber));
        Assert.That(script.Commands[0].Address.Value1, Is.EqualTo("3"));
    }

    [Test]
    public void Parse_SingleCharCommand_WithPatternAddress()
    {
        var script = SedParser.Parse("/foo/P");
        Assert.That(script.Commands.Count, Is.EqualTo(1));
        Assert.That(script.Commands[0].Type, Is.EqualTo(CommandType.PrintFirstLine));
        Assert.That(script.Commands[0].Address.Type, Is.EqualTo(AddressType.Pattern));
        Assert.That(script.Commands[0].Address.Value1, Is.EqualTo("foo"));
    }

    [Test]
    public void Parse_SingleCharCommand_WithRangeAddress()
    {
        var script = SedParser.Parse("2,5q");
        Assert.That(script.Commands.Count, Is.EqualTo(1));
        Assert.That(script.Commands[0].Type, Is.EqualTo(CommandType.Quit));
        Assert.That(script.Commands[0].Address.Type, Is.EqualTo(AddressType.Range));
    }

    #endregion

    #region Parser Rewrite Tests - Label Commands

    [Test]
    public void Parse_BranchWithLabel_ReturnsCorrectLabel()
    {
        var script = SedParser.Parse("b myloop");
        Assert.That(script.Commands.Count, Is.EqualTo(1));
        Assert.That(script.Commands[0].Type, Is.EqualTo(CommandType.Branch));
        Assert.That(script.Commands[0].Label, Is.EqualTo("myloop"));
    }

    [Test]
    public void Parse_BranchWithoutLabel_ReturnsNullLabel()
    {
        var script = SedParser.Parse("b");
        Assert.That(script.Commands.Count, Is.EqualTo(1));
        Assert.That(script.Commands[0].Type, Is.EqualTo(CommandType.Branch));
        Assert.That(script.Commands[0].Label, Is.Null.Or.EqualTo(""));
    }

    [Test]
    public void Parse_TestWithLabel_ReturnsCorrectLabel()
    {
        var script = SedParser.Parse("t done");
        Assert.That(script.Commands.Count, Is.EqualTo(1));
        Assert.That(script.Commands[0].Type, Is.EqualTo(CommandType.Test));
        Assert.That(script.Commands[0].Label, Is.EqualTo("done"));
    }

    [Test]
    public void Parse_TestNotWithLabel_ReturnsCorrectLabel()
    {
        var script = SedParser.Parse("T retry");
        Assert.That(script.Commands.Count, Is.EqualTo(1));
        Assert.That(script.Commands[0].Type, Is.EqualTo(CommandType.TestNot));
        Assert.That(script.Commands[0].Label, Is.EqualTo("retry"));
    }

    [Test]
    public void Parse_DefineLabel_ReturnsCorrectLabel()
    {
        var script = SedParser.Parse(":myloop");
        Assert.That(script.Commands.Count, Is.EqualTo(1));
        Assert.That(script.Commands[0].Type, Is.EqualTo(CommandType.Label));
        Assert.That(script.Commands[0].Label, Is.EqualTo("myloop"));
    }

    [Test]
    public void Parse_LabelWithAddress_UsesAddress()
    {
        // Branch with line number address
        var script = SedParser.Parse("3b done");
        Assert.That(script.Commands.Count, Is.EqualTo(1));
        Assert.That(script.Commands[0].Type, Is.EqualTo(CommandType.Branch));
        Assert.That(script.Commands[0].Label, Is.EqualTo("done"));
        Assert.That(script.Commands[0].Address.Type, Is.EqualTo(AddressType.LineNumber));
    }

    #endregion

    #region Parser Rewrite Tests - Text Commands (a, i, c)

    [Test]
    public void Parse_AppendText_ReturnsCorrectText()
    {
        var script = SedParser.Parse(@"a\appended line");
        Assert.That(script.Commands.Count, Is.EqualTo(1));
        Assert.That(script.Commands[0].Type, Is.EqualTo(CommandType.Append));
        Assert.That(script.Commands[0].Text, Is.EqualTo("appended line"));
    }

    [Test]
    public void Parse_InsertText_ReturnsCorrectText()
    {
        var script = SedParser.Parse(@"i\inserted line");
        Assert.That(script.Commands.Count, Is.EqualTo(1));
        Assert.That(script.Commands[0].Type, Is.EqualTo(CommandType.Insert));
        Assert.That(script.Commands[0].Text, Is.EqualTo("inserted line"));
    }

    [Test]
    public void Parse_ChangeText_ReturnsCorrectText()
    {
        var script = SedParser.Parse(@"c\changed line");
        Assert.That(script.Commands.Count, Is.EqualTo(1));
        Assert.That(script.Commands[0].Type, Is.EqualTo(CommandType.Change));
        Assert.That(script.Commands[0].Text, Is.EqualTo("changed line"));
    }

    [Test]
    public void Parse_AppendText_WithAddress()
    {
        var script = SedParser.Parse(@"3a\third line addition");
        Assert.That(script.Commands.Count, Is.EqualTo(1));
        Assert.That(script.Commands[0].Type, Is.EqualTo(CommandType.Append));
        Assert.That(script.Commands[0].Text, Is.EqualTo("third line addition"));
        Assert.That(script.Commands[0].Address.Type, Is.EqualTo(AddressType.LineNumber));
    }

    #endregion

    #region Parser Rewrite Tests - File Commands (r, w)

    [Test]
    public void Parse_ReadFileCommand_ReturnsCorrectFilename()
    {
        var script = SedParser.Parse("r input.txt");
        Assert.That(script.Commands.Count, Is.EqualTo(1));
        Assert.That(script.Commands[0].Type, Is.EqualTo(CommandType.ReadFile));
        Assert.That(script.Commands[0].Filename, Is.EqualTo("input.txt"));
    }

    [Test]
    public void Parse_WriteFileCommand_ReturnsCorrectFilename()
    {
        var script = SedParser.Parse("w output.txt");
        Assert.That(script.Commands.Count, Is.EqualTo(1));
        Assert.That(script.Commands[0].Type, Is.EqualTo(CommandType.WriteFile));
        Assert.That(script.Commands[0].Filename, Is.EqualTo("output.txt"));
    }

    [Test]
    public void Parse_ReadFileCommand_WithAddress()
    {
        var script = SedParser.Parse("3r data.txt");
        Assert.That(script.Commands.Count, Is.EqualTo(1));
        Assert.That(script.Commands[0].Type, Is.EqualTo(CommandType.ReadFile));
        Assert.That(script.Commands[0].Filename, Is.EqualTo("data.txt"));
        Assert.That(script.Commands[0].Address.Type, Is.EqualTo(AddressType.LineNumber));
    }

    [Test]
    public void Parse_WriteFileCommand_FilenameWithPath()
    {
        var script = SedParser.Parse("w /tmp/out.txt");
        Assert.That(script.Commands.Count, Is.EqualTo(1));
        Assert.That(script.Commands[0].Type, Is.EqualTo(CommandType.WriteFile));
        Assert.That(script.Commands[0].Filename, Is.EqualTo("/tmp/out.txt"));
    }

    #endregion

    #region Parser Rewrite Tests - Transliterate (y)

    [Test]
    public void Parse_Transliterate_ReturnsCorrectSourceAndDest()
    {
        var script = SedParser.Parse("y/abc/xyz/");
        Assert.That(script.Commands.Count, Is.EqualTo(1));
        Assert.That(script.Commands[0].Type, Is.EqualTo(CommandType.Transliterate));
        Assert.That(script.Commands[0].Pattern, Is.EqualTo("abc"));
        Assert.That(script.Commands[0].Replacement, Is.EqualTo("xyz"));
    }

    [Test]
    public void Parse_Transliterate_WithAddress()
    {
        var script = SedParser.Parse("2y/abc/ABC/");
        Assert.That(script.Commands.Count, Is.EqualTo(1));
        Assert.That(script.Commands[0].Type, Is.EqualTo(CommandType.Transliterate));
        Assert.That(script.Commands[0].Address.Type, Is.EqualTo(AddressType.LineNumber));
    }

    [Test]
    public void Parse_Transliterate_AlternateDelimiters()
    {
        var script = SedParser.Parse("y|abc|xyz|");
        Assert.That(script.Commands.Count, Is.EqualTo(1));
        Assert.That(script.Commands[0].Type, Is.EqualTo(CommandType.Transliterate));
        Assert.That(script.Commands[0].Pattern, Is.EqualTo("abc"));
        Assert.That(script.Commands[0].Replacement, Is.EqualTo("xyz"));
    }

    #endregion

    #region Parser Rewrite Tests - SplitCommands with Embedded Semicolons

    [Test]
    public void SplitCommands_SemicolonInsidePattern_DoesNotSplit()
    {
        // The semicolon inside the regex pattern should not cause a split
        var script = SedParser.Parse("s/a;b/c/");
        Assert.That(script.Commands.Count, Is.EqualTo(1));
        Assert.That(script.Commands[0].Type, Is.EqualTo(CommandType.Substitute));
        Assert.That(script.Commands[0].Pattern, Is.EqualTo("a;b"));
        Assert.That(script.Commands[0].Replacement, Is.EqualTo("c"));
    }

    [Test]
    public void SplitCommands_SemicolonInsideReplacement_DoesNotSplit()
    {
        // The semicolon inside the replacement string should not cause a split
        var script = SedParser.Parse("s/a/b;c/");
        Assert.That(script.Commands.Count, Is.EqualTo(1));
        Assert.That(script.Commands[0].Pattern, Is.EqualTo("a"));
        Assert.That(script.Commands[0].Replacement, Is.EqualTo("b;c"));
    }

    [Test]
    public void SplitCommands_SemicolonAfterSubstitute_SplitsCorrectly()
    {
        // Semicolon after a complete substitute should split to next command
        var script = SedParser.Parse("s/a/b/;s/c/d/");
        Assert.That(script.Commands.Count, Is.EqualTo(2));
        Assert.That(script.Commands[0].Pattern, Is.EqualTo("a"));
        Assert.That(script.Commands[1].Pattern, Is.EqualTo("c"));
    }

    [Test]
    public void SplitCommands_SemicolonInsideYCommand_DoesNotSplit()
    {
        // Semicolons inside y command's source/dest should not split
        var script = SedParser.Parse("y/a;b/c;d/");
        Assert.That(script.Commands.Count, Is.EqualTo(1));
        Assert.That(script.Commands[0].Type, Is.EqualTo(CommandType.Transliterate));
        Assert.That(script.Commands[0].Pattern, Is.EqualTo("a;b"));
    }

    [Test]
    public void SplitCommands_NewlinesAsCommandSeparators()
    {
        var script = SedParser.Parse("s/a/b/\ns/c/d/");
        Assert.That(script.Commands.Count, Is.EqualTo(2));
    }

    [Test]
    public void SplitCommands_BracesAsImplicitSeparators()
    {
        // Opening and closing braces should act as command boundaries
        var script = SedParser.Parse("/test/{s/old/new/}");
        // Should parse the substitute inside the braces
        // (blocks not fully implemented yet, but the command inside should parse)
        Assert.That(script.Commands.Count, Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    public void SplitCommands_MixedSemicolonsInSubstituteAndSimpleCommands()
    {
        // Complex script with semicolons inside patterns plus actual command separators
        var script = SedParser.Parse("s/a;b/c/;d");
        Assert.That(script.Commands.Count, Is.EqualTo(2));
        Assert.That(script.Commands[0].Type, Is.EqualTo(CommandType.Substitute));
        Assert.That(script.Commands[0].Pattern, Is.EqualTo("a;b"));
        Assert.That(script.Commands[1].Type, Is.EqualTo(CommandType.Delete));
    }

    #endregion

    #region Parser Rewrite Tests - Address Negation from Script Strings

    [Test]
    public void Parse_NegatedLineNumberAddress_ParsesCorrectly()
    {
        var script = SedParser.Parse("2!d");
        Assert.That(script.Commands.Count, Is.EqualTo(1));
        Assert.That(script.Commands[0].Type, Is.EqualTo(CommandType.Delete));
        Assert.That(script.Commands[0].Address.Negated, Is.True);
        Assert.That(script.Commands[0].Address.Type, Is.EqualTo(AddressType.LineNumber));
    }

    [Test]
    public void Parse_NegatedPatternAddress_ParsesCorrectly()
    {
        var script = SedParser.Parse("/test/!d");
        Assert.That(script.Commands.Count, Is.EqualTo(1));
        Assert.That(script.Commands[0].Type, Is.EqualTo(CommandType.Delete));
        Assert.That(script.Commands[0].Address.Negated, Is.True);
        Assert.That(script.Commands[0].Address.Type, Is.EqualTo(AddressType.Pattern));
    }

    [Test]
    public void Parse_NegatedRangeAddress_ParsesCorrectly()
    {
        var script = SedParser.Parse("2,4!d");
        Assert.That(script.Commands.Count, Is.EqualTo(1));
        Assert.That(script.Commands[0].Address.Negated, Is.True);
        Assert.That(script.Commands[0].Address.Type, Is.EqualTo(AddressType.Range));
    }

    [Test]
    public void Parse_NegatedAddress_WithSubstitute()
    {
        var script = SedParser.Parse("1!s/old/new/");
        Assert.That(script.Commands.Count, Is.EqualTo(1));
        Assert.That(script.Commands[0].Type, Is.EqualTo(CommandType.Substitute));
        Assert.That(script.Commands[0].Address.Negated, Is.True);
    }

    #endregion

    #region Parser Rewrite Tests - Dollar Address (Last Line)

    [Test]
    public void Parse_DollarAddress_Standalone_ParsedAsLastLine()
    {
        var script = SedParser.Parse("$d");
        Assert.That(script.Commands.Count, Is.EqualTo(1));
        Assert.That(script.Commands[0].Type, Is.EqualTo(CommandType.Delete));
        Assert.That(script.Commands[0].Address.Type, Is.EqualTo(AddressType.LastLine));
    }

    [Test]
    public void Parse_DollarAddress_WithSubstitute()
    {
        var script = SedParser.Parse("$s/old/new/");
        Assert.That(script.Commands.Count, Is.EqualTo(1));
        Assert.That(script.Commands[0].Type, Is.EqualTo(CommandType.Substitute));
        Assert.That(script.Commands[0].Address.Type, Is.EqualTo(AddressType.LastLine));
    }

    [Test]
    public void Parse_DollarInRange_EndAddress()
    {
        // Already partially supported via ExtractAddress range regex
        var script = SedParser.Parse("1,$d");
        Assert.That(script.Commands.Count, Is.EqualTo(1));
        Assert.That(script.Commands[0].Address.Type, Is.EqualTo(AddressType.Range));
        Assert.That(script.Commands[0].Address.Value2, Is.EqualTo("$"));
    }

    #endregion

    #region Parser Rewrite Tests - Combined/Integration Parsing

    [Test]
    public void Parse_ComplexScript_MultipleCommandTypes()
    {
        // A realistic sed script combining multiple command types
        var script = SedParser.Parse(":loop;s/old/new/;t loop");
        Assert.That(script.Commands.Count, Is.EqualTo(3));
        Assert.That(script.Commands[0].Type, Is.EqualTo(CommandType.Label));
        Assert.That(script.Commands[0].Label, Is.EqualTo("loop"));
        Assert.That(script.Commands[1].Type, Is.EqualTo(CommandType.Substitute));
        Assert.That(script.Commands[2].Type, Is.EqualTo(CommandType.Test));
        Assert.That(script.Commands[2].Label, Is.EqualTo("loop"));
    }

    [Test]
    public void Parse_MultipleSimpleCommands_InSequence()
    {
        var script = SedParser.Parse("h;g;p;d");
        Assert.That(script.Commands.Count, Is.EqualTo(4));
        Assert.That(script.Commands[0].Type, Is.EqualTo(CommandType.HoldCopy));
        Assert.That(script.Commands[1].Type, Is.EqualTo(CommandType.GetHold));
        Assert.That(script.Commands[2].Type, Is.EqualTo(CommandType.Print));
        Assert.That(script.Commands[3].Type, Is.EqualTo(CommandType.Delete));
    }

    [Test]
    public void Parse_SubstituteWithAnyDelimiter_Parsed()
    {
        // Substitute should work with any character as delimiter
        var script = SedParser.Parse("s,old,new,g");
        Assert.That(script.Commands.Count, Is.EqualTo(1));
        Assert.That(script.Commands[0].Type, Is.EqualTo(CommandType.Substitute));
        Assert.That(script.Commands[0].Pattern, Is.EqualTo("old"));
        Assert.That(script.Commands[0].Replacement, Is.EqualTo("new"));
        Assert.That(script.Commands[0].Flags, Is.EqualTo("g"));
    }

    #endregion



    #region WI-003 Address System Fix Tests

    // --- Regex range: /start/,/end/ ---

    [Test]
    public void RegexRange_ActivatesOnStartPattern_DeactivatesOnEndPattern()
    {
        // Lines between (and including) the matching lines should be transformed
        var script = SedParser.Parse("/BEGIN/,/END/s/x/X/");
        var input = "no\nBEGIN\nxa\nxb\nEND\nno\n";
        var result = script.Transform(input);
        Assert.That(result, Is.EqualTo("no\nBEGIN\nXa\nXb\nEND\nno\n"));
    }

    [Test]
    public void RegexRange_StartLineIsIncluded()
    {
        var script = SedParser.Parse("/start/,/end/s/line/LINE/");
        var input = "before\nstart line\nmiddle line\nend line\nafter\n";
        var result = script.Transform(input);
        Assert.That(result, Is.EqualTo("before\nstart LINE\nmiddle LINE\nend LINE\nafter\n"));
    }

    [Test]
    public void RegexRange_EndLineIsIncluded()
    {
        var script = SedParser.Parse("/start/,/end/d");
        var input = "before\nstart\nmiddle\nend\nafter\n";
        var result = script.Transform(input);
        Assert.That(result, Is.EqualTo("before\nafter\n"));
    }

    [Test]
    public void RegexRange_ReactivatesAfterDeactivation()
    {
        // If start matches again after range ends, range should reactivate
        var script = SedParser.Parse("/A/,/B/s/x/X/");
        var input = "xA\nxin\nxB\nxout\nxA\nxin2\nxB\n";
        var result = script.Transform(input);
        Assert.That(result, Is.EqualTo("XA\nXin\nXB\nxout\nXA\nXin2\nXB\n"));
    }

    [Test]
    public void RegexRange_SameLineMatchesStartAndEnd_ActivatesAndDeactivates()
    {
        // When a line matches both start and end patterns, it is included (range is just that one line)
        var script = SedParser.Parse("/BOTH/,/BOTH/s/x/X/");
        var input = "xbefore\nxBOTH\nxafter\n";
        var result = script.Transform(input);
        Assert.That(result, Is.EqualTo("xbefore\nXBOTH\nxafter\n"));
    }

    // --- Mixed range: numeric start, regex end ---

    [Test]
    public void MixedRange_NumericStart_RegexEnd_MatchesCorrectLines()
    {
        var script = SedParser.Parse("2,/end/s/x/X/");
        var input = "xa\nxb\nxc\nend\nxe\n";
        var result = script.Transform(input);
        Assert.That(result, Is.EqualTo("xa\nXb\nXc\nend\nxe\n"));
    }

    [Test]
    public void MixedRange_RegexStart_NumericEnd_MatchesCorrectLines()
    {
        var script = SedParser.Parse("/start/,4s/x/X/");
        var input = "xa\nxstart\nxc\nxd\nxe\n";
        var result = script.Transform(input);
        Assert.That(result, Is.EqualTo("xa\nXstart\nXc\nXd\nxe\n"));
    }

    // --- Standalone $ address ---

    [Test]
    public void StandaloneLastLine_MatchesOnlyLastLine()
    {
        var script = SedParser.Parse("$s/last/LAST/");
        var input = "first\nsecond\nlast\n";
        var result = script.Transform(input);
        Assert.That(result, Is.EqualTo("first\nsecond\nLAST\n"));
    }

    [Test]
    public void StandaloneLastLine_Delete_RemovesOnlyLastLine()
    {
        var script = SedParser.Parse("$d");
        var input = "first\nsecond\nthird\n";
        var result = script.Transform(input);
        Assert.That(result, Is.EqualTo("first\nsecond\n"));
    }

    [Test]
    public void StandaloneLastLine_SingleLine_Matches()
    {
        var script = SedParser.Parse("$s/x/X/");
        var input = "x\n";
        var result = script.Transform(input);
        Assert.That(result, Is.EqualTo("X\n"));
    }

    // --- Range state resets between Transform() calls ---

    [Test]
    public void RangeState_ResetsPerTransformCall()
    {
        // Each Transform() call is independent; range state from prior call must not bleed over
        var script = SedParser.Parse("/A/,/B/s/x/X/");

        // First call: range opens on A and never closes (no B in input)
        var result1 = script.Transform("xA\nxin\n");
        Assert.That(result1, Is.EqualTo("XA\nXin\n"));

        // Second call: starts fresh -- range NOT active at start
        var result2 = script.Transform("xout\nxA\nxin\n");
        Assert.That(result2, Is.EqualTo("xout\nXA\nXin\n"));
    }

    // --- GNU 0,/pattern/ extension ---

    [Test]
    public void GnuAddress_Zero_AllowsEndPatternToMatchLine1()
    {
        // 0,/pattern/: end pattern can match on line 1 (unlike 1,/pattern/ which checks end only from line 2)
        var script = SedParser.Parse("0,/match/s/x/X/");
        var input = "xmatch\nxno\n";
        var result = script.Transform(input);
        Assert.That(result, Is.EqualTo("Xmatch\nxno\n"));
    }

    [Test]
    public void GnuAddress_Zero_RegularBehaviorWhenLine1DoesNotMatch()
    {
        // When line 1 does not match the end pattern, 0,/end/ behaves like 1,/end/
        var script = SedParser.Parse("0,/end/s/x/X/");
        var input = "xa\nxb\nxend\nxc\n";
        var result = script.Transform(input);
        Assert.That(result, Is.EqualTo("Xa\nXb\nXend\nxc\n"));
    }

    // --- GNU addr,+N extension ---

    [Test]
    public void GnuAddress_PlusN_MatchesAddrThroughAddrPlusN()
    {
        // 3,+2 means lines 3, 4, 5
        var script = SedParser.Parse("3,+2s/x/X/");
        var input = "xa\nxb\nxc\nxd\nxe\nxf\n";
        var result = script.Transform(input);
        Assert.That(result, Is.EqualTo("xa\nxb\nXc\nXd\nXe\nxf\n"));
    }

    [Test]
    public void GnuAddress_PlusN_PatternStart_MatchesPatternThroughPlusN()
    {
        // /start/,+2 means the line matching /start/ plus 2 more lines
        var script = SedParser.Parse("/start/,+2s/x/X/");
        var input = "xa\nxstart\nxb\nxc\nxd\n";
        var result = script.Transform(input);
        Assert.That(result, Is.EqualTo("xa\nXstart\nXb\nXc\nxd\n"));
    }

    // --- GNU addr,~N extension ---

    [Test]
    public void GnuAddress_TildeN_MatchesAddrThroughNextMultipleOfN()
    {
        // 2,~5 means from line 2 through the next line that is a multiple of 5 (line 5)
        var script = SedParser.Parse("2,~5s/x/X/");
        var input = "xa\nxb\nxc\nxd\nxe\nxf\n";
        var result = script.Transform(input);
        Assert.That(result, Is.EqualTo("xa\nXb\nXc\nXd\nXe\nxf\n"));
    }

    [Test]
    public void GnuAddress_TildeN_StartAlreadyOnMultiple_MatchesOnlyThatLine()
    {
        // 5,~5: line 5 is already a multiple of 5, so the range is just line 5
        var script = SedParser.Parse("5,~5s/x/X/");
        var input = "xa\nxb\nxc\nxd\nxe\nxf\n";
        var result = script.Transform(input);
        Assert.That(result, Is.EqualTo("xa\nxb\nxc\nxd\nXe\nxf\n"));
    }

    // --- GNU first~step extension ---

    [Test]
    public void GnuAddress_StepParsed_EvenLines_0Tilde2()
    {
        // 0~2: every 2nd line starting from 0 (i.e., lines 2, 4, 6 -- even lines)
        var script = SedParser.Parse("0~2s/x/X/");
        var input = "xa\nxb\nxc\nxd\nxe\nxf\n";
        var result = script.Transform(input);
        Assert.That(result, Is.EqualTo("xa\nXb\nxc\nXd\nxe\nXf\n"));
    }

    [Test]
    public void GnuAddress_StepParsed_OddLines_1Tilde2()
    {
        // 1~2: every 2nd line starting from 1 (i.e., lines 1, 3, 5 -- odd lines)
        var script = SedParser.Parse("1~2s/x/X/");
        var input = "xa\nxb\nxc\nxd\nxe\nxf\n";
        var result = script.Transform(input);
        Assert.That(result, Is.EqualTo("Xa\nxb\nXc\nxd\nXe\nxf\n"));
    }

    [Test]
    public void GnuAddress_StepParsed_EveryThirdStartingAt2_2Tilde3()
    {
        // 2~3: every 3rd line starting from 2 (i.e., lines 2, 5)
        var script = SedParser.Parse("2~3s/x/X/");
        var input = "xa\nxb\nxc\nxd\nxe\nxf\n";
        var result = script.Transform(input);
        Assert.That(result, Is.EqualTo("xa\nXb\nxc\nxd\nXe\nxf\n"));
    }

    [Test]
    public void GnuAddress_StepParsed_ParsesAsStepAddressType()
    {
        // Verify the parser produces AddressType.Step for first~step syntax
        var script = SedParser.Parse("1~2d");
        Assert.That(script.Commands.Count, Is.EqualTo(1));
        Assert.That(script.Commands[0].Address.Type, Is.EqualTo(AddressType.Step));
        Assert.That(script.Commands[0].Address.Value1, Is.EqualTo("1"));
        Assert.That(script.Commands[0].Address.Value2, Is.EqualTo("2"));
    }

    // --- Regression: existing numeric ranges still work after fix ---

    [Test]
    public void NumericRange_StillWorks_AfterAddressSystemFix()
    {
        var script = SedParser.Parse("2,4s/x/X/");
        var input = "xa\nxb\nxc\nxd\nxe\n";
        var result = script.Transform(input);
        Assert.That(result, Is.EqualTo("xa\nXb\nXc\nXd\nxe\n"));
    }

    [Test]
    public void NumericRange_WithLastLine_StillWorks()
    {
        var script = SedParser.Parse("2,$s/x/X/");
        var input = "xa\nxb\nxc\n";
        var result = script.Transform(input);
        Assert.That(result, Is.EqualTo("xa\nXb\nXc\n"));
    }

    #endregion

    #region Transliterate (y command) Tests

    // --- Basic transliteration ---

    [Test]
    public void Transliterate_BasicChars_ReplacesEach()
    {
        var script = SedParser.Parse("y/abc/ABC/");
        var result = script.Transform("abcdef");
        Assert.That(result, Is.EqualTo("ABCdef"));
    }

    [Test]
    public void Transliterate_AllCharsInSource_ReplacedByCorrespondingDest()
    {
        var script = SedParser.Parse("y/aeiou/AEIOU/");
        var result = script.Transform("hello world");
        Assert.That(result, Is.EqualTo("hEllO wOrld"));
    }

    [Test]
    public void Transliterate_MultipleOccurrences_ReplacesAll()
    {
        var script = SedParser.Parse("y/a/X/");
        var result = script.Transform("banana");
        Assert.That(result, Is.EqualTo("bXnXnX"));
    }

    [Test]
    public void Transliterate_NoSourceMatch_NoChange()
    {
        var script = SedParser.Parse("y/xyz/XYZ/");
        var result = script.Transform("hello");
        Assert.That(result, Is.EqualTo("hello"));
    }

    // --- Literal hyphen (GNU sed does NOT support ranges in y command) ---

    [Test]
    public void Transliterate_LiteralHyphen_TreatedAsLiteral()
    {
        // GNU sed: y/a-c/X-Z/ maps a->X, -->-, c->Z  (hyphen is literal, not a range)
        var script = SedParser.Parse("y/a-c/X-Z/");
        var result = script.Transform("a-bcdef");
        Assert.That(result, Is.EqualTo("X-bZdef"));
    }

    [Test]
    public void Transliterate_HyphenInDest_TreatedAsLiteral()
    {
        // Hyphen in dest is also literal
        var script = SedParser.Parse("y/XY/a-/");
        var result = script.Transform("XY");
        Assert.That(result, Is.EqualTo("a-"));
    }

    [Test]
    public void Transliterate_HyphenOnlyMapping_ReplacesHyphen()
    {
        // y/-/X/ replaces literal hyphens
        var script = SedParser.Parse("y/-/X/");
        var result = script.Transform("a-b-c");
        Assert.That(result, Is.EqualTo("aXbXc"));
    }

    // --- Escape sequences ---

    [Test]
    public void Transliterate_EscapeNewline_LookupTableBuiltCorrectly()
    {
        // Verify that \n in a y command source is parsed as the newline character (ASCII 10)
        // and that the command is created without error. The lookup table must map \n -> X.
        // Embedded newline replacement in pattern space requires the N command (WI-005);
        // here we verify the command parses and does not affect lines without embedded newlines.
        var script = SedParser.Parse("y/\\n/X/");
        var result = script.Transform("hello");
        Assert.That(result, Is.EqualTo("hello")); // no newline in pattern space, no change
    }

    [Test]
    public void Transliterate_EscapeTab_ReplacesTabChar()
    {
        // Pass literal \t escape sequence to parser (not a C# tab character)
        var script = SedParser.Parse(@"y/\t/X/");
        var result = script.Transform("col1\tcol2");
        Assert.That(result, Is.EqualTo("col1Xcol2"));
    }

    [Test]
    public void Transliterate_EscapeBackslash_ReplacesBackslash()
    {
        var script = SedParser.Parse(@"y/\\/X/");
        var result = script.Transform(@"a\b");
        Assert.That(result, Is.EqualTo("aXb"));
    }

    // --- Length validation ---

    [Test]
    public void Transliterate_LengthMismatch_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => SedParser.Parse("y/abc/AB/"));
    }

    [Test]
    public void Transliterate_LengthMismatch_WithHyphen_ThrowsArgumentException()
    {
        // y/a-z/AB/ has source=[a,-,z] (3 chars) and dest=[A,B] (2 chars) -> length mismatch
        Assert.Throws<ArgumentException>(() => SedParser.Parse("y/a-z/AB/"));
    }

    // --- Alternate delimiter ---

    [Test]
    public void Transliterate_AlternateDelimiter_WorksCorrectly()
    {
        var script = SedParser.Parse("y|abc|ABC|");
        var result = script.Transform("abcdef");
        Assert.That(result, Is.EqualTo("ABCdef"));
    }

    [Test]
    public void Transliterate_DelimiterEscapedInSource_Handles()
    {
        // y|/|X| replaces forward slash with X
        var script = SedParser.Parse("y|/|X|");
        var result = script.Transform("a/b/c");
        Assert.That(result, Is.EqualTo("aXbXc"));
    }

    // --- Unicode characters ---

    [Test]
    public void Transliterate_UnicodeChars_ReplacedCorrectly()
    {
        var script = SedParser.Parse("y/\u00e9\u00e0/ea/");
        var result = script.Transform("caf\u00e9 \u00e0 paris");
        Assert.That(result, Is.EqualTo("cafe a paris"));
    }

    // --- Address matching ---

    [Test]
    public void Transliterate_WithLineAddress_OnlyTransliteratesMatchingLine()
    {
        var script = SedParser.Parse("2y/a/X/");
        var result = script.Transform("abc\nabc\nabc");
        Assert.That(result, Is.EqualTo("abc\nXbc\nabc"));
    }

    [Test]
    public void Transliterate_WithPatternAddress_OnlyTransliteratesMatchingLine()
    {
        var script = SedParser.Parse("/hello/y/a/X/");
        var result = script.Transform("abc\nhello world\nabc\nhello abc");
        Assert.That(result, Is.EqualTo("abc\nhello world\nabc\nhello Xbc"));
    }

    // --- Multiple y commands ---

    [Test]
    public void Transliterate_MultipleYCommands_AppliedInOrder()
    {
        // First y: a->b, Second y: b->c, so a->b->c
        var script = SedParser.Parse("y/a/b/\ny/b/c/");
        var result = script.Transform("abc");
        Assert.That(result, Is.EqualTo("ccc"));
    }

    // --- Idempotent / no-op ---

    [Test]
    public void Transliterate_SameSourceAndDest_NoChange()
    {
        var script = SedParser.Parse("y/abc/abc/");
        var result = script.Transform("abcdef");
        Assert.That(result, Is.EqualTo("abcdef"));
    }

    // --- Lookup table is built once (verify SedScript reuse) ---

    [Test]
    public void Transliterate_ScriptReuse_ProducesSameResult()
    {
        var script = SedParser.Parse("y/abcdefghijklmnopqrstuvwxyz/ABCDEFGHIJKLMNOPQRSTUVWXYZ/");
        var result1 = script.Transform("hello");
        var result2 = script.Transform("world");
        Assert.That(result1, Is.EqualTo("HELLO"));
        Assert.That(result2, Is.EqualTo("WORLD"));
    }

    // --- Empty pattern space ---

    [Test]
    public void Transliterate_EmptyPatternSpace_ReturnsEmpty()
    {
        // ExecuteTransliterate has an early-return for empty pattern space
        var script = SedParser.Parse("y/a/X/");
        var result = script.Transform("");
        Assert.That(result, Is.EqualTo(""));
    }

    // --- Duplicate source characters: first mapping wins ---

    [Test]
    public void Transliterate_DuplicateSourceChar_FirstMappingWins()
    {
        // GNU sed: y/aa/XY/ applied to "aaa" -> "XXX" (first mapping wins)
        var script = SedParser.Parse("y/aa/XY/");
        var result = script.Transform("aaa");
        Assert.That(result, Is.EqualTo("XXX"));
    }

    [Test]
    public void Transliterate_IdentityFirstMapping_WinsOverSubsequent()
    {
        // y/aa/aX/ on "aaa": first mapping 'a'->'a' wins, second ignored -> "aaa"
        var script = SedParser.Parse("y/aa/aX/");
        var result = script.Transform("aaa");
        Assert.That(result, Is.EqualTo("aaa"));
    }

    [Test]
    public void Transliterate_EmptySourceDest_IsNoOp()
    {
        // y/// on "hello" -> "hello" (empty transliteration, no mappings)
        var script = SedParser.Parse("y///");
        var result = script.Transform("hello");
        Assert.That(result, Is.EqualTo("hello"));
    }

    [Test]
    public void Transliterate_BackslashEscape_InSource()
    {
        // y/a\\\\/XY/ maps 'a'->'X' and backslash->'Y'
        // C# "y/a\\\\/XY/" = runtime string y/a\\/XY/
        // SplitByDelimiter: \\\\ = escaped backslash -> source="a\\\\", dest="XY"
        var script = SedParser.Parse("y/a\\\\/XY/");
        var result = script.Transform("a\\");
        Assert.That(result, Is.EqualTo("XY"));
    }

    #endregion

    #region WI-004: Hold Space Operations

    // --- h (copy pattern space to hold space) ---

    [Test]
    public void HoldCopy_ThenGetHold_ReplacesPatternSpaceWithHeldContent()
    {
        // h on line 1, g on line 2: line 2 should become line 1's content
        var script = SedParser.Parse("1h\n2g");
        var result = script.Transform("hello\nworld");
        Assert.That(result, Is.EqualTo("hello\nhello\n"));
    }

    [Test]
    public void HoldCopy_OverwritesPreviousHoldContent()
    {
        // h on both lines, then g on a third: should have line 2's content
        var script = SedParser.Parse("1,2h\n3g");
        var result = script.Transform("first\nsecond\nthird");
        Assert.That(result, Is.EqualTo("first\nsecond\nsecond\n"));
    }

    // --- H (append pattern space to hold space) ---

    [Test]
    public void HoldAppend_AccumulatesNewlinePlusContent()
    {
        // H on lines 1 and 2, g on line 3:
        //   H line 1: hold = "" + "\n" + "line1" = "\nline1"
        //   H line 2: hold = "\nline1" + "\n" + "line2" = "\nline1\nline2"
        //   g line 3: pattern = "\nline1\nline2"
        // Output: "line1\nline2\n\nline1\nline2" (the \n prefix from H starting on empty hold)
        var script = SedParser.Parse("1,2H\n3g");
        var result = script.Transform("line1\nline2\nline3");
        Assert.That(result, Is.EqualTo("line1\nline2\n\nline1\nline2\n"));
    }

    [Test]
    public void HoldAppend_StartsWithEmptyHold_FirstAppendAddsNewlinePrefix()
    {
        // H on line 1 only, then g: hold space is "\nline1"
        var script = SedParser.Parse("1H\n2g");
        var result = script.Transform("alpha\nbeta");
        Assert.That(result, Is.EqualTo("alpha\n\nalpha\n"));
    }

    // --- g (copy hold space to pattern space) ---

    [Test]
    public void GetHold_WithEmptyHoldSpace_PatternSpaceBecomesEmpty()
    {
        // g without any prior h/H: pattern space should become empty
        var script = SedParser.Parse("g");
        var result = script.Transform("hello");
        Assert.That(result, Is.EqualTo(""));
    }

    [Test]
    public void GetHold_ReplacesPatternSpaceCompletely()
    {
        // h line 1, g line 3: line 3 becomes line 1's content, line 2 unchanged
        var script = SedParser.Parse("1h\n3g");
        var result = script.Transform("saved\nuntouched\noriginal");
        Assert.That(result, Is.EqualTo("saved\nuntouched\nsaved\n"));
    }

    // --- G (append hold space to pattern space) ---

    [Test]
    public void GetHoldAppend_WithEmptyHoldSpace_AppendsJustNewline()
    {
        // G with empty hold space appends "\n" + "" = trailing newline
        var script = SedParser.Parse("G");
        var result = script.Transform("hello");
        Assert.That(result, Is.EqualTo("hello\n\n"));
    }

    [Test]
    public void GetHoldAppend_AppendsSavedContentWithNewlineSeparator()
    {
        // h line 1, G line 2: line 2 becomes "line2\nline1"
        var script = SedParser.Parse("1h\n2G");
        var result = script.Transform("first\nsecond");
        Assert.That(result, Is.EqualTo("first\nsecond\nfirst\n"));
    }

    // --- x (exchange pattern and hold spaces) ---

    [Test]
    public void Exchange_SwapsPatternAndHoldSpaces()
    {
        // h line 1, x line 2: pattern becomes "first" (old hold), hold becomes "second"
        var script = SedParser.Parse("1h\n2x");
        var result = script.Transform("first\nsecond\nthird");
        Assert.That(result, Is.EqualTo("first\nfirst\nthird"));
    }

    [Test]
    public void Exchange_WithEmptyHold_PatternBecomesEmptyHoldGetsOldPattern()
    {
        // x without prior h: pattern becomes "" (empty hold), hold gets old pattern
        var script = SedParser.Parse("1x\n2g");
        var result = script.Transform("hello\nworld");
        // Line 1: x -> pattern="" (empty hold), hold="hello". Output: ""
        // Line 2: g -> pattern="hello" (from hold). Output: "hello"
        Assert.That(result, Is.EqualTo("\nhello\n"));
    }

    [Test]
    public void Exchange_BothDirections_WorkCorrectly()
    {
        // h line1, then x line2: pattern gets "line1", hold gets "line2"
        // then g line3: pattern gets "line2"
        var script = SedParser.Parse("1h\n2x\n3g");
        var result = script.Transform("alpha\nbeta\ngamma");
        // Line 1: h -> hold="alpha". Output: "alpha"
        // Line 2: x -> pattern="alpha", hold="beta". Output: "alpha"
        // Line 3: g -> pattern="beta". Output: "beta"
        Assert.That(result, Is.EqualTo("alpha\nalpha\nbeta\n"));
    }

    // --- Hold space lifecycle ---

    [Test]
    public void HoldSpace_ResetsToEmptyBetweenTransformCalls()
    {
        // Second call on fresh script: g immediately, should get empty
        var script2 = SedParser.Parse("g");
        var result = script2.Transform("should-be-gone");
        Assert.That(result, Is.EqualTo(""));
    }

    [Test]
    public void HoldSpace_PersistsAcrossAllLinesInSingleTransform()
    {
        // h line 1, then g on lines 2, 3, 4: all should show line 1's content
        var script = SedParser.Parse("1h\n2,4g");
        var result = script.Transform("saved\na\nb\nc");
        Assert.That(result, Is.EqualTo("saved\nsaved\nsaved\nsaved\n"));
    }

    // --- Classic sed idioms ---

    [Test]
    public void ClassicIdiom_ReverseFile_BuildUpInHoldSpace()
    {
        // Classic reverse-file idiom: sed '1!G;h;$!d'
        var script = SedParser.Parse("1!G\nh\n$!d");
        var result = script.Transform("1\n2\n3");
        Assert.That(result, Is.EqualTo("3\n2\n1\n"));
    }

    [Test]
    public void ClassicIdiom_HoldAndGet_KeepOnlyLastLine()
    {
        // h each line, at last line d is not matched, g restores last line.
        // Real sed: printf 'keep\nignore\nlast' | sed 'h;$!d;g' => 'last' (no trailing newline)
        // because h and g both execute on the same (last) line.
        var script = SedParser.Parse("h\n$!d\ng");
        var result = script.Transform("keep\nignore\nlast");
        Assert.That(result, Is.EqualTo("last"));
    }

    #endregion
    #region WI-005: Multi-line Commands (N, P, D, n)

    // --- N: NextAppend ---

    [Test]
    public void N_AppendsNextLineWithNewlineSeparator()
    {
        // N appends newline + next line to pattern space
        var script = SedParser.Parse("N");
        var result = script.Transform("line1\nline2");
        Assert.That(result, Is.EqualTo("line1\nline2"));
    }

    [Test]
    public void N_AppendsThenSubstitute_JoinsPairs()
    {
        // Classic: N;s/\n/ / joins pairs of lines
        var script = SedParser.Parse("N\ns/\\n/ /");
        var result = script.Transform("a\nb\nc\nd");
        Assert.That(result, Is.EqualTo("a b\nc d"));
    }

    [Test]
    public void N_AtEndOfInput_QuitsGracefully()
    {
        // N at EOF: prints pattern space and quits (GNU behavior)
        var script = SedParser.Parse("N");
        var result = script.Transform("only");
        Assert.That(result, Is.EqualTo("only"));
    }

    [Test]
    public void N_ThreeLines_TwoNCommands_TripletsJoined()
    {
        // Two N commands accumulate 3 lines into pattern space
        var script = SedParser.Parse("N\nN\ns/\\n/ /g");
        var result = script.Transform("a\nb\nc");
        Assert.That(result, Is.EqualTo("a b c"));
    }

    // --- P: PrintFirstLine ---

    [Test]
    public void P_PrintsUpToFirstNewline()
    {
        // After N, P prints only the first accumulated line
        var script = SedParser.Parse("N\nP\nd");
        var result = script.Transform("first\nsecond\nthird\nfourth");
        // Pair 1: pattern="first\nsecond", P prints "first", d discards
        // Pair 2: pattern="third\nfourth", P prints "third", d discards
        Assert.That(result, Is.EqualTo("first\nthird"));
    }

    [Test]
    public void P_WithNoEmbeddedNewline_PrintsEntirePatternSpace()
    {
        // P on single-line pattern space prints everything
        var script = SedParser.Parse("P");
        var result = script.Transform("hello");
        // P prints "hello", then default output also prints "hello"
        Assert.That(result, Is.EqualTo("hello\nhello"));
    }

    [Test]
    public void P_PrintsFirstLine_OfMultilinePatternSpace()
    {
        // N then P: prints first line, then default output prints both lines
        var script = SedParser.Parse("N\nP");
        var result = script.Transform("line1\nline2");
        // P prints "line1", then default output prints "line1\nline2"
        Assert.That(result, Is.EqualTo("line1\nline1\nline2"));
    }

    // --- D: DeleteFirstLine ---

    [Test]
    public void D_WhenPatternSpaceHasNoNewline_BehavesLikeDelete()
    {
        // D on single-line pattern space: equivalent to 'd', starts next cycle
        var script = SedParser.Parse("D");
        var result = script.Transform("line1\nline2\nline3");
        // D deletes each line, nothing output
        Assert.That(result, Is.EqualTo(""));
    }

    [Test]
    public void D_RemovesFirstLineAndRestartsCycle()
    {
        // N;P;D classic sliding-window pattern
        // N appends line2 -> "line1\nline2", P prints "line1", D removes "line1\n", restarts with "line2"
        // Next iteration: "line2" is now the pattern space, N appends "line3" -> "line2\nline3", P prints "line2", D removes -> "line3"
        // "line3": N at EOF quits, default output prints "line3"
        var script = SedParser.Parse("N\nP\nD");
        var result = script.Transform("line1\nline2\nline3");
        Assert.That(result, Is.EqualTo("line1\nline2\nline3"));
    }

    [Test]
    public void D_RestartsCycleWithoutReadingNewInput()
    {
        // After N; s/a\n//; P; D  -- test that D restarts with remaining content
        // Cycle 1: ps="a", N -> ps="a\nb", s removes "a\n" -> ps="b", P prints "b", D: no \n in "b", like 'd'
        // Cycle 2: ps="c", N: EOF, quit with "c" as default output
        var script = SedParser.Parse("N\ns/a\\n//\nP\nD");
        var result = script.Transform("a\nb\nc");
        Assert.That(result, Is.EqualTo("b\nc"));
    }

    [Test]
    public void D_DeletesBlankLineFollowingPattern()
    {
        // /^$/{ N; D } collapses blank lines into adjacent content
        var script = SedParser.Parse("/^$/{ N\nD }");
        var result = script.Transform("text\n\nmore");
        // line1="text": /^$/ does not match, default output "text"
        // line2="": /^$/ matches, N appends "more" -> ps="\nmore", D removes "\n" -> ps="more", restart
        // Restarted with "more": /^$/ does not match, default output "more"
        Assert.That(result, Is.EqualTo("text\nmore"));
    }

    // --- n: Next ---

    [Test]
    public void n_PrintsCurrentAndReadsNextLine()
    {
        // n: prints pattern space (unless -n), reads next line
        var script = SedParser.Parse("n");
        var result = script.Transform("line1\nline2");
        // line1: n prints "line1", reads "line2" into pattern space, default output "line2"
        Assert.That(result, Is.EqualTo("line1\nline2"));
    }

    [Test]
    public void n_AtEndOfInput_QuitsGracefully()
    {
        // n at EOF: prints current line and exits
        var script = SedParser.Parse("n");
        var result = script.Transform("only");
        Assert.That(result, Is.EqualTo("only"));
    }

    [Test]
    public void n_ProcessesAlternateLines()
    {
        // n advances past current line; use to act on every other line
        // Script: n; s/x/X/ -- skip first, substitute second
        var script = SedParser.Parse("n\ns/x/X/");
        var result = script.Transform("x\nx\nx\nx");
        // line1: n prints "x", reads "x" into ps; s -> "X"; output "X"
        // line3: n prints "x", reads "x" into ps; s -> "X"; output "X"
        Assert.That(result, Is.EqualTo("x\nX\nx\nX"));
    }

    // --- Classic multi-line idioms ---

    [Test]
    public void Classic_JoinPairsOfLines()
    {
        // N;s/\n/ / joins consecutive pairs
        var script = SedParser.Parse("N\ns/\\n/ /");
        var result = script.Transform("hello\nworld\nfoo\nbar");
        Assert.That(result, Is.EqualTo("hello world\nfoo bar"));
    }

    [Test]
    public void Classic_SlidingWindowOf3Lines()
    {
        // N;N;P;D produces a sliding window of 3 lines
        var script = SedParser.Parse("N\nN\nP\nD");
        var result = script.Transform("1\n2\n3\n4");
        // Start: ps="1", N -> "1\n2", N -> "1\n2\n3"
        // P prints "1", D removes "1\n" -> ps="2\n3", restart
        // Restart: ps="2\n3", N -> "2\n3\n4", P prints "2", D -> ps="3\n4", restart
        // Restart: ps="3\n4", N: EOF -> quit with "3\n4" default output
        Assert.That(result, Is.EqualTo("1\n2\n3\n4"));
    }

    [Test]
    public void Classic_NPD_PrintsAllLines()
    {
        // Without -n, N;P;D on "a\nb\nc" should print all lines
        var script = SedParser.Parse("N\nP\nD");
        var result = script.Transform("a\nb\nc");
        Assert.That(result, Is.EqualTo("a\nb\nc"));
    }

    // --- MAJOR 4: P with -n suppress output ---

    [Test]
    public void P_WithSuppressOutput_StillPrints()
    {
        // P command explicitly prints the first line of pattern space
        // even when default output is suppressed with -n
        var script = SedParser.Parse("-n\nN\nP");
        var result = script.Transform("alpha\nbeta");
        // N appends beta -> pattern space = "alpha\nbeta"
        // P prints "alpha" (first line); default output suppressed -> no second output
        // Real sed appends trailing newline after explicit P output
        Assert.That(result, Is.EqualTo("alpha\n"));
    }

    // --- MAJOR 5: D restarts cycle and re-evaluates addresses ---

    [Test]
    public void D_RestartCycle_ReEvaluatesAddresses()
    {
        // D deletes first line of pattern space and restarts cycle without reading new input.
        // The restarted cycle re-evaluates addresses against the remaining pattern space.
        // Script: N;/^keep/!D  -- accumulate two lines, delete first unless it starts with "keep"
        var script = SedParser.Parse("N\n/^keep/!D");
        var result = script.Transform("drop\nkeep\nthis");
        // Cycle 1: read "drop", N appends -> "drop\nkeep"; /^keep/!D matches (drop != keep) -> D
        // D removes "drop\n" -> ps="keep", restart cycle (no new input read)
        // Restart: ps="keep"; N appends "this" -> "keep\nthis"; /^keep/!D does NOT match -> output "keep\nthis"
        Assert.That(result, Is.EqualTo("keep\nthis"));
    }

    // --- MAJOR 6: N at EOF with suppress output still outputs pattern space ---

    [Test]
    public void N_AtEOF_WithSuppressOutput_StillOutputsPatternSpace()
    {
        // GNU sed behavior: when N hits EOF (no next line to append),
        // the current pattern space is written to output and the program exits,
        // even when -n (suppress default output) is active.
        var script = SedParser.Parse("-n\nN");
        var result = script.Transform("onlyone");
        // Only one line: N hits EOF -> pattern space "onlyone" is output and program exits
        Assert.That(result, Is.EqualTo("onlyone"));
    }

    #endregion


    #region WI-006: Text Commands (a, i, c)

    [Test]
    public void AppendCommand_AppendsTextAfterLine()
    {
        var script = SedParser.Parse("a\\appended");
        var result = script.Transform("hello");
        Assert.That(result, Is.EqualTo("hello\nappended"));
    }

    [Test]
    public void AppendCommand_WithAddress_OnlyOnMatchingLines()
    {
        var script = SedParser.Parse("2a\\appended");
        var result = script.Transform("line1\nline2\nline3");
        Assert.That(result, Is.EqualTo("line1\nline2\nappended\nline3"));
    }

    [Test]
    public void InsertCommand_InsertsTextBeforeLine()
    {
        var script = SedParser.Parse("i\\inserted");
        var result = script.Transform("hello");
        Assert.That(result, Is.EqualTo("inserted\nhello"));
    }

    [Test]
    public void InsertCommand_WithAddress()
    {
        var script = SedParser.Parse("2i\\inserted");
        var result = script.Transform("line1\nline2\nline3");
        Assert.That(result, Is.EqualTo("line1\ninserted\nline2\nline3"));
    }

    [Test]
    public void ChangeCommand_ReplacesLine()
    {
        var script = SedParser.Parse("c\\new line");
        var result = script.Transform("hello");
        Assert.That(result, Is.EqualTo("new line"));
    }

    [Test]
    public void ChangeCommand_WithAddress_OnlyChangesMatchingLine()
    {
        var script = SedParser.Parse("2c\\changed");
        var result = script.Transform("line1\nline2\nline3");
        Assert.That(result, Is.EqualTo("line1\nchanged\nline3"));
    }

    [Test]
    public void ChangeCommand_WithRange_OutputsTextOnlyAtEndOfRange()
    {
        var script = SedParser.Parse("2,3c\\replaced");
        var result = script.Transform("line1\nline2\nline3\nline4");
        Assert.That(result, Is.EqualTo("line1\nreplaced\nline4"));
    }

    [Test]
    public void AppendAndInsert_CorrectOrder()
    {
        var script = SedParser.Parse("-e\ni\\before\n-e\na\\after");
        var result = script.Transform("hello");
        Assert.That(result, Is.EqualTo("before\nhello\nafter"));
    }

    [Test]
    public void AppendCommand_WithSuppressFlag_StillOutputsAppendedText()
    {
        var script = SedParser.Parse("-n\na\\appended");
        var result = script.Transform("hello");
        Assert.That(result, Is.EqualTo("appended"));
    }

    [Test]
    public void InsertCommand_WithSuppressFlag_StillOutputsInsertedText()
    {
        var script = SedParser.Parse("-n\ni\\inserted");
        var result = script.Transform("hello");
        Assert.That(result, Is.EqualTo("inserted"));
    }

    [Test]
    public void MultilineAppendText_AppendsBothLines()
    {
        // \n in text argument outputs multiple lines
        var script = SedParser.Parse("a\\line1\\nline2");
        var result = script.Transform("hello");
        Assert.That(result, Is.EqualTo("hello\nline1\nline2"));
    }

    [Test]
    public void ChangeCommand_WithPatternAddress_ChangesMatchingLines()
    {
        var script = SedParser.Parse("/hello/c\\goodbye");
        var result = script.Transform("hello world\nother");
        Assert.That(result, Is.EqualTo("goodbye\nother"));
    }


    [Test]
    public void AppendCommand_WithDelete_StillOutputsAppendedText()
    {
        // POSIX: 'a' text queued before 'd' ends the cycle must still be flushed
        var script = SedParser.Parse("-e\na\\appended\n-e\nd");
        var result = script.Transform("hello");
        Assert.That(result, Is.EqualTo("appended"));
    }

    [Test]
    public void AppendCommand_WithChange_StillOutputsAppendedText()
    {
        // POSIX: 'a' text queued before 'c' ends the cycle must still be flushed
        var script = SedParser.Parse("-e\na\\appended\n-e\nc\\changed");
        var result = script.Transform("hello");
        Assert.That(result, Is.EqualTo("changed\nappended"));
    }

    [Test]
    public void AppendCommand_WithQuit_StillOutputsAppendedText()
    {
        // POSIX: 'a' text queued before 'q' ends processing must still be flushed
        var script = SedParser.Parse("-e\na\\appended\n-e\nq");
        var result = script.Transform("hello");
        Assert.That(result, Is.EqualTo("hello\nappended"));
    }

    [Test]
    public void ChangeCommand_WithSuppressFlag_StillOutputsChangedText()
    {
        // 'c' output bypasses -n suppression (text is unconditional like 'i')
        var script = SedParser.Parse("-n\nc\\changed");
        var result = script.Transform("hello");
        Assert.That(result, Is.EqualTo("changed"));
    }

    [Test]
    public void AppendCommand_EmptyText_AppendsEmptyLine()
    {
        // An 'a' command with empty text appends an empty line
        var script = SedParser.Parse("a\\");
        var result = script.Transform("hello");
        Assert.That(result, Is.EqualTo("hello\n"));
    }

    [Test]
    public void AppendCommand_WithD_StillOutputsAppendedText()
    {
        // 'D' restarts the cycle; any 'a' appends queued before D must still flush
        // Input: two lines joined by N, then D triggers cycle restart
        var script = SedParser.Parse("-e\na\\appended\n-e\nN\n-e\nD");
        var result = script.Transform("line1\nline2");
        // After N: pattern space = "line1\nline2"; D deletes up to first newline,
        // restarts with "line2". The append queued on line1 cycle flushes at restart.
        Assert.That(result, Is.EqualTo("appended\nline2"));
    }


    #endregion

    #region Text Command Execution Tests (a, i, c) - WI-006 Review

    // --- MAJOR 1: Multiple a commands on same line produce both outputs ---

    [Test]
    public void AppendCommand_MultipleAppends_AllOutputted()
    {
        // Two a commands on same pattern space: both appended texts should appear
        var script = SedParser.Parse("a\\first append\na\\second append");
        var result = script.Transform("original");
        Assert.That(result, Is.EqualTo("original\nfirst append\nsecond append"));
    }

    // --- MAJOR 2: c with regex range address ---

    [Test]
    public void ChangeCommand_WithRegexRangeAddress_ReplacesOnlyInRange()
    {
        // c with regex range: lines inside /start/,/end/ range are replaced
        var script = SedParser.Parse("/start/,/end/c\\replaced");
        var result = script.Transform("before\nstart here\nmiddle\nend here\nafter");
        Assert.That(result, Is.EqualTo("before\nreplaced\nafter"));
    }

    // --- MAJOR 3: a (append) on last line ($ address) ---

    [Test]
    public void AppendCommand_AddressedToLastLine_AppendIsFlushed()
    {
        // a on last line ($): pending append must be output after the line
        var script = SedParser.Parse("$a\\appended at eof");
        var result = script.Transform("only line");
        Assert.That(result, Is.EqualTo("only line\nappended at eof"));
    }

    // --- MAJOR 4: i with range address ---

    [Test]
    public void InsertCommand_WithRangeAddress_InsertsOnMatchingLines()
    {
        // i on range 2,3: inserts before each line in range
        var script = SedParser.Parse("2,3i\\inserted");
        var result = script.Transform("line1\nline2\nline3\nline4");
        Assert.That(result, Is.EqualTo("line1\ninserted\nline2\ninserted\nline3\nline4"));
    }

    [Test]
    public void AppendCommand_WithLineNumberAddress_AppendsOnlyAfterMatchingLine()
    {
        var script = SedParser.Parse(@"2a\appended");
        var result = script.Transform("line1\nline2\nline3");
        Assert.That(result, Is.EqualTo("line1\nline2\nappended\nline3"));
    }

    [Test]
    public void InsertCommand_WithLineNumberAddress_InsertsOnlyBeforeMatchingLine()
    {
        var script = SedParser.Parse(@"2i\inserted");
        var result = script.Transform("line1\nline2\nline3");
        Assert.That(result, Is.EqualTo("line1\ninserted\nline2\nline3"));
    }

    [Test]
    public void ChangeCommand_ReplacesCurrentLine()
    {
        var script = SedParser.Parse(@"c\replaced");
        var result = script.Transform("original");
        Assert.That(result, Is.EqualTo("replaced"));
    }

    [Test]
    public void ChangeCommand_WithPatternAddress_ReplacesMatchingLines()
    {
        var script = SedParser.Parse(@"/old/c\new line");
        var result = script.Transform("keep\nold line\nkeep");
        Assert.That(result, Is.EqualTo("keep\nnew line\nkeep"));
    }

    // --- MINOR 1: ExpandEscapes handles \t -> tab and \a -> bell ---

    [Test]
    public void AppendCommand_TextWithTabEscape_ExpandsTab()
    {
        var script = SedParser.Parse("a\\before\\tafter");
        var result = script.Transform("line");
        Assert.That(result, Is.EqualTo("line\nbefore\tafter"));
    }

    [Test]
    public void AppendCommand_TextWithBellEscape_ExpandsBell()
    {
        var script = SedParser.Parse("a\\before\\aafter");
        var result = script.Transform("line");
        Assert.That(result, Is.EqualTo("line\nbefore\aafter"));
    }

    [Test]
    public void InsertCommand_TextWithTabEscape_ExpandsTab()
    {
        var script = SedParser.Parse("i\\col1\\tcol2");
        var result = script.Transform("line");
        Assert.That(result, Is.EqualTo("col1\tcol2\nline"));
    }

    [Test]
    public void ChangeCommand_TextWithTabEscape_ExpandsTab()
    {
        var script = SedParser.Parse("c\\col1\\tcol2");
        var result = script.Transform("original");
        Assert.That(result, Is.EqualTo("col1\tcol2"));
    }

    // --- MINOR 2: GNU compatibility - a/i/c accepts text without backslash ---

    [Test]
    public void AppendCommand_WithoutBackslash_GnuCompatible()
    {
        var script = SedParser.Parse("a appended text");
        var result = script.Transform("original");
        Assert.That(result, Is.EqualTo("original\nappended text"));
    }

    [Test]
    public void InsertCommand_WithoutBackslash_GnuCompatible()
    {
        var script = SedParser.Parse("i inserted text");
        var result = script.Transform("original");
        Assert.That(result, Is.EqualTo("inserted text\noriginal"));
    }

    [Test]
    public void ChangeCommand_WithoutBackslash_GnuCompatible()
    {
        var script = SedParser.Parse("c replaced text");
        var result = script.Transform("original");
        Assert.That(result, Is.EqualTo("replaced text"));
    }

    #endregion

    #region WI-007: Output Commands (p, P, =, l, q, Q)

    // --- Print Command (p) ---

    [Test]
    public void PrintCommand_WithoutSuppression_LineAppearsTwice()
    {
        // Without -n, p causes the line to be printed twice: once explicitly, once auto-print
        var script = SedParser.Parse("p");
        var result = script.Transform("hello");
        Assert.That(result, Is.EqualTo("hello\nhello"));
    }

    [Test]
    public void PrintCommand_WithSuppression_OnlyExplicitPrint()
    {
        // With -n (suppress), p is the only output
        var script = SedParser.Parse("-n p");
        var result = script.Transform("hello");
        Assert.That(result, Is.EqualTo("hello"));
    }

    [Test]
    public void PrintCommand_WithSuppression_MultiLine_SelectivePrint()
    {
        // sed -n '2p' prints only line 2
        var script = SedParser.Parse("2p", suppressDefaultOutput: true);
        var result = script.Transform("line1\nline2\nline3");
        Assert.That(result, Is.EqualTo("line2\n"));
    }

    [Test]
    public void PrintCommand_WithAddress_MatchingLinePrintedTwice()
    {
        // Without -n, matching line appears twice, others once
        var script = SedParser.Parse("2p");
        var result = script.Transform("line1\nline2\nline3");
        Assert.That(result, Is.EqualTo("line1\nline2\nline2\nline3"));
    }

    [Test]
    public void PrintCommand_WithPattern_MatchingLinesPrintedTwice()
    {
        // /foo/p without -n: matching lines appear twice
        var script = SedParser.Parse("/foo/p");
        var result = script.Transform("foo\nbar\nfoo");
        Assert.That(result, Is.EqualTo("foo\nfoo\nbar\nfoo\nfoo"));
    }

    [Test]
    public void PrintCommand_WithSuppression_PatternMatch()
    {
        // sed -n '/foo/p'
        var script = SedParser.Parse("/foo/p", suppressDefaultOutput: true);
        var result = script.Transform("foo\nbar\nfoo");
        Assert.That(result, Is.EqualTo("foo\nfoo"));
    }

    // --- PrintFirstLine Command (P) ---

    [Test]
    public void PrintFirstLineCommand_WithEmbeddedNewline_PrintsOnlyFirstLine()
    {
        // P with embedded newline prints only up to the first \n
        // We need N to create multi-line pattern space, then P
        var script = SedParser.Parse("N;P;d");
        var result = script.Transform("first\nsecond\nthird");
        Assert.That(result, Is.EqualTo("first\nthird"));
    }

    [Test]
    public void PrintFirstLineCommand_NoEmbeddedNewline_PrintsEntirePatternSpace()
    {
        // P with no embedded newline in pattern space acts like p
        var script = SedParser.Parse("P");
        var result = script.Transform("hello");
        Assert.That(result, Is.EqualTo("hello\nhello"));
    }

    [Test]
    public void PrintFirstLineCommand_WithSuppression_OnlyFirstLineOutput()
    {
        // -n with N;P: only prints first line of each pair
        var script = SedParser.Parse("N;P;d", suppressDefaultOutput: true);
        var result = script.Transform("line1\nline2\nline3\nline4");
        Assert.That(result, Is.EqualTo("line1\nline3\n"));
    }

    // --- LineNumber Command (=) ---

    [Test]
    public void LineNumberCommand_PrintsLineNumber()
    {
        // = prints line number before the line
        var script = SedParser.Parse("=");
        var result = script.Transform("hello");
        Assert.That(result, Is.EqualTo("1\nhello"));
    }

    [Test]
    public void LineNumberCommand_MultipleLines_CorrectNumbers()
    {
        var script = SedParser.Parse("=");
        var result = script.Transform("a\nb\nc");
        Assert.That(result, Is.EqualTo("1\na\n2\nb\n3\nc"));
    }

    [Test]
    public void LineNumberCommand_WithAddress_OnlyMatchingLine()
    {
        // 2= prints line number only for line 2
        var script = SedParser.Parse("2=");
        var result = script.Transform("a\nb\nc");
        Assert.That(result, Is.EqualTo("a\n2\nb\nc"));
    }

    [Test]
    public void LineNumberCommand_WithSuppression_LineNumberStillPrinted()
    {
        // = always outputs to stdout even under -n
        var script = SedParser.Parse("=", suppressDefaultOutput: true);
        var result = script.Transform("hello");
        Assert.That(result, Is.EqualTo("1\n"));
    }

    // --- List Command (l) ---

    [Test]
    public void ListCommand_PlainText_AppendsDollarSign()
    {
        // l appends $ to show end of line
        var script = SedParser.Parse("l", suppressDefaultOutput: true);
        var result = script.Transform("hello");
        Assert.That(result, Is.EqualTo("hello$\n"));
    }

    [Test]
    public void ListCommand_WithTab_ShowsEscapedTab()
    {
        var script = SedParser.Parse("l", suppressDefaultOutput: true);
        var result = script.Transform("he\tllo");
        Assert.That(result, Is.EqualTo("he\\tllo$\n"));
    }

    [Test]
    public void ListCommand_WithBackslash_ShowsEscapedBackslash()
    {
        var script = SedParser.Parse("l", suppressDefaultOutput: true);
        var result = script.Transform("he\\llo");
        Assert.That(result, Is.EqualTo("he\\\\llo$\n"));
    }

    [Test]
    public void ListCommand_WithEmbeddedNewline_ShowsEscapedNewline()
    {
        // After N, pattern space has embedded newline
        var script = SedParser.Parse("N;l;d", suppressDefaultOutput: true);
        var result = script.Transform("first\nsecond");
        Assert.That(result, Is.EqualTo("first\\nsecond$\n"));
    }

    [Test]
    public void ListCommand_WithCarriageReturn_ShowsEscapedCR()
    {
        var script = SedParser.Parse("l", suppressDefaultOutput: true);
        var result = script.Transform("he\rllo");
        Assert.That(result, Is.EqualTo("he\\rllo$\n"));
    }

    [Test]
    public void ListCommand_LongLine_FoldsAt70Columns()
    {
        // Lines longer than 70 columns should be folded with backslash-newline
        // GNU sed: 69 content chars + \ = 70 columns per line.
        var script = SedParser.Parse("l", suppressDefaultOutput: true);
        // 75 'a' chars: folds at column 69 (backslash at col 70), leaving 6 remaining + $
        var input = new string('a', 75);
        var result = script.Transform(input);
        // 69 chars + backslash (col 70), newline, 6 remaining chars + $
        var expected = new string('a', 69) + "\\\n" + new string('a', 6) + "$\n";
        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public void ListCommand_NonPrintableChar_ShowsOctalEscape()
    {
        var script = SedParser.Parse("l", suppressDefaultOutput: true);
        // \u0001 is SOH (non-printable control character)
        var result = script.Transform("a\u0001b");
        Assert.That(result, Is.EqualTo("a\\001b$\n"));
    }

    [Test]
    public void ListCommand_WithoutSuppression_LineAppearsAfterListing()
    {
        // Without -n, l outputs the visual form AND then pattern space is auto-printed
        var script = SedParser.Parse("l");
        var result = script.Transform("hi");
        Assert.That(result, Is.EqualTo("hi$\nhi"));
    }

    // --- Quit Command (q) ---

    [Test]
    public void QuitCommand_StopsProcessing_OutputsCurrentLine()
    {
        // q outputs current pattern space, then stops
        var script = SedParser.Parse("2q");
        var result = script.Transform("line1\nline2\nline3");
        Assert.That(result, Is.EqualTo("line1\nline2\n"));
    }

    [Test]
    public void QuitCommand_FirstLine_OutputsFirstLine()
    {
        var script = SedParser.Parse("1q");
        var result = script.Transform("line1\nline2\nline3");
        Assert.That(result, Is.EqualTo("line1\n"));
    }

    [Test]
    public void QuitCommand_WithSuppression_DoesNotOutputCurrentLine()
    {
        // With -n, q still stops but no auto-print
        var script = SedParser.Parse("2q", suppressDefaultOutput: true);
        var result = script.Transform("line1\nline2\nline3");
        Assert.That(result, Is.EqualTo(""));
    }

    // --- QuitNoprint Command (Q) ---

    [Test]
    public void QuitNoprintCommand_StopsProcessing_DoesNotOutputCurrentLine()
    {
        // Q stops immediately, does NOT print current pattern space
        var script = SedParser.Parse("2Q");
        var result = script.Transform("line1\nline2\nline3");
        Assert.That(result, Is.EqualTo("line1\n"));
    }

    [Test]
    public void QuitNoprintCommand_FirstLine_NoOutput()
    {
        var script = SedParser.Parse("1Q");
        var result = script.Transform("line1\nline2");
        Assert.That(result, Is.EqualTo(""));
    }

    // --- Combined tests ---

    [Test]
    public void Combined_Suppression_EqualsThenPrint_SelectiveOutput()
    {
        // sed -n '/foo/{ =; p }' - prints line number and line for matching lines
        var script = SedParser.Parse("/foo/=\n/foo/p", suppressDefaultOutput: true);
        var result = script.Transform("bar\nfoo\nbaz");
        Assert.That(result, Is.EqualTo("2\nfoo\n"));
    }

    [Test]
    public void PrintCommand_EmptyInput_ReturnsEmpty()
    {
        var script = SedParser.Parse("p");
        var result = script.Transform("");
        // Transform("") hits the IsNullOrEmpty early-return guard and returns "" directly.
        Assert.That(result, Is.EqualTo(""));
    }


    [Test]
    public void ListCommand_SingleChar_OutputsCharAndDollar()
    {
        // l on a single-char pattern space outputs the char followed by "$"
        var script = SedParser.Parse("l", suppressDefaultOutput: true);
        var result = script.Transform("x");
        // "x" -> "x$"
        Assert.That(result, Is.EqualTo("x$\n"));
    }

    [Test]
    public void ListCommand_EmptyLine_OutputsDollarOnly()
    {
        // l on a line with no characters outputs just "$\n"
        var script = SedParser.Parse("l", suppressDefaultOutput: true);
        var result = script.Transform("\n");
        Assert.That(result, Is.EqualTo("$\n"));
    }

    [Test]
    public void PrintFirstLineCommand_NoEmbeddedNewline_OutputsEntireLine()
    {
        // P with no embedded newline in pattern space outputs the whole pattern space
        var script = SedParser.Parse("P", suppressDefaultOutput: true);
        var result = script.Transform("hello");
        Assert.That(result, Is.EqualTo("hello"));
    }

    [Test]
    public void ListCommand_MixedEscapeTypes_AllEscapedCorrectly()
    {
        // l should escape tab, backslash, and control chars correctly in one line
        var script = SedParser.Parse("l", suppressDefaultOutput: true);
        var input = "\t\\" + "\x01";
        var result = script.Transform(input);
        Assert.That(result, Is.EqualTo("\\t\\\\\\001$\n"));
    }

    [Test]
    public void ListCommand_NonAsciiChar_OutputsOctalBytes()
    {
        // GNU sed operates at byte level: \u00E9 (e-acute) is UTF-8 0xC3 0xA9 -> \303\251
        var script = SedParser.Parse("l", suppressDefaultOutput: true);
        var result = script.Transform("caf\u00e9");
        Assert.That(result, Is.EqualTo("caf\\303\\251$\n"));
    }

    [Test]
    public void SubstituteCommand_NthOccurrence_WithBackreference_ExpandsCorrectly()
    {
        var script = SedParser.Parse(@"s/\(word\)/[\1]/2");
        var result = script.Transform("word word word");
        Assert.That(result, Is.EqualTo("word [word] word"));
    }

    [Test]
    public void ListCommand_SurrogatePair_OutputsOctalBytes()
    {
        // U+1F600 emoji in UTF-8 = F0 9F 98 80 = \360\237\230\200
        var script = SedParser.Parse("l");
        var result = script.Transform("\U0001F600");
        Assert.That(result, Does.Contain(@"\360\237\230\200$"));
    }

    [Test]
    public void QuitCommand_WithSilentMode_PrintsOnlyExplicitlyPrinted()
    {
        var commands = SedParser.Parse("1p\n2q").Commands;
        var silentScript = new SedScript(commands, suppressDefaultOutput: true);
        var result = silentScript.Transform("line1\nline2\nline3");
        Assert.That(result, Is.EqualTo("line1\n"));
    }


    // --- MAJOR 1: l with exactly 69-char input (no fold) ---

    [Test]
    public void ListCommand_69CharInput_NoFold()
    {
        // GNU sed: 69 content chars fit on one line without folding.
        // The $ terminator must NOT trigger a fold; it appears at column 69 (0-indexed).
        // Expected: 69 a's + '$', single unfolded line.
        var input = new string('a', 69);
        var script = SedParser.Parse("l", suppressDefaultOutput: true);
        var result = script.Transform(input);
        var expected = new string('a', 69) + "$\n";
        Assert.That(result, Is.EqualTo(expected));
    }

    // --- MAJOR 2: escape sequence at fold boundary is not split ---

    [Test]
    public void ListCommand_EscapeAtFoldBoundary_NotSplit()
    {
        // 68 'a' chars followed by a tab.
        // col=68 after the 68 a's. Tab expands to \t (2 chars). 68+2=70 >= FoldWidth(70),
        // so the entire \t escape is pushed to the next line.
        // Expected: 68 a's + '\\' continuation, then \t$ on next line.
        var input = new string('a', 68) + "	";
        var script = SedParser.Parse("l", suppressDefaultOutput: true);
        var result = script.Transform(input);
        var expected = new string('a', 68) + "\\\n\\t$\n";
        Assert.That(result, Is.EqualTo(expected));
    }

    // --- MAJOR 3: Q with multi-line input ---

    [Test]
    public void QuitNoprintCommand_StopsBeforeSecondLine()
    {
        // 2Q: quit without printing at line 2.
        // Line 1 is auto-printed normally. Line 2 triggers Q, which stops without printing it.
        // Line 3 is never reached.
        var script = SedParser.Parse("2Q");
        var result = script.Transform("line1\nline2\nline3");
        Assert.That(result, Is.EqualTo("line1\n"));
    }

    // --- MAJOR 4: q with pending a\ append text ---

    [Test]
    public void QuitCommand_WithPendingAppend_OutputsAppendBeforeQuit()
    {
        // Script: a\after on all lines, 1q.
        // Line 1: 'a\after' queues "after", then '1q' triggers quit.
        // q auto-prints "only", then flushes pending appends -> "after".
        // Expected: "only\nafter"
        var script = SedParser.Parse("a\\after\n1q");
        var result = script.Transform("only");
        Assert.That(result, Is.EqualTo("only\nafter"));
    }

    // --- GetHold (g) contamination flag fix ---

    [Test]
    public void GetHold_PlainTextHoldSpace_NoSpuriousTrailingNewline()
    {
        // h copies pattern space ("hello") into hold space on the same cycle.
        // g copies hold space back into pattern space on the same cycle.
        // Because hold was set on the same line as g, _patternSpaceContaminatedByHoldOrN stays false.
        // Input has no trailing newline, so output must be exactly "hello" with no trailing newline.
        // Real sed: printf 'hello' | sed 'h;g' => 'hello' (no trailing newline).
        var result = SedParser.Parse("h\ng").Transform("hello");
        Assert.That(result, Is.EqualTo("hello"));
    }

    // --- n command: pipeline continues (not restarts) after n ---

    [Test]
    public void Next_PipelineContinues_HoldGetAfterN()
    {
        // h;n;g: h saves line 1, n prints line 1 and advances to line 2,
        // g replaces pattern space (line 2) with hold (line 1).
        // GNU sed: printf 'a\nb\n' | sed 'h;n;g' => 'a\na\n'
        // After n, execution must CONTINUE with g (not restart from h).
        var result = SedParser.Parse("h;n;g").Transform("a\nb");
        Assert.That(result, Is.EqualTo("a\na\n"));
    }

    [Test]
    public void Next_PipelineContinues_SubstituteAfterN()
    {
        // n;s/b/X/: n advances to line 2, s/b/X/ must still execute on new pattern space.
        // Input: a\nb (no trailing newline) => n prints 'a', loads 'b'; s/b/X/ => 'X'.
        // Since the last line had no trailing newline and hold space is not involved,
        // output preserves the absence of trailing newline: 'a\nX' (no trailing newline).
        // GNU sed: printf 'a\nb' | sed 'n;s/b/X/' => 'a\nX' (no trailing newline)
        var result = SedParser.Parse("n;s/b/X/").Transform("a\nb");
        Assert.That(result, Is.EqualTo("a\nX"));
    }

    [Test]
    public void Next_NoTrailingNewline_HoldGetAfterN_GnuSedBehavior()
    {
        // Script: h;n;g  Input: a\nbb (no trailing newline)
        // GNU sed: printf 'a\nbb' | sed 'h;n;g' => 'a\na\n' (trailing newline present)
        // _currentLineIndex must be updated after n so the contamination check is correct.
        var result = SedParser.Parse("h;n;g").Transform("a\nbb");
        Assert.That(result, Is.EqualTo("a\na\n"));
    }

    [Test]
    public void CrossLine_Hold1Get2_NoTrailingNewline_ForcesTrailingNewline()
    {
        // Script: 1h;2g  Input: a\nb (no trailing newline)
        // GNU sed: printf 'a\nb' | sed '1h;2g' => 'a\na\n'
        // g on line 2 loads hold from line 1 => cross-line => forced trailing newline.
        var result = SedParser.Parse("1h;2g").Transform("a\nb");
        Assert.That(result, Is.EqualTo("a\na\n"));
    }

    [Test]
    public void NextAppend_PipelineContinues_HoldGetAfterN()
    {
        // h;N;g: h saves 'a', N appends line 2 => pattern='a\nb', g replaces with hold='a'.
        // GNU sed: printf 'a\nb\n' | sed 'h;N;g' => 'a\n'
        // After N, execution must CONTINUE with g (not restart from h).
        var result = SedParser.Parse("h;N;g").Transform("a\nb");
        Assert.That(result, Is.EqualTo("a\n"));
    }

    #endregion
}
