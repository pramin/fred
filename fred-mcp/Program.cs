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
    private static readonly LruCache<string, JqScript> s_jqCache = new(64);
    private static readonly LruCache<string, List<string>> s_findCache = new(16);

    private static readonly string s_stateDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".freds-mcp");

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
        Console.WriteLine($"  {ServerName} install [claude|vscode|copilot|all]");
        Console.WriteLine("      Update tool, configure MCP clients (Claude Code, VS Code, Copilot CLI).");
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
            Console.WriteLine($"freds-mcp install: updating to latest version...");

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
        if (all || target == "copilot")
        {
            any = true;
            InstallCopilot(command);
        }

        if (!any)
        {
            Console.Error.WriteLine($"Unknown target: {target}");
            Console.Error.WriteLine("Usage: freds-mcp install [claude|vscode|copilot|all]");
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

    private static void InstallCopilot(string command)
    {
        // Copilot CLI: ~/.copilot/mcp-config.json with "mcpServers" key
        string copilotDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot");
        string configPath = Path.Combine(copilotDir, "mcp-config.json");

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
        else
        {
            Directory.CreateDirectory(copilotDir);
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
                    if (prop.Name == "mcpServers") continue;
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
                    if (prop.Name == "fred") continue;
                    prop.WriteTo(writer);
                }
            }

            // Add/update fred
            writer.WritePropertyName("fred");
            writer.WriteStartObject();
            writer.WriteString("type", "stdio");
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
        Console.WriteLine($"  Copilot CLI: configured in {configPath}");
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
                    "description": "Locate files and directories by name, type, size, or modification time. Use this FIRST when you need to discover which files exist before reading, searching, or editing them. Faster than listing directories manually. Results are cached — repeat calls with the same args are free.\n\nExamples:\n- Find all C# files: {path: \".\", name: \"*.cs\", type: \"f\"}\n- Find large log files: {path: \"/var/log\", name: \"*.log\", size: \"+10M\"}\n- Find recently changed files: {path: \"src\", type: \"f\", mmin: \"-30\"}",
                    "inputSchema": {
                        "type": "object",
                        "properties": {
                            "path": {"type": "string", "description": "Starting directory path"},
                            "name": {"type": "string", "description": "Filename glob pattern (e.g., '*.cs', '*.json')"},
                            "iname": {"type": "string", "description": "Case-insensitive filename glob pattern"},
                            "path_pattern": {"type": "string", "description": "Full path glob pattern (e.g., '*/controllers/*.cs')"},
                            "type": {"type": "string", "enum": ["f", "d", "l"], "description": "f=file, d=directory, l=symlink"},
                            "size": {"type": "string", "description": "Size filter: '+100k' (>100KB), '-1M' (<1MB), '0c' (empty). Suffixes: c=bytes, k=KiB, M=MiB, G=GiB"},
                            "maxdepth": {"type": "integer", "description": "Maximum directory depth to search"},
                            "mindepth": {"type": "integer", "description": "Minimum directory depth before matching"},
                            "mtime": {"type": "string", "description": "Modified N days ago: '+7' (>7 days), '-1' (<1 day), '0' (today)"},
                            "mmin": {"type": "string", "description": "Modified N minutes ago: '+60' (>1hr ago), '-5' (<5min ago)"},
                            "newer": {"type": "string", "description": "Match files newer than this file path"},
                            "empty": {"type": "boolean", "description": "Match only empty files or directories"},
                            "prune": {"type": "array", "items": {"type": "string"}, "description": "Directory names to skip entirely (e.g., ['node_modules', '.git', 'bin'])"},
                            "print0": {"type": "boolean", "description": "Null-byte separated output (for paths with spaces)"}
                        },
                        "required": ["path"]
                    }
                },
                {
                    "name": "grep",
                    "description": "Search file contents for patterns. Returns matching lines with file paths and line numbers. Use this to find WHERE something appears in the codebase — function calls, string literals, config values, TODOs, error messages. Supports regex, fixed strings, context lines, and word boundaries.\n\nExamples:\n- Find TODO comments: {pattern: \"TODO\", path: \"src\", glob: \"*.cs\"}\n- Find function definition: {pattern: \"public.*void DoWork\", path: \".\", glob: \"*.cs\", useERE: true}\n- Count matches per file: {pattern: \"import\", path: \"src\", glob: \"*.ts\", count: true}",
                    "inputSchema": {
                        "type": "object",
                        "properties": {
                            "pattern": {"type": "string", "description": "Search pattern — regex by default, or literal with fixedStrings"},
                            "path": {"type": "string", "description": "File or directory to search in"},
                            "glob": {"type": "string", "description": "Only search files matching this glob (e.g., '*.cs', '*.json')"},
                            "ignoreCase": {"type": "boolean", "description": "Case-insensitive matching"},
                            "invertMatch": {"type": "boolean", "description": "Return lines that do NOT match"},
                            "count": {"type": "boolean", "description": "Return count of matches per file instead of lines"},
                            "filesWithMatches": {"type": "boolean", "description": "Return only file paths that contain a match"},
                            "onlyMatching": {"type": "boolean", "description": "Return only the matched substring, not the full line"},
                            "wholeWord": {"type": "boolean", "description": "Match only at word boundaries"},
                            "fixedStrings": {"type": "boolean", "description": "Treat pattern as a literal string (no regex interpretation)"},
                            "useERE": {"type": "boolean", "description": "Extended regex: +, ?, |, () without backslash escaping"},
                            "contextLines": {"type": "integer", "description": "Show N lines before AND after each match"},
                            "beforeContext": {"type": "integer", "description": "Show N lines before each match"},
                            "afterContext": {"type": "integer", "description": "Show N lines after each match"},
                            "maxResults": {"type": "integer", "description": "Stop after this many matching lines (default 100)"}
                        },
                        "required": ["pattern", "path"]
                    }
                },
                {
                    "name": "sed",
                    "description": "Transform text with sed scripts — find-and-replace, delete lines, extract ranges. Use this for EDITING file content: renaming variables, updating config values, removing lines, inserting text. Supports in-place editing with backup and dry-run preview.\n\nExamples:\n- Rename a method: {script: \"s/oldMethod/newMethod/g\", file: \"src/App.cs\", inPlace: true}\n- Delete blank lines: {script: \"/^$/d\", file: \"output.txt\"}\n- Preview changes: {script: \"s/http:/https:/g\", file: \"config.yaml\", dryRun: true}\n- Extract lines 10-20: {script: \"10,20!d\", file: \"log.txt\"}",
                    "inputSchema": {
                        "type": "object",
                        "properties": {
                            "script": {"type": "string", "description": "Sed script: 's/old/new/g' for replace, '/pattern/d' for delete, etc."},
                            "file": {"type": "string", "description": "File to transform"},
                            "files": {"type": "array", "items": {"type": "string"}, "description": "Multiple files to transform"},
                            "input": {"type": "string", "description": "Text input (if no file specified)"},
                            "suppressDefault": {"type": "boolean", "description": "Suppress auto-print (sed -n mode). Use with /pattern/p to print only matches."},
                            "useEre": {"type": "boolean", "description": "Extended regex: +, ?, |, () without backslash escaping"},
                            "inPlace": {"type": "boolean", "description": "Write changes directly to the file(s)"},
                            "backup": {"type": "string", "description": "Create backup with this suffix before in-place edit (e.g., '.bak')"},
                            "dryRun": {"type": "boolean", "description": "Show a unified diff of what would change, without modifying anything"}
                        },
                        "required": ["script"]
                    }
                },
                {
                    "name": "awk",
                    "description": "Process structured text with AWK programs. Use this for COLUMNAR DATA: extracting fields, computing sums/averages, reformatting CSV/TSV, filtering rows by field values. More powerful than grep for structured data.\n\nExamples:\n- Extract 2nd column: {program: \"{print $2}\", file: \"data.tsv\"}\n- Sum a column: {program: \"{sum+=$3} END{print sum}\", file: \"sales.csv\", fieldSeparator: \",\"}\n- Filter rows: {program: \"$3 > 100\", file: \"data.txt\"}\n- Reformat: {program: \"{printf \\\"%s=%s\\\\n\\\", $1, $2}\", file: \"pairs.txt\"}",
                    "inputSchema": {
                        "type": "object",
                        "properties": {
                            "program": {"type": "string", "description": "AWK program. Patterns: BEGIN{}, /regex/{}, END{}. Fields: $1, $2, ... $NF"},
                            "file": {"type": "string", "description": "File to process"},
                            "files": {"type": "array", "items": {"type": "string"}, "description": "Multiple files to process"},
                            "input": {"type": "string", "description": "Text input (if no file specified)"},
                            "fieldSeparator": {"type": "string", "description": "Field separator (default: whitespace). Use ',' for CSV, '\\t' for TSV."},
                            "variables": {"type": "object", "additionalProperties": {"type": "string"}, "description": "Variables to set before execution (AWK -v var=val)"}
                        },
                        "required": ["program"]
                    }
                },
                {
                    "name": "jq",
                    "description": "Query and transform JSON data. Use this for ANYTHING JSON: parsing API responses, extracting values from config files (package.json, tsconfig.json, etc.), restructuring data, filtering arrays. Understands nested objects, arrays, and has 80+ builtins.\n\nExamples:\n- Extract a field: {expression: \".name\", input: \"{\\\"name\\\": \\\"fred\\\"}\"}\n- List dependencies: {expression: \".dependencies | keys[]\", file: \"package.json\"}\n- Filter array: {expression: \"[.[] | select(.age > 21)]\", file: \"users.json\"}\n- Transform: {expression: \"{name: .title, url: .html_url}\", file: \"repo.json\", rawOutput: true}\n- Extract from nested: {expression: \".results[].items[] | .id\", file: \"data.json\"}",
                    "inputSchema": {
                        "type": "object",
                        "properties": {
                            "expression": {"type": "string", "description": "jq expression: . (identity), .field, .[0], .[] (iterate), | (pipe), select(), map(), keys, etc."},
                            "file": {"type": "string", "description": "JSON file to process"},
                            "input": {"type": "string", "description": "JSON string input (if no file specified)"},
                            "rawOutput": {"type": "boolean", "description": "Output raw strings without JSON quotes (like jq -r)"},
                            "compactOutput": {"type": "boolean", "description": "One-line output, no pretty-printing (like jq -c)"},
                            "sortKeys": {"type": "boolean", "description": "Sort object keys alphabetically"},
                            "slurp": {"type": "boolean", "description": "Read all inputs into a single array first"},
                            "nullInput": {"type": "boolean", "description": "Don't read input — start with null (useful with env, $ENV)"},
                            "args": {"type": "object", "additionalProperties": {"type": "string"}, "description": "Bind string variables: {\"name\": \"fred\"} makes $name available"},
                            "jsonArgs": {"type": "object", "additionalProperties": {"type": "string"}, "description": "Bind JSON variables: {\"config\": \"{...}\"} makes $config available"}
                        },
                        "required": ["expression"]
                    }
                },
                {
                    "name": "curl",
                    "description": "Make HTTP requests. Use this to fetch URLs, test APIs, download files, post JSON — anything that needs network access. Supports all HTTP methods, headers, auth, redirects, and structured output.\n\nExamples:\n- GET a URL: {url: \"https://api.example.com/data\"}\n- POST JSON: {url: \"https://api.example.com/items\", method: \"POST\", json: \"{\\\"name\\\": \\\"test\\\"}\"}\n- With auth: {url: \"https://api.example.com/me\", bearerToken: \"tok_xxx\"}\n- Download file: {url: \"https://example.com/file.zip\", outputFile: \"file.zip\"}\n- Get status code: {url: \"https://example.com\", writeOut: \"%{http_code}\", silent: true}",
                    "inputSchema": {
                        "type": "object",
                        "properties": {
                            "url": {"type": "string", "description": "URL to request"},
                            "method": {"type": "string", "enum": ["GET","POST","PUT","PATCH","DELETE","HEAD","OPTIONS"], "description": "HTTP method (default: GET, or POST when data/json is set)"},
                            "headers": {"type": "array", "items": {"type": "string"}, "description": "Request headers as 'Name: Value' strings"},
                            "data": {"type": "string", "description": "Request body (sets method to POST if not specified)"},
                            "json": {"type": "string", "description": "JSON request body (auto-sets Content-Type and Accept to application/json)"},
                            "basicAuth": {"type": "string", "description": "Basic auth as 'user:password'"},
                            "bearerToken": {"type": "string", "description": "Bearer token for Authorization header"},
                            "outputFile": {"type": "string", "description": "Save response body to this file"},
                            "includeHeaders": {"type": "boolean", "description": "Include HTTP response headers in output"},
                            "headOnly": {"type": "boolean", "description": "Show response headers only (HEAD request)"},
                            "followRedirects": {"type": "boolean", "description": "Follow HTTP redirects (3xx)"},
                            "maxRedirects": {"type": "integer", "description": "Maximum number of redirects to follow (default: 50)"},
                            "maxTime": {"type": "integer", "description": "Maximum total time in seconds"},
                            "connectTimeout": {"type": "integer", "description": "Connection timeout in seconds"},
                            "insecure": {"type": "boolean", "description": "Skip TLS certificate verification"},
                            "compressed": {"type": "boolean", "description": "Request and auto-decompress gzip/deflate/brotli responses"},
                            "silent": {"type": "boolean", "description": "Suppress error messages"},
                            "failOnError": {"type": "boolean", "description": "Return exit code 22 for HTTP 4xx/5xx errors"},
                            "writeOut": {"type": "string", "description": "Format string output after transfer. Codes: %{http_code}, %{size_download}, %{time_total}, %{content_type}, %{url_effective}"},
                            "userAgent": {"type": "string", "description": "User-Agent header value"},
                            "retry": {"type": "integer", "description": "Number of retries on transient failure"},
                            "retryDelay": {"type": "integer", "description": "Seconds between retries"},
                            "verbose": {"type": "boolean", "description": "Show request/response headers on stderr"}
                        },
                        "required": ["url"]
                    }
                },
                {
                    "name": "wc",
                    "description": "Count lines, words, characters, and bytes. Use this to quickly check file size before deciding whether to read the whole thing, or to count occurrences after grep.\n\nExamples:\n- Count lines in a file: {file: \"src/App.cs\"}\n- Count words in text: {input: \"hello world\"}",
                    "inputSchema": {
                        "type": "object",
                        "properties": {
                            "file": {"type": "string", "description": "File to count"},
                            "input": {"type": "string", "description": "Text to count (if no file)"}
                        }
                    }
                },
                {
                    "name": "base64",
                    "description": "Encode or decode Base64. Use this for API payloads, embedded images, JWT tokens, or any binary-to-text conversion.\n\nExamples:\n- Encode: {input: \"hello\", action: \"encode\"}\n- Decode: {input: \"aGVsbG8=\", action: \"decode\"}\n- URL-safe: {input: \"data\", action: \"encode\", urlSafe: true}",
                    "inputSchema": {
                        "type": "object",
                        "properties": {
                            "input": {"type": "string", "description": "String to encode, or Base64 string to decode"},
                            "file": {"type": "string", "description": "File to encode (reads as bytes)"},
                            "action": {"type": "string", "enum": ["encode", "decode"], "description": "encode or decode"},
                            "urlSafe": {"type": "boolean", "description": "Use URL-safe Base64 (no padding, -_ instead of +/)"}
                        },
                        "required": ["action"]
                    }
                },
                {
                    "name": "sort",
                    "description": "Sort and deduplicate lines of text. Use this to organize output from other tools, rank results, or remove duplicates.\n\nExamples:\n- Sort file: {file: \"list.txt\"}\n- Unique sorted: {file: \"data.txt\", unique: true}\n- Numeric sort: {input: \"3\\n1\\n2\", numeric: true}\n- Sort by 2nd field: {file: \"data.tsv\", keyField: 2, fieldSeparator: \"\\t\"}",
                    "inputSchema": {
                        "type": "object",
                        "properties": {
                            "file": {"type": "string", "description": "File to sort"},
                            "input": {"type": "string", "description": "Text to sort (if no file)"},
                            "reverse": {"type": "boolean", "description": "Reverse sort order"},
                            "numeric": {"type": "boolean", "description": "Sort numerically instead of alphabetically"},
                            "ignoreCase": {"type": "boolean", "description": "Case-insensitive sorting"},
                            "unique": {"type": "boolean", "description": "Remove duplicate lines"},
                            "keyField": {"type": "integer", "description": "Sort by this field number (1-based)"},
                            "fieldSeparator": {"type": "string", "description": "Field separator for keyField sorting"}
                        }
                    }
                },
                {
                    "name": "uniq",
                    "description": "Filter adjacent duplicate lines. Unlike sort -u which deduplicates globally, uniq only collapses CONSECUTIVE identical lines — pipe through sort first for global dedup. Use for frequency analysis with -c, finding repeated lines with -d.\n\nExamples:\n- Remove adjacent dupes: {file: \"output.txt\"}\n- Count occurrences: {input: \"a\\na\\nb\\na\", count: true}\n- Show only repeated lines: {file: \"log.txt\", onlyDuplicates: true}",
                    "inputSchema": {
                        "type": "object",
                        "properties": {
                            "file": {"type": "string", "description": "File to process"},
                            "input": {"type": "string", "description": "Text to process (if no file)"},
                            "count": {"type": "boolean", "description": "Prefix each line with number of occurrences"},
                            "onlyDuplicates": {"type": "boolean", "description": "Only output lines that are repeated"},
                            "onlyUnique": {"type": "boolean", "description": "Only output lines that are NOT repeated"},
                            "ignoreCase": {"type": "boolean", "description": "Case-insensitive comparison"}
                        }
                    }
                },
                {
                    "name": "diff",
                    "description": "Generate or apply unified diffs. Use this to compare files, preview changes before applying, or apply patches. Works like Unix diff/patch.\n\nExamples:\n- Compare two files: {originalFile: \"old.cs\", modifiedFile: \"new.cs\"}\n- Compare strings: {original: \"hello\\nworld\", modified: \"hello\\nearth\"}\n- Apply a patch: {file: \"src/App.cs\", patch: \"--- a/...\\n+++ b/...\\n@@ ...\"}\n- Dry-run patch: {file: \"src/App.cs\", patch: \"...\", dryRun: true}",
                    "inputSchema": {
                        "type": "object",
                        "properties": {
                            "original": {"type": "string", "description": "Original text (for generating diff)"},
                            "modified": {"type": "string", "description": "Modified text (for generating diff)"},
                            "originalFile": {"type": "string", "description": "Path to original file"},
                            "modifiedFile": {"type": "string", "description": "Path to modified file"},
                            "patch": {"type": "string", "description": "Unified diff to apply"},
                            "file": {"type": "string", "description": "File to patch"},
                            "backup": {"type": "string", "description": "Backup suffix before patching (e.g. .bak)"},
                            "dryRun": {"type": "boolean", "description": "Check if patch applies cleanly without modifying"}
                        }
                    }
                },
                {
                    "name": "pipeline",
                    "description": "Chain multiple tools into a single operation: find files, then grep/sed/awk/jq/edit them in sequence. The output of each stage feeds into the next. Start with 'find' to select files, then transform their contents. Supports in-place editing and dry-run preview.\n\nExamples:\n- Find and replace across codebase: {stages: [{tool:\"find\", path:\"src\", name:\"*.cs\", type:\"f\"}, {tool:\"sed\", script:\"s/oldAPI/newAPI/g\"}], inPlace: true}\n- Find JSON configs and extract a field: {stages: [{tool:\"find\", path:\".\", name:\"*.json\", type:\"f\"}, {tool:\"jq\", expression:\".version\"}]}\n- Search and transform: {stages: [{tool:\"find\", path:\".\", name:\"*.ts\"}, {tool:\"grep\", pattern:\"deprecated\"}, {tool:\"awk\", program:\"{print FILENAME \\\":\\\" NR}\"}]}\n- Literal string replace: {stages: [{tool:\"find\", path:\".\", name:\"*.md\"}, {tool:\"edit\", old:\"v1.0\", new:\"v2.0\", replaceAll:true}], inPlace: true}",
                    "inputSchema": {
                        "type": "object",
                        "properties": {
                            "stages": {
                                "type": "array",
                                "description": "Ordered processing stages. First can be 'find' (produces file list); rest transform content.",
                                "items": {
                                    "type": "object",
                                    "properties": {
                                        "tool": {"type": "string", "enum": ["find", "grep", "sed", "awk", "jq", "edit", "wc", "base64", "sort", "uniq", "diff"], "description": "Tool for this stage"},
                                        "path": {"type": "string", "description": "find: starting directory"},
                                        "name": {"type": "string", "description": "find: filename glob pattern"},
                                        "iname": {"type": "string", "description": "find: case-insensitive glob"},
                                        "type": {"type": "string", "enum": ["f", "d", "l"], "description": "find: file type"},
                                        "size": {"type": "string", "description": "find: size filter"},
                                        "maxdepth": {"type": "integer", "description": "find: max depth"},
                                        "mindepth": {"type": "integer", "description": "find: min depth"},
                                        "prune": {"type": "array", "items": {"type": "string"}, "description": "find: directories to skip"},
                                        "pattern": {"type": "string", "description": "grep: search pattern"},
                                        "ignoreCase": {"type": "boolean", "description": "grep: case-insensitive"},
                                        "invertMatch": {"type": "boolean", "description": "grep: invert match"},
                                        "wholeWord": {"type": "boolean", "description": "grep: whole words only"},
                                        "fixedStrings": {"type": "boolean", "description": "grep: literal string match"},
                                        "useERE": {"type": "boolean", "description": "grep/sed: extended regex"},
                                        "script": {"type": "string", "description": "sed: transformation script"},
                                        "suppressDefault": {"type": "boolean", "description": "sed: suppress auto-print (-n)"},
                                        "program": {"type": "string", "description": "awk: AWK program"},
                                        "fieldSeparator": {"type": "string", "description": "awk: field separator"},
                                        "variables": {"type": "object", "additionalProperties": {"type": "string"}, "description": "awk: variables"},
                                        "expression": {"type": "string", "description": "jq: jq expression"},
                                        "rawOutput": {"type": "boolean", "description": "jq: raw string output"},
                                        "compactOutput": {"type": "boolean", "description": "jq: compact one-line output"},
                                        "sortKeys": {"type": "boolean", "description": "jq: sort object keys"},
                                        "old": {"type": "string", "description": "edit: exact string to find (no regex)"},
                                        "new": {"type": "string", "description": "edit: replacement string"},
                                        "replaceAll": {"type": "boolean", "description": "edit: replace all occurrences (default: first only)"},
                                        "reverse": {"type": "boolean", "description": "sort: reverse order"},
                                        "numeric": {"type": "boolean", "description": "sort: numeric comparison"},
                                        "unique": {"type": "boolean", "description": "sort/uniq: remove duplicates"},
                                        "onlyDuplicates": {"type": "boolean", "description": "uniq: output only repeated lines"},
                                        "onlyUnique": {"type": "boolean", "description": "uniq: output only non-repeated lines"},
                                        "count": {"type": "boolean", "description": "uniq: prefix lines with occurrence count"},
                                        "action": {"type": "string", "description": "base64: encode or decode"},
                                        "urlSafe": {"type": "boolean", "description": "base64: URL-safe encoding"}
                                    },
                                    "required": ["tool"]
                                }
                            },
                            "inPlace": {"type": "boolean", "description": "Write changes back to the files found by find stage"},
                            "backup": {"type": "string", "description": "Backup suffix for in-place edits (e.g., '.bak')"},
                            "dryRun": {"type": "boolean", "description": "Show unified diff of what would change, without modifying files"}
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
                "jq" => ExecuteJq(arguments),
                "curl" => ExecuteCurl(arguments),
                "wc" => ExecuteWc(arguments),
                "base64" => ExecuteBase64(arguments),
                "sort" => ExecuteSort(arguments),
                "uniq" => ExecuteUniq(arguments),
                "diff" => ExecuteDiff(arguments),
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

    private static string ExecuteJq(JsonElement? args)
    {
        if (args == null)
            throw new ArgumentException("Missing arguments for jq");

        var a = args.Value;

        string expression = a.TryGetProperty("expression", out var exprEl)
            ? exprEl.GetString() ?? ""
            : throw new ArgumentException("Missing expression");

        var options = new JqOptions();
        if (a.TryGetProperty("rawOutput", out var roEl) && roEl.GetBoolean()) options.RawOutput = true;
        if (a.TryGetProperty("compactOutput", out var coEl) && coEl.GetBoolean()) options.CompactOutput = true;
        if (a.TryGetProperty("sortKeys", out var skEl) && skEl.GetBoolean()) options.SortKeys = true;
        if (a.TryGetProperty("slurp", out var slEl) && slEl.GetBoolean()) options.Slurp = true;
        if (a.TryGetProperty("nullInput", out var niEl) && niEl.GetBoolean()) options.NullInput = true;

        // Bind string args
        if (a.TryGetProperty("args", out var argsObj) && argsObj.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in argsObj.EnumerateObject())
            {
                string? val = prop.Value.GetString();
                if (val != null) options.StringArgs[prop.Name] = val;
            }
        }

        // Bind JSON args
        if (a.TryGetProperty("jsonArgs", out var jsonArgsObj) && jsonArgsObj.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in jsonArgsObj.EnumerateObject())
            {
                string? val = prop.Value.GetString();
                if (val != null) options.JsonArgs[prop.Name] = val;
            }
        }

        // Get input: from file, from input string, or null input
        string jsonInput;
        if (a.TryGetProperty("file", out var fileEl))
        {
            string? filePath = fileEl.GetString();
            if (filePath == null) throw new ArgumentException("file must be a string");
            jsonInput = File.ReadAllText(filePath);
        }
        else if (a.TryGetProperty("input", out var inputEl))
        {
            jsonInput = inputEl.GetString() ?? "";
        }
        else if (options.NullInput)
        {
            jsonInput = "null";
        }
        else
        {
            throw new ArgumentException("Missing file, input, or nullInput");
        }

        var compiled = GetOrCompileJq(expression);
        var (result, _) = compiled.Execute(jsonInput, options);
        return result;
    }

    /// <summary>
    /// Gets a cached JqScript or compiles and caches a new one.
    /// </summary>
    private static JqScript GetOrCompileJq(string expression)
    {
        if (s_jqCache.TryGet(expression, out var cached))
        {
            Console.Error.WriteLine($"{ServerName}: jq cache hit for '{expression}'");
            return cached;
        }

        Console.Error.WriteLine($"{ServerName}: jq cache miss, compiling '{expression}'");
        var compiled = JqEngine.Compile(expression);
        s_jqCache.Set(expression, compiled);
        return compiled;
    }

    private static string ExecuteCurl(JsonElement? args)
    {
        if (args == null)
            throw new ArgumentException("Missing arguments for curl");

        var a = args.Value;

        string url = a.TryGetProperty("url", out var urlEl)
            ? urlEl.GetString() ?? ""
            : throw new ArgumentException("Missing url");

        var options = new CurlOptions { Url = url };

        if (a.TryGetProperty("method", out var mEl)) options.Method = mEl.GetString() ?? "GET";
        if (a.TryGetProperty("data", out var dEl)) options.Data = dEl.GetString();
        if (a.TryGetProperty("json", out var jEl)) options.JsonData = jEl.GetString();
        if (a.TryGetProperty("basicAuth", out var baEl)) options.BasicAuth = baEl.GetString();
        if (a.TryGetProperty("bearerToken", out var btEl)) options.BearerToken = btEl.GetString();
        if (a.TryGetProperty("outputFile", out var ofEl)) options.OutputFile = ofEl.GetString();
        if (a.TryGetProperty("userAgent", out var uaEl)) options.UserAgent = uaEl.GetString();
        if (a.TryGetProperty("writeOut", out var woEl)) options.WriteOutFormat = woEl.GetString();
        if (a.TryGetProperty("includeHeaders", out var ihEl) && ihEl.GetBoolean()) options.IncludeHeaders = true;
        if (a.TryGetProperty("headOnly", out var hoEl) && hoEl.GetBoolean()) options.HeadOnly = true;
        if (a.TryGetProperty("followRedirects", out var frEl) && frEl.GetBoolean()) options.FollowRedirects = true;
        if (a.TryGetProperty("insecure", out var ikEl) && ikEl.GetBoolean()) options.Insecure = true;
        if (a.TryGetProperty("compressed", out var cpEl) && cpEl.GetBoolean()) options.Compressed = true;
        if (a.TryGetProperty("silent", out var slnEl) && slnEl.GetBoolean()) options.Silent = true;
        if (a.TryGetProperty("failOnError", out var feEl) && feEl.GetBoolean()) options.FailOnError = true;
        if (a.TryGetProperty("verbose", out var vbEl) && vbEl.GetBoolean()) options.Verbose = true;
        if (a.TryGetProperty("maxTime", out var mtEl)) options.MaxTimeSeconds = mtEl.GetInt32();
        if (a.TryGetProperty("connectTimeout", out var ctEl)) options.ConnectTimeoutSeconds = ctEl.GetInt32();
        if (a.TryGetProperty("maxRedirects", out var mrEl)) options.MaxRedirects = mrEl.GetInt32();
        if (a.TryGetProperty("retry", out var rtEl)) options.Retry = rtEl.GetInt32();
        if (a.TryGetProperty("retryDelay", out var rdEl)) options.RetryDelay = rdEl.GetInt32();

        if (a.TryGetProperty("headers", out var hdrsEl) && hdrsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in hdrsEl.EnumerateArray())
            {
                string? h = item.GetString();
                if (h != null) options.Headers.Add(h);
            }
        }

        var compiled = CurlEngine.Compile(options);
        var (result, exitCode) = compiled.Execute();

        if (exitCode != 0)
            throw new InvalidOperationException($"curl exited with code {exitCode}: {result}");

        return result;
    }


    /// <summary>
    /// Executes the diff tool: generate unified diffs or apply patches.
    /// </summary>
    private static string ExecuteDiff(JsonElement? args)
    {
        string? original = null;
        string? modified = null;
        string? originalFile = null;
        string? modifiedFile = null;
        string? patch = null;
        string? file = null;
        string? backup = null;
        bool dryRun = false;

        if (args != null)
        {
            var a = args.Value;
            if (a.TryGetProperty("original", out var origEl)) original = origEl.GetString();
            if (a.TryGetProperty("modified", out var modEl)) modified = modEl.GetString();
            if (a.TryGetProperty("originalFile", out var origFileEl)) originalFile = origFileEl.GetString();
            if (a.TryGetProperty("modifiedFile", out var modFileEl)) modifiedFile = modFileEl.GetString();
            if (a.TryGetProperty("patch", out var patchEl)) patch = patchEl.GetString();
            if (a.TryGetProperty("file", out var fileEl)) file = fileEl.GetString();
            if (a.TryGetProperty("backup", out var backupEl)) backup = backupEl.GetString();
            if (a.TryGetProperty("dryRun", out var dryRunEl)) dryRun = dryRunEl.GetBoolean();
        }

        // Mode 1: Diff two files
        if (originalFile != null && modifiedFile != null)
        {
            return DiffEngine.DiffFiles(originalFile, modifiedFile);
        }

        // Mode 2: Diff two strings
        if (original != null && modified != null)
        {
            return DiffEngine.Diff(original, modified);
        }

        // Mode 3: Apply patch to file
        if (patch != null && file != null)
        {
            if (dryRun)
            {
                string fileContent = File.ReadAllText(file);
                bool canApply = DiffEngine.CanPatch(fileContent, patch);
                if (canApply)
                {
                    string result = DiffEngine.Patch(fileContent, patch);
                    string preview = UnifiedDiff.Generate(fileContent, result, file, file);
                    return string.IsNullOrEmpty(preview)
                        ? "Patch applies cleanly (no changes)."
                        : $"Patch applies cleanly. Preview:\n{preview}";
                }
                else
                {
                    return "Patch cannot be applied cleanly.";
                }
            }

            DiffEngine.PatchFile(file, patch, backup);
            return backup != null
                ? $"Patched {file} (backup: {file}{backup})"
                : $"Patched {file}";
        }

        // Mode 4: Apply patch to string
        if (patch != null && original != null)
        {
            return DiffEngine.Patch(original, patch);
        }

        throw new ArgumentException(
            "Invalid arguments for diff. Provide either: " +
            "(originalFile + modifiedFile) to diff files, " +
            "(original + modified) to diff strings, or " +
            "(patch + file) to apply a patch.");
    }

    private static string ExecuteUniq(JsonElement? args)
    {
        if (args == null)
            throw new ArgumentException("Missing arguments for uniq");

        var a = args.Value;

        string input;
        if (a.TryGetProperty("file", out var fileEl))
        {
            string? path = fileEl.GetString();
            if (path == null) throw new ArgumentException("file must be a string");
            input = File.ReadAllText(path);
        }
        else if (a.TryGetProperty("input", out var inputEl))
        {
            input = inputEl.GetString() ?? "";
        }
        else
        {
            throw new ArgumentException("Missing file or input");
        }

        var options = new UniqOptions();
        if (a.TryGetProperty("count", out var cEl) && cEl.GetBoolean()) options.Count = true;
        if (a.TryGetProperty("onlyDuplicates", out var dEl) && dEl.GetBoolean()) options.OnlyDuplicates = true;
        if (a.TryGetProperty("onlyUnique", out var uEl) && uEl.GetBoolean()) options.OnlyUnique = true;
        if (a.TryGetProperty("ignoreCase", out var iEl) && iEl.GetBoolean()) options.IgnoreCase = true;

        return UniqEngine.Execute(input, options);
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
                    case "jq":
                    {
                        var (jqOut, _) = cs.Jq!.Execute(current, cs.JqOptions ?? new JqOptions());
                        current = jqOut;
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
                    case "wc":
                    {
                        var wcResult = WcEngine.Count(current);
                        current = $"  {wcResult.Lines}  {wcResult.Words}  {wcResult.Bytes}\n";
                        break;
                    }
                    case "base64":
                    {
                        if (cs.Base64Decode)
                            current = cs.Base64UrlSafe ? Base64Engine.DecodeUrl(current.TrimEnd('\n', '\r')) : Base64Engine.Decode(current.TrimEnd('\n', '\r'));
                        else
                            current = cs.Base64UrlSafe ? Base64Engine.EncodeUrl(current) : Base64Engine.Encode(current);
                        break;
                    }
                    case "sort":
                    {
                        current = SortEngine.Sort(current, cs.SortOptions);
                        break;
                    }
                    case "uniq":
                    {
                        current = UniqEngine.Execute(current, cs.UniqOptions);
                        break;
                    }
                    case "diff":
                    {
                        // diff in pipeline context: no-op (needs two inputs, not applicable as a transform stage)
                        break;
                    }
                }

                if (filtered) break;
            }

            if (filtered) continue;

            result.FilesMatched++;

            // Determine if the last stage was a transformation (sed/awk/jq/edit) or a filter (grep)
            bool hasTransform = compiledStages.Count > 0 &&
                (compiledStages[^1].Tool == "sed" || compiledStages[^1].Tool == "awk" ||
                 compiledStages[^1].Tool == "jq" || compiledStages[^1].Tool == "edit" ||
                 compiledStages[^1].Tool == "wc" || compiledStages[^1].Tool == "base64" ||
                 compiledStages[^1].Tool == "sort" || compiledStages[^1].Tool == "uniq" ||
                 compiledStages[^1].Tool == "diff");

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
            case "jq":
            {
                string expression = s.TryGetProperty("expression", out var eEl)
                    ? eEl.GetString() ?? ""
                    : throw new ArgumentException("jq stage requires 'expression'");
                bool rawOutput = s.TryGetProperty("rawOutput", out var roEl) && roEl.GetBoolean();
                bool compactOutput = s.TryGetProperty("compactOutput", out var coEl) && coEl.GetBoolean();
                bool sortKeys = s.TryGetProperty("sortKeys", out var skEl) && skEl.GetBoolean();
                var jqOpts = new JqOptions { RawOutput = rawOutput, CompactOutput = compactOutput, SortKeys = sortKeys };
                var compiled = GetOrCompileJq(expression);
                return new CompiledStage("jq") { Jq = compiled, JqOptions = jqOpts };
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
            case "wc":
                return new CompiledStage("wc");
            case "base64":
            {
                bool decode = s.TryGetProperty("action", out var actEl) && actEl.GetString() == "decode";
                bool urlSafe = s.TryGetProperty("urlSafe", out var usEl) && usEl.GetBoolean();
                return new CompiledStage("base64") { Base64Decode = decode, Base64UrlSafe = urlSafe };
            }
            case "sort":
            {
                var sortOpts = new SortOptions();
                if (s.TryGetProperty("reverse", out var rEl) && rEl.GetBoolean()) sortOpts.Reverse = true;
                if (s.TryGetProperty("numeric", out var nEl2) && nEl2.GetBoolean()) sortOpts.Numeric = true;
                if (s.TryGetProperty("ignoreCase", out var icEl) && icEl.GetBoolean()) sortOpts.IgnoreCase = true;
                if (s.TryGetProperty("unique", out var uEl) && uEl.GetBoolean()) sortOpts.Unique = true;
                if (s.TryGetProperty("keyField", out var kfEl)) sortOpts.KeyField = kfEl.GetInt32();
                if (s.TryGetProperty("fieldSeparator", out var fsEl2)) sortOpts.FieldSeparator = fsEl2.GetString();
                return new CompiledStage("sort") { SortOptions = sortOpts };
            }
            case "uniq":
            {
                var uniqOpts = new UniqOptions();
                if (s.TryGetProperty("count", out var cEl) && cEl.GetBoolean()) uniqOpts.Count = true;
                if (s.TryGetProperty("onlyDuplicates", out var dEl) && dEl.GetBoolean()) uniqOpts.OnlyDuplicates = true;
                if (s.TryGetProperty("onlyUnique", out var uuEl) && uuEl.GetBoolean()) uniqOpts.OnlyUnique = true;
                if (s.TryGetProperty("ignoreCase", out var iiEl) && iiEl.GetBoolean()) uniqOpts.IgnoreCase = true;
                return new CompiledStage("uniq") { UniqOptions = uniqOpts };
            }
            case "diff":
            {
                return new CompiledStage("diff");
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


    /// <summary>
    /// Executes the wc (word count) tool. Reads from file or input text.
    /// </summary>
    private static string ExecuteWc(JsonElement? args)
    {
        string input;

        if (args != null && args.Value.TryGetProperty("file", out var fileEl))
        {
            string? filePath = fileEl.GetString();
            if (filePath == null || !File.Exists(filePath))
                throw new ArgumentException($"File not found: {filePath}");
            input = File.ReadAllText(filePath);
        }
        else if (args != null && args.Value.TryGetProperty("input", out var inputEl))
        {
            input = inputEl.GetString() ?? "";
        }
        else
        {
            input = "";
        }

        var result = WcEngine.Count(input);
        return $"  {result.Lines}  {result.Words}  {result.Bytes}\n";
    }

    /// <summary>
    /// Executes the base64 encode/decode tool.
    /// </summary>
    private static string ExecuteBase64(JsonElement? args)
    {
        if (args == null)
            throw new ArgumentException("Missing arguments for base64");

        var a = args.Value;

        string action = a.TryGetProperty("action", out var actEl)
            ? actEl.GetString() ?? "encode"
            : throw new ArgumentException("Missing 'action' (encode or decode)");

        bool urlSafe = a.TryGetProperty("urlSafe", out var usEl) && usEl.GetBoolean();

        if (action == "encode")
        {
            // Check for file first (read as bytes)
            if (a.TryGetProperty("file", out var fileEl))
            {
                string? filePath = fileEl.GetString();
                if (filePath == null || !File.Exists(filePath))
                    throw new ArgumentException($"File not found: {filePath}");
                byte[] fileBytes = File.ReadAllBytes(filePath);
                return urlSafe
                    ? Base64Engine.EncodeUrl(System.Text.Encoding.UTF8.GetString(fileBytes))
                    : Base64Engine.Encode(fileBytes);
            }

            string input = a.TryGetProperty("input", out var inputEl)
                ? inputEl.GetString() ?? ""
                : "";

            return urlSafe ? Base64Engine.EncodeUrl(input) : Base64Engine.Encode(input);
        }
        else if (action == "decode")
        {
            string input = a.TryGetProperty("input", out var inputEl)
                ? inputEl.GetString() ?? ""
                : throw new ArgumentException("Missing 'input' for decode");

            return urlSafe ? Base64Engine.DecodeUrl(input) : Base64Engine.Decode(input);
        }
        else
        {
            throw new ArgumentException($"Unknown action: {action}. Use 'encode' or 'decode'.");
        }
    }

    /// <summary>
    /// Executes the sort tool. Reads from file or input text, sorts lines.
    /// </summary>
    private static string ExecuteSort(JsonElement? args)
    {
        string input;

        if (args != null && args.Value.TryGetProperty("file", out var fileEl))
        {
            string? filePath = fileEl.GetString();
            if (filePath == null || !File.Exists(filePath))
                throw new ArgumentException($"File not found: {filePath}");
            input = File.ReadAllText(filePath);
        }
        else if (args != null && args.Value.TryGetProperty("input", out var inputEl))
        {
            input = inputEl.GetString() ?? "";
        }
        else
        {
            input = "";
        }

        var options = new SortOptions();
        if (args != null)
        {
            var a = args.Value;
            if (a.TryGetProperty("reverse", out var rEl) && rEl.GetBoolean()) options.Reverse = true;
            if (a.TryGetProperty("numeric", out var nEl) && nEl.GetBoolean()) options.Numeric = true;
            if (a.TryGetProperty("ignoreCase", out var icEl) && icEl.GetBoolean()) options.IgnoreCase = true;
            if (a.TryGetProperty("unique", out var uEl) && uEl.GetBoolean()) options.Unique = true;
            if (a.TryGetProperty("keyField", out var kfEl)) options.KeyField = kfEl.GetInt32();
            if (a.TryGetProperty("fieldSeparator", out var fsEl)) options.FieldSeparator = fsEl.GetString();
        }

        return SortEngine.Sort(input, options);
    }

    private sealed class CompiledStage(string tool)
    {
        public string Tool { get; } = tool;
        public GrepScript? Grep { get; init; }
        public SedScript? Sed { get; init; }
        public AwkScript? Awk { get; init; }
        public string? AwkFieldSep { get; init; }
        public Dictionary<string, string>? AwkVariables { get; init; }
        public JqScript? Jq { get; init; }
        public JqOptions? JqOptions { get; init; }
        public string? EditOld { get; init; }
        public string? EditNew { get; init; }
        public bool EditReplaceAll { get; init; }
        public bool Base64Decode { get; init; }
        public bool Base64UrlSafe { get; init; }
        public SortOptions? SortOptions { get; init; }
        public UniqOptions? UniqOptions { get; init; }
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
