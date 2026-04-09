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
    private const string ServerName = "fred-mcp";
    private const string ServerVersion = "1.0.0";
    private const string ProtocolVersion = "2024-11-05";

    // LRU caches for compiled scripts and find results
    private static readonly LruCache<string, SedScript> s_sedCache = new(64);
    private static readonly LruCache<string, GrepScript> s_grepCache = new(64);
    private static readonly LruCache<string, AwkScript> s_awkCache = new(64);
    private static readonly LruCache<string, List<string>> s_findCache = new(16);

    public static async Task<int> Main(string[] args)
    {
        Console.Error.WriteLine($"{ServerName} v{ServerVersion} starting...");

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
                    "description": "Execute a find->grep->sed/awk pipeline. Find files, filter by content, transform. Returns structured JSON with file paths, line numbers, and content. Can modify files in-place.",
                    "inputSchema": {
                        "type": "object",
                        "properties": {
                            "path": {"type": "string", "default": "."},
                            "name": {"type": "string", "description": "Filename glob pattern"},
                            "iname": {"type": "string", "description": "Case-insensitive filename glob"},
                            "type": {"type": "string", "enum": ["f", "d"]},
                            "size": {"type": "string", "description": "Size filter"},
                            "maxdepth": {"type": "integer"},
                            "mindepth": {"type": "integer"},
                            "prune": {"type": "array", "items": {"type": "string"}, "description": "Directory names to skip"},
                            "containing": {"type": "string", "description": "Filter files containing this pattern"},
                            "grepIgnoreCase": {"type": "boolean", "description": "Case-insensitive grep"},
                            "grepInvertMatch": {"type": "boolean", "description": "Invert grep match"},
                            "grepWholeWord": {"type": "boolean", "description": "Grep whole words only"},
                            "grepFixedStrings": {"type": "boolean", "description": "Treat grep pattern as literal"},
                            "grepUseERE": {"type": "boolean", "description": "Use ERE for grep pattern"},
                            "sedScript": {"type": "string", "description": "Sed script to apply to matching files"},
                            "sedSuppressDefault": {"type": "boolean", "description": "Suppress sed default output (-n)"},
                            "sedUseEre": {"type": "boolean", "description": "Use ERE for sed script"},
                            "awkProgram": {"type": "string", "description": "AWK program to apply"},
                            "awkFieldSep": {"type": "string"},
                            "awkVariables": {"type": "object", "additionalProperties": {"type": "string"}, "description": "AWK variables"},
                            "inPlace": {"type": "boolean", "default": false},
                            "backup": {"type": "string"},
                            "dryRun": {"type": "boolean", "default": false}
                        },
                        "required": ["path"]
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

        string path = a.TryGetProperty("path", out var pathEl) ? pathEl.GetString() ?? "." : ".";
        string? name = a.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
        string? iname = a.TryGetProperty("iname", out var inameEl) ? inameEl.GetString() : null;
        string? type = a.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
        string? size = a.TryGetProperty("size", out var sizeEl) ? sizeEl.GetString() : null;
        int? maxdepth = a.TryGetProperty("maxdepth", out var mdEl) ? mdEl.GetInt32() : null;
        int? mindepth = a.TryGetProperty("mindepth", out var minEl) ? minEl.GetInt32() : null;
        string[]? prune = null;
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
        string? containing = a.TryGetProperty("containing", out var contEl) ? contEl.GetString() : null;
        bool grepIgnoreCase = a.TryGetProperty("grepIgnoreCase", out var gicEl) && gicEl.GetBoolean();
        bool grepInvertMatch = a.TryGetProperty("grepInvertMatch", out var givEl) && givEl.GetBoolean();
        bool grepWholeWord = a.TryGetProperty("grepWholeWord", out var gwwEl) && gwwEl.GetBoolean();
        bool grepFixedStrings = a.TryGetProperty("grepFixedStrings", out var gfsEl) && gfsEl.GetBoolean();
        bool grepUseERE = a.TryGetProperty("grepUseERE", out var gueEl) && gueEl.GetBoolean();
        string? sedScript = a.TryGetProperty("sedScript", out var sedEl) ? sedEl.GetString() : null;
        bool sedSuppressDefault = a.TryGetProperty("sedSuppressDefault", out var ssdEl) && ssdEl.GetBoolean();
        bool sedUseEre = a.TryGetProperty("sedUseEre", out var sueEl) && sueEl.GetBoolean();
        string? awkProgram = a.TryGetProperty("awkProgram", out var awkEl) ? awkEl.GetString() : null;
        string? awkFieldSep = a.TryGetProperty("awkFieldSep", out var afsEl) ? afsEl.GetString() : null;
        Dictionary<string, string>? awkVariables = null;
        if (a.TryGetProperty("awkVariables", out var avEl) && avEl.ValueKind == JsonValueKind.Object)
        {
            awkVariables = new Dictionary<string, string>();
            foreach (var prop in avEl.EnumerateObject())
            {
                string? val = prop.Value.GetString();
                if (val != null) awkVariables[prop.Name] = val;
            }
        }
        bool inPlace = a.TryGetProperty("inPlace", out var ipEl) && ipEl.GetBoolean();
        bool dryRun = a.TryGetProperty("dryRun", out var drEl) && drEl.GetBoolean();
        string? backup = a.TryGetProperty("backup", out var bEl) ? bEl.GetString() : null;

        // Build find args using the shared helper
        var findArgs = BuildFindArgs(path, name, iname, pathPattern: null, type, size, maxdepth, mindepth,
            mtime: null, mmin: null, newer: null, empty: false, prune, print0: false);

        var files = ExecuteFindCached(findArgs);

        // Compile grep for containing filter (with line numbers for structured output)
        GrepScript? grepFilter = null;
        GrepScript? grepWithLines = null;
        if (containing != null)
        {
            grepFilter = GetOrCompileGrep(containing, ignoreCase: grepIgnoreCase,
                invertMatch: grepInvertMatch, wholeWord: grepWholeWord,
                fixedStrings: grepFixedStrings, useERE: grepUseERE);
            grepWithLines = GetOrCompileGrep(containing, ignoreCase: grepIgnoreCase,
                lineNumbers: true, suppressFilename: true,
                invertMatch: grepInvertMatch, wholeWord: grepWholeWord,
                fixedStrings: grepFixedStrings, useERE: grepUseERE);
        }

        // Compile sed/awk
        SedScript? compiledSed = sedScript != null ? GetOrCompileSed(sedScript, sedSuppressDefault, sedUseEre) : null;
        AwkScript? compiledAwk = awkProgram != null ? GetOrCompileAwk(awkProgram) : null;

        var result = new FredResult();
        int filesModified = 0;

        for (int i = 0; i < files.Count; i++)
        {
            string filePath = files[i];
            if (!File.Exists(filePath))
                continue;

            result.FilesSearched++;

            string content;
            try { content = File.ReadAllText(filePath); }
            catch { continue; }

            // Apply containing filter
            if (grepFilter != null)
            {
                var sw = new StringWriter();
                int grepResult = grepFilter.Execute(new StringReader(content), sw);
                if (grepResult != 0)
                    continue;
            }

            result.FilesMatched++;

            if (compiledSed != null)
            {
                string transformed = compiledSed.Transform(content);

                if (inPlace && content != transformed)
                {
                    if (backup != null)
                        File.WriteAllText(filePath + backup, content);
                    File.WriteAllText(filePath, transformed);
                    filesModified++;
                }

                // Always populate structured matches for sed
                var fileMatch = new FredFileMatch { File = filePath };

                if (dryRun || inPlace)
                {
                    // Show only changed lines
                    string[] origLines = content.Split('\n');
                    string[] modLines = transformed.Split('\n');
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
                    if (!inPlace && content == transformed)
                    {
                        // No changes in dry-run, skip
                    }
                    else if (fileMatch.Lines.Count > 0)
                    {
                        result.Matches.Add(fileMatch);
                        if (dryRun && content != transformed)
                            filesModified++;
                    }
                }
                else
                {
                    // stdout mode: show all lines with replacements where changed
                    string[] origLines = content.Split('\n');
                    string[] modLines = transformed.Split('\n');
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
                    {
                        result.Matches.Add(fileMatch);
                    }
                }
            }
            else if (compiledAwk != null)
            {
                var (awkResult, _) = compiledAwk.Execute(content, awkFieldSep, awkVariables);
                var fileMatch = new FredFileMatch { File = filePath };
                string[] awkLines = awkResult.Split('\n');
                for (int li = 0; li < awkLines.Length; li++)
                {
                    if (awkLines[li].Length == 0 && li == awkLines.Length - 1)
                        continue;
                    fileMatch.Lines.Add(new FredLineMatch
                    {
                        Number = li + 1,
                        Content = awkLines[li],
                    });
                }
                if (fileMatch.Lines.Count > 0)
                    result.Matches.Add(fileMatch);
            }
            else if (grepWithLines != null)
            {
                // Find+grep only: capture matching lines with line numbers
                var sw = new StringWriter();
                grepWithLines.Execute(new StringReader(content), sw);
                string grepText = sw.ToString();

                var fileMatch = new FredFileMatch { File = filePath };
                string[] lines = grepText.Split('\n');
                for (int li = 0; li < lines.Length; li++)
                {
                    string line = lines[li];
                    if (line.Length == 0 && li == lines.Length - 1)
                        continue;

                    // Parse "linenum:content" format
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
                        fileMatch.Lines.Add(new FredLineMatch
                        {
                            Number = li + 1,
                            Content = line,
                        });
                    }
                }
                if (fileMatch.Lines.Count > 0)
                    result.Matches.Add(fileMatch);
            }
            else
            {
                // Find-only with no filter: just list file paths
                result.Matches.Add(new FredFileMatch { File = filePath });
            }
        }

        result.FilesModified = filesModified;

        return result.ToJson();
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
