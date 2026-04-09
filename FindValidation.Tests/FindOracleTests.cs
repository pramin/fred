using System.Diagnostics;
using NUnit.Framework;

namespace FindValidation.Tests;

/// <summary>
/// Oracle test suite for GNU find. Each test runs the real find binary and nfind,
/// asserting that nfind produces identical sorted output to real find.
/// All temp directories are cleaned up; tests create isolated filesystem structures.
/// </summary>
[TestFixture]
public class FindOracleTests
{
    private const string FindPath = "/usr/bin/find";
    private string _nfindBin = string.Empty;
    private string _tempRoot = string.Empty;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        if (!File.Exists(FindPath))
            Assert.Ignore($"find not found at {FindPath}; skipping oracle tests.");

        // Build nfind
        var buildDir = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."));
        var psi = new ProcessStartInfo("dotnet", $"build {Path.Combine(buildDir, "nfind", "nfind.csproj")} -c Debug -o {Path.Combine(buildDir, "nfind", "bin", "oracle-test")}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        var proc = Process.Start(psi)!;
        proc.WaitForExit(60000);
        Assert.That(proc.ExitCode, Is.EqualTo(0), "nfind build failed");

        _nfindBin = Path.Combine(buildDir, "nfind", "bin", "oracle-test", "nfind");

        _tempRoot = Path.Combine(Path.GetTempPath(), $"find-oracle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        if (Directory.Exists(_tempRoot))
        {
            try { Directory.Delete(_tempRoot, true); } catch { }
        }
    }

    // -------------------------------------------------------------------------
    // Infrastructure
    // -------------------------------------------------------------------------

    private string MakeTempDir()
    {
        string dir = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private void CreateFile(string path, string content = "")
    {
        string? dir = Path.GetDirectoryName(path);
        if (dir != null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, content);
    }

    private void CreateDir(string path)
    {
        Directory.CreateDirectory(path);
    }

    private (string Output, int ExitCode) RunFind(string[] args)
    {
        return RunProcess(FindPath, args);
    }

    private (string Output, int ExitCode) RunNfind(string[] args)
    {
        return RunProcess("dotnet", PrependDll(args));
    }

    private string[] PrependDll(string[] args)
    {
        // Run via: dotnet <nfind.dll> <args...>
        var result = new string[args.Length + 1];
        result[0] = _nfindBin + ".dll";
        for (int i = 0; i < args.Length; i++)
            result[i + 1] = args[i];
        return result;
    }

    private static (string Output, int ExitCode) RunProcess(string exe, string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        for (int i = 0; i < args.Length; i++)
            psi.ArgumentList.Add(args[i]);

        using var proc = Process.Start(psi)!;
        string stdout = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(30000);
        return (stdout, proc.ExitCode);
    }

    /// <summary>
    /// Run both find and nfind with the same args, compare sorted output.
    /// </summary>
    private void AssertFindMatch(string[] args, bool compareExitCode = false)
    {
        var findResult = RunFind(args);
        var nfindResult = RunNfind(args);

        var findLines = SortedLines(findResult.Output);
        var nfindLines = SortedLines(nfindResult.Output);

        Assert.That(nfindLines, Is.EqualTo(findLines),
            $"nfind output should match find.\n  args: {string.Join(" ", args)}\n  find ({findLines.Length} lines): {Truncate(findResult.Output)}\n  nfind ({nfindLines.Length} lines): {Truncate(nfindResult.Output)}");

        if (compareExitCode)
        {
            Assert.That(nfindResult.ExitCode, Is.EqualTo(findResult.ExitCode),
                $"nfind exit code should match find.\n  args: {string.Join(" ", args)}");
        }
    }

    private static string[] SortedLines(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Array.Sort(lines, StringComparer.Ordinal);
        return lines;
    }

    private static string Truncate(string s, int max = 500)
    {
        return s.Length <= max ? s : s.Substring(0, max) + "...";
    }

    // =========================================================================
    // 1. Basic tests
    // =========================================================================

    [Test]
    public void Basic_FindCurrentDir()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "a.txt"), "hello");
        AssertFindMatch(new[] { dir });
    }

    [Test]
    public void Basic_FindWithPath()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "file1.txt"));
        CreateFile(Path.Combine(dir, "file2.txt"));
        AssertFindMatch(new[] { dir });
    }

    [Test]
    public void Basic_EmptyDirectory()
    {
        string dir = MakeTempDir();
        AssertFindMatch(new[] { dir });
    }

    [Test]
    public void Basic_SingleFile()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "only.txt"), "content");
        AssertFindMatch(new[] { dir });
    }

    // =========================================================================
    // 2. -name tests
    // =========================================================================

    [Test]
    public void Name_ExactMatch()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "target.txt"));
        CreateFile(Path.Combine(dir, "other.log"));
        AssertFindMatch(new[] { dir, "-name", "target.txt" });
    }

    [Test]
    public void Name_WildcardStar()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "a.txt"));
        CreateFile(Path.Combine(dir, "b.txt"));
        CreateFile(Path.Combine(dir, "c.log"));
        AssertFindMatch(new[] { dir, "-name", "*.txt" });
    }

    [Test]
    public void Name_WildcardQuestion()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "a1.txt"));
        CreateFile(Path.Combine(dir, "b2.txt"));
        CreateFile(Path.Combine(dir, "cc.txt"));
        AssertFindMatch(new[] { dir, "-name", "??.txt" });
    }

    [Test]
    public void Name_CharacterClass()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "a.txt"));
        CreateFile(Path.Combine(dir, "b.txt"));
        CreateFile(Path.Combine(dir, "c.txt"));
        CreateFile(Path.Combine(dir, "d.txt"));
        AssertFindMatch(new[] { dir, "-name", "[abc].txt" });
    }

    [Test]
    public void Name_CaseSensitive()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "Hello.TXT"));
        CreateFile(Path.Combine(dir, "hello.txt"));
        AssertFindMatch(new[] { dir, "-name", "hello.txt" });
    }

    [Test]
    public void Name_NoMatch()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "a.txt"));
        AssertFindMatch(new[] { dir, "-name", "*.csv" });
    }

    // =========================================================================
    // 3. -iname tests
    // =========================================================================

    [Test]
    public void IName_CaseInsensitive()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "Hello.TXT"));
        CreateFile(Path.Combine(dir, "hello.txt"));
        CreateFile(Path.Combine(dir, "HELLO.txt"));
        AssertFindMatch(new[] { dir, "-iname", "hello.txt" });
    }

    [Test]
    public void IName_WildcardCaseInsensitive()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "Test.CS"));
        CreateFile(Path.Combine(dir, "main.cs"));
        CreateFile(Path.Combine(dir, "util.py"));
        AssertFindMatch(new[] { dir, "-iname", "*.cs" });
    }

    // =========================================================================
    // 4. -path / -ipath tests
    // =========================================================================

    [Test]
    public void Path_FullPathMatch()
    {
        string dir = MakeTempDir();
        CreateDir(Path.Combine(dir, "src"));
        CreateFile(Path.Combine(dir, "src", "main.cs"));
        CreateFile(Path.Combine(dir, "readme.txt"));
        AssertFindMatch(new[] { dir, "-path", $"{dir}/src/*" });
    }

    [Test]
    public void Path_WildcardInPath()
    {
        string dir = MakeTempDir();
        CreateDir(Path.Combine(dir, "src"));
        CreateDir(Path.Combine(dir, "lib"));
        CreateFile(Path.Combine(dir, "src", "a.cs"));
        CreateFile(Path.Combine(dir, "lib", "b.cs"));
        AssertFindMatch(new[] { dir, "-path", "*/*.cs" });
    }

    [Test]
    public void IPath_CaseInsensitive()
    {
        string dir = MakeTempDir();
        CreateDir(Path.Combine(dir, "SRC"));
        CreateFile(Path.Combine(dir, "SRC", "main.cs"));
        AssertFindMatch(new[] { dir, "-ipath", "*src*" });
    }

    // =========================================================================
    // 5. -type f tests
    // =========================================================================

    [Test]
    public void Type_RegularFilesOnly()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "file1.txt"));
        CreateDir(Path.Combine(dir, "subdir"));
        CreateFile(Path.Combine(dir, "subdir", "file2.txt"));
        AssertFindMatch(new[] { dir, "-type", "f" });
    }

    [Test]
    public void Type_FilesOnlyNested()
    {
        string dir = MakeTempDir();
        CreateDir(Path.Combine(dir, "a", "b"));
        CreateFile(Path.Combine(dir, "a", "b", "deep.txt"));
        CreateFile(Path.Combine(dir, "top.txt"));
        AssertFindMatch(new[] { dir, "-type", "f" });
    }

    // =========================================================================
    // 6. -type d tests
    // =========================================================================

    [Test]
    public void Type_DirectoriesOnly()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "file.txt"));
        CreateDir(Path.Combine(dir, "subdir1"));
        CreateDir(Path.Combine(dir, "subdir2"));
        AssertFindMatch(new[] { dir, "-type", "d" });
    }

    [Test]
    public void Type_DirectoriesNested()
    {
        string dir = MakeTempDir();
        CreateDir(Path.Combine(dir, "a"));
        CreateDir(Path.Combine(dir, "a", "b"));
        CreateDir(Path.Combine(dir, "a", "b", "c"));
        AssertFindMatch(new[] { dir, "-type", "d" });
    }

    // =========================================================================
    // 7. -type f,d combined with -or
    // =========================================================================

    [Test]
    public void Type_FilesAndDirsWithOr()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "file.txt"));
        CreateDir(Path.Combine(dir, "subdir"));
        // -type f -o -type d should match everything (same as no -type)
        AssertFindMatch(new[] { dir, "(", "-type", "f", "-o", "-type", "d", ")" });
    }

    // =========================================================================
    // 8. -maxdepth tests
    // =========================================================================

    [Test]
    public void MaxDepth_Zero()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "file.txt"));
        CreateDir(Path.Combine(dir, "sub"));
        AssertFindMatch(new[] { dir, "-maxdepth", "0" });
    }

    [Test]
    public void MaxDepth_One()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "top.txt"));
        CreateDir(Path.Combine(dir, "sub"));
        CreateFile(Path.Combine(dir, "sub", "deep.txt"));
        AssertFindMatch(new[] { dir, "-maxdepth", "1" });
    }

    [Test]
    public void MaxDepth_Two()
    {
        string dir = MakeTempDir();
        CreateDir(Path.Combine(dir, "a"));
        CreateDir(Path.Combine(dir, "a", "b"));
        CreateFile(Path.Combine(dir, "a", "b", "c.txt"));
        CreateFile(Path.Combine(dir, "a", "f.txt"));
        AssertFindMatch(new[] { dir, "-maxdepth", "2" });
    }

    [Test]
    public void MaxDepth_Three()
    {
        string dir = MakeTempDir();
        CreateDir(Path.Combine(dir, "a", "b", "c"));
        CreateFile(Path.Combine(dir, "a", "b", "c", "deep.txt"));
        AssertFindMatch(new[] { dir, "-maxdepth", "3" });
    }

    // =========================================================================
    // 9. -mindepth tests
    // =========================================================================

    [Test]
    public void MinDepth_One()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "top.txt"));
        CreateDir(Path.Combine(dir, "sub"));
        CreateFile(Path.Combine(dir, "sub", "file.txt"));
        AssertFindMatch(new[] { dir, "-mindepth", "1" });
    }

    [Test]
    public void MinDepth_Two()
    {
        string dir = MakeTempDir();
        CreateDir(Path.Combine(dir, "a"));
        CreateFile(Path.Combine(dir, "a", "file.txt"));
        CreateDir(Path.Combine(dir, "a", "b"));
        CreateFile(Path.Combine(dir, "a", "b", "deep.txt"));
        AssertFindMatch(new[] { dir, "-mindepth", "2" });
    }

    // =========================================================================
    // 10. -maxdepth + -mindepth combined
    // =========================================================================

    [Test]
    public void MinMaxDepth_Combined()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "top.txt"));
        CreateDir(Path.Combine(dir, "a"));
        CreateFile(Path.Combine(dir, "a", "mid.txt"));
        CreateDir(Path.Combine(dir, "a", "b"));
        CreateFile(Path.Combine(dir, "a", "b", "deep.txt"));
        AssertFindMatch(new[] { dir, "-mindepth", "1", "-maxdepth", "2" });
    }

    [Test]
    public void MinMaxDepth_SameLevel()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "top.txt"));
        CreateDir(Path.Combine(dir, "sub"));
        CreateFile(Path.Combine(dir, "sub", "mid.txt"));
        AssertFindMatch(new[] { dir, "-mindepth", "1", "-maxdepth", "1" });
    }

    // =========================================================================
    // 11. -size tests
    // =========================================================================

    [Test]
    public void Size_GreaterThanBytes()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "small.txt"), "hi");
        CreateFile(Path.Combine(dir, "big.txt"), new string('x', 200));
        AssertFindMatch(new[] { dir, "-type", "f", "-size", "+100c" });
    }

    [Test]
    public void Size_LessThanBytes()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "small.txt"), "hi");
        CreateFile(Path.Combine(dir, "big.txt"), new string('x', 200));
        AssertFindMatch(new[] { dir, "-type", "f", "-size", "-100c" });
    }

    [Test]
    public void Size_ExactBytes()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "exact.txt"), "hello"); // 5 bytes
        CreateFile(Path.Combine(dir, "other.txt"), "hi");    // 2 bytes
        AssertFindMatch(new[] { dir, "-type", "f", "-size", "5c" });
    }

    [Test]
    public void Size_KiB()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "small.txt"), "hi");
        CreateFile(Path.Combine(dir, "large.txt"), new string('x', 2048));
        AssertFindMatch(new[] { dir, "-type", "f", "-size", "+1k" });
    }

    // =========================================================================
    // 12. -empty tests
    // =========================================================================

    [Test]
    public void Empty_EmptyFile()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "empty.txt"), "");
        CreateFile(Path.Combine(dir, "notempty.txt"), "content");
        AssertFindMatch(new[] { dir, "-type", "f", "-empty" });
    }

    [Test]
    public void Empty_EmptyDir()
    {
        string dir = MakeTempDir();
        CreateDir(Path.Combine(dir, "emptydir"));
        CreateDir(Path.Combine(dir, "notemptydir"));
        CreateFile(Path.Combine(dir, "notemptydir", "file.txt"));
        AssertFindMatch(new[] { dir, "-type", "d", "-empty" });
    }

    [Test]
    public void Empty_MixedFileAndDir()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "empty.txt"), "");
        CreateDir(Path.Combine(dir, "emptydir"));
        CreateFile(Path.Combine(dir, "full.txt"), "data");
        AssertFindMatch(new[] { dir, "-empty" });
    }

    // =========================================================================
    // 13. -mtime tests
    // =========================================================================

    [Test]
    public void MTime_RecentFiles()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "recent.txt"), "now");
        // File just created should be -mtime 0
        AssertFindMatch(new[] { dir, "-type", "f", "-mtime", "0" });
    }

    [Test]
    public void MTime_GreaterThanZero()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "recent.txt"), "now");
        // No files should be older than 0 days since we just created them
        AssertFindMatch(new[] { dir, "-type", "f", "-mtime", "+0" });
    }

    [Test]
    public void MTime_LessThanOneDay()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "fresh.txt"), "new");
        AssertFindMatch(new[] { dir, "-type", "f", "-mtime", "-1" });
    }

    // =========================================================================
    // 14. -mmin tests
    // =========================================================================

    [Test]
    public void MMin_RecentFiles()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "recent.txt"), "now");
        // Just created: -mmin -5 should match
        AssertFindMatch(new[] { dir, "-type", "f", "-mmin", "-5" });
    }

    [Test]
    public void MMin_GreaterThan()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "recent.txt"), "now");
        // Just created: -mmin +60 should not match
        AssertFindMatch(new[] { dir, "-type", "f", "-mmin", "+60" });
    }

    // =========================================================================
    // 15. -newer FILE tests
    // =========================================================================

    [Test]
    public void Newer_FileComparison()
    {
        string dir = MakeTempDir();
        string refFile = Path.Combine(dir, "ref.txt");
        CreateFile(refFile, "reference");
        // Set ref file to 1 hour ago
        File.SetLastWriteTimeUtc(refFile, DateTime.UtcNow.AddHours(-1));
        // Create a newer file
        CreateFile(Path.Combine(dir, "newer.txt"), "new");
        AssertFindMatch(new[] { dir, "-type", "f", "-newer", refFile });
    }

    [Test]
    public void Newer_NoNewerFiles()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "old.txt"), "old");
        // Set old to 1 hour ago
        File.SetLastWriteTimeUtc(Path.Combine(dir, "old.txt"), DateTime.UtcNow.AddHours(-1));
        // Create reference file NOW
        string refFile = Path.Combine(dir, "ref.txt");
        CreateFile(refFile, "reference");
        // old.txt should NOT be newer than ref.txt
        AssertFindMatch(new[] { dir, "-type", "f", "-newer", refFile });
    }

    // =========================================================================
    // 16. -not / ! tests
    // =========================================================================

    [Test]
    public void Not_NegateType()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "file.txt"));
        CreateDir(Path.Combine(dir, "subdir"));
        // ! -type d -> everything that isn't a directory
        AssertFindMatch(new[] { dir, "!", "-type", "d" });
    }

    [Test]
    public void Not_NegateName()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "keep.txt"));
        CreateFile(Path.Combine(dir, "skip.log"));
        AssertFindMatch(new[] { dir, "-type", "f", "!", "-name", "*.log" });
    }

    [Test]
    public void Not_DashNotSyntax()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "a.txt"));
        CreateFile(Path.Combine(dir, "b.log"));
        AssertFindMatch(new[] { dir, "-type", "f", "-not", "-name", "*.log" });
    }

    // =========================================================================
    // 17. -and / -a tests
    // =========================================================================

    [Test]
    public void And_ImplicitConjunction()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "test.txt"), new string('x', 100));
        CreateFile(Path.Combine(dir, "test.log"), new string('x', 100));
        CreateFile(Path.Combine(dir, "small.txt"), "hi");
        // Implicit AND: -name "*.txt" -size +50c
        AssertFindMatch(new[] { dir, "-type", "f", "-name", "*.txt", "-size", "+50c" });
    }

    [Test]
    public void And_ExplicitDashA()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "test.txt"), new string('x', 100));
        CreateFile(Path.Combine(dir, "test.log"), new string('x', 100));
        AssertFindMatch(new[] { dir, "-type", "f", "-name", "*.txt", "-a", "-size", "+50c" });
    }

    // =========================================================================
    // 18. -or / -o tests
    // =========================================================================

    [Test]
    public void Or_DisjunctionNames()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "a.txt"));
        CreateFile(Path.Combine(dir, "b.log"));
        CreateFile(Path.Combine(dir, "c.csv"));
        AssertFindMatch(new[] { dir, "-type", "f", "(", "-name", "*.txt", "-o", "-name", "*.log", ")" });
    }

    [Test]
    public void Or_DisjunctionTypes()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "file.txt"));
        CreateDir(Path.Combine(dir, "subdir"));
        AssertFindMatch(new[] { dir, "(", "-type", "f", "-o", "-type", "d", ")" });
    }

    // =========================================================================
    // 19. Parentheses grouping
    // =========================================================================

    [Test]
    public void Parens_GroupedExpression()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "a.txt"));
        CreateFile(Path.Combine(dir, "b.log"));
        CreateFile(Path.Combine(dir, "c.csv"));
        // ( -name "*.txt" -o -name "*.csv" )
        AssertFindMatch(new[] { dir, "-type", "f", "(", "-name", "*.txt", "-o", "-name", "*.csv", ")" });
    }

    [Test]
    public void Parens_NestedGroups()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "a.txt"));
        CreateFile(Path.Combine(dir, "b.log"));
        CreateFile(Path.Combine(dir, "c.csv"));
        CreateFile(Path.Combine(dir, "d.xml"));
        // ( -name "*.txt" -o ( -name "*.csv" -o -name "*.xml" ) )
        AssertFindMatch(new[] { dir, "-type", "f", "(", "-name", "*.txt", "-o", "(", "-name", "*.csv", "-o", "-name", "*.xml", ")", ")" });
    }

    // =========================================================================
    // 20. Combined predicates
    // =========================================================================

    [Test]
    public void Combined_NameTypeMaxdepth()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "top.cs"));
        CreateDir(Path.Combine(dir, "sub"));
        CreateFile(Path.Combine(dir, "sub", "deep.cs"));
        CreateDir(Path.Combine(dir, "sub", "sub2"));
        CreateFile(Path.Combine(dir, "sub", "sub2", "deeper.cs"));
        AssertFindMatch(new[] { dir, "-name", "*.cs", "-type", "f", "-maxdepth", "2" });
    }

    [Test]
    public void Combined_TypeSizeName()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "big.txt"), new string('x', 500));
        CreateFile(Path.Combine(dir, "small.txt"), "hi");
        CreateFile(Path.Combine(dir, "big.log"), new string('y', 500));
        AssertFindMatch(new[] { dir, "-type", "f", "-name", "*.txt", "-size", "+100c" });
    }

    [Test]
    public void Combined_NotNameType()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "keep.cs"));
        CreateFile(Path.Combine(dir, "skip.txt"));
        CreateDir(Path.Combine(dir, "dir1"));
        AssertFindMatch(new[] { dir, "-type", "f", "!", "-name", "*.txt" });
    }

    // =========================================================================
    // 21. -print0 tests
    // =========================================================================

    [Test]
    public void Print0_NullTerminated()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "a.txt"));
        CreateFile(Path.Combine(dir, "b.txt"));

        var findResult = RunFind(new[] { dir, "-type", "f", "-print0" });
        var nfindResult = RunNfind(new[] { dir, "-type", "f", "-print0" });

        // Sort null-terminated entries
        var findEntries = findResult.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var nfindEntries = nfindResult.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        Array.Sort(findEntries, StringComparer.Ordinal);
        Array.Sort(nfindEntries, StringComparer.Ordinal);

        Assert.That(nfindEntries, Is.EqualTo(findEntries), "print0 output should match");
    }

    // =========================================================================
    // 22. -prune tests
    // =========================================================================

    [Test]
    public void Prune_SkipDirectory()
    {
        string dir = MakeTempDir();
        CreateDir(Path.Combine(dir, "skip"));
        CreateFile(Path.Combine(dir, "skip", "hidden.txt"));
        CreateDir(Path.Combine(dir, "keep"));
        CreateFile(Path.Combine(dir, "keep", "visible.txt"));
        // -name skip -prune -o -type f -print
        AssertFindMatch(new[] { dir, "-name", "skip", "-prune", "-o", "-type", "f", "-print" });
    }

    [Test]
    public void Prune_SkipNestedDir()
    {
        string dir = MakeTempDir();
        CreateDir(Path.Combine(dir, "a"));
        CreateDir(Path.Combine(dir, "a", "node_modules"));
        CreateFile(Path.Combine(dir, "a", "node_modules", "pkg.json"));
        CreateFile(Path.Combine(dir, "a", "src.txt"));
        AssertFindMatch(new[] { dir, "-name", "node_modules", "-prune", "-o", "-type", "f", "-print" });
    }

    // =========================================================================
    // 23. Multiple start paths
    // =========================================================================

    [Test]
    public void MultipleStartPaths()
    {
        string dir1 = MakeTempDir();
        string dir2 = MakeTempDir();
        CreateFile(Path.Combine(dir1, "a.txt"));
        CreateFile(Path.Combine(dir2, "b.txt"));
        AssertFindMatch(new[] { dir1, dir2, "-type", "f" });
    }

    [Test]
    public void MultipleStartPaths_WithName()
    {
        string dir1 = MakeTempDir();
        string dir2 = MakeTempDir();
        CreateFile(Path.Combine(dir1, "target.cs"));
        CreateFile(Path.Combine(dir1, "other.txt"));
        CreateFile(Path.Combine(dir2, "target.cs"));
        AssertFindMatch(new[] { dir1, dir2, "-name", "*.cs" });
    }

    // =========================================================================
    // 24. No matches
    // =========================================================================

    [Test]
    public void NoMatches_EmptyResult()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "a.txt"));
        AssertFindMatch(new[] { dir, "-name", "*.nonexistent" });
    }

    // =========================================================================
    // 25. Symlinks
    // =========================================================================

    [Test]
    public void Symlinks_BasicHandling()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "target.txt"), "content");
        try
        {
            File.CreateSymbolicLink(Path.Combine(dir, "link.txt"), Path.Combine(dir, "target.txt"));
        }
        catch (IOException)
        {
            Assert.Ignore("Symlink creation not supported in this environment");
            return;
        }
        AssertFindMatch(new[] { dir, "-maxdepth", "1" });
    }

    // =========================================================================
    // 26. Hidden files
    // =========================================================================

    [Test]
    public void HiddenFiles_DotFiles()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, ".hidden"));
        CreateFile(Path.Combine(dir, "visible.txt"));
        AssertFindMatch(new[] { dir, "-type", "f" });
    }

    [Test]
    public void HiddenFiles_DotDirectories()
    {
        string dir = MakeTempDir();
        CreateDir(Path.Combine(dir, ".git"));
        CreateFile(Path.Combine(dir, ".git", "config"));
        CreateFile(Path.Combine(dir, "src.txt"));
        AssertFindMatch(new[] { dir, "-name", ".git" });
    }

    [Test]
    public void HiddenFiles_NameFilterDot()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, ".bashrc"));
        CreateFile(Path.Combine(dir, ".profile"));
        CreateFile(Path.Combine(dir, "readme.txt"));
        AssertFindMatch(new[] { dir, "-name", ".*" });
    }

    // =========================================================================
    // 27. Special characters in filenames
    // =========================================================================

    [Test]
    public void SpecialChars_SpacesInName()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "my file.txt"), "content");
        CreateFile(Path.Combine(dir, "normal.txt"));
        AssertFindMatch(new[] { dir, "-type", "f" });
    }

    [Test]
    public void SpecialChars_ParensInName()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "file(1).txt"), "content");
        AssertFindMatch(new[] { dir, "-type", "f" });
    }

    [Test]
    public void SpecialChars_DashInName()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "my-file.txt"));
        AssertFindMatch(new[] { dir, "-name", "*-*" });
    }

    // =========================================================================
    // 28. Deep nesting
    // =========================================================================

    [Test]
    public void DeepNesting_FiveLevels()
    {
        string dir = MakeTempDir();
        string deep = Path.Combine(dir, "a", "b", "c", "d", "e");
        CreateDir(deep);
        CreateFile(Path.Combine(deep, "deep.txt"), "bottom");
        CreateFile(Path.Combine(dir, "top.txt"), "top");
        AssertFindMatch(new[] { dir, "-type", "f" });
    }

    [Test]
    public void DeepNesting_WithMaxdepth()
    {
        string dir = MakeTempDir();
        string deep = Path.Combine(dir, "a", "b", "c", "d", "e");
        CreateDir(deep);
        CreateFile(Path.Combine(deep, "deep.txt"));
        CreateFile(Path.Combine(dir, "a", "shallow.txt"));
        AssertFindMatch(new[] { dir, "-type", "f", "-maxdepth", "3" });
    }

    [Test]
    public void DeepNesting_AllDirs()
    {
        string dir = MakeTempDir();
        CreateDir(Path.Combine(dir, "a", "b", "c", "d", "e"));
        AssertFindMatch(new[] { dir, "-type", "d" });
    }

    // =========================================================================
    // 29. Multiple -name with -or
    // =========================================================================

    [Test]
    public void MultipleNames_OrPattern()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "main.c"));
        CreateFile(Path.Combine(dir, "util.h"));
        CreateFile(Path.Combine(dir, "readme.txt"));
        CreateFile(Path.Combine(dir, "Makefile"));
        AssertFindMatch(new[] { dir, "-type", "f", "(", "-name", "*.c", "-o", "-name", "*.h", ")" });
    }

    [Test]
    public void MultipleNames_TripleOr()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "a.c"));
        CreateFile(Path.Combine(dir, "b.h"));
        CreateFile(Path.Combine(dir, "c.cs"));
        CreateFile(Path.Combine(dir, "d.txt"));
        AssertFindMatch(new[] { dir, "-type", "f", "(", "-name", "*.c", "-o", "-name", "*.h", "-o", "-name", "*.cs", ")" });
    }

    // =========================================================================
    // Additional coverage tests
    // =========================================================================

    [Test]
    public void ManyFiles_Stress()
    {
        string dir = MakeTempDir();
        for (int i = 0; i < 50; i++)
        {
            CreateFile(Path.Combine(dir, $"file{i:D3}.txt"), $"content {i}");
        }
        AssertFindMatch(new[] { dir, "-type", "f", "-name", "*.txt" });
    }

    [Test]
    public void MixedStructure_Complex()
    {
        string dir = MakeTempDir();
        CreateDir(Path.Combine(dir, "src"));
        CreateDir(Path.Combine(dir, "test"));
        CreateDir(Path.Combine(dir, "build"));
        CreateFile(Path.Combine(dir, "src", "main.cs"));
        CreateFile(Path.Combine(dir, "src", "util.cs"));
        CreateFile(Path.Combine(dir, "test", "test_main.cs"));
        CreateFile(Path.Combine(dir, "build", "output.dll"));
        CreateFile(Path.Combine(dir, "README.md"));
        AssertFindMatch(new[] { dir, "-name", "*.cs", "-type", "f" });
    }

    [Test]
    public void Name_StarOnly()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "a.txt"));
        CreateFile(Path.Combine(dir, "b.log"));
        // -name "*" matches everything
        AssertFindMatch(new[] { dir, "-type", "f", "-name", "*" });
    }

    [Test]
    public void MaxDepth_WithName()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "top.txt"));
        CreateDir(Path.Combine(dir, "sub"));
        CreateFile(Path.Combine(dir, "sub", "mid.txt"));
        CreateDir(Path.Combine(dir, "sub", "deep"));
        CreateFile(Path.Combine(dir, "sub", "deep", "bottom.txt"));
        AssertFindMatch(new[] { dir, "-maxdepth", "2", "-name", "*.txt" });
    }

    [Test]
    public void MinDepth_WithType()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "top.txt"));
        CreateDir(Path.Combine(dir, "sub"));
        CreateFile(Path.Combine(dir, "sub", "mid.txt"));
        AssertFindMatch(new[] { dir, "-mindepth", "1", "-type", "f" });
    }

    [Test]
    public void Empty_NoEmptyAnywhere()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "a.txt"), "content");
        CreateDir(Path.Combine(dir, "sub"));
        CreateFile(Path.Combine(dir, "sub", "b.txt"), "more");
        AssertFindMatch(new[] { dir, "-empty" });
    }

    [Test]
    public void Not_WithOr()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "a.txt"));
        CreateFile(Path.Combine(dir, "b.log"));
        CreateFile(Path.Combine(dir, "c.csv"));
        // ! ( -name "*.txt" -o -name "*.log" ) -> only .csv
        AssertFindMatch(new[] { dir, "-type", "f", "!", "(", "-name", "*.txt", "-o", "-name", "*.log", ")" });
    }

    [Test]
    public void Type_DirWithName()
    {
        string dir = MakeTempDir();
        CreateDir(Path.Combine(dir, "src"));
        CreateDir(Path.Combine(dir, "test"));
        CreateDir(Path.Combine(dir, "build"));
        CreateFile(Path.Combine(dir, "file.txt"));
        AssertFindMatch(new[] { dir, "-type", "d", "-name", "src" });
    }

    [Test]
    public void Size_EmptyFiles()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "empty.txt"), "");
        CreateFile(Path.Combine(dir, "notempty.txt"), "data");
        AssertFindMatch(new[] { dir, "-type", "f", "-size", "0c" });
    }

    [Test]
    public void Or_WithMaxdepth()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "a.txt"));
        CreateFile(Path.Combine(dir, "b.cs"));
        CreateDir(Path.Combine(dir, "sub"));
        CreateFile(Path.Combine(dir, "sub", "c.txt"));
        CreateFile(Path.Combine(dir, "sub", "d.py"));
        AssertFindMatch(new[] { dir, "-maxdepth", "1", "-type", "f", "(", "-name", "*.txt", "-o", "-name", "*.cs", ")" });
    }

    [Test]
    public void Name_TrailingDot()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "file."));
        CreateFile(Path.Combine(dir, "file.txt"));
        AssertFindMatch(new[] { dir, "-name", "file." });
    }

    [Test]
    public void Name_LeadingDot()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, ".gitignore"));
        CreateFile(Path.Combine(dir, "gitignore"));
        AssertFindMatch(new[] { dir, "-name", ".gitignore" });
    }

    [Test]
    public void MultiLevel_NameAndType()
    {
        string dir = MakeTempDir();
        CreateDir(Path.Combine(dir, "src", "controllers"));
        CreateDir(Path.Combine(dir, "src", "models"));
        CreateFile(Path.Combine(dir, "src", "controllers", "auth.cs"));
        CreateFile(Path.Combine(dir, "src", "controllers", "user.cs"));
        CreateFile(Path.Combine(dir, "src", "models", "data.cs"));
        CreateFile(Path.Combine(dir, "src", "models", "config.json"));
        AssertFindMatch(new[] { dir, "-type", "f", "-name", "*.cs" });
    }

    [Test]
    public void MaxDepth_ZeroWithName()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "file.txt"));
        // maxdepth 0 should only return the start path itself
        AssertFindMatch(new[] { dir, "-maxdepth", "0", "-name", "file.txt" });
    }

    [Test]
    public void ImplicitAnd_ThreePredicates()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "big.txt"), new string('a', 200));
        CreateFile(Path.Combine(dir, "small.txt"), "hi");
        CreateFile(Path.Combine(dir, "big.log"), new string('b', 200));
        CreateDir(Path.Combine(dir, "sub"));
        // -type f -name "*.txt" -size +100c -> only big.txt
        AssertFindMatch(new[] { dir, "-type", "f", "-name", "*.txt", "-size", "+100c" });
    }

    [Test]
    public void DoubleNegation()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "a.txt"));
        CreateFile(Path.Combine(dir, "b.log"));
        // ! ! -name "*.txt" -> same as -name "*.txt"
        AssertFindMatch(new[] { dir, "-type", "f", "!", "!", "-name", "*.txt" });
    }

    [Test]
    public void Size_LargerSuffixes()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "small.bin"), new string('x', 100));
        // -size +1M -> nothing matches (100 bytes < 1MB)
        AssertFindMatch(new[] { dir, "-type", "f", "-size", "+1M" });
    }

    [Test]
    public void Name_MultipleWildcards()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "test_data_2024.csv"));
        CreateFile(Path.Combine(dir, "test_log.csv"));
        CreateFile(Path.Combine(dir, "prod_data_2024.csv"));
        AssertFindMatch(new[] { dir, "-name", "test_*_*.csv" });
    }

    [Test]
    public void MinDepth_DeepOnly()
    {
        string dir = MakeTempDir();
        CreateDir(Path.Combine(dir, "a", "b", "c"));
        CreateFile(Path.Combine(dir, "top.txt"));
        CreateFile(Path.Combine(dir, "a", "mid.txt"));
        CreateFile(Path.Combine(dir, "a", "b", "deep.txt"));
        CreateFile(Path.Combine(dir, "a", "b", "c", "deepest.txt"));
        AssertFindMatch(new[] { dir, "-mindepth", "3", "-type", "f" });
    }

    [Test]
    public void SingleDir_TypeD()
    {
        string dir = MakeTempDir();
        // Start dir itself is a directory, depth 0
        AssertFindMatch(new[] { dir, "-type", "d" });
    }

    [Test]
    public void MixedExtensions()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "a.c"));
        CreateFile(Path.Combine(dir, "b.cpp"));
        CreateFile(Path.Combine(dir, "c.h"));
        CreateFile(Path.Combine(dir, "d.hpp"));
        CreateFile(Path.Combine(dir, "e.py"));
        CreateFile(Path.Combine(dir, "f.rs"));
        AssertFindMatch(new[] { dir, "-type", "f", "(", "-name", "*.c", "-o", "-name", "*.cpp", "-o", "-name", "*.h", "-o", "-name", "*.hpp", ")" });
    }

    [Test]
    public void Name_QuestionMarkOnly()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "a"));
        CreateFile(Path.Combine(dir, "ab"));
        CreateFile(Path.Combine(dir, "abc"));
        AssertFindMatch(new[] { dir, "-name", "?" });
    }

    [Test]
    public void NameExact_NoWildcard()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "Makefile"));
        CreateFile(Path.Combine(dir, "makefile"));
        CreateFile(Path.Combine(dir, "other.txt"));
        AssertFindMatch(new[] { dir, "-name", "Makefile" });
    }

    [Test]
    public void Empty_WithNot()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "empty.txt"), "");
        CreateFile(Path.Combine(dir, "full.txt"), "content");
        AssertFindMatch(new[] { dir, "-type", "f", "!", "-empty" });
    }

    [Test]
    public void MaxDepth_WithMinDepthAndType()
    {
        string dir = MakeTempDir();
        CreateDir(Path.Combine(dir, "a", "b"));
        CreateFile(Path.Combine(dir, "top.txt"));
        CreateFile(Path.Combine(dir, "a", "mid.txt"));
        CreateFile(Path.Combine(dir, "a", "b", "deep.txt"));
        AssertFindMatch(new[] { dir, "-mindepth", "1", "-maxdepth", "1", "-type", "f" });
    }

    [Test]
    public void MultiStartPaths_WithMaxdepth()
    {
        string dir1 = MakeTempDir();
        string dir2 = MakeTempDir();
        CreateDir(Path.Combine(dir1, "sub"));
        CreateFile(Path.Combine(dir1, "a.txt"));
        CreateFile(Path.Combine(dir1, "sub", "b.txt"));
        CreateFile(Path.Combine(dir2, "c.txt"));
        AssertFindMatch(new[] { dir1, dir2, "-maxdepth", "1", "-type", "f" });
    }

    [Test]
    public void Name_NumericPattern()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "001.txt"));
        CreateFile(Path.Combine(dir, "002.txt"));
        CreateFile(Path.Combine(dir, "abc.txt"));
        AssertFindMatch(new[] { dir, "-name", "0*.txt" });
    }

    [Test]
    public void ComplexTree_FindAllFiles()
    {
        string dir = MakeTempDir();
        CreateDir(Path.Combine(dir, "src", "main"));
        CreateDir(Path.Combine(dir, "src", "test"));
        CreateDir(Path.Combine(dir, "docs"));
        CreateDir(Path.Combine(dir, ".git", "objects"));
        CreateFile(Path.Combine(dir, "README.md"));
        CreateFile(Path.Combine(dir, "src", "main", "App.cs"));
        CreateFile(Path.Combine(dir, "src", "main", "Util.cs"));
        CreateFile(Path.Combine(dir, "src", "test", "AppTest.cs"));
        CreateFile(Path.Combine(dir, "docs", "guide.md"));
        CreateFile(Path.Combine(dir, ".git", "config"));
        CreateFile(Path.Combine(dir, ".git", "objects", "abc123"));
        AssertFindMatch(new[] { dir, "-type", "f" });
    }

    [Test]
    public void ComplexTree_FindAllDirs()
    {
        string dir = MakeTempDir();
        CreateDir(Path.Combine(dir, "a", "b", "c"));
        CreateDir(Path.Combine(dir, "d"));
        CreateDir(Path.Combine(dir, "e", "f"));
        CreateFile(Path.Combine(dir, "file.txt"));
        AssertFindMatch(new[] { dir, "-type", "d" });
    }

    [Test]
    public void IName_MixedCase()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "README.md"));
        CreateFile(Path.Combine(dir, "readme.txt"));
        CreateFile(Path.Combine(dir, "ReadMe.MD"));
        AssertFindMatch(new[] { dir, "-iname", "readme.*" });
    }

    [Test]
    public void Size_ZeroPlusC()
    {
        string dir = MakeTempDir();
        CreateFile(Path.Combine(dir, "empty.txt"), "");
        CreateFile(Path.Combine(dir, "notempty.txt"), "x");
        // +0c means size > 0
        AssertFindMatch(new[] { dir, "-type", "f", "-size", "+0c" });
    }

    [Test]
    public void NameWildcardStar_NestedDirs()
    {
        string dir = MakeTempDir();
        CreateDir(Path.Combine(dir, "src"));
        CreateDir(Path.Combine(dir, "lib"));
        CreateFile(Path.Combine(dir, "src", "main.cs"));
        CreateFile(Path.Combine(dir, "lib", "helper.cs"));
        CreateFile(Path.Combine(dir, "top.py"));
        AssertFindMatch(new[] { dir, "-name", "*.cs" });
    }
}
