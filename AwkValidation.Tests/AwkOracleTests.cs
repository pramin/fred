using System.Diagnostics;
using NUnit.Framework;

namespace AwkValidation.Tests;

/// <summary>
/// Oracle test suite for awk. Each test runs the real awk binary and nawk,
/// asserting that nawk produces identical output and exit codes to real awk.
/// All temp files are cleaned up in finally blocks; tests are safe to run in parallel.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.Children)]
public class AwkOracleTests
{
    private const string AwkPath = "/usr/bin/awk";
    private readonly string _nawkPath;
    private string _tempDir = string.Empty;

    public AwkOracleTests()
    {
        _nawkPath = Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "nawk", "bin", "Debug", "net8.0", "nawk");
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "awk-oracle-tests");
        Directory.CreateDirectory(_tempDir);
    }

    [SetUp]
    public void SetUp()
    {
        if (!File.Exists(AwkPath))
            Assert.Ignore($"awk not found at {AwkPath}; skipping oracle tests.");
    }

    // -------------------------------------------------------------------------
    // Infrastructure helpers
    // -------------------------------------------------------------------------

    private static string Truncate(string s, int max = 200)
        => s.Length <= max ? s : s[..max] + "...";

    /// <summary>
    /// Run real awk with the given arguments, providing input via stdin.
    /// </summary>
    private static (string Output, int ExitCode) RunAwkProcess(string[] args, string input)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = AwkPath,
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
            // awk may exit before reading all stdin
        }
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (output, process.ExitCode);
    }

    /// <summary>
    /// Run nawk (dotnet run) with the given arguments, providing input via stdin.
    /// </summary>
    private (string Output, int ExitCode) RunNawkProcess(string[] args, string input)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _nawkPath,
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

    /// <summary>
    /// Run awk with the given arguments, providing input via stdin.
    /// Also runs nawk with identical arguments and asserts matching output and exit code.
    /// Returns (stdout, exitCode) from awk.
    /// </summary>
    private (string Output, int ExitCode) RunAwk(string[] args, string input)
    {
        var awkResult = RunAwkProcess(args, input);
        var nawkResult = RunNawkProcess(args, input);

        Assert.That(nawkResult.ExitCode, Is.EqualTo(awkResult.ExitCode),
            $"nawk exit code should match awk.\n  args: {string.Join(" ", args)}\n  input: {Truncate(input)}");
        Assert.That(nawkResult.Output, Is.EqualTo(awkResult.Output),
            $"nawk output should match awk.\n  args: {string.Join(" ", args)}\n  input: {Truncate(input)}\n  awk output: {Truncate(awkResult.Output)}\n  nawk output: {Truncate(nawkResult.Output)}");

        return awkResult;
    }

    /// <summary>
    /// Run awk with the given arguments reading from one or more temp files.
    /// Also runs nawk with identical files and asserts matching output and exit code.
    /// Returns (stdout, exitCode) from awk.
    /// </summary>
    private (string Output, int ExitCode) RunAwkOnFiles(string[] args, params string[] fileContents)
    {
        var files = new string[fileContents.Length];
        for (int i = 0; i < fileContents.Length; i++)
        {
            files[i] = Path.Combine(_tempDir, $"awk_input_{Guid.NewGuid():N}.txt");
            File.WriteAllText(files[i], fileContents[i]);
        }

        try
        {
            // Run real awk
            var awkProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = AwkPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            for (int i = 0; i < args.Length; i++)
                awkProcess.StartInfo.ArgumentList.Add(args[i]);
            for (int i = 0; i < files.Length; i++)
                awkProcess.StartInfo.ArgumentList.Add(files[i]);

            awkProcess.Start();
            string awkOutput = awkProcess.StandardOutput.ReadToEnd();
            awkProcess.WaitForExit();
            var awkResult = (Output: awkOutput, ExitCode: awkProcess.ExitCode);

            // Run nawk
            var nawkProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _nawkPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            for (int i = 0; i < args.Length; i++)
                nawkProcess.StartInfo.ArgumentList.Add(args[i]);
            for (int i = 0; i < files.Length; i++)
                nawkProcess.StartInfo.ArgumentList.Add(files[i]);

            nawkProcess.Start();
            string nawkOutput = nawkProcess.StandardOutput.ReadToEnd();
            nawkProcess.WaitForExit();
            var nawkResult = (Output: nawkOutput, ExitCode: nawkProcess.ExitCode);

            Assert.That(nawkResult.ExitCode, Is.EqualTo(awkResult.ExitCode),
                $"nawk exit code should match awk for file-based test.\n  args: {string.Join(" ", args)}");
            Assert.That(nawkResult.Output, Is.EqualTo(awkResult.Output),
                $"nawk output should match awk for file-based test.\n  args: {string.Join(" ", args)}\n  awk: {Truncate(awkResult.Output)}\n  nawk: {Truncate(nawkResult.Output)}");

            return awkResult;
        }
        finally
        {
            for (int i = 0; i < files.Length; i++)
                if (File.Exists(files[i])) File.Delete(files[i]);
        }
    }

    /// <summary>
    /// Run awk with a program file (-f), providing input via stdin.
    /// Also runs nawk with the same program file and asserts matching output.
    /// </summary>
    private (string Output, int ExitCode) RunAwkWithProgramFile(string program, string input)
    {
        var progFile = Path.Combine(_tempDir, $"prog_{Guid.NewGuid():N}.awk");
        File.WriteAllText(progFile, program);

        try
        {
            return RunAwk(["-f", progFile], input);
        }
        finally
        {
            if (File.Exists(progFile)) File.Delete(progFile);
        }
    }

    // =========================================================================
    // 1. Basic Patterns & Actions (15+ tests)
    // =========================================================================

    #region Basic Patterns & Actions

    [Test]
    public void BasicPrint_PrintsAllLines()
    {
        var (output, exitCode) = RunAwk(["{ print }"], "foo\nbar\nbaz\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("foo\nbar\nbaz\n"));
    }

    [Test]
    public void BasicPrint_Print0_PrintsAllLines()
    {
        var (output, exitCode) = RunAwk(["{ print $0 }"], "hello\nworld\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("hello\nworld\n"));
    }

    [Test]
    public void PatternMatch_RegexFilter()
    {
        var (output, exitCode) = RunAwk(["/foo/ { print }"], "foo\nbar\nfoobar\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("foo\nfoobar\n"));
    }

    [Test]
    public void NegatedPattern_PrintsNonMatching()
    {
        var (output, exitCode) = RunAwk(["!/foo/ { print }"], "foo\nbar\nbaz\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("bar\nbaz\n"));
    }

    [Test]
    public void BeginBlock_RunsBeforeInput()
    {
        var (output, exitCode) = RunAwk(["BEGIN { print \"header\" }"], "");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("header\n"));
    }

    [Test]
    public void EndBlock_RunsAfterInput()
    {
        var (output, exitCode) = RunAwk(["END { print \"done\" }"], "a\nb\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("done\n"));
    }

    [Test]
    public void BeginAndEnd_BothRun()
    {
        var (output, exitCode) = RunAwk(
            ["BEGIN { print \"start\" } { print } END { print \"end\" }"],
            "middle\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("start\nmiddle\nend\n"));
    }

    [Test]
    public void MultipleRules_AllApply()
    {
        var (output, exitCode) = RunAwk(
            ["/hello/ { print \"matched\" } { print \"always\" }"],
            "hello\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("matched\nalways\n"));
    }

    [Test]
    public void ComparisonPattern_NumericGreaterThan()
    {
        var (output, exitCode) = RunAwk(["$0 > 7 { print }"], "10\n5\n20\n1\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("10\n20\n"));
    }

    [Test]
    public void RangePattern_NRRange()
    {
        var (output, exitCode) = RunAwk(
            ["NR>=2 && NR<=4 { print }"],
            "one\ntwo\nthree\nfour\nfive\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("two\nthree\nfour\n"));
    }

    [Test]
    public void DefaultAction_PrintIsImplied()
    {
        // When no action is given, the default action is { print }
        var (output, exitCode) = RunAwk(["/foo/"], "foo\nbar\nfoo2\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("foo\nfoo2\n"));
    }

    [Test]
    public void EmptyInput_NoOutput()
    {
        var (output, exitCode) = RunAwk(["{ print }"], "");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo(""));
    }

    [Test]
    public void BeginOnly_NoInputNeeded()
    {
        var (output, exitCode) = RunAwk(
            ["BEGIN { for (i=1; i<=3; i++) printf \"%d \", i; print \"\" }"],
            "");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("1 2 3 \n"));
    }

    [Test]
    public void MultiplePatterns_MatchSeparately()
    {
        var (output, exitCode) = RunAwk(
            ["/alpha/ { print \"A\" } /beta/ { print \"B\" }"],
            "alpha\nbeta\nalpha beta\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("A\nB\nA\nB\n"));
    }

    [Test]
    public void TernaryOperator_ConditionalOutput()
    {
        var (output, exitCode) = RunAwk(
            ["{ print ($0 > 5 ? \"big\" : \"small\") }"],
            "5\n10\n3\n8\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("small\nbig\nsmall\nbig\n"));
    }

    [Test]
    public void ProgramFile_FFlag()
    {
        var (output, exitCode) = RunAwkWithProgramFile(
            "{ print NR, $0 }\n",
            "alpha\nbeta\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("1 alpha\n2 beta\n"));
    }

    #endregion

    // =========================================================================
    // 2. Built-in Variables (15+ tests)
    // =========================================================================

    #region Built-in Variables

    [Test]
    public void NR_RecordNumber()
    {
        var (output, exitCode) = RunAwk(["{ print NR }"], "a\nb\nc\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("1\n2\n3\n"));
    }

    [Test]
    public void NF_NumberOfFields()
    {
        var (output, exitCode) = RunAwk(["{ print NF }"], "a b c\nd e f\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("3\n3\n"));
    }

    [Test]
    public void NF_VariesPerLine()
    {
        var (output, exitCode) = RunAwk(["{ print NF }"], "one\ntwo three\na b c d\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("1\n2\n4\n"));
    }

    [Test]
    public void FS_CustomFieldSeparator()
    {
        var (output, exitCode) = RunAwk(["-F:", "{ print $2 }"], "a:b:c\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("b\n"));
    }

    [Test]
    public void FS_CommaDelimited()
    {
        var (output, exitCode) = RunAwk(["-F,", "{ print $1, $3 }"], "one,two,three\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("one three\n"));
    }

    [Test]
    public void FS_RegexFieldSeparator()
    {
        var (output, exitCode) = RunAwk(["-F", "[0-9]", "{ for(i=1;i<=NF;i++) printf \"[%s]\", $i; print \"\" }"], "a1b2c3\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("[a][b][c][]\n"));
    }

    [Test]
    public void OFS_OutputFieldSeparator()
    {
        var (output, exitCode) = RunAwk(
            ["BEGIN { OFS=\"-\" } { print $1, $2 }"],
            "hello world\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("hello-world\n"));
    }

    [Test]
    public void ORS_OutputRecordSeparator()
    {
        var (output, exitCode) = RunAwk(
            ["BEGIN { OFS=\"-\"; ORS=\";\" } { print NR, $0 }"],
            "1\n2\n3\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("1-1;2-2;3-3;"));
    }

    [Test]
    public void DollarNF_LastField()
    {
        var (output, exitCode) = RunAwk(["{ print $NF }"], "1 2 3\n4 5 6\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("3\n6\n"));
    }

    [Test]
    public void Dollar1_FirstField()
    {
        var (output, exitCode) = RunAwk(["{ print $1 }"], "alpha beta gamma\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("alpha\n"));
    }

    [Test]
    public void Dollar0_WholeLine()
    {
        var (output, exitCode) = RunAwk(["{ print $0 }"], "full line here\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("full line here\n"));
    }

    [Test]
    public void EmptyLine_NFIsZero()
    {
        var (output, exitCode) = RunAwk(["{ print NF }"], "\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("0\n"));
    }

    [Test]
    public void RS_CustomRecordSeparator()
    {
        var (output, exitCode) = RunAwk(
            ["BEGIN { RS=\"|\" } { print NR, $0 }"],
            "a|b|c");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("1 a\n2 b\n3 c\n"));
    }

    [Test]
    public void LeadingWhitespace_StrippedFromFields()
    {
        var (output, exitCode) = RunAwk(["{ print NF, $1 }"], "  hello  \n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("1 hello\n"));
    }

    [Test]
    public void NR_InEndBlock()
    {
        var (output, exitCode) = RunAwk(["END { print NR }"], "a\nb\nc\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("3\n"));
    }

    [Test]
    public void EmptyInput_NRIsZeroInEnd()
    {
        var (output, exitCode) = RunAwk(["END { print NR }"], "");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("0\n"));
    }

    [Test]
    public void FILENAME_FromFile()
    {
        var (output, exitCode) = RunAwkOnFiles(
            ["{ print FILENAME; exit }"],
            "line1\n");
        Assert.That(exitCode, Is.EqualTo(0));
        // Output should contain the temp file path, just verify it's non-empty
        Assert.That(output.Trim(), Does.Contain("awk_input_"));
    }

    [Test]
    public void FS_EmptyFieldsWithColon()
    {
        var (output, exitCode) = RunAwk(
            ["-F:", "{ print NF; for(i=1;i<=NF;i++) printf \"[%s]\", $i; print \"\" }"],
            "a:b::d\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("4\n[a][b][][d]\n"));
    }

    #endregion

    // =========================================================================
    // 3. Arithmetic & String Operations (15+ tests)
    // =========================================================================

    #region Arithmetic & String Operations

    [Test]
    public void Arithmetic_Addition()
    {
        var (output, exitCode) = RunAwk(["{ print $1 + $2 }"], "3 4\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("7\n"));
    }

    [Test]
    public void Arithmetic_Multiplication()
    {
        var (output, exitCode) = RunAwk(["{ print $1 * $2 }"], "6 7\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("42\n"));
    }

    [Test]
    public void Arithmetic_Modulo()
    {
        var (output, exitCode) = RunAwk(["{ print $1 % $2 }"], "17 5\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("2\n"));
    }

    [Test]
    public void Arithmetic_Accumulator()
    {
        var (output, exitCode) = RunAwk(
            ["{ sum += $1 } END { print sum }"],
            "10\n20\n30\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("60\n"));
    }

    [Test]
    public void Arithmetic_Average()
    {
        var (output, exitCode) = RunAwk(
            ["{ sum += $2 } END { printf \"avg=%.1f\\n\", sum/NR }"],
            "a 1\nb 2\nc 3\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("avg=2.0\n"));
    }

    [Test]
    public void String_Length()
    {
        var (output, exitCode) = RunAwk(["{ print length($0) }"], "hello\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("5\n"));
    }

    [Test]
    public void String_LengthNoArgs()
    {
        var (output, exitCode) = RunAwk(["{ print length }"], "hello\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("5\n"));
    }

    [Test]
    public void String_Substr()
    {
        var (output, exitCode) = RunAwk(["{ print substr($0, 2, 3) }"], "hello\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("ell\n"));
    }

    [Test]
    public void String_SubstrToEnd()
    {
        var (output, exitCode) = RunAwk(["{ print substr($0, 3) }"], "hello\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("llo\n"));
    }

    [Test]
    public void String_Index()
    {
        var (output, exitCode) = RunAwk(["{ print index($0, \"bar\") }"], "foo bar baz\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("5\n"));
    }

    [Test]
    public void String_IndexNotFound()
    {
        var (output, exitCode) = RunAwk(["{ print index($0, \"xyz\") }"], "hello\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("0\n"));
    }

    [Test]
    public void String_Split()
    {
        var (output, exitCode) = RunAwk(
            ["{ n = split($0, a, \"l\"); for (i=1;i<=n;i++) printf \"%s|\", a[i]; print \"\" }"],
            "hello\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("he||o|\n"));
    }

    [Test]
    public void String_Sub()
    {
        var (output, exitCode) = RunAwk(["{ sub(/o/, \"0\"); print }"], "foobar\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("f0obar\n"));
    }

    [Test]
    public void String_Gsub()
    {
        var (output, exitCode) = RunAwk(["{ gsub(/b/, \"B\"); print }"], "abc\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("aBc\n"));
    }

    [Test]
    public void String_GsubMultipleOccurrences()
    {
        var (output, exitCode) = RunAwk(["{ gsub(/o/, \"0\"); print }"], "foo boo\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("f00 b00\n"));
    }

    [Test]
    public void String_Toupper()
    {
        var (output, exitCode) = RunAwk(["{ print toupper($0) }"], "hello\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("HELLO\n"));
    }

    [Test]
    public void String_Tolower()
    {
        var (output, exitCode) = RunAwk(["{ print tolower($0) }"], "HELLO world\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("hello world\n"));
    }

    [Test]
    public void String_Sprintf()
    {
        var (output, exitCode) = RunAwk(
            ["{ s = sprintf(\"value=%s len=%d\", $0, length($0)); print s }"],
            "test\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("value=test len=4\n"));
    }

    [Test]
    public void Regex_TildeMatch()
    {
        var (output, exitCode) = RunAwk(
            ["$0 ~ /[0-9]/ { print }"],
            "hello123\nworld\nabc456\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("hello123\nabc456\n"));
    }

    [Test]
    public void Regex_NotTildeMatch()
    {
        var (output, exitCode) = RunAwk(
            ["$0 !~ /[0-9]/ { print }"],
            "hello123\nworld\nabc456\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("world\n"));
    }

    [Test]
    public void String_Match()
    {
        var (output, exitCode) = RunAwk(
            ["{ match($0, /[0-9]+/); print RSTART, RLENGTH }"],
            "abc123def\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("4 3\n"));
    }

    [Test]
    public void String_MatchNoMatch()
    {
        var (output, exitCode) = RunAwk(
            ["{ match($0, /[0-9]+/); print RSTART, RLENGTH }"],
            "abcdef\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("0 -1\n"));
    }

    [Test]
    public void String_Concatenation()
    {
        var (output, exitCode) = RunAwk(["{ print $1 $2 }"], "hello world\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("helloworld\n"));
    }

    [Test]
    public void String_ConcatenationWithSeparator()
    {
        var (output, exitCode) = RunAwk(["{ print $1, $2 }"], "hello world\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("hello world\n"));
    }

    #endregion

    // =========================================================================
    // 4. Control Flow (10+ tests)
    // =========================================================================

    #region Control Flow

    [Test]
    public void IfElse_BasicConditional()
    {
        var (output, exitCode) = RunAwk(
            ["{ if ($0 > 5) print \"big\"; else print \"small\" }"],
            "3\n10\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("small\nbig\n"));
    }

    [Test]
    public void WhileLoop_Counting()
    {
        var (output, exitCode) = RunAwk(
            ["BEGIN { i=1; while (i<=5) { printf \"%d \", i; i++ }; print \"\" }"],
            "");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("1 2 3 4 5 \n"));
    }

    [Test]
    public void ForLoop_CStyleLoop()
    {
        var (output, exitCode) = RunAwk(
            ["BEGIN { for (i=1; i<=3; i++) printf \"%d \", i; print \"\" }"],
            "");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("1 2 3 \n"));
    }

    [Test]
    public void DoWhile_Loop()
    {
        var (output, exitCode) = RunAwk(
            ["{ i=0; do { i++; printf \"%d \", i } while (i<3); print \"\" }"],
            "test\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("1 2 3 \n"));
    }

    [Test]
    public void Next_SkipsCurrentRecord()
    {
        var (output, exitCode) = RunAwk(
            ["/two/ { next } { print }"],
            "one\ntwo\nthree\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("one\nthree\n"));
    }

    [Test]
    public void Exit_StopsProcessing()
    {
        var (output, exitCode) = RunAwk(
            ["{ if (NR==2) exit } { print }"],
            "a\nb\nc\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("a\n"));
    }

    [Test]
    public void Exit_WithCode()
    {
        var (output, exitCode) = RunAwk(
            ["BEGIN { exit 42 }"],
            "");
        Assert.That(exitCode, Is.EqualTo(42));
    }

    [Test]
    public void Break_ExitsLoop()
    {
        var (output, exitCode) = RunAwk(
            ["{ for(i=1;i<=5;i++) { if(i==3) break; printf \"%d \",i }; print \"\" }"],
            "test\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("1 2 \n"));
    }

    [Test]
    public void Continue_SkipsIteration()
    {
        var (output, exitCode) = RunAwk(
            ["{ for(i=1;i<=5;i++) { if(i==3) continue; printf \"%d \",i }; print \"\" }"],
            "test\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("1 2 4 5 \n"));
    }

    [Test]
    public void BreakAndContinue_Combined()
    {
        var (output, exitCode) = RunAwk(
            ["{ for(i=1;i<=5;i++) { if(i==3) continue; if(i==5) break; printf \"%d \",i }; print \"\" }"],
            "test\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("1 2 4 \n"));
    }

    [Test]
    public void NestedIf_MultipleConditions()
    {
        var (output, exitCode) = RunAwk(
            ["{ if ($0 < 3) print \"low\"; else if ($0 < 7) print \"mid\"; else print \"high\" }"],
            "1\n5\n9\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("low\nmid\nhigh\n"));
    }

    [Test]
    public void NR_ModuloFilter()
    {
        var (output, exitCode) = RunAwk(
            ["NR % 2 == 0 { print }"],
            "1\n2\n3\n4\n5\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("2\n4\n"));
    }

    #endregion

    // =========================================================================
    // 5. Arrays (10+ tests)
    // =========================================================================

    #region Arrays

    [Test]
    public void Array_BasicAssociative()
    {
        var (output, exitCode) = RunAwk(
            ["{ a[$1] = $2 } END { print a[\"x\"], a[\"y\"] }"],
            "x 10\ny 20\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("10 20\n"));
    }

    [Test]
    public void Array_Accumulate()
    {
        var (output, exitCode) = RunAwk(
            ["{ sum[$1] += $2 } END { print sum[\"a\"] }"],
            "a 1\nb 2\na 3\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("4\n"));
    }

    [Test]
    public void Array_InOperator()
    {
        var (output, exitCode) = RunAwk(
            ["{ a[$1]=$2 } END { print (\"key1\" in a), (\"key9\" in a) }"],
            "key1 val1\nkey2 val2\nkey1 val3\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("1 0\n"));
    }

    [Test]
    public void Array_Delete()
    {
        var (output, exitCode) = RunAwk(
            ["BEGIN { a[1]=\"x\"; a[2]=\"y\"; delete a[1]; print (1 in a), (2 in a) }"],
            "");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("0 1\n"));
    }

    [Test]
    public void Array_ForIn()
    {
        // For-in order is implementation-defined — compare sorted output
        var args = new[] { "BEGIN { a[\"c\"]=3; a[\"a\"]=1; a[\"b\"]=2; for (k in a) printf \"%s=%s\\n\", k, a[k] }" };
        var awkResult = RunAwkProcess(args, "");
        var nawkResult = RunNawkProcess(args, "");
        Assert.That(nawkResult.ExitCode, Is.EqualTo(awkResult.ExitCode), "exit codes should match");
        // Sort lines before comparing since for-in order is implementation-defined
        var awkLines = awkResult.Output.Split('\n').OrderBy(x => x).ToArray();
        var nawkLines = nawkResult.Output.Split('\n').OrderBy(x => x).ToArray();
        Assert.That(nawkLines, Is.EqualTo(awkLines), "sorted output should match");
    }

    [Test]
    public void Array_StoreLinesAndReverse()
    {
        var (output, exitCode) = RunAwk(
            ["{ lines[NR]=$0 } END { for (i=NR; i>=1; i--) print lines[i] }"],
            "a\nb\nc\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("c\nb\na\n"));
    }

    [Test]
    public void Array_Count()
    {
        var (output, exitCode) = RunAwk(
            ["{ count[$1]++ } END { print count[\"a\"], count[\"b\"] }"],
            "a\nb\na\na\nb\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("3 2\n"));
    }

    [Test]
    public void Array_NumericKeys()
    {
        var (output, exitCode) = RunAwk(
            ["BEGIN { for(i=1;i<=3;i++) a[i]=i*10; print a[1], a[2], a[3] }"],
            "");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("10 20 30\n"));
    }

    [Test]
    public void Array_LengthOfArray()
    {
        // mawk supports length(array) to get number of elements
        var (output, exitCode) = RunAwk(
            ["BEGIN { a[1]=1; a[2]=2; a[3]=3; print length(a) }"],
            "");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("3\n"));
    }

    [Test]
    public void Array_DeleteAll()
    {
        var (output, exitCode) = RunAwk(
            ["BEGIN { a[1]=1; a[2]=2; delete a; print length(a) }"],
            "");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("0\n"));
    }

    [Test]
    public void Array_MultiDimSubscript()
    {
        var (output, exitCode) = RunAwk(
            ["BEGIN { a[1,2]=\"val\"; print a[1,2] }"],
            "");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("val\n"));
    }

    #endregion

    // =========================================================================
    // 6. I/O: print, printf, formatting (5+ tests)
    // =========================================================================

    #region I/O

    [Test]
    public void Printf_FloatFormatting()
    {
        var (output, exitCode) = RunAwk(["{ printf \"%.2f\\n\", $0 }"], "3.14159\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("3.14\n"));
    }

    [Test]
    public void Printf_ZeroPaddedInt()
    {
        var (output, exitCode) = RunAwk(["{ printf \"%05d\\n\", $0 }"], "42\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("00042\n"));
    }

    [Test]
    public void Printf_LeftJustified()
    {
        var (output, exitCode) = RunAwk(["{ printf \"%-10s|\\n\", $0 }"], "hello\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("hello     |\n"));
    }

    [Test]
    public void Printf_CharConversion()
    {
        var (output, exitCode) = RunAwk(["{ printf \"%c\\n\", $0 }"], "65\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("A\n"));
    }

    [Test]
    public void Printf_HexFormat()
    {
        var (output, exitCode) = RunAwk(["{ printf \"%x\\n\", $0 }"], "255\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("ff\n"));
    }

    [Test]
    public void Printf_OctalFormat()
    {
        var (output, exitCode) = RunAwk(["{ printf \"%o\\n\", $0 }"], "255\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("377\n"));
    }

    [Test]
    public void Printf_MultipleArgs()
    {
        var (output, exitCode) = RunAwk(
            ["{ printf \"%s has %d chars\\n\", $0, length($0) }"],
            "hello world\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("hello world has 11 chars\n"));
    }

    [Test]
    public void Getline_ReadsNextLine()
    {
        var (output, exitCode) = RunAwk(
            ["/abc/ { getline; print }"],
            "abc\ndef\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("def\n"));
    }

    #endregion

    // =========================================================================
    // 7. Field Manipulation (5+ tests)
    // =========================================================================

    #region Field Manipulation

    [Test]
    public void FieldAssign_ModifyField()
    {
        var (output, exitCode) = RunAwk(["{ $2 = \"X\"; print }"], "a b c d\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("a X c d\n"));
    }

    [Test]
    public void FieldAssign_FirstField()
    {
        var (output, exitCode) = RunAwk(["{ $1 = \"NEW\"; print }"], "old rest\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("NEW rest\n"));
    }

    [Test]
    public void FieldAssign_WithOFS()
    {
        var (output, exitCode) = RunAwk(
            ["BEGIN { OFS=\",\" } { $2 = \"X\"; print }"],
            "a b c\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("a,X,c\n"));
    }

    [Test]
    public void NF_TruncateFields()
    {
        var (output, exitCode) = RunAwk(["{ NF=2; print }"], "aaa bbb ccc\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("aaa bbb\n"));
    }

    [Test]
    public void NF_ExpandFields()
    {
        var (output, exitCode) = RunAwk(
            ["BEGIN { OFS=\",\" } { NF=4; print }"],
            "a b\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("a,b,,\n"));
    }

    [Test]
    public void Field_CustomFS_Colon()
    {
        var (output, exitCode) = RunAwk(
            ["-F:", "{ $2 = \"REPLACED\"; print }"],
            "a:b:c\n");
        Assert.That(exitCode, Is.EqualTo(0));
        // After field assignment, OFS (default space) is used for output
        Assert.That(output, Is.EqualTo("a REPLACED c\n"));
    }

    [Test]
    public void Field_SumAllFields()
    {
        var (output, exitCode) = RunAwk(
            ["{ for(i=1;i<=NF;i++) sum+=$i } END { print sum }"],
            "1 2 3\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("6\n"));
    }

    #endregion

    // =========================================================================
    // 8. Edge Cases (5+ tests)
    // =========================================================================

    #region Edge Cases

    [Test]
    public void EdgeCase_SingleField()
    {
        var (output, exitCode) = RunAwk(["{ print NF, $1 }"], "hello\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("1 hello\n"));
    }

    [Test]
    public void EdgeCase_NumericString()
    {
        var (output, exitCode) = RunAwk(["{ print $0 + 0 }"], "10\n2\n30\n4\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("10\n2\n30\n4\n"));
    }

    [Test]
    public void EdgeCase_UninitializedVariable()
    {
        var (output, exitCode) = RunAwk(["{ print x + 0 }"], "test\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("0\n"));
    }

    [Test]
    public void EdgeCase_UninitializedStringContext()
    {
        var (output, exitCode) = RunAwk(["{ print x \"\" }"], "test\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("\n"));
    }

    [Test]
    public void EdgeCase_StringConcatInVariable()
    {
        var (output, exitCode) = RunAwk(
            ["{ s = \"hello\" \" \" \"world\"; print s }"],
            "test\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("hello world\n"));
    }

    [Test]
    public void EdgeCase_LongLine()
    {
        // 1000 character line
        var longLine = new string('x', 1000) + "\n";
        var (output, exitCode) = RunAwk(["{ print length($0) }"], longLine);
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("1000\n"));
    }

    [Test]
    public void EdgeCase_ManyFields()
    {
        var manyFields = string.Join(" ", Enumerable.Range(1, 100)) + "\n";
        var (output, exitCode) = RunAwk(["{ print NF, $100 }"], manyFields);
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("100 100\n"));
    }

    [Test]
    public void EdgeCase_SubWithAmpersand()
    {
        // & in replacement refers to the matched text
        var (output, exitCode) = RunAwk(
            ["{ sub(/hello/, \"[&]\"); print }"],
            "hello world\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("[hello] world\n"));
    }

    [Test]
    public void EdgeCase_GsubReturnValue()
    {
        // gsub returns the number of replacements made
        var (output, exitCode) = RunAwk(
            ["{ n = gsub(/o/, \"0\"); print n, $0 }"],
            "foo boo\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("4 f00 b00\n"));
    }

    [Test]
    public void EdgeCase_ExitInEndBlock()
    {
        var (output, exitCode) = RunAwk(
            ["END { print \"end\"; exit 7 }"],
            "a\n");
        Assert.That(exitCode, Is.EqualTo(7));
        Assert.That(output, Is.EqualTo("end\n"));
    }

    #endregion
}
