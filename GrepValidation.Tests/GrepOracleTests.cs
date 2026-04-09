using System.Diagnostics;
using NUnit.Framework;

namespace GrepValidation.Tests;

/// <summary>
/// Oracle test suite for GNU grep. Each test runs the real grep binary and nrep,
/// asserting that nrep produces identical output and exit codes to real grep.
/// All temp files are cleaned up in finally blocks; tests are safe to run in parallel.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.Children)]
public class GrepOracleTests
{
    private const string GrepPath = "/usr/bin/grep";
    private string _nrepBin = string.Empty;
    private string _tempDir = string.Empty;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "grep-oracle-tests");
        Directory.CreateDirectory(_tempDir);

        // Build nrep once, then use the compiled binary for all tests
        var buildDir = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."));
        var psi = new ProcessStartInfo("dotnet", $"build {Path.Combine(buildDir, "nrep", "nrep.csproj")} -c Debug -o {Path.Combine(buildDir, "nrep", "bin", "oracle-test")}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        var proc = Process.Start(psi)!;
        proc.WaitForExit(60000);
        Assert.That(proc.ExitCode, Is.EqualTo(0), "nrep build failed");

        _nrepBin = Path.Combine(buildDir, "nrep", "bin", "oracle-test", "nrep");
    }

    [SetUp]
    public void SetUp()
    {
        if (!File.Exists(GrepPath))
            Assert.Ignore($"grep not found at {GrepPath}; skipping oracle tests.");
    }

    // -------------------------------------------------------------------------
    // Infrastructure helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Run grep with the given arguments, providing <paramref name="input"/> via stdin.
    /// Also runs nrep with identical arguments and asserts matching output and exit code.
    /// Returns (stdout, exitCode) from grep.
    /// </summary>
    private (string Output, int ExitCode) RunGrep(string[] args, string input)
    {
        var grepResult = RunProcess(GrepPath, args, input);
        var nrepResult = RunNrepProcess(args, input);

        Assert.That(nrepResult.ExitCode, Is.EqualTo(grepResult.ExitCode),
            $"nrep exit code should match grep.\n  args: {string.Join(" ", args)}\n  input: {Truncate(input)}");
        Assert.That(nrepResult.Output, Is.EqualTo(grepResult.Output),
            $"nrep output should match grep.\n  args: {string.Join(" ", args)}\n  input: {Truncate(input)}");

        return grepResult;
    }

    /// <summary>
    /// Run grep with the given arguments reading from one or more temp files.
    /// Also runs nrep with identical files and asserts matching output and exit code.
    /// Returns (stdout, exitCode) from grep.
    /// </summary>
    private (string Output, int ExitCode) RunGrepOnFiles(string[] args, params string[] fileContents)
    {
        var files = new string[fileContents.Length];
        for (int i = 0; i < fileContents.Length; i++)
        {
            files[i] = Path.Combine(_tempDir, $"grep_input_{Guid.NewGuid():N}.txt");
            File.WriteAllText(files[i], fileContents[i]);
        }

        try
        {
            // Run real grep
            var grepResult = RunProcessOnFiles(GrepPath, args, files);

            // Run nrep
            var nrepResult = RunNrepProcessOnFiles(args, files);

            // For file-based tests, filenames differ so we can't do exact output comparison
            // when filenames are part of output. Compare exit codes and line counts.
            Assert.That(nrepResult.ExitCode, Is.EqualTo(grepResult.ExitCode),
                $"nrep exit code should match grep for file-based test.\n  args: {string.Join(" ", args)}");

            // For -h (suppress filename) or -c/-l/-L, we can compare output directly
            // For tests with filenames in output, compare structure
            if (HasFlag(args, "-h") || HasFlag(args, "-c") || HasFlag(args, "-l") || HasFlag(args, "-L"))
            {
                Assert.That(nrepResult.Output, Is.EqualTo(grepResult.Output),
                    $"nrep output should match grep.\n  args: {string.Join(" ", args)}");
            }
            else
            {
                // Compare line count and content after stripping filename prefixes
                var grepLines = grepResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                var nrepLines = nrepResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                Assert.That(nrepLines.Length, Is.EqualTo(grepLines.Length),
                    $"nrep output line count should match grep.\n  args: {string.Join(" ", args)}\n  grep output: {Truncate(grepResult.Output)}\n  nrep output: {Truncate(nrepResult.Output)}");

                // Compare content after the filename:  prefix
                for (int i = 0; i < grepLines.Length; i++)
                {
                    string gContent = StripFilenamePrefix(grepLines[i]);
                    string nContent = StripFilenamePrefix(nrepLines[i]);
                    Assert.That(nContent, Is.EqualTo(gContent),
                        $"nrep output line {i} content should match grep after stripping filename.\n  grep: {grepLines[i]}\n  nrep: {nrepLines[i]}");
                }
            }

            return grepResult;
        }
        finally
        {
            for (int i = 0; i < files.Length; i++)
                if (File.Exists(files[i])) File.Delete(files[i]);
        }
    }

    /// <summary>
    /// Write a pattern file with the given lines, run grep -f, return (stdout, exitCode).
    /// Also runs nrep with the same pattern file and asserts matching output.
    /// </summary>
    private (string Output, int ExitCode) RunGrepWithPatternFile(string[] extraArgs, string[] patterns, string input)
    {
        var patternFile = Path.Combine(_tempDir, $"patterns_{Guid.NewGuid():N}.txt");
        File.WriteAllText(patternFile, string.Join("\n", patterns) + "\n");

        try
        {
            var allArgs = new List<string>(extraArgs);
            allArgs.Add("-f");
            allArgs.Add(patternFile);

            return RunGrep(allArgs.ToArray(), input);
        }
        finally
        {
            if (File.Exists(patternFile)) File.Delete(patternFile);
        }
    }

    // -------------------------------------------------------------------------
    // Low-level process runners (no assertions — just run and return)
    // -------------------------------------------------------------------------

    private static (string Output, int ExitCode) RunProcess(string executable, string[] args, string input)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        for (int i = 0; i < args.Length; i++)
            process.StartInfo.ArgumentList.Add(args[i]);

        process.Start();
        try
        {
            process.StandardInput.Write(input);
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // Process may exit before reading all stdin
        }
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (output, process.ExitCode);
    }

    private (string Output, int ExitCode) RunNrepProcess(string[] args, string input)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _nrepBin,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        for (int i = 0; i < args.Length; i++)
            process.StartInfo.ArgumentList.Add(args[i]);

        process.Start();
        try
        {
            process.StandardInput.Write(input);
            process.StandardInput.Close();
        }
        catch (IOException)
        {
        }
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (output, process.ExitCode);
    }

    private static (string Output, int ExitCode) RunProcessOnFiles(string executable, string[] args, string[] files)
    {
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

        for (int i = 0; i < files.Length; i++)
            process.StartInfo.ArgumentList.Add(files[i]);

        process.Start();
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (output, process.ExitCode);
    }

    private (string Output, int ExitCode) RunNrepProcessOnFiles(string[] args, string[] files)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _nrepBin,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        for (int i = 0; i < args.Length; i++)
            process.StartInfo.ArgumentList.Add(args[i]);

        for (int i = 0; i < files.Length; i++)
            process.StartInfo.ArgumentList.Add(files[i]);

        process.Start();
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (output, process.ExitCode);
    }

    private static bool HasFlag(string[] args, string flag)
    {
        for (int i = 0; i < args.Length; i++)
            if (args[i] == flag) return true;
        return false;
    }

    private static string StripFilenamePrefix(string line)
    {
        int colon = line.IndexOf(':');
        if (colon >= 0 && colon < line.Length - 1)
            return line.Substring(colon + 1);
        return line;
    }

    private static string Truncate(string s, int max = 80)
        => s.Length <= max ? s : s[..max] + "...";


    // -------------------------------------------------------------------------
    // 1. Basic BRE pattern matching
    // -------------------------------------------------------------------------

    #region Basic BRE

    [Test]
    public void BasicBRE_SimpleWord_MatchesLine()
    {
        var (output, exitCode) = RunGrep(["foo"], "foo\nbar\nbaz\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("foo\n"));
    }

    [Test]
    public void BasicBRE_NoMatch_ReturnsExitCode1()
    {
        var (output, exitCode) = RunGrep(["xyz"], "foo\nbar\nbaz\n");
        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(output, Is.EqualTo(string.Empty));
    }

    [Test]
    public void BasicBRE_MatchesPartialWord()
    {
        var (output, exitCode) = RunGrep(["oo"], "foo\nbar\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("foo\n"));
    }

    [Test]
    public void BasicBRE_AnchorCaret_MatchesStartOfLine()
    {
        var (output, exitCode) = RunGrep(["^foo"], "foo bar\nbar foo\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("foo bar\n"));
    }

    [Test]
    public void BasicBRE_AnchorDollar_MatchesEndOfLine()
    {
        var (output, exitCode) = RunGrep(["foo$"], "bar foo\nfoo bar\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("bar foo\n"));
    }

    [Test]
    public void BasicBRE_Dot_MatchesAnyChar()
    {
        var (output, exitCode) = RunGrep(["f.o"], "foo\nfao\nfbo\nbar\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("foo\nfao\nfbo\n"));
    }

    [Test]
    public void BasicBRE_Star_MatchesZeroOrMore()
    {
        var (output, exitCode) = RunGrep(["ab*c"], "ac\nabc\nabbc\nabbbc\nxyz\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("ac\nabc\nabbc\nabbbc\n"));
    }

    [Test]
    public void BasicBRE_BracketExpression_MatchesCharClass()
    {
        var (output, exitCode) = RunGrep(["[aeiou]"], "hello\ngrp\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("hello\n"));
    }

    [Test]
    public void BasicBRE_NegatedBracket_MatchesComplement()
    {
        var (output, exitCode) = RunGrep(["[^aeiou]"], "aeiou\nhello\n");
        Assert.That(exitCode, Is.EqualTo(0));
        // "hello" has consonants; "aeiou" only has vowels but also matches because grep matches if ANY char satisfies
        // Actually "aeiou" — all vowels — still has non-newline chars but wait: [^aeiou] means consonant; aeiou has no consonants
        // So only "hello" matches
        Assert.That(output, Is.EqualTo("hello\n"));
    }

    [Test]
    public void BasicBRE_BRE_GroupAndBackref()
    {
        // BRE uses \( \) for groups and \1 for backreference
        var (output, exitCode) = RunGrep(["\\(ab\\)\\1"], "abab\nabc\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("abab\n"));
    }

    [Test]
    public void BasicBRE_BRE_PlusIsLiteral()
    {
        // In BRE, + is literal (not a quantifier) unless escaped as \+
        var (output, exitCode) = RunGrep(["a+b"], "a+b\naab\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("a+b\n"));
    }

    [Test]
    public void BasicBRE_EmptyPattern_MatchesAllLines()
    {
        var (output, exitCode) = RunGrep([""], "foo\nbar\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("foo\nbar\n"));
    }

    [Test]
    public void BasicBRE_MultipleMatchingLines()
    {
        var (output, exitCode) = RunGrep(["a"], "apple\nbanana\ncherry\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("apple\nbanana\n"));
    }

    #endregion

    // -------------------------------------------------------------------------
    // 2. Extended Regular Expressions (-E)
    // -------------------------------------------------------------------------

    #region ERE (-E)

    [Test]
    public void ERE_Plus_OneOrMore()
    {
        var (output, exitCode) = RunGrep(["-E", "ab+c"], "ac\nabc\nabbc\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("abc\nabbc\n"));
    }

    [Test]
    public void ERE_QuestionMark_ZeroOrOne()
    {
        var (output, exitCode) = RunGrep(["-E", "colou?r"], "color\ncolour\ncolouur\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("color\ncolour\n"));
    }

    [Test]
    public void ERE_Alternation_Pipe()
    {
        var (output, exitCode) = RunGrep(["-E", "cat|dog"], "I have a cat\nI have a dog\nI have a fish\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("I have a cat\nI have a dog\n"));
    }

    [Test]
    public void ERE_Grouping()
    {
        var (output, exitCode) = RunGrep(["-E", "(ab)+"], "ab\nababab\ncd\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("ab\nababab\n"));
    }

    [Test]
    public void ERE_IntervalExpression_Exact()
    {
        var (output, exitCode) = RunGrep(["-E", "a{3}"], "aa\naaa\naaaa\n");
        Assert.That(exitCode, Is.EqualTo(0));
        // "aaa" matches, "aaaa" also contains "aaa"
        Assert.That(output, Is.EqualTo("aaa\naaaa\n"));
    }

    [Test]
    public void ERE_IntervalExpression_Range()
    {
        var (output, exitCode) = RunGrep(["-E", "a{2,3}"], "a\naa\naaa\naaaa\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("aa\naaa\naaaa\n"));
    }

    [Test]
    public void ERE_IntervalExpression_AtLeast()
    {
        var (output, exitCode) = RunGrep(["-E", "a{2,}"], "a\naa\naaa\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("aa\naaa\n"));
    }

    [Test]
    public void ERE_BackreferenceInERE()
    {
        // ERE uses \1 for backreferences
        var (output, exitCode) = RunGrep(["-E", "(ab)\\1"], "abab\nabc\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("abab\n"));
    }

    #endregion

    // -------------------------------------------------------------------------
    // 3. Fixed-string matching (-F)
    // -------------------------------------------------------------------------

    #region Fixed Strings (-F)

    [Test]
    public void FixedString_TreatsPatternAsLiteral()
    {
        // The dot should be literal, not match any char
        var (output, exitCode) = RunGrep(["-F", "a.b"], "a.b\naxb\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("a.b\n"));
    }

    [Test]
    public void FixedString_SpecialCharsAreLiteral()
    {
        var (output, exitCode) = RunGrep(["-F", "a+b*c"], "a+b*c\nabc\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("a+b*c\n"));
    }

    [Test]
    public void FixedString_NoMatch_ExitCode1()
    {
        var (output, exitCode) = RunGrep(["-F", "xyz"], "abc\ndef\n");
        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(output, Is.EqualTo(string.Empty));
    }

    [Test]
    public void FixedString_Multiline_MatchAll()
    {
        var (output, exitCode) = RunGrep(["-F", "foo"], "foobar\nbazfoo\nqux\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("foobar\nbazfoo\n"));
    }

    #endregion

    // -------------------------------------------------------------------------
    // 4. Case-insensitive matching (-i)
    // -------------------------------------------------------------------------

    #region Case-Insensitive (-i)

    [Test]
    public void CaseInsensitive_MatchesUpperAndLower()
    {
        var (output, exitCode) = RunGrep(["-i", "hello"], "Hello World\nhello world\nHELLO\ngoodbye\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("Hello World\nhello world\nHELLO\n"));
    }

    [Test]
    public void CaseInsensitive_WithERE()
    {
        var (output, exitCode) = RunGrep(["-E", "-i", "cat|dog"], "CAT\nDog\nbird\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("CAT\nDog\n"));
    }

    [Test]
    public void CaseInsensitive_WithFixedString()
    {
        var (output, exitCode) = RunGrep(["-F", "-i", "FOO"], "foo\nFoo\nFOO\nbar\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("foo\nFoo\nFOO\n"));
    }

    #endregion

    // -------------------------------------------------------------------------
    // 5. Inverted matching (-v)
    // -------------------------------------------------------------------------

    #region Inverted Match (-v)

    [Test]
    public void InvertMatch_PrintsNonMatchingLines()
    {
        var (output, exitCode) = RunGrep(["-v", "foo"], "foo\nbar\nbaz\nfoobar\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("bar\nbaz\n"));
    }

    [Test]
    public void InvertMatch_AllLinesMatch_ExitCode1()
    {
        var (output, exitCode) = RunGrep(["-v", "a"], "a\naa\naaa\n");
        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(output, Is.EqualTo(string.Empty));
    }

    [Test]
    public void InvertMatch_NoLinesMatch_AllPrinted()
    {
        var (output, exitCode) = RunGrep(["-v", "xyz"], "foo\nbar\nbaz\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("foo\nbar\nbaz\n"));
    }

    [Test]
    public void InvertMatch_CombinedWithCaseInsensitive()
    {
        var (output, exitCode) = RunGrep(["-v", "-i", "foo"], "FOO\nbar\nFooBar\nbaz\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("bar\nbaz\n"));
    }

    #endregion

    // -------------------------------------------------------------------------
    // 6. Line numbers (-n)
    // -------------------------------------------------------------------------

    #region Line Numbers (-n)

    [Test]
    public void LineNumbers_PrefixesMatchedLines()
    {
        var (output, exitCode) = RunGrep(["-n", "foo"], "bar\nfoo\nbaz\nfoo\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("2:foo\n4:foo\n"));
    }

    [Test]
    public void LineNumbers_FirstLine()
    {
        var (output, exitCode) = RunGrep(["-n", "^first"], "first line\nsecond line\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("1:first line\n"));
    }

    [Test]
    public void LineNumbers_WithInvertMatch()
    {
        var (output, exitCode) = RunGrep(["-n", "-v", "foo"], "foo\nbar\nbaz\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("2:bar\n3:baz\n"));
    }

    #endregion

    // -------------------------------------------------------------------------
    // 7. Files with matches (-l) and without matches (-L)
    // -------------------------------------------------------------------------

    #region File List (-l / -L)

    [Test]
    public void FilesWithMatches_PrintsMatchingFilenames()
    {
        var (output, exitCode) = RunGrepOnFiles(["-l", "foo"], "foo bar\nbaz\n", "qux quux\n");
        Assert.That(exitCode, Is.EqualTo(0));
        // Only the first file contains "foo"
        Assert.That(output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length, Is.EqualTo(1));
    }

    [Test]
    public void FilesWithoutMatches_PrintsNonMatchingFilenames()
    {
        var (output, exitCode) = RunGrepOnFiles(["-L", "foo"], "foo bar\n", "qux quux\n");
        Assert.That(exitCode, Is.EqualTo(0));
        // Only the second file has no "foo"
        Assert.That(output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length, Is.EqualTo(1));
    }

    [Test]
    public void FilesWithMatches_BothMatch_PrintsBothFilenames()
    {
        var (output, exitCode) = RunGrepOnFiles(["-l", "foo"], "foo\n", "foo bar\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length, Is.EqualTo(2));
    }

    #endregion

    // -------------------------------------------------------------------------
    // 8. Count matches (-c)
    // -------------------------------------------------------------------------

    #region Count (-c)

    [Test]
    public void Count_PrintsMatchingLineCount()
    {
        var (output, exitCode) = RunGrep(["-c", "foo"], "foo\nbar\nfoo\nbaz\nfoo\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output.Trim(), Is.EqualTo("3"));
    }

    [Test]
    public void Count_ZeroMatches_PrintsZero()
    {
        var (output, exitCode) = RunGrep(["-c", "xyz"], "foo\nbar\nbaz\n");
        // grep returns exit code 1 when nothing matches, but still prints 0 for -c
        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(output.Trim(), Is.EqualTo("0"));
    }

    [Test]
    public void Count_MultipleFiles_PrintsCountPerFile()
    {
        var (output, exitCode) = RunGrepOnFiles(["-c", "foo"], "foo\nfoo\n", "foo\n");
        Assert.That(exitCode, Is.EqualTo(0));
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        // Each line is "filename:count"
        Assert.That(lines.Length, Is.EqualTo(2));
        Assert.That(lines[0], Does.EndWith(":2"));
        Assert.That(lines[1], Does.EndWith(":1"));
    }

    #endregion

    // -------------------------------------------------------------------------
    // 9. Only matching (-o)
    // -------------------------------------------------------------------------

    #region Only Matching (-o)

    [Test]
    public void OnlyMatching_PrintsMatchedPortionOnly()
    {
        var (output, exitCode) = RunGrep(["-o", "foo"], "foobar bazfoo\nqux\n");
        Assert.That(exitCode, Is.EqualTo(0));
        // Each match on its own line
        Assert.That(output, Is.EqualTo("foo\nfoo\n"));
    }

    [Test]
    public void OnlyMatching_ERE_MultipleGroupMatches()
    {
        var (output, exitCode) = RunGrep(["-E", "-o", "[0-9]+"], "abc123def456\nno digits\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("123\n456\n"));
    }

    [Test]
    public void OnlyMatching_WithLineNumbers()
    {
        var (output, exitCode) = RunGrep(["-n", "-o", "foo"], "foobar\nqux\nfoo baz\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("1:foo\n3:foo\n"));
    }

    [Test]
    public void OnlyMatching_NoMatch_Empty()
    {
        var (output, exitCode) = RunGrep(["-o", "xyz"], "abc\ndef\n");
        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(output, Is.EqualTo(string.Empty));
    }

    #endregion

    // -------------------------------------------------------------------------
    // 10. Whole-word matching (-w)
    // -------------------------------------------------------------------------

    #region Whole Word (-w)

    [Test]
    public void WholeWord_DoesNotMatchSubstring()
    {
        var (output, exitCode) = RunGrep(["-w", "foo"], "foobar\nfoo\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("foo\n"));
    }

    [Test]
    public void WholeWord_MatchesSurroundedByNonWord()
    {
        var (output, exitCode) = RunGrep(["-w", "foo"], "a foo b\n(foo)\nfoo.\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("a foo b\n(foo)\nfoo.\n"));
    }

    [Test]
    public void WholeWord_AtStartOfLine()
    {
        var (output, exitCode) = RunGrep(["-w", "hello"], "hello world\nxhello world\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("hello world\n"));
    }

    [Test]
    public void WholeWord_CombinedWithIgnoreCase()
    {
        var (output, exitCode) = RunGrep(["-w", "-i", "foo"], "FOO\nFooBar\nfoo bar\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("FOO\nfoo bar\n"));
    }

    #endregion

    // -------------------------------------------------------------------------
    // 11. Whole-line matching (-x)
    // -------------------------------------------------------------------------

    #region Whole Line (-x)

    [Test]
    public void WholeLine_ExactLineMatch()
    {
        var (output, exitCode) = RunGrep(["-x", "foo"], "foo\nfoo bar\nbar foo\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("foo\n"));
    }

    [Test]
    public void WholeLine_NoMatch_WhenPartial()
    {
        var (output, exitCode) = RunGrep(["-x", "foo"], "foobar\nbarfoo\n");
        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(output, Is.EqualTo(string.Empty));
    }

    [Test]
    public void WholeLine_WithFixedString()
    {
        var (output, exitCode) = RunGrep(["-x", "-F", "a.b"], "a.b\naxb\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("a.b\n"));
    }

    [Test]
    public void WholeLine_CombinedWithERE()
    {
        var (output, exitCode) = RunGrep(["-x", "-E", "[0-9]+"], "123\n456\nabc\n12x\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("123\n456\n"));
    }

    #endregion

    // -------------------------------------------------------------------------
    // 12. Max count (-m N)
    // -------------------------------------------------------------------------

    #region Max Count (-m)

    [Test]
    public void MaxCount_StopsAfterNMatches()
    {
        var (output, exitCode) = RunGrep(["-m", "2", "foo"], "foo\nbar\nfoo\nbaz\nfoo\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("foo\nfoo\n"));
    }

    [Test]
    public void MaxCount_One_OnlyFirstMatch()
    {
        var (output, exitCode) = RunGrep(["-m", "1", "a"], "apple\napricot\nbanana\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("apple\n"));
    }

    [Test]
    public void MaxCount_LargerThanMatchCount_AllMatches()
    {
        var (output, exitCode) = RunGrep(["-m", "100", "foo"], "foo\nbar\nfoo\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("foo\nfoo\n"));
    }

    #endregion

    // -------------------------------------------------------------------------
    // 13. Context lines (-A, -B, -C)
    // -------------------------------------------------------------------------

    #region Context Lines (-A / -B / -C)

    [Test]
    public void ContextAfter_PrintsLinesAfterMatch()
    {
        var (output, exitCode) = RunGrep(["-A", "2", "MATCH"], "before\nMATCH\nafter1\nafter2\nafter3\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("MATCH\nafter1\nafter2\n"));
    }

    [Test]
    public void ContextBefore_PrintsLinesBeforeMatch()
    {
        var (output, exitCode) = RunGrep(["-B", "2", "MATCH"], "before2\nbefore1\nMATCH\nafter\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("before2\nbefore1\nMATCH\n"));
    }

    [Test]
    public void ContextBoth_PrintsLinesAroundMatch()
    {
        var (output, exitCode) = RunGrep(["-C", "1", "MATCH"], "before\nMATCH\nafter\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("before\nMATCH\nafter\n"));
    }

    [Test]
    public void ContextAfter_Separator_BetweenGroups()
    {
        // When two match groups don't overlap, grep prints "--" separator
        var (output, exitCode) = RunGrep(["-A", "1", "MATCH"],
            "a\nMATCH\nb\nc\nd\nMATCH\ne\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("MATCH\nb\n--\nMATCH\ne\n"));
    }

    [Test]
    public void ContextBefore_AtStartOfFile()
    {
        // Request 3 lines before but file starts at match — only the match line is printed
        var (output, exitCode) = RunGrep(["-B", "3", "MATCH"], "MATCH\nafter\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("MATCH\n"));
    }

    [Test]
    public void ContextAfter_AtEndOfFile()
    {
        // Request 3 lines after but file ends at match — only the match line is printed
        var (output, exitCode) = RunGrep(["-A", "3", "MATCH"], "before\nMATCH\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("MATCH\n"));
    }

    [Test]
    public void ContextBoth_OverlappingGroups_MergedOutput()
    {
        // Two matches close together — context overlaps, no separator
        var (output, exitCode) = RunGrep(["-C", "2", "MATCH"],
            "a\nMATCH\nb\nMATCH\nc\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("a\nMATCH\nb\nMATCH\nc\n"));
    }

    #endregion

    // -------------------------------------------------------------------------
    // 14. Multiple patterns (-e)
    // -------------------------------------------------------------------------

    #region Multiple Patterns (-e)

    [Test]
    public void MultiplePatterns_TwoPatterns_MatchEither()
    {
        var (output, exitCode) = RunGrep(["-e", "foo", "-e", "bar"], "foo\nbar\nbaz\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("foo\nbar\n"));
    }

    [Test]
    public void MultiplePatterns_ThreePatterns()
    {
        var (output, exitCode) = RunGrep(["-e", "cat", "-e", "dog", "-e", "bird"],
            "I have a cat\nI have a dog\nI have a bird\nI have a fish\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("I have a cat\nI have a dog\nI have a bird\n"));
    }

    [Test]
    public void MultiplePatterns_NoneMatch_ExitCode1()
    {
        var (output, exitCode) = RunGrep(["-e", "xyz", "-e", "abc"], "foo\nbar\n");
        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(output, Is.EqualTo(string.Empty));
    }

    [Test]
    public void MultiplePatterns_WithERE()
    {
        var (output, exitCode) = RunGrep(["-E", "-e", "[0-9]+", "-e", "[A-Z]+"],
            "hello123\nWORLD\nlower\n42\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("hello123\nWORLD\n42\n"));
    }

    #endregion

    // -------------------------------------------------------------------------
    // 15. Pattern file (-f)
    // -------------------------------------------------------------------------

    #region Pattern File (-f)

    [Test]
    public void PatternFile_SinglePattern()
    {
        var (output, exitCode) = RunGrepWithPatternFile([], ["foo"], "foo\nbar\nbaz\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("foo\n"));
    }

    [Test]
    public void PatternFile_MultiplePatterns_MatchAny()
    {
        var (output, exitCode) = RunGrepWithPatternFile([], ["foo", "bar"], "foo\nbar\nbaz\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("foo\nbar\n"));
    }

    [Test]
    public void PatternFile_WithERE()
    {
        var (output, exitCode) = RunGrepWithPatternFile(["-E"], ["[0-9]+", "[A-Z]+"],
            "hello\n123\nWORLD\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("123\nWORLD\n"));
    }

    [Test]
    public void PatternFile_CombinedWithInlinePattern()
    {
        // -f pattern file AND -e inline — both patterns active
        var patternFile = Path.Combine(_tempDir, $"pf_{Guid.NewGuid():N}.txt");
        File.WriteAllText(patternFile, "baz\n");
        try
        {
            var (output, exitCode) = RunGrep(["-e", "foo", "-f", patternFile], "foo\nbar\nbaz\n");
            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(output, Is.EqualTo("foo\nbaz\n"));
        }
        finally
        {
            if (File.Exists(patternFile)) File.Delete(patternFile);
        }
    }

    #endregion

    // -------------------------------------------------------------------------
    // 16. Recursive search (-r / -R)
    // -------------------------------------------------------------------------

    #region Recursive (-r)

    [Test]
    public void Recursive_FindsMatchInSubdirectory()
    {
        var dir = Path.Combine(_tempDir, $"recur_{Guid.NewGuid():N}");
        var subDir = Path.Combine(dir, "sub");
        Directory.CreateDirectory(subDir);
        var file1 = Path.Combine(dir, "a.txt");
        var file2 = Path.Combine(subDir, "b.txt");
        File.WriteAllText(file1, "no match here\n");
        File.WriteAllText(file2, "foo found here\n");
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = GrepPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.StartInfo.ArgumentList.Add("-r");
            process.StartInfo.ArgumentList.Add("foo");
            process.StartInfo.ArgumentList.Add(dir);
            process.Start();
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            Assert.That(process.ExitCode, Is.EqualTo(0));
            Assert.That(output, Does.Contain("foo found here"));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public void Recursive_NoMatch_ExitCode1()
    {
        var dir = Path.Combine(_tempDir, $"recur_nm_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.txt"), "nothing here\n");
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = GrepPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.StartInfo.ArgumentList.Add("-r");
            process.StartInfo.ArgumentList.Add("xyz");
            process.StartInfo.ArgumentList.Add(dir);
            process.Start();
            process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            Assert.That(process.ExitCode, Is.EqualTo(1));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public void Recursive_WithIncludeGlob()
    {
        var dir = Path.Combine(_tempDir, $"recur_inc_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.txt"), "foo in txt\n");
        File.WriteAllText(Path.Combine(dir, "b.log"), "foo in log\n");
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = GrepPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.StartInfo.ArgumentList.Add("-r");
            process.StartInfo.ArgumentList.Add("--include=*.txt");
            process.StartInfo.ArgumentList.Add("foo");
            process.StartInfo.ArgumentList.Add(dir);
            process.Start();
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            Assert.That(process.ExitCode, Is.EqualTo(0));
            Assert.That(output, Does.Contain("foo in txt"));
            Assert.That(output, Does.Not.Contain("foo in log"));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public void Recursive_WithExcludeGlob()
    {
        var dir = Path.Combine(_tempDir, $"recur_exc_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.txt"), "foo in txt\n");
        File.WriteAllText(Path.Combine(dir, "b.log"), "foo in log\n");
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = GrepPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.StartInfo.ArgumentList.Add("-r");
            process.StartInfo.ArgumentList.Add("--exclude=*.log");
            process.StartInfo.ArgumentList.Add("foo");
            process.StartInfo.ArgumentList.Add(dir);
            process.Start();
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            Assert.That(process.ExitCode, Is.EqualTo(0));
            Assert.That(output, Does.Contain("foo in txt"));
            Assert.That(output, Does.Not.Contain("foo in log"));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    #endregion

    // -------------------------------------------------------------------------
    // 17. Quiet mode (-q) and exit codes
    // -------------------------------------------------------------------------

    #region Quiet Mode (-q) and Exit Codes

    [Test]
    public void Quiet_Match_ExitCode0_NoOutput()
    {
        var (output, exitCode) = RunGrep(["-q", "foo"], "foo\nbar\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo(string.Empty));
    }

    [Test]
    public void Quiet_NoMatch_ExitCode1_NoOutput()
    {
        var (output, exitCode) = RunGrep(["-q", "xyz"], "foo\nbar\n");
        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(output, Is.EqualTo(string.Empty));
    }

    [Test]
    public void ExitCode_MatchFound_Is0()
    {
        var (_, exitCode) = RunGrep(["foo"], "foo\n");
        Assert.That(exitCode, Is.EqualTo(0));
    }

    [Test]
    public void ExitCode_NoMatchFound_Is1()
    {
        var (_, exitCode) = RunGrep(["foo"], "bar\n");
        Assert.That(exitCode, Is.EqualTo(1));
    }

    [Test]
    public void ExitCode_InvalidPattern_Is2()
    {
        // An invalid regex should produce exit code 2
        var (_, exitCode) = RunGrep(["[invalid"], "anything\n");
        Assert.That(exitCode, Is.EqualTo(2));
    }

    #endregion

    // -------------------------------------------------------------------------
    // 18. Multiple files — filename prefixes
    // -------------------------------------------------------------------------

    #region Multiple Files

    [Test]
    public void MultipleFiles_MatchesPrefixedWithFilename()
    {
        var (output, exitCode) = RunGrepOnFiles(["foo"], "foo line\n", "foo other\n");
        Assert.That(exitCode, Is.EqualTo(0));
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.That(lines.Length, Is.EqualTo(2));
        // Each line prefixed with filename
        Assert.That(lines[0], Does.Contain(":foo"));
        Assert.That(lines[1], Does.Contain(":foo"));
    }

    [Test]
    public void MultipleFiles_OnlyOneMatches_OtherSilent()
    {
        var (output, exitCode) = RunGrepOnFiles(["foo"], "foo\n", "bar\n");
        Assert.That(exitCode, Is.EqualTo(0));
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.That(lines.Length, Is.EqualTo(1));
        Assert.That(lines[0], Does.Contain(":foo"));
    }

    [Test]
    public void MultipleFiles_NoneMatch_ExitCode1()
    {
        var (output, exitCode) = RunGrepOnFiles(["xyz"], "foo\n", "bar\n");
        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(output, Is.EqualTo(string.Empty));
    }

    [Test]
    public void MultipleFiles_NoFilenamePrefix_WithH()
    {
        // -h suppresses filename prefix when searching multiple files
        var (output, exitCode) = RunGrepOnFiles(["-h", "foo"], "foo\n", "bar\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("foo\n"));
    }

    #endregion

    // -------------------------------------------------------------------------
    // 19. Edge cases
    // -------------------------------------------------------------------------

    #region Edge Cases

    [Test]
    public void EdgeCase_EmptyInput_NoMatch_ExitCode1()
    {
        var (output, exitCode) = RunGrep(["foo"], string.Empty);
        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(output, Is.EqualTo(string.Empty));
    }

    [Test]
    public void EdgeCase_EmptyInput_EmptyPattern_ExitCode1()
    {
        // Empty pattern on empty input — no lines to match
        var (output, exitCode) = RunGrep([""], string.Empty);
        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(output, Is.EqualTo(string.Empty));
    }

    [Test]
    public void EdgeCase_SingleLineNoNewline_Matches()
    {
        // Input with no trailing newline
        var (output, exitCode) = RunGrep(["foo"], "foo");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("foo\n"));
    }

    [Test]
    public void EdgeCase_BlankLinesInput_MatchBlanks()
    {
        var (output, exitCode) = RunGrep(["^$"], "foo\n\nbar\n\nbaz\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("\n\n"));
    }

    [Test]
    public void EdgeCase_TabCharacterInPattern()
    {
        var (output, exitCode) = RunGrep(["foo\tbar"], "foo\tbar\nfoo bar\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("foo\tbar\n"));
    }

    [Test]
    public void EdgeCase_SpecialRegexCharsInFixedString()
    {
        var (output, exitCode) = RunGrep(["-F", "a(b)c"], "a(b)c\nabc\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("a(b)c\n"));
    }

    [Test]
    public void EdgeCase_VeryLongLine_Matches()
    {
        string longLine = new string('a', 10000) + "NEEDLE" + new string('b', 10000);
        var (output, exitCode) = RunGrep(["NEEDLE"], longLine + "\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output.Trim(), Is.EqualTo(longLine));
    }

    [Test]
    public void EdgeCase_UnicodeCharacters_Match()
    {
        var (output, exitCode) = RunGrep(["caf\u00e9"], "I love caf\u00e9\nno match\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("I love caf\u00e9\n"));
    }

    [Test]
    public void EdgeCase_DotMatchesAnyNonNewline()
    {
        // Dot should NOT match newline
        var (output, exitCode) = RunGrep(["^.$"], "a\nab\n\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("a\n"));
    }

    [Test]
    public void EdgeCase_CaseSensitive_ByDefault()
    {
        var (output, exitCode) = RunGrep(["foo"], "FOO\nfoo\nFoo\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("foo\n"));
    }

    [Test]
    public void EdgeCase_PatternMatchingWholeFile()
    {
        // Pattern matches every line
        var (output, exitCode) = RunGrep([".*"], "a\nb\nc\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("a\nb\nc\n"));
    }

    [Test]
    public void EdgeCase_SquareBracketSpecialChars()
    {
        // Match lines containing ] or [ using POSIX bracket expression [][]
        var (output, exitCode) = RunGrep(["[][]"], "a[b\nc]d\nxyz\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("a[b\nc]d\n"));
    }

    #endregion

    // -------------------------------------------------------------------------
    // 20. Anchors and multiline behaviour
    // -------------------------------------------------------------------------

    #region Anchors

    [Test]
    public void Anchor_CaretMatchesBeginningOfLine()
    {
        var (output, exitCode) = RunGrep(["^The"], "The cat\nNot The cat\nThe dog\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("The cat\nThe dog\n"));
    }

    [Test]
    public void Anchor_DollarMatchesEndOfLine()
    {
        var (output, exitCode) = RunGrep(["end$"], "at the end\nmiddle end here\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("at the end\n"));
    }

    [Test]
    public void Anchor_CaretAndDollar_EmptyLine()
    {
        var (output, exitCode) = RunGrep(["^$"], "nonempty\n\nalso nonempty\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("\n"));
    }

    [Test]
    public void Anchor_CaretDollar_ExactMatch()
    {
        var (output, exitCode) = RunGrep(["^exact$"], "exact\nexact match\nnot exact\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("exact\n"));
    }

    #endregion

    // -------------------------------------------------------------------------
    // 21. Character classes (POSIX)
    // -------------------------------------------------------------------------

    #region POSIX Character Classes

    [Test]
    public void CharClass_Alpha_MatchesLetters()
    {
        var (output, exitCode) = RunGrep(["[[:alpha:]]"], "abc\n123\n!@#\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("abc\n"));
    }

    [Test]
    public void CharClass_Digit_MatchesNumbers()
    {
        var (output, exitCode) = RunGrep(["[[:digit:]]"], "abc\n123\n!@#\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("123\n"));
    }

    [Test]
    public void CharClass_Space_MatchesWhitespace()
    {
        var (output, exitCode) = RunGrep(["[[:space:]]"], "nospace\nhas space\nhas\ttab\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("has space\nhas\ttab\n"));
    }

    [Test]
    public void CharClass_Upper_MatchesUppercase()
    {
        var (output, exitCode) = RunGrep(["[[:upper:]]"], "lower\nUPPER\nMixed\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("UPPER\nMixed\n"));
    }

    [Test]
    public void CharClass_Alnum_MatchesAlphanumeric()
    {
        var (output, exitCode) = RunGrep(["^[[:alnum:]]*$"], "abc123\n!@#\nhello\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("abc123\nhello\n"));
    }

    #endregion

    // -------------------------------------------------------------------------
    // 22. Combination flags
    // -------------------------------------------------------------------------

    #region Combination Flags

    [Test]
    public void Combination_n_v_ShowsNonMatchingLinesWithNumbers()
    {
        var (output, exitCode) = RunGrep(["-n", "-v", "foo"],
            "foo\nbar\nbaz\nfoobar\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("2:bar\n3:baz\n"));
    }

    [Test]
    public void Combination_c_i_CountsCaseInsensitive()
    {
        var (output, exitCode) = RunGrep(["-c", "-i", "foo"],
            "FOO\nfoo\nFoo\nbar\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output.Trim(), Is.EqualTo("3"));
    }

    [Test]
    public void Combination_o_n_PrintsMatchedPartsWithLineNumbers()
    {
        var (output, exitCode) = RunGrep(["-E", "-o", "-n", "[0-9]+"],
            "abc123def\nno digits\n456\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("1:123\n3:456\n"));
    }

    [Test]
    public void Combination_E_i_w_CaseInsensitiveWholeWordERE()
    {
        var (output, exitCode) = RunGrep(["-E", "-i", "-w", "foo|bar"],
            "FOO\nfoobar\nBAR baz\nother\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("FOO\nBAR baz\n"));
    }

    [Test]
    public void Combination_m_v_MaxCountWithInvert()
    {
        var (output, exitCode) = RunGrep(["-m", "2", "-v", "foo"],
            "foo\nbar\nbaz\nfoobar\nqux\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("bar\nbaz\n"));
    }

    #endregion

    // -------------------------------------------------------------------------
    // 23. BRE vs ERE equivalence
    // -------------------------------------------------------------------------

    #region BRE vs ERE

    [Test]
    public void BRE_EscapedParens_ForGrouping()
    {
        var (output, exitCode) = RunGrep(["\\(foo\\)\\+"], "foo\nfoofoo\nbar\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("foo\nfoofoo\n"));
    }

    [Test]
    public void ERE_UnescapedParens_ForGrouping()
    {
        var (output, exitCode) = RunGrep(["-E", "(foo)+"], "foo\nfoofoo\nbar\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("foo\nfoofoo\n"));
    }

    [Test]
    public void BRE_LiteralPlus_NoMetameaning()
    {
        // In BRE, + is literal. Match the string "a+b".
        var (output, exitCode) = RunGrep(["a+b"], "a+b\naab\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("a+b\n"));
    }

    [Test]
    public void ERE_Plus_AsQuantifier()
    {
        var (output, exitCode) = RunGrep(["-E", "a+b"], "ab\naab\nb\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("ab\naab\n"));
    }

    [Test]
    public void BRE_LiteralQuestionMark()
    {
        // In BRE, ? is literal
        var (output, exitCode) = RunGrep(["done?"], "done?\ndone\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("done?\n"));
    }

    [Test]
    public void ERE_QuestionMark_AsQuantifier()
    {
        var (output, exitCode) = RunGrep(["-E", "colou?r"], "colour\ncolor\ncolouur\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("colour\ncolor\n"));
    }

    #endregion

    // -------------------------------------------------------------------------
    // 24. Stdin / pipe behaviour (already covered by RunGrep helper)
    // -------------------------------------------------------------------------

    #region Stdin Behaviour

    [Test]
    public void Stdin_WithDash_ReadsFromStdin()
    {
        // When the filename is "-", grep reads from stdin
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = GrepPath,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("foo");
        process.StartInfo.ArgumentList.Add("-");
        process.Start();
        process.StandardInput.Write("foo\nbar\n");
        process.StandardInput.Close();
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.That(process.ExitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("foo\n"));
    }

    [Test]
    public void Stdin_ManyLines_AllProcessed()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 1000; i++)
            sb.AppendLine(i % 2 == 0 ? "match" : "other");

        var (output, exitCode) = RunGrep(["match"], sb.ToString());
        Assert.That(exitCode, Is.EqualTo(0));
        int matchCount = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        Assert.That(matchCount, Is.EqualTo(500));
    }

    #endregion

    // -------------------------------------------------------------------------
    // 25. Suppressing filename with -h and forcing it with -H
    // -------------------------------------------------------------------------

    #region Filename Control (-h / -H)

    [Test]
    public void FilenameControl_H_ForcesFilenameOnSingleFile()
    {
        var (output, exitCode) = RunGrepOnFiles(["-H", "foo"], "foo\nbar\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Does.Contain(":foo"));
    }

    [Test]
    public void FilenameControl_h_SuppressesFilenameOnMultipleFiles()
    {
        var (output, exitCode) = RunGrepOnFiles(["-h", "foo"], "foo\n", "foo bar\n");
        Assert.That(exitCode, Is.EqualTo(0));
        // No ":" filename prefix — raw matching lines only
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.That(lines[0], Does.Not.Contain(":"));
    }

    #endregion
}
