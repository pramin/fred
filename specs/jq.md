# njq — jq-compatible JSON processor for .NET

## Overview

njq is a jq-compatible JSON query/transform tool. It follows Fred's compile-once pattern: `JqEngine.Compile(expr)` returns a `JqScript` that can be reused across inputs. The CLI (`njq`) is a drop-in replacement for `/usr/bin/jq` for the most common operations.

## Scope — What to implement

Focus on the features LLMs actually use. jq has a massive spec; we implement the 80% that matters:

### Tier 1 — Must have (oracle-tested)
- **Identity**: `.`
- **Field access**: `.foo`, `.foo.bar`, `.["key"]`
- **Array index**: `.[0]`, `.[-1]`, `.[2:5]` (slicing)
- **Array/object iteration**: `.[]`, `.foo[]`
- **Pipe**: `expr | expr`
- **Comma** (multiple outputs): `.foo, .bar`
- **Parentheses**: `(expr)`
- **Object construction**: `{name: .foo, value: .bar}`, `{(.key): .value}`
- **Array construction**: `[expr]`
- **String interpolation**: `\(expr)` inside strings
- **Conditionals**: `if-then-elif-else-end`
- **Comparison**: `==`, `!=`, `<`, `>`, `<=`, `>=`
- **Boolean**: `and`, `or`, `not`
- **Arithmetic**: `+`, `-`, `*`, `/`, `%`
- **String concatenation**: `+` on strings
- **Try-catch**: `try-catch`, `?` (try without catch)
- **Optional access**: `.foo?`, `.[]?`
- **Builtins**:
  - `length`, `keys`, `keys_unsorted`, `values`, `has(key)`, `in(obj)`
  - `map(f)`, `select(f)`, `empty`, `error`
  - `add`, `any`, `all`, `flatten`, `range(n)`, `range(a;b)`
  - `type`, `null`, `true`, `false`, `infinite`, `nan`
  - `tostring`, `tonumber`, `ascii_downcase`, `ascii_upcase`
  - `ltrimstr`, `rtrimstr`, `startswith`, `endswith`, `test(re)`, `match(re)`, `capture(re)`
  - `split(s)`, `join(s)`, `gsub(re; s)`, `sub(re; s)`
  - `sort`, `sort_by(f)`, `group_by(f)`, `unique`, `unique_by(f)`
  - `reverse`, `contains`, `inside`
  - `to_entries`, `from_entries`, `with_entries(f)`
  - `paths`, `getpath(p)`, `setpath(p; v)`, `delpaths(ps)`
  - `env`, `$ENV`
  - `input`, `inputs`, `debug`, `stderr`
  - `limit(n; f)`, `first(f)`, `last(f)`, `nth(n; f)`
  - `indices(s)`, `index(s)`, `rindex(s)`
  - `min`, `max`, `min_by(f)`, `max_by(f)`
  - `ascii`, `explode`, `implode`
  - `tojson`, `fromjson`
  - `recurse`, `recurse(f)`, `walk(f)`
  - `leaf_paths`, `path(f)`
  - `del(f)`, `getpath`, `setpath`, `delpaths`
  - `@base64`, `@base64d`, `@uri`, `@csv`, `@tsv`, `@html`, `@json`, `@text`
- **Variable binding**: `expr as $var | expr`
- **Reduce**: `reduce expr as $var (init; update)`
- **Label-break**: `label $name | expr` (with `break $name`)
- **Foreach**: `foreach expr as $var (init; update; extract)`
- **Define functions**: `def name(args): body;`
- **Recursive descent**: `..`
- **Alternative operator**: `//`

### CLI flags
- `-r` / `--raw-output` — output raw strings (no quotes)
- `-R` / `--raw-input` — read each line as a string
- `-n` / `--null-input` — don't read input, use `null`
- `-c` / `--compact-output` — no pretty printing
- `-e` / `--exit-status` — exit 1 if last output is false/null
- `-s` / `--slurp` — read all inputs into array
- `-S` / `--sort-keys` — sort object keys
- `--arg name value` — bind `$name` to string value
- `--argjson name value` — bind `$name` to JSON value
- `--tab` — use tabs for indentation
- `--indent n` — set indentation level
- `--jsonargs` — remaining args are JSON values
- `--args` — remaining args are string values
- `-j` / `--join-output` — like -r but don't print newline after each output

### Exit codes
- `0` — success
- `1` — last output was false or null (with `-e`)
- `2` — usage error
- `5` — system error (I/O)

## Architecture

```
FredDotNet/
  JqEngine.cs        # Lexer, parser, interpreter, builtins

njq/
  Program.cs          # CLI: parse args, compile, execute, format output

JqValidation.Tests/
  JqOracleTests.cs    # njq vs /usr/bin/jq byte-for-byte comparison
```

### Engine API

```csharp
public static class JqEngine
{
    /// <summary>Compile a jq expression into a reusable script.</summary>
    public static JqScript Compile(string expression);
}

public sealed class JqScript
{
    /// <summary>Execute against a JSON string, returning (output, exitCode).</summary>
    public (string Output, int ExitCode) Execute(string jsonInput);

    /// <summary>Execute with streaming I/O.</summary>
    public int Execute(TextReader input, TextWriter output);
}

public sealed class JqOptions
{
    public bool RawOutput { get; set; }
    public bool RawInput { get; set; }
    public bool NullInput { get; set; }
    public bool CompactOutput { get; set; }
    public bool ExitStatus { get; set; }
    public bool Slurp { get; set; }
    public bool SortKeys { get; set; }
    public bool JoinOutput { get; set; }
    public int IndentWidth { get; set; } = 2;
    public bool UseTabs { get; set; }
    public Dictionary<string, string> StringArgs { get; } = new();
    public Dictionary<string, string> JsonArgs { get; } = new();
}

public sealed class JqException : Exception
{
    public JqException(string message) : base(message) { }
}
```

### Internal design

- **Lexer**: character-by-character tokenizer (no regex on hot path)
- **Parser**: recursive descent producing an AST
- **Interpreter**: tree-walking evaluator with `JqValue` wrapper around `JsonElement`/`JsonNode`
- Use `System.Text.Json` throughout — no Newtonsoft
- Thread-safe: `JqScript` holds immutable AST; fresh interpreter state per `Execute()` call
- No LINQ on hot paths

### JqValue

Wraps JSON values for the interpreter. Must handle jq's "multiple outputs" model (generators):

```csharp
// Each jq expression can produce 0..N values (generator model)
// The interpreter yields values via IEnumerable<JqValue> or callback
internal readonly struct JqValue
{
    // Backed by JsonNode for mutability during construction,
    // or JsonElement for immutable input traversal
}
```

## Oracle testing

Same pattern as grep/sed/awk/find oracles:

```csharp
[TestFixture]
[Parallelizable(ParallelScope.Children)]
public class JqOracleTests
{
    private const string JqPath = "/usr/bin/jq";
    private string _njqBin = string.Empty;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        // Build njq once
    }

    private (string Output, int ExitCode) RunJq(string expr, string input, params string[] extraArgs)
    {
        var jqResult = RunProcess(JqPath, ...);
        var njqResult = RunProcess(_njqBin, ...);
        Assert.That(njqResult.ExitCode, Is.EqualTo(jqResult.ExitCode));
        Assert.That(njqResult.Output, Is.EqualTo(jqResult.Output));
        return jqResult;
    }
}
```

### Test categories (target: 120+ tests)
1. **Identity and access** (~15): `.`, `.foo`, `.foo.bar`, `.[0]`, `.[-1]`, `.[2:5]`
2. **Iteration** (~10): `.[]`, `.foo[]`, nested iteration
3. **Pipe and comma** (~10): chaining, multiple outputs
4. **Construction** (~10): object/array construction, string interpolation
5. **Conditionals and comparison** (~10): if/then/else, ==, !=, <, >
6. **Arithmetic** (~5): +, -, *, /, % on numbers and strings
7. **Builtins — collections** (~20): map, select, sort, group_by, unique, flatten, etc.
8. **Builtins — strings** (~15): split, join, test, gsub, ascii_downcase, @base64, etc.
9. **Builtins — objects** (~10): keys, values, has, to_entries, with_entries, del
10. **Builtins — types** (~5): type, length, tostring, tonumber
11. **Variables and reduce** (~10): `as $var`, reduce, foreach
12. **Functions** (~5): def, recursive functions
13. **Try/catch and optional** (~5): try, `?`, `//`
14. **CLI flags** (~10): -r, -n, -s, -c, -S, --arg, --argjson
15. **Edge cases** (~5): empty input, null, unicode, large numbers
