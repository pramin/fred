using NUnit.Framework;
using FredDotNet;
using System.IO;

namespace FredDotNet.Tests;

/// <summary>
/// Tests for File I/O commands: r, R, w, W, and the w flag on s commands.
/// </summary>
[TestFixture]
[NonParallelizable]
public class FileIOTests
{
    // Temp file paths registered for cleanup in TearDown.
    private readonly List<string> _tempFiles = new();

    [TearDown]
    public void TearDown()
    {
        foreach (var path in _tempFiles)
        {
            try { File.Delete(path); } catch { /* best-effort */ }
        }
        _tempFiles.Clear();
    }

    /// <summary>Create a temp file, register it for cleanup, and return its path.</summary>
    private string TempFile(string? content = null)
    {
        var path = Path.GetTempFileName();
        _tempFiles.Add(path);
        if (content != null)
            File.WriteAllText(path, content);
        return path;
    }

    // ─────────────────────────────── r command ───────────────────────────────

    [Test]
    public void R_Command_ReadsFileAndAppendsAfterLine()
    {
        var readFile = TempFile("appended line\n");
        var script = SedParser.Parse($"r {readFile}");
        var result = script.Transform("hello\nworld");
        // Each line triggers a file read appended after it
        Assert.That(result, Is.EqualTo("hello\nappended line\nworld\nappended line"));
    }

    [Test]
    public void R_Command_NonexistentFile_SilentlySkipped()
    {
        var script = SedParser.Parse("r /nonexistent/path/that/does/not/exist.txt");
        var result = script.Transform("hello\nworld");
        Assert.That(result, Is.EqualTo("hello\nworld"));
    }

    [Test]
    public void R_Command_WithAddress_OnlyAppendsOnMatchingLines()
    {
        var readFile = TempFile("INSERTED\n");
        var script = SedParser.Parse($"2r {readFile}");
        var result = script.Transform("line1\nline2\nline3");
        Assert.That(result, Is.EqualTo("line1\nline2\nINSERTED\nline3"));
    }

    [Test]
    public void R_Command_FileWithNoTrailingNewline_AppendsCorrectly()
    {
        // File has no trailing newline
        var readFile = TempFile("no newline");
        var script = SedParser.Parse($"r {readFile}");
        var result = script.Transform("hello");
        Assert.That(result, Is.EqualTo("hello\nno newline"));
    }

    [Test]
    public void R_Command_EmptyFile_AppendsNothing_LineStillOutputs()
    {
        var readFile = TempFile("");
        var script = SedParser.Parse($"r {readFile}");
        var result = script.Transform("hello");
        Assert.That(result, Is.EqualTo("hello"));
    }

    // ─────────────────────────────── w command ───────────────────────────────

    [Test]
    public void W_Command_WritesPatternSpaceToFile()
    {
        var writeFile = TempFile();
        var script = SedParser.Parse($"w {writeFile}");
        script.Transform("line one\nline two");
        var written = File.ReadAllText(writeFile);
        Assert.That(written, Is.EqualTo("line one\nline two\n"));
    }

    [Test]
    public void W_Command_MultipleMatchingLines_AllAppendedToSameFile()
    {
        var writeFile = TempFile();
        var script = SedParser.Parse($"w {writeFile}");
        script.Transform("alpha\nbeta\ngamma");
        var written = File.ReadAllText(writeFile);
        Assert.That(written, Is.EqualTo("alpha\nbeta\ngamma\n"));
    }

    [Test]
    public void W_Command_WithAddress_OnlyWritesMatchingLines()
    {
        var writeFile = TempFile();
        var script = SedParser.Parse($"/beta/w {writeFile}");
        script.Transform("alpha\nbeta\ngamma");
        var written = File.ReadAllText(writeFile);
        Assert.That(written, Is.EqualTo("beta\n"));
    }

    [Test]
    public void W_Command_FileCreatedEmptyAtScriptStart_EvenIfNoLinesMatch()
    {
        var writeFile = TempFile("old content");
        var script = SedParser.Parse($"/nomatch/w {writeFile}");
        script.Transform("alpha\nbeta");
        // File should be truncated to empty at script start
        var written = File.ReadAllText(writeFile);
        Assert.That(written, Is.EqualTo(""));
    }

    [Test]
    public void W_Command_MultipleWriteCommandsSameFilename_ShareHandle()
    {
        var writeFile = TempFile();
        // Two separate w commands pointing to the same file
        var script = SedParser.Parse($"/alpha/w {writeFile}\n/gamma/w {writeFile}");
        script.Transform("alpha\nbeta\ngamma");
        var written = File.ReadAllText(writeFile);
        Assert.That(written, Is.EqualTo("alpha\ngamma\n"));
    }

    [Test]
    public void W_Command_InBlock_WritesMatchingLines()
    {
        var writeFile = TempFile();
        var script = SedParser.Parse($"/beta/{{\nw {writeFile}\n}}");
        script.Transform("alpha\nbeta\ngamma");
        var written = File.ReadAllText(writeFile);
        Assert.That(written, Is.EqualTo("beta\n"));
    }

    // ─────────────────────────────── R command ───────────────────────────────

    [Test]
    public void ReadOneLine_Command_ReadsOneLineFromFile()
    {
        var readFile = TempFile("first line\nsecond line\nthird line\n");
        var script = SedParser.Parse($"R {readFile}");
        var result = script.Transform("input");
        // R reads one line from the file, appended after the input line
        Assert.That(result, Is.EqualTo("input\nfirst line"));
    }

    [Test]
    public void ReadOneLine_Command_SequentialReads_AdvanceFilePointer()
    {
        // Two lines in input, R command reads successive lines from the file
        var readFile = TempFile("first line\nsecond line\n");
        var script = SedParser.Parse($"R {readFile}");
        var result = script.Transform("lineA\nlineB");
        Assert.That(result, Is.EqualTo("lineA\nfirst line\nlineB\nsecond line"));
    }

    [Test]
    public void ReadOneLine_Command_FileExhausted_SilentlySkipped()
    {
        // File has only one line; second R silently skips
        var readFile = TempFile("only line\n");
        var script = SedParser.Parse($"R {readFile}");
        var result = script.Transform("lineA\nlineB\nlineC");
        Assert.That(result, Is.EqualTo("lineA\nonly line\nlineB\nlineC"));
    }

    [Test]
    public void ReadOneLine_Command_NonexistentFile_SilentlySkipped()
    {
        var script = SedParser.Parse("R /nonexistent/file.txt");
        var result = script.Transform("hello");
        Assert.That(result, Is.EqualTo("hello"));
    }

    [Test]
    public void ReadOneLine_Command_InvalidPath_SilentlySkipped()
    {
        // Path containing a null byte throws ArgumentException in new StreamReader(...) on Linux;
        // the R handler must catch it and silently skip.
        var script = new SedScript(new[]
        {
            SedCommand.ReadOneLine(SedAddress.All(), "file\0name.txt")
        });
        var result = script.Transform("hello");
        Assert.That(result, Is.EqualTo("hello"));
    }

    [Test]
    public void ReadFile_Command_InvalidPath_SilentlySkipped()
    {
        // Path containing a null byte throws ArgumentException in File.ReadAllText(...) on Linux;
        // the r handler must catch it and silently skip.
        var script = new SedScript(new[]
        {
            SedCommand.ReadFile(SedAddress.All(), "file\0name.txt")
        });
        var result = script.Transform("hello");
        Assert.That(result, Is.EqualTo("hello"));
    }

    [Test]
    public void ReadOneLine_Command_WithAddress_OnlyReadsOnMatchingLine()
    {
        // R with address 2: only line 2 triggers a read; line 1 and 3 are unaffected
        var readFile = TempFile("injected\n");
        var script = SedParser.Parse($"2R {readFile}");
        var result = script.Transform("line1\nline2\nline3");
        Assert.That(result, Is.EqualTo("line1\nline2\ninjected\nline3"));
    }

    // ─────────────────────────────── W command ───────────────────────────────

    [Test]
    public void WriteFirstLine_Command_WritesFirstLineOfPatternSpace()
    {
        var writeFile = TempFile();
        // Pattern space is multiline (via N command); W writes only first line
        var script = SedParser.Parse($"N\nW {writeFile}");
        script.Transform("first\nsecond");
        var written = File.ReadAllText(writeFile);
        Assert.That(written, Is.EqualTo("first\n"));
    }

    [Test]
    public void WriteFirstLine_Command_SingleLinePatternSpace_WritesEntireLine()
    {
        var writeFile = TempFile();
        var script = SedParser.Parse($"W {writeFile}");
        script.Transform("single line");
        var written = File.ReadAllText(writeFile);
        Assert.That(written, Is.EqualTo("single line\n"));
    }

    [Test]
    public void WriteFirstLine_Command_WithAddress_OnlyWritesMatchingLines()
    {
        var writeFile = TempFile();
        // Use a pattern that uniquely matches "match this" but not "no" or "also no"
        var script = SedParser.Parse($"/match this/W {writeFile}");
        script.Transform("no\nmatch this\nalso no");
        var written = File.ReadAllText(writeFile);
        Assert.That(written, Is.EqualTo("match this\n"));
    }

    [Test]
    public void WriteFirstLine_Command_InBlock_WritesFirstLineOfMultilinePatternSpace()
    {
        // /alpha/{ N; W file } — N joins next line, then W writes only the first line
        var writeFile = TempFile();
        var script = SedParser.Parse($"/alpha/{{\nN\nW {writeFile}\n}}");
        script.Transform("alpha\nbeta\ngamma");
        var written = File.ReadAllText(writeFile);
        Assert.That(written, Is.EqualTo("alpha\n"));
    }

    // ─────────────────────────────── s///w flag ──────────────────────────────

    [Test]
    public void SubstituteWFlag_WritesToFileOnSuccessfulSubstitution()
    {
        var writeFile = TempFile();
        var script = SedParser.Parse($"s/old/new/w {writeFile}");
        script.Transform("old line\nunchanged line");
        var written = File.ReadAllText(writeFile);
        Assert.That(written, Is.EqualTo("new line\n"));
    }

    [Test]
    public void SubstituteWFlag_DoesNotWriteWhenNoSubstitutionMade()
    {
        var writeFile = TempFile();
        var script = SedParser.Parse($"s/nomatch/new/w {writeFile}");
        script.Transform("hello\nworld");
        var written = File.ReadAllText(writeFile);
        Assert.That(written, Is.EqualTo(""));
    }

    [Test]
    public void SubstituteWFlag_FileCreatedEmptyAtScriptStart()
    {
        var writeFile = TempFile("stale content");
        var script = SedParser.Parse($"s/nomatch/x/w {writeFile}");
        script.Transform("hello");
        var written = File.ReadAllText(writeFile);
        Assert.That(written, Is.EqualTo(""));
    }

    [Test]
    public void SubstituteWFlag_MultipleMatchingLines_AllWritten()
    {
        var writeFile = TempFile();
        var script = SedParser.Parse($"s/foo/bar/w {writeFile}");
        script.Transform("foo one\nno match\nfoo two");
        var written = File.ReadAllText(writeFile);
        Assert.That(written, Is.EqualTo("bar one\nbar two\n"));
    }

    [Test]
    public void SubstituteWFlag_GlobalFlag_WritesFullySubstitutedLine()
    {
        // s/foo/bar/gw file — all occurrences replaced, result written once per line
        var writeFile = TempFile();
        var script = SedParser.Parse($"s/foo/bar/gw {writeFile}");
        script.Transform("foo foo\nno match\nfoo foo foo");
        var written = File.ReadAllText(writeFile);
        Assert.That(written, Is.EqualTo("bar bar\nbar bar bar\n"));
    }

    [Test]
    public void SubstituteWFlag_CaseInsensitiveAndGlobalFlags_WritesSubstitutedLine()
    {
        // s/foo/bar/Igw file — case-insensitive + global, result written on match
        var writeFile = TempFile();
        var script = SedParser.Parse($"s/foo/bar/Igw {writeFile}");
        script.Transform("FOO Foo\nno match\nfOo");
        var written = File.ReadAllText(writeFile);
        Assert.That(written, Is.EqualTo("bar bar\nbar\n"));
    }

    [Test]
    public void SubstituteWFlag_NthOccurrence_WritesOnlyWhenNthOccurrenceMatches()
    {
        // s/foo/bar/2w file — only 2nd occurrence replaced; written only when substitution occurred
        var writeFile = TempFile();
        var script = SedParser.Parse($"s/foo/bar/2w {writeFile}");
        script.Transform("one foo\nfoo foo\nno match");
        var written = File.ReadAllText(writeFile);
        // "one foo" has only 1 occurrence — no substitution, not written
        // "foo foo" has 2nd occurrence replaced → "foo bar" written
        Assert.That(written, Is.EqualTo("foo bar\n"));
    }

    // ─────────────────────────────── r + d interaction ───────────────────────

    [Test]
    public void R_Command_PendingRead_DiscardedWhenDeleteStartsNewCycle()
    {
        // r appends a deferred read; if the same cycle then executes d, the pending read is discarded
        var readFile = TempFile("should not appear\n");
        var script = SedParser.Parse($"r {readFile}\nd");
        var result = script.Transform("line1\nline2");
        // d deletes the pattern space and starts the next cycle; pending r content is lost
        Assert.That(result, Is.EqualTo(""));
    }

    // ─────────────────────────────── SedScript reuse ─────────────────────────

    [Test]
    public void W_Command_SecondTransformCall_TruncatesFile()
    {
        // w file: second Transform() on the same SedScript must truncate the file, not append
        var writeFile = TempFile();
        var script = SedParser.Parse($"w {writeFile}");
        script.Transform("first run");
        script.Transform("second run");
        var written = File.ReadAllText(writeFile);
        Assert.That(written, Is.EqualTo("second run\n"));
    }

    [Test]
    public void ReadOneLine_Command_SecondTransformCall_RestartsFileFromBeginning()
    {
        // R file: second Transform() on the same SedScript must re-read from line 1
        var readFile = TempFile("line one\nline two\n");
        var script = SedParser.Parse($"R {readFile}");
        var first = script.Transform("input");
        var second = script.Transform("input");
        // Both calls should read "line one" (first line) because the reader resets
        Assert.That(first, Is.EqualTo("input\nline one"));
        Assert.That(second, Is.EqualTo("input\nline one"));
    }

    // ─────────────────────────────── Combined ────────────────────────────────

    [Test]
    public void R_And_W_Combined_WorkCorrectly()
    {
        var readFile = TempFile("appended\n");
        var writeFile = TempFile();
        // Write matching lines and also append file content after each line
        var script = SedParser.Parse($"/keep/w {writeFile}\nr {readFile}");
        var result = script.Transform("keep this\nskip this\nkeep also");
        var written = File.ReadAllText(writeFile);
        Assert.That(written, Is.EqualTo("keep this\nkeep also\n"));
        Assert.That(result, Is.EqualTo("keep this\nappended\nskip this\nappended\nkeep also\nappended"));
    }
}
