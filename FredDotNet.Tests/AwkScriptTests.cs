using NUnit.Framework;
using FredDotNet;

namespace FredDotNet.Tests;

/// <summary>
/// Tests for AwkScript compile-once API and FredPipeline builder.
/// </summary>
[TestFixture]
public class AwkScriptTests
{
    #region AwkScript Compile-Once

    [Test]
    public void Compile_ValidProgram_ReturnsAwkScript()
    {
        var script = AwkEngine.Compile("{ print $0 }");
        Assert.That(script, Is.Not.Null);
    }

    [Test]
    public void Compile_InvalidProgram_Throws()
    {
        Assert.Throws<AwkException>(() => AwkEngine.Compile("{ print $"));
    }

    [Test]
    public void AwkScript_ExecuteString_BasicPrint()
    {
        var script = AwkEngine.Compile("{ print $1 }");
        var (output, exitCode) = script.Execute("hello world");
        Assert.That(output.TrimEnd(), Is.EqualTo("hello"));
        Assert.That(exitCode, Is.EqualTo(0));
    }

    [Test]
    public void AwkScript_ExecuteString_WithFieldSeparator()
    {
        var script = AwkEngine.Compile("{ print $2 }");
        var (output, _) = script.Execute("a:b:c", fieldSeparator: ":");
        Assert.That(output.TrimEnd(), Is.EqualTo("b"));
    }

    [Test]
    public void AwkScript_ExecuteString_WithVariables()
    {
        var script = AwkEngine.Compile("{ print x }");
        var vars = new Dictionary<string, string> { ["x"] = "42" };
        var (output, _) = script.Execute("anything", variables: vars);
        Assert.That(output.TrimEnd(), Is.EqualTo("42"));
    }

    [Test]
    public void AwkScript_ReuseMultipleTimes_IndependentResults()
    {
        var script = AwkEngine.Compile("{ sum += $1 } END { print sum }");
        var (output1, _) = script.Execute("10\n20\n30");
        var (output2, _) = script.Execute("1\n2\n3");
        Assert.That(output1.TrimEnd(), Is.EqualTo("60"));
        Assert.That(output2.TrimEnd(), Is.EqualTo("6"));
    }

    [Test]
    public void AwkScript_ExecuteTextReaderWriter()
    {
        var script = AwkEngine.Compile("{ print NR, $0 }");
        var reader = new StringReader("alpha\nbeta");
        var writer = new StringWriter();
        int exitCode = script.Execute(reader, writer);
        Assert.That(exitCode, Is.EqualTo(0));
        var lines = writer.ToString().TrimEnd().Split('\n');
        Assert.That(lines[0], Is.EqualTo("1 alpha"));
        Assert.That(lines[1], Is.EqualTo("2 beta"));
    }

    [Test]
    public void AwkScript_ExecuteFiles_ReadsFromFiles()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmpFile, "line1\nline2\n");
            var script = AwkEngine.Compile("{ print $0 }");
            var (output, exitCode) = script.Execute(new[] { tmpFile });
            Assert.That(output, Does.Contain("line1"));
            Assert.That(output, Does.Contain("line2"));
            Assert.That(exitCode, Is.EqualTo(0));
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    [Test]
    public void AwkEngine_Execute_DelegatesToCompile()
    {
        // Verify that the static Execute still works (delegates to Compile internally)
        var (output, exitCode) = AwkEngine.Execute("{ print $1 }", "hello world");
        Assert.That(output.TrimEnd(), Is.EqualTo("hello"));
        Assert.That(exitCode, Is.EqualTo(0));
    }

    #endregion

    #region FredPipeline

    [Test]
    public void Pipeline_Empty_PassesThrough()
    {
        string result = FredPipeline.Create().Execute("hello");
        Assert.That(result, Is.EqualTo("hello"));
    }

    [Test]
    public void Pipeline_SingleSedStage_String()
    {
        string result = FredPipeline.Create()
            .Sed("s/hello/world/")
            .Execute("hello there");
        Assert.That(result, Is.EqualTo("world there"));
    }

    [Test]
    public void Pipeline_SingleSedStage_Compiled()
    {
        var sedScript = SedParser.Parse("s/foo/bar/g");
        string result = FredPipeline.Create()
            .Sed(sedScript)
            .Execute("foo and foo");
        Assert.That(result, Is.EqualTo("bar and bar"));
    }

    [Test]
    public void Pipeline_SingleAwkStage_String()
    {
        string result = FredPipeline.Create()
            .Awk("{ print $1 }")
            .Execute("hello world");
        Assert.That(result.TrimEnd(), Is.EqualTo("hello"));
    }

    [Test]
    public void Pipeline_SingleAwkStage_Compiled()
    {
        var awkScript = AwkEngine.Compile("{ print NR }");
        string result = FredPipeline.Create()
            .Awk(awkScript)
            .Execute("a\nb\nc");
        Assert.That(result.TrimEnd(), Is.EqualTo("1\n2\n3"));
    }

    [Test]
    public void Pipeline_SedThenAwk_Chained()
    {
        // sed replaces, then awk extracts field
        string result = FredPipeline.Create()
            .Sed("s/,/ /g")
            .Awk("{ print $2 }")
            .Execute("alice,bob,charlie");
        Assert.That(result.TrimEnd(), Is.EqualTo("bob"));
    }

    [Test]
    public void Pipeline_MultipleSedStages()
    {
        string result = FredPipeline.Create()
            .Sed("s/a/b/g")
            .Sed("s/b/c/g")
            .Execute("aaa");
        Assert.That(result, Is.EqualTo("ccc"));
    }

    [Test]
    public void Pipeline_AwkThenSed()
    {
        // awk adds prefix, sed replaces it
        string result = FredPipeline.Create()
            .Awk("{ print \"PREFIX:\" $0 }")
            .Sed("s/PREFIX:/DONE:/")
            .Execute("test");
        Assert.That(result.TrimEnd(), Is.EqualTo("DONE:test"));
    }

    [Test]
    public void Pipeline_TextReaderWriter()
    {
        var reader = new StringReader("hello world");
        var writer = new StringWriter();
        FredPipeline.Create()
            .Sed("s/hello/goodbye/")
            .Execute(reader, writer);
        Assert.That(writer.ToString(), Is.EqualTo("goodbye world"));
    }

    [Test]
    public void Pipeline_CustomStage()
    {
        var pipeline = FredPipeline.Create()
            .Stage(new UpperCaseStage())
            .Execute("hello");
        Assert.That(pipeline, Is.EqualTo("HELLO"));
    }

    [Test]
    public void Pipeline_ThreeStages_SedAwkSed()
    {
        string result = FredPipeline.Create()
            .Sed("s/:/\\n/g")
            .Awk("{ print NR \": \" $0 }")
            .Sed("s/: /= /")
            .Execute("a:b:c");
        var lines = result.TrimEnd().Split('\n');
        Assert.That(lines.Length, Is.EqualTo(3));
        Assert.That(lines[0], Is.EqualTo("1= a"));
        Assert.That(lines[1], Is.EqualTo("2= b"));
        Assert.That(lines[2], Is.EqualTo("3= c"));
    }

    [Test]
    public void Pipeline_AwkWithFieldSeparator()
    {
        string result = FredPipeline.Create()
            .Awk("{ print $2 }", fieldSeparator: ",")
            .Execute("one,two,three");
        Assert.That(result.TrimEnd(), Is.EqualTo("two"));
    }

    #endregion

    #region Pipeline Grep Tests

    [Test]
    public void Pipeline_GrepFiltersLines()
    {
        string result = FredPipeline.Create()
            .Grep("error", ignoreCase: true)
            .Execute("INFO: ok\nERROR: fail\nWARN: meh\nerror: also bad\n");
        Assert.That(result, Is.EqualTo("ERROR: fail\nerror: also bad\n"));
    }

    [Test]
    public void Pipeline_SedGrepAwk_FullChain()
    {
        // sed: remove leading whitespace
        // grep: filter to lines containing "ERR"
        // awk: extract first field
        string input = "  ERR something\n  OK fine\n  ERR another\n";
        string result = FredPipeline.Create()
            .Sed("s/^[ ]*//")
            .Grep("ERR")
            .Awk("{ print $2 }")
            .Execute(input);
        var lines = result.TrimEnd().Split('\n');
        Assert.That(lines.Length, Is.EqualTo(2));
        Assert.That(lines[0], Is.EqualTo("something"));
        Assert.That(lines[1], Is.EqualTo("another"));
    }

    [Test]
    public void Pipeline_GrepInvertMatch()
    {
        string result = FredPipeline.Create()
            .Grep("^#", invertMatch: true)
            .Execute("# comment\ncode\n# another\nmore code\n");
        Assert.That(result, Is.EqualTo("code\nmore code\n"));
    }

    [Test]
    public void Pipeline_GrepWithPrecompiledScript()
    {
        var script = GrepEngine.Compile("hello", ignoreCase: true);
        string result = FredPipeline.Create()
            .Grep(script)
            .Execute("Hello world\ngoodbye\nHELLO\n");
        Assert.That(result, Is.EqualTo("Hello world\nHELLO\n"));
    }

    [Test]
    public void Pipeline_GrepWithOptions()
    {
        var opts = new GrepOptions { InvertMatch = true, UseERE = true };
        opts.Patterns.Add("^\\s*$");
        string result = FredPipeline.Create()
            .Grep(opts)
            .Execute("hello\n\nworld\n \n!\n");
        Assert.That(result, Is.EqualTo("hello\nworld\n!\n"));
    }

    #endregion

    #region GrepEngine Convenience API Tests

    [Test]
    public void GrepEngine_CompileWithPattern_BasicMatch()
    {
        var script = GrepEngine.Compile("hello");
        var (output, exitCode) = script.Execute("hello world\ngoodbye\nhello again\n");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Is.EqualTo("hello world\nhello again\n"));
    }

    [Test]
    public void GrepEngine_CompileWithPattern_IgnoreCase()
    {
        var script = GrepEngine.Compile("hello", ignoreCase: true);
        var (output, _) = script.Execute("Hello\nHELLO\nworld\n");
        Assert.That(output, Is.EqualTo("Hello\nHELLO\n"));
    }

    [Test]
    public void GrepEngine_CompileWithPattern_InvertMatch()
    {
        var script = GrepEngine.Compile("^#", invertMatch: true);
        var (output, _) = script.Execute("# comment\ncode\n# x\nmore\n");
        Assert.That(output, Is.EqualTo("code\nmore\n"));
    }

    [Test]
    public void GrepEngine_CompileWithPattern_WholeWord()
    {
        var script = GrepEngine.Compile("cat", wholeWord: true);
        var (output, _) = script.Execute("cat\ncatch\nthe cat sat\n");
        Assert.That(output, Is.EqualTo("cat\nthe cat sat\n"));
    }

    [Test]
    public void GrepEngine_CompileWithPattern_ERE()
    {
        var script = GrepEngine.Compile("foo|bar", useERE: true);
        var (output, _) = script.Execute("foo\nbaz\nbar\nqux\n");
        Assert.That(output, Is.EqualTo("foo\nbar\n"));
    }

    [Test]
    public void GrepEngine_CompileWithPattern_FixedStrings()
    {
        var script = GrepEngine.Compile("a.b", fixedStrings: true);
        var (output, _) = script.Execute("a.b\naxb\na.b.c\n");
        Assert.That(output, Is.EqualTo("a.b\na.b.c\n"));
    }

    [Test]
    public void GrepEngine_CompileReuse()
    {
        var script = GrepEngine.Compile("error", ignoreCase: true);
        var (out1, _) = script.Execute("ERROR here\nok\n");
        var (out2, _) = script.Execute("fine\nerror there\n");
        Assert.That(out1, Is.EqualTo("ERROR here\n"));
        Assert.That(out2, Is.EqualTo("error there\n"));
    }

    #endregion
}

/// <summary>
/// Test helper: custom pipeline stage that uppercases all input.
/// </summary>
internal sealed class UpperCaseStage : IPipelineStage
{
    public int Execute(TextReader input, TextWriter output)
    {
        output.Write(input.ReadToEnd().ToUpperInvariant());
        return 0;
    }
}
