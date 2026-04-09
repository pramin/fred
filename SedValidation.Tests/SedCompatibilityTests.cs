using NUnit.Framework;
using System.Diagnostics;

namespace SedValidation.Tests;

/// <summary>
/// Validation tests comparing ned output with actual sed output for compatibility.
/// Each test runs both the real sed and ned against identical input/script, then
/// asserts byte-for-byte identical output.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.Children)]
public class SedCompatibilityTests
{
    private string _nedBin = string.Empty;
    private readonly string _tempDir;

    public SedCompatibilityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "sed-validation-tests");
        Directory.CreateDirectory(_tempDir);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        // Build ned once, then use the compiled binary for all tests
        var buildDir = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."));
        var psi = new ProcessStartInfo("dotnet", $"build {Path.Combine(buildDir, "ned", "ned.csproj")} -c Debug -o {Path.Combine(buildDir, "ned", "bin", "oracle-test")}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        var proc = Process.Start(psi)!;
        proc.WaitForExit(60000);
        Assert.That(proc.ExitCode, Is.EqualTo(0), "ned build failed");

        _nedBin = Path.Combine(buildDir, "ned", "bin", "oracle-test", "ned");
    }

    [SetUp]
    public void Setup()
    {
        // Verify sed is available on the system
        if (!IsSedAvailable())
        {
            Assert.Ignore("sed command not available on this system");
        }
    }

    // =========================================================================
    // 1. Basic Substitution
    // =========================================================================

    #region Basic Substitution Tests

    [Test]
    public void BasicSubstitution_MatchesSed()
    {
        AssertMatch("s/world/ned/", "hello world");
    }

    [Test]
    public void GlobalSubstitution_MatchesSed()
    {
        AssertMatch("s/test/TEST/g", "test test test");
    }

    [Test]
    public void NumericOccurrence_MatchesSed()
    {
        AssertMatch("s/a/X/2", "banana");
    }

    [Test]
    public void ThirdOccurrence_MatchesSed()
    {
        AssertMatch("s/a/X/3", "banana");
    }

    [Test]
    public void CaseInsensitive_MatchesSed()
    {
        AssertMatch("s/hello/hi/i", "Hello World");
    }

    [Test]
    public void CaseInsensitiveGlobal_MatchesSed()
    {
        AssertMatch("s/hello/hi/gi", "Hello hello HELLO world");
    }

    [Test]
    public void SubstitutionWithPrintFlag_MatchesSed()
    {
        // s///p with default output prints matched lines twice
        AssertMatch("s/foo/FOO/p", "foo\nbar\nfoo");
    }

    [Test]
    public void SubstitutionWithPrintFlag_SuppressDefault_MatchesSed()
    {
        // -n s///p prints only substituted lines once
        AssertMatch("-n", "s/foo/FOO/p", "foo\nbar\nfoo");
    }

    [Test]
    public void SubstitutionWithBackreference_MatchesSed()
    {
        AssertMatch(@"s/\(test\)/[\1]/", "test value");
    }

    [Test]
    public void SubstitutionAlternateDelimiter_MatchesSed()
    {
        AssertMatch("s|/|_|g", "path/to/file");
    }

    [Test]
    public void SubstitutionEscapedDot_MatchesSed()
    {
        AssertMatch(@"s/\./DOT/", "file.txt");
    }

    [Test]
    public void SubstitutionAmpersandReplacement_MatchesSed()
    {
        // & in replacement refers to matched text
        AssertMatch("s/[0-9]*/[&]/", "123 abc");
    }

    #endregion

    // =========================================================================
    // 2. Delete Command
    // =========================================================================

    #region Delete Command Tests

    [Test]
    public void DeleteAll_MatchesSed()
    {
        AssertMatch("d", "line1\nline2\nline3");
    }

    [Test]
    public void DeleteByLineNumber_MatchesSed()
    {
        AssertMatch("2d", "line1\nline2\nline3");
    }

    [Test]
    public void DeleteByPattern_MatchesSed()
    {
        AssertMatch("/delete/d", "keep\ndelete this\nkeep");
    }

    [Test]
    public void DeleteRange_MatchesSed()
    {
        AssertMatch("2,4d", "one\ntwo\nthree\nfour\nfive");
    }

    [Test]
    public void DeleteFromPatternToEnd_MatchesSed()
    {
        AssertMatch("/three/,$d", "one\ntwo\nthree\nfour\nfive");
    }

    [Test]
    public void DeleteNegated_MatchesSed()
    {
        // Delete all lines EXCEPT line 2
        AssertMatch("2!d", "one\ntwo\nthree");
    }

    #endregion

    // =========================================================================
    // 3. Print Command (-n with p)
    // =========================================================================

    #region Print Tests

    [Test]
    public void PrintSuppressedLineNumber_MatchesSed()
    {
        AssertMatch("-n", "3p", "line1\nline2\nline3\nline4");
    }

    [Test]
    public void PrintSuppressedPattern_MatchesSed()
    {
        AssertMatch("-n", "/pattern/p", "normal\npattern here\nnormal");
    }

    [Test]
    public void PrintSuppressedRange_MatchesSed()
    {
        AssertMatch("-n", "2,4p", "one\ntwo\nthree\nfour\nfive");
    }

    [Test]
    public void PrintSuppressedPatternRange_MatchesSed()
    {
        AssertMatch("-n", "/START/,/END/p", "before\nSTART\nmiddle\nEND\nafter");
    }

    [Test]
    public void PrintWithoutSuppression_MatchesSed()
    {
        // p without -n prints matching lines twice
        AssertMatch("2p", "one\ntwo\nthree");
    }

    #endregion

    // =========================================================================
    // 4. Hold Space Commands (h, H, g, G, x)
    // =========================================================================

    #region Hold Space Tests

    [Test]
    public void HoldAndGet_MatchesSed()
    {
        // 1{h} 2{g} — line 2 replaced by line 1
        AssertMatch("1{h}\n2{g}", "first\nsecond\nthird");
    }

    [Test]
    public void HoldAppendAndGet_MatchesSed()
    {
        // H appends to hold space; at end get all lines joined
        AssertMatch("H;$!d;${x;s/\\n/ /g}", "one\ntwo\nthree");
    }

    [Test]
    public void ReverseLines_MatchesSed()
    {
        // Classic reverse: 1!G;h;$!d
        AssertMatch("1!G;h;$!d", "one\ntwo\nthree");
    }

    [Test]
    public void Exchange_MatchesSed()
    {
        // 1{x} 2{x}: swap pattern and hold on lines 1 and 2
        AssertMatch("1{x}\n2{x}", "line1\nline2\nline3");
    }

    [Test]
    public void GetAppend_MatchesSed()
    {
        // G appends hold space content to pattern space
        AssertMatch("1h\n2G", "first\nsecond\nthird");
    }

    [Test]
    public void HoldGetAfterNext_MatchesSed()
    {
        // h;n;g: save line 1 to hold, print line 1, load line 2, replace with hold (line 1)
        // n must continue pipeline (not restart), so g executes on the advanced line.
        AssertMatch("h;n;g", "a\nb\nc");
    }

    [Test]
    public void HoldGetAfterNextAppend_MatchesSed()
    {
        // h;N;g: save line 1, append line 2 to pattern space via N, then g replaces with hold
        // N must continue pipeline (not restart), so g executes after N.
        AssertMatch("h;N;g", "a\nb\nc");
    }

    [Test]
    public void SubstituteAfterNext_MatchesSed()
    {
        // n;s/two/TWO/: print line 1, advance to line 2, then substitute on line 2
        // Verifies pipeline continues at the next command after n, not from the start.
        AssertMatch("n;s/two/TWO/", "one\ntwo\nthree");
    }

    #endregion

    // =========================================================================
    // 5. Multi-line Commands (N, D, P)
    // =========================================================================

    #region Multi-line Tests

    [Test]
    public void JoinPairs_MatchesSed()
    {
        // N;s/\n/ / — joins each pair of lines
        AssertMatch("N;s/\\n/ /", "one\ntwo\nthree\nfour");
    }

    [Test]
    public void JoinAllLines_MatchesSed()
    {
        // :a;N;$!ba;s/\n/ /g — join all lines into one
        AssertMatch(":a;N;$!ba;s/\\n/ /g", "one\ntwo\nthree\nfour");
    }

    [Test]
    public void MultilinePrint_MatchesSed()
    {
        // N then P prints only first line of the two-line pattern space
        AssertMatch("-n", "N;P", "one\ntwo\nthree\nfour");
    }

    [Test]
    public void MultilineDelete_MatchesSed()
    {
        // 1{N;D}: join line1+line2 then delete up to first newline (line1), restart with line2
        AssertMatch("1{N;D}", "line1\nline2\nline3");
    }

    [Test]
    public void JoinLinesInRange_MatchesSed()
    {
        // /START/,/END/{N;s/\n/ /} — join lines inside a pattern range
        AssertMatch("/START/,/END/{N;s/\\n/ /}", "before\nSTART\nmiddle\nEND\nafter");
    }

    #endregion

    // =========================================================================
    // 6. Text Commands (a, i, c)
    // =========================================================================

    #region Text Command Tests

    [Test]
    public void AppendText_MatchesSed()
    {
        AssertMatch("2a\\APPENDED", "one\ntwo\nthree");
    }

    [Test]
    public void InsertText_MatchesSed()
    {
        AssertMatch("2i\\INSERTED", "one\ntwo\nthree");
    }

    [Test]
    public void ChangeText_MatchesSed()
    {
        AssertMatch("2c\\CHANGED", "one\ntwo\nthree");
    }

    [Test]
    public void AppendToPatternMatch_MatchesSed()
    {
        AssertMatch("/foo/a\\APPENDED", "bar\nfoo\nbaz");
    }

    [Test]
    public void InsertBeforePatternMatch_MatchesSed()
    {
        AssertMatch("/foo/i\\INSERTED", "bar\nfoo\nbaz");
    }

    [Test]
    public void ChangePatternMatch_MatchesSed()
    {
        AssertMatch("/foo/c\\REPLACED", "bar\nfoo\nbaz");
    }

    #endregion

    // =========================================================================
    // 7. Transliterate (y)
    // =========================================================================

    #region Transliterate Tests

    [Test]
    public void TransliterateVowels_MatchesSed()
    {
        AssertMatch("y/aeiou/AEIOU/", "hello world");
    }

    [Test]
    public void TransliterateUppercase_MatchesSed()
    {
        AssertMatch("y/abcdefghijklmnopqrstuvwxyz/ABCDEFGHIJKLMNOPQRSTUVWXYZ/", "hello world");
    }

    [Test]
    public void TransliterateDigits_MatchesSed()
    {
        AssertMatch("y/0123456789/9876543210/", "0123456789");
    }

    [Test]
    public void TransliterateWithAddressedLine_MatchesSed()
    {
        AssertMatch("2y/aeiou/AEIOU/", "hello\nworld\nhello");
    }

    #endregion

    // =========================================================================
    // 8. Flow Control (b, t, T, labels, :)
    // =========================================================================

    #region Flow Control Tests

    [Test]
    public void BranchUnconditional_MatchesSed()
    {
        // b end skips s/bar/baz/
        AssertMatch("s/foo/bar/;bend;s/bar/baz/;:end", "foo\nbar\nother");
    }

    [Test]
    public void BranchToEmpty_MatchesSed()
    {
        // b with no label branches to end of script
        AssertMatch("s/foo/bar/;b;s/bar/baz/", "foo\nbar");
    }

    [Test]
    public void TestBranch_MatchesSed()
    {
        // t branches only if substitution was made in this cycle
        AssertMatch("s/foo/bar/;tend;s/bar/baz/;:end", "foo\nbar\nother");
    }

    [Test]
    public void TestNotBranch_MatchesSed()
    {
        // T branches only if NO substitution was made in this cycle
        AssertMatch("s/foo/bar/;Tend;s/bar/baz/;:end", "foo\nother");
    }

    [Test]
    public void JoinAllLinesWithLoop_MatchesSed()
    {
        // Classic join-all loop using t
        AssertMatch(":top;N;$!btop;s/\\n/ /g", "a\nb\nc\nd");
    }

    #endregion

    // =========================================================================
    // 9. Line Number Command (=)
    // =========================================================================

    #region Line Number Tests

    [Test]
    public void LineNumberAll_MatchesSed()
    {
        AssertMatch("=", "one\ntwo\nthree");
    }

    [Test]
    public void LineNumberWithSuppression_MatchesSed()
    {
        // -n '=' prints only line numbers without content
        AssertMatch("-n", "=", "one\ntwo\nthree");
    }

    [Test]
    public void LineNumberAddressed_MatchesSed()
    {
        AssertMatch("2=", "one\ntwo\nthree");
    }

    [Test]
    public void LineNumberWithSubstitution_MatchesSed()
    {
        // Print line number and then substitute on matched line
        AssertMatch("/two/{=;s/two/TWO/}", "one\ntwo\nthree");
    }

    #endregion

    // =========================================================================
    // 10. Blocks ({ })
    // =========================================================================

    #region Block Tests

    [Test]
    public void Block_LineNumberAddress_MatchesSed()
    {
        AssertMatch("2{s/line/LINE/}", "line1\nline2\nline3");
    }

    [Test]
    public void Block_PatternRangeAddress_MatchesSed()
    {
        AssertMatch("/START/,/END/{s/x/X/g}", "xxx\nSTART\nxxx\nEND\nxxx");
    }

    [Test]
    public void Block_MultipleCommandsInBlock_MatchesSed()
    {
        AssertMatch("2{s/a/b/;s/c/d/}", "ac\nac\nac");
    }

    [Test]
    public void Block_Negated_MatchesSed()
    {
        AssertMatch("2!{s/a/b/}", "aaa\naaa\naaa");
    }

    [Test]
    public void Block_DeleteInside_MatchesSed()
    {
        AssertMatch("/START/,/END/{/START/d;/END/d}", "before\nSTART\ncontent\nEND\nafter");
    }

    #endregion

    // =========================================================================
    // 11. Step Addressing (GNU extension: first~step)
    // =========================================================================

    #region Step Addressing Tests

    [Test]
    public void StepAddress_OddLines_MatchesSed()
    {
        // 1~2p with -n: print odd-numbered lines
        AssertMatch("-n", "1~2p", "one\ntwo\nthree\nfour\nfive");
    }

    [Test]
    public void StepAddress_EvenLines_MatchesSed()
    {
        // 0~2p with -n: print even-numbered lines (GNU extension: 0~2 means every 2nd starting at 2)
        AssertMatch("-n", "0~2p", "one\ntwo\nthree\nfour\nfive");
    }

    [Test]
    public void StepAddress_EveryThird_MatchesSed()
    {
        // 2~3p with -n: print lines 2, 5, 8...
        AssertMatch("-n", "2~3p", "one\ntwo\nthree\nfour\nfive\nsix\neight");
    }

    [Test]
    public void StepAddress_DeleteEvenLines_MatchesSed()
    {
        // 0~2d: delete even-numbered lines
        AssertMatch("0~2d", "one\ntwo\nthree\nfour\nfive");
    }

    [Test]
    public void StepAddress_NegatedOdd_MatchesSed()
    {
        // 1~2!d: delete all non-odd lines (keep only odd)
        AssertMatch("1~2!d", "one\ntwo\nthree\nfour\nfive");
    }

    #endregion

    // =========================================================================
    // 12. Address Matching
    // =========================================================================

    #region Address Matching Tests

    [Test]
    public void LineNumberAddress_MatchesSed()
    {
        AssertMatch("2s/line/LINE/", "line1\nline2\nline3");
    }

    [Test]
    public void PatternAddress_MatchesSed()
    {
        AssertMatch("/test/s/test/TEST/", "normal\ntest line\nnormal");
    }

    [Test]
    public void RangeAddress_MatchesSed()
    {
        AssertMatch("2,4s/x/X/g", "xxx\nxxx\nxxx\nxxx\nxxx");
    }

    [Test]
    public void PatternRangeAddress_MatchesSed()
    {
        AssertMatch("/foo/,/bar/s/x/X/g", "xxx\nfoo\nxxx\nbar\nxxx");
    }

    [Test]
    public void LastLineAddress_MatchesSed()
    {
        AssertMatch("$s/last/LAST/", "first\nsecond\nlast");
    }

    [Test]
    public void LastLineDelete_MatchesSed()
    {
        AssertMatch("$d", "one\ntwo\nthree");
    }

    [Test]
    public void NegatedLineAddress_MatchesSed()
    {
        AssertMatch("2!s/line/LINE/", "line1\nline2\nline3");
    }

    #endregion

    // =========================================================================
    // 13. Quit Commands (q, Q)
    // =========================================================================

    #region Quit Tests

    [Test]
    public void Quit_AfterFirstLine_MatchesSed()
    {
        AssertMatch("1q", "one\ntwo\nthree");
    }

    [Test]
    public void Quit_AfterNthLine_MatchesSed()
    {
        AssertMatch("3q", "one\ntwo\nthree\nfour\nfive");
    }

    [Test]
    public void QuitSilent_AfterNthLine_MatchesSed()
    {
        // Q quits without printing the current line
        AssertMatch("2Q", "one\ntwo\nthree");
    }

    [Test]
    public void QuitSilent_OnPattern_MatchesSed()
    {
        AssertMatch("/stop/Q", "one\ntwo\nstop\nfour");
    }

    #endregion

    // =========================================================================
    // 14. Multiple Commands and Scripts
    // =========================================================================

    #region Multiple Commands Tests

    [Test]
    public void MultipleCommands_Semicolon_MatchesSed()
    {
        AssertMatch("s/old/new/; s/test/TEST/", "old test value");
    }

    [Test]
    public void MultipleCommands_Newline_MatchesSed()
    {
        AssertMatch("s/old/new/\ns/test/TEST/", "old test value");
    }

    [Test]
    public void MultipleScripts_MultipleE_MatchesSed()
    {
        var input = "hello world";
        var sedOutput = RunSedMultiE(new[] { "s/hello/hi/", "s/world/earth/" }, input);
        var nedOutput = RunNedMultiE(new[] { "s/hello/hi/", "s/world/earth/" }, input);
        Assert.That(nedOutput, Is.EqualTo(sedOutput), "ned output should match sed for multiple -e scripts");
    }

    #endregion

    // =========================================================================
    // 15. ERE Mode (-E / -r)
    // =========================================================================

    #region ERE Mode Tests

    [Test]
    public void EreMode_PlusQuantifier_MatchesSed()
    {
        AssertMatch("-E", "s/[0-9]+/NUM/g", "foo 123 bar 456");
    }

    [Test]
    public void EreMode_Alternation_MatchesSed()
    {
        AssertMatch("-E", "s/cat|dog/pet/g", "I have a cat and a dog");
    }

    [Test]
    public void EreMode_Groups_MatchesSed()
    {
        AssertMatch("-E", "s/(foo)(bar)/\\2\\1/", "foobar");
    }

    [Test]
    public void EreMode_QuestionMark_MatchesSed()
    {
        AssertMatch("-E", "s/colou?r/colour/g", "color colour");
    }

    #endregion

    // =========================================================================
    // 16. Newline Handling
    // =========================================================================

    #region Newline Handling Tests

    [Test]
    public void InputWithTrailingNewline_MatchesSed()
    {
        AssertMatch("s/test/TEST/", "test\n");
    }

    [Test]
    public void InputWithoutTrailingNewline_MatchesSed()
    {
        AssertMatch("s/test/TEST/", "test");
    }

    [Test]
    public void MultilineInput_MatchesSed()
    {
        AssertMatch("s/line/LINE/", "line1\nline2\nline3");
    }

    [Test]
    public void EmptyLines_MatchesSed()
    {
        AssertMatch("s/test/TEST/", "test\n\ntest");
    }

    #endregion

    // =========================================================================
    // 17. Edge Cases
    // =========================================================================

    #region Edge Cases Tests

    [Test]
    public void EmptyInput_MatchesSed()
    {
        AssertMatch("s/test/TEST/", "");
    }

    [Test]
    public void SingleCharacter_MatchesSed()
    {
        AssertMatch("s/a/X/", "a");
    }

    [Test]
    public void NoMatch_MatchesSed()
    {
        AssertMatch("s/xyz/ABC/", "hello world");
    }

    [Test]
    public void UnicodeInput_MatchesSed()
    {
        AssertMatch("s/world/monde/", "hello world \u4e16\u754c");
    }

    [Test]
    public void EmptyReplacement_MatchesSed()
    {
        // Delete first vowel on each line
        AssertMatch("s/[aeiou]//", "hello world");
    }

    [Test]
    public void SpecialCharsInInput_MatchesSed()
    {
        AssertMatch("s/\\./[dot]/g", "file.txt.bak");
    }

    #endregion

    // =========================================================================
    // 18. BRE (Basic Regular Expressions)
    // =========================================================================

    #region BRE Tests

    [Test]
    public void BREBackreferences_MatchesSed()
    {
        // Multiple groups: swap two words to verify both backreferences work
        AssertMatch(@"s/\(hello\) \(world\)/[\2] [\1]/", "hello world");
    }

    [Test]
    public void BREDot_MatchesSed()
    {
        AssertMatch("s/f.o/bar/", "foo fao fbo");
    }

    [Test]
    public void BREStar_MatchesSed()
    {
        AssertMatch("s/ab*/X/g", "a ab abb abbb b");
    }

    [Test]
    public void BRELineStart_MatchesSed()
    {
        AssertMatch("s/^/START:/", "hello\nworld");
    }

    [Test]
    public void BRELineEnd_MatchesSed()
    {
        AssertMatch("s/$/:END/", "hello\nworld");
    }

    [Test]
    public void BREBracketExpr_MatchesSed()
    {
        AssertMatch("s/[aeiou]/*/g", "hello world");
    }

    [Test]
    public void BRENegatedBracket_MatchesSed()
    {
        AssertMatch("s/[^aeiou ]/_/g", "hello world");
    }

    [Test]
    public void BREMultipleBackreferences_MatchesSed()
    {
        AssertMatch(@"s/\([a-z]*\) \([a-z]*\)/\2 \1/", "hello world");
    }

    #endregion

    // =========================================================================
    // 19. In-place Editing (-i)
    // =========================================================================

    #region In-place Editing Tests

    [Test]
    public void InPlace_NoBackup_MatchesSed()
    {
        var inputContent = "hello world\nfoo bar\n";
        var sedResult = RunSedInPlace("-i", "s/hello/hi/g", inputContent);
        var nedResult = RunNedInPlace("-i", "s/hello/hi/g", inputContent);
        Assert.That(nedResult, Is.EqualTo(sedResult), "ned in-place (no backup) should produce same result as sed");
    }

    [Test]
    public void InPlace_WithBackup_MatchesSed()
    {
        var inputContent = "hello world\nfoo bar\n";
        var sedResult = RunSedInPlace("-i.bak", "s/hello/hi/g", inputContent);
        var nedResult = RunNedInPlace("-i.bak", "s/hello/hi/g", inputContent);
        Assert.That(nedResult, Is.EqualTo(sedResult), "ned in-place (.bak) should produce same result as sed");
    }

    #endregion

    // =========================================================================
    // Utility Methods
    // =========================================================================

    #region Utility Methods

    /// <summary>
    /// Assert that ned and real sed produce identical output for a given script and input.
    /// </summary>
    private void AssertMatch(string script, string input)
    {
        var sedOutput = RunSed(script, input);
        var nedOutput = RunNed(script, input);
        Assert.That(nedOutput, Is.EqualTo(sedOutput),
            $"ned output should match sed output.\n  script: {script}\n  input:  {Truncate(input)}");
    }

    /// <summary>
    /// Assert that ned and real sed produce identical output when a leading flag is provided
    /// (e.g. "-n", "-E") followed by a script and input.
    /// </summary>
    private void AssertMatch(string flags, string script, string input)
    {
        var sedOutput = RunSed(flags, script, input);
        var nedOutput = RunNed(flags, script, input);
        Assert.That(nedOutput, Is.EqualTo(sedOutput),
            $"ned output should match sed output.\n  flags:  {flags}\n  script: {script}\n  input:  {Truncate(input)}");
    }

    private static string Truncate(string s, int max = 80)
        => s.Length <= max ? s : s[..max] + "...";

    private bool IsSedAvailable()
    {
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "sed",
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private string RunSed(string script, string input)
    {
        return RunProcess("sed", new[] { "-e", script }, input);
    }

    private string RunSed(string flags, string script, string input)
    {
        return RunProcess("sed", new[] { flags, "-e", script }, input);
    }

    private string RunSedMultiE(string[] scripts, string input)
    {
        var args = new List<string>();
        for (int i = 0; i < scripts.Length; i++)
        {
            args.Add("-e");
            args.Add(scripts[i]);
        }
        return RunProcess("sed", args.ToArray(), input);
    }

    private string RunNed(string script, string input)
    {
        return RunNedProcess(new[] { "-e", script }, input);
    }

    private string RunNed(string flags, string script, string input)
    {
        return RunNedProcess(new[] { flags, "-e", script }, input);
    }

    private string RunNedMultiE(string[] scripts, string input)
    {
        var args = new List<string>();
        for (int i = 0; i < scripts.Length; i++)
        {
            args.Add("-e");
            args.Add(scripts[i]);
        }
        return RunNedProcess(args.ToArray(), input);
    }

    private string RunProcess(string executable, string[] args, string input)
    {
        var inputFile = Path.Combine(_tempDir, $"input_{Guid.NewGuid():N}.txt");

        try
        {
            File.WriteAllText(inputFile, input);

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            for (int i = 0; i < args.Length; i++)
                process.StartInfo.ArgumentList.Add(args[i]);

            process.StartInfo.ArgumentList.Add(inputFile);

            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
                throw new Exception($"{executable} failed with exit code {process.ExitCode}: {error}");

            return output;
        }
        finally
        {
            if (File.Exists(inputFile)) File.Delete(inputFile);
        }
    }

    private string RunNedProcess(string[] args, string input)
    {
        var inputFile = Path.Combine(_tempDir, $"input_{Guid.NewGuid():N}.txt");

        try
        {
            File.WriteAllText(inputFile, input);

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _nedBin,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            for (int i = 0; i < args.Length; i++)
                process.StartInfo.ArgumentList.Add(args[i]);

            process.StartInfo.ArgumentList.Add(inputFile);

            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
                throw new Exception($"ned failed with exit code {process.ExitCode}: {error}");

            return output;
        }
        finally
        {
            if (File.Exists(inputFile)) File.Delete(inputFile);
        }
    }

    /// <summary>
    /// Run sed in-place on a temp file and return the modified file content.
    /// </summary>
    private string RunSedInPlace(string inPlaceFlag, string script, string inputContent)
    {
        var inputFile = Path.Combine(_tempDir, $"inplace_sed_{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(inputFile, inputContent);

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "sed",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.StartInfo.ArgumentList.Add(inPlaceFlag);
            process.StartInfo.ArgumentList.Add("-e");
            process.StartInfo.ArgumentList.Add(script);
            process.StartInfo.ArgumentList.Add(inputFile);

            process.Start();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
                throw new Exception($"sed in-place failed: {error}");

            return File.ReadAllText(inputFile);
        }
        finally
        {
            // Clean up file and any backup
            if (File.Exists(inputFile)) File.Delete(inputFile);
            // Glob for backups
            foreach (var f in Directory.GetFiles(_tempDir, Path.GetFileName(inputFile) + ".*"))
                File.Delete(f);
        }
    }

    /// <summary>
    /// Run ned in-place on a temp file and return the modified file content.
    /// </summary>
    private string RunNedInPlace(string inPlaceFlag, string script, string inputContent)
    {
        var inputFile = Path.Combine(_tempDir, $"inplace_ned_{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(inputFile, inputContent);

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _nedBin,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.StartInfo.ArgumentList.Add(inPlaceFlag);
            process.StartInfo.ArgumentList.Add("-e");
            process.StartInfo.ArgumentList.Add(script);
            process.StartInfo.ArgumentList.Add(inputFile);

            process.Start();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
                throw new Exception($"ned in-place failed: {error}");

            return File.ReadAllText(inputFile);
        }
        finally
        {
            if (File.Exists(inputFile)) File.Delete(inputFile);
            foreach (var f in Directory.GetFiles(_tempDir, Path.GetFileName(inputFile) + ".*"))
                File.Delete(f);
        }
    }

    #endregion
}
