using NUnit.Framework;
using FredDotNet;

namespace FredDotNet.Tests;

[TestFixture]
public class DiffEngineTests
{
    // ---------------------------------------------------------------
    // Diff (string, string) tests
    // ---------------------------------------------------------------

    [Test]
    public void Diff_IdenticalStrings_ReturnsEmpty()
    {
        string result = DiffEngine.Diff("hello\nworld\n", "hello\nworld\n");
        Assert.That(result, Is.Empty);
    }

    [Test]
    public void Diff_EmptyOriginalAndModified_ReturnsEmpty()
    {
        string result = DiffEngine.Diff("", "");
        Assert.That(result, Is.Empty);
    }

    [Test]
    public void Diff_WithLabel_IncludesLabelInHeader()
    {
        string result = DiffEngine.Diff("hello\n", "world\n", "test.txt");
        Assert.That(result, Does.Contain("--- a/test.txt"));
        Assert.That(result, Does.Contain("+++ b/test.txt"));
    }

    [Test]
    public void Diff_WithoutLabel_UsesDefaultHeaders()
    {
        string result = DiffEngine.Diff("hello\n", "world\n");
        Assert.That(result, Does.Contain("--- a"));
        Assert.That(result, Does.Contain("+++ b"));
    }

    [Test]
    public void Diff_SingleLineChange_ProducesValidDiff()
    {
        string result = DiffEngine.Diff("hello\n", "world\n");
        Assert.That(result, Does.Contain("-hello"));
        Assert.That(result, Does.Contain("+world"));
        Assert.That(result, Does.Contain("@@"));
    }

    // ---------------------------------------------------------------
    // DiffFiles tests
    // ---------------------------------------------------------------

    [Test]
    public void DiffFiles_BothExist_ProducesDiff()
    {
        string dir = Path.Combine(Path.GetTempPath(), "fredtest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string orig = Path.Combine(dir, "orig.txt");
            string mod = Path.Combine(dir, "mod.txt");
            File.WriteAllText(orig, "line1\nline2\nline3\n");
            File.WriteAllText(mod, "line1\nchanged\nline3\n");

            string result = DiffEngine.DiffFiles(orig, mod);
            Assert.That(result, Does.Contain("-line2"));
            Assert.That(result, Does.Contain("+changed"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public void DiffFiles_OriginalMissing_ThrowsFileNotFound()
    {
        Assert.Throws<FileNotFoundException>(() =>
            DiffEngine.DiffFiles("/nonexistent/file.txt", "/nonexistent/other.txt"));
    }

    [Test]
    public void DiffFiles_ModifiedMissing_ThrowsFileNotFound()
    {
        string dir = Path.Combine(Path.GetTempPath(), "fredtest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string orig = Path.Combine(dir, "orig.txt");
            File.WriteAllText(orig, "hello\n");

            Assert.Throws<FileNotFoundException>(() =>
                DiffEngine.DiffFiles(orig, "/nonexistent/other.txt"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public void DiffFiles_IdenticalFiles_ReturnsEmpty()
    {
        string dir = Path.Combine(Path.GetTempPath(), "fredtest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string a = Path.Combine(dir, "a.txt");
            string b = Path.Combine(dir, "b.txt");
            File.WriteAllText(a, "same content\n");
            File.WriteAllText(b, "same content\n");

            string result = DiffEngine.DiffFiles(a, b);
            Assert.That(result, Is.Empty);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ---------------------------------------------------------------
    // Patch tests
    // ---------------------------------------------------------------

    [Test]
    public void Patch_EmptyDiff_ReturnsOriginal()
    {
        string original = "hello\nworld\n";
        string result = DiffEngine.Patch(original, "");
        Assert.That(result, Is.EqualTo(original));
    }

    [Test]
    public void Patch_NullDiff_ReturnsOriginal()
    {
        string original = "hello\nworld\n";
        string result = DiffEngine.Patch(original, null!);
        Assert.That(result, Is.EqualTo(original));
    }

    [Test]
    public void Patch_RoundTrip_SingleLineReplace()
    {
        string original = "line1\nline2\nline3\n";
        string modified = "line1\nchanged\nline3\n";

        string diff = DiffEngine.Diff(original, modified, "test.txt");
        string patched = DiffEngine.Patch(original, diff);

        Assert.That(patched, Is.EqualTo(modified));
    }

    [Test]
    public void Patch_RoundTrip_AddLine()
    {
        string original = "line1\nline2\n";
        string modified = "line1\nline2\nline3\n";

        string diff = DiffEngine.Diff(original, modified, "test.txt");
        string patched = DiffEngine.Patch(original, diff);

        Assert.That(patched, Is.EqualTo(modified));
    }

    [Test]
    public void Patch_RoundTrip_RemoveLine()
    {
        string original = "line1\nline2\nline3\n";
        string modified = "line1\nline3\n";

        string diff = DiffEngine.Diff(original, modified, "test.txt");
        string patched = DiffEngine.Patch(original, diff);

        Assert.That(patched, Is.EqualTo(modified));
    }

    [Test]
    public void Patch_RoundTrip_MultipleHunks()
    {
        // Create a file with enough lines between changes to create separate hunks
        var origLines = new string[20];
        var modLines = new string[20];
        for (int i = 0; i < 20; i++)
        {
            origLines[i] = $"line{i + 1}";
            modLines[i] = $"line{i + 1}";
        }
        origLines[2] = "old_a";
        modLines[2] = "new_a";
        origLines[17] = "old_b";
        modLines[17] = "new_b";

        string original = string.Join("\n", origLines) + "\n";
        string modified = string.Join("\n", modLines) + "\n";

        string diff = DiffEngine.Diff(original, modified, "test.txt");
        string patched = DiffEngine.Patch(original, diff);

        Assert.That(patched, Is.EqualTo(modified));
    }

    [Test]
    public void Patch_ContextMismatch_ThrowsDiffException()
    {
        string original = "line1\nline2\nline3\n";
        string modified = "line1\nchanged\nline3\n";

        string diff = DiffEngine.Diff(original, modified, "test.txt");

        // Apply diff to a different file
        string differentOriginal = "line1\nsomething_else\nline3\n";
        Assert.Throws<DiffException>(() => DiffEngine.Patch(differentOriginal, diff));
    }

    [Test]
    public void Patch_RoundTrip_NoTrailingNewline()
    {
        string original = "line1\nline2\nline3";
        string modified = "line1\nchanged\nline3";

        string diff = DiffEngine.Diff(original, modified, "test.txt");
        string patched = DiffEngine.Patch(original, diff);

        Assert.That(patched, Is.EqualTo(modified));
    }

    [Test]
    public void Patch_ManualDiff_Applied()
    {
        string original = "aaa\nbbb\nccc\n";
        string diff = "--- a/test.txt\n+++ b/test.txt\n@@ -1,3 +1,3 @@\n aaa\n-bbb\n+BBB\n ccc\n";

        string patched = DiffEngine.Patch(original, diff);
        Assert.That(patched, Is.EqualTo("aaa\nBBB\nccc\n"));
    }

    [Test]
    public void Patch_InvalidHunkHeader_ThrowsDiffException()
    {
        string original = "hello\n";
        string badDiff = "--- a\n+++ b\n@@ invalid @@\n-hello\n+world\n";

        Assert.Throws<DiffException>(() => DiffEngine.Patch(original, badDiff));
    }

    // ---------------------------------------------------------------
    // PatchFile tests
    // ---------------------------------------------------------------

    [Test]
    public void PatchFile_AppliesPatch()
    {
        string dir = Path.Combine(Path.GetTempPath(), "fredtest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "test.txt");
            File.WriteAllText(file, "line1\nline2\nline3\n");

            string diff = "--- a/test.txt\n+++ b/test.txt\n@@ -1,3 +1,3 @@\n line1\n-line2\n+changed\n line3\n";
            DiffEngine.PatchFile(file, diff);

            string result = File.ReadAllText(file);
            Assert.That(result, Is.EqualTo("line1\nchanged\nline3\n"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public void PatchFile_WithBackup_CreatesBackup()
    {
        string dir = Path.Combine(Path.GetTempPath(), "fredtest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "test.txt");
            File.WriteAllText(file, "line1\nline2\nline3\n");

            string diff = "--- a/test.txt\n+++ b/test.txt\n@@ -1,3 +1,3 @@\n line1\n-line2\n+changed\n line3\n";
            DiffEngine.PatchFile(file, diff, ".bak");

            Assert.That(File.Exists(file + ".bak"), Is.True);
            Assert.That(File.ReadAllText(file + ".bak"), Is.EqualTo("line1\nline2\nline3\n"));
            Assert.That(File.ReadAllText(file), Is.EqualTo("line1\nchanged\nline3\n"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public void PatchFile_MissingFile_ThrowsFileNotFound()
    {
        Assert.Throws<FileNotFoundException>(() =>
            DiffEngine.PatchFile("/nonexistent/file.txt", "--- a\n+++ b\n@@ -1 +1 @@\n-old\n+new\n"));
    }

    // ---------------------------------------------------------------
    // CanPatch tests
    // ---------------------------------------------------------------

    [Test]
    public void CanPatch_ValidPatch_ReturnsTrue()
    {
        string original = "aaa\nbbb\nccc\n";
        string diff = "--- a\n+++ b\n@@ -1,3 +1,3 @@\n aaa\n-bbb\n+BBB\n ccc\n";

        Assert.That(DiffEngine.CanPatch(original, diff), Is.True);
    }

    [Test]
    public void CanPatch_MismatchedOriginal_ReturnsFalse()
    {
        string original = "xxx\nyyy\nzzz\n";
        string diff = "--- a\n+++ b\n@@ -1,3 +1,3 @@\n aaa\n-bbb\n+BBB\n ccc\n";

        Assert.That(DiffEngine.CanPatch(original, diff), Is.False);
    }

    [Test]
    public void CanPatch_EmptyDiff_ReturnsTrue()
    {
        Assert.That(DiffEngine.CanPatch("anything", ""), Is.True);
    }

    // ---------------------------------------------------------------
    // Edge cases
    // ---------------------------------------------------------------

    [Test]
    public void Patch_EmptyOriginal_AddingLines()
    {
        string original = "";
        string modified = "new line\n";

        string diff = DiffEngine.Diff(original, modified, "test.txt");
        string patched = DiffEngine.Patch(original, diff);

        Assert.That(patched, Is.EqualTo(modified));
    }

    [Test]
    public void Patch_SingleLineFile()
    {
        string original = "hello\n";
        string modified = "world\n";

        string diff = DiffEngine.Diff(original, modified, "test.txt");
        string patched = DiffEngine.Patch(original, diff);

        Assert.That(patched, Is.EqualTo(modified));
    }

    [Test]
    public void Diff_RoundTrip_LargerFile()
    {
        var origLines = new string[50];
        var modLines = new string[50];
        for (int i = 0; i < 50; i++)
        {
            origLines[i] = $"original line {i + 1}";
            modLines[i] = $"original line {i + 1}";
        }
        // Modify a few lines
        modLines[5] = "MODIFIED line 6";
        modLines[25] = "MODIFIED line 26";
        modLines[45] = "MODIFIED line 46";

        string original = string.Join("\n", origLines) + "\n";
        string modified = string.Join("\n", modLines) + "\n";

        string diff = DiffEngine.Diff(original, modified, "big.txt");
        string patched = DiffEngine.Patch(original, diff);

        Assert.That(patched, Is.EqualTo(modified));
    }

    [Test]
    public void Patch_HunkHeaderWithoutCount_UsesDefaultOne()
    {
        // @@ -1 +1 @@ means count=1 for both sides
        string original = "hello\n";
        string diff = "--- a\n+++ b\n@@ -1 +1 @@\n-hello\n+world\n";

        string patched = DiffEngine.Patch(original, diff);
        Assert.That(patched, Is.EqualTo("world\n"));
    }
}
