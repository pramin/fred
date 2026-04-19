// JqEngine for FredDotNet - jq-compatible JSON query/transform interpreter
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FredDotNet;

#region Public API

/// <summary>
/// Exception thrown by the jq engine for parse errors, type errors, and runtime errors.
/// </summary>
public sealed class JqException : Exception
{
    /// <summary>Initializes a new JqException with the specified message.</summary>
    public JqException(string message) : base(message) { }
    /// <summary>Initializes a new JqException with the specified message and inner exception.</summary>
    public JqException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Options controlling jq execution behavior and output formatting.
/// </summary>
public sealed class JqOptions
{
    /// <summary>Output raw strings without quotes (-r).</summary>
    public bool RawOutput { get; set; }
    /// <summary>Read each input line as a JSON string (-R).</summary>
    public bool RawInput { get; set; }
    /// <summary>Don't read any input; use null as input (-n).</summary>
    public bool NullInput { get; set; }
    /// <summary>Produce compact output without whitespace (-c).</summary>
    public bool CompactOutput { get; set; }
    /// <summary>Exit with code 1 if last output is false or null (-e).</summary>
    public bool ExitStatus { get; set; }
    /// <summary>Slurp all inputs into a single JSON array (-s).</summary>
    public bool Slurp { get; set; }
    /// <summary>Sort object keys in output (-S).</summary>
    public bool SortKeys { get; set; }
    /// <summary>Like raw output but without trailing newlines (-j).</summary>
    public bool JoinOutput { get; set; }
    /// <summary>Number of spaces for indentation (default 2).</summary>
    public int IndentWidth { get; set; } = 2;
    /// <summary>Use tabs instead of spaces for indentation.</summary>
    public bool UseTabs { get; set; }
    /// <summary>String arguments bound as $name variables.</summary>
    public Dictionary<string, string> StringArgs { get; } = new();
    /// <summary>JSON arguments bound as $name variables.</summary>
    public Dictionary<string, string> JsonArgs { get; } = new();
}

/// <summary>
/// A compiled jq script that can be executed against JSON inputs.
/// Thread-safe: the AST is immutable; fresh interpreter state is created per Execute() call.
/// </summary>
public sealed class JqScript
{
    private readonly JqNode _ast;
    private readonly string _expression;

    internal JqScript(JqNode ast, string expression) { _ast = ast; _expression = expression; }

    /// <summary>
    /// Execute this script against a JSON string input.
    /// Returns (output, exitCode) where output is the formatted result.
    /// </summary>
    public (string Output, int ExitCode) Execute(string jsonInput) => Execute(jsonInput, new JqOptions());

    /// <summary>
    /// Execute this script against a JSON string input with the specified options.
    /// Returns (output, exitCode) where output is the formatted result.
    /// </summary>
    public (string Output, int ExitCode) Execute(string jsonInput, JqOptions options)
    {
        var interpreter = new JqInterpreter(options);
        var sb = new StringBuilder();
        bool lastWasFalseOrNull = true;
        var inputs = PrepareInputs(jsonInput, options);
        for (int inputIdx = 0; inputIdx < inputs.Count; inputIdx++)
        {
            var input = inputs[inputIdx];
            var remainingInputs = new List<JqValue>();
            for (int ri = inputIdx + 1; ri < inputs.Count; ri++) remainingInputs.Add(inputs[ri]);
            interpreter.SetInputQueue(remainingInputs);
            try
            {
                foreach (var result in interpreter.Eval(_ast, input))
                {
                    string formatted = FormatValue(result, options);
                    sb.Append(formatted);
                    if (!options.JoinOutput || !IsString(result)) sb.Append('\n');
                    lastWasFalseOrNull = IsFalseOrNull(result);
                }
            }
            catch (EmptyException) { }
            int originalRemaining = inputs.Count - inputIdx - 1;
            int numConsumed = originalRemaining - remainingInputs.Count;
            inputIdx += numConsumed;
        }
        int exitCode = 0;
        if (options.ExitStatus && lastWasFalseOrNull) exitCode = 1;
        return (sb.ToString(), exitCode);
    }

    /// <summary>Execute this script with streaming I/O. Returns the exit code.</summary>
    public int Execute(TextReader input, TextWriter output) => Execute(input, output, new JqOptions());

    /// <summary>Execute this script with streaming I/O and the specified options. Returns the exit code.</summary>
    public int Execute(TextReader input, TextWriter output, JqOptions options)
    {
        string jsonInput = input.ReadToEnd();
        var (result, exitCode) = Execute(jsonInput, options);
        output.Write(result);
        return exitCode;
    }

    private static List<JqValue> PrepareInputs(string jsonInput, JqOptions options)
    {
        var inputs = new List<JqValue>();
        if (options.NullInput) { inputs.Add(JqValue.Null); return inputs; }
        if (options.RawInput)
        {
            if (options.Slurp)
            {
                var lines = new List<JqValue>();
                using var reader = new StringReader(jsonInput);
                string? line;
                while ((line = reader.ReadLine()) != null) lines.Add(JqValue.FromString(line));
                inputs.Add(JqValue.FromArray(lines));
            }
            else
            {
                using var reader = new StringReader(jsonInput);
                string? line;
                while ((line = reader.ReadLine()) != null) inputs.Add(JqValue.FromString(line));
            }
            return inputs;
        }
        var parsed = ParseJsonValues(jsonInput);
        if (options.Slurp) inputs.Add(JqValue.FromArray(parsed));
        else { for (int i = 0; i < parsed.Count; i++) inputs.Add(parsed[i]); }
        if (inputs.Count == 0) inputs.Add(JqValue.Null);
        return inputs;
    }

    private static List<JqValue> ParseJsonValues(string input)
    {
        var results = new List<JqValue>();
        var trimmed = input.AsSpan().Trim();
        if (trimmed.Length == 0) return results;
        var bytes = System.Text.Encoding.UTF8.GetBytes(trimmed.ToString());
        int offset = 0;
        while (offset < bytes.Length)
        {
            while (offset < bytes.Length && (bytes[offset] == (byte)' ' || bytes[offset] == (byte)'\t' || bytes[offset] == (byte)'\r' || bytes[offset] == (byte)'\n')) offset++;
            if (offset >= bytes.Length) break;
            var reader = new Utf8JsonReader(bytes.AsSpan(offset), new JsonReaderOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            try { using var doc = JsonDocument.ParseValue(ref reader); results.Add(JqValue.FromElement(doc.RootElement.Clone())); offset += (int)reader.BytesConsumed; }
            catch (JsonException) { break; }
        }
        return results;
    }

    private static string FormatValue(JqValue value, JqOptions options)
    {
        if ((options.RawOutput || options.JoinOutput) && IsString(value)) return value.GetString();
        return value.ToJsonString(options);
    }

    private static bool IsString(JqValue value) => value.Kind == JqValueKind.String;
    private static bool IsFalseOrNull(JqValue value) { if (value.Kind == JqValueKind.Null) return true; if (value.Kind == JqValueKind.Boolean) return !value.GetBoolean(); return false; }
}

/// <summary>Static entry point for compiling jq expressions.</summary>
public static class JqEngine
{
    /// <summary>Compile a jq expression into a reusable, thread-safe script.</summary>
    /// <param name="expression">The jq filter expression to compile.</param>
    /// <returns>A compiled JqScript ready for execution.</returns>
    /// <exception cref="JqException">Thrown if the expression has syntax errors.</exception>
    public static JqScript Compile(string expression)
    {
        var lexer = new JqLexer(expression);
        var tokens = lexer.Tokenize();
        var parser = new JqParser(tokens, expression);
        var ast = parser.Parse();
        return new JqScript(ast, expression);
    }
}

#endregion


#region Value types

/// <summary>
/// The kind of a jq value.
/// </summary>
internal enum JqValueKind
{
    Null,
    Boolean,
    Number,
    String,
    Array,
    Object,
}

/// <summary>
/// Represents a JSON value in the jq interpreter. Wraps System.Text.Json types.
/// </summary>
internal readonly struct JqValue : IEquatable<JqValue>
{
    private readonly object? _value;

    public readonly JqValueKind Kind;

    private JqValue(JqValueKind kind, object? value)
    {
        Kind = kind;
        _value = value;
    }

    public static readonly JqValue Null = new(JqValueKind.Null, null);
    public static readonly JqValue True = new(JqValueKind.Boolean, true);
    public static readonly JqValue False = new(JqValueKind.Boolean, false);

    public static JqValue FromBool(bool b) => b ? True : False;
    public static JqValue FromNumber(double d) => new(JqValueKind.Number, d);
    public static JqValue FromString(string s) => new(JqValueKind.String, s);
    public static JqValue FromArray(List<JqValue> arr) => new(JqValueKind.Array, arr);
    public static JqValue FromObject(List<KeyValuePair<string, JqValue>> pairs) => new(JqValueKind.Object, pairs);

    public static JqValue FromElement(JsonElement elem)
    {
        switch (elem.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return Null;
            case JsonValueKind.True:
                return True;
            case JsonValueKind.False:
                return False;
            case JsonValueKind.Number:
                return FromNumber(elem.GetDouble());
            case JsonValueKind.String:
                return FromString(elem.GetString()!);
            case JsonValueKind.Array:
            {
                var list = new List<JqValue>(elem.GetArrayLength());
                foreach (var item in elem.EnumerateArray())
                    list.Add(FromElement(item));
                return FromArray(list);
            }
            case JsonValueKind.Object:
            {
                var pairs = new List<KeyValuePair<string, JqValue>>();
                foreach (var prop in elem.EnumerateObject())
                    pairs.Add(new KeyValuePair<string, JqValue>(prop.Name, FromElement(prop.Value)));
                return FromObject(pairs);
            }
            default:
                return Null;
        }
    }

    public bool IsTruthy
    {
        get
        {
            if (Kind == JqValueKind.Null) return false;
            if (Kind == JqValueKind.Boolean) return (bool)_value!;
            return true;
        }
    }

    public bool GetBoolean()
    {
        if (Kind != JqValueKind.Boolean) throw new JqException("cannot convert " + TypeName + " to boolean");
        return (bool)_value!;
    }

    public double GetNumber()
    {
        if (Kind != JqValueKind.Number) throw new JqException("cannot convert " + TypeName + " to number");
        return (double)_value!;
    }

    public string GetString()
    {
        if (Kind != JqValueKind.String) throw new JqException("cannot convert " + TypeName + " to string");
        return (string)_value!;
    }

    public List<JqValue> GetArray()
    {
        if (Kind != JqValueKind.Array) throw new JqException("cannot iterate over " + TypeName);
        return (List<JqValue>)_value!;
    }

    public List<KeyValuePair<string, JqValue>> GetObject()
    {
        if (Kind != JqValueKind.Object) throw new JqException("cannot iterate over " + TypeName);
        return (List<KeyValuePair<string, JqValue>>)_value!;
    }

    public string TypeName
    {
        get
        {
            return Kind switch
            {
                JqValueKind.Null => "null",
                JqValueKind.Boolean => "boolean",
                JqValueKind.Number => "number",
                JqValueKind.String => "string",
                JqValueKind.Array => "array",
                JqValueKind.Object => "object",
                _ => "unknown",
            };
        }
    }

    public int Length
    {
        get
        {
            return Kind switch
            {
                JqValueKind.Null => 0,
                JqValueKind.String => GetString().Length,
                JqValueKind.Array => GetArray().Count,
                JqValueKind.Object => GetObject().Count,
                _ => throw new JqException(TypeName + " has no length"),
            };
        }
    }

    public JqValue GetField(string key)
    {
        if (Kind == JqValueKind.Null) return Null;
        if (Kind != JqValueKind.Object)
            throw new JqException("null (" + TypeName + ") and string (\"" + key + "\") cannot be iterated over");
        var obj = GetObject();
        for (int i = 0; i < obj.Count; i++)
        {
            if (obj[i].Key == key)
                return obj[i].Value;
        }
        return Null;
    }

    public JqValue GetIndex(int index)
    {
        if (Kind == JqValueKind.Null) return Null;
        if (Kind != JqValueKind.Array)
            throw new JqException(TypeName + " cannot be indexed");
        var arr = GetArray();
        if (index < 0) index += arr.Count;
        if (index < 0 || index >= arr.Count) return Null;
        return arr[index];
    }

    public JqValue Slice(int? from, int? to)
    {
        if (Kind == JqValueKind.Array)
        {
            var arr = GetArray();
            int start = from ?? 0;
            int end = to ?? arr.Count;
            if (start < 0) start += arr.Count;
            if (end < 0) end += arr.Count;
            if (start < 0) start = 0;
            if (end > arr.Count) end = arr.Count;
            if (start >= end) return FromArray(new List<JqValue>());
            var result = new List<JqValue>(end - start);
            for (int i = start; i < end; i++)
                result.Add(arr[i]);
            return FromArray(result);
        }
        if (Kind == JqValueKind.String)
        {
            var s = GetString();
            int start = from ?? 0;
            int end = to ?? s.Length;
            if (start < 0) start += s.Length;
            if (end < 0) end += s.Length;
            if (start < 0) start = 0;
            if (end > s.Length) end = s.Length;
            if (start >= end) return FromString("");
            return FromString(s.Substring(start, end - start));
        }
        throw new JqException(TypeName + " cannot be sliced");
    }

    public string ToJsonString(JqOptions options)
    {
        var sb = new StringBuilder(64);
        WriteJson(sb, options, 0);
        return sb.ToString();
    }

    private void WriteJson(StringBuilder sb, JqOptions options, int depth)
    {
        bool compact = options.CompactOutput;
        string indent;
        if (options.UseTabs)
            indent = "\t";
        else
        {
            indent = options.IndentWidth switch
            {
                0 => "",
                1 => " ",
                2 => "  ",
                4 => "    ",
                _ => new string(' ', options.IndentWidth),
            };
        }

        switch (Kind)
        {
            case JqValueKind.Null:
                sb.Append("null");
                break;
            case JqValueKind.Boolean:
                sb.Append((bool)_value! ? "true" : "false");
                break;
            case JqValueKind.Number:
            {
                double d = (double)_value!;
                if (double.IsPositiveInfinity(d))
                    sb.Append("1.7976931348623157e+308");
                else if (double.IsNegativeInfinity(d))
                    sb.Append("-1.7976931348623157e+308");
                else if (double.IsNaN(d))
                    sb.Append("null");
                else
                    FormatNumber(sb, d);
                break;
            }
            case JqValueKind.String:
                WriteJsonString(sb, (string)_value!);
                break;
            case JqValueKind.Array:
            {
                var arr = (List<JqValue>)_value!;
                if (arr.Count == 0)
                {
                    sb.Append("[]");
                    break;
                }
                sb.Append('[');
                for (int i = 0; i < arr.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    if (!compact)
                    {
                        sb.Append('\n');
                        AppendIndent(sb, indent, depth + 1);
                    }
                    var item = arr[i];
                    if (options.SortKeys)
                        item = SortKeysDeep(item);
                    item.WriteJson(sb, options, depth + 1);
                }
                if (!compact)
                {
                    sb.Append('\n');
                    AppendIndent(sb, indent, depth);
                }
                sb.Append(']');
                break;
            }
            case JqValueKind.Object:
            {
                var obj = (List<KeyValuePair<string, JqValue>>)_value!;
                var source = obj;
                if (options.SortKeys)
                {
                    source = new List<KeyValuePair<string, JqValue>>(obj);
                    source.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.Ordinal));
                }
                if (source.Count == 0)
                {
                    sb.Append("{}");
                    break;
                }
                sb.Append('{');
                for (int i = 0; i < source.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    if (!compact)
                    {
                        sb.Append('\n');
                        AppendIndent(sb, indent, depth + 1);
                    }
                    WriteJsonString(sb, source[i].Key);
                    sb.Append(':');
                    if (!compact) sb.Append(' ');
                    var val = source[i].Value;
                    if (options.SortKeys)
                        val = SortKeysDeep(val);
                    val.WriteJson(sb, options, depth + 1);
                }
                if (!compact)
                {
                    sb.Append('\n');
                    AppendIndent(sb, indent, depth);
                }
                sb.Append('}');
                break;
            }
        }
    }

    private static JqValue SortKeysDeep(JqValue v)
    {
        if (v.Kind == JqValueKind.Object)
        {
            var obj = v.GetObject();
            var sorted = new List<KeyValuePair<string, JqValue>>(obj.Count);
            for (int i = 0; i < obj.Count; i++)
                sorted.Add(new KeyValuePair<string, JqValue>(obj[i].Key, SortKeysDeep(obj[i].Value)));
            sorted.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.Ordinal));
            return FromObject(sorted);
        }
        if (v.Kind == JqValueKind.Array)
        {
            var arr = v.GetArray();
            var newArr = new List<JqValue>(arr.Count);
            for (int i = 0; i < arr.Count; i++)
                newArr.Add(SortKeysDeep(arr[i]));
            return FromArray(newArr);
        }
        return v;
    }

    private static void AppendIndent(StringBuilder sb, string indent, int depth)
    {
        for (int i = 0; i < depth; i++)
            sb.Append(indent);
    }

    private static void FormatNumber(StringBuilder sb, double d)
    {
        if (d == Math.Floor(d) && !double.IsInfinity(d) && Math.Abs(d) < 1e18)
        {
            long l = (long)d;
            sb.Append(l.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            string r = d.ToString("R", CultureInfo.InvariantCulture);
            sb.Append(r);
        }
    }

    private static void WriteJsonString(StringBuilder sb, string s)
    {
        sb.Append('"');
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                        sb.AppendFormat("\\u{0:x4}", (int)c);
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    public bool Equals(JqValue other)
    {
        if (Kind != other.Kind) return false;
        switch (Kind)
        {
            case JqValueKind.Null: return true;
            case JqValueKind.Boolean: return (bool)_value! == (bool)other._value!;
            case JqValueKind.Number: return (double)_value! == (double)other._value!;
            case JqValueKind.String: return (string)_value! == (string)other._value!;
            case JqValueKind.Array:
            {
                var a = (List<JqValue>)_value!;
                var b = (List<JqValue>)other._value!;
                if (a.Count != b.Count) return false;
                for (int i = 0; i < a.Count; i++)
                    if (!a[i].Equals(b[i])) return false;
                return true;
            }
            case JqValueKind.Object:
            {
                var a = (List<KeyValuePair<string, JqValue>>)_value!;
                var b = (List<KeyValuePair<string, JqValue>>)other._value!;
                if (a.Count != b.Count) return false;
                for (int i = 0; i < a.Count; i++)
                {
                    if (a[i].Key != b[i].Key) return false;
                    if (!a[i].Value.Equals(b[i].Value)) return false;
                }
                return true;
            }
            default: return false;
        }
    }

    public override bool Equals(object? obj) => obj is JqValue other && Equals(other);
    public override int GetHashCode() => _value?.GetHashCode() ?? 0;

    public static int Compare(JqValue a, JqValue b)
    {
        if (a.Kind != b.Kind)
            return KindOrder(a.Kind).CompareTo(KindOrder(b.Kind));
        switch (a.Kind)
        {
            case JqValueKind.Null: return 0;
            case JqValueKind.Boolean:
                return ((bool)a._value!).CompareTo((bool)b._value!);
            case JqValueKind.Number:
                return ((double)a._value!).CompareTo((double)b._value!);
            case JqValueKind.String:
                return string.Compare((string)a._value!, (string)b._value!, StringComparison.Ordinal);
            case JqValueKind.Array:
            {
                var aa = a.GetArray();
                var ba = b.GetArray();
                int minLen = Math.Min(aa.Count, ba.Count);
                for (int i = 0; i < minLen; i++)
                {
                    int c = Compare(aa[i], ba[i]);
                    if (c != 0) return c;
                }
                return aa.Count.CompareTo(ba.Count);
            }
            case JqValueKind.Object:
            {
                var ao = a.GetObject();
                var bo = b.GetObject();
                var asorted = new List<KeyValuePair<string, JqValue>>(ao);
                var bsorted = new List<KeyValuePair<string, JqValue>>(bo);
                asorted.Sort((x, y) => string.Compare(x.Key, y.Key, StringComparison.Ordinal));
                bsorted.Sort((x, y) => string.Compare(x.Key, y.Key, StringComparison.Ordinal));
                int minLen = Math.Min(asorted.Count, bsorted.Count);
                for (int i = 0; i < minLen; i++)
                {
                    int kc = string.Compare(asorted[i].Key, bsorted[i].Key, StringComparison.Ordinal);
                    if (kc != 0) return kc;
                    int vc = Compare(asorted[i].Value, bsorted[i].Value);
                    if (vc != 0) return vc;
                }
                return asorted.Count.CompareTo(bsorted.Count);
            }
            default: return 0;
        }
    }

    private static int KindOrder(JqValueKind kind)
    {
        return kind switch
        {
            JqValueKind.Null => 0,
            JqValueKind.Boolean => 1,
            JqValueKind.Number => 2,
            JqValueKind.String => 3,
            JqValueKind.Array => 4,
            JqValueKind.Object => 5,
            _ => 6,
        };
    }

    public JqValue DeepClone()
    {
        switch (Kind)
        {
            case JqValueKind.Array:
            {
                var arr = GetArray();
                var clone = new List<JqValue>(arr.Count);
                for (int i = 0; i < arr.Count; i++)
                    clone.Add(arr[i].DeepClone());
                return FromArray(clone);
            }
            case JqValueKind.Object:
            {
                var obj = GetObject();
                var clone = new List<KeyValuePair<string, JqValue>>(obj.Count);
                for (int i = 0; i < obj.Count; i++)
                    clone.Add(new KeyValuePair<string, JqValue>(obj[i].Key, obj[i].Value.DeepClone()));
                return FromObject(clone);
            }
            default:
                return this;
        }
    }

    public bool Contains(JqValue other)
    {
        if (Kind != other.Kind) return false;
        switch (Kind)
        {
            case JqValueKind.Null:
            case JqValueKind.Boolean:
            case JqValueKind.Number:
                return Equals(other);
            case JqValueKind.String:
                return GetString().Contains(other.GetString());
            case JqValueKind.Array:
            {
                var arr = GetArray();
                var otherArr = other.GetArray();
                for (int oi = 0; oi < otherArr.Count; oi++)
                {
                    bool found = false;
                    for (int ai = 0; ai < arr.Count; ai++)
                    {
                        if (arr[ai].Contains(otherArr[oi])) { found = true; break; }
                    }
                    if (!found) return false;
                }
                return true;
            }
            case JqValueKind.Object:
            {
                var obj = GetObject();
                var otherObj = other.GetObject();
                for (int oi = 0; oi < otherObj.Count; oi++)
                {
                    bool found = false;
                    for (int ai = 0; ai < obj.Count; ai++)
                    {
                        if (obj[ai].Key == otherObj[oi].Key && obj[ai].Value.Contains(otherObj[oi].Value))
                        { found = true; break; }
                    }
                    if (!found) return false;
                }
                return true;
            }
            default:
                return Equals(other);
        }
    }

    public string ToJqString()
    {
        if (Kind == JqValueKind.String) return GetString();
        return ToJsonString(new JqOptions());
    }
}

#endregion

#region Tokens

internal enum JqTokenType
{
    Number, String, Ident, Variable, Format,
    Dot, LParen, RParen, LBracket, RBracket, LBrace, RBrace,
    Pipe, Comma, Semicolon, Colon, Question,
    Plus, Minus, Star, Slash, Percent,
    Eq, Ne, Lt, Gt, Le, Ge,
    Assign, UpdateAssign, AddAssign, SubAssign, MulAssign, DivAssign, ModAssign, AltAssign,
    And, Or, Not,
    If, Then, Elif, Else, End,
    TryCatch, Try, Catch,
    Reduce, Foreach, As, Label, Break,
    Def, Import, Include,
    DotDot,
    Optional,
    AltOp,
    Eof,
}

internal sealed class JqToken
{
    public JqTokenType Type;
    public string Value = "";
    public double NumValue;
    public int Pos;
}

#endregion

#region Lexer

internal sealed class JqLexer
{
    private readonly string _src;
    private int _pos;

    public JqLexer(string src) { _src = src; }

    public List<JqToken> Tokenize()
    {
        var tokens = new List<JqToken>();
        while (_pos < _src.Length)
        {
            SkipWhitespaceAndComments();
            if (_pos >= _src.Length) break;

            int start = _pos;
            char c = _src[_pos];

            if (c == '"') { tokens.Add(ReadString()); continue; }

            if (c == '@')
            {
                _pos++;
                int identStart = _pos;
                while (_pos < _src.Length && (char.IsLetterOrDigit(_src[_pos]) || _src[_pos] == '_')) _pos++;
                tokens.Add(new JqToken { Type = JqTokenType.Format, Value = _src[identStart.._pos], Pos = start });
                continue;
            }

            if (char.IsDigit(c) || (c == '-' && _pos + 1 < _src.Length && char.IsDigit(_src[_pos + 1]) && ShouldTreatMinusAsNegative(tokens)))
            {
                tokens.Add(ReadNumber());
                continue;
            }

            if (c == '$')
            {
                _pos++;
                int identStart = _pos;
                while (_pos < _src.Length && (char.IsLetterOrDigit(_src[_pos]) || _src[_pos] == '_')) _pos++;
                tokens.Add(new JqToken { Type = JqTokenType.Variable, Value = _src[identStart.._pos], Pos = start });
                continue;
            }

            if (char.IsLetter(c) || c == '_') { tokens.Add(ReadIdent()); continue; }

            switch (c)
            {
                case '.':
                    _pos++;
                    if (_pos < _src.Length && _src[_pos] == '.')
                    {
                        _pos++;
                        tokens.Add(new JqToken { Type = JqTokenType.DotDot, Pos = start });
                    }
                    else if (_pos < _src.Length && char.IsLetter(_src[_pos]))
                    {
                        int identStart = _pos;
                        while (_pos < _src.Length && (char.IsLetterOrDigit(_src[_pos]) || _src[_pos] == '_')) _pos++;
                        tokens.Add(new JqToken { Type = JqTokenType.Dot, Pos = start });
                        tokens.Add(new JqToken { Type = JqTokenType.Ident, Value = _src[identStart.._pos], Pos = identStart });
                    }
                    else
                    {
                        tokens.Add(new JqToken { Type = JqTokenType.Dot, Pos = start });
                    }
                    break;
                case '|':
                    _pos++;
                    if (_pos < _src.Length && _src[_pos] == '=') { _pos++; tokens.Add(new JqToken { Type = JqTokenType.UpdateAssign, Pos = start }); }
                    else tokens.Add(new JqToken { Type = JqTokenType.Pipe, Pos = start });
                    break;
                case ',': _pos++; tokens.Add(new JqToken { Type = JqTokenType.Comma, Pos = start }); break;
                case ':': _pos++; tokens.Add(new JqToken { Type = JqTokenType.Colon, Pos = start }); break;
                case ';': _pos++; tokens.Add(new JqToken { Type = JqTokenType.Semicolon, Pos = start }); break;
                case '(': _pos++; tokens.Add(new JqToken { Type = JqTokenType.LParen, Pos = start }); break;
                case ')': _pos++; tokens.Add(new JqToken { Type = JqTokenType.RParen, Pos = start }); break;
                case '[': _pos++; tokens.Add(new JqToken { Type = JqTokenType.LBracket, Pos = start }); break;
                case ']': _pos++; tokens.Add(new JqToken { Type = JqTokenType.RBracket, Pos = start }); break;
                case '{': _pos++; tokens.Add(new JqToken { Type = JqTokenType.LBrace, Pos = start }); break;
                case '}': _pos++; tokens.Add(new JqToken { Type = JqTokenType.RBrace, Pos = start }); break;
                case '+':
                    _pos++;
                    if (_pos < _src.Length && _src[_pos] == '=') { _pos++; tokens.Add(new JqToken { Type = JqTokenType.AddAssign, Pos = start }); }
                    else tokens.Add(new JqToken { Type = JqTokenType.Plus, Pos = start });
                    break;
                case '-':
                    _pos++;
                    if (_pos < _src.Length && _src[_pos] == '=') { _pos++; tokens.Add(new JqToken { Type = JqTokenType.SubAssign, Pos = start }); }
                    else tokens.Add(new JqToken { Type = JqTokenType.Minus, Pos = start });
                    break;
                case '*':
                    _pos++;
                    if (_pos < _src.Length && _src[_pos] == '=') { _pos++; tokens.Add(new JqToken { Type = JqTokenType.MulAssign, Pos = start }); }
                    else tokens.Add(new JqToken { Type = JqTokenType.Star, Pos = start });
                    break;
                case '/':
                    _pos++;
                    if (_pos < _src.Length && _src[_pos] == '/')
                    {
                        _pos++;
                        if (_pos < _src.Length && _src[_pos] == '=') { _pos++; tokens.Add(new JqToken { Type = JqTokenType.AltAssign, Pos = start }); }
                        else tokens.Add(new JqToken { Type = JqTokenType.AltOp, Pos = start });
                    }
                    else if (_pos < _src.Length && _src[_pos] == '=') { _pos++; tokens.Add(new JqToken { Type = JqTokenType.DivAssign, Pos = start }); }
                    else tokens.Add(new JqToken { Type = JqTokenType.Slash, Pos = start });
                    break;
                case '%':
                    _pos++;
                    if (_pos < _src.Length && _src[_pos] == '=') { _pos++; tokens.Add(new JqToken { Type = JqTokenType.ModAssign, Pos = start }); }
                    else tokens.Add(new JqToken { Type = JqTokenType.Percent, Pos = start });
                    break;
                case '=':
                    _pos++;
                    if (_pos < _src.Length && _src[_pos] == '=') { _pos++; tokens.Add(new JqToken { Type = JqTokenType.Eq, Pos = start }); }
                    else tokens.Add(new JqToken { Type = JqTokenType.Assign, Pos = start });
                    break;
                case '!':
                    _pos++;
                    if (_pos < _src.Length && _src[_pos] == '=') { _pos++; tokens.Add(new JqToken { Type = JqTokenType.Ne, Pos = start }); }
                    else throw new JqException("unexpected character '!' at position " + start);
                    break;
                case '<':
                    _pos++;
                    if (_pos < _src.Length && _src[_pos] == '=') { _pos++; tokens.Add(new JqToken { Type = JqTokenType.Le, Pos = start }); }
                    else tokens.Add(new JqToken { Type = JqTokenType.Lt, Pos = start });
                    break;
                case '>':
                    _pos++;
                    if (_pos < _src.Length && _src[_pos] == '=') { _pos++; tokens.Add(new JqToken { Type = JqTokenType.Ge, Pos = start }); }
                    else tokens.Add(new JqToken { Type = JqTokenType.Gt, Pos = start });
                    break;
                case '?':
                    _pos++;
                    if (_pos < _src.Length && _src[_pos] == '/')
                    {
                        _pos++;
                        if (_pos < _src.Length && _src[_pos] == '/') { _pos++; tokens.Add(new JqToken { Type = JqTokenType.AltOp, Pos = start }); }
                        else { _pos--; tokens.Add(new JqToken { Type = JqTokenType.Question, Pos = start }); }
                    }
                    else
                    {
                        tokens.Add(new JqToken { Type = JqTokenType.Question, Pos = start });
                    }
                    break;
                default:
                    throw new JqException("unexpected character '" + c + "' at position " + _pos);
            }
        }

        tokens.Add(new JqToken { Type = JqTokenType.Eof, Pos = _pos });
        return tokens;
    }

    private bool ShouldTreatMinusAsNegative(List<JqToken> tokens)
    {
        if (tokens.Count == 0) return true;
        var last = tokens[tokens.Count - 1].Type;
        return last == JqTokenType.LParen || last == JqTokenType.LBracket ||
               last == JqTokenType.Pipe || last == JqTokenType.Comma ||
               last == JqTokenType.Semicolon || last == JqTokenType.Colon ||
               last == JqTokenType.Eq || last == JqTokenType.Ne ||
               last == JqTokenType.Lt || last == JqTokenType.Gt ||
               last == JqTokenType.Le || last == JqTokenType.Ge ||
               last == JqTokenType.Plus || last == JqTokenType.Minus ||
               last == JqTokenType.Star || last == JqTokenType.Slash ||
               last == JqTokenType.Percent || last == JqTokenType.And ||
               last == JqTokenType.Or || last == JqTokenType.Not ||
               last == JqTokenType.Assign || last == JqTokenType.Then ||
               last == JqTokenType.Else || last == JqTokenType.Elif;
    }

    private void SkipWhitespaceAndComments()
    {
        while (_pos < _src.Length)
        {
            char c = _src[_pos];
            if (c == ' ' || c == '\t' || c == '\r' || c == '\n') { _pos++; continue; }
            if (c == '#') { while (_pos < _src.Length && _src[_pos] != '\n') _pos++; continue; }
            break;
        }
    }

    private JqToken ReadString()
    {
        int start = _pos;
        _pos++;
        var sb = new StringBuilder();
        bool hasInterpolation = false;
        var parts = new List<string>();

        while (_pos < _src.Length && _src[_pos] != '"')
        {
            if (_src[_pos] == '\\')
            {
                _pos++;
                if (_pos >= _src.Length) throw new JqException("unterminated string");
                char esc = _src[_pos];
                switch (esc)
                {
                    case '"': sb.Append('"'); _pos++; break;
                    case '\\': sb.Append('\\'); _pos++; break;
                    case '/': sb.Append('/'); _pos++; break;
                    case 'b': sb.Append('\b'); _pos++; break;
                    case 'f': sb.Append('\f'); _pos++; break;
                    case 'n': sb.Append('\n'); _pos++; break;
                    case 'r': sb.Append('\r'); _pos++; break;
                    case 't': sb.Append('\t'); _pos++; break;
                    case 'u':
                        _pos++;
                        if (_pos + 4 > _src.Length) throw new JqException("invalid unicode escape");
                        string hex = _src.Substring(_pos, 4);
                        sb.Append((char)int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        _pos += 4;
                        break;
                    case '(':
                        hasInterpolation = true;
                        parts.Add(sb.ToString());
                        sb.Clear();
                        _pos++;
                        int depth = 1;
                        int exprStart = _pos;
                        while (_pos < _src.Length && depth > 0)
                        {
                            if (_src[_pos] == '(') depth++;
                            else if (_src[_pos] == ')') depth--;
                            if (depth > 0) _pos++;
                        }
                        if (depth != 0) throw new JqException("unterminated string interpolation");
                        string expr = _src[exprStart.._pos];
                        parts.Add("\0INTERP:" + expr);
                        _pos++;
                        break;
                    default:
                        sb.Append('\\'); sb.Append(esc); _pos++; break;
                }
            }
            else
            {
                sb.Append(_src[_pos]); _pos++;
            }
        }
        if (_pos >= _src.Length) throw new JqException("unterminated string");
        _pos++;

        if (hasInterpolation)
        {
            parts.Add(sb.ToString());
            return new JqToken { Type = JqTokenType.String, Value = string.Join("\x01", parts), Pos = start };
        }
        return new JqToken { Type = JqTokenType.String, Value = sb.ToString(), Pos = start };
    }

    private JqToken ReadNumber()
    {
        int start = _pos;
        if (_src[_pos] == '-') _pos++;
        while (_pos < _src.Length && char.IsDigit(_src[_pos])) _pos++;
        if (_pos < _src.Length && _src[_pos] == '.')
        {
            _pos++;
            while (_pos < _src.Length && char.IsDigit(_src[_pos])) _pos++;
        }
        if (_pos < _src.Length && (_src[_pos] == 'e' || _src[_pos] == 'E'))
        {
            _pos++;
            if (_pos < _src.Length && (_src[_pos] == '+' || _src[_pos] == '-')) _pos++;
            while (_pos < _src.Length && char.IsDigit(_src[_pos])) _pos++;
        }
        string numStr = _src[start.._pos];
        double val = double.Parse(numStr, CultureInfo.InvariantCulture);
        return new JqToken { Type = JqTokenType.Number, Value = numStr, NumValue = val, Pos = start };
    }

    private JqToken ReadIdent()
    {
        int start = _pos;
        while (_pos < _src.Length && (char.IsLetterOrDigit(_src[_pos]) || _src[_pos] == '_')) _pos++;
        string ident = _src[start.._pos];
        var type = ident switch
        {
            "and" => JqTokenType.And, "or" => JqTokenType.Or, "not" => JqTokenType.Not,
            "if" => JqTokenType.If, "then" => JqTokenType.Then, "elif" => JqTokenType.Elif,
            "else" => JqTokenType.Else, "end" => JqTokenType.End,
            "try" => JqTokenType.Try, "catch" => JqTokenType.Catch,
            "reduce" => JqTokenType.Reduce, "foreach" => JqTokenType.Foreach,
            "as" => JqTokenType.As, "def" => JqTokenType.Def,
            "label" => JqTokenType.Label,
            "import" => JqTokenType.Import, "include" => JqTokenType.Include,
            _ => JqTokenType.Ident,
        };
        return new JqToken { Type = type, Value = ident, Pos = start };
    }
}

#endregion

#region AST Nodes

internal abstract class JqNode { }
internal sealed class JqIdentityNode : JqNode { }
internal sealed class JqRecurseNode : JqNode { }
internal sealed class JqLiteralNode : JqNode { public JqValue Value; public JqLiteralNode(JqValue value) { Value = value; } }
internal sealed class JqFieldNode : JqNode { public string Name; public bool Optional; public JqFieldNode(string name, bool optional = false) { Name = name; Optional = optional; } }
internal sealed class JqIndexNode : JqNode { public JqNode Index; public bool Optional; public JqIndexNode(JqNode index, bool optional = false) { Index = index; Optional = optional; } }
internal sealed class JqSliceNode : JqNode { public JqNode? From; public JqNode? To; public JqSliceNode(JqNode? from, JqNode? to) { From = from; To = to; } }
internal sealed class JqIterateNode : JqNode { public bool Optional; public JqIterateNode(bool optional = false) { Optional = optional; } }
internal sealed class JqPipeNode : JqNode { public JqNode Left, Right; public JqPipeNode(JqNode left, JqNode right) { Left = left; Right = right; } }
internal sealed class JqCommaNode : JqNode { public JqNode Left, Right; public JqCommaNode(JqNode left, JqNode right) { Left = left; Right = right; } }
internal sealed class JqParenNode : JqNode { public JqNode Expr; public JqParenNode(JqNode expr) { Expr = expr; } }

internal sealed class JqObjectNode : JqNode { public List<JqObjectEntryNode> Entries; public JqObjectNode(List<JqObjectEntryNode> entries) { Entries = entries; } }
internal sealed class JqObjectEntryNode
{
    public JqNode? Key; public JqNode Value; public bool IsDynamic; public string? IdentKey;
    public JqObjectEntryNode(JqNode? key, JqNode value, bool isDynamic = false, string? identKey = null) { Key = key; Value = value; IsDynamic = isDynamic; IdentKey = identKey; }
}
internal sealed class JqArrayNode : JqNode { public JqNode? Expr; public JqArrayNode(JqNode? expr) { Expr = expr; } }

internal sealed class JqStringInterpolationNode : JqNode { public List<object> Parts; public JqStringInterpolationNode(List<object> parts) { Parts = parts; } }

internal sealed class JqIfNode : JqNode
{
    public JqNode Condition, Then; public List<(JqNode Cond, JqNode Body)> Elifs; public JqNode? Else;
    public JqIfNode(JqNode cond, JqNode then, List<(JqNode, JqNode)> elifs, JqNode? els) { Condition = cond; Then = then; Elifs = elifs; Else = els; }
}

internal sealed class JqCompareNode : JqNode { public JqTokenType Op; public JqNode Left, Right; public JqCompareNode(JqTokenType op, JqNode left, JqNode right) { Op = op; Left = left; Right = right; } }
internal sealed class JqArithNode : JqNode { public JqTokenType Op; public JqNode Left, Right; public JqArithNode(JqTokenType op, JqNode left, JqNode right) { Op = op; Left = left; Right = right; } }
internal sealed class JqBoolNode : JqNode { public JqTokenType Op; public JqNode Left, Right; public JqBoolNode(JqTokenType op, JqNode left, JqNode right) { Op = op; Left = left; Right = right; } }
internal sealed class JqNotNode : JqNode { public JqNode Expr; public JqNotNode(JqNode expr) { Expr = expr; } }
internal sealed class JqNegateNode : JqNode { public JqNode Expr; public JqNegateNode(JqNode expr) { Expr = expr; } }
internal sealed class JqTryNode : JqNode { public JqNode Expr; public JqNode? Catch; public JqTryNode(JqNode expr, JqNode? catchExpr) { Expr = expr; Catch = catchExpr; } }
internal sealed class JqOptionalNode : JqNode { public JqNode Expr; public JqOptionalNode(JqNode expr) { Expr = expr; } }
internal sealed class JqAlternativeNode : JqNode { public JqNode Left, Right; public JqAlternativeNode(JqNode left, JqNode right) { Left = left; Right = right; } }
internal sealed class JqVariableNode : JqNode { public string Name; public JqVariableNode(string name) { Name = name; } }
internal sealed class JqBindingNode : JqNode { public JqNode Expr; public string VarName; public JqNode Body; public JqBindingNode(JqNode expr, string varName, JqNode body) { Expr = expr; VarName = varName; Body = body; } }
internal sealed class JqReduceNode : JqNode { public JqNode Expr; public string VarName; public JqNode Init; public JqNode Update; public JqReduceNode(JqNode expr, string varName, JqNode init, JqNode update) { Expr = expr; VarName = varName; Init = init; Update = update; } }
internal sealed class JqForeachNode : JqNode { public JqNode Expr; public string VarName; public JqNode Init; public JqNode Update; public JqNode? Extract; public JqForeachNode(JqNode expr, string varName, JqNode init, JqNode update, JqNode? extract) { Expr = expr; VarName = varName; Init = init; Update = update; Extract = extract; } }
internal sealed class JqFuncDefNode : JqNode { public string Name; public List<string> Params; public JqNode Body; public JqNode Rest; public JqFuncDefNode(string name, List<string> parms, JqNode body, JqNode rest) { Name = name; Params = parms; Body = body; Rest = rest; } }
internal sealed class JqFuncCallNode : JqNode { public string Name; public List<JqNode> Args; public JqFuncCallNode(string name, List<JqNode> args) { Name = name; Args = args; } }
internal sealed class JqLabelNode : JqNode { public string Name; public JqNode Body; public JqLabelNode(string name, JqNode body) { Name = name; Body = body; } }
internal sealed class JqBreakNode : JqNode { public string Label; public JqBreakNode(string label) { Label = label; } }
internal sealed class JqFormatNode : JqNode { public string FormatName; public JqNode? Expr; public JqFormatNode(string formatName, JqNode? expr) { FormatName = formatName; Expr = expr; } }
internal sealed class JqUpdateNode : JqNode { public JqNode Path; public JqNode Value; public JqTokenType Op; public JqUpdateNode(JqNode path, JqNode value, JqTokenType op) { Path = path; Value = value; Op = op; } }
internal sealed class JqPostfixNode : JqNode { public JqNode Expr; public JqNode Accessor; public JqPostfixNode(JqNode expr, JqNode accessor) { Expr = expr; Accessor = accessor; } }

#endregion

#region Parser

internal sealed class JqParser
{
    private readonly List<JqToken> _tokens;
    private readonly string _src;
    private int _pos;

    public JqParser(List<JqToken> tokens, string src) { _tokens = tokens; _src = src; }

    private JqToken Peek() => _tokens[_pos];
    private JqToken Advance() => _tokens[_pos++];
    private JqToken Expect(JqTokenType type)
    {
        var t = Peek();
        if (t.Type != type) throw new JqException("expected " + type + " but got " + t.Type + " ('" + t.Value + "') at position " + t.Pos);
        return Advance();
    }
    private bool Match(JqTokenType type) { if (Peek().Type == type) { Advance(); return true; } return false; }

    public JqNode Parse()
    {
        var node = ParsePipe();
        if (Peek().Type != JqTokenType.Eof) throw new JqException("unexpected token '" + Peek().Value + "' at position " + Peek().Pos);
        return node;
    }

    private JqNode ParsePipe()
    {
        var left = ParseComma();
        while (Peek().Type == JqTokenType.Pipe) { Advance(); var right = ParseComma(); left = new JqPipeNode(left, right); }
        if (Peek().Type == JqTokenType.As) { Advance(); var varToken = Expect(JqTokenType.Variable); Expect(JqTokenType.Pipe); var body = ParsePipe(); return new JqBindingNode(left, varToken.Value, body); }
        return left;
    }
    private JqNode ParsePipeExpr()
    {
        var left = ParseAssign();
        while (Peek().Type == JqTokenType.Pipe) { Advance(); var right = ParseAssign(); left = new JqPipeNode(left, right); }
        if (Peek().Type == JqTokenType.As) { Advance(); var varToken = Expect(JqTokenType.Variable); Expect(JqTokenType.Pipe); var body = ParsePipeExpr(); return new JqBindingNode(left, varToken.Value, body); }
        return left;
    }

    private JqNode ParseComma()
    {
        var left = ParseAssign();
        while (Peek().Type == JqTokenType.Comma) { Advance(); var right = ParseAssign(); left = new JqCommaNode(left, right); }
        return left;
    }

    private JqNode ParseAssign()
    {
        var left = ParseAlternative();
        var t = Peek().Type;
        if (t == JqTokenType.Assign || t == JqTokenType.UpdateAssign ||
            t == JqTokenType.AddAssign || t == JqTokenType.SubAssign ||
            t == JqTokenType.MulAssign || t == JqTokenType.DivAssign ||
            t == JqTokenType.ModAssign || t == JqTokenType.AltAssign)
        { Advance(); var right = ParseAlternative(); return new JqUpdateNode(left, right, t); }
        return left;
    }

    private JqNode ParseAlternative()
    {
        var left = ParseOr();
        while (Peek().Type == JqTokenType.AltOp) { Advance(); var right = ParseOr(); left = new JqAlternativeNode(left, right); }
        return left;
    }

    private JqNode ParseOr()
    {
        var left = ParseAnd();
        while (Peek().Type == JqTokenType.Or) { Advance(); var right = ParseAnd(); left = new JqBoolNode(JqTokenType.Or, left, right); }
        return left;
    }

    private JqNode ParseAnd()
    {
        var left = ParseComparison();
        while (Peek().Type == JqTokenType.And) { Advance(); var right = ParseComparison(); left = new JqBoolNode(JqTokenType.And, left, right); }
        return left;
    }

    private JqNode ParseComparison()
    {
        var left = ParseAddSub();
        var t = Peek().Type;
        if (t == JqTokenType.Eq || t == JqTokenType.Ne || t == JqTokenType.Lt || t == JqTokenType.Gt || t == JqTokenType.Le || t == JqTokenType.Ge)
        { Advance(); var right = ParseAddSub(); return new JqCompareNode(t, left, right); }
        return left;
    }

    private JqNode ParseAddSub()
    {
        var left = ParseMulDiv();
        while (Peek().Type == JqTokenType.Plus || Peek().Type == JqTokenType.Minus) { var op = Advance(); var right = ParseMulDiv(); left = new JqArithNode(op.Type, left, right); }
        return left;
    }

    private JqNode ParseMulDiv()
    {
        var left = ParseUnary();
        while (Peek().Type == JqTokenType.Star || Peek().Type == JqTokenType.Slash || Peek().Type == JqTokenType.Percent)
        { var op = Advance(); var right = ParseUnary(); left = new JqArithNode(op.Type, left, right); }
        return left;
    }

    private JqNode ParseUnary()
    {
        if (Peek().Type == JqTokenType.Minus) { Advance(); var expr = ParsePostfix(); return new JqNegateNode(expr); }
        return ParsePostfix();
    }

    private JqNode ParsePostfix()
    {
        var expr = ParsePrimary();
        while (true)
        {
            if (Peek().Type == JqTokenType.LBracket)
            {
                Advance();
                if (Peek().Type == JqTokenType.RBracket) { Advance(); bool optional = Match(JqTokenType.Question); expr = new JqPostfixNode(expr, new JqIterateNode(optional)); }
                else { var indexExpr = ParseSliceOrIndex(); Expect(JqTokenType.RBracket); bool optional = Match(JqTokenType.Question); if (indexExpr is JqSliceNode sn) expr = new JqPostfixNode(expr, sn); else expr = new JqPostfixNode(expr, new JqIndexNode(indexExpr, optional)); }
            }
            else if (Peek().Type == JqTokenType.Dot && _pos + 1 < _tokens.Count && _tokens[_pos + 1].Type == JqTokenType.Ident)
            {
                Advance(); var ident = Advance(); bool optional = Match(JqTokenType.Question); expr = new JqPostfixNode(expr, new JqFieldNode(ident.Value, optional));
            }
            else if (Peek().Type == JqTokenType.Question) { Advance(); expr = new JqOptionalNode(expr); }
            else if (Peek().Type == JqTokenType.Not) { Advance(); expr = new JqNotNode(expr); }
            else break;
        }
        return expr;
    }

    private JqNode ParseSliceOrIndex()
    {
        if (Peek().Type == JqTokenType.Colon)
        {
            Advance();
            if (Peek().Type == JqTokenType.RBracket) return new JqSliceNode(null, null);
            var to = ParsePipe();
            return new JqSliceNode(null, to);
        }
        var first = ParsePipe();
        if (Peek().Type == JqTokenType.Colon) { Advance(); if (Peek().Type == JqTokenType.RBracket) return new JqSliceNode(first, null); var to = ParsePipe(); return new JqSliceNode(first, to); }
        return first;
    }

    private JqNode ParsePrimary()
    {
        var t = Peek();
        switch (t.Type)
        {
            case JqTokenType.Dot:
            {
                Advance();
                if (Peek().Type == JqTokenType.Ident) { var ident = Advance(); bool optional = Match(JqTokenType.Question); return new JqFieldNode(ident.Value, optional); }
                if (Peek().Type == JqTokenType.LBracket)
                {
                    Advance();
                    if (Peek().Type == JqTokenType.RBracket) { Advance(); bool optional = Match(JqTokenType.Question); return new JqIterateNode(optional); }
                    var indexExpr = ParseSliceOrIndex(); Expect(JqTokenType.RBracket); bool opt = Match(JqTokenType.Question);
                    if (indexExpr is JqSliceNode sn) return sn;
                    return new JqIndexNode(indexExpr, opt);
                }
                if (Peek().Type == JqTokenType.String) { var key = Advance(); return new JqFieldNode(key.Value, false); }
                return new JqIdentityNode();
            }
            case JqTokenType.DotDot: Advance(); return new JqRecurseNode();
            case JqTokenType.Number: Advance(); return new JqLiteralNode(JqValue.FromNumber(t.NumValue));
            case JqTokenType.String:
            {
                Advance();
                if (t.Value.Contains('\x01')) return ParseStringInterpolation(t.Value);
                return new JqLiteralNode(JqValue.FromString(t.Value));
            }
            case JqTokenType.Format:
            {
                Advance();
                JqNode? expr = null;
                if (Peek().Type == JqTokenType.String)
                {
                    var strToken = Advance();
                    if (strToken.Value.Contains('\x01')) expr = ParseStringInterpolation(strToken.Value);
                    else expr = new JqLiteralNode(JqValue.FromString(strToken.Value));
                }
                return new JqFormatNode(t.Value, expr);
            }
            case JqTokenType.LParen: Advance(); var inner = ParsePipe(); Expect(JqTokenType.RParen); return new JqParenNode(inner);
            case JqTokenType.LBracket: return ParseArrayConstruction();
            case JqTokenType.LBrace: return ParseObjectConstruction();
            case JqTokenType.If: return ParseIf();
            case JqTokenType.Try: return ParseTry();
            case JqTokenType.Reduce: return ParseReduce();
            case JqTokenType.Foreach: return ParseForeach();
            case JqTokenType.Def: return ParseDef();
            case JqTokenType.Label: return ParseLabel();
            case JqTokenType.Variable: Advance(); return new JqVariableNode(t.Value);
            case JqTokenType.Ident:
            {
                string name = t.Value;
                if (name == "null") { Advance(); return new JqLiteralNode(JqValue.Null); }
                if (name == "true") { Advance(); return new JqLiteralNode(JqValue.True); }
                if (name == "false") { Advance(); return new JqLiteralNode(JqValue.False); }
                if (name == "empty") { Advance(); return new JqFuncCallNode("empty", new List<JqNode>()); }
                if (name == "break") { Advance(); var label = Expect(JqTokenType.Variable); return new JqBreakNode(label.Value); }
                Advance();
                if (Peek().Type == JqTokenType.LParen)
                {
                    Advance();
                    var args = new List<JqNode>();
                    if (Peek().Type != JqTokenType.RParen)
                    {
                        args.Add(ParsePipe());
                        while (Peek().Type == JqTokenType.Semicolon) { Advance(); args.Add(ParsePipe()); }
                    }
                    Expect(JqTokenType.RParen);
                    return new JqFuncCallNode(name, args);
                }
                return new JqFuncCallNode(name, new List<JqNode>());
            }
            case JqTokenType.Not: Advance(); return new JqFuncCallNode("not", new List<JqNode>());
            case JqTokenType.Minus: Advance(); var ne = ParsePostfix(); return new JqNegateNode(ne);
            default: throw new JqException("unexpected token '" + t.Value + "' (" + t.Type + ") at position " + t.Pos);
        }
    }

    private JqNode ParseStringInterpolation(string encoded)
    {
        var rawParts = encoded.Split('\x01');
        var parts = new List<object>();
        for (int i = 0; i < rawParts.Length; i++)
        {
            string part = rawParts[i];
            if (part.StartsWith("\0INTERP:"))
            {
                string expr = part.Substring(8);
                var lexer = new JqLexer(expr); var tokens = lexer.Tokenize(); var parser = new JqParser(tokens, expr); parts.Add(parser.Parse());
            }
            else if (part.Length > 0) parts.Add(part);
        }
        return new JqStringInterpolationNode(parts);
    }

    private JqNode ParseArrayConstruction()
    {
        Expect(JqTokenType.LBracket);
        if (Peek().Type == JqTokenType.RBracket) { Advance(); return new JqArrayNode(null); }
        var expr = ParsePipe(); Expect(JqTokenType.RBracket); return new JqArrayNode(expr);
    }

    private JqNode ParseObjectConstruction()
    {
        Expect(JqTokenType.LBrace);
        var entries = new List<JqObjectEntryNode>();
        if (Peek().Type != JqTokenType.RBrace)
        {
            entries.Add(ParseObjectEntry());
            while (Peek().Type == JqTokenType.Comma) { Advance(); if (Peek().Type == JqTokenType.RBrace) break; entries.Add(ParseObjectEntry()); }
        }
        Expect(JqTokenType.RBrace);
        return new JqObjectNode(entries);
    }

    private JqObjectEntryNode ParseObjectEntry()
    {
        if (Peek().Type == JqTokenType.LParen) { Advance(); var key = ParsePipeExpr(); Expect(JqTokenType.RParen); Expect(JqTokenType.Colon); var value = ParsePipeExpr(); return new JqObjectEntryNode(key, value, isDynamic: true); }
        if (Peek().Type == JqTokenType.String)
        {
            var keyToken = Advance();
            if (keyToken.Value.Contains('\x01')) { var keyNode = ParseStringInterpolation(keyToken.Value); Expect(JqTokenType.Colon); var value = ParsePipeExpr(); return new JqObjectEntryNode(keyNode, value, isDynamic: true); }
            Expect(JqTokenType.Colon); var val = ParsePipeExpr();
            return new JqObjectEntryNode(new JqLiteralNode(JqValue.FromString(keyToken.Value)), val);
        }
        if (Peek().Type == JqTokenType.Ident || Peek().Type == JqTokenType.Variable)
        {
            bool isVar = Peek().Type == JqTokenType.Variable;
            var keyToken = Advance(); string name = keyToken.Value;
            if (Peek().Type == JqTokenType.Colon) { Advance(); var value = ParsePipeExpr(); return new JqObjectEntryNode(new JqLiteralNode(JqValue.FromString(name)), value, identKey: name); }
            if (isVar) return new JqObjectEntryNode(new JqLiteralNode(JqValue.FromString(name)), new JqVariableNode(name), identKey: name);
            return new JqObjectEntryNode(null, new JqFieldNode(name), identKey: name);
        }
        if (Peek().Type == JqTokenType.Format) { var fmt = Advance(); JqNode? expr = null; if (Peek().Type == JqTokenType.Colon) { Advance(); expr = ParsePipeExpr(); } return new JqObjectEntryNode(null, new JqFormatNode(fmt.Value, expr)); }
        throw new JqException("unexpected token in object construction at position " + Peek().Pos);
    }

    private JqNode ParseIf()
    {
        Expect(JqTokenType.If); var cond = ParsePipe(); Expect(JqTokenType.Then); var then = ParsePipe();
        var elifs = new List<(JqNode, JqNode)>();
        while (Peek().Type == JqTokenType.Elif) { Advance(); var elifCond = ParsePipe(); Expect(JqTokenType.Then); var elifBody = ParsePipe(); elifs.Add((elifCond, elifBody)); }
        JqNode? els = null;
        if (Peek().Type == JqTokenType.Else) { Advance(); els = ParsePipe(); }
        Expect(JqTokenType.End);
        return new JqIfNode(cond, then, elifs, els);
    }

    private JqNode ParseTry() { Advance(); var expr = ParsePostfix(); JqNode? catchExpr = null; if (Peek().Type == JqTokenType.Catch) { Advance(); catchExpr = ParsePostfix(); } return new JqTryNode(expr, catchExpr); }
    private JqNode ParseReduce() { Advance(); var expr = ParsePostfix(); Expect(JqTokenType.As); var varToken = Expect(JqTokenType.Variable); Expect(JqTokenType.LParen); var init = ParsePipe(); Expect(JqTokenType.Semicolon); var update = ParsePipe(); Expect(JqTokenType.RParen); return new JqReduceNode(expr, varToken.Value, init, update); }
    private JqNode ParseForeach() { Advance(); var expr = ParsePostfix(); Expect(JqTokenType.As); var varToken = Expect(JqTokenType.Variable); Expect(JqTokenType.LParen); var init = ParsePipe(); Expect(JqTokenType.Semicolon); var update = ParsePipe(); JqNode? extract = null; if (Peek().Type == JqTokenType.Semicolon) { Advance(); extract = ParsePipe(); } Expect(JqTokenType.RParen); return new JqForeachNode(expr, varToken.Value, init, update, extract); }
    private JqNode ParseDef() { Advance(); var name = Expect(JqTokenType.Ident); var parms = new List<string>(); if (Peek().Type == JqTokenType.LParen) { Advance(); if (Peek().Type != JqTokenType.RParen) { parms.Add(Expect(JqTokenType.Ident).Value); while (Peek().Type == JqTokenType.Semicolon) { Advance(); parms.Add(Expect(JqTokenType.Ident).Value); } } Expect(JqTokenType.RParen); } Expect(JqTokenType.Colon); var body = ParsePipe(); Expect(JqTokenType.Semicolon); var rest = ParsePipe(); return new JqFuncDefNode(name.Value, parms, body, rest); }
    private JqNode ParseLabel() { Advance(); var varToken = Expect(JqTokenType.Variable); Expect(JqTokenType.Pipe); var body = ParsePipe(); return new JqLabelNode(varToken.Value, body); }
}

#endregion

#region Interpreter

internal sealed class BreakException : Exception { public string Label; public BreakException(string label) : base("break") { Label = label; } }
internal sealed class EmptyException : Exception { public EmptyException() : base("empty") { } }
internal sealed class JqErrorException : Exception { public JqValue Value; public JqErrorException(JqValue value) : base(value.Kind == JqValueKind.String ? value.GetString() : "error") { Value = value; } }

internal sealed class JqFuncDef
{
    public List<string> Params; public JqNode Body;
    public Dictionary<string, JqFuncDef> ClosureFuncs; public Dictionary<string, JqValue> ClosureVars;
    public JqFuncDef(List<string> parms, JqNode body, Dictionary<string, JqFuncDef> closureFuncs, Dictionary<string, JqValue> closureVars)
    { Params = parms; Body = body; ClosureFuncs = closureFuncs; ClosureVars = closureVars; }
}

internal sealed class JqInterpreter
{
    private readonly JqOptions _options;
    private readonly Dictionary<string, JqValue> _variables = new();
    private readonly Dictionary<string, JqFuncDef> _functions = new();
    private List<JqValue> _inputQueue = new();
    private int _inputQueuePos;

    public JqInterpreter(JqOptions options)
    {
        _options = options;
        foreach (var kv in options.StringArgs) _variables[kv.Key] = JqValue.FromString(kv.Value);
        foreach (var kv in options.JsonArgs)
        {
            try { using var doc = JsonDocument.Parse(kv.Value); _variables[kv.Key] = JqValue.FromElement(doc.RootElement.Clone()); }
            catch { throw new JqException("invalid JSON for --argjson " + kv.Key); }
        }
        var envPairs = new List<KeyValuePair<string, JqValue>>();
        foreach (var e in Environment.GetEnvironmentVariables())
        {
            var entry = (System.Collections.DictionaryEntry)e;
            envPairs.Add(new KeyValuePair<string, JqValue>(entry.Key!.ToString()!, JqValue.FromString(entry.Value?.ToString() ?? "")));
        }
        _variables["ENV"] = JqValue.FromObject(envPairs);
    }

    public void SetInputQueue(List<JqValue> inputs) { _inputQueue = inputs; _inputQueuePos = 0; }

    public IEnumerable<JqValue> Eval(JqNode node, JqValue input)
    {
        switch (node)
        {
            case JqIdentityNode: yield return input; break;
            case JqLiteralNode lit: yield return lit.Value; break;
            case JqRecurseNode: foreach (var v in Recurse(input)) yield return v; break;

            case JqFieldNode field:
                if (input.Kind == JqValueKind.Null) { yield return JqValue.Null; break; }
                if (input.Kind != JqValueKind.Object) { if (field.Optional) break; throw new JqException("null (" + input.TypeName + ") and string (\"" + field.Name + "\") cannot be iterated over"); }
                yield return input.GetField(field.Name); break;

            case JqIndexNode idx:
                foreach (var indexVal in Eval(idx.Index, input))
                {
                    if (indexVal.Kind == JqValueKind.Number) { int ii = (int)indexVal.GetNumber(); if (input.Kind == JqValueKind.Null) yield return JqValue.Null; else if (input.Kind == JqValueKind.Array) yield return input.GetIndex(ii); else if (!idx.Optional) throw new JqException(input.TypeName + " cannot be indexed by number"); }
                    else if (indexVal.Kind == JqValueKind.String) { if (input.Kind == JqValueKind.Null) yield return JqValue.Null; else if (input.Kind == JqValueKind.Object) yield return input.GetField(indexVal.GetString()); else if (!idx.Optional) throw new JqException(input.TypeName + " cannot be indexed by string"); }
                    else if (!idx.Optional) throw new JqException("invalid index type: " + indexVal.TypeName);
                }
                break;

            case JqSliceNode slice:
            {
                int? from = null, to = null;
                if (slice.From != null) { foreach (var v in Eval(slice.From, input)) { from = (int)v.GetNumber(); break; } }
                if (slice.To != null) { foreach (var v in Eval(slice.To, input)) { to = (int)v.GetNumber(); break; } }
                yield return input.Slice(from, to); break;
            }

            case JqIterateNode iter:
                if (input.Kind == JqValueKind.Array) { var arr = input.GetArray(); for (int i = 0; i < arr.Count; i++) yield return arr[i]; }
                else if (input.Kind == JqValueKind.Object) { var obj = input.GetObject(); for (int i = 0; i < obj.Count; i++) yield return obj[i].Value; }
                else if (input.Kind == JqValueKind.Null) { if (!iter.Optional) throw new JqException("null cannot be iterated over"); }
                else { if (!iter.Optional) throw new JqException(input.TypeName + " is not iterable"); }
                break;

            case JqPipeNode pipe:
            {
                List<JqValue> leftValues;
                try { leftValues = new List<JqValue>(); foreach (var v in Eval(pipe.Left, input)) leftValues.Add(v); }
                catch (EmptyException) { break; }
                for (int pi = 0; pi < leftValues.Count; pi++)
                {
                    List<JqValue> rightResults = new();
                    try { foreach (var v in Eval(pipe.Right, leftValues[pi])) rightResults.Add(v); }
                    catch (EmptyException) { }
                    for (int ri = 0; ri < rightResults.Count; ri++) yield return rightResults[ri];
                }
                break;
            }

            case JqCommaNode comma:
            {
                List<JqValue> commaLeft = new();
                try { foreach (var v in Eval(comma.Left, input)) commaLeft.Add(v); } catch (EmptyException) { }
                for (int ci = 0; ci < commaLeft.Count; ci++) yield return commaLeft[ci];
                List<JqValue> commaRight = new();
                try { foreach (var v in Eval(comma.Right, input)) commaRight.Add(v); } catch (EmptyException) { }
                for (int ci = 0; ci < commaRight.Count; ci++) yield return commaRight[ci];
                break;
            }

            case JqParenNode paren: foreach (var v in Eval(paren.Expr, input)) yield return v; break;

            case JqArrayNode arr:
            {
                var result = new List<JqValue>();
                if (arr.Expr != null)
                {
                    try { foreach (var v in Eval(arr.Expr, input)) result.Add(v); }
                    catch (EmptyException) { }
                }
                yield return JqValue.FromArray(result); break;
            }

            case JqObjectNode obj:
            {
                var resultPairs = new List<List<KeyValuePair<string, JqValue>>>();
                resultPairs.Add(new List<KeyValuePair<string, JqValue>>());
                for (int ei = 0; ei < obj.Entries.Count; ei++)
                {
                    var entry = obj.Entries[ei];
                    var newResults = new List<List<KeyValuePair<string, JqValue>>>();
                    foreach (var current in resultPairs)
                    {
                        if (entry.IsDynamic || entry.Key != null)
                        {
                            IEnumerable<JqValue> keys;
                            if (entry.IsDynamic && entry.Key != null) keys = Eval(entry.Key, input);
                            else if (entry.Key is JqLiteralNode klit) keys = new[] { klit.Value };
                            else if (entry.Key != null) keys = Eval(entry.Key, input);
                            else keys = new[] { JqValue.FromString(entry.IdentKey ?? "") };
                            foreach (var keyVal in keys)
                            {
                                string keyStr = keyVal.Kind == JqValueKind.String ? keyVal.GetString() : keyVal.ToJqString();
                                foreach (var valResult in Eval(entry.Value, input))
                                { var newList = new List<KeyValuePair<string, JqValue>>(current); newList.Add(new KeyValuePair<string, JqValue>(keyStr, valResult)); newResults.Add(newList); }
                            }
                        }
                        else
                        {
                            string keyStr = entry.IdentKey ?? "";
                            foreach (var valResult in Eval(entry.Value, input))
                            { var newList = new List<KeyValuePair<string, JqValue>>(current); newList.Add(new KeyValuePair<string, JqValue>(keyStr, valResult)); newResults.Add(newList); }
                        }
                    }
                    resultPairs = newResults;
                }
                for (int i = 0; i < resultPairs.Count; i++) yield return JqValue.FromObject(resultPairs[i]);
                break;
            }

            case JqStringInterpolationNode interp:
                foreach (var v in EvalInterpolation(interp.Parts, input, 0, "")) yield return v;
                break;

            case JqIfNode ifNode:
                foreach (var condVal in Eval(ifNode.Condition, input))
                {
                    if (condVal.IsTruthy) { foreach (var v in Eval(ifNode.Then, input)) yield return v; }
                    else
                    {
                        bool handled = false;
                        for (int i = 0; i < ifNode.Elifs.Count; i++)
                        {
                            bool elifMatch = false;
                            foreach (var elifCond in Eval(ifNode.Elifs[i].Cond, input))
                            { if (elifCond.IsTruthy) { foreach (var v in Eval(ifNode.Elifs[i].Body, input)) yield return v; elifMatch = true; break; } }
                            if (elifMatch) { handled = true; break; }
                        }
                        if (!handled) { if (ifNode.Else != null) { foreach (var v in Eval(ifNode.Else, input)) yield return v; } else yield return input; }
                    }
                }
                break;

            case JqCompareNode cmp:
                foreach (var leftVal in Eval(cmp.Left, input))
                    foreach (var rightVal in Eval(cmp.Right, input))
                    {
                        int c = JqValue.Compare(leftVal, rightVal);
                        bool result = cmp.Op switch { JqTokenType.Eq => leftVal.Equals(rightVal), JqTokenType.Ne => !leftVal.Equals(rightVal), JqTokenType.Lt => c < 0, JqTokenType.Gt => c > 0, JqTokenType.Le => c <= 0, JqTokenType.Ge => c >= 0, _ => false };
                        yield return JqValue.FromBool(result);
                    }
                break;

            case JqArithNode arith:
                foreach (var leftVal in Eval(arith.Left, input))
                    foreach (var rightVal in Eval(arith.Right, input))
                        yield return DoArith(arith.Op, leftVal, rightVal);
                break;

            case JqBoolNode boolNode:
                if (boolNode.Op == JqTokenType.And) { foreach (var leftVal in Eval(boolNode.Left, input)) { if (!leftVal.IsTruthy) yield return leftVal; else foreach (var rightVal in Eval(boolNode.Right, input)) yield return rightVal; } }
                else { foreach (var leftVal in Eval(boolNode.Left, input)) { if (leftVal.IsTruthy) yield return leftVal; else foreach (var rightVal in Eval(boolNode.Right, input)) yield return rightVal; } }
                break;

            case JqNotNode notNode: foreach (var v in Eval(notNode.Expr, input)) yield return JqValue.FromBool(!v.IsTruthy); break;

            case JqNegateNode neg:
                foreach (var v in Eval(neg.Expr, input)) { if (v.Kind != JqValueKind.Number) throw new JqException("cannot negate " + v.TypeName); yield return JqValue.FromNumber(-v.GetNumber()); }
                break;

            case JqTryNode tryNode:
            {
                List<JqValue> tryResults = new();
                bool tryCaught = false;
                JqValue catchInput = JqValue.Null;
                bool useCatch = false;
                try { foreach (var v in Eval(tryNode.Expr, input)) tryResults.Add(v); }
                catch (JqErrorException ex) { tryCaught = true; useCatch = tryNode.Catch != null; catchInput = ex.Value; }
                catch (JqException) { tryCaught = true; useCatch = tryNode.Catch != null; catchInput = JqValue.Null; }
                catch (EmptyException) { tryCaught = true; }
                if (tryCaught) { if (useCatch) foreach (var v in Eval(tryNode.Catch!, catchInput)) yield return v; }
                else { foreach (var v in tryResults) yield return v; }
                break;
            }

            case JqOptionalNode opt:
            {
                IEnumerable<JqValue> results;
                try { results = Eval(opt.Expr, input); var list = new List<JqValue>(); foreach (var v in results) list.Add(v); results = list; }
                catch { break; }
                foreach (var v in results) yield return v; break;
            }

            case JqAlternativeNode alt:
            {
                List<JqValue> altResults = new();
                bool hasValue = false;
                try
                {
                    foreach (var v in Eval(alt.Left, input))
                    {
                        if (v.Kind != JqValueKind.Null && (v.Kind != JqValueKind.Boolean || v.GetBoolean())) { hasValue = true; altResults.Add(v); }
                    }
                }
                catch (EmptyException) { }
                if (hasValue) { for (int ai = 0; ai < altResults.Count; ai++) yield return altResults[ai]; }
                else { foreach (var v in Eval(alt.Right, input)) yield return v; }
                break;
            }

            case JqVariableNode varNode:
                if (_variables.TryGetValue(varNode.Name, out var val)) yield return val;
                else throw new JqException("$" + varNode.Name + " is not defined");
                break;

            case JqBindingNode binding:
                foreach (var bval in Eval(binding.Expr, input))
                {
                    var old = _variables.TryGetValue(binding.VarName, out var prev) ? prev : (JqValue?)null;
                    _variables[binding.VarName] = bval;
                    try { foreach (var v in Eval(binding.Body, input)) yield return v; }
                    finally { if (old.HasValue) _variables[binding.VarName] = old.Value; else _variables.Remove(binding.VarName); }
                }
                break;

            case JqReduceNode reduce:
            {
                JqValue acc = JqValue.Null;
                foreach (var initVal in Eval(reduce.Init, input)) { acc = initVal; break; }
                foreach (var rval in Eval(reduce.Expr, input))
                {
                    var oldVar = _variables.TryGetValue(reduce.VarName, out var prev2) ? prev2 : (JqValue?)null;
                    _variables[reduce.VarName] = rval;
                    try { foreach (var newAcc in Eval(reduce.Update, acc)) { acc = newAcc; break; } }
                    finally { if (oldVar.HasValue) _variables[reduce.VarName] = oldVar.Value; else _variables.Remove(reduce.VarName); }
                }
                yield return acc; break;
            }

            case JqForeachNode foreachNode:
            {
                JqValue acc = JqValue.Null;
                foreach (var initVal in Eval(foreachNode.Init, input)) { acc = initVal; break; }
                foreach (var fval in Eval(foreachNode.Expr, input))
                {
                    var oldVar = _variables.TryGetValue(foreachNode.VarName, out var prev3) ? prev3 : (JqValue?)null;
                    _variables[foreachNode.VarName] = fval;
                    try
                    {
                        foreach (var newAcc in Eval(foreachNode.Update, acc)) { acc = newAcc; break; }
                        if (foreachNode.Extract != null) { foreach (var v in Eval(foreachNode.Extract, acc)) yield return v; } else yield return acc;
                    }
                    finally { if (oldVar.HasValue) _variables[foreachNode.VarName] = oldVar.Value; else _variables.Remove(foreachNode.VarName); }
                }
                break;
            }

            case JqFuncDefNode funcDef:
            {
                var closureFuncs = new Dictionary<string, JqFuncDef>(_functions);
                var closureVars = new Dictionary<string, JqValue>(_variables);
                var newFunc = new JqFuncDef(funcDef.Params, funcDef.Body, closureFuncs, closureVars);
                // Allow recursive calls by including self in the closure
                closureFuncs[funcDef.Name] = newFunc;
                _functions[funcDef.Name] = newFunc;
                foreach (var v in Eval(funcDef.Rest, input)) yield return v;
                break;
            }

            case JqFuncCallNode call: foreach (var v in EvalFuncCall(call, input)) yield return v; break;

            case JqLabelNode label:
            {
                List<JqValue> labelResults = new();
                try { foreach (var v in Eval(label.Body, input)) labelResults.Add(v); }
                catch (BreakException ex) when (ex.Label == label.Name) { }
                foreach (var v in labelResults) yield return v;
                break;
            }

            case JqBreakNode brk: throw new BreakException(brk.Label);

            case JqFormatNode fmt:
            {
                JqNode expr = fmt.Expr ?? new JqIdentityNode();
                foreach (var v in Eval(expr, input)) yield return JqValue.FromString(ApplyFormat(fmt.FormatName, v));
                break;
            }

            case JqUpdateNode update: foreach (var v in EvalUpdate(update, input)) yield return v; break;

            case JqPostfixNode postfix:
                foreach (var baseVal in Eval(postfix.Expr, input))
                    foreach (var v in EvalPostfixAccess(postfix.Accessor, baseVal))
                        yield return v;
                break;

            default: throw new JqException("unknown node type: " + node.GetType().Name);
        }
    }

    private IEnumerable<JqValue> EvalPostfixAccess(JqNode accessor, JqValue input)
    {
        switch (accessor)
        {
            case JqFieldNode field:
                if (input.Kind == JqValueKind.Null) yield return JqValue.Null;
                else if (input.Kind == JqValueKind.Object) yield return input.GetField(field.Name);
                else if (!field.Optional) throw new JqException("null (" + input.TypeName + ") and string (\"" + field.Name + "\") cannot be iterated over");
                break;
            case JqIndexNode idx: foreach (var v in Eval(new JqIndexNode(idx.Index, idx.Optional), input)) yield return v; break;
            case JqSliceNode slice: foreach (var v in Eval(slice, input)) yield return v; break;
            case JqIterateNode iter: foreach (var v in Eval(iter, input)) yield return v; break;
            default: foreach (var v in Eval(accessor, input)) yield return v; break;
        }
    }

    private IEnumerable<JqValue> EvalInterpolation(List<object> parts, JqValue input, int index, string accumulated)
    {
        if (index >= parts.Count) { yield return JqValue.FromString(accumulated); yield break; }
        var part = parts[index];
        if (part is string s) { foreach (var v in EvalInterpolation(parts, input, index + 1, accumulated + s)) yield return v; }
        else if (part is JqNode exprNode) { foreach (var pval in Eval(exprNode, input)) { string strVal = pval.ToJqString(); foreach (var v in EvalInterpolation(parts, input, index + 1, accumulated + strVal)) yield return v; } }
    }

    private IEnumerable<JqValue> Recurse(JqValue input)
    {
        yield return input;
        if (input.Kind == JqValueKind.Array) { var arr = input.GetArray(); for (int i = 0; i < arr.Count; i++) foreach (var v in Recurse(arr[i])) yield return v; }
        else if (input.Kind == JqValueKind.Object) { var obj = input.GetObject(); for (int i = 0; i < obj.Count; i++) foreach (var v in Recurse(obj[i].Value)) yield return v; }
    }

    private static JqValue DoArith(JqTokenType op, JqValue left, JqValue right)
    {
        if (op == JqTokenType.Plus && left.Kind == JqValueKind.String && right.Kind == JqValueKind.String)
            return JqValue.FromString(left.GetString() + right.GetString());
        if (op == JqTokenType.Plus && left.Kind == JqValueKind.Array && right.Kind == JqValueKind.Array)
        {
            var la = left.GetArray(); var ra = right.GetArray();
            var result = new List<JqValue>(la.Count + ra.Count);
            for (int i = 0; i < la.Count; i++) result.Add(la[i]);
            for (int i = 0; i < ra.Count; i++) result.Add(ra[i]);
            return JqValue.FromArray(result);
        }
        if (op == JqTokenType.Plus && left.Kind == JqValueKind.Object && right.Kind == JqValueKind.Object)
        {
            var lo = left.GetObject(); var ro = right.GetObject();
            var result = new List<KeyValuePair<string, JqValue>>();
            var rightMap = new Dictionary<string, JqValue>();
            for (int i = 0; i < ro.Count; i++) rightMap[ro[i].Key] = ro[i].Value;
            var seen = new HashSet<string>();
            for (int i = 0; i < lo.Count; i++)
            {
                if (rightMap.TryGetValue(lo[i].Key, out var rv)) result.Add(new KeyValuePair<string, JqValue>(lo[i].Key, rv));
                else result.Add(lo[i]);
                seen.Add(lo[i].Key);
            }
            for (int i = 0; i < ro.Count; i++) { if (!seen.Contains(ro[i].Key)) result.Add(ro[i]); }
            return JqValue.FromObject(result);
        }
        if (op == JqTokenType.Minus && left.Kind == JqValueKind.Array && right.Kind == JqValueKind.Array)
        {
            var la = left.GetArray(); var ra = right.GetArray();
            var result = new List<JqValue>();
            for (int i = 0; i < la.Count; i++) { bool found = false; for (int j = 0; j < ra.Count; j++) { if (la[i].Equals(ra[j])) { found = true; break; } } if (!found) result.Add(la[i]); }
            return JqValue.FromArray(result);
        }
        if (left.Kind == JqValueKind.Null && op == JqTokenType.Plus) return right;
        if (right.Kind == JqValueKind.Null && op == JqTokenType.Plus) return left;
        if (left.Kind == JqValueKind.Number && right.Kind == JqValueKind.Number)
        {
            double l = left.GetNumber(), r = right.GetNumber();
            return op switch
            {
                JqTokenType.Plus => JqValue.FromNumber(l + r),
                JqTokenType.Minus => JqValue.FromNumber(l - r),
                JqTokenType.Star => JqValue.FromNumber(l * r),
                JqTokenType.Slash => r == 0 ? throw new JqException("number (" + l + ") and number (" + r + ") cannot be divided because the divisor is zero") : JqValue.FromNumber(l / r),
                JqTokenType.Percent => r == 0 ? throw new JqException("modulo by zero") : JqValue.FromNumber((double)((long)l % (long)r)),
                _ => throw new JqException("unknown arithmetic op"),
            };
        }
        if (left.Kind == JqValueKind.Null && op == JqTokenType.Minus && right.Kind == JqValueKind.Number) return JqValue.FromNumber(-right.GetNumber());
        throw new JqException(left.TypeName + " and " + right.TypeName + " cannot be " + OpName(op));
    }

    private static string OpName(JqTokenType op) => op switch { JqTokenType.Plus => "added", JqTokenType.Minus => "subtracted", JqTokenType.Star => "multiplied", JqTokenType.Slash => "divided", JqTokenType.Percent => "divided (modulo)", _ => "operated on" };

    private IEnumerable<JqValue> EvalUpdate(JqUpdateNode update, JqValue input)
    {
        if (update.Op == JqTokenType.Assign) { foreach (var newVal in Eval(update.Value, input)) yield return SetPath(input, update.Path, newVal); }
        else if (update.Op == JqTokenType.UpdateAssign) { yield return UpdatePath(input, update.Path, update.Value); }
        else if (update.Op == JqTokenType.AddAssign) yield return UpdatePathWithOp(input, update.Path, update.Value, JqTokenType.Plus);
        else if (update.Op == JqTokenType.SubAssign) yield return UpdatePathWithOp(input, update.Path, update.Value, JqTokenType.Minus);
        else if (update.Op == JqTokenType.MulAssign) yield return UpdatePathWithOp(input, update.Path, update.Value, JqTokenType.Star);
        else if (update.Op == JqTokenType.DivAssign) yield return UpdatePathWithOp(input, update.Path, update.Value, JqTokenType.Slash);
        else if (update.Op == JqTokenType.ModAssign) yield return UpdatePathWithOp(input, update.Path, update.Value, JqTokenType.Percent);
        else if (update.Op == JqTokenType.AltAssign) yield return UpdatePath(input, update.Path, new JqAlternativeNode(new JqIdentityNode(), update.Value));
    }

    private JqValue SetPath(JqValue root, JqNode pathExpr, JqValue newVal) { var paths = GetPaths(pathExpr, root); var result = root.DeepClone(); for (int pi = 0; pi < paths.Count; pi++) result = SetAtPath(result, paths[pi], newVal); return result; }
    private JqValue UpdatePath(JqValue root, JqNode pathExpr, JqNode updateExpr)
    {
        var paths = GetPaths(pathExpr, root); var result = root.DeepClone();
        for (int pi = 0; pi < paths.Count; pi++) { var currentVal = GetAtPath(result, paths[pi]); JqValue newVal = currentVal; foreach (var v in Eval(updateExpr, currentVal)) { newVal = v; break; } result = SetAtPath(result, paths[pi], newVal); }
        return result;
    }
    private JqValue UpdatePathWithOp(JqValue root, JqNode pathExpr, JqNode valueExpr, JqTokenType op)
    {
        var paths = GetPaths(pathExpr, root); var result = root.DeepClone();
        for (int pi = 0; pi < paths.Count; pi++) { var currentVal = GetAtPath(result, paths[pi]); foreach (var rhsVal in Eval(valueExpr, root)) { result = SetAtPath(result, paths[pi], DoArith(op, currentVal, rhsVal)); break; } }
        return result;
    }

    private List<List<object>> GetPaths(JqNode pathExpr, JqValue root)
    {
        var paths = new List<List<object>>();
        switch (pathExpr)
        {
            case JqFieldNode f: paths.Add(new List<object> { f.Name }); break;
            case JqIndexNode idx: foreach (var v in Eval(idx.Index, root)) { if (v.Kind == JqValueKind.Number) paths.Add(new List<object> { (int)v.GetNumber() }); else if (v.Kind == JqValueKind.String) paths.Add(new List<object> { v.GetString() }); break; } break;
            case JqIterateNode:
                if (root.Kind == JqValueKind.Array) { var arr = root.GetArray(); for (int i = 0; i < arr.Count; i++) paths.Add(new List<object> { i }); }
                else if (root.Kind == JqValueKind.Object) { var obj = root.GetObject(); for (int i = 0; i < obj.Count; i++) paths.Add(new List<object> { obj[i].Key }); }
                break;
            case JqPipeNode pipe:
                var leftPaths = GetPaths(pipe.Left, root);
                for (int i = 0; i < leftPaths.Count; i++) { var intermediate = GetAtPath(root, leftPaths[i]); var rightPaths = GetPaths(pipe.Right, intermediate); for (int j = 0; j < rightPaths.Count; j++) { var combined = new List<object>(leftPaths[i]); combined.AddRange(rightPaths[j]); paths.Add(combined); } }
                break;
            case JqPostfixNode postfix:
                var basePaths = GetPaths(postfix.Expr, root);
                for (int i = 0; i < basePaths.Count; i++) { var intermediate2 = GetAtPath(root, basePaths[i]); var accessorPaths = GetPaths(postfix.Accessor, intermediate2); for (int j = 0; j < accessorPaths.Count; j++) { var combined = new List<object>(basePaths[i]); combined.AddRange(accessorPaths[j]); paths.Add(combined); } }
                break;
            case JqIdentityNode: paths.Add(new List<object>()); break;
            case JqFuncCallNode call when call.Name == "select":
                if (call.Args.Count == 1) { foreach (var v in Eval(call.Args[0], root)) { if (v.IsTruthy) paths.Add(new List<object>()); break; } }
                break;
            case JqParenNode paren: return GetPaths(paren.Expr, root);
            default: paths.Add(new List<object>()); break;
        }
        return paths;
    }

    private static JqValue GetAtPath(JqValue root, List<object> path)
    {
        var current = root;
        for (int i = 0; i < path.Count; i++)
        {
            var seg = path[i];
            if (seg is string key) current = current.GetField(key);
            else if (seg is int idx) current = current.GetIndex(idx);
        }
        return current;
    }

    private static JqValue SetAtPath(JqValue root, List<object> path, JqValue newVal)
    {
        if (path.Count == 0) return newVal;
        var seg = path[0];
        if (seg is string key)
        {
            if (root.Kind != JqValueKind.Object) root = JqValue.FromObject(new List<KeyValuePair<string, JqValue>>());
            var obj = root.GetObject(); var newObj = new List<KeyValuePair<string, JqValue>>(obj.Count);
            bool found = false; var subPath = path.GetRange(1, path.Count - 1);
            for (int i = 0; i < obj.Count; i++) { if (obj[i].Key == key) { found = true; newObj.Add(new KeyValuePair<string, JqValue>(key, SetAtPath(obj[i].Value, subPath, newVal))); } else newObj.Add(obj[i]); }
            if (!found) newObj.Add(new KeyValuePair<string, JqValue>(key, SetAtPath(JqValue.Null, subPath, newVal)));
            return JqValue.FromObject(newObj);
        }
        else if (seg is int idx)
        {
            if (root.Kind != JqValueKind.Array) root = JqValue.FromArray(new List<JqValue>());
            var arr = root.GetArray(); int realIdx = idx < 0 ? idx + arr.Count : idx;
            var newArr = new List<JqValue>(arr); while (newArr.Count <= realIdx) newArr.Add(JqValue.Null);
            var subPath = path.GetRange(1, path.Count - 1); newArr[realIdx] = SetAtPath(newArr[realIdx], subPath, newVal);
            return JqValue.FromArray(newArr);
        }
        return root;
    }

    private IEnumerable<JqValue> EvalFuncCall(JqFuncCallNode call, JqValue input)
    {
        if (_functions.TryGetValue(call.Name, out var funcDef) && funcDef.Params.Count == call.Args.Count) { foreach (var v in CallUserFunc(funcDef, call.Args, input)) yield return v; yield break; }

        switch (call.Name)
        {
            case "empty": throw new EmptyException();
            case "error":
                if (call.Args.Count > 0) { foreach (var v in Eval(call.Args[0], input)) throw new JqErrorException(v); }
                throw new JqErrorException(input);
            case "debug": Console.Error.WriteLine("[\"DEBUG:\",{0}]", input.ToJsonString(new JqOptions())); yield return input; break;
            case "stderr": Console.Error.Write(input.ToJsonString(new JqOptions())); yield return input; break;
            case "length": yield return JqValue.FromNumber(input.Length); break;
            case "utf8bytelength": yield return input.Kind == JqValueKind.String ? JqValue.FromNumber(Encoding.UTF8.GetByteCount(input.GetString())) : JqValue.FromNumber(input.Length); break;

            case "keys" or "keys_unsorted":
                if (input.Kind == JqValueKind.Object)
                {
                    var obj = input.GetObject(); var keys = new List<JqValue>(obj.Count);
                    for (int i = 0; i < obj.Count; i++) keys.Add(JqValue.FromString(obj[i].Key));
                    if (call.Name == "keys") keys.Sort((a, b) => string.Compare(a.GetString(), b.GetString(), StringComparison.Ordinal));
                    yield return JqValue.FromArray(keys);
                }
                else if (input.Kind == JqValueKind.Array) { var arr = input.GetArray(); var keys = new List<JqValue>(arr.Count); for (int i = 0; i < arr.Count; i++) keys.Add(JqValue.FromNumber(i)); yield return JqValue.FromArray(keys); }
                else throw new JqException(input.TypeName + " has no keys");
                break;

            case "values":
                if (input.Kind != JqValueKind.Null) yield return input;
                break;

            case "has":
                if (call.Args.Count < 1) throw new JqException("has requires 1 argument");
                foreach (var key in Eval(call.Args[0], input))
                {
                    if (input.Kind == JqValueKind.Object && key.Kind == JqValueKind.String) { var obj = input.GetObject(); bool found = false; string k = key.GetString(); for (int i = 0; i < obj.Count; i++) { if (obj[i].Key == k) { found = true; break; } } yield return JqValue.FromBool(found); }
                    else if (input.Kind == JqValueKind.Array && key.Kind == JqValueKind.Number) { int idx = (int)key.GetNumber(); yield return JqValue.FromBool(idx >= 0 && idx < input.GetArray().Count); }
                    else yield return JqValue.False;
                }
                break;

            case "in":
                if (call.Args.Count < 1) throw new JqException("in requires 1 argument");
                foreach (var obj in Eval(call.Args[0], input))
                {
                    if (obj.Kind == JqValueKind.Object && input.Kind == JqValueKind.String) { var o = obj.GetObject(); bool found = false; string k = input.GetString(); for (int i = 0; i < o.Count; i++) { if (o[i].Key == k) { found = true; break; } } yield return JqValue.FromBool(found); }
                    else yield return JqValue.False;
                }
                break;

            case "map":
                if (call.Args.Count < 1) throw new JqException("map requires 1 argument");
                { var result = new List<JqValue>(); if (input.Kind == JqValueKind.Array) { var arr = input.GetArray(); for (int i = 0; i < arr.Count; i++) foreach (var v in Eval(call.Args[0], arr[i])) result.Add(v); } yield return JqValue.FromArray(result); }
                break;

            case "map_values":
                if (call.Args.Count < 1) throw new JqException("map_values requires 1 argument");
                if (input.Kind == JqValueKind.Object)
                {
                    var obj = input.GetObject(); var result = new List<KeyValuePair<string, JqValue>>();
                    for (int i = 0; i < obj.Count; i++) foreach (var v in Eval(call.Args[0], obj[i].Value)) { result.Add(new KeyValuePair<string, JqValue>(obj[i].Key, v)); break; }
                    yield return JqValue.FromObject(result);
                }
                else if (input.Kind == JqValueKind.Array)
                { var arr = input.GetArray(); var result = new List<JqValue>(); for (int i = 0; i < arr.Count; i++) foreach (var v in Eval(call.Args[0], arr[i])) { result.Add(v); break; } yield return JqValue.FromArray(result); }
                break;

            case "select":
                if (call.Args.Count < 1) throw new JqException("select requires 1 argument");
                foreach (var cond in Eval(call.Args[0], input)) { if (cond.IsTruthy) yield return input; break; }
                break;

            case "add":
                if (input.Kind != JqValueKind.Array) throw new JqException("cannot add " + input.TypeName);
                { var arr = input.GetArray(); if (arr.Count == 0) { yield return JqValue.Null; break; } var result = arr[0]; for (int i = 1; i < arr.Count; i++) result = DoArith(JqTokenType.Plus, result, arr[i]); yield return result; }
                break;

            case "any":
                if (input.Kind == JqValueKind.Array)
                {
                    var arr = input.GetArray(); bool found = false;
                    if (call.Args.Count == 0) { for (int i = 0; i < arr.Count; i++) { if (arr[i].IsTruthy) { found = true; break; } } }
                    else { for (int i = 0; i < arr.Count; i++) { foreach (var v in Eval(call.Args[0], arr[i])) { if (v.IsTruthy) { found = true; break; } } if (found) break; } }
                    yield return JqValue.FromBool(found);
                }
                break;

            case "all":
                if (input.Kind == JqValueKind.Array)
                {
                    var arr = input.GetArray(); bool allTrue = true;
                    if (call.Args.Count == 0) { for (int i = 0; i < arr.Count; i++) { if (!arr[i].IsTruthy) { allTrue = false; break; } } }
                    else { for (int i = 0; i < arr.Count; i++) { foreach (var v in Eval(call.Args[0], arr[i])) { if (!v.IsTruthy) { allTrue = false; break; } } if (!allTrue) break; } }
                    yield return JqValue.FromBool(allTrue);
                }
                break;

            case "flatten":
            {
                int depth = int.MaxValue;
                if (call.Args.Count > 0) foreach (var v in Eval(call.Args[0], input)) { depth = (int)v.GetNumber(); break; }
                yield return FlattenArray(input, depth); break;
            }

            case "range":
                if (call.Args.Count == 1) { foreach (var nVal in Eval(call.Args[0], input)) { int n = (int)nVal.GetNumber(); for (int i = 0; i < n; i++) yield return JqValue.FromNumber(i); } }
                else if (call.Args.Count >= 2)
                {
                    foreach (var fromVal in Eval(call.Args[0], input))
                    foreach (var toVal in Eval(call.Args[1], input))
                    {
                        double from = fromVal.GetNumber(), to = toVal.GetNumber(), step = 1;
                        if (call.Args.Count >= 3) foreach (var stepVal in Eval(call.Args[2], input)) { step = stepVal.GetNumber(); break; }
                        if (step > 0) { for (double i = from; i < to; i += step) yield return JqValue.FromNumber(i); }
                        else if (step < 0) { for (double i = from; i > to; i += step) yield return JqValue.FromNumber(i); }
                    }
                }
                break;

            case "type": yield return JqValue.FromString(input.TypeName); break;
            case "infinite": yield return JqValue.FromNumber(double.PositiveInfinity); break;
            case "nan": yield return JqValue.FromNumber(double.NaN); break;
            case "isinfinite": yield return JqValue.FromBool(input.Kind == JqValueKind.Number && double.IsInfinity(input.GetNumber())); break;
            case "isnan": yield return JqValue.FromBool(input.Kind == JqValueKind.Number && double.IsNaN(input.GetNumber())); break;
            case "isnormal": yield return JqValue.FromBool(input.Kind == JqValueKind.Number && double.IsNormal(input.GetNumber())); break;

            case "tostring":
                if (input.Kind == JqValueKind.String) yield return input;
                else yield return JqValue.FromString(input.ToJsonString(new JqOptions()));
                break;

            case "tonumber":
                if (input.Kind == JqValueKind.Number) yield return input;
                else if (input.Kind == JqValueKind.String) { if (double.TryParse(input.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double n)) yield return JqValue.FromNumber(n); else throw new JqException("invalid number: " + input.GetString()); }
                else throw new JqException(input.TypeName + " cannot be converted to number");
                break;

            case "ascii_downcase": if (input.Kind == JqValueKind.String) yield return JqValue.FromString(input.GetString().ToLowerInvariant()); else throw new JqException(input.TypeName + " cannot be downcase'd"); break;
            case "ascii_upcase": if (input.Kind == JqValueKind.String) yield return JqValue.FromString(input.GetString().ToUpperInvariant()); else throw new JqException(input.TypeName + " cannot be upcase'd"); break;

            case "ltrimstr":
                if (call.Args.Count < 1) throw new JqException("ltrimstr requires 1 argument");
                foreach (var prefix in Eval(call.Args[0], input)) { if (input.Kind == JqValueKind.String && prefix.Kind == JqValueKind.String) { string s = input.GetString(), p = prefix.GetString(); yield return s.StartsWith(p, StringComparison.Ordinal) ? JqValue.FromString(s.Substring(p.Length)) : input; } else yield return input; }
                break;

            case "rtrimstr":
                if (call.Args.Count < 1) throw new JqException("rtrimstr requires 1 argument");
                foreach (var suffix in Eval(call.Args[0], input)) { if (input.Kind == JqValueKind.String && suffix.Kind == JqValueKind.String) { string s = input.GetString(), p = suffix.GetString(); yield return s.EndsWith(p, StringComparison.Ordinal) ? JqValue.FromString(s.Substring(0, s.Length - p.Length)) : input; } else yield return input; }
                break;

            case "startswith":
                if (call.Args.Count < 1) throw new JqException("startswith requires 1 argument");
                foreach (var prefix in Eval(call.Args[0], input)) yield return input.Kind == JqValueKind.String && prefix.Kind == JqValueKind.String ? JqValue.FromBool(input.GetString().StartsWith(prefix.GetString(), StringComparison.Ordinal)) : JqValue.False;
                break;

            case "endswith":
                if (call.Args.Count < 1) throw new JqException("endswith requires 1 argument");
                foreach (var suffix in Eval(call.Args[0], input)) yield return input.Kind == JqValueKind.String && suffix.Kind == JqValueKind.String ? JqValue.FromBool(input.GetString().EndsWith(suffix.GetString(), StringComparison.Ordinal)) : JqValue.False;
                break;

            case "test":
                if (call.Args.Count < 1) throw new JqException("test requires 1 argument");
                foreach (var patternVal in Eval(call.Args[0], input)) { string pattern = patternVal.GetString(), flags = ""; if (call.Args.Count >= 2) foreach (var f in Eval(call.Args[1], input)) { flags = f.GetString(); break; } yield return JqValue.FromBool(MakeRegex(pattern, flags).IsMatch(input.GetString())); }
                break;

            case "match":
                if (call.Args.Count < 1) throw new JqException("match requires 1 argument");
                foreach (var patternVal in Eval(call.Args[0], input))
                {
                    string pattern = patternVal.GetString(), flags = "";
                    if (call.Args.Count >= 2) foreach (var f in Eval(call.Args[1], input)) { flags = f.GetString(); break; }
                    bool global = flags.Contains('g'); flags = flags.Replace("g", "");
                    var regex = MakeRegex(pattern, flags); var matches = regex.Matches(input.GetString());
                    for (int mi = 0; mi < matches.Count; mi++) { yield return MatchToValue(matches[mi]); if (!global) break; }
                }
                break;

            case "capture":
                if (call.Args.Count < 1) throw new JqException("capture requires 1 argument");
                foreach (var patternVal in Eval(call.Args[0], input))
                {
                    string pattern = patternVal.GetString(), flags = "";
                    if (call.Args.Count >= 2) foreach (var f in Eval(call.Args[1], input)) { flags = f.GetString(); break; }
                    var regex = MakeRegex(pattern, flags); var m = regex.Match(input.GetString());
                    if (m.Success)
                    {
                        var pairs = new List<KeyValuePair<string, JqValue>>();
                        for (int gi = 1; gi < m.Groups.Count; gi++) { string gname = regex.GroupNameFromNumber(gi); if (gname != gi.ToString()) pairs.Add(new KeyValuePair<string, JqValue>(gname, JqValue.FromString(m.Groups[gi].Value))); }
                        yield return JqValue.FromObject(pairs);
                    }
                }
                break;

            case "split":
                if (call.Args.Count < 1) throw new JqException("split requires 1 argument");
                foreach (var sep in Eval(call.Args[0], input))
                {
                    if (input.Kind == JqValueKind.String && sep.Kind == JqValueKind.String)
                    {
                        string[] parts;
                        if (call.Args.Count >= 2) { string flags = ""; foreach (var f in Eval(call.Args[1], input)) { flags = f.GetString(); break; } parts = MakeRegex(sep.GetString(), flags).Split(input.GetString()); }
                        else parts = input.GetString().Split(sep.GetString());
                        var result = new List<JqValue>(parts.Length); for (int i = 0; i < parts.Length; i++) result.Add(JqValue.FromString(parts[i]));
                        yield return JqValue.FromArray(result);
                    }
                    else throw new JqException("split requires string input and separator");
                }
                break;

            case "join":
                if (call.Args.Count < 1) throw new JqException("join requires 1 argument");
                foreach (var sep in Eval(call.Args[0], input))
                {
                    if (input.Kind != JqValueKind.Array) throw new JqException("join requires array input");
                    string sepStr = sep.GetString(); var arr = input.GetArray(); var sb = new StringBuilder();
                    for (int i = 0; i < arr.Count; i++) { if (i > 0) sb.Append(sepStr); if (arr[i].Kind == JqValueKind.String) sb.Append(arr[i].GetString()); else if (arr[i].Kind != JqValueKind.Null) sb.Append(arr[i].ToJqString()); }
                    yield return JqValue.FromString(sb.ToString());
                }
                break;

            case "gsub":
                if (call.Args.Count < 2) throw new JqException("gsub requires 2 arguments");
                foreach (var patternVal in Eval(call.Args[0], input))
                {
                    string pattern = patternVal.GetString(), flags = "";
                    if (call.Args.Count >= 3) foreach (var f in Eval(call.Args[2], input)) { flags = f.GetString(); break; }
                    var regex = MakeRegex(pattern, flags);
                    string result = regex.Replace(input.GetString(), m => { foreach (var v in Eval(call.Args[1], MatchToValue(m))) return v.Kind == JqValueKind.String ? v.GetString() : v.ToJqString(); return ""; });
                    yield return JqValue.FromString(result);
                }
                break;

            case "sub":
                if (call.Args.Count < 2) throw new JqException("sub requires 2 arguments");
                foreach (var patternVal in Eval(call.Args[0], input))
                {
                    string pattern = patternVal.GetString(), flags = "";
                    if (call.Args.Count >= 3) foreach (var f in Eval(call.Args[2], input)) { flags = f.GetString(); break; }
                    var regex = MakeRegex(pattern, flags);
                    bool replaced = false;
                    string result = regex.Replace(input.GetString(), m => { if (replaced) return m.Value; replaced = true; foreach (var v in Eval(call.Args[1], MatchToValue(m))) return v.Kind == JqValueKind.String ? v.GetString() : v.ToJqString(); return ""; });
                    yield return JqValue.FromString(result);
                }
                break;

            case "sort":
                if (input.Kind != JqValueKind.Array) throw new JqException("cannot sort " + input.TypeName);
                { var sorted = new List<JqValue>(input.GetArray()); sorted.Sort(JqValue.Compare); yield return JqValue.FromArray(sorted); }
                break;

            case "sort_by":
                if (call.Args.Count < 1) throw new JqException("sort_by requires 1 argument");
                if (input.Kind != JqValueKind.Array) throw new JqException("cannot sort " + input.TypeName);
                {
                    var arr = input.GetArray(); var keyed = new List<(JqValue Key, JqValue Value)>(arr.Count);
                    for (int i = 0; i < arr.Count; i++) { JqValue key = JqValue.Null; foreach (var v in Eval(call.Args[0], arr[i])) { key = v; break; } keyed.Add((key, arr[i])); }
                    keyed.Sort((a, b) => JqValue.Compare(a.Key, b.Key));
                    var result = new List<JqValue>(keyed.Count); for (int i = 0; i < keyed.Count; i++) result.Add(keyed[i].Value);
                    yield return JqValue.FromArray(result);
                }
                break;

            case "group_by":
                if (call.Args.Count < 1) throw new JqException("group_by requires 1 argument");
                if (input.Kind != JqValueKind.Array) throw new JqException("cannot group " + input.TypeName);
                {
                    var arr = input.GetArray(); var keyed = new List<(JqValue Key, JqValue Value)>(arr.Count);
                    for (int i = 0; i < arr.Count; i++) { JqValue key = JqValue.Null; foreach (var v in Eval(call.Args[0], arr[i])) { key = v; break; } keyed.Add((key, arr[i])); }
                    keyed.Sort((a, b) => JqValue.Compare(a.Key, b.Key));
                    var groups = new List<JqValue>();
                    if (keyed.Count > 0) { var curGroup = new List<JqValue> { keyed[0].Value }; var curKey = keyed[0].Key; for (int i = 1; i < keyed.Count; i++) { if (keyed[i].Key.Equals(curKey)) curGroup.Add(keyed[i].Value); else { groups.Add(JqValue.FromArray(curGroup)); curGroup = new List<JqValue> { keyed[i].Value }; curKey = keyed[i].Key; } } groups.Add(JqValue.FromArray(curGroup)); }
                    yield return JqValue.FromArray(groups);
                }
                break;

            case "unique":
                if (input.Kind != JqValueKind.Array) throw new JqException("cannot unique " + input.TypeName);
                { var sorted = new List<JqValue>(input.GetArray()); sorted.Sort(JqValue.Compare); var result = new List<JqValue>(); for (int i = 0; i < sorted.Count; i++) if (i == 0 || !sorted[i].Equals(sorted[i - 1])) result.Add(sorted[i]); yield return JqValue.FromArray(result); }
                break;

            case "unique_by":
                if (call.Args.Count < 1) throw new JqException("unique_by requires 1 argument");
                if (input.Kind != JqValueKind.Array) throw new JqException("cannot unique " + input.TypeName);
                {
                    var arr = input.GetArray(); var keyed = new List<(JqValue Key, JqValue Value, int Idx)>(arr.Count);
                    for (int i = 0; i < arr.Count; i++) { JqValue key = JqValue.Null; foreach (var v in Eval(call.Args[0], arr[i])) { key = v; break; } keyed.Add((key, arr[i], i)); }
                    keyed.Sort((a, b) => { int c = JqValue.Compare(a.Key, b.Key); return c != 0 ? c : a.Idx.CompareTo(b.Idx); });
                    var result = new List<JqValue>(); for (int i = 0; i < keyed.Count; i++) if (i == 0 || !keyed[i].Key.Equals(keyed[i - 1].Key)) result.Add(keyed[i].Value);
                    yield return JqValue.FromArray(result);
                }
                break;

            case "reverse":
                if (input.Kind == JqValueKind.Array) { var arr = input.GetArray(); var result = new List<JqValue>(arr.Count); for (int i = arr.Count - 1; i >= 0; i--) result.Add(arr[i]); yield return JqValue.FromArray(result); }
                else if (input.Kind == JqValueKind.String) { var chars = input.GetString().ToCharArray(); Array.Reverse(chars); yield return JqValue.FromString(new string(chars)); }
                else throw new JqException(input.TypeName + " cannot be reversed");
                break;

            case "contains":
                if (call.Args.Count < 1) throw new JqException("contains requires 1 argument");
                foreach (var other in Eval(call.Args[0], input)) yield return JqValue.FromBool(input.Contains(other));
                break;

            case "inside":
                if (call.Args.Count < 1) throw new JqException("inside requires 1 argument");
                foreach (var other in Eval(call.Args[0], input)) yield return JqValue.FromBool(other.Contains(input));
                break;

            case "to_entries":
                if (input.Kind != JqValueKind.Object) throw new JqException(input.TypeName + " cannot be converted to entries");
                { var obj = input.GetObject(); var result = new List<JqValue>(obj.Count); for (int i = 0; i < obj.Count; i++) result.Add(JqValue.FromObject(new List<KeyValuePair<string, JqValue>> { new("key", JqValue.FromString(obj[i].Key)), new("value", obj[i].Value) })); yield return JqValue.FromArray(result); }
                break;

            case "from_entries":
                if (input.Kind != JqValueKind.Array) throw new JqException(input.TypeName + " cannot be converted from entries");
                {
                    var arr = input.GetArray(); var result = new List<KeyValuePair<string, JqValue>>();
                    for (int i = 0; i < arr.Count; i++) { if (arr[i].Kind != JqValueKind.Object) continue; var keyVal = arr[i].GetField("key"); if (keyVal.Kind == JqValueKind.Null) keyVal = arr[i].GetField("name"); var vval = arr[i].GetField("value"); string key = keyVal.Kind == JqValueKind.String ? keyVal.GetString() : keyVal.ToJqString(); result.Add(new KeyValuePair<string, JqValue>(key, vval)); }
                    yield return JqValue.FromObject(result);
                }
                break;

            case "with_entries":
                if (call.Args.Count < 1) throw new JqException("with_entries requires 1 argument");
                if (input.Kind != JqValueKind.Object) throw new JqException(input.TypeName + " cannot be used with with_entries");
                {
                    var obj = input.GetObject(); var entries = new List<JqValue>(obj.Count);
                    for (int i = 0; i < obj.Count; i++) { var entry = JqValue.FromObject(new List<KeyValuePair<string, JqValue>> { new("key", JqValue.FromString(obj[i].Key)), new("value", obj[i].Value) }); foreach (var v in Eval(call.Args[0], entry)) entries.Add(v); }
                    var result = new List<KeyValuePair<string, JqValue>>();
                    for (int i = 0; i < entries.Count; i++) { if (entries[i].Kind != JqValueKind.Object) continue; var keyVal = entries[i].GetField("key"); if (keyVal.Kind == JqValueKind.Null) keyVal = entries[i].GetField("name"); var vval = entries[i].GetField("value"); string key = keyVal.Kind == JqValueKind.String ? keyVal.GetString() : keyVal.ToJqString(); result.Add(new KeyValuePair<string, JqValue>(key, vval)); }
                    yield return JqValue.FromObject(result);
                }
                break;

            case "paths":
                if (call.Args.Count == 0) { foreach (var p in GetAllPaths(input, new List<JqValue>())) yield return p; }
                else { foreach (var p in GetAllPathsFiltered(input, call.Args[0], new List<JqValue>())) yield return p; }
                break;

            case "leaf_paths": foreach (var p in GetLeafPaths(input, new List<JqValue>())) yield return p; break;

            case "path":
                if (call.Args.Count < 1) throw new JqException("path requires 1 argument");
                { var pathExprs = GetPaths(call.Args[0], input); for (int i = 0; i < pathExprs.Count; i++) { var pathArr = new List<JqValue>(pathExprs[i].Count); for (int j = 0; j < pathExprs[i].Count; j++) { var seg = pathExprs[i][j]; if (seg is string s) pathArr.Add(JqValue.FromString(s)); else if (seg is int idx) pathArr.Add(JqValue.FromNumber(idx)); } yield return JqValue.FromArray(pathArr); } }
                break;

            case "getpath":
                if (call.Args.Count < 1) throw new JqException("getpath requires 1 argument");
                foreach (var pathArr in Eval(call.Args[0], input))
                {
                    if (pathArr.Kind != JqValueKind.Array) throw new JqException("getpath requires array argument");
                    var path = pathArr.GetArray(); var result = input;
                    for (int i = 0; i < path.Count; i++) { if (path[i].Kind == JqValueKind.String) result = result.GetField(path[i].GetString()); else if (path[i].Kind == JqValueKind.Number) result = result.GetIndex((int)path[i].GetNumber()); }
                    yield return result;
                }
                break;

            case "setpath":
                if (call.Args.Count < 2) throw new JqException("setpath requires 2 arguments");
                foreach (var pathArr in Eval(call.Args[0], input))
                    foreach (var setval in Eval(call.Args[1], input))
                    {
                        if (pathArr.Kind != JqValueKind.Array) throw new JqException("setpath requires array path");
                        var path = new List<object>(); var pa = pathArr.GetArray();
                        for (int i = 0; i < pa.Count; i++) { if (pa[i].Kind == JqValueKind.String) path.Add(pa[i].GetString()); else if (pa[i].Kind == JqValueKind.Number) path.Add((int)pa[i].GetNumber()); }
                        yield return SetAtPath(input, path, setval);
                    }
                break;

            case "delpaths":
                if (call.Args.Count < 1) throw new JqException("delpaths requires 1 argument");
                foreach (var pathsArr in Eval(call.Args[0], input))
                {
                    if (pathsArr.Kind != JqValueKind.Array) throw new JqException("delpaths requires array of paths");
                    var dpaths = pathsArr.GetArray(); var pathLists = new List<List<object>>(dpaths.Count);
                    for (int i = 0; i < dpaths.Count; i++) { var pa = dpaths[i].GetArray(); var path = new List<object>(pa.Count); for (int j = 0; j < pa.Count; j++) { if (pa[j].Kind == JqValueKind.String) path.Add(pa[j].GetString()); else if (pa[j].Kind == JqValueKind.Number) path.Add((int)pa[j].GetNumber()); } pathLists.Add(path); }
                    var result = input.DeepClone();
                    pathLists.Sort((a, b) => { int minLen = Math.Min(a.Count, b.Count); for (int i = 0; i < minLen; i++) { if (a[i] is int ai && b[i] is int bi) { int c = bi.CompareTo(ai); if (c != 0) return c; } } return b.Count.CompareTo(a.Count); });
                    for (int i = 0; i < pathLists.Count; i++) result = DeleteAtPath(result, pathLists[i]);
                    yield return result;
                }
                break;

            case "del":
                if (call.Args.Count < 1) throw new JqException("del requires 1 argument");
                {
                    var pathsToDelete = GetPaths(call.Args[0], input); var result = input.DeepClone();
                    pathsToDelete.Sort((a, b) => { int minLen = Math.Min(a.Count, b.Count); for (int i = 0; i < minLen; i++) { if (a[i] is int ai && b[i] is int bi) { int c = bi.CompareTo(ai); if (c != 0) return c; } } return b.Count.CompareTo(a.Count); });
                    for (int i = 0; i < pathsToDelete.Count; i++) result = DeleteAtPath(result, pathsToDelete[i]);
                    yield return result;
                }
                break;

            case "env":
                if (_variables.TryGetValue("ENV", out var envObj)) yield return envObj;
                else yield return JqValue.FromObject(new List<KeyValuePair<string, JqValue>>());
                break;

            case "input":
                if (_inputQueuePos < _inputQueue.Count) { yield return _inputQueue[_inputQueuePos]; _inputQueue.RemoveAt(_inputQueuePos); }
                break;

            case "inputs":
                while (_inputQueuePos < _inputQueue.Count) { yield return _inputQueue[_inputQueuePos]; _inputQueue.RemoveAt(_inputQueuePos); }
                break;

            case "limit":
                if (call.Args.Count < 2) throw new JqException("limit requires 2 arguments");
                { int n = 0; foreach (var nVal in Eval(call.Args[0], input)) { n = (int)nVal.GetNumber(); break; } int count = 0; foreach (var v in Eval(call.Args[1], input)) { if (count >= n) break; yield return v; count++; } }
                break;

            case "first":
                if (call.Args.Count >= 1) { foreach (var v in Eval(call.Args[0], input)) { yield return v; break; } }
                else yield return input;
                break;

            case "last":
                if (call.Args.Count >= 1) { JqValue lastVal = JqValue.Null; bool hasValue = false; foreach (var v in Eval(call.Args[0], input)) { lastVal = v; hasValue = true; } if (hasValue) yield return lastVal; }
                else yield return input;
                break;

            case "nth":
                if (call.Args.Count < 2) throw new JqException("nth requires 2 arguments");
                { int n = 0; foreach (var nVal in Eval(call.Args[0], input)) { n = (int)nVal.GetNumber(); break; } int count = 0; foreach (var v in Eval(call.Args[1], input)) { if (count == n) { yield return v; break; } count++; } }
                break;

            case "indices" or "index" or "rindex":
                if (call.Args.Count < 1) throw new JqException(call.Name + " requires 1 argument");
                foreach (var searchVal in Eval(call.Args[0], input))
                {
                    if (input.Kind == JqValueKind.String && searchVal.Kind == JqValueKind.String)
                    {
                        string s = input.GetString(), sub = searchVal.GetString(); var idxlist = new List<JqValue>(); int pos = 0;
                        while (pos <= s.Length - sub.Length) { int idx = s.IndexOf(sub, pos, StringComparison.Ordinal); if (idx < 0) break; idxlist.Add(JqValue.FromNumber(idx)); pos = idx + 1; }
                        if (call.Name == "indices") yield return JqValue.FromArray(idxlist);
                        else if (call.Name == "index") yield return idxlist.Count > 0 ? idxlist[0] : JqValue.Null;
                        else yield return idxlist.Count > 0 ? idxlist[idxlist.Count - 1] : JqValue.Null;
                    }
                    else if (input.Kind == JqValueKind.Array)
                    {
                        var arr = input.GetArray(); var idxlist = new List<JqValue>();
                        if (searchVal.Kind == JqValueKind.Array) { var sub = searchVal.GetArray(); for (int i = 0; i <= arr.Count - sub.Count; i++) { bool match = true; for (int j = 0; j < sub.Count; j++) { if (!arr[i + j].Equals(sub[j])) { match = false; break; } } if (match) idxlist.Add(JqValue.FromNumber(i)); } }
                        else { for (int i = 0; i < arr.Count; i++) if (arr[i].Equals(searchVal)) idxlist.Add(JqValue.FromNumber(i)); }
                        if (call.Name == "indices") yield return JqValue.FromArray(idxlist);
                        else if (call.Name == "index") yield return idxlist.Count > 0 ? idxlist[0] : JqValue.Null;
                        else yield return idxlist.Count > 0 ? idxlist[idxlist.Count - 1] : JqValue.Null;
                    }
                }
                break;

            case "min": case "max":
                if (input.Kind != JqValueKind.Array) throw new JqException("cannot get " + call.Name + " of " + input.TypeName);
                { var arr = input.GetArray(); if (arr.Count == 0) { yield return JqValue.Null; break; } var best = arr[0]; for (int i = 1; i < arr.Count; i++) { int c = JqValue.Compare(arr[i], best); if (call.Name == "min" ? c < 0 : c > 0) best = arr[i]; } yield return best; }
                break;

            case "min_by": case "max_by":
                if (call.Args.Count < 1) throw new JqException(call.Name + " requires 1 argument");
                if (input.Kind != JqValueKind.Array) throw new JqException("cannot get " + call.Name + " of " + input.TypeName);
                {
                    var arr = input.GetArray(); if (arr.Count == 0) { yield return JqValue.Null; break; }
                    var bestVal = arr[0]; JqValue bestKey = JqValue.Null; foreach (var v in Eval(call.Args[0], arr[0])) { bestKey = v; break; }
                    for (int i = 1; i < arr.Count; i++) { JqValue key = JqValue.Null; foreach (var v in Eval(call.Args[0], arr[i])) { key = v; break; } int c = JqValue.Compare(key, bestKey); if (call.Name == "min_by" ? c < 0 : c > 0) { bestKey = key; bestVal = arr[i]; } }
                    yield return bestVal;
                }
                break;

            case "explode": if (input.Kind != JqValueKind.String) throw new JqException(input.TypeName + " cannot be exploded"); { string s = input.GetString(); var result = new List<JqValue>(s.Length); for (int i = 0; i < s.Length; i++) result.Add(JqValue.FromNumber(s[i])); yield return JqValue.FromArray(result); } break;
            case "implode": if (input.Kind != JqValueKind.Array) throw new JqException(input.TypeName + " cannot be imploded"); { var arr = input.GetArray(); var sb = new StringBuilder(arr.Count); for (int i = 0; i < arr.Count; i++) sb.Append((char)(int)arr[i].GetNumber()); yield return JqValue.FromString(sb.ToString()); } break;
            case "tojson": yield return JqValue.FromString(input.ToJsonString(new JqOptions { CompactOutput = true })); break;
            case "fromjson":
                if (input.Kind != JqValueKind.String) throw new JqException(input.TypeName + " cannot be parsed as JSON");
                { JqValue parsed; try { using var doc = JsonDocument.Parse(input.GetString()); parsed = JqValue.FromElement(doc.RootElement.Clone()); } catch (JsonException ex) { throw new JqException("invalid JSON: " + ex.Message); } yield return parsed; }
                break;

            case "recurse":
                if (call.Args.Count == 0) foreach (var v in Recurse(input)) yield return v;
                else foreach (var v in RecurseWith(input, call.Args[0])) yield return v;
                break;

            case "walk":
                if (call.Args.Count < 1) throw new JqException("walk requires 1 argument");
                yield return Walk(input, call.Args[0]); break;

            case "numbers": if (input.Kind == JqValueKind.Number) yield return input; break;
            case "strings": if (input.Kind == JqValueKind.String) yield return input; break;
            case "booleans": if (input.Kind == JqValueKind.Boolean) yield return input; break;
            case "nulls": if (input.Kind == JqValueKind.Null) yield return input; break;
            case "arrays": if (input.Kind == JqValueKind.Array) yield return input; break;
            case "objects": if (input.Kind == JqValueKind.Object) yield return input; break;
            case "iterables": if (input.Kind == JqValueKind.Array || input.Kind == JqValueKind.Object) yield return input; break;
            case "scalars": if (input.Kind != JqValueKind.Array && input.Kind != JqValueKind.Object) yield return input; break;
            case "not": yield return JqValue.FromBool(!input.IsTruthy); break;
            case "abs": if (input.Kind == JqValueKind.Number) yield return JqValue.FromNumber(Math.Abs(input.GetNumber())); else throw new JqException(input.TypeName + " is not a number"); break;
            case "floor": if (input.Kind == JqValueKind.Number) yield return JqValue.FromNumber(Math.Floor(input.GetNumber())); else throw new JqException(input.TypeName + " is not a number"); break;
            case "ceil": if (input.Kind == JqValueKind.Number) yield return JqValue.FromNumber(Math.Ceiling(input.GetNumber())); else throw new JqException(input.TypeName + " is not a number"); break;
            case "round": if (input.Kind == JqValueKind.Number) yield return JqValue.FromNumber(Math.Round(input.GetNumber(), MidpointRounding.AwayFromZero)); else throw new JqException(input.TypeName + " is not a number"); break;
            case "sqrt": if (input.Kind == JqValueKind.Number) yield return JqValue.FromNumber(Math.Sqrt(input.GetNumber())); else throw new JqException(input.TypeName + " is not a number"); break;
            case "log": if (input.Kind == JqValueKind.Number) yield return JqValue.FromNumber(Math.Log(input.GetNumber())); else throw new JqException(input.TypeName + " is not a number"); break;
            case "log2": if (input.Kind == JqValueKind.Number) yield return JqValue.FromNumber(Math.Log2(input.GetNumber())); else throw new JqException(input.TypeName + " is not a number"); break;
            case "log10": if (input.Kind == JqValueKind.Number) yield return JqValue.FromNumber(Math.Log10(input.GetNumber())); else throw new JqException(input.TypeName + " is not a number"); break;
            case "exp": if (input.Kind == JqValueKind.Number) yield return JqValue.FromNumber(Math.Exp(input.GetNumber())); else throw new JqException(input.TypeName + " is not a number"); break;
            case "fabs": if (input.Kind == JqValueKind.Number) yield return JqValue.FromNumber(Math.Abs(input.GetNumber())); else throw new JqException(input.TypeName + " is not a number"); break;
            case "pow": if (call.Args.Count < 2) throw new JqException("pow requires 2 arguments"); foreach (var bv in Eval(call.Args[0], input)) foreach (var ev in Eval(call.Args[1], input)) yield return JqValue.FromNumber(Math.Pow(bv.GetNumber(), ev.GetNumber())); break;
            case "builtins": { var names = new[] { "length", "keys", "values", "has", "in", "map", "select", "empty", "error", "add", "any", "all", "flatten", "range", "type", "tostring", "tonumber", "ascii_downcase", "ascii_upcase", "sort", "sort_by", "group_by", "unique", "unique_by", "reverse", "contains", "inside", "to_entries", "from_entries", "with_entries", "paths", "getpath", "setpath", "delpaths", "del", "test", "match", "capture", "split", "join", "gsub", "sub", "ltrimstr", "rtrimstr", "startswith", "endswith", "min", "max", "min_by", "max_by", "explode", "implode", "tojson", "fromjson", "recurse", "walk", "env", "not", "abs", "floor", "ceil", "round", "sqrt", "log", "indices", "index", "rindex", "limit", "first", "last", "nth", "input", "inputs", "debug", "stderr", "map_values" }; var result = new List<JqValue>(names.Length); for (int i = 0; i < names.Length; i++) result.Add(JqValue.FromString(names[i])); yield return JqValue.FromArray(result); } break;
            case "halt": Environment.Exit(0); break;
            case "halt_error": { int code = 5; if (call.Args.Count > 0) foreach (var v in Eval(call.Args[0], input)) { code = (int)v.GetNumber(); break; } if (input.Kind == JqValueKind.String) Console.Error.Write(input.GetString()); else Console.Error.Write(input.ToJsonString(new JqOptions())); Environment.Exit(code); } break;

            case "ascii":
                if (input.Kind == JqValueKind.Number) yield return JqValue.FromString(((char)(int)input.GetNumber()).ToString());
                else yield return input;
                break;

            case "scan":
                if (call.Args.Count < 1) throw new JqException("scan requires 1 argument");
                foreach (var patternVal in Eval(call.Args[0], input))
                {
                    string pattern = patternVal.GetString(), flags = "";
                    if (call.Args.Count >= 2) foreach (var f in Eval(call.Args[1], input)) { flags = f.GetString(); break; }
                    var regex = MakeRegex(pattern, flags); var matches = regex.Matches(input.GetString());
                    for (int mi = 0; mi < matches.Count; mi++) { var m = matches[mi]; if (m.Groups.Count > 1) { var captures = new List<JqValue>(m.Groups.Count - 1); for (int gi = 1; gi < m.Groups.Count; gi++) captures.Add(JqValue.FromString(m.Groups[gi].Value)); yield return JqValue.FromArray(captures); } else yield return JqValue.FromArray(new List<JqValue> { JqValue.FromString(m.Value) }); }
                }
                break;

            case "splits":
                if (call.Args.Count < 1) throw new JqException("splits requires 1 argument");
                foreach (var patternVal in Eval(call.Args[0], input))
                {
                    string pattern = patternVal.GetString(), flags = "";
                    if (call.Args.Count >= 2) foreach (var f in Eval(call.Args[1], input)) { flags = f.GetString(); break; }
                    var parts = MakeRegex(pattern, flags).Split(input.GetString());
                    for (int i = 0; i < parts.Length; i++) yield return JqValue.FromString(parts[i]);
                }
                break;

            case "limit2":
                break;

            default: throw new JqException(call.Name + "/0 is not defined");
        }
    }

    private IEnumerable<JqValue> CallUserFunc(JqFuncDef funcDef, List<JqNode> args, JqValue input)
    {
        var savedFuncs = new Dictionary<string, JqFuncDef>(_functions);
        var savedVars = new Dictionary<string, JqValue>(_variables);
        try
        {
            foreach (var kv in funcDef.ClosureFuncs) _functions[kv.Key] = kv.Value;
            foreach (var kv in funcDef.ClosureVars) _variables[kv.Key] = kv.Value;
            for (int i = 0; i < funcDef.Params.Count; i++)
            {
                // Try to eagerly evaluate the argument in the caller's scope
                // This is critical for recursive functions: fact(n-1) needs
                // to evaluate n-1 using the current n value, not lazily
                var argNode = args[i];
                List<JqValue> argValues = new();
                // Save current state, restore caller state for arg evaluation
                var tempFuncs = new Dictionary<string, JqFuncDef>(_functions);
                var tempVars = new Dictionary<string, JqValue>(_variables);
                _functions.Clear(); foreach (var kv in savedFuncs) _functions[kv.Key] = kv.Value;
                _variables.Clear(); foreach (var kv in savedVars) _variables[kv.Key] = kv.Value;
                try { foreach (var v in Eval(argNode, input)) argValues.Add(v); } catch { }
                _functions.Clear(); foreach (var kv in tempFuncs) _functions[kv.Key] = kv.Value;
                _variables.Clear(); foreach (var kv in tempVars) _variables[kv.Key] = kv.Value;

                if (argValues.Count == 1)
                {
                    // Bind as a literal value
                    _functions[funcDef.Params[i]] = new JqFuncDef(new List<string>(), new JqLiteralNode(argValues[0]), new Dictionary<string, JqFuncDef>(), new Dictionary<string, JqValue>());
                }
                else
                {
                    // Bind as a filter (for multi-output or no-output args)
                    _functions[funcDef.Params[i]] = new JqFuncDef(new List<string>(), argNode, new Dictionary<string, JqFuncDef>(savedFuncs), new Dictionary<string, JqValue>(savedVars));
                }
            }
            foreach (var v in Eval(funcDef.Body, input)) yield return v;
        }
        finally { _functions.Clear(); foreach (var kv in savedFuncs) _functions[kv.Key] = kv.Value; _variables.Clear(); foreach (var kv in savedVars) _variables[kv.Key] = kv.Value; }
    }

    private IEnumerable<JqValue> RecurseWith(JqValue input, JqNode filter)
    {
        yield return input;
        List<JqValue> next;
        try { next = new List<JqValue>(); foreach (var v in Eval(filter, input)) next.Add(v); }
        catch (EmptyException) { yield break; }
        catch (JqException) { yield break; }
        for (int i = 0; i < next.Count; i++) foreach (var v in RecurseWith(next[i], filter)) yield return v;
    }

    private JqValue Walk(JqValue input, JqNode filter)
    {
        if (input.Kind == JqValueKind.Array) { var arr = input.GetArray(); var newArr = new List<JqValue>(arr.Count); for (int i = 0; i < arr.Count; i++) newArr.Add(Walk(arr[i], filter)); var arrayVal = JqValue.FromArray(newArr); foreach (var v in Eval(filter, arrayVal)) return v; return arrayVal; }
        if (input.Kind == JqValueKind.Object) { var obj = input.GetObject(); var newObj = new List<KeyValuePair<string, JqValue>>(obj.Count); for (int i = 0; i < obj.Count; i++) newObj.Add(new KeyValuePair<string, JqValue>(obj[i].Key, Walk(obj[i].Value, filter))); var objVal = JqValue.FromObject(newObj); foreach (var v in Eval(filter, objVal)) return v; return objVal; }
        foreach (var v in Eval(filter, input)) return v;
        return input;
    }

    private static JqValue FlattenArray(JqValue input, int depth)
    {
        if (input.Kind != JqValueKind.Array) return input;
        var result = new List<JqValue>();
        FlattenInto(input.GetArray(), result, depth);
        return JqValue.FromArray(result);
    }

    private static void FlattenInto(List<JqValue> arr, List<JqValue> result, int depth)
    {
        for (int i = 0; i < arr.Count; i++) { if (arr[i].Kind == JqValueKind.Array && depth > 0) FlattenInto(arr[i].GetArray(), result, depth - 1); else result.Add(arr[i]); }
    }

    private IEnumerable<JqValue> GetAllPaths(JqValue input, List<JqValue> prefix)
    {
        if (input.Kind == JqValueKind.Array) { var arr = input.GetArray(); for (int i = 0; i < arr.Count; i++) { var np = new List<JqValue>(prefix) { JqValue.FromNumber(i) }; yield return JqValue.FromArray(np); foreach (var p in GetAllPaths(arr[i], np)) yield return p; } }
        else if (input.Kind == JqValueKind.Object) { var obj = input.GetObject(); for (int i = 0; i < obj.Count; i++) { var np = new List<JqValue>(prefix) { JqValue.FromString(obj[i].Key) }; yield return JqValue.FromArray(np); foreach (var p in GetAllPaths(obj[i].Value, np)) yield return p; } }
    }

    private IEnumerable<JqValue> GetAllPathsFiltered(JqValue input, JqNode filter, List<JqValue> prefix)
    {
        bool matches = false; try { foreach (var v in Eval(filter, input)) { if (v.IsTruthy) { matches = true; break; } } } catch { }
        if (matches) yield return JqValue.FromArray(new List<JqValue>(prefix));
        if (input.Kind == JqValueKind.Array) { var arr = input.GetArray(); for (int i = 0; i < arr.Count; i++) { var np = new List<JqValue>(prefix) { JqValue.FromNumber(i) }; foreach (var p in GetAllPathsFiltered(arr[i], filter, np)) yield return p; } }
        else if (input.Kind == JqValueKind.Object) { var obj = input.GetObject(); for (int i = 0; i < obj.Count; i++) { var np = new List<JqValue>(prefix) { JqValue.FromString(obj[i].Key) }; foreach (var p in GetAllPathsFiltered(obj[i].Value, filter, np)) yield return p; } }
    }

    private IEnumerable<JqValue> GetLeafPaths(JqValue input, List<JqValue> prefix)
    {
        if (input.Kind == JqValueKind.Array) { var arr = input.GetArray(); if (arr.Count == 0) { yield return JqValue.FromArray(new List<JqValue>(prefix)); yield break; } for (int i = 0; i < arr.Count; i++) foreach (var p in GetLeafPaths(arr[i], new List<JqValue>(prefix) { JqValue.FromNumber(i) })) yield return p; }
        else if (input.Kind == JqValueKind.Object) { var obj = input.GetObject(); if (obj.Count == 0) { yield return JqValue.FromArray(new List<JqValue>(prefix)); yield break; } for (int i = 0; i < obj.Count; i++) foreach (var p in GetLeafPaths(obj[i].Value, new List<JqValue>(prefix) { JqValue.FromString(obj[i].Key) })) yield return p; }
        else yield return JqValue.FromArray(new List<JqValue>(prefix));
    }

    private static JqValue DeleteAtPath(JqValue root, List<object> path)
    {
        if (path.Count == 0) return JqValue.Null;
        if (path.Count == 1)
        {
            var seg = path[0];
            if (seg is string key && root.Kind == JqValueKind.Object) { var obj = root.GetObject(); var newObj = new List<KeyValuePair<string, JqValue>>(obj.Count); for (int i = 0; i < obj.Count; i++) if (obj[i].Key != key) newObj.Add(obj[i]); return JqValue.FromObject(newObj); }
            if (seg is int idx && root.Kind == JqValueKind.Array) { var arr = root.GetArray(); int realIdx = idx < 0 ? idx + arr.Count : idx; var newArr = new List<JqValue>(arr.Count); for (int i = 0; i < arr.Count; i++) if (i != realIdx) newArr.Add(arr[i]); return JqValue.FromArray(newArr); }
            return root;
        }
        var firstSeg = path[0]; var subPath = path.GetRange(1, path.Count - 1);
        if (firstSeg is string fkey && root.Kind == JqValueKind.Object) { var obj = root.GetObject(); var newObj = new List<KeyValuePair<string, JqValue>>(obj.Count); for (int i = 0; i < obj.Count; i++) { if (obj[i].Key == fkey) newObj.Add(new KeyValuePair<string, JqValue>(fkey, DeleteAtPath(obj[i].Value, subPath))); else newObj.Add(obj[i]); } return JqValue.FromObject(newObj); }
        if (firstSeg is int fidx && root.Kind == JqValueKind.Array) { var arr = root.GetArray(); int realIdx = fidx < 0 ? fidx + arr.Count : fidx; var newArr = new List<JqValue>(arr.Count); for (int i = 0; i < arr.Count; i++) { if (i == realIdx) newArr.Add(DeleteAtPath(arr[i], subPath)); else newArr.Add(arr[i]); } return JqValue.FromArray(newArr); }
        return root;
    }

    private static JqValue MatchToValue(Match m)
    {
        var captures = new List<JqValue>();
        for (int gi = 1; gi < m.Groups.Count; gi++)
        {
            var g = m.Groups[gi];
            captures.Add(JqValue.FromObject(new List<KeyValuePair<string, JqValue>> { new("offset", JqValue.FromNumber(g.Index)), new("length", JqValue.FromNumber(g.Length)), new("string", JqValue.FromString(g.Value)), new("name", JqValue.Null) }));
        }
        return JqValue.FromObject(new List<KeyValuePair<string, JqValue>> { new("offset", JqValue.FromNumber(m.Index)), new("length", JqValue.FromNumber(m.Length)), new("string", JqValue.FromString(m.Value)), new("captures", JqValue.FromArray(captures)) });
    }

    private static Regex MakeRegex(string pattern, string flags)
    {
        var options = RegexOptions.None;
        for (int i = 0; i < flags.Length; i++) { switch (flags[i]) { case 'x': options |= RegexOptions.IgnorePatternWhitespace; break; case 'i': options |= RegexOptions.IgnoreCase; break; case 's': options |= RegexOptions.Singleline; break; case 'm': options |= RegexOptions.Multiline; break; case 'g': break; } }
        return new Regex(pattern, options | RegexOptions.Compiled);
    }

    private static string ApplyFormat(string formatName, JqValue input)
    {
        switch (formatName)
        {
            case "text": return input.ToJqString();
            case "json": return input.ToJsonString(new JqOptions());
            case "base64": return Convert.ToBase64String(Encoding.UTF8.GetBytes(input.ToJqString()));
            case "base64d": return Encoding.UTF8.GetString(Convert.FromBase64String(input.GetString()));
            case "uri": return Uri.EscapeDataString(input.ToJqString());
            case "csv":
                if (input.Kind != JqValueKind.Array) throw new JqException("@csv requires array input");
                { var arr = input.GetArray(); var sb = new StringBuilder(); for (int i = 0; i < arr.Count; i++) { if (i > 0) sb.Append(','); if (arr[i].Kind == JqValueKind.String) { sb.Append('"'); string s = arr[i].GetString(); for (int j = 0; j < s.Length; j++) { if (s[j] == '"') sb.Append("\"\""); else sb.Append(s[j]); } sb.Append('"'); } else if (arr[i].Kind != JqValueKind.Null) sb.Append(arr[i].ToJqString()); } return sb.ToString(); }
            case "tsv":
                if (input.Kind != JqValueKind.Array) throw new JqException("@tsv requires array input");
                { var arr = input.GetArray(); var sb = new StringBuilder(); for (int i = 0; i < arr.Count; i++) { if (i > 0) sb.Append('\t'); if (arr[i].Kind == JqValueKind.String) sb.Append(arr[i].GetString().Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\n", "\\n").Replace("\r", "\\r")); else if (arr[i].Kind != JqValueKind.Null) sb.Append(arr[i].ToJqString()); } return sb.ToString(); }
            case "html":
                { string s = input.ToJqString(); var sb = new StringBuilder(s.Length); for (int i = 0; i < s.Length; i++) { switch (s[i]) { case '<': sb.Append("&lt;"); break; case '>': sb.Append("&gt;"); break; case '&': sb.Append("&amp;"); break; case '\'': sb.Append("&#39;"); break; case '"': sb.Append("&quot;"); break; default: sb.Append(s[i]); break; } } return sb.ToString(); }
            default: throw new JqException("unknown format: @" + formatName);
        }
    }
}

#endregion
