using System.Diagnostics;
using NUnit.Framework;

namespace JqValidation.Tests;

/// <summary>
/// Oracle test suite for jq. Each test runs the real jq binary and njq,
/// asserting that njq produces identical output and exit codes to real jq.
/// All tests are safe to run in parallel.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.Children)]
public class JqOracleTests
{
    private const string JqPath = "/usr/bin/jq";
    private string _njqBin = string.Empty;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var buildDir = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."));
        var psi = new ProcessStartInfo("dotnet", $"build {Path.Combine(buildDir, "njq", "njq.csproj")} -c Debug -o {Path.Combine(buildDir, "njq", "bin", "oracle-test")}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        var proc = Process.Start(psi)!;
        proc.WaitForExit(60000);
        Assert.That(proc.ExitCode, Is.EqualTo(0), "njq build failed");
        _njqBin = Path.Combine(buildDir, "njq", "bin", "oracle-test", "njq");
    }

    [SetUp]
    public void SetUp()
    {
        if (!File.Exists(JqPath))
            Assert.Ignore($"jq not found at {JqPath}; skipping oracle tests.");
    }

    // -------------------------------------------------------------------------
    // Infrastructure
    // -------------------------------------------------------------------------

    private void RunJq(string expr, string input, params string[] extraArgs)
    {
        var args = new List<string>();
        for (int i = 0; i < extraArgs.Length; i++) args.Add(extraArgs[i]);
        args.Add(expr);

        var jqResult = RunProcess(JqPath, args.ToArray(), input);
        var njqResult = RunProcess(_njqBin, args.ToArray(), input);

        Assert.That(njqResult.ExitCode, Is.EqualTo(jqResult.ExitCode),
            $"njq exit code should match jq.\n  expr: {expr}\n  args: {string.Join(" ", extraArgs)}\n  input: {Truncate(input)}\n  jq stdout: {Truncate(jqResult.Output)}\n  njq stdout: {Truncate(njqResult.Output)}\n  jq stderr: {Truncate(jqResult.Stderr)}\n  njq stderr: {Truncate(njqResult.Stderr)}");
        Assert.That(njqResult.Output, Is.EqualTo(jqResult.Output),
            $"njq output should match jq.\n  expr: {expr}\n  args: {string.Join(" ", extraArgs)}\n  input: {Truncate(input)}");
    }

    private static (string Output, string Stderr, int ExitCode) RunProcess(string executable, string[] args, string input)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        for (int i = 0; i < args.Length; i++)
            process.StartInfo.ArgumentList.Add(args[i]);

        process.Start();
        try
        {
            process.StandardInput.Write(input);
            process.StandardInput.Close();
        }
        catch (IOException) { }

        string output = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(10000);
        return (output, stderr, process.ExitCode);
    }

    private static string Truncate(string s) => s.Length > 200 ? s[..200] + "..." : s;

    // =========================================================================
    // 1. Identity and field access (~15 tests)
    // =========================================================================

    [Test] public void Identity() => RunJq(".", "{\"a\":1,\"b\":2}");
    [Test] public void FieldAccess() => RunJq(".foo", "{\"foo\":42,\"bar\":99}");
    [Test] public void NestedFieldAccess() => RunJq(".foo.bar", "{\"foo\":{\"bar\":42}}");
    [Test] public void FieldAccessNull() => RunJq(".foo", "null");
    [Test] public void FieldAccessMissing() => RunJq(".missing", "{\"foo\":1}");
    [Test] public void BracketFieldAccess() => RunJq(".[\"foo\"]", "{\"foo\":42}");
    [Test] public void ArrayIndex0() => RunJq(".[0]", "[10,20,30]");
    [Test] public void ArrayIndexNeg1() => RunJq(".[-1]", "[10,20,30]");
    [Test] public void ArrayIndexOutOfBounds() => RunJq(".[10]", "[1,2,3]");
    [Test] public void ArraySlice() => RunJq(".[2:5]", "[0,1,2,3,4,5,6]");
    [Test] public void ArraySliceFrom() => RunJq(".[3:]", "[0,1,2,3,4,5]");
    [Test] public void ArraySliceTo() => RunJq(".[:3]", "[0,1,2,3,4,5]");
    [Test] public void ArraySliceNegative() => RunJq(".[-2:]", "[0,1,2,3,4]");
    [Test] public void StringSlice() => RunJq(".[2:5]", "\"hello world\"");
    [Test] public void IdentityNull() => RunJq(".", "null");

    // =========================================================================
    // 2. Iteration (~10 tests)
    // =========================================================================

    [Test] public void ArrayIteration() => RunJq(".[]", "[1,2,3]");
    [Test] public void ObjectIteration() => RunJq(".[]", "{\"a\":1,\"b\":2}");
    [Test] public void NestedArrayIteration() => RunJq(".foo[]", "{\"foo\":[1,2,3]}");
    [Test] public void IterateAndIndex() => RunJq(".[][0]", "[[1,2],[3,4]]");
    [Test] public void IterateAndField() => RunJq(".[].name", "[{\"name\":\"a\"},{\"name\":\"b\"}]");
    [Test] public void IterateOptional() => RunJq(".[]?", "null");
    [Test] public void IterateEmpty() => RunJq(".[]", "[]");
    [Test] public void DoubleIterate() => RunJq(".[][]", "[[1,2],[3,4]]");
    [Test] public void ObjectFieldIterate() => RunJq(".foo[]", "{\"foo\":{\"a\":1,\"b\":2}}");
    [Test] public void IterateStrings() => RunJq("[.[] | length]", "[\"hello\",\"hi\",\"world\"]");

    // =========================================================================
    // 3. Pipe and comma (~10 tests)
    // =========================================================================

    [Test] public void SimplePipe() => RunJq(".foo | .bar", "{\"foo\":{\"bar\":42}}");
    [Test] public void PipeChain() => RunJq(".[] | . * 2", "[1,2,3]");
    [Test] public void CommaMultipleOutputs() => RunJq(".a, .b", "{\"a\":1,\"b\":2}");
    [Test] public void CommaInArray() => RunJq("[.a, .b]", "{\"a\":1,\"b\":2}");
    [Test] public void PipeAfterComma() => RunJq(".[] | . , . * 2", "[1,2]");
    [Test] public void MultiplePipes() => RunJq(". | . | .", "42");
    [Test] public void PipeToBuiltin() => RunJq(".[] | tostring", "[1,2,3]");
    [Test] public void CommaThreeValues() => RunJq(".a, .b, .c", "{\"a\":1,\"b\":2,\"c\":3}");
    [Test] public void PipeIterate() => RunJq(". | .[] | . + 1", "[1,2,3]");
    [Test] public void EmptyPipe() => RunJq("empty | .", "1");

    // =========================================================================
    // 4. Construction (~10 tests)
    // =========================================================================

    [Test] public void ObjectConstruction() => RunJq("{name: .foo, value: .bar}", "{\"foo\":\"x\",\"bar\":42}");
    [Test] public void ArrayConstruction() => RunJq("[.[] | . * 2]", "[1,2,3]");
    [Test] public void EmptyArray() => RunJq("[]", "null");
    [Test] public void EmptyObject() => RunJq("{}", "null");
    [Test] public void DynamicKey() => RunJq("{(.key): .value}", "{\"key\":\"name\",\"value\":42}");
    [Test] public void ObjectShorthand() => RunJq("{a,b}", "{\"a\":1,\"b\":2,\"c\":3}");
    [Test] public void StringInterpolation() => RunJq("\"hello \\(.name)\"", "{\"name\":\"world\"}");
    [Test] public void NestedInterpolation() => RunJq("\"\\(.a) + \\(.b) = \\(.a + .b)\"", "{\"a\":1,\"b\":2}");
    [Test] public void ArrayOfObjects() => RunJq("[.[] | {name: .}]", "[\"a\",\"b\"]");
    [Test] public void ConstructionWithPipe() => RunJq("{a: (.x | . + 1)}", "{\"x\":5}");

    // =========================================================================
    // 5. Conditionals and comparison (~10 tests)
    // =========================================================================

    [Test] public void IfThenElse() => RunJq("if . > 3 then \"big\" else \"small\" end", "5");
    [Test] public void IfThenElseFalse() => RunJq("if . > 3 then \"big\" else \"small\" end", "2");
    [Test] public void IfElif() => RunJq("if . > 3 then \"big\" elif . > 1 then \"medium\" else \"small\" end", "2");
    [Test] public void CompareEqual() => RunJq(". == 1", "1");
    [Test] public void CompareNotEqual() => RunJq(". != 1", "2");
    [Test] public void CompareLess() => RunJq(". < 3", "2");
    [Test] public void CompareGreater() => RunJq(". > 3", "5");
    [Test] public void CompareLessEqual() => RunJq(". <= 3", "3");
    [Test] public void CompareGreaterEqual() => RunJq(". >= 3", "2");
    [Test] public void CompareStrings() => RunJq(". == \"hello\"", "\"hello\"");

    // =========================================================================
    // 6. Arithmetic (~5 tests)
    // =========================================================================

    [Test] public void Addition() => RunJq(". + 1", "5");
    [Test] public void Subtraction() => RunJq(". - 1", "5");
    [Test] public void Multiplication() => RunJq(". * 3", "5");
    [Test] public void Modulo() => RunJq(". % 3", "10");
    [Test] public void StringConcat() => RunJq(". + \" world\"", "\"hello\"");
    [Test] public void ArrayConcat() => RunJq(". + [4,5]", "[1,2,3]");
    [Test] public void ObjectMerge() => RunJq(". + {\"c\":3}", "{\"a\":1,\"b\":2}");

    // =========================================================================
    // 7. Builtins - collections (~20 tests)
    // =========================================================================

    [Test] public void MapDoubles() => RunJq("map(. * 2)", "[1,2,3]");
    [Test] public void SelectFilter() => RunJq("[.[] | select(. > 2)]", "[1,2,3,4,5]");
    [Test] public void Sort() => RunJq("sort", "[3,1,2]");
    [Test] public void SortBy() => RunJq("sort_by(.a)", "[{\"a\":3},{\"a\":1},{\"a\":2}]");
    [Test] public void GroupBy() => RunJq("group_by(.a)", "[{\"a\":1},{\"a\":2},{\"a\":1}]");
    [Test] public void Unique() => RunJq("unique", "[1,2,1,3,2]");
    [Test] public void UniqueBy() => RunJq("[unique_by(.a) | .[]]", "[{\"a\":1,\"b\":\"x\"},{\"a\":1,\"b\":\"y\"},{\"a\":2,\"b\":\"z\"}]");
    [Test] public void Flatten() => RunJq("flatten", "[1,[2,[3]]]");
    [Test] public void FlattenDepth() => RunJq("flatten(1)", "[1,[2,[3]]]");
    [Test] public void Add() => RunJq("add", "[1,2,3]");
    [Test] public void AddStrings() => RunJq("add", "[\"a\",\"b\",\"c\"]");
    [Test] public void AddArrays() => RunJq("add", "[[1,2],[3,4]]");
    [Test] public void Any() => RunJq("any(. > 2)", "[1,2,3]");
    [Test] public void All() => RunJq("all(. > 0)", "[1,2,3]");
    [Test] public void Reverse() => RunJq("reverse", "[1,2,3]");
    [Test] public void Contains() => RunJq("contains({\"a\":1})", "{\"a\":1,\"b\":2}");
    [Test] public void Inside() => RunJq("inside({\"a\":1,\"b\":2})", "{\"a\":1}");
    [Test] public void Min() => RunJq("min", "[3,1,2]");
    [Test] public void Max() => RunJq("max", "[3,1,2]");
    [Test] public void MinBy() => RunJq("min_by(.a)", "[{\"a\":3},{\"a\":1},{\"a\":2}]");
    [Test] public void Range() => RunJq("[range(5)]", "null", "-n");
    [Test] public void RangeFromTo() => RunJq("[range(2;5)]", "null", "-n");
    [Test] public void RangeWithStep() => RunJq("[range(0;10;3)]", "null", "-n");

    // =========================================================================
    // 8. Builtins - strings (~15 tests)
    // =========================================================================

    [Test] public void Split() => RunJq("split(\" \")", "\"hello world\"");
    [Test] public void Join() => RunJq("join(\", \")", "[\"a\",\"b\",\"c\"]");
    [Test] public void Test_() => RunJq("test(\"foo\")", "\"foo bar\"");
    [Test] public void TestNoMatch() => RunJq("test(\"xyz\")", "\"foo bar\"");
    [Test] public void Match_() => RunJq("match(\"(foo) (bar)\")", "\"foo bar\"");
    [Test] public void CaptureNamed() => RunJq("capture(\"(?<a>\\\\w+) (?<b>\\\\w+)\")", "\"foo bar\"");
    [Test] public void Gsub() => RunJq("gsub(\"o\"; \"0\")", "\"hello world\"");
    [Test] public void Sub() => RunJq("sub(\"o\"; \"0\")", "\"hello world\"");
    [Test] public void AsciiDowncase() => RunJq("ascii_downcase", "\"HELLO\"");
    [Test] public void AsciiUpcase() => RunJq("ascii_upcase", "\"hello\"");
    [Test] public void Ltrimstr() => RunJq("ltrimstr(\"hello \")", "\"hello world\"");
    [Test] public void Rtrimstr() => RunJq("rtrimstr(\" world\")", "\"hello world\"");
    [Test] public void Startswith() => RunJq("startswith(\"hello\")", "\"hello world\"");
    [Test] public void Endswith() => RunJq("endswith(\"world\")", "\"hello world\"");
    [Test] public void Base64Encode() => RunJq("@base64", "\"hello\"");
    [Test] public void Base64Decode() => RunJq("@base64d", "\"aGVsbG8=\"");
    [Test] public void HtmlEscape() => RunJq("@html", "\"<div>hello</div>\"");

    // =========================================================================
    // 9. Builtins - objects (~10 tests)
    // =========================================================================

    [Test] public void Keys() => RunJq("keys", "{\"b\":2,\"a\":1}");
    [Test] public void KeysUnsorted() => RunJq("keys_unsorted", "{\"b\":2,\"a\":1}");
    [Test] public void Values() => RunJq("values", "{\"a\":1,\"b\":2}");
    [Test] public void Has() => RunJq("has(\"a\")", "{\"a\":1}");
    [Test] public void HasMissing() => RunJq("has(\"b\")", "{\"a\":1}");
    [Test] public void ToEntries() => RunJq("to_entries", "{\"a\":1,\"b\":2}");
    [Test] public void FromEntries() => RunJq("from_entries", "[{\"key\":\"a\",\"value\":1}]");
    [Test] public void WithEntries() => RunJq("with_entries(select(.value > 1))", "{\"a\":1,\"b\":2}");
    [Test] public void Del() => RunJq("del(.a)", "{\"a\":1,\"b\":2}");
    [Test] public void DelArrayIndex() => RunJq("del(.[1])", "[1,2,3]");

    // =========================================================================
    // 10. Builtins - types (~5 tests)
    // =========================================================================

    [Test] public void TypeNumber() => RunJq("type", "1");
    [Test] public void TypeString() => RunJq("type", "\"hi\"");
    [Test] public void TypeNull() => RunJq("type", "null");
    [Test] public void TypeBool() => RunJq("type", "true");
    [Test] public void TypeArray() => RunJq("type", "[]");
    [Test] public void TypeObject() => RunJq("type", "{}");
    [Test] public void Length_Number() => RunJq("length", "null");
    [Test] public void LengthString() => RunJq("length", "\"hello\"");
    [Test] public void LengthArray() => RunJq("length", "[1,2,3]");
    [Test] public void LengthObject() => RunJq("length", "{\"a\":1,\"b\":2}");
    [Test] public void Tostring() => RunJq("tostring", "42");
    [Test] public void Tonumber() => RunJq("tonumber", "\"42\"");

    // =========================================================================
    // 11. Variables and reduce (~10 tests)
    // =========================================================================

    [Test] public void VariableBinding() => RunJq(". as $x | $x * $x", "5");
    [Test] public void ReduceSum() => RunJq("reduce .[] as $x (0; . + $x)", "[1,2,3,4,5]");
    [Test] public void ReduceProduct() => RunJq("reduce .[] as $x (1; . * $x)", "[1,2,3,4,5]");
    [Test] public void Foreach() => RunJq("[foreach .[] as $x (0; . + $x)]", "[1,2,3]");
    [Test] public void ArgString() => RunJq("$name", "null", "-n", "--arg", "name", "world");
    [Test] public void ArgJson() => RunJq("$val + 1", "null", "-n", "--argjson", "val", "42");
    [Test] public void ArgInInterp() => RunJq("\"hello \\($name)\"", "null", "-n", "--arg", "name", "world");
    [Test] public void VariableInPipe() => RunJq(".[] | . as $x | {val: $x, double: ($x * 2)}", "[1,2]");

    // =========================================================================
    // 12. Functions (~5 tests)
    // =========================================================================

    [Test] public void DefSimple() => RunJq("def double(x): x * 2; double(5)", "null", "-n");
    [Test] public void DefRecursive() => RunJq("def fact(n): if n <= 1 then 1 else n * fact(n-1) end; fact(5)", "null", "-n");
    [Test] public void DefNoArgs() => RunJq("def addone: . + 1; 5 | addone", "null", "-n");
    [Test] public void DefFilterArg() => RunJq("def apply(f): . | f; 5 | apply(. * 2)", "null", "-n");
    [Test] public void DefMultipleArgs() => RunJq("def add3(a;b;c): a + b + c; add3(1;2;3)", "null", "-n");

    // =========================================================================
    // 13. Try/catch and optional (~5 tests)
    // =========================================================================

    [Test] public void TrySuccess() => RunJq("try .foo", "{\"foo\":42}");
    [Test] public void TryFailSilent() => RunJq("[.[] | try .foo]", "[{\"foo\":1},\"string\",{\"foo\":2}]");
    [Test] public void AlternativeOp() => RunJq(".foo // \"default\"", "{}");
    [Test] public void AlternativeOpNull() => RunJq("null // \"fallback\"", "null");
    [Test] public void OptionalField() => RunJq(".foo?", "\"not an object\"");

    // =========================================================================
    // 14. CLI flags (~10 tests)
    // =========================================================================

    [Test] public void RawOutput() => RunJq(".", "\"hello\"", "-r");
    [Test] public void CompactOutput() => RunJq(".", "{\"a\":1,\"b\":[1,2]}", "-c");
    [Test] public void NullInput() => RunJq("1 + 2", "", "-n");
    [Test] public void SortKeysFlag() => RunJq(".", "{\"c\":3,\"a\":1,\"b\":2}", "-S");
    [Test] public void ExitStatusTrue() => RunJq(".", "true", "-e");
    [Test] public void ExitStatusFalse() => RunJq(".", "false", "-e");
    [Test] public void ExitStatusNull() => RunJq(".", "null", "-e");
    [Test] public void TabIndent() => RunJq(".", "{\"a\":1}", "--tab");
    [Test] public void SlurpMode() => RunJq(".", "1\n2\n3", "-s");
    [Test] public void RawInput() => RunJq(".", "hello\nworld", "-R");

    // =========================================================================
    // 15. Edge cases (~5 tests)
    // =========================================================================

    [Test] public void EmptyInputNull() => RunJq(".", "null");
    [Test] public void BooleanTrue() => RunJq(".", "true");
    [Test] public void BooleanFalse() => RunJq(".", "false");
    [Test] public void LargeNumber() => RunJq(". + 1", "9999999999999");
    [Test] public void UnicodeString() => RunJq(".", "\"\\u00e9\\u00e8\"");

    // =========================================================================
    // Additional coverage
    // =========================================================================

    [Test] public void RecursiveDescent() => RunJq(".. | numbers", "{\"a\":{\"b\":2},\"c\":3}");
    [Test] public void Paths() => RunJq("[paths]", "{\"a\":{\"b\":1},\"c\":2}");
    [Test] public void GetPath() => RunJq("getpath([\"a\",\"b\"])", "{\"a\":{\"b\":1}}");
    [Test] public void SetPath() => RunJq("setpath([\"a\",\"c\"]; 2)", "{\"a\":{\"b\":1}}");
    [Test] public void Indices() => RunJq("[indices(\"bc\")]", "\"abcabc\"");
    [Test] public void Index_() => RunJq("index(\"bc\")", "\"abcabc\"");
    [Test] public void Rindex() => RunJq("rindex(\"bc\")", "\"abcabc\"");
    [Test] public void Explode() => RunJq("explode", "\"AB\"");
    [Test] public void Implode() => RunJq("implode", "[65,66]");
    [Test] public void Tojson() => RunJq("tojson", "{\"a\":1}");
    [Test] public void Fromjson() => RunJq("fromjson", "\"{\\\"a\\\":1}\"");
    [Test] public void Walk() => RunJq("walk(if type == \"number\" then . * 2 else . end)", "{\"a\":{\"b\":1},\"c\":[2,3]}");
    [Test] public void Limit() => RunJq("[limit(3; range(10))]", "null", "-n");
    [Test] public void First() => RunJq("first(range(10))", "null", "-n");
    [Test] public void Csv() => RunJq(".[] | @csv", "[[\"a\",1],[\"b\",2]]");
    [Test] public void Tsv() => RunJq(".[] | @tsv", "[[\"a\",1],[\"b\",2]]");
    [Test] public void Uri() => RunJq("@uri", "\"hello world\"");
    [Test] public void Not() => RunJq("not", "true");
    [Test] public void NotFalse() => RunJq("not", "false");
    [Test] public void BoolAnd() => RunJq("true and false", "null", "-n");
    [Test] public void BoolOr() => RunJq("true or false", "null", "-n");
    [Test] public void NegateNumber() => RunJq("-(. + 1)", "5");
    [Test] public void Abs() => RunJq("abs", "-5");
    [Test] public void Floor() => RunJq("floor", "3.7");
    [Test] public void Ceil() => RunJq("ceil", "3.2");
    [Test] public void Round() => RunJq("round", "3.5");
    [Test] public void MapValues() => RunJq("map_values(. + 1)", "{\"a\":1,\"b\":2}");
    [Test] public void ArraySubtraction() => RunJq(". - [2,3]", "[1,2,3,4]");
    [Test] public void AssignUpdate() => RunJq(".a |= . + 1", "{\"a\":1,\"b\":2}");
    [Test] public void AddAssign() => RunJq(".a += 10", "{\"a\":1,\"b\":2}");
    [Test] public void Recurse_Builtin() => RunJq("[recurse(.a?) | .a? // empty]", "{\"a\":{\"a\":{\"a\":1}}}");
    [Test] public void NullInputExpr() => RunJq("\"hello\"", "", "-n");
    [Test] public void MaxBy() => RunJq("max_by(.a)", "[{\"a\":3},{\"a\":1},{\"a\":2}]");
    [Test] public void LabelBreak() => RunJq("label $out | foreach .[] as $x (0; . + $x; if . > 5 then ., break $out else . end)", "[1,2,3,4,5]");
    [Test] public void Scalars() => RunJq("[.[] | scalars]", "[1,\"a\",[],{}]");
    [Test] public void BooleanNot() => RunJq("[true, false, null] | map(not)", "null", "-n");
    [Test] public void ReduceRange() => RunJq("reduce range(5) as $x (0; . + $x)", "null", "-n");
}
