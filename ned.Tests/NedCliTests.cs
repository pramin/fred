using System;
using System.IO;
using System.Text;
using NUnit.Framework;
using ned;

namespace ned.Tests;

/// <summary>
/// Tests for WI-011 CLI enhancements: multiple -e, -f mixing, -i in-place,
/// -E/-r ERE mode, -s separate files, -z NUL-separated, -- end-of-options,
/// and error cases.
///
/// Tests call Program.Main() with redirected Console streams to avoid
/// spawning child processes.
/// </summary>
[TestFixture]
public class NedCliTests
{
    // Temp files created per-test, cleaned up in TearDown.
    private List<string> _tempFiles = new();

    [TearDown]
    public void TearDown()
    {
        for (int i = 0; i < _tempFiles.Count; i++)
        {
            var path = _tempFiles[i];
            if (File.Exists(path))
                File.Delete(path);
        }
        _tempFiles.Clear();
    }

    // -----------------------------------------------------------------------
    // Helper: run Program.Main with controlled stdin/stdout/stderr
    // -----------------------------------------------------------------------

    private static (int ExitCode, string Stdout, string Stderr) RunNed(
        string[] args,
        string? stdinContent = null)
    {
        var origIn = Console.In;
        var origOut = Console.Out;
        var origErr = Console.Error;

        var stdoutBuf = new StringBuilder();
        var stderrBuf = new StringBuilder();

        try
        {
            if (stdinContent != null)
                Console.SetIn(new StringReader(stdinContent));

            Console.SetOut(new StringWriter(stdoutBuf));
            Console.SetError(new StringWriter(stderrBuf));

            int exitCode = Program.Main(args);
            return (exitCode, stdoutBuf.ToString(), stderrBuf.ToString());
        }
        finally
        {
            Console.SetIn(origIn);
            Console.SetOut(origOut);
            Console.SetError(origErr);
        }
    }

    /// <summary>
    /// Assert that a run completed with exit code 0 and no stderr output.
    /// </summary>
    private static void AssertCleanRun(int exitCode, string stderr)
    {
        Assert.That(exitCode, Is.EqualTo(0), $"Expected exit code 0 but got {exitCode}. Stderr: {stderr}");
        Assert.That(stderr, Is.Empty, $"Expected empty stderr but got: {stderr}");
    }

    private string WriteTempFile(string content)
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, content);
        _tempFiles.Add(path);
        return path;
    }

    // -----------------------------------------------------------------------
    // 1. Multiple -e scripts
    // -----------------------------------------------------------------------

    [Test]
    public void MultipleE_BothScriptsApplied()
    {
        var (code, stdout, stderr) = RunNed(
            new[] { "-e", "s/a/b/", "-e", "s/c/d/" },
            "ac\n");
        AssertCleanRun(code, stderr);
        Assert.That(stdout, Is.EqualTo("bd\n"));
    }

    [Test]
    public void MultipleE_OrderMatters()
    {
        // First script turns 'a' to 'b', second turns 'b' to 'c'.
        // Applied in order means 'a' -> 'b' -> 'c'.
        var (code, stdout, stderr) = RunNed(
            new[] { "-e", "s/a/b/", "-e", "s/b/c/" },
            "a\n");
        AssertCleanRun(code, stderr);
        Assert.That(stdout, Is.EqualTo("c\n"));
    }

    [Test]
    public void MultipleE_ThreeScripts()
    {
        var (code, stdout, stderr) = RunNed(
            new[] { "-e", "s/1/one/", "-e", "s/2/two/", "-e", "s/3/three/" },
            "123\n");
        AssertCleanRun(code, stderr);
        Assert.That(stdout, Is.EqualTo("onetwothree\n"));
    }

    // -----------------------------------------------------------------------
    // 2. Mixed -e and -f
    // -----------------------------------------------------------------------

    [Test]
    public void MixedEandF_ScriptsAppliedInOrder()
    {
        var scriptFile = WriteTempFile("s/b/B/\n");
        var (code, stdout, stderr) = RunNed(
            new[] { "-e", "s/a/A/", "-f", scriptFile },
            "ab\n");
        AssertCleanRun(code, stderr);
        Assert.That(stdout, Is.EqualTo("AB\n"));
    }

    [Test]
    public void MixedEandF_FBeforeE()
    {
        var scriptFile = WriteTempFile("s/a/A/\n");
        var (code, stdout, stderr) = RunNed(
            new[] { "-f", scriptFile, "-e", "s/b/B/" },
            "ab\n");
        AssertCleanRun(code, stderr);
        Assert.That(stdout, Is.EqualTo("AB\n"));
    }

    // -----------------------------------------------------------------------
    // 3. -i with backup suffix
    // -----------------------------------------------------------------------

    [Test]
    public void InPlace_WithBackupSuffix_OriginalPreserved()
    {
        var file = WriteTempFile("hello world\n");
        var backup = file + ".bak";
        _tempFiles.Add(backup);

        var (code, stdout, stderr) = RunNed(new[] { "-i.bak", "s/hello/goodbye/", file });

        AssertCleanRun(code, stderr);
        Assert.That(stdout, Is.EqualTo(""));  // no stdout for in-place
        Assert.That(File.ReadAllText(file), Is.EqualTo("goodbye world\n"));
        Assert.That(File.Exists(backup), Is.True);
        Assert.That(File.ReadAllText(backup), Is.EqualTo("hello world\n"));
    }

    [Test]
    public void InPlace_WithBackupSuffix_NoSpaceBetweenFlagAndSuffix()
    {
        // GNU sed convention: -i.bak (no space), NOT -i .bak (space)
        var file = WriteTempFile("foo\n");
        var backup = file + ".orig";
        _tempFiles.Add(backup);

        var (code, _, stderr) = RunNed(new[] { "-i.orig", "s/foo/bar/", file });

        AssertCleanRun(code, stderr);
        Assert.That(File.ReadAllText(file), Is.EqualTo("bar\n"));
        Assert.That(File.Exists(backup), Is.True);
    }

    // -----------------------------------------------------------------------
    // 4. -i without suffix
    // -----------------------------------------------------------------------

    [Test]
    public void InPlace_NoSuffix_FileModifiedNoBackup()
    {
        var file = WriteTempFile("original content\n");
        var backup = file + ".bak";  // should NOT exist

        var (code, stdout, stderr) = RunNed(new[] { "-i", "s/original/modified/", file });

        AssertCleanRun(code, stderr);
        Assert.That(stdout, Is.EqualTo(""));
        Assert.That(File.ReadAllText(file), Is.EqualTo("modified content\n"));
        Assert.That(File.Exists(backup), Is.False);
    }

    [Test]
    public void InPlace_NoSuffix_AtomicWrite_FileReplaced()
    {
        // Verify the temp-file + rename approach: file content is replaced atomically
        var file = WriteTempFile("line1\nline2\nline3\n");

        var (code, _, stderr) = RunNed(new[] { "-i", "s/line/row/g", file });

        AssertCleanRun(code, stderr);
        Assert.That(File.ReadAllText(file), Is.EqualTo("row1\nrow2\nrow3\n"));
    }

    // -----------------------------------------------------------------------
    // 5. -i with multiple files
    // -----------------------------------------------------------------------

    [Test]
    public void InPlace_MultipleFiles_EachModifiedIndependently()
    {
        var file1 = WriteTempFile("apple\n");
        var file2 = WriteTempFile("banana\n");
        var bak1 = file1 + ".bak";
        var bak2 = file2 + ".bak";
        _tempFiles.Add(bak1);
        _tempFiles.Add(bak2);

        var (code, stdout, stderr) = RunNed(
            new[] { "-i.bak", "s/a/X/g", file1, file2 });

        AssertCleanRun(code, stderr);
        Assert.That(stdout, Is.EqualTo(""));
        Assert.That(File.ReadAllText(file1), Is.EqualTo("Xpple\n"));
        Assert.That(File.ReadAllText(file2), Is.EqualTo("bXnXnX\n"));
        Assert.That(File.Exists(bak1), Is.True);
        Assert.That(File.Exists(bak2), Is.True);
        Assert.That(File.ReadAllText(bak1), Is.EqualTo("apple\n"));
        Assert.That(File.ReadAllText(bak2), Is.EqualTo("banana\n"));
    }

    [Test]
    public void InPlace_MultipleFiles_NoSuffix()
    {
        var file1 = WriteTempFile("cat\n");
        var file2 = WriteTempFile("dog\n");

        var (code, _, stderr) = RunNed(new[] { "-i", "s/./X/g", file1, file2 });

        AssertCleanRun(code, stderr);
        Assert.That(File.ReadAllText(file1), Is.EqualTo("XXX\n"));
        Assert.That(File.ReadAllText(file2), Is.EqualTo("XXX\n"));
    }

    // -----------------------------------------------------------------------
    // 6. -E / -r ERE mode
    // -----------------------------------------------------------------------

    [Test]
    public void EreMode_DashE_GroupWithoutBackslash()
    {
        // In ERE, ( ) are grouping without backslash
        var (code, stdout, stderr) = RunNed(
            new[] { "-E", "s/(word)/[\\1]/g" },
            "word\n");
        AssertCleanRun(code, stderr);
        Assert.That(stdout, Is.EqualTo("[word]\n"));
    }

    [Test]
    public void EreMode_DashR_GroupWithoutBackslash()
    {
        // -r is an alias for -E
        var (code, stdout, stderr) = RunNed(
            new[] { "-r", "s/(hello)/(\\1 world)/" },
            "hello\n");
        AssertCleanRun(code, stderr);
        Assert.That(stdout, Is.EqualTo("(hello world)\n"));
    }

    [Test]
    public void EreMode_PlusQuantifier_NoBackslash()
    {
        // In ERE, + is a quantifier directly (no backslash needed)
        var (code, stdout, stderr) = RunNed(
            new[] { "-E", "s/a+/X/" },
            "aaab\n");
        AssertCleanRun(code, stderr);
        Assert.That(stdout, Is.EqualTo("Xb\n"));
    }

    [Test]
    public void EreMode_Alternation_Pipe()
    {
        // In ERE, | is alternation without backslash
        var (code, stdout, stderr) = RunNed(
            new[] { "-E", "s/cat|dog/pet/g" },
            "cat and dog\n");
        AssertCleanRun(code, stderr);
        Assert.That(stdout, Is.EqualTo("pet and pet\n"));
    }

    [Test]
    public void EreMode_QuestionMark_NoBackslash()
    {
        // In ERE, ? is optional quantifier without backslash
        var (code, stdout, stderr) = RunNed(
            new[] { "-E", "s/colou?r/color/g" },
            "colour and color\n");
        AssertCleanRun(code, stderr);
        Assert.That(stdout, Is.EqualTo("color and color\n"));
    }

    // -----------------------------------------------------------------------
    // 7. -s separate file mode (line numbers and $ per-file)
    // -----------------------------------------------------------------------

    [Test]
    public void SeparateMode_LineNumbersRestartPerFile()
    {
        // Without -s, line 1 is only the first line of the first file.
        // With -s, line 1 is the first line of EACH file.
        var file1 = WriteTempFile("first\nsecond\n");
        var file2 = WriteTempFile("alpha\nbeta\n");

        var (code, stdout, stderr) = RunNed(
            new[] { "-s", "-n", "1p", file1, file2 });

        AssertCleanRun(code, stderr);
        // Should print first line of each file
        Assert.That(stdout, Is.EqualTo("first\nalpha\n"));
    }

    [Test]
    public void SeparateMode_DollarMatchesLastLineOfEachFile()
    {
        var file1 = WriteTempFile("line1\nlastline1\n");
        var file2 = WriteTempFile("lineA\nlastlineA\n");

        var (code, stdout, stderr) = RunNed(
            new[] { "-s", "-n", "$p", file1, file2 });

        AssertCleanRun(code, stderr);
        // $ should match last line of each file independently
        Assert.That(stdout, Is.EqualTo("lastline1\nlastlineA\n"));
    }

    [Test]
    public void NoSeparateMode_DollarMatchesOnlyGlobalLastLine()
    {
        // Without -s, $ only matches the very last line across all files
        var file1 = WriteTempFile("line1\nlastline1\n");
        var file2 = WriteTempFile("lineA\nlastlineA\n");

        var (code, stdout, stderr) = RunNed(
            new[] { "-n", "$p", file1, file2 });

        AssertCleanRun(code, stderr);
        // Only last line of file2 should be printed
        Assert.That(stdout, Is.EqualTo("lastlineA\n"));
    }

    // -----------------------------------------------------------------------
    // 8. -z NUL-separated input
    // -----------------------------------------------------------------------

    [Test]
    public void NulSeparated_SplitsOnNulByte()
    {
        // Records separated by \0 instead of \n
        var (code, stdout, stderr) = RunNed(
            new[] { "-z", "s/foo/bar/" },
            "foo\0baz\0");
        AssertCleanRun(code, stderr);
        // Output should be NUL-separated
        Assert.That(stdout, Is.EqualTo("bar\0baz\0"));
    }

    [Test]
    public void NulSeparated_SubstituteOnlyWithinRecord()
    {
        // Each NUL-delimited record is processed independently
        var (code, stdout, stderr) = RunNed(
            new[] { "-z", "s/x/X/" },
            "ax\0bx\0cx\0");
        AssertCleanRun(code, stderr);
        Assert.That(stdout, Is.EqualTo("aX\0bX\0cX\0"));
    }

    [Test]
    public void NulSeparated_PreservesHoldSpaceAcrossRecords()
    {
        // Hold space must persist across NUL-separated records within a single Transform call.
        // 'h' copies pattern space to hold; 'g' copies hold to pattern.
        // With correct single-pass implementation, 'g' on second record retrieves first record.
        var (code, stdout, stderr) = RunNed(
            new[] { "-z", "-n", "-e", "1h", "-e", "2{g;p}" },
            "first\0second\0");
        AssertCleanRun(code, stderr);
        // Line 1 is stored in hold; line 2 retrieves hold and prints "first"
        Assert.That(stdout, Is.EqualTo("first\0"));
    }

    [Test]
    public void NulSeparated_LineCounterContinuesAcrossRecords()
    {
        // Line numbers must not reset between NUL-separated records.
        // Record 1 is line 1, record 2 is line 2.
        var (code, stdout, stderr) = RunNed(
            new[] { "-z", "-n", "2p" },
            "alpha\0beta\0");
        AssertCleanRun(code, stderr);
        Assert.That(stdout, Is.EqualTo("beta\0"));
    }

    // -----------------------------------------------------------------------
    // 9. -- end-of-options
    // -----------------------------------------------------------------------

    [Test]
    public void EndOfOptions_DashDash_FilenameStartingWithDash()
    {
        // A filename starting with - should be treated as a file, not a flag, after --
        var file = WriteTempFile("hello\n");
        var dirName = Path.GetDirectoryName(file)!;
        var dashFile = Path.Combine(dirName, "-testfile-" + Path.GetFileName(file));
        File.Copy(file, dashFile);
        _tempFiles.Add(dashFile);

        var (code, stdout, stderr) = RunNed(
            new[] { "s/hello/world/", "--", dashFile });

        AssertCleanRun(code, stderr);
        Assert.That(stdout, Is.EqualTo("world\n"));
    }

    [Test]
    public void EndOfOptions_DashDash_MultipleFilesAfter()
    {
        var file1 = WriteTempFile("aaa\n");
        var file2 = WriteTempFile("bbb\n");

        var (code, stdout, stderr) = RunNed(
            new[] { "s/a/X/g", "--", file1, file2 });

        AssertCleanRun(code, stderr);
        Assert.That(stdout, Is.EqualTo("XXX\nbbb\n"));
    }

    // -----------------------------------------------------------------------
    // 10. Error cases
    // -----------------------------------------------------------------------

    [Test]
    public void Error_MissingEArgument_ReturnsError()
    {
        // -e with no following argument should produce an error
        var (code, _, stderr) = RunNed(new[] { "-e" }, "input\n");
        Assert.That(code, Is.Not.EqualTo(0));
        Assert.That(stderr, Is.Not.Empty);
    }

    [Test]
    public void Error_MissingFFile_ReturnsError()
    {
        // -f pointing to a nonexistent file should produce an error
        var (code, _, stderr) = RunNed(
            new[] { "-f", "/nonexistent/script/file.sed", "input.txt" });
        Assert.That(code, Is.Not.EqualTo(0));
        Assert.That(stderr, Is.Not.Empty);
    }

    [Test]
    public void Error_NoScriptSpecified_ReturnsError()
    {
        var (code, _, stderr) = RunNed(Array.Empty<string>(), "input\n");
        Assert.That(code, Is.Not.EqualTo(0));
        Assert.That(stderr, Does.Contain("no script"));
    }

    [Test]
    public void Error_InPlace_NonexistentFile_ReturnsError()
    {
        var (code, _, stderr) = RunNed(
            new[] { "-i", "s/a/b/", "/nonexistent/path/file.txt" });
        Assert.That(code, Is.Not.EqualTo(0));
        Assert.That(stderr, Is.Not.Empty);
    }

    // -----------------------------------------------------------------------
    // 11. -i rejects stdin (MAJOR 10)
    // -----------------------------------------------------------------------

    [Test]
    public void InPlace_RejectsStdin_WhenNoFilesProvided()
    {
        // -i with no file arguments (stdin mode) must be rejected with a clear error.
        var (code, _, stderr) = RunNed(new[] { "-i", "s/a/b/" }, "input\n");
        Assert.That(code, Is.Not.EqualTo(0));
        Assert.That(stderr, Does.Contain("-i"));
    }

    [Test]
    public void InPlace_RejectsDashAsFilename()
    {
        // -i with '-' as an explicit filename must be rejected.
        var realFile = WriteTempFile("x\n");
        var (code, _, stderr) = RunNed(new[] { "-i", "s/a/b/", "-", realFile });
        Assert.That(code, Is.Not.EqualTo(0));
        Assert.That(stderr, Does.Contain("-i"));
    }

    // -----------------------------------------------------------------------
    // 12. -s upfront validation (MAJOR 10)
    // -----------------------------------------------------------------------

    [Test]
    public void SeparateMode_UpfrontValidation_FailsBeforeAnyOutput()
    {
        // With -s, if a later file does not exist, no output should be produced
        // (validation happens before processing starts).
        var file1 = WriteTempFile("hello\n");
        var nonexistent = "/nonexistent/path/missing.txt";

        var (code, stdout, stderr) = RunNed(
            new[] { "-s", "s/hello/world/", file1, nonexistent });

        Assert.That(code, Is.Not.EqualTo(0));
        Assert.That(stderr, Is.Not.Empty);
        // No output should have been produced because validation failed before processing
        Assert.That(stdout, Is.Empty);
    }

    // -----------------------------------------------------------------------
    // 13. Stdin as '-' in default and separate modes (MAJOR 10)
    // -----------------------------------------------------------------------

    [Test]
    public void DefaultMode_DashAsFilename_ReadsFromStdin()
    {
        // '-' as a filename argument should read from stdin
        var file = WriteTempFile("file content\n");

        var (code, stdout, stderr) = RunNed(
            new[] { "s/stdin/STDIN/", "-", file },
            "stdin input\n");

        AssertCleanRun(code, stderr);
        // stdin line processed first, then the file
        Assert.That(stdout, Does.Contain("STDIN input"));
        Assert.That(stdout, Does.Contain("file content"));
    }

    [Test]
    public void SeparateMode_DashAsFilename_ReadsFromStdin()
    {
        // '-' as a filename in separate mode should also read from stdin
        var file = WriteTempFile("file line\n");

        var (code, stdout, stderr) = RunNed(
            new[] { "-s", "-n", "1p", "-", file },
            "stdin line\n");

        AssertCleanRun(code, stderr);
        Assert.That(stdout, Does.Contain("stdin line"));
        Assert.That(stdout, Does.Contain("file line"));
    }

    // -----------------------------------------------------------------------
    // 14. Combined short flags (MAJOR 10)
    // -----------------------------------------------------------------------

    [Test]
    public void CombinedFlags_NZ_WorkTogether()
    {
        // -nz combined: suppress output + NUL separator
        var (code, stdout, stderr) = RunNed(
            new[] { "-nz", "1p" },
            "first\0second\0");
        AssertCleanRun(code, stderr);
        Assert.That(stdout, Is.EqualTo("first\0"));
    }

    [Test]
    public void CombinedFlags_NE_WorkTogether()
    {
        // -n combined with -e
        var (code, stdout, stderr) = RunNed(
            new[] { "-ne", "s/a/b/p" },
            "abc\n");
        AssertCleanRun(code, stderr);
        Assert.That(stdout, Is.EqualTo("bbc\n"));
    }
}
