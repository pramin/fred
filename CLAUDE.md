# Fred - Find | gREp | seD | AWK for .NET

## Project Overview
FredDotNet is a high-performance .NET 8 text processing toolkit designed as **file editing infrastructure for AI coding tools** (Claude Code, Copilot, VS Code). Four engines (find, grep, sed, AWK) follow a compile-once, execute-many pattern with thread-safe reuse. The unified `fred` CLI and MCP server enable codebase-wide search and in-place editing in a single tool call.

## Solution Structure

```
Fred.sln
├── FredDotNet/              # Core library (all four engines + pipeline + utilities)
│   ├── FredDotNet.cs        # Sed engine, grep engine, RegexTranslator (~3970 lines)
│   ├── AwkEngine.cs         # AWK interpreter (lexer, parser, interpreter) (~2800 lines)
│   ├── FindEngine.cs        # Find engine (directory walker, predicates) (~840 lines)
│   ├── Pipeline.cs          # FredPipeline composable builder (~250 lines)
│   ├── FredResult.cs        # Structured result DTOs + JSON serialization
│   └── UnifiedDiff.cs       # Diff generation utility
├── fred/                    # Unified CLI: find → grep → sed/awk with in-place editing
├── fred-mcp/                # MCP server (stdio JSON-RPC) for AI tool integration
├── ned/                     # sed CLI tool
├── nrep/                    # grep CLI tool
├── nawk/                    # AWK CLI tool
├── nfind/                   # find CLI tool
├── FredDotNet.Tests/        # Library unit tests (446 tests)
├── ned.Tests/               # ned CLI tests (36 tests)
├── SedValidation.Tests/     # sed oracle: ned vs /usr/bin/sed (103 tests)
├── GrepValidation.Tests/    # grep oracle: nrep vs /usr/bin/grep (120 tests)
├── AwkValidation.Tests/     # AWK oracle: nawk vs /usr/bin/awk (106 tests)
└── FindValidation.Tests/    # find oracle: nfind vs /usr/bin/find (104 tests)
```

## Build and Test

```bash
dotnet build Fred.sln           # 0 errors, 0 warnings
dotnet test Fred.sln            # 915 tests, all passing
```

## Library API

All four engines follow a consistent pattern:

```csharp
// Compile once, execute many (thread-safe)
var sed  = SedEngine.Compile("s/foo/bar/g");
var grep = GrepEngine.Compile("error", ignoreCase: true);
var awk  = AwkEngine.Compile("{ print $1 }");
var find = FindEngine.Compile(new[] { ".", "-name", "*.cs", "-type", "f" });

// String/list execution
var (output, exitCode) = sed.Execute(input);       // → (string, int)
var (output, exitCode) = grep.Execute(input);      // → (string, int)
var (output, exitCode) = awk.Execute(input);       // → (string, int)
List<string> paths     = find.Execute();            // → List<string>

// Streaming execution
sed.Execute(reader, writer);    // slurps (sed needs full input for $ address)
grep.Execute(reader, writer);   // truly streams line-at-a-time
awk.Execute(reader, writer);    // slurps (AWK needs all records for END blocks)
find.Execute(writer);           // streams paths as found

// Pipeline composition
string result = FredPipeline.Create()
    .Find(".", "-name", "*.log")
    .Grep("ERROR", ignoreCase: true)
    .Sed("s/ERROR/WARN/g")
    .Execute("");

// Unified diff generation
string diff = UnifiedDiff.Generate(original, modified, "file.cs");
```

### Entry Points

| Engine | Static class | Factory | Script type |
|--------|-------------|---------|-------------|
| Find | `FindEngine` | `.Compile(args)` / `.Compile(options)` | `FindScript` |
| Grep | `GrepEngine` | `.Compile(pattern, ...)` / `.Compile(options)` | `GrepScript` |
| Sed | `SedEngine` | `.Compile(script)` | `SedScript` |
| AWK | `AwkEngine` | `.Compile(program)` | `AwkScript` |

### Thread Safety
All compiled scripts are safe for concurrent reuse:
- `FindScript`: Immutable options + compiled glob regexes; per-call `EvalContext`
- `GrepScript`: Fully immutable after construction
- `SedScript`: Fresh `SedExecutionContext` per call; caches are read-only after construction
- `AwkScript`: Fresh `AwkInterpreter` per call; AST is immutable

## fred CLI — Unified Tool

```bash
# Find + grep
fred . -name "*.cs" -containing "TODO"

# Find + grep + sed (stdout)
fred . -name "*.cs" --grep "TODO" --sed 's/TODO/DONE/g'

# In-place editing — modifies files directly
fred . -name "*.cs" --sed -i 's/oldMethod/newMethod/g'

# In-place with backup
fred . -name "*.cs" --sed -i.bak 's/old/new/g'

# Dry-run — show unified diff without modifying
fred . -name "*.cs" --sed --dry-run 's/TODO/DONE/g'

# JSON structured output
fred . -name "*.cs" --grep "TODO" --json

# Stdin pipeline mode
echo "hello" | fred --sed 's/hello/world/'
```

## fred-mcp — MCP Server for AI Tools

STDIO-based MCP server (JSON-RPC 2.0) exposing 5 tools:

| Tool | Description |
|------|-------------|
| `fred_find` | Find files by name, type, size, depth |
| `fred_grep` | Search file contents with regex patterns |
| `fred_sed` | Apply sed transformations (with in-place editing) |
| `fred_awk` | Process files with AWK programs |
| `fred_pipeline` | Full find→grep→sed/awk pipeline |

### Usage with Claude Code

```json
{
  "mcpServers": {
    "fred": {
      "command": "dotnet",
      "args": ["run", "--project", "/path/to/fred-mcp"]
    }
  }
}
```

Or publish as single-file binary:
```bash
dotnet publish fred-mcp -c Release -r linux-x64 --self-contained
```

## Key Design Decisions
- No LINQ on hot paths in any engine
- Compiled regex caching (ConcurrentDictionary) for sed patterns
- Grep streams line-at-a-time with ring buffer for before-context (O(beforeContext) memory)
- AWK slurps input (inherent to AWK's END block semantics)
- Find uses `Directory.EnumerateFileSystemEntries` for lazy directory walking
- `RegexTranslator` (internal) shared by sed and grep for BRE/ERE → .NET regex translation
- Oracle tests compare byte-for-byte output against real Unix binaries (sorted for find)
- JSON serialization uses source-generated `JsonSerializerContext` (zero reflection)
- MCP server uses stdio transport with newline-delimited JSON-RPC 2.0
