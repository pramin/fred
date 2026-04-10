using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FredDotNet;

namespace FredMcp;

/// <summary>
/// MCP (Model Context Protocol) server for Fred text processing tools.
/// Communicates over stdin/stdout using JSON-RPC 2.0, newline-delimited.
/// </summary>
public static class McpServer
{
    private const string ServerName = "freds-mcp";
    private const string ServerVersion = "1.0.0";
    private const string ProtocolVersion = "2024-11-05";

    // LRU caches for compiled scripts and find results
    private static readonly LruCache<string, SedScript> s_sedCache = new(64);
    private static readonly LruCache<string, GrepScript> s_grepCache = new(64);
    private static readonly LruCache<string, AwkScript> s_awkCache = new(64);
    private static readonly LruCache<string, List<string>> s_findCache = new(16);

    private static readonly string s_stateDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".fred-mcp");

    public static async Task<int> Main(string[] args)
    {
        string? cmd = args.Length > 0 ? args[0] : null;

        switch (cmd)
        {
            case "install":
                // Update tool, configure clients, schedule future update checks
                return Install(args.Length > 1 ? args[1] : "all", updateTool: true);

            case "server":
                Console.Error.WriteLine($"{ServerName} v{ServerVersion} starting...");
                _ = Task.Run(CheckForUpdate);
                try
                {
                    await RunMainLoop();
                    return 0;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"{ServerName}: fatal error: {ex.Message}");
                    return 1;
                }

            case null:
            case "help":
            case "--help":
            case "-h":
            case "-?":
            case "--?":
            case "/?":
            case "/h":
            case "/help":
                PrintHelp();
                return 0;

            default:
                Console.Error.WriteLine($"{ServerName}: unknown command: {cmd}");
                Console.Error.WriteLine();
                PrintHelp();
                return 1;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine($"{ServerName} v{ServerVersion} — MCP server for find/grep/sed/awk");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine($"  {ServerName} install [claude|vscode|all]");
        Console.WriteLine("      Update tool, configure MCP clients, schedule daily update checks.");
        Console.WriteLine($"  {ServerName} server");
        Console.WriteLine("      Run the JSON-RPC server on stdin/stdout (MCP clients invoke this).");
        Console.WriteLine($"  {ServerName} help");
        Console.WriteLine("      Print this help.");
        Console.WriteLine();
        Console.WriteLine("First-time setup:");
        Console.WriteLine($"  dotnet tool install -g FredsMCP");
        Console.WriteLine($"  {ServerName} install");
    }

    // -------------------------------------------------------------------------
    // --install: update tool + configure MCP for claude/vscode/copilot
    // -------------------------------------------------------------------------

    private static int Install(string target, bool updateTool)
    {
        if (updateTool)
        {
            Console.WriteLine($"fred-mcp install: updating to latest version...");

            // Update the tool (no-op if already latest)
            var update = Process.Start(new ProcessStartInfo("dotnet", "tool update -g FredsMCP")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (update != null)
            {
                string output = update.StandardOutput.ReadToEnd();
                string error = update.StandardError.ReadToEnd();
                update.WaitForExit();
                if (output.Length > 0) Console.WriteLine(output.TrimEnd());
                if (update.ExitCode != 0 && error.Length > 0) Console.Error.WriteLine(error.TrimEnd());
            }
        }

        string command = "freds-mcp";

        bool all = target == "all";
        bool any = false;

        if (all || target == "claude")
        {
            any = true;
            InstallClaude(command);
        }
        if (all || target == "vscode")
        {
            any = true;
            InstallVSCode(command);
        }

        if (!any)
        {
            Console.Error.WriteLine($"Unknown target: {target}");
            Console.Error.WriteLine("Usage: fred-mcp --install [claude|vscode|all]");
            return 1;
        }

        return 0;
    }

    private static void InstallClaude(string command)
    {
        // Claude Code: ~/.claude.json with "mcpServers" key
        string configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");

        JsonElement root = default;

        if (File.Exists(configPath))
        {
            try
            {
                string existing = File.ReadAllText(configPath);
                using var doc = JsonDocument.Parse(existing);
                root = doc.RootElement.Clone();
            }
            catch { }
        }

        // Build new config preserving existing content
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();

            // Copy existing properties
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in root.EnumerateObject())
                {
                    if (prop.Name == "mcpServers") continue; // we'll write our own
                    prop.WriteTo(writer);
                }
            }

            // Write mcpServers, merging with existing
            writer.WritePropertyName("mcpServers");
            writer.WriteStartObject();

            // Copy existing servers
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("mcpServers", out var existingServers) &&
                existingServers.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in existingServers.EnumerateObject())
                {
                    if (prop.Name == "fred") continue; // we'll overwrite
                    prop.WriteTo(writer);
                }
            }

            // Add/update fred
            writer.WritePropertyName("fred");
            writer.WriteStartObject();
            writer.WriteString("command", command);
            writer.WritePropertyName("args");
            writer.WriteStartArray();
            writer.WriteStringValue("server");
            writer.WriteEndArray();
            writer.WriteEndObject();

            writer.WriteEndObject(); // mcpServers
            writer.WriteEndObject(); // root
        }

        File.WriteAllText(configPath, Encoding.UTF8.GetString(ms.ToArray()));
        Console.WriteLine($"  Claude Code: configured in {configPath}");
    }

    private static void InstallVSCode(string command)
    {
        // VS Code: .vscode/mcp.json with "servers" key (workspace-level)
        // Also check user-level settings
        string workspacePath = Path.Combine(Directory.GetCurrentDirectory(), ".vscode", "mcp.json");

        JsonElement root = default;

        if (File.Exists(workspacePath))
        {
            try
            {
                string existing = File.ReadAllText(workspacePath);
                using var doc = JsonDocument.Parse(existing);
                root = doc.RootElement.Clone();
            }
            catch { }
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(workspacePath)!);
        }

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();

            // Copy existing properties
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in root.EnumerateObject())
                {
                    if (prop.Name == "servers") continue;
                    prop.WriteTo(writer);
                }
            }

            writer.WritePropertyName("servers");
            writer.WriteStartObject();

            // Copy existing servers
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("servers", out var existingServers) &&
                existingServers.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in existingServers.EnumerateObject())
                {
                    if (prop.Name == "fred") continue;
                    prop.WriteTo(writer);
                }
            }

            writer.WritePropertyName("fred");
            writer.WriteStartObject();
            writer.WriteString("type", "stdio");
            writer.WriteString("command", command);
            writer.WritePropertyName("args");
            writer.WriteStartArray();
            writer.WriteStringValue("server");
            writer.WriteEndArray();
            writer.WriteEndObject();

            writer.WriteEndObject(); // servers
            writer.WriteEndObject(); // root
        }

        File.WriteAllText(workspacePath, Encoding.UTF8.GetString(ms.ToArray()));
        Console.WriteLine($"  VS Code: configured in {workspacePath}");
    }

    // -------------------------------------------------------------------------
    // Auto-update: check once per day, run in background on startup
    // -------------------------------------------------------------------------

    private static void CheckForUpdate()
    {
        try
        {
            Directory.CreateDirectory(s_stateDir);
            string timestampFile = Path.Combine(s_stateDir, "last-update-check");

            // Check if we already checked today
            if (File.Exists(timestampFile))
            {
                var lastCheck = File.GetLastWriteTimeUtc(timestampFile);
                if ((DateTime.UtcNow - lastCheck).TotalHours < 24)
                    return;
            }

            Console.Error.WriteLine($"{ServerName}: checking for updates...");

            var proc = Process.Start(new ProcessStartInfo("dotnet", "tool update -g FredsMCP")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });

            if (proc != null)
            {
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit();

                if (output.Contains("was updated") || output.Contains("was reinstalled"))
                    Console.Error.WriteLine($"{ServerName}: updated! Restart to use new version.");
                else
                    Console.Error.WriteLine($"{ServerName}: up to date.");
            }

            // Touch timestamp file
            File.WriteAllText(timestampFile, DateTime.UtcNow.ToString("o"));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{ServerName}: update check failed: {ex.Message}");
        }
    }

    private static async Task RunMainLoop()
    {
        using var stdin = Console.OpenStandardInput();
        using var reader = new StreamReader(stdin, Encoding.UTF8);

        while (true)
        {
            string? line = await reader.ReadLineAsync();
            if (line == null)
                break; // EOF

            if (line.Length == 0)
                continue;

            Console.Error.WriteLine($"<-- {line}");

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                string? method = null;
                if (root.TryGetProperty("method", out var methodEl))
                    method = methodEl.GetString();

                JsonElement? id = null;
                if (root.TryGetProperty("id", out var idEl))
                    id = idEl.Clone();

                JsonElement? paramsEl = null;
                if (root.TryGetProperty("params", out var pe))
                    paramsEl = pe.Clone();

                if (method == null && id != null)
                {
                    // Response to something we sent (shouldn't happen for server)
                    continue;
                }

                string? response = method switch
                {
                    "initialize" => HandleInitialize(id),
                    "notifications/initialized" => null, // notification, no response
                    "tools/list" => HandleToolsList(id),
                    "tools/call" => HandleToolsCall(id, paramsEl),
                    "ping" => HandlePing(id),
                    _ => HandleUnknownMethod(id, method),
                };

                if (response != null)
                {
                    Console.Error.WriteLine($"--> {response}");
                    Console.Out.WriteLine(response);
                    Console.Out.Flush();
                }
            }
            catch (JsonException ex)
            {
                var errorResp = MakeErrorResponse(null, -32700, $"Parse error: {ex.Message}");
                Console.Out.WriteLine(errorResp);
                Console.Out.Flush();
            }
        }
    }

    private static string HandleInitialize(JsonElement? id)
    {
        return MakeResponse(id, $$"""
        {
            "protocolVersion": "{{ProtocolVersion}}",
            "capabilities": {
                "tools": {}
            },
            "serverInfo": {
                "name": "{{ServerName}}",
                "version": "{{ServerVersion}}"
            }
        }
        """);
    }

    private static string HandlePing(JsonElement? id)
    {
        return MakeResponse(id, "{}");
    }

    private static string HandleToolsList(JsonElement? id)
    {
        string toolsJson = $$"""
        {
            "tools": [
                {
                    "name": "find",
                    "description": "Find files in a directory tree matching name patterns, type, size, and other criteria",
                    "inputSchema": {
                        "type": "object",
                        "properties": {
                            "path": {"type": "string", "description": "Starting directory path", "default": "."},
                            "name": {"type": "string", "description": "Filename glob pattern (e.g., '*.cs')"},
                            "iname": {"type": "string", "description": "Case-insensitive filename glob pattern"},
                            "path_pattern": {"type": "string", "description": "Full path glob pattern (-path)"},
                            "type": {"type": "string", "enum": ["f", "d", "l"], "description": "File type: f=file, d=directory, l=symlink"},
                            "size": {"type": "string", "description": "Size filter, e.g. '+100k', '-1M', '0c' (suffixes: c=bytes, k=KiB, M=MiB, G=GiB)"},
                            "maxdepth": {"type": "integer", "description": "Maximum directory depth"},
                            "mindepth": {"type": "integer", "description": "Minimum directory depth"},
                            "mtime": {"type": "string", "description": "Modification time in days, e.g. '+7', '-1', '0'"},
                            "mmin": {"type": "string", "description": "Modification time in minutes, e.g. '+60', '-5'"},
                            "newer": {"type": "string", "description": "File path; match files newer than this file"},
                            "empty": {"type": "boolean", "description": "Match empty files/directories"},
                            "prune": {"type": "array", "items": {"type": "string"}, "description": "Directory names to skip (e.g. ['node_modules', '.git'])"},
                            "print0": {"type": "boolean", "description": "Null-terminated output"}
                        },
                        "required": ["path"]
                    }
                },
                {
                    "name": "grep",
                    "description": "Search for pattern matches in files. Returns matching lines with file paths and line numbers.",
                    "inputSchema": {
                        "type": "object",
                        "properties": {
                            "pattern": {"type": "string", "description": "Search pattern (regex)"},
                            "path": {"type": "string", "description": "File or directory to search"},
                            "glob": {"type": "string", "description": "Filename filter glob (e.g., '*.cs')"},
                            "ignoreCase": {"type": "boolean", "default": false},
                            "invertMatch": {"type": "boolean", "description": "Select non-matching lines (-v)"},
                            "count": {"type": "boolean", "description": "Print count of matching lines per file (-c)"},
                            "filesWithMatches": {"type": "boolean", "description": "Print only filenames containing matches (-l)"},
                            "onlyMatching": {"type": "boolean", "description": "Print only the matched parts (-o)"},
                            "wholeWord": {"type": "boolean", "description": "Match whole words only (-w)"},
                            "fixedStrings": {"type": "boolean", "description": "Treat pattern as literal string, not regex (-F)"},
                            "useERE": {"type": "boolean", "description": "Use extended regular expressions (-E)"},
                            "contextLines": {"type": "integer", "description": "Lines of context before and after each match (-C)"},
                            "beforeContext": {"type": "integer", "description": "Lines of context before each match (-B)"},
                            "afterContext": {"type": "integer", "description": "Lines of context after each match (-A)"},
                            "maxResults": {"type": "integer", "description": "Maximum number of matching lines to return", "default": 100}
                        },
                        "required": ["pattern", "path"]
                    }
                },
                {
                    "name": "sed",
                    "description": "Apply sed script transformations to file contents. Can modify files in-place or return transformed content.",
                    "inputSchema": {
                        "type": "object",
                        "properties": {
                            "script": {"type": "string", "description": "Sed script (e.g., 's/old/new/g')"},
                            "file": {"type": "string", "description": "Single file to transform"},
                            "files": {"type": "array", "items": {"type": "string"}, "description": "Multiple files to transform"},
                            "suppressDefault": {"type": "boolean", "description": "Suppress default output, like sed -n"},
                            "useEre": {"type": "boolean", "description": "Use extended regular expressions (-E/-r)"},
                            "inPlace": {"type": "boolean", "description": "Write changes back to file", "default": false},
                            "backup": {"type": "string", "description": "Backup suffix (e.g., '.bak') when editing in-place"},
                            "dryRun": {"type": "boolean", "description": "Show diff of what would change without modifying", "default": false}
                        },
                        "required": ["script"]
                    }
                },
                {
                    "name": "awk",
                    "description": "Process file contents with an AWK program. Useful for extracting fields, computing statistics, reformatting data.",
                    "inputSchema": {
                        "type": "object",
                        "properties": {
                            "program": {"type": "string", "description": "AWK program (e.g., '{print $1}')"},
                            "file": {"type": "string", "description": "Single file to process"},
                            "files": {"type": "array", "items": {"type": "string"}, "description": "Multiple files to process"},
                            "fieldSeparator": {"type": "string", "description": "Field separator character"},
                            "variables": {"type": "object", "additionalProperties": {"type": "string"}, "description": "Variables to set before execution (-v var=val)"}
                        },
                        "required": ["program"]
                    }
                },
                {
                    "name": "pipeline",
                    "description": "Execute an array of stages: find, grep, sed, awk. Each stage feeds its output to the next. The first stage can be a find (returns file list) or any text stage reading from input. Supports in-place file editing and dry-run diffs.",
                    "inputSchema": {
                        "type": "object",
                        "properties": {
                            "stages": {
                                "type": "array",
                                "description": "Ordered array of processing stages. Each stage has a 'tool' key and tool-specific options.",
                                "items": {
                                    "type": "object",
                                    "properties": {
                                        "tool": {"type": "string", "enum": ["find", "grep", "sed", "awk", "edit"], "description": "Which tool to run at this stage"},
                                        "path": {"type": "string", "description": "find: starting directory"},
                                        "name": {"type": "string", "description": "find: filename glob pattern"},
                                        "iname": {"type": "string", "description": "find: case-insensitive glob"},
                                        "type": {"type": "string", "enum": ["f", "d", "l"], "description": "find: file type"},
                                        "size": {"type": "string", "description": "find: size filter"},
                                        "maxdepth": {"type": "integer", "description": "find: max directory depth"},
                                        "mindepth": {"type": "integer", "description": "find: min directory depth"},
                                        "prune": {"type": "array", "items": {"type": "string"}, "description": "find: directories to skip"},
                                        "pattern": {"type": "string", "description": "grep: search pattern"},
                                        "ignoreCase": {"type": "boolean", "description": "grep: case-insensitive"},
                                        "invertMatch": {"type": "boolean", "description": "grep: invert match"},
                                        "wholeWord": {"type": "boolean", "description": "grep: whole words only"},
                                        "fixedStrings": {"type": "boolean", "description": "grep: literal string match"},
                                        "useERE": {"type": "boolean", "description": "grep/sed: extended regex"},
                                        "script": {"type": "string", "description": "sed: transformation script"},
                                        "suppressDefault": {"type": "boolean", "description": "sed: suppress default output (-n)"},
                                        "program": {"type": "string", "description": "awk: AWK program"},
                                        "fieldSeparator": {"type": "string", "description": "awk: field separator"},
                                        "variables": {"type": "object", "additionalProperties": {"type": "string"}, "description": "awk: variables"},
                                        "old": {"type": "string", "description": "edit: exact string to find (literal, no regex)"},
                                        "new": {"type": "string", "description": "edit: replacement string"},
                                        "replaceAll": {"type": "boolean", "description": "edit: replace all occurrences (default: first only)", "default": false}
                                    },
                                    "required": ["tool"]
                                }
                            },
                            "inPlace": {"type": "boolean", "description": "Write changes back to files found by find stage", "default": false},
                            "backup": {"type": "string", "description": "Backup suffix when editing in-place (e.g. '.bak')"},
                            "dryRun": {"type": "boolean", "description": "Show unified diff without modifying files", "default": false}
                        },
                        "required": ["stages"]
                    }
                }
            ]
        }
        """;

        return MakeResponse(id, toolsJson);
    }

    private static string HandleToolsCall(JsonElement? id, JsonElement? paramsEl)
    {
        if (paramsEl == null)
            return MakeErrorResponse(id, -32602, "Missing params");

        var p = paramsEl.Value;

        string? toolName = null;
        if (p.TryGetProperty("name", out var nameEl))
            toolName = nameEl.GetString();

        JsonElement? arguments = null;
        if (p.TryGetProperty("arguments", out var argsEl))
            arguments = argsEl;

        if (toolName == null)
            return MakeErrorResponse(id, -32602, "Missing tool name");

        try
        {
            string resultText = toolName switch
            {
                "find" => ExecuteFind(arguments),
                "grep" => ExecuteGrep(arguments),
                "sed" => ExecuteSed(arguments),
                "awk" => ExecuteAwk(arguments),
                "pipeline" => ExecutePipeline(arguments),
                _ => throw new InvalidOperationException($"Unknown tool: {toolName}"),
            };

            return MakeToolResult(id, resultText);
        }
        catch (Exception ex)
        {
            return MakeToolError(id, ex.Message);
        }
    }

    /// <summary>
    /// Builds a cache key for find operations from the argument list.
    /// </summary>
    private static string BuildFindCacheKey(List<string> findArgs)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < findArgs.Count; i++)
        {
            if (i > 0) sb.Append('\0');
            sb.Append(findArgs[i]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Executes a find with caching. Returns cached results if available.
    /// </summary>
    private static List<string> ExecuteFindCached(List<string> findArgs)
    {
        string cacheKey = BuildFindCacheKey(findArgs);

        if (s_findCache.TryGet(cacheKey, out var cached))
        {
            Console.Error.WriteLine($"{ServerName}: find cache hit for '{cacheKey.Replace('\0', ' ')}'");
            return cached;
        }

        Console.Error.WriteLine($"{ServerName}: find cache miss, walking '{cacheKey.Replace('\0', ' ')}'");
        var script = FindEngine.Compile(findArgs.ToArray());
        var results = script.Execute();
        s_findCache.Set(cacheKey, results);
        return results;
    }

    /// <summary>
    /// Builds find arguments from common find parameters (path, name, iname, type, size, etc.).
    /// Supports prune by generating: ( -name "dir1" -o -name "dir2" ) -prune -o ... -print
    /// </summary>
    private static List<string> BuildFindArgs(
        string path,
        string? name,
        string? iname,
        string? pathPattern,
        string? type,
        string? size,
        int? maxdepth,
        int? mindepth,
        string? mtime,
        string? mmin,
        string? newer,
        bool empty,
        string[]? prune,
        bool print0)
    {
        var findArgs = new List<string> { path };

        // Global options must come before predicates
        if (maxdepth != null) { findArgs.Add("-maxdepth"); findArgs.Add(maxdepth.Value.ToString()); }
        if (mindepth != null) { findArgs.Add("-mindepth"); findArgs.Add(mindepth.Value.ToString()); }

        // Prune expression: ( -name "dir1" -o -name "dir2" ) -prune -o <rest> -print
        bool hasPrune = prune != null && prune.Length > 0;
        if (hasPrune)
        {
            findArgs.Add("(");
            for (int i = 0; i < prune!.Length; i++)
            {
                if (i > 0) findArgs.Add("-o");
                findArgs.Add("-name");
                findArgs.Add(prune[i]);
            }
            findArgs.Add(")");
            findArgs.Add("-prune");
            findArgs.Add("-o");
        }

        // Regular predicates
        if (name != null) { findArgs.Add("-name"); findArgs.Add(name); }
        if (iname != null) { findArgs.Add("-iname"); findArgs.Add(iname); }
        if (pathPattern != null) { findArgs.Add("-path"); findArgs.Add(pathPattern); }
        if (type != null) { findArgs.Add("-type"); findArgs.Add(type); }
        if (size != null) { findArgs.Add("-size"); findArgs.Add(size); }
        if (mtime != null) { findArgs.Add("-mtime"); findArgs.Add(mtime); }
        if (mmin != null) { findArgs.Add("-mmin"); findArgs.Add(mmin); }
        if (newer != null) { findArgs.Add("-newer"); findArgs.Add(newer); }
        if (empty) { findArgs.Add("-empty"); }

        // When pruning, we need an explicit -print or -print0 action
        if (hasPrune)
        {
            findArgs.Add(print0 ? "-print0" : "-print");
        }
        else if (print0)
        {
            findArgs.Add("-print0");
        }

        return findArgs;
    }

    private static string ExecuteFind(JsonElement? args)
    {
        string path = ".";
        string? name = null;
        string? iname = null;
        string? pathPattern = null;
        string? type = null;
        string? size = null;
        int? maxdepth = null;
        int? mindepth = null;
        string? mtime = null;
        string? mmin = null;
        string? newer = null;
        bool empty = false;
        string[]? prune = null;
        bool print0 = false;

        if (args != null)
        {
            var a = args.Value;
            if (a.TryGetProperty("path", out var pathEl)) path = pathEl.GetString() ?? ".";
            if (a.TryGetProperty("name", out var nameEl)) name = nameEl.GetString();
            if (a.TryGetProperty("iname", out var inameEl)) iname = inameEl.GetString();
            if (a.TryGetProperty("path_pattern", out var ppEl)) pathPattern = ppEl.GetString();
            if (a.TryGetProperty("type", out var typeEl)) type = typeEl.GetString();
            if (a.TryGetProperty("size", out var sizeEl)) size = sizeEl.GetString();
            if (a.TryGetProperty("maxdepth", out var maxEl)) maxdepth = maxEl.GetInt32();
            if (a.TryGetProperty("mindepth", out var minEl)) mindepth = minEl.GetInt32();
            if (a.TryGetProperty("mtime", out var mtimeEl)) mtime = mtimeEl.GetString();
            if (a.TryGetProperty("mmin", out var mminEl)) mmin = mminEl.GetString();
            if (a.TryGetProperty("newer", out var newerEl)) newer = newerEl.GetString();
            if (a.TryGetProperty("empty", out var emptyEl) && emptyEl.GetBoolean()) empty = true;
            if (a.TryGetProperty("prune", out var pruneEl) && pruneEl.ValueKind == JsonValueKind.Array)
            {
                var pruneList = new List<string>();
                foreach (var item in pruneEl.EnumerateArray())
                {
                    string? val = item.GetString();
                    if (val != null) pruneList.Add(val);
                }
                if (pruneList.Count > 0) prune = pruneList.ToArray();
            }
            if (a.TryGetProperty("print0", out var p0El) && p0El.GetBoolean()) print0 = true;
        }

        var findArgs = BuildFindArgs(path, name, iname, pathPattern, type, size, maxdepth, mindepth,
            mtime, mmin, newer, empty, prune, print0);
        var results = ExecuteFindCached(findArgs);

        var sb = new StringBuilder();
        for (int i = 0; i < results.Count; i++)
        {
            sb.Append(results[i]);
            sb.Append(print0 ? '\0' : '\n');
        }

        return sb.ToString();
    }

    private static string ExecuteGrep(JsonElement? args)
    {
        if (args == null)
            throw new ArgumentException("Missing arguments for grep");

        var a = args.Value;

        string pattern = a.TryGetProperty("pattern", out var patEl)
            ? patEl.GetString() ?? ""
            : throw new ArgumentException("Missing pattern");

        string path = a.TryGetProperty("path", out var pathEl)
            ? pathEl.GetString() ?? "."
            : ".";

        bool ignoreCase = a.TryGetProperty("ignoreCase", out var icEl) && icEl.GetBoolean();
        bool invertMatch = a.TryGetProperty("invertMatch", out var ivEl) && ivEl.GetBoolean();
        bool count = a.TryGetProperty("count", out var cntEl) && cntEl.GetBoolean();
        bool filesWithMatches = a.TryGetProperty("filesWithMatches", out var fwmEl) && fwmEl.GetBoolean();
        bool onlyMatching = a.TryGetProperty("onlyMatching", out var omEl) && omEl.GetBoolean();
        bool wholeWord = a.TryGetProperty("wholeWord", out var wwEl) && wwEl.GetBoolean();
        bool fixedStrings = a.TryGetProperty("fixedStrings", out var fsEl) && fsEl.GetBoolean();
        bool useERE = a.TryGetProperty("useERE", out var ereEl) && ereEl.GetBoolean();
        int contextLines = a.TryGetProperty("contextLines", out var clEl) ? clEl.GetInt32() : 0;
        int beforeContext = a.TryGetProperty("beforeContext", out var bcEl) ? bcEl.GetInt32() : 0;
        int afterContext = a.TryGetProperty("afterContext", out var acEl) ? acEl.GetInt32() : 0;
        int maxResults = a.TryGetProperty("maxResults", out var mrEl) ? mrEl.GetInt32() : 100;

        string? glob = a.TryGetProperty("glob", out var globEl) ? globEl.GetString() : null;

        // If path is a single file, grep it directly
        if (File.Exists(path) && !Directory.Exists(path))
        {
            var compiledGrep = GetOrCompileGrep(pattern, ignoreCase, lineNumbers: true, forceFilename: true,
                invertMatch: invertMatch, count: count, filesWithMatches: filesWithMatches,
                onlyMatching: onlyMatching, wholeWord: wholeWord, fixedStrings: fixedStrings,
                useERE: useERE, contextLines: contextLines, beforeContext: beforeContext,
                afterContext: afterContext);

            string content = File.ReadAllText(path);
            var sw = new StringWriter();
            compiledGrep.Execute(new StringReader(content), sw, path);
            return sw.ToString();
        }

        // Directory mode: enumerate files with FindEngine, then grep each one
        if (!Directory.Exists(path))
            throw new ArgumentException($"Path not found: {path}");

        var findArgs = new List<string> { path };
        if (glob != null)
        {
            findArgs.Add("-name");
            findArgs.Add(glob);
        }
        findArgs.Add("-type");
        findArgs.Add("f");

        var files = ExecuteFindCached(findArgs);

        // Compile grep once with line numbers and filenames enabled
        var grepScript = GetOrCompileGrep(pattern, ignoreCase, lineNumbers: true, forceFilename: true,
            invertMatch: invertMatch, count: count, filesWithMatches: filesWithMatches,
            onlyMatching: onlyMatching, wholeWord: wholeWord, fixedStrings: fixedStrings,
            useERE: useERE, contextLines: contextLines, beforeContext: beforeContext,
            afterContext: afterContext);

        var output = new StringBuilder();
        int totalResults = 0;

        for (int i = 0; i < files.Count && totalResults < maxResults; i++)
        {
            try
            {
                string content = File.ReadAllText(files[i]);
                var sw = new StringWriter();
                int result = grepScript.Execute(new StringReader(content), sw, files[i]);
                if (result == 0)
                {
                    string text = sw.ToString();
                    output.Append(text);
                    // Count lines appended
                    for (int c = 0; c < text.Length; c++)
                    {
                        if (text[c] == '\n')
                            totalResults++;
                    }
                }
            }
            catch
            {
                // Skip files we can't read (binary, permission denied, etc.)
            }
        }

        return output.ToString();
    }

    /// <summary>
    /// Gets a cached GrepScript or compiles and caches a new one.
    /// </summary>
    private static GrepScript GetOrCompileGrep(string pattern, bool ignoreCase,
        bool lineNumbers = false, bool forceFilename = false, bool suppressFilename = false,
        bool invertMatch = false, bool count = false, bool filesWithMatches = false,
        bool onlyMatching = false, bool wholeWord = false, bool fixedStrings = false,
        bool useERE = false, int contextLines = 0, int beforeContext = 0, int afterContext = 0)
    {
        // Build cache key from pattern + all flags
        var keyBuilder = new StringBuilder();
        keyBuilder.Append(pattern);
        keyBuilder.Append('\0');
        if (ignoreCase) keyBuilder.Append('i');
        if (lineNumbers) keyBuilder.Append('n');
        if (forceFilename) keyBuilder.Append('H');
        if (suppressFilename) keyBuilder.Append('h');
        if (invertMatch) keyBuilder.Append('v');
        if (count) keyBuilder.Append('c');
        if (filesWithMatches) keyBuilder.Append('l');
        if (onlyMatching) keyBuilder.Append('o');
        if (wholeWord) keyBuilder.Append('w');
        if (fixedStrings) keyBuilder.Append('F');
        if (useERE) keyBuilder.Append('E');
        if (contextLines > 0) { keyBuilder.Append('C'); keyBuilder.Append(contextLines); }
        if (beforeContext > 0) { keyBuilder.Append('B'); keyBuilder.Append(beforeContext); }
        if (afterContext > 0) { keyBuilder.Append('A'); keyBuilder.Append(afterContext); }
        string cacheKey = keyBuilder.ToString();

        if (s_grepCache.TryGet(cacheKey, out var cached))
        {
            Console.Error.WriteLine($"{ServerName}: grep cache hit for '{pattern}'");
            return cached;
        }

        Console.Error.WriteLine($"{ServerName}: grep cache miss, compiling '{pattern}'");
        var options = new GrepOptions
        {
            IgnoreCase = ignoreCase,
            LineNumbers = lineNumbers,
            ForceFilename = forceFilename,
            SuppressFilename = suppressFilename,
            InvertMatch = invertMatch,
            Count = count,
            FilesWithMatches = filesWithMatches,
            OnlyMatching = onlyMatching,
            WholeWord = wholeWord,
            FixedStrings = fixedStrings,
            UseERE = useERE,
            BothContext = contextLines,
            BeforeContext = beforeContext,
            AfterContext = afterContext,
        };
        options.Patterns.Add(pattern);
        var compiled = GrepEngine.Compile(options);
        s_grepCache.Set(cacheKey, compiled);
        return compiled;
    }

    private static string ExecuteSed(JsonElement? args)
    {
        if (args == null)
            throw new ArgumentException("Missing arguments for sed");

        var a = args.Value;

        string script = a.TryGetProperty("script", out var sEl)
            ? sEl.GetString() ?? ""
            : throw new ArgumentException("Missing script");

        bool suppressDefault = a.TryGetProperty("suppressDefault", out var sdEl) && sdEl.GetBoolean();
        bool useEre = a.TryGetProperty("useEre", out var ueEl) && ueEl.GetBoolean();
        bool inPlace = a.TryGetProperty("inPlace", out var ipEl) && ipEl.GetBoolean();
        bool dryRun = a.TryGetProperty("dryRun", out var drEl) && drEl.GetBoolean();
        string? backup = a.TryGetProperty("backup", out var bEl) ? bEl.GetString() : null;

        // Collect files from both "file" and "files" parameters
        var allFiles = new List<string>();
        if (a.TryGetProperty("file", out var fEl))
        {
            string? f = fEl.GetString();
            if (f != null) allFiles.Add(f);
        }
        if (a.TryGetProperty("files", out var filesEl) && filesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in filesEl.EnumerateArray())
            {
                string? f = item.GetString();
                if (f != null) allFiles.Add(f);
            }
        }

        if (allFiles.Count == 0)
            throw new ArgumentException("Missing file or files");

        var compiled = GetOrCompileSed(script, suppressDefault, useEre);
        var output = new StringBuilder();

        for (int fi = 0; fi < allFiles.Count; fi++)
        {
            string file = allFiles[fi];
            string content = File.ReadAllText(file);
            string transformed = compiled.Transform(content);

            if (dryRun)
            {
                string diff = UnifiedDiff.Generate(content, transformed, file, file);
                if (diff.Length == 0)
                {
                    if (allFiles.Count > 1) output.AppendLine($"--- {file}: No changes.");
                    else output.Append("No changes.");
                }
                else
                {
                    output.Append(diff);
                }
            }
            else if (inPlace)
            {
                if (content == transformed)
                {
                    if (allFiles.Count > 1) output.AppendLine($"--- {file}: No changes.");
                    else output.Append("No changes.");
                }
                else
                {
                    if (backup != null)
                        File.WriteAllText(file + backup, content);
                    File.WriteAllText(file, transformed);
                    int changes = UnifiedDiff.CountChangedLines(content, transformed);
                    if (allFiles.Count > 1) output.AppendLine($"Modified {file} ({changes} lines changed)");
                    else output.Append($"Modified {file} ({changes} lines changed)");
                }
            }
            else
            {
                // Return transformed content
                output.Append(transformed);
            }
        }

        return output.ToString();
    }

    /// <summary>
    /// Gets a cached SedScript or compiles and caches a new one.
    /// Cache key includes script text, suppressDefault, and useEre.
    /// </summary>
    private static SedScript GetOrCompileSed(string script, bool suppressDefault = false, bool useEre = false)
    {
        var keyBuilder = new StringBuilder();
        keyBuilder.Append(script);
        if (suppressDefault) keyBuilder.Append("\0n");
        if (useEre) keyBuilder.Append("\0E");
        string cacheKey = keyBuilder.ToString();

        if (s_sedCache.TryGet(cacheKey, out var cached))
        {
            Console.Error.WriteLine($"{ServerName}: sed cache hit for '{script}'");
            return cached;
        }

        Console.Error.WriteLine($"{ServerName}: sed cache miss, compiling '{script}'");
        var compiled = SedParser.Parse(script, suppressDefault, useEre);
        s_sedCache.Set(cacheKey, compiled);
        return compiled;
    }

    private static string ExecuteAwk(JsonElement? args)
    {
        if (args == null)
            throw new ArgumentException("Missing arguments for awk");

        var a = args.Value;

        string program = a.TryGetProperty("program", out var pEl)
            ? pEl.GetString() ?? ""
            : throw new ArgumentException("Missing program");

        string? fieldSep = a.TryGetProperty("fieldSeparator", out var fsEl) ? fsEl.GetString() : null;

        // Parse variables
        Dictionary<string, string>? variables = null;
        if (a.TryGetProperty("variables", out var varsEl) && varsEl.ValueKind == JsonValueKind.Object)
        {
            variables = new Dictionary<string, string>();
            foreach (var prop in varsEl.EnumerateObject())
            {
                string? val = prop.Value.GetString();
                if (val != null)
                    variables[prop.Name] = val;
            }
        }

        // Collect files from both "file" and "files" parameters
        var allFiles = new List<string>();
        if (a.TryGetProperty("file", out var fEl))
        {
            string? f = fEl.GetString();
            if (f != null) allFiles.Add(f);
        }
        if (a.TryGetProperty("files", out var filesEl) && filesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in filesEl.EnumerateArray())
            {
                string? f = item.GetString();
                if (f != null) allFiles.Add(f);
            }
        }

        if (allFiles.Count == 0)
            throw new ArgumentException("Missing file or files");

        var compiled = GetOrCompileAwk(program);

        if (allFiles.Count == 1)
        {
            string content = File.ReadAllText(allFiles[0]);
            var (result, _) = compiled.Execute(content, fieldSep, variables);
            return result;
        }

        // Multiple files: use the file-based overload
        var (multiResult, _) = compiled.Execute(allFiles.ToArray(), fieldSep, variables);
        return multiResult;
    }

    /// <summary>
    /// Gets a cached AwkScript or compiles and caches a new one.
    /// </summary>
    private static AwkScript GetOrCompileAwk(string program)
    {
        if (s_awkCache.TryGet(program, out var cached))
        {
            Console.Error.WriteLine($"{ServerName}: awk cache hit for '{program}'");
            return cached;
        }

        Console.Error.WriteLine($"{ServerName}: awk cache miss, compiling '{program}'");
        var compiled = AwkEngine.Compile(program);
        s_awkCache.Set(program, compiled);
        return compiled;
    }

    private static string ExecutePipeline(JsonElement? args)
    {
        if (args == null)
            throw new ArgumentException("Missing arguments for pipeline");

        var a = args.Value;

        if (!a.TryGetProperty("stages", out var stagesEl) || stagesEl.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("Missing or invalid 'stages' array");

        bool inPlace = a.TryGetProperty("inPlace", out var ipEl) && ipEl.GetBoolean();
        bool dryRun = a.TryGetProperty("dryRun", out var drEl) && drEl.GetBoolean();
        string? backup = a.TryGetProperty("backup", out var bEl) ? bEl.GetString() : null;

        // Parse stages
        var stages = new List<PipelineStage>();
        foreach (var stageEl in stagesEl.EnumerateArray())
        {
            string tool = stageEl.TryGetProperty("tool", out var toolEl)
                ? toolEl.GetString() ?? ""
                : throw new ArgumentException("Each stage must have a 'tool' key");
            stages.Add(new PipelineStage(tool, stageEl));
        }

        if (stages.Count == 0)
            throw new ArgumentException("Pipeline must have at least one stage");

        // Phase 1: If first stage is find, get file list; otherwise treat as text pipeline
        List<string>? files = null;
        int firstTextStage = 0;

        if (stages[0].Tool == "find")
        {
            files = ExecuteFindStage(stages[0]);
            firstTextStage = 1;
        }

        // If we have files, process each file through remaining stages
        if (files != null)
        {
            return ExecuteFilesPipeline(files, stages, firstTextStage, inPlace, dryRun, backup);
        }

        // Text-only pipeline (no find stage): not file-based, just chain text stages
        // This shouldn't normally happen in practice but handle gracefully
        throw new ArgumentException("Pipeline must start with a find stage to locate files");
    }

    private static List<string> ExecuteFindStage(PipelineStage stage)
    {
        var s = stage.Element;
        string path = s.TryGetProperty("path", out var pathEl) ? pathEl.GetString() ?? "." : ".";
        string? name = s.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
        string? iname = s.TryGetProperty("iname", out var inameEl) ? inameEl.GetString() : null;
        string? type = s.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
        string? size = s.TryGetProperty("size", out var sizeEl) ? sizeEl.GetString() : null;
        int? maxdepth = s.TryGetProperty("maxdepth", out var mdEl) ? mdEl.GetInt32() : null;
        int? mindepth = s.TryGetProperty("mindepth", out var minEl) ? minEl.GetInt32() : null;
        string[]? prune = null;
        if (s.TryGetProperty("prune", out var pruneEl) && pruneEl.ValueKind == JsonValueKind.Array)
        {
            var pruneList = new List<string>();
            foreach (var item in pruneEl.EnumerateArray())
            {
                string? val = item.GetString();
                if (val != null) pruneList.Add(val);
            }
            if (pruneList.Count > 0) prune = pruneList.ToArray();
        }

        var findArgs = BuildFindArgs(path, name, iname, pathPattern: null, type, size, maxdepth, mindepth,
            mtime: null, mmin: null, newer: null, empty: false, prune, print0: false);
        return ExecuteFindCached(findArgs);
    }

    private static string ExecuteFilesPipeline(List<string> files, List<PipelineStage> stages,
        int firstTextStage, bool inPlace, bool dryRun, string? backup)
    {
        // Pre-compile all text stages
        var compiledStages = new List<CompiledStage>();
        for (int i = firstTextStage; i < stages.Count; i++)
        {
            compiledStages.Add(CompileStage(stages[i]));
        }

        var result = new FredResult();
        int filesModified = 0;

        for (int fi = 0; fi < files.Count; fi++)
        {
            string filePath = files[fi];
            if (!File.Exists(filePath))
                continue;

            result.FilesSearched++;

            string content;
            try { content = File.ReadAllText(filePath); }
            catch { continue; }

            // Run each text stage in sequence
            string current = content;
            bool filtered = false;

            for (int si = 0; si < compiledStages.Count; si++)
            {
                var cs = compiledStages[si];

                switch (cs.Tool)
                {
                    case "grep":
                    {
                        var sw = new StringWriter();
                        int exitCode = cs.Grep!.Execute(new StringReader(current), sw);
                        if (exitCode != 0)
                        {
                            filtered = true;
                            break;
                        }
                        // For intermediate grep stages, pass matched content forward
                        // For the last stage, keep line-numbered output for structured result
                        if (si < compiledStages.Count - 1)
                        {
                            // Strip line numbers for intermediate stages so downstream sees plain text
                            current = StripLineNumbers(sw.ToString());
                        }
                        else
                        {
                            current = sw.ToString();
                        }
                        break;
                    }
                    case "sed":
                    {
                        current = cs.Sed!.Transform(current);
                        break;
                    }
                    case "awk":
                    {
                        var (awkOut, _) = cs.Awk!.Execute(current, cs.AwkFieldSep, cs.AwkVariables);
                        current = awkOut;
                        break;
                    }
                    case "edit":
                    {
                        if (cs.EditReplaceAll)
                            current = current.Replace(cs.EditOld!, cs.EditNew!);
                        else
                        {
                            int idx = current.IndexOf(cs.EditOld!, StringComparison.Ordinal);
                            if (idx >= 0)
                                current = string.Concat(current.AsSpan(0, idx), cs.EditNew!, current.AsSpan(idx + cs.EditOld!.Length));
                        }
                        break;
                    }
                }

                if (filtered) break;
            }

            if (filtered) continue;

            result.FilesMatched++;

            // Determine if the last stage was a transformation (sed/awk/edit) or a filter (grep)
            bool hasTransform = compiledStages.Count > 0 &&
                (compiledStages[^1].Tool == "sed" || compiledStages[^1].Tool == "awk" || compiledStages[^1].Tool == "edit");

            if (hasTransform && (inPlace || dryRun))
            {
                // File modification mode
                var fileMatch = new FredFileMatch { File = filePath };
                string[] origLines = content.Split('\n');
                string[] modLines = current.Split('\n');

                for (int li = 0; li < origLines.Length; li++)
                {
                    string origLine = origLines[li];
                    string? modLine = (li < modLines.Length) ? modLines[li] : null;
                    if (modLine != null && origLine != modLine)
                    {
                        fileMatch.Lines.Add(new FredLineMatch
                        {
                            Number = li + 1,
                            Content = origLine,
                            Replacement = modLine,
                        });
                    }
                }

                if (content != current)
                {
                    if (inPlace)
                    {
                        if (backup != null)
                            File.WriteAllText(filePath + backup, content);
                        File.WriteAllText(filePath, current);
                    }
                    filesModified++;
                }

                if (fileMatch.Lines.Count > 0)
                    result.Matches.Add(fileMatch);
            }
            else if (compiledStages.Count > 0 && compiledStages[^1].Tool == "grep")
            {
                // Last stage is grep: show matching lines with line numbers
                var fileMatch = new FredFileMatch { File = filePath };
                string[] lines = current.Split('\n');
                for (int li = 0; li < lines.Length; li++)
                {
                    string line = lines[li];
                    if (line.Length == 0 && li == lines.Length - 1) continue;

                    int colonIdx = line.IndexOf(':');
                    if (colonIdx > 0 && int.TryParse(line.AsSpan(0, colonIdx), out int lineNum))
                    {
                        fileMatch.Lines.Add(new FredLineMatch
                        {
                            Number = lineNum,
                            Content = line.Substring(colonIdx + 1),
                        });
                    }
                    else
                    {
                        fileMatch.Lines.Add(new FredLineMatch { Number = li + 1, Content = line });
                    }
                }
                if (fileMatch.Lines.Count > 0)
                    result.Matches.Add(fileMatch);
            }
            else if (hasTransform)
            {
                // Transform without in-place: show changed lines
                var fileMatch = new FredFileMatch { File = filePath };
                string[] origLines = content.Split('\n');
                string[] modLines = current.Split('\n');
                for (int li = 0; li < origLines.Length; li++)
                {
                    string origLine = origLines[li];
                    string? modLine = (li < modLines.Length) ? modLines[li] : null;
                    if (modLine != null && origLine != modLine)
                    {
                        fileMatch.Lines.Add(new FredLineMatch
                        {
                            Number = li + 1,
                            Content = origLine,
                            Replacement = modLine,
                        });
                    }
                }
                if (fileMatch.Lines.Count > 0)
                    result.Matches.Add(fileMatch);
            }
            else
            {
                // Find-only: list file paths
                result.Matches.Add(new FredFileMatch { File = filePath });
            }
        }

        result.FilesModified = filesModified;
        return result.ToJson();
    }

    private static CompiledStage CompileStage(PipelineStage stage)
    {
        var s = stage.Element;

        switch (stage.Tool)
        {
            case "grep":
            {
                string pattern = s.TryGetProperty("pattern", out var pEl)
                    ? pEl.GetString() ?? ""
                    : throw new ArgumentException("grep stage requires 'pattern'");
                bool ignoreCase = s.TryGetProperty("ignoreCase", out var icEl) && icEl.GetBoolean();
                bool invertMatch = s.TryGetProperty("invertMatch", out var ivEl) && ivEl.GetBoolean();
                bool wholeWord = s.TryGetProperty("wholeWord", out var wwEl) && wwEl.GetBoolean();
                bool fixedStrings = s.TryGetProperty("fixedStrings", out var fsEl) && fsEl.GetBoolean();
                bool useERE = s.TryGetProperty("useERE", out var ereEl) && ereEl.GetBoolean();

                var compiled = GetOrCompileGrep(pattern, ignoreCase,
                    lineNumbers: true, suppressFilename: true,
                    invertMatch: invertMatch, wholeWord: wholeWord,
                    fixedStrings: fixedStrings, useERE: useERE);
                return new CompiledStage("grep") { Grep = compiled };
            }
            case "sed":
            {
                string script = s.TryGetProperty("script", out var sEl)
                    ? sEl.GetString() ?? ""
                    : throw new ArgumentException("sed stage requires 'script'");
                bool suppress = s.TryGetProperty("suppressDefault", out var sdEl) && sdEl.GetBoolean();
                bool useEre = s.TryGetProperty("useERE", out var ueEl) && ueEl.GetBoolean();
                var compiled = GetOrCompileSed(script, suppress, useEre);
                return new CompiledStage("sed") { Sed = compiled };
            }
            case "awk":
            {
                string program = s.TryGetProperty("program", out var pEl)
                    ? pEl.GetString() ?? ""
                    : throw new ArgumentException("awk stage requires 'program'");
                string? fieldSep = s.TryGetProperty("fieldSeparator", out var fsEl) ? fsEl.GetString() : null;
                Dictionary<string, string>? variables = null;
                if (s.TryGetProperty("variables", out var vEl) && vEl.ValueKind == JsonValueKind.Object)
                {
                    variables = new Dictionary<string, string>();
                    foreach (var prop in vEl.EnumerateObject())
                    {
                        string? val = prop.Value.GetString();
                        if (val != null) variables[prop.Name] = val;
                    }
                }
                var compiled = GetOrCompileAwk(program);
                return new CompiledStage("awk") { Awk = compiled, AwkFieldSep = fieldSep, AwkVariables = variables };
            }
            case "edit":
            {
                string old = s.TryGetProperty("old", out var oEl)
                    ? oEl.GetString() ?? ""
                    : throw new ArgumentException("edit stage requires 'old'");
                string @new = s.TryGetProperty("new", out var nEl)
                    ? nEl.GetString() ?? ""
                    : throw new ArgumentException("edit stage requires 'new'");
                bool replaceAll = s.TryGetProperty("replaceAll", out var raEl) && raEl.GetBoolean();
                return new CompiledStage("edit") { EditOld = old, EditNew = @new, EditReplaceAll = replaceAll };
            }
            default:
                throw new ArgumentException($"Unknown stage tool: {stage.Tool}");
        }
    }

    private static string StripLineNumbers(string grepOutput)
    {
        var sb = new StringBuilder();
        var lines = grepOutput.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Length == 0 && i == lines.Length - 1) continue;

            int colonIdx = line.IndexOf(':');
            if (colonIdx > 0 && int.TryParse(line.AsSpan(0, colonIdx), out _))
                sb.AppendLine(line.Substring(colonIdx + 1));
            else
                sb.AppendLine(line);
        }
        return sb.ToString();
    }

    private sealed record PipelineStage(string Tool, JsonElement Element);

    private sealed class CompiledStage(string tool)
    {
        public string Tool { get; } = tool;
        public GrepScript? Grep { get; init; }
        public SedScript? Sed { get; init; }
        public AwkScript? Awk { get; init; }
        public string? AwkFieldSep { get; init; }
        public Dictionary<string, string>? AwkVariables { get; init; }
        public string? EditOld { get; init; }
        public string? EditNew { get; init; }
        public bool EditReplaceAll { get; init; }
    }

    private static string? HandleUnknownMethod(JsonElement? id, string? method)
    {
        if (id == null)
            return null; // notification for unknown method - ignore

        return MakeErrorResponse(id, -32601, $"Method not found: {method}");
    }

    private static string MakeResponse(JsonElement? id, string resultJson)
    {
        string idStr = FormatId(id);
        // Parse resultJson to ensure it's valid and compact
        using var resultDoc = JsonDocument.Parse(resultJson);
        string compactResult = JsonSerializer.Serialize(resultDoc.RootElement);
        return $"{{\"jsonrpc\":\"2.0\",\"id\":{idStr},\"result\":{compactResult}}}";
    }

    private static string MakeToolResult(JsonElement? id, string text)
    {
        string idStr = FormatId(id);
        string escapedText = JsonSerializer.Serialize(text);
        return $"{{\"jsonrpc\":\"2.0\",\"id\":{idStr},\"result\":{{\"content\":[{{\"type\":\"text\",\"text\":{escapedText}}}]}}}}";
    }

    private static string MakeToolError(JsonElement? id, string message)
    {
        string idStr = FormatId(id);
        string escapedMsg = JsonSerializer.Serialize(message);
        return $"{{\"jsonrpc\":\"2.0\",\"id\":{idStr},\"result\":{{\"isError\":true,\"content\":[{{\"type\":\"text\",\"text\":{escapedMsg}}}]}}}}";
    }

    private static string MakeErrorResponse(JsonElement? id, int code, string message)
    {
        string idStr = FormatId(id);
        string escapedMsg = JsonSerializer.Serialize(message);
        return $"{{\"jsonrpc\":\"2.0\",\"id\":{idStr},\"error\":{{\"code\":{code},\"message\":{escapedMsg}}}}}";
    }

    private static string FormatId(JsonElement? id)
    {
        if (id == null)
            return "null";

        return id.Value.ValueKind switch
        {
            JsonValueKind.Number => id.Value.GetRawText(),
            JsonValueKind.String => JsonSerializer.Serialize(id.Value.GetString()),
            _ => id.Value.GetRawText(),
        };
    }
}
