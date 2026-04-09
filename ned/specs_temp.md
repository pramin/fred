# Sed Tool Specification for AI Agents

## Overview

This sed tool provides a JSON-based interface for applying sed scripts to multiple files. It wraps the standard sed command with a structured input format for batch processing and consistent error handling.

## Tool Interface

### Input Format

```json
{
  "script": "sed script text",
  "files": ["file1", "file2", "glob"]
}
```

### Parameters

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `script` | string | Yes | The sed script to execute |
| `files` | array[string] | Yes | List of files, file paths, or glob patterns |

### Output Format

```json
{
  "success": true,
  "results": [
    {
      "file": "path/to/file",
      "status": "success",
      "lines_processed": 150,
      "changes_made": 5,
      "output": "processed content (if requested)"
    }
  ],
  "errors": [],
  "summary": {
    "total_files": 3,
    "successful": 3,
    "failed": 0,
    "total_lines_processed": 450,
    "total_changes": 15
  }
}
```

### Error Format

```json
{
  "success": false,
  "results": [],
  "errors": [
    {
      "type": "script_error",
      "message": "Invalid regex pattern in script",
      "details": "Unmatched [ or [^"
    },
    {
      "type": "file_error", 
      "file": "nonexistent.txt",
      "message": "File not found"
    }
  ],
  "summary": {
    "total_files": 0,
    "successful": 0,
    "failed": 1,
    "total_lines_processed": 0,
    "total_changes": 0
  }
}
```

## Error Types

| Error Type | Description | Example |
|------------|-------------|---------|
| `script_error` | Invalid sed script syntax | Invalid regex, unmatched brackets |
| `file_error` | File access issues | File not found, permission denied |
| `glob_error` | Glob pattern issues | Invalid pattern, no matches |
| `permission_error` | Insufficient permissions | Cannot write to file |
| `validation_error` | Input validation failure | Empty script, invalid JSON |

## Tool Options

The tool can be configured with additional options:

```json
{
  "script": "s/old/new/g",
  "files": ["*.txt"],
  "options": {
    "in_place": true,
    "backup_suffix": ".bak",
    "extended_regex": true,
    "quiet": false,
    "dry_run": false,
    "include_output": false,
    "max_file_size": "100MB",
    "timeout": 30
  }
}
```

### Option Descriptions

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `in_place` | boolean | false | Edit files in place |
| `backup_suffix` | string | null | Create backup with suffix (requires in_place) |
| `extended_regex` | boolean | false | Use extended regular expressions |
| `quiet` | boolean | false | Suppress automatic printing |
| `dry_run` | boolean | false | Show what would be done without executing |
| `include_output` | boolean | false | Include processed content in results |
| `max_file_size` | string | "10MB" | Maximum file size to process |
| `timeout` | number | 60 | Timeout in seconds per file |

## Input Validation Rules

### Script Validation
- Must be non-empty string
- Must be valid sed syntax
- Cannot contain dangerous operations (e.g., arbitrary file writes outside working directory)
- Maximum length: 10,000 characters

### Files Validation
- Must be non-empty array
- Each file entry must be a string
- Maximum 1,000 files per request
- File paths must be relative to working directory or absolute paths within allowed directories
- Glob patterns must be valid
- Total expanded files cannot exceed 10,000

### Options Validation
- `backup_suffix`: Must be valid filename suffix (max 10 chars)
- `max_file_size`: Must be valid size string (e.g., "10MB", "1GB")
- `timeout`: Must be positive integer ≤ 300 seconds

## Usage Examples

### Basic Text Replacement
```json
{
  "script": "s/old_function/new_function/g",
  "files": ["src/*.js", "lib/*.ts"]
}
```

### Line Deletion
```json
{
  "script": "/debug/d",
  "files": ["logs/*.log"]
}
```

### In-place Editing with Backup
```json
{
  "script": "s/version = \"1.0\"/version = \"2.0\"/g",
  "files": ["config.txt"],
  "options": {
    "in_place": true,
    "backup_suffix": ".bak"
  }
}
```

### Complex Multi-line Script
```json
{
  "script": "1i\\# Auto-generated file\n/^$/d",
  "files": ["output/*.txt"],
  "options": {
    "extended_regex": true
  }
}
```

### Dry Run Mode
```json
{
  "script": "s/production/staging/g",
  "files": ["config/*.yml"],
  "options": {
    "dry_run": true,
    "include_output": true
  }
}
```

### Extract Specific Lines
```json
{
  "script": "10,20p",
  "files": ["large_file.txt"],
  "options": {
    "quiet": true,
    "include_output": true
  }
}
```

### Advanced Pattern Matching
```json
{
  "script": "/^ERROR:/,/^$/{s/ERROR:/WARNING:/; /^$/d}",
  "files": ["*.log"],
  "options": {
    "extended_regex": true,
    "max_file_size": "50MB"
  }
}
```

## Addressing

Sed commands can be preceded by addresses that specify which lines to operate on.

### Address Types

| Address | Description | Example |
|---------|-------------|---------|
| `n` | Line number n | `5d` (delete line 5) |
| `$` | Last line | `$p` (print last line) |
| `/pattern/` | Lines matching regex pattern | `/error/d` (delete lines containing "error") |
| `\cpatternc` | Lines matching pattern with custom delimiter | `\|/path|d` (delete lines containing "/path") |
| `addr1,addr2` | Range from addr1 to addr2 | `1,5d` (delete lines 1-5) |
| `addr1~step` | Every step-th line starting from addr1 | `1~2p` (print odd lines) |
| `0,addr2` | From start to addr2 (GNU extension) | `0,/pattern/d` |
| `addr1,+n` | From addr1 for n additional lines | `5,+3d` (delete lines 5-8) |
| `addr1,~n` | From addr1 to next multiple of n | `5,~10d` (delete from line 5 to line 10) |

### Address Modifiers

| Modifier | Description | Example |
|----------|-------------|---------|
| `!` | Negate address | `1!d` (delete all except first line) |

## Commands

### Basic Commands

| Command | Description | Syntax | Example |
|---------|-------------|--------|---------|
| `p` | Print pattern space | `[addr]p` | `1,5p` (print lines 1-5) |
| `d` | Delete pattern space, start next cycle | `[addr]d` | `/debug/d` (delete debug lines) |
| `q` | Quit immediately | `[addr]q [exit-code]` | `5q` (quit after line 5) |
| `Q` | Quit immediately without printing | `[addr]Q [exit-code]` | `5Q` (quit after line 5, no output) |
| `n` | Read next line into pattern space | `[addr]n` | `n` (skip current line) |
| `N` | Append next line to pattern space | `[addr]N` | `N` (join with next line) |
| `=` | Print current line number | `[addr]=` | `$=` (print total line count) |
| `l` | Print pattern space visually | `[addr]l [width]` | `l` (show special characters) |

### Substitution

| Command | Description | Syntax |
|---------|-------------|--------|
| `s` | Substitute | `[addr]s/pattern/replacement/[flags]` |

#### Substitution Flags

| Flag | Description | Example |
|------|-------------|---------|
| `g` | Replace all occurrences | `s/old/new/g` |
| `n` | Replace nth occurrence | `s/old/new/2` (replace 2nd occurrence) |
| `p` | Print if substitution made | `s/old/new/p` |
| `w file` | Write to file if substitution made | `s/old/new/w output.txt` |
| `i` or `I` | Case insensitive | `s/old/new/i` |
| `m` or `M` | Multi-line mode | `s/old/new/m` |
| `x` or `X` | Extended regex mode | `s/old/new/x` |

#### Replacement Special Characters

| Character | Description | Example |
|-----------|-------------|---------|
| `&` | Matched string | `s/word/[&]/` → `[word]` |
| `\n` | nth captured group | `s/\(.*\)/\1/` |
| `\L` | Convert to lowercase until `\E` | `s/.*/\L&\E/` |
| `\U` | Convert to uppercase until `\E` | `s/.*/\U&\E/` |
| `\l` | Convert next char to lowercase | `s/./\l&/` |
| `\u` | Convert next char to uppercase | `s/./\u&/` |
| `\E` | End case conversion | Used with `\L` or `\U` |

### Text Insertion/Appending

| Command | Description | Syntax |
|---------|-------------|--------|
| `i` | Insert text before line | `[addr]i\text` |
| `a` | Append text after line | `[addr]a\text` |
| `c` | Change (replace) lines | `[addr]c\text` |

### File Operations

| Command | Description | Syntax |
|---------|-------------|--------|
| `r` | Read file | `[addr]r filename` |
| `R` | Read one line from file | `[addr]R filename` |
| `w` | Write pattern space to file | `[addr]w filename` |
| `W` | Write first line of pattern space | `[addr]W filename` |

### Hold Space Operations

| Command | Description | Syntax |
|---------|-------------|--------|
| `h` | Copy pattern space to hold space | `[addr]h` |
| `H` | Append pattern space to hold space | `[addr]H` |
| `g` | Copy hold space to pattern space | `[addr]g` |
| `G` | Append hold space to pattern space | `[addr]G` |
| `x` | Exchange pattern and hold spaces | `[addr]x` |

### Flow Control

| Command | Description | Syntax |
|---------|-------------|--------|
| `b` | Branch to label or end | `[addr]b [label]` |
| `t` | Branch if substitution made | `[addr]t [label]` |
| `T` | Branch if no substitution made | `[addr]T [label]` |
| `:` | Define label | `:label` |

### Translation

| Command | Description | Syntax |
|---------|-------------|--------|
| `y` | Transliterate characters | `[addr]y/source/dest/` |

## Regular Expressions

### Basic Regular Expression (BRE) Metacharacters

| Character | Description | Example |
|-----------|-------------|---------|
| `.` | Any single character | `a.c` matches "abc", "adc" |
| `*` | Zero or more of preceding | `ab*c` matches "ac", "abc", "abbc" |
| `^` | Start of line | `^abc` matches "abc" at line start |
| `$` | End of line | `abc$` matches "abc" at line end |
| `[]` | Character class | `[abc]` matches "a", "b", or "c" |
| `[^]` | Negated character class | `[^abc]` matches any except "a", "b", "c" |
| `\{n\}` | Exactly n occurrences | `a\{3\}` matches "aaa" |
| `\{n,\}` | n or more occurrences | `a\{3,\}` matches "aaa", "aaaa", etc. |
| `\{n,m\}` | Between n and m occurrences | `a\{2,4\}` matches "aa", "aaa", "aaaa" |
| `\(` `\)` | Grouping | `\(abc\)*` matches "", "abc", "abcabc" |
| `\n` | Back-reference to nth group | `\(.*\)\1` matches repeated patterns |

### Extended Regular Expression (ERE) Additional Features

Available with `-r` or `-E` options:

| Character | Description | Example |
|-----------|-------------|---------|
| `+` | One or more occurrences | `ab+c` matches "abc", "abbc" |
| `?` | Zero or one occurrence | `ab?c` matches "ac", "abc" |
| `|` | Alternation | `abc|def` matches "abc" or "def" |
| `()` | Grouping (no backslash needed) | `(abc)*` |
| `{n}` | Exactly n occurrences | `a{3}` matches "aaa" |
| `{n,}` | n or more occurrences | `a{3,}` |
| `{n,m}` | Between n and m occurrences | `a{2,4}` |

### Character Classes

| Class | Description | Equivalent |
|-------|-------------|------------|
| `[:alnum:]` | Alphanumeric | `[a-zA-Z0-9]` |
| `[:alpha:]` | Alphabetic | `[a-zA-Z]` |
| `[:digit:]` | Digits | `[0-9]` |
| `[:lower:]` | Lowercase | `[a-z]` |
| `[:upper:]` | Uppercase | `[A-Z]` |
| `[:space:]` | Whitespace | `[ \t\n\r\f\v]` |
| `[:blank:]` | Space and tab | `[ \t]` |
| `[:punct:]` | Punctuation | `[!-/:-@\[-`{-~]` |
| `[:xdigit:]` | Hexadecimal | `[0-9A-Fa-f]` |
| `[:word:]` | Word characters | `[a-zA-Z0-9_]` |

## Common Use Cases and Examples

### Text Substitution
```bash
# Replace first occurrence
sed 's/old/new/' file.txt

# Replace all occurrences
sed 's/old/new/g' file.txt

# Case insensitive replacement
sed 's/old/new/gi' file.txt

# Replace only on lines containing pattern
sed '/pattern/s/old/new/g' file.txt
```

### Line Operations
```bash
# Delete lines
sed '5d' file.txt                    # Delete line 5
sed '1,3d' file.txt                  # Delete lines 1-3
sed '/pattern/d' file.txt            # Delete lines matching pattern

# Print specific lines
sed -n '1,5p' file.txt               # Print lines 1-5
sed -n '/pattern/p' file.txt         # Print lines matching pattern

# Insert/append text
sed '5i\New line before 5' file.txt  # Insert before line 5
sed '5a\New line after 5' file.txt   # Append after line 5
```

### Advanced Operations
```bash
# Swap lines
sed -n '1!G;h;$p' file.txt           # Reverse file

# Remove empty lines
sed '/^$/d' file.txt

# Remove leading whitespace
sed 's/^[ \t]*//' file.txt

# Number lines
sed '=' file.txt | sed 'N;s/\n/\t/'

# Extract between patterns
sed -n '/start/,/end/p' file.txt
```

## AI Agent Integration Guidelines

### Safety Considerations

1. **File Modification**: Always use `-i` with a backup suffix when modifying files
2. **Input Validation**: Validate patterns and addresses before execution
3. **Resource Limits**: Set timeouts for operations on large files
4. **Escape Special Characters**: Properly escape user input in patterns

### Recommended Usage Patterns

1. **Preview Mode**: Use `-n` with `p` to preview changes before applying
2. **Batch Processing**: Combine multiple operations in a single script
3. **Error Handling**: Check exit codes and validate output
4. **Logging**: Log all sed operations for debugging

### Common Error Patterns to Avoid

1. **Unescaped Delimiters**: Use alternative delimiters for paths (`s|/old/path|/new/path|`)
2. **BRE vs ERE**: Remember to use `-r` for extended regex features
3. **Address Ranges**: Validate that start address comes before end address
4. **In-place Editing**: Always create backups when using `-i`

### Performance Considerations

1. **Large Files**: Consider using `--unbuffered` for real-time processing
2. **Complex Patterns**: Pre-compile regex patterns when possible
3. **Memory Usage**: Use streaming approach for very large files
4. **Multiple Operations**: Combine operations in single sed command when possible

## Exit Codes

| Code | Description |
|------|-------------|
| 0 | Success |
| 1 | Invalid command, syntax, or regex |
| 2 | One or more input files could not be opened |
| 4 | I/O error or serious processing error |

## Implementation Notes for AI Agents

### Security Considerations
1. **Sandbox Execution**: Execute sed in a sandboxed environment
2. **Path Validation**: Validate all file paths to prevent directory traversal
3. **Resource Limits**: Enforce memory and time limits per operation
4. **Dangerous Commands**: Block commands that could write to arbitrary locations
5. **Input Sanitization**: Sanitize script content to prevent command injection

### Performance Optimization
1. **Batch Processing**: Process multiple files efficiently
2. **Streaming**: Use streaming for large files when possible  
3. **Caching**: Cache compiled regex patterns
4. **Parallel Processing**: Process independent files in parallel
5. **Progress Tracking**: Provide progress updates for long operations

### Error Recovery
1. **Partial Success**: Continue processing other files if one fails
2. **Rollback**: Provide rollback capability for in-place edits
3. **Retry Logic**: Implement retry for transient failures
4. **Detailed Logging**: Log all operations for debugging

### Best Practices
1. **Validation First**: Always validate inputs before processing
2. **Dry Run**: Offer dry-run mode for destructive operations
3. **Backups**: Create backups for in-place edits
4. **Clear Feedback**: Provide clear success/error messages
5. **Resource Monitoring**: Monitor resource usage during execution

## JSON Schema

### Input Schema
```json
{
  "$schema": "http://json-schema.org/draft-07/schema#",
  "type": "object",
  "required": ["script", "files"],
  "properties": {
    "script": {
      "type": "string",
      "minLength": 1,
      "maxLength": 10000,
      "description": "The sed script to execute"
    },
    "files": {
      "type": "array",
      "minItems": 1,
      "maxItems": 1000,
      "items": {
        "type": "string",
        "minLength": 1
      },
      "description": "Array of file paths or glob patterns"
    },
    "options": {
      "type": "object",
      "properties": {
        "in_place": {"type": "boolean", "default": false},
        "backup_suffix": {"type": "string", "maxLength": 10},
        "extended_regex": {"type": "boolean", "default": false},
        "quiet": {"type": "boolean", "default": false},
        "dry_run": {"type": "boolean", "default": false},
        "include_output": {"type": "boolean", "default": false},
        "max_file_size": {"type": "string", "default": "10MB"},
        "timeout": {"type": "integer", "minimum": 1, "maximum": 300, "default": 60}
      }
    }
  }
}
```

### Output Schema
```json
{
  "$schema": "http://json-schema.org/draft-07/schema#",
  "type": "object",
  "required": ["success", "results", "errors", "summary"],
  "properties": {
    "success": {"type": "boolean"},
    "results": {
      "type": "array",
      "items": {
        "type": "object",
        "required": ["file", "status"],
        "properties": {
          "file": {"type": "string"},
          "status": {"type": "string", "enum": ["success", "error"]},
          "lines_processed": {"type": "integer", "minimum": 0},
          "changes_made": {"type": "integer", "minimum": 0},
          "output": {"type": "string"},
          "error": {"type": "string"}
        }
      }
    },
    "errors": {
      "type": "array",
      "items": {
        "type": "object",
        "required": ["type", "message"],
        "properties": {
          "type": {"type": "string"},
          "message": {"type": "string"},
          "file": {"type": "string"},
          "details": {"type": "string"}
        }
      }
    },
    "summary": {
      "type": "object",
      "required": ["total_files", "successful", "failed", "total_lines_processed", "total_changes"],
      "properties": {
        "total_files": {"type": "integer", "minimum": 0},
        "successful": {"type": "integer", "minimum": 0},
        "failed": {"type": "integer", "minimum": 0},
        "total_lines_processed": {"type": "integer", "minimum": 0},
        "total_changes": {"type": "integer", "minimum": 0}
      }
    }
  }
}
```

This specification provides a complete JSON-based sed tool interface for AI agents with comprehensive error handling, validation, and safety features.