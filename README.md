# Fred

**F**ind | g**RE**p | se**D** | AWK for .NET

A high-performance text processing toolkit designed as file editing infrastructure for AI coding tools. Four engines (find, grep, sed, AWK) with a compile-once, execute-many pattern. Thread-safe. No subprocess overhead.

## Why Fred?

AI coding assistants (Claude Code, Copilot, VS Code extensions) need to find files, search code, and make targeted edits across entire codebases. Shelling out to Unix tools is slow, fragile, and platform-dependent. Fred provides the same capabilities as a native .NET library with compiled, reusable, thread-safe script objects.

## Quick Start

### Library

```csharp
using FredDotNet;

// Compile once, reuse many times (thread-safe)
var sed  = SedEngine.Compile("s/oldMethod/newMethod/g");
var grep = GrepEngine.Compile("TODO", ignoreCase: true);
var awk  = AwkEngine.Compile("{ print $1, $3 }");
var find = FindEngine.Compile(new[] { "src", "-name", "*.cs", "-type", "f" });

// Execute
var (output, exitCode) = sed.Execute(input);
var (output, exitCode) = grep.Execute(input);
var (output, exitCode) = awk.Execute(input);
List<string> files     = find.Execute();

// Compose into pipelines
string result = FredPipeline.Create()
    .Find("src", "-name", "*.cs", "-type", "f")
    .Grep("TODO", ignoreCase: true)
    .Sed("s/TODO/DONE/g")
    .Execute("");
```

### CLI

```bash
# Individual tools (Unix-compatible)
echo "hello" | ned 's/hello/goodbye/'
echo "hello" | nrep -i "hello"
echo "a 1"   | nawk '{ print $2 }'
nfind . -name "*.cs" -type f

# Unified tool with in-place editing
fred . -name "*.cs" -containing "oldMethod" --sed -i 's/oldMethod/newMethod/g'

# Dry-run (preview changes as unified diff)
fred . -name "*.cs" --sed --dry-run 's/TODO/DONE/g'

# JSON structured output
fred . -name "*.cs" --grep "TODO" --json
```

### MCP Server (for AI tool integration)

Fred includes a Model Context Protocol server for direct integration with AI coding tools:

```bash
# Run the MCP server
dotnet run --project fred-mcp

# Or publish as a single-file binary
dotnet publish fred-mcp -c Release -r linux-x64 --self-contained
```

Tools exposed: `find`, `grep`, `sed`, `awk`, `pipeline`

Features:
- LRU caching of compiled scripts (64 entries) and find results (16 entries)
- Repeat calls hit the cache -- zero recompilation, zero filesystem re-walking
- In-place editing with backup and dry-run support
- Structured JSON results with file paths and line numbers

Claude Code configuration:
```json
{
  "mcpServers": {
    "fred": {
      "command": "/path/to/fred-mcp"
    }
  }
}
```

## Architecture

```
FredDotNet/              # Core library
  FredDotNet.cs          # Sed engine + Grep engine + RegexTranslator
  AwkEngine.cs           # AWK interpreter (lexer, parser, interpreter)
  FindEngine.cs          # Find engine (directory walker, predicates)
  Pipeline.cs            # Composable pipeline builder
  LruCache.cs            # Generic thread-safe LRU cache
  UnifiedDiff.cs         # Diff generation
  FredResult.cs          # Structured result DTOs
```

### Compile-Once Pattern

Every engine follows the same pattern:

| Engine | Static class | Compile | Script | Thread-safe via |
|--------|-------------|---------|--------|----------------|
| Find | `FindEngine` | `.Compile(args)` | `FindScript` | Immutable + per-call `EvalContext` |
| Grep | `GrepEngine` | `.Compile(pattern)` | `GrepScript` | Fully immutable |
| Sed | `SedEngine` | `.Compile(script)` | `SedScript` | Per-call `SedExecutionContext` |
| AWK | `AwkEngine` | `.Compile(program)` | `AwkScript` | Fresh `AwkInterpreter` per call |

### Performance

- No LINQ on hot paths
- Compiled regex caching (`ConcurrentDictionary`)
- Grep streams line-at-a-time with ring buffer for context (O(beforeContext) memory)
- Find uses `Directory.EnumerateFileSystemEntries` (lazy enumeration)
- BRE/ERE regex translation shared between sed and grep via `RegexTranslator`

## Testing

```bash
dotnet build Fred.sln    # 0 errors, 0 warnings
dotnet test Fred.sln     # 926 tests
```

| Test suite | Tests | What it validates |
|-----------|-------|-------------------|
| FredDotNet.Tests | 457 | Library unit tests |
| ned.Tests | 36 | sed CLI tests |
| SedValidation.Tests | 103 | ned vs `/usr/bin/sed` oracle |
| GrepValidation.Tests | 120 | nrep vs `/usr/bin/grep` oracle |
| AwkValidation.Tests | 106 | nawk vs `/usr/bin/awk` oracle |
| FindValidation.Tests | 104 | nfind vs `/usr/bin/find` oracle |

Oracle tests run both the .NET tool and the real Unix binary on identical input, then assert byte-for-byte identical output and exit codes.

## Building

```bash
git clone https://github.com/user/fred.git
cd fred
dotnet build Fred.sln
dotnet test Fred.sln
```

### Publishing single-file binaries

```bash
dotnet publish fred -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true
dotnet publish fred-mcp -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true
```

## License

MIT
