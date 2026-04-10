using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NUnit.Framework;

namespace FredDotNet.Tests;

/// <summary>
/// Integration tests for the "edit" pipeline stage in the fred-mcp MCP server.
/// Each test spawns the MCP server process, sends JSON-RPC requests over stdin,
/// and validates JSON-RPC responses from stdout.
/// </summary>
[TestFixture]
public class McpEditPipelineTests
{
    // Resolve the fred-mcp binary from the build output.
    // TestDirectory is something like .../FredDotNet.Tests/bin/Release/net10.0
    // The MCP binary is at .../fred-mcp/bin/Release/net10.0/fred-mcp
    private static readonly string s_mcpBinary = ResolveMcpBinary();

    private readonly List<string> _tempFiles = new();
    private readonly List<string> _tempDirs = new();

    private static string ResolveMcpBinary()
    {
        string testDir = TestContext.CurrentContext.TestDirectory;
        // Walk up from FredDotNet.Tests/bin/Release/net10.0 to solution root
        string solutionRoot = Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", ".."));
        // Determine configuration from the test directory path
        string config = testDir.Contains("Release") ? "Release" : "Debug";
        // Determine target framework from the test directory
        string tfm = Path.GetFileName(testDir); // e.g. "net10.0"
        string binary = Path.Combine(solutionRoot, "fred-mcp", "bin", config, tfm, "fred-mcp");
        if (!File.Exists(binary))
            return "";
        return binary;
    }

    [TearDown]
    public void TearDown()
    {
        for (int i = 0; i < _tempFiles.Count; i++)
        {
            if (File.Exists(_tempFiles[i]))
                File.Delete(_tempFiles[i]);
        }
        _tempFiles.Clear();

        for (int i = 0; i < _tempDirs.Count; i++)
        {
            if (Directory.Exists(_tempDirs[i]))
                Directory.Delete(_tempDirs[i], recursive: true);
        }
        _tempDirs.Clear();
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private string CreateTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "fred_mcp_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private string WriteTempFile(string dir, string name, string content)
    {
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, content);
        _tempFiles.Add(path);
        return path;
    }

    /// <summary>
    /// Sends a tools/call request for the "pipeline" tool with the given arguments,
    /// waits for the response, and returns the parsed result text.
    /// </summary>
    private static string CallPipelineTool(string argumentsJson)
    {
        string request = string.Concat(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"pipeline\",\"arguments\":",
            argumentsJson,
            "}}");

        string response = SendRequest(request);
        return ParseToolResultText(response);
    }

    /// <summary>
    /// Sends a raw JSON-RPC request to the MCP server and returns the raw response line.
    /// </summary>
    private static string SendRequest(string request)
    {
        ProcessStartInfo psi;
        if (s_mcpBinary.Length > 0 && File.Exists(s_mcpBinary))
        {
            psi = new ProcessStartInfo(s_mcpBinary)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
        }
        else
        {
            string projectDir = Path.GetFullPath(Path.Combine(
                TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "fred-mcp"));
            psi = new ProcessStartInfo("dotnet")
            {
                ArgumentList = { "run", "--project", projectDir, "-c", "Release", "--no-build" },
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
        }

        using var proc = Process.Start(psi)!;

        // Drain stderr asynchronously to prevent deadlock
        _ = proc.StandardError.ReadToEndAsync();

        // Send initialize handshake
        proc.StandardInput.WriteLine(
            "{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{},\"clientInfo\":{\"name\":\"test\",\"version\":\"1.0\"}}}");
        proc.StandardInput.Flush();

        // Read initialize response
        string? initResponse = proc.StandardOutput.ReadLine();
        Assert.That(initResponse, Is.Not.Null, "Expected initialize response from MCP server");

        // Send the actual request
        proc.StandardInput.WriteLine(request);
        proc.StandardInput.Flush();

        // Read the response
        string? response = proc.StandardOutput.ReadLine();
        Assert.That(response, Is.Not.Null, "Expected tool call response from MCP server");

        // Close stdin to let the process exit
        proc.StandardInput.Close();
        bool exited = proc.WaitForExit(15_000);
        if (!exited)
        {
            proc.Kill();
            Assert.Fail("MCP server process did not exit within timeout");
        }

        return response!;
    }

    /// <summary>
    /// Parses the text content from a JSON-RPC tool result response.
    /// </summary>
    private static string ParseToolResultText(string response)
    {
        using var doc = JsonDocument.Parse(response);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var errorEl))
        {
            string errorMsg = errorEl.TryGetProperty("message", out var msgEl)
                ? msgEl.GetString() ?? "" : "";
            Assert.Fail("JSON-RPC error: " + errorMsg);
        }

        if (!root.TryGetProperty("result", out var resultEl))
            Assert.Fail("Unexpected response format: " + response);

        if (resultEl.TryGetProperty("isError", out var isErrEl) && isErrEl.GetBoolean())
        {
            string errText = resultEl.GetProperty("content")[0]
                .GetProperty("text").GetString() ?? "";
            Assert.Fail("Tool error: " + errText);
        }

        var content = resultEl.GetProperty("content");
        return content[0].GetProperty("text").GetString() ?? "";
    }

    /// <summary>
    /// Builds a pipeline arguments JSON string with the given stages and flags.
    /// Uses JsonSerializer to properly escape file paths and special characters.
    /// </summary>
    private static string BuildPipelineArgs(
        object[] stages,
        bool inPlace = false,
        bool dryRun = false,
        string? backup = null)
    {
        var dict = new Dictionary<string, object> { ["stages"] = stages };
        if (inPlace) dict["inPlace"] = true;
        if (dryRun) dict["dryRun"] = true;
        if (backup != null) dict["backup"] = backup;
        return JsonSerializer.Serialize(dict);
    }

    private static Dictionary<string, object> FindStage(string path, string name)
    {
        return new Dictionary<string, object>
        {
            ["tool"] = "find",
            ["path"] = path,
            ["name"] = name,
            ["type"] = "f",
        };
    }

    private static Dictionary<string, object> EditStage(
        string old, string @new, bool replaceAll = false)
    {
        var d = new Dictionary<string, object>
        {
            ["tool"] = "edit",
            ["old"] = old,
            ["new"] = @new,
        };
        if (replaceAll) d["replaceAll"] = true;
        return d;
    }

    private static Dictionary<string, object> GrepStage(string pattern)
    {
        return new Dictionary<string, object>
        {
            ["tool"] = "grep",
            ["pattern"] = pattern,
        };
    }

    // -----------------------------------------------------------------------
    // 1. Edit replaces first occurrence only (default)
    // -----------------------------------------------------------------------

    [Test]
    public void Edit_DefaultReplacesFirstOccurrenceOnly()
    {
        string dir = CreateTempDir();
        WriteTempFile(dir, "test.txt", "foo bar foo baz foo\n");

        string args = BuildPipelineArgs(
            new object[] { FindStage(dir, "test.txt"), EditStage("foo", "REPLACED") },
            dryRun: true);

        string resultText = CallPipelineTool(args);
        using var resultDoc = JsonDocument.Parse(resultText);
        var root = resultDoc.RootElement;

        Assert.That(root.GetProperty("filesMatched").GetInt32(), Is.EqualTo(1));

        var lines = root.GetProperty("matches")[0].GetProperty("lines");
        Assert.That(lines.GetArrayLength(), Is.GreaterThan(0));

        string replacement = lines[0].GetProperty("replacement").GetString()!;
        Assert.That(replacement, Is.EqualTo("REPLACED bar foo baz foo"));
    }

    // -----------------------------------------------------------------------
    // 2. Edit with replaceAll replaces all occurrences
    // -----------------------------------------------------------------------

    [Test]
    public void Edit_ReplaceAllReplacesAllOccurrences()
    {
        string dir = CreateTempDir();
        WriteTempFile(dir, "test.txt", "foo bar foo baz foo\n");

        string args = BuildPipelineArgs(
            new object[] { FindStage(dir, "test.txt"), EditStage("foo", "X", replaceAll: true) },
            dryRun: true);

        string resultText = CallPipelineTool(args);
        using var resultDoc = JsonDocument.Parse(resultText);
        var root = resultDoc.RootElement;

        Assert.That(root.GetProperty("filesMatched").GetInt32(), Is.EqualTo(1));

        var lines = root.GetProperty("matches")[0].GetProperty("lines");
        Assert.That(lines.GetArrayLength(), Is.GreaterThan(0));

        string replacement = lines[0].GetProperty("replacement").GetString()!;
        Assert.That(replacement, Is.EqualTo("X bar X baz X"));
    }

    // -----------------------------------------------------------------------
    // 3. Edit with no match leaves content unchanged
    // -----------------------------------------------------------------------

    [Test]
    public void Edit_NoMatchLeavesContentUnchanged()
    {
        string dir = CreateTempDir();
        string filePath = WriteTempFile(dir, "test.txt", "hello world\n");

        string args = BuildPipelineArgs(
            new object[] { FindStage(dir, "test.txt"), EditStage("NOTFOUND", "replaced") },
            dryRun: true);

        string resultText = CallPipelineTool(args);
        using var resultDoc = JsonDocument.Parse(resultText);
        var root = resultDoc.RootElement;

        // File is searched but content is unchanged -- no matches reported
        Assert.That(root.GetProperty("filesSearched").GetInt32(), Is.EqualTo(1));
        Assert.That(root.GetProperty("filesMatched").GetInt32(), Is.EqualTo(1));

        // No changed lines means no match entries in the matches array
        var matches = root.GetProperty("matches");
        Assert.That(matches.GetArrayLength(), Is.EqualTo(0));

        // Original file is untouched
        string currentContent = File.ReadAllText(filePath);
        Assert.That(currentContent, Is.EqualTo("hello world\n"));
    }

    // -----------------------------------------------------------------------
    // 4. Edit in pipeline: find -> grep -> edit with inPlace
    // -----------------------------------------------------------------------

    [Test]
    public void Edit_FindGrepEditInPlace_ModifiesFile()
    {
        string dir = CreateTempDir();
        string filePath = WriteTempFile(dir, "data.txt",
            "error: something went wrong\ninfo: all good\nerror: another problem\n");

        string args = BuildPipelineArgs(
            new object[]
            {
                FindStage(dir, "data.txt"),
                GrepStage("error"),
                EditStage("error", "WARNING", replaceAll: true),
            },
            inPlace: true);

        string resultText = CallPipelineTool(args);
        using var resultDoc = JsonDocument.Parse(resultText);
        var root = resultDoc.RootElement;

        Assert.That(root.GetProperty("filesMatched").GetInt32(), Is.EqualTo(1));
        Assert.That(root.GetProperty("filesModified").GetInt32(), Is.EqualTo(1));

        // Verify file was modified in-place
        string modifiedContent = File.ReadAllText(filePath);
        Assert.That(modifiedContent, Does.Contain("WARNING"));
        Assert.That(modifiedContent, Does.Not.Contain("error"));
    }

    // -----------------------------------------------------------------------
    // 5. Edit in pipeline: find -> edit with dryRun shows changes
    // -----------------------------------------------------------------------

    [Test]
    public void Edit_FindEditDryRun_ShowsChangesWithoutModifying()
    {
        string dir = CreateTempDir();
        string filePath = WriteTempFile(dir, "readme.txt", "Hello World\nGoodbye World\n");
        string originalContent = File.ReadAllText(filePath);

        string args = BuildPipelineArgs(
            new object[] { FindStage(dir, "readme.txt"), EditStage("World", "Universe") },
            dryRun: true);

        string resultText = CallPipelineTool(args);
        using var resultDoc = JsonDocument.Parse(resultText);
        var root = resultDoc.RootElement;

        Assert.That(root.GetProperty("filesMatched").GetInt32(), Is.EqualTo(1));

        // File should NOT be modified (dry run)
        string currentContent = File.ReadAllText(filePath);
        Assert.That(currentContent, Is.EqualTo(originalContent));

        // Response should show what would change
        var lines = root.GetProperty("matches")[0].GetProperty("lines");
        Assert.That(lines.GetArrayLength(), Is.GreaterThan(0));

        string replacement = lines[0].GetProperty("replacement").GetString()!;
        Assert.That(replacement, Does.Contain("Universe"));
    }

    // -----------------------------------------------------------------------
    // 6. Edit with multi-line old/new strings
    // -----------------------------------------------------------------------

    [Test]
    public void Edit_MultiLineStrings()
    {
        string dir = CreateTempDir();
        WriteTempFile(dir, "multi.txt", "line1\nline2\nline3\n");

        string args = BuildPipelineArgs(
            new object[]
            {
                FindStage(dir, "multi.txt"),
                EditStage("line1\nline2", "replaced1\nreplaced2"),
            },
            dryRun: true);

        string resultText = CallPipelineTool(args);
        using var resultDoc = JsonDocument.Parse(resultText);
        var root = resultDoc.RootElement;

        Assert.That(root.GetProperty("filesMatched").GetInt32(), Is.EqualTo(1));

        var lines = root.GetProperty("matches")[0].GetProperty("lines");
        Assert.That(lines.GetArrayLength(), Is.GreaterThan(0));
    }

    // -----------------------------------------------------------------------
    // 7. Edit with special regex characters in old string (proves literal)
    // -----------------------------------------------------------------------

    [Test]
    public void Edit_SpecialRegexCharsAreLiteral()
    {
        string dir = CreateTempDir();
        WriteTempFile(dir, "special.txt", "price is $100.00 (USD)\n");

        string args = BuildPipelineArgs(
            new object[]
            {
                FindStage(dir, "special.txt"),
                EditStage("$100.00 (USD)", "200.00 EUR"),
            },
            dryRun: true);

        string resultText = CallPipelineTool(args);
        using var resultDoc = JsonDocument.Parse(resultText);
        var root = resultDoc.RootElement;

        Assert.That(root.GetProperty("filesMatched").GetInt32(), Is.EqualTo(1));

        var lines = root.GetProperty("matches")[0].GetProperty("lines");
        Assert.That(lines.GetArrayLength(), Is.GreaterThan(0));

        string replacement = lines[0].GetProperty("replacement").GetString()!;
        Assert.That(replacement, Is.EqualTo("price is 200.00 EUR"));
    }

    [Test]
    public void Edit_MoreRegexMetacharacters_TreatedLiterally()
    {
        string dir = CreateTempDir();
        string content = "match: [a-z]+ and .*?\\d{3}\n";
        WriteTempFile(dir, "meta.txt", content);

        string args = BuildPipelineArgs(
            new object[]
            {
                FindStage(dir, "meta.txt"),
                EditStage("[a-z]+ and .*?\\d{3}", "LITERAL"),
            },
            dryRun: true);

        string resultText = CallPipelineTool(args);
        using var resultDoc = JsonDocument.Parse(resultText);
        var root = resultDoc.RootElement;

        Assert.That(root.GetProperty("filesMatched").GetInt32(), Is.EqualTo(1));

        var lines = root.GetProperty("matches")[0].GetProperty("lines");
        Assert.That(lines.GetArrayLength(), Is.GreaterThan(0));

        string replacement = lines[0].GetProperty("replacement").GetString()!;
        Assert.That(replacement, Is.EqualTo("match: LITERAL"));
    }

    // -----------------------------------------------------------------------
    // 8. Multiple edit stages chained
    // -----------------------------------------------------------------------

    [Test]
    public void Edit_MultipleStagesChained()
    {
        string dir = CreateTempDir();
        WriteTempFile(dir, "chain.txt", "apple banana cherry\n");

        string args = BuildPipelineArgs(
            new object[]
            {
                FindStage(dir, "chain.txt"),
                EditStage("apple", "APPLE"),
                EditStage("banana", "BANANA"),
                EditStage("cherry", "CHERRY"),
            },
            dryRun: true);

        string resultText = CallPipelineTool(args);
        using var resultDoc = JsonDocument.Parse(resultText);
        var root = resultDoc.RootElement;

        Assert.That(root.GetProperty("filesMatched").GetInt32(), Is.EqualTo(1));

        var lines = root.GetProperty("matches")[0].GetProperty("lines");
        Assert.That(lines.GetArrayLength(), Is.GreaterThan(0));

        string replacement = lines[0].GetProperty("replacement").GetString()!;
        Assert.That(replacement, Is.EqualTo("APPLE BANANA CHERRY"));
    }

    [Test]
    public void Edit_MultipleStagesWithReplaceAll()
    {
        string dir = CreateTempDir();
        WriteTempFile(dir, "multi_edit.txt", "aaa bbb aaa ccc bbb\n");

        string args = BuildPipelineArgs(
            new object[]
            {
                FindStage(dir, "multi_edit.txt"),
                EditStage("aaa", "XXX", replaceAll: true),
                EditStage("bbb", "YYY", replaceAll: true),
            },
            dryRun: true);

        string resultText = CallPipelineTool(args);
        using var resultDoc = JsonDocument.Parse(resultText);
        var root = resultDoc.RootElement;

        var lines = root.GetProperty("matches")[0].GetProperty("lines");
        Assert.That(lines.GetArrayLength(), Is.GreaterThan(0));

        string replacement = lines[0].GetProperty("replacement").GetString()!;
        Assert.That(replacement, Is.EqualTo("XXX YYY XXX ccc YYY"));
    }

    // -----------------------------------------------------------------------
    // 9. Edit in-place actually modifies the file
    // -----------------------------------------------------------------------

    [Test]
    public void Edit_InPlace_FileIsActuallyModified()
    {
        string dir = CreateTempDir();
        string filePath = WriteTempFile(dir, "inplace.txt", "old content here\n");

        string args = BuildPipelineArgs(
            new object[] { FindStage(dir, "inplace.txt"), EditStage("old content", "new content") },
            inPlace: true);

        string resultText = CallPipelineTool(args);
        using var resultDoc = JsonDocument.Parse(resultText);
        var root = resultDoc.RootElement;

        Assert.That(root.GetProperty("filesModified").GetInt32(), Is.EqualTo(1));

        string modifiedContent = File.ReadAllText(filePath);
        Assert.That(modifiedContent, Is.EqualTo("new content here\n"));
    }

    // -----------------------------------------------------------------------
    // 10. Edit with empty new string (deletion)
    // -----------------------------------------------------------------------

    [Test]
    public void Edit_EmptyNewString_DeletesOldText()
    {
        string dir = CreateTempDir();
        WriteTempFile(dir, "delete.txt", "keep DELETE_ME keep\n");

        string args = BuildPipelineArgs(
            new object[] { FindStage(dir, "delete.txt"), EditStage("DELETE_ME ", "") },
            dryRun: true);

        string resultText = CallPipelineTool(args);
        using var resultDoc = JsonDocument.Parse(resultText);
        var root = resultDoc.RootElement;

        var lines = root.GetProperty("matches")[0].GetProperty("lines");
        Assert.That(lines.GetArrayLength(), Is.GreaterThan(0));

        string replacement = lines[0].GetProperty("replacement").GetString()!;
        Assert.That(replacement, Is.EqualTo("keep keep"));
    }

    // -----------------------------------------------------------------------
    // 11. Edit in-place with backup suffix
    // -----------------------------------------------------------------------

    [Test]
    public void Edit_InPlaceWithBackup_CreatesBackupFile()
    {
        string dir = CreateTempDir();
        string filePath = WriteTempFile(dir, "backup_test.txt", "original text\n");
        string backupPath = filePath + ".bak";

        string args = BuildPipelineArgs(
            new object[] { FindStage(dir, "backup_test.txt"), EditStage("original", "modified") },
            inPlace: true,
            backup: ".bak");

        string resultText = CallPipelineTool(args);
        using var resultDoc = JsonDocument.Parse(resultText);
        var root = resultDoc.RootElement;

        Assert.That(root.GetProperty("filesModified").GetInt32(), Is.EqualTo(1));

        // Modified file has new content
        string modifiedContent = File.ReadAllText(filePath);
        Assert.That(modifiedContent, Is.EqualTo("modified text\n"));

        // Backup file has original content
        Assert.That(File.Exists(backupPath), Is.True,
            "Backup file should exist at " + backupPath);
        string backupContent = File.ReadAllText(backupPath);
        Assert.That(backupContent, Is.EqualTo("original text\n"));

        _tempFiles.Add(backupPath);
    }
}
