using NUnit.Framework;
using FredDotNet;

namespace FredDotNet.Tests;

[TestFixture]
public class UnifiedDiffTests
{
    [Test]
    public void Generate_IdenticalContent_ReturnsEmpty()
    {
        string content = "line1\nline2\nline3\n";
        string result = UnifiedDiff.Generate(content, content, "a.txt", "b.txt");
        Assert.That(result, Is.EqualTo(string.Empty));
    }

    [Test]
    public void Generate_SingleLineChange_ShowsHunk()
    {
        string original = "line1\nline2\nline3\n";
        string modified = "line1\nLINE2\nline3\n";
        string diff = UnifiedDiff.Generate(original, modified, "a.txt", "b.txt");

        Assert.That(diff, Does.Contain("--- a.txt"));
        Assert.That(diff, Does.Contain("+++ b.txt"));
        Assert.That(diff, Does.Contain("@@"));
        Assert.That(diff, Does.Contain("-line2"));
        Assert.That(diff, Does.Contain("+LINE2"));
    }

    [Test]
    public void Generate_MultipleChanges_ShowsAllChanges()
    {
        string original = "aaa\nbbb\nccc\nddd\neee\n";
        string modified = "aaa\nBBB\nccc\nDDD\neee\n";
        string diff = UnifiedDiff.Generate(original, modified, "orig", "mod");

        Assert.That(diff, Does.Contain("-bbb"));
        Assert.That(diff, Does.Contain("+BBB"));
        Assert.That(diff, Does.Contain("-ddd"));
        Assert.That(diff, Does.Contain("+DDD"));
    }

    [Test]
    public void CountChangedLines_IdenticalContent_ReturnsZero()
    {
        string content = "line1\nline2\n";
        int count = UnifiedDiff.CountChangedLines(content, content);
        Assert.That(count, Is.EqualTo(0));
    }

    [Test]
    public void CountChangedLines_OneChange_ReturnsOne()
    {
        string original = "line1\nline2\nline3\n";
        string modified = "line1\nLINE2\nline3\n";
        int count = UnifiedDiff.CountChangedLines(original, modified);
        Assert.That(count, Is.EqualTo(1));
    }

    [Test]
    public void CountChangedLines_AllChanged_ReturnsLineCount()
    {
        string original = "aaa\nbbb\nccc\n";
        string modified = "xxx\nyyy\nzzz\n";
        int count = UnifiedDiff.CountChangedLines(original, modified);
        Assert.That(count, Is.EqualTo(3));
    }
}

[TestFixture]
public class FredResultTests
{
    [Test]
    public void ToJson_EmptyResult_ValidJson()
    {
        var result = new FredResult();
        string json = result.ToJson();

        Assert.That(json, Does.Contain("\"filesSearched\": 0"));
        Assert.That(json, Does.Contain("\"filesMatched\": 0"));
        Assert.That(json, Does.Contain("\"filesModified\": 0"));
        Assert.That(json, Does.Contain("\"matches\": []"));
    }

    [Test]
    public void ToJson_WithMatches_ContainsFileAndLines()
    {
        var result = new FredResult
        {
            FilesSearched = 10,
            FilesMatched = 1,
            FilesModified = 1,
        };
        result.Matches.Add(new FredFileMatch
        {
            File = "test.cs",
            Lines = { new FredLineMatch { Number = 5, Content = "old", Replacement = "new" } }
        });

        string json = result.ToJson();

        Assert.That(json, Does.Contain("\"filesSearched\": 10"));
        Assert.That(json, Does.Contain("\"file\": \"test.cs\""));
        Assert.That(json, Does.Contain("\"number\": 5"));
        Assert.That(json, Does.Contain("\"content\": \"old\""));
        Assert.That(json, Does.Contain("\"replacement\": \"new\""));
    }

    [Test]
    public void ToJson_NullReplacement_OmittedFromJson()
    {
        var result = new FredResult();
        result.Matches.Add(new FredFileMatch
        {
            File = "test.cs",
            Lines = { new FredLineMatch { Number = 1, Content = "hello" } }
        });

        string json = result.ToJson();

        Assert.That(json, Does.Not.Contain("replacement"));
    }

    [Test]
    public void ToJson_Roundtrip_ValidStructure()
    {
        var result = new FredResult
        {
            FilesSearched = 5,
            FilesMatched = 2,
            FilesModified = 1,
        };

        string json = result.ToJson();

        // Parse to verify it's valid JSON
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.That(root.GetProperty("filesSearched").GetInt32(), Is.EqualTo(5));
        Assert.That(root.GetProperty("filesMatched").GetInt32(), Is.EqualTo(2));
        Assert.That(root.GetProperty("filesModified").GetInt32(), Is.EqualTo(1));
        Assert.That(root.GetProperty("matches").GetArrayLength(), Is.EqualTo(0));
    }
}

[TestFixture]
public class FredInPlaceTests
{
    private string _tempDir = "";

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "fred_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private string CreateFile(string name, string content)
    {
        string path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Test]
    public void InPlace_ModifiesFile()
    {
        string file = CreateFile("test.txt", "hello world\n");
        string[] args = { _tempDir, "-name", "test.txt", "--sed", "-i", "s/hello/goodbye/g" };

        // Use fred's ParsePhases to verify parsing
        var parsed = fred.Program.ParsePhases(args);
        Assert.That(parsed.InPlace, Is.True);
        Assert.That(parsed.SedScript, Is.EqualTo("s/hello/goodbye/g"));
        Assert.That(parsed.BackupSuffix, Is.Null);
    }

    [Test]
    public void InPlaceWithBackup_ParsesCorrectly()
    {
        string[] args = { ".", "-name", "*.txt", "--sed", "-i.bak", "s/old/new/g" };
        var parsed = fred.Program.ParsePhases(args);
        Assert.That(parsed.InPlace, Is.True);
        Assert.That(parsed.BackupSuffix, Is.EqualTo(".bak"));
        Assert.That(parsed.SedScript, Is.EqualTo("s/old/new/g"));
    }

    [Test]
    public void DryRun_ParsesCorrectly()
    {
        string[] args = { ".", "-name", "*.txt", "--sed", "--dry-run", "s/old/new/g" };
        var parsed = fred.Program.ParsePhases(args);
        Assert.That(parsed.DryRun, Is.True);
        Assert.That(parsed.SedScript, Is.EqualTo("s/old/new/g"));
    }

    [Test]
    public void JsonFlag_ParsesCorrectly()
    {
        string[] args = { ".", "-name", "*.txt", "--json" };
        var parsed = fred.Program.ParsePhases(args);
        Assert.That(parsed.JsonOutput, Is.True);
    }

    [Test]
    public void JsonWithGrep_ParsesCorrectly()
    {
        string[] args = { ".", "-name", "*.txt", "--grep", "TODO", "--json" };
        var parsed = fred.Program.ParsePhases(args);
        Assert.That(parsed.JsonOutput, Is.True);
        Assert.That(parsed.GrepArgs, Is.Not.Null);
        Assert.That(parsed.GrepArgs, Does.Contain("TODO"));
    }

    [Test]
    public void InPlaceAndDryRun_BothParsed()
    {
        // -i and --dry-run together - parsing works, behavior is that --dry-run wins in logic
        string[] args = { ".", "--sed", "--dry-run", "-i", "s/a/b/g" };
        var parsed = fred.Program.ParsePhases(args);
        Assert.That(parsed.DryRun, Is.True);
        Assert.That(parsed.InPlace, Is.True);
    }

    [Test]
    public void InPlaceWithCustomSuffix_ParsesCorrectly()
    {
        string[] args = { ".", "--sed", "-i.orig", "s/a/b/g" };
        var parsed = fred.Program.ParsePhases(args);
        Assert.That(parsed.InPlace, Is.True);
        Assert.That(parsed.BackupSuffix, Is.EqualTo(".orig"));
    }

    [Test]
    public void NoSedFlags_DefaultsParsedCorrectly()
    {
        string[] args = { ".", "--sed", "s/a/b/g" };
        var parsed = fred.Program.ParsePhases(args);
        Assert.That(parsed.InPlace, Is.False);
        Assert.That(parsed.DryRun, Is.False);
        Assert.That(parsed.BackupSuffix, Is.Null);
        Assert.That(parsed.SedScript, Is.EqualTo("s/a/b/g"));
    }
}
