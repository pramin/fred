# Fred's Pipe: Find | Grep | Sed Pipeline Tool

## Overview

Fred's a plumber who knows how to pipe things together properly. He takes the classic Unix philosophy of composable tools and brings it to .NET with modern performance. Fred's pipe combines **F**ind, g**RE**p, and se**D** into one smooth, flowing pipeline.

## What Fred Does

Fred's pipe has four stages that data flows through:

1. **Find Files** - Discovers files in specified paths
2. **Filter Files** - Applies file type and pattern filters (optional)
3. **Grep Files** - Searches for regex patterns in content (optional)  
4. **Sed Transform** - Applies sed script transformations (optional)

Each stage is optional - Fred's pipe adapts to whatever job needs doing.

## Core Architecture

### FredsPipe Class
```csharp
public class FredsPipe
{
    private readonly FredsConfig _config;
    
    public FredsPipe(FredsConfig config) { ... }
    
    // Main pipeline execution
    public async Task<FredsResult> ExecuteAsync(IFileSystem fileSystem)
    {
        // Fred's pipeline: find → filter → grep → sed
        // Each stage flows into the next
    }
}
```

### FredsConfig Structure
```csharp
public class FredsConfig
{
    public required List<string> Paths { get; set; }            // Where to start looking
    public bool Recursive { get; set; } = true;                // Dig deep into directories
    
    // Optional pipeline stages
    public FilterConfig? FilterConfig { get; set; }            // File filtering
    public RegexConfig? RegexConfig { get; set; }              // Pattern searching  
    public SedConfig? SedConfig { get; set; }                  // Content transformation
    
    // Flow control
    public FredsOutputConfig Output { get; set; } = new();     // What comes out the end
    public int ParallelWorkers { get; set; } = 4;              // How many pipes to run
    public bool DryRun { get; set; } = false;                  // Test the flow first
}
```

## Building Fred's Pipe

Use `FredsPipeBuilder` to construct pipelines:

```csharp
// Simple file search
var findJSFiles = FredsPipeBuilder.Create()
    .Paths("src/", "lib/")
    .FilterFiles(f => f.FileTypes = ["js", "ts"])
    .Build();

// Classic Unix pipeline: find | grep | sed
var refactorLogger = FredsPipeBuilder.Create()
    .Paths("src/")                                      // find src/
    .FilterFiles(f => f.FileTypes = ["js"])             // *.js files
    .SearchPattern("console\\.log\\(")                  // grep console.log
    .Transform("s/console\\.log(/logger.debug(/g")      // sed replacement
    .Build();

// Mass configuration update
var updateConfigs = FredsPipeBuilder.Create()
    .Paths("package.json", "setup.py", "Cargo.toml")   // specific files
    .Transform("s/version.*1\\.0/version = \"2.0\"/g")  // no grep needed
    .Build();
```

## Pipeline Configuration

### Filter Stage (Optional)
```csharp
public class FilterConfig
{
    public List<string>? FileTypes { get; set; }        // ["js", "ts", "py"]
    public List<string>? IncludePatterns { get; set; }  // ["*.test.js"]
    public List<string>? ExcludePatterns { get; set; }  // ["node_modules/", ".git/"]
    public string? MaxFileSize { get; set; }            // "10MB"
    public bool SkipBinaryFiles { get; set; } = true;   // Keep the flow clean
}
```

### Grep Stage (Optional)
```csharp
public class RegexConfig
{
    public required string Pattern { get; set; }        // What to search for
    public bool CaseSensitive { get; set; } = true;     // Exact matching
    public bool WholeWord { get; set; } = false;        // Word boundaries
    public bool Multiline { get; set; } = false;        // Cross-line patterns
    public int MaxMatchesPerFile { get; set; } = 1000;  // Flow control
    public bool IncludeContext { get; set; } = true;    // Surrounding lines
    public int ContextLines { get; set; } = 2;          // How much context
}
```

### Sed Stage (Optional)
```csharp
public class SedConfig
{
    public required string Script { get; set; }         // Transformation script
    public string Target { get; set; } = "matched_files"; // What to transform
    public bool CreateBackups { get; set; } = false;    // Safety first
    public string BackupSuffix { get; set; } = ".bak";  // Backup naming
}
```

#### Sed Target Options
- `"matched_files"` - Only transform files that had grep matches
- `"all_files"` - Transform all files in the pipeline
- `"specific_files"` - Transform files specified in SedConfig.Files

## Output Control

Fred's pipe can produce different amounts of output depending on your needs:

### FredsOutputConfig
```csharp
public class FredsOutputConfig
{
    // What flows out of the pipe
    public bool IncludeFileList { get; set; } = true;       // All files found
    public bool IncludeMatches { get; set; } = true;        // Detailed match info
    public bool IncludeDiffs { get; set; } = false;         // Transformation diffs
    public bool IncludeContext { get; set; } = true;        // Context around matches
    
    // Flow control - prevent flooding
    public int MaxMatchesPerFile { get; set; } = 100;       
    public int MaxTotalMatches { get; set; } = 10000;       
    public int MaxFileListSize { get; set; } = 1000;        
    
    // Compact modes
    public bool SummaryOnly { get; set; } = false;          // Just stats
    public bool UltraCompact { get; set; } = false;         // Minimal output
}
```

### Output Modes

```csharp
// For huge operations - minimal flow
var pipeline = FredsPipeBuilder.Create()
    .Paths("massive-codebase/")
    .SearchPattern("deprecated")
    .UltraCompact()     // Tiny JSON output
    .Build();

// Detailed analysis - full flow
var pipeline = FredsPipeBuilder.Create()
    .Paths("src/")
    .SearchPattern("TODO")
    .Transform("s/TODO:/✅ DONE:/g")
    .FullOutput()       // Complete details
    .Build();
```

## Results Structure

### FredsResult
```csharp
public class FredsResult
{
    public bool Success { get; set; }                       // Did the pipe work?
    public string? Error { get; set; }                      // What went wrong?
    
    // Pipeline flow outputs
    public List<string> Files { get; set; } = new();                    // find results
    public List<FileMatch> MatchingFiles { get; set; } = new();         // grep results
    public List<TransformResult>? TransformedFiles { get; set; }        // sed results
    
    // Flow summary
    public FredsStats Stats { get; set; } = new();
}
```

### FredsStats - Fred's Work Summary
```csharp
public class FredsStats
{
    public int FilesFound { get; set; }                     // "Fred found 156 files"
    public int FilesAfterFilter { get; set; }               // "Fred filtered to 89"
    public int FilesWithMatches { get; set; }               // "Fred grepped 12 matches"
    public int TotalMatches { get; set; }                   // "Fred found 47 patterns"
    public int FilesTransformed { get; set; }               // "Fred sed-ed 12 files"
    public int TotalChanges { get; set; }                   // "Fred made 47 changes"
    public int TotalTimeMs { get; set; }                    // "Fred finished in 234ms"
    
    // Pipeline stage timing (for flow optimization)
    public Dictionary<string, int> StageTimings { get; set; } = new()
    {
        ["find"] = 0,
        ["filter"] = 0,
        ["grep"] = 0, 
        ["sed"] = 0
    };
}
```

## Usage Examples

### Basic File Discovery
```csharp
var findFiles = FredsPipeBuilder.Create()
    .Paths("src/", "lib/")
    .FilterFiles(f => 
    {
        f.FileTypes = ["js", "ts", "jsx", "tsx"];
        f.ExcludePatterns = ["node_modules/", "*.min.js"];
    })
    .Build();

var result = await findFiles.ExecuteAsync(fileSystem);
// Fred found 245 files, filtered to 89 TypeScript/JavaScript files
```

### Search for Patterns
```csharp
var findTodos = FredsPipeBuilder.Create()
    .Paths(".")
    .SearchPattern("TODO|FIXME|HACK", r => 
    {
        r.CaseSensitive = false;
        r.IncludeContext = true;
        r.ContextLines = 2;
    })
    .Build();

var result = await findTodos.ExecuteAsync(fileSystem);  
// Fred found TODOs in 15 files with 43 total matches
```

### Classic Refactoring Pipeline
```csharp
var modernizeJS = FredsPipeBuilder.Create()
    .Paths("src/")
    .FilterFiles(f => 
    {
        f.FileTypes = ["js"];
        f.ExcludePatterns = ["*.min.js", "vendor/"];
    })
    .SearchPattern("var\\s+(\\w+)\\s*=")                    // Find var declarations
    .Transform("s/var (\\w+)/const \\1/g", s =>            // Convert to const
    {
        s.CreateBackups = true;
        s.BackupSuffix = ".pre-modernize";
        s.Target = "matched_files";                         // Only matched files
    })
    .DryRun()                                               // Test the flow first
    .Build();

var result = await modernizeJS.ExecuteAsync(fileSystem);
```

### Mass Configuration Update  
```csharp
var bumpVersion = FredsPipeBuilder.Create()
    .Paths("package.json", "setup.py", "Cargo.toml", "pom.xml")
    // No FilterConfig - specific files listed
    // No RegexConfig - transform all specified files  
    .Transform("s/version.*1\\.0.*/version = \"2.0\"/g", s =>
    {
        s.CreateBackups = true;
        s.Target = "all_files";                             // All specified files
    })
    .Build();

var result = await bumpVersion.ExecuteAsync(fileSystem);
```

### Parallel Processing for Large Codebases
```csharp
var largeScan = FredsPipeBuilder.Create()
    .Paths("enterprise-monorepo/")
    .FilterFiles(f => 
    {
        f.FileTypes = ["java", "scala", "kt"];
        f.ExcludePatterns = ["target/", ".git/", "*.class"];
        f.MaxFileSize = "5MB";
    })
    .SearchPattern("@Deprecated")
    .Parallel(16)                                           // 16 parallel pipes
    .UltraCompact()                                         // Minimal output
    .Build();

var result = await largeScan.ExecuteAsync(fileSystem);
```

## Output Examples

### Ultra-Compact (Minimal Flow)
Perfect for massive operations where you just need to know what changed:

```json
{
  "success": true,
  "modifiedFiles": ["src/app.js", "src/utils.js"],
  "stats": {
    "filesModified": 2,
    "totalChanges": 347,
    "totalTimeMs": 123
  }
}
```

### Full Flow (Detailed Analysis)
Complete pipeline results for smaller operations:

```json
{
  "success": true,
  "files": ["src/app.js", "src/utils.js", "src/helper.js"],
  "matchingFiles": [
    {
      "file": "src/app.js",
      "matches": [
        {
          "lineNumber": 42,
          "column": 8,
          "content": "console.log('debug info');",
          "match": "console.log",
          "contextBefore": ["function debugInfo() {", "  // Log debug information"],
          "contextAfter": ["  return data;", "}"]
        }
      ]
    }
  ],
  "transformedFiles": [
    {
      "file": "src/app.js",
      "success": true,
      "changesMade": 3,
      "diff": "-console.log('debug');\n+logger.debug('debug');"
    }
  ],
  "stats": {
    "filesFound": 156,
    "filesAfterFilter": 89,
    "filesWithMatches": 12,
    "totalMatches": 47,
    "filesTransformed": 12,
    "totalChanges": 47,
    "totalTimeMs": 234,
    "stageTimings": {
      "find": 45,
      "filter": 12,
      "grep": 89,
      "sed": 88
    }
  }
}
```

## File System Abstraction

Fred's pipe works through the `IFileSystem` interface for testability:

```csharp
public interface IFileSystem
{
    Task<IEnumerable<string>> ExpandPathsAsync(IEnumerable<string> paths, bool recursive = true);
    Task<bool> FileExistsAsync(string path);
    Task<string> ReadFileAsync(string path);
    Task WriteFileAsync(string path, string content);
    Task<FileInfo> GetFileInfoAsync(string path);
    Task<IAsyncEnumerable<string>> ReadLinesAsync(string path);
}

// Real implementation
public class FileSystemAdapter : IFileSystem { ... }

// Test implementation  
public class MockFileSystem : IFileSystem { ... }
```

## Performance Optimizations

Fred's pipe is built for speed:

### Parallel Flow
- Configurable worker pool for concurrent file processing
- Smart load balancing across workers
- Memory-efficient streaming for large files

### Smart Pattern Detection
- Literal string optimization for non-regex patterns
- SIMD vectorization where possible
- Compiled regex caching

### Flow Control
- Early termination when limits are reached
- Streaming results to prevent memory buildup
- Configurable timeouts and resource limits

### Memory Management
- Array pooling for high-frequency allocations
- Span<T> usage for zero-copy operations
- Incremental garbage collection friendly

## Error Handling

Fred's pipe handles problems gracefully:

```csharp
public class FredsResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }                      // Overall pipeline error
    public List<string> Warnings { get; set; } = new();     // Non-fatal issues
    
    // Partial results when some operations fail
    public bool PartialSuccess { get; set; }                // Some files processed
    public List<FileError> FileErrors { get; set; } = new(); // Per-file issues
}

public class FileError
{
    public required string File { get; set; }
    public required string Error { get; set; }
    public string Stage { get; set; } = "";                 // Which pipeline stage failed
}
```

## Testing Fred's Pipe

Fred's pipe is designed for comprehensive testing:

```csharp
[TestFixture]
public class FredsPipeTests
{
    private MockFileSystem _fileSystem;

    [SetUp] 
    public void SetUp()
    {
        _fileSystem = new MockFileSystem();
    }

    [Test]
    public async Task FredsPipe_FindAndGrep_FlowsCorrectly()
    {
        // Arrange
        _fileSystem.AddFile("/src/app.js", "console.log('hello');");
        _fileSystem.AddFile("/src/utils.js", "const x = 42;");
        
        var pipe = FredsPipeBuilder.Create()
            .Paths("/src")
            .SearchPattern("console\\.log")
            .Build();

        // Act
        var result = await pipe.ExecuteAsync(_fileSystem);

        // Assert - Fred found the right stuff
        Assert.That(result.Success, Is.True);
        Assert.That(result.Files, Has.Count.EqualTo(2));
        Assert.That(result.MatchingFiles, Has.Count.EqualTo(1));
        Assert.That(result.MatchingFiles[0].File, Is.EqualTo("/src/app.js"));
    }
}
```

## Integration Options

### MCP Tool Integration
```csharp
[MCPTool("fred")]
[Description("Fred's pipe: unified find, grep, sed pipeline tool")]
public class FredsMCPTool : IMCPTool
{
    public async Task<MCPToolResult> ExecuteAsync(JsonElement arguments)
    {
        var request = JsonSerializer.Deserialize<FredsRequest>(arguments.GetRawText());
        var fredsPipe = BuildFromMCPRequest(request);
        var result = await fredsPipe.ExecuteAsync(new FileSystemAdapter());
        return MCPToolResult.Success(JsonSerializer.Serialize(result));
    }
}
```

### Console Application
```csharp
public class FredsConsole
{
    public static async Task<int> Main(string[] args)
    {
        var pipe = ParseCommandLineArgs(args);
        var result = await pipe.ExecuteAsync(new FileSystemAdapter());
        
        OutputResults(result);
        return result.Success ? 0 : 1;
    }
}
```

## Why Fred's Pipe?

Fred's pipe brings the **Unix philosophy** of composable tools into the modern .NET ecosystem:

✅ **Composable** - Mix and match find/grep/sed stages as needed  
✅ **Testable** - Pure functions with dependency injection  
✅ **Performant** - Parallel processing with smart optimizations  
✅ **Scalable** - Handles everything from small scripts to enterprise codebases  
✅ **Observable** - Detailed stats and timing for each pipeline stage  
✅ **Flexible** - Ultra-compact to full detailed output modes  
✅ **Safe** - Dry-run mode, backups, and graceful error handling  

Fred keeps the data flowing smoothly from discovery to transformation, just like a well-built plumbing system. No leaks, no clogs, just clean, efficient processing of your codebase! 🔧🚰✨