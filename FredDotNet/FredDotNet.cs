using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Text;

namespace FredDotNet;

#region Core Types

/// <summary>
/// Types of sed commands supported by the engine.
/// </summary>
public enum CommandType
{
    /// <summary>Substitute: s/pattern/replacement/flags</summary>
    Substitute,
    /// <summary>Delete pattern space, start next cycle: d</summary>
    Delete,
    /// <summary>Print pattern space: p</summary>
    Print,
    /// <summary>Append text after current line: a\text</summary>
    Append,
    /// <summary>Insert text before current line: i\text</summary>
    Insert,
    /// <summary>Change/replace line with text: c\text</summary>
    Change,
    /// <summary>Print and read next input line: n</summary>
    Next,
    /// <summary>Append next input line to pattern space: N</summary>
    NextAppend,
    /// <summary>Print first line of pattern space: P</summary>
    PrintFirstLine,
    /// <summary>Delete first line of pattern space, restart: D</summary>
    DeleteFirstLine,
    /// <summary>Copy pattern space to hold space: h</summary>
    HoldCopy,
    /// <summary>Append pattern space to hold space: H</summary>
    HoldAppend,
    /// <summary>Copy hold space to pattern space: g</summary>
    GetHold,
    /// <summary>Append hold space to pattern space: G</summary>
    GetHoldAppend,
    /// <summary>Exchange pattern and hold spaces: x</summary>
    Exchange,
    /// <summary>Branch to label: b [label]</summary>
    Branch,
    /// <summary>Branch if substitution succeeded: t [label]</summary>
    Test,
    /// <summary>Branch if substitution did NOT succeed (GNU): T [label]</summary>
    TestNot,
    /// <summary>Define a label: :label</summary>
    Label,
    /// <summary>Quit, printing current pattern space: q</summary>
    Quit,
    /// <summary>Print line number: =</summary>
    LineNumber,
    /// <summary>List pattern space unambiguously: l</summary>
    List,
    /// <summary>Transliterate characters: y/src/dst/</summary>
    Transliterate,
    /// <summary>Read file contents after current line: r filename</summary>
    ReadFile,
    /// <summary>Write pattern space to file: w filename</summary>
    WriteFile,
    /// <summary>Read one line from file per match (GNU): R filename</summary>
    ReadOneLine,
    /// <summary>Write first line of pattern space to file (GNU): W filename</summary>
    WriteFirstLine,
    /// <summary>Quit without printing (GNU): Q</summary>
    QuitNoprint,
    /// <summary>Block grouping of commands: { commands }</summary>
    Block
}

/// <summary>
/// Types of addresses that can be used with sed commands.
/// </summary>
public enum AddressType
{
    /// <summary>No address - matches all lines.</summary>
    None,
    /// <summary>Specific line number.</summary>
    LineNumber,
    /// <summary>Lines matching a regex pattern.</summary>
    Pattern,
    /// <summary>Range of lines (start,end).</summary>
    Range,
    /// <summary>Step address (first~step) - every Nth line.</summary>
    Step,
    /// <summary>Last line of input ($).</summary>
    LastLine
}

/// <summary>
/// Represents an address specification for a sed command.
/// Caches parsed integers and compiled Regex objects at construction time
/// so the hot path (AddressMatches) has zero allocation and O(1) checks.
/// </summary>
public class SedAddress
{
    public AddressType Type { get; }
    public string? Value1 { get; }
    public string? Value2 { get; }
    public bool Negated { get; }

    // Cached derived values -- computed once at construction, read-only thereafter.
    /// <summary>Parsed integer for LineNumber addresses (avoids int.Parse on hot path).</summary>
    internal readonly int CachedLineNumber;
    /// <summary>Cached start integer for Step addresses.</summary>
    internal readonly int CachedStepStart;
    /// <summary>Cached step integer for Step addresses.</summary>
    internal readonly int CachedStepValue;
    /// <summary>Compiled Regex for Pattern addresses (avoids repeated compilation).</summary>
    internal readonly Regex? CompiledPattern;

    public SedAddress(AddressType type, string? value1 = null, string? value2 = null, bool negated = false)
    {
        Type = type;
        Value1 = value1;
        Value2 = value2;
        Negated = negated;

        // Pre-compute derived values for hot-path use
        if (type == AddressType.LineNumber && value1 != null)
            CachedLineNumber = int.Parse(value1);

        if (type == AddressType.Step && value1 != null && value2 != null)
        {
            CachedStepStart = int.Parse(value1);
            CachedStepValue = int.Parse(value2);
        }

        if (type == AddressType.Pattern && value1 != null)
            CompiledPattern = new Regex(value1, RegexOptions.Compiled);
    }

    public static SedAddress All() => new(AddressType.None);
    public static SedAddress LineNumber(int lineNumber) => new(AddressType.LineNumber, lineNumber.ToString());
    public static SedAddress Pattern(string pattern) => new(AddressType.Pattern, pattern);
    public static SedAddress Range(string start, string end) => new(AddressType.Range, start, end);
    public static SedAddress Step(int start, int step) => new(AddressType.Step, start.ToString(), step.ToString());
    public static SedAddress LastLine() => new(AddressType.LastLine);

    /// <summary>
    /// Create a negated copy of this address (toggles the Negated flag).
    /// </summary>
    public SedAddress Negate() => new(Type, Value1, Value2, !Negated);
}

/// <summary>
/// Exception type for sed parse/runtime errors.
/// </summary>
public sealed class SedException : Exception
{
    public SedException(string message) : base(message) { }
}

/// <summary>
/// Exception type for grep parse/runtime errors.
/// </summary>
public sealed class GrepException : Exception
{
    public GrepException(string message) : base(message) { }
}

/// <summary>
/// Represents a single sed command with its address and parameters.
/// </summary>
public class SedCommand
{
    public SedAddress Address { get; }
    public CommandType Type { get; }
    public string? Pattern { get; }
    public string? Replacement { get; }
    public string? Flags { get; }
    public string? Label { get; }
    public string? Text { get; }

    /// <summary>
    /// Filename for 'r' (read file) and 'w' (write file) commands.
    /// </summary>
    public string? Filename { get; }

    // Unique per-instance ID for range state tracking (blocks have no stable index).
    private static int _nextId = 0;
    /// <summary>
    /// Unique monotonically-increasing identifier for range state keying.
    /// Only assigned for Range-addressed commands; all others remain 0.
    /// </summary>
    public readonly int Id;

    /// <summary>
    /// For Block commands: the list of commands inside the block.
    /// Returns null for all non-block commands.
    /// </summary>
    public virtual IReadOnlyList<SedCommand>? Block => null;

    public SedCommand(SedAddress address, CommandType type, string? pattern = null,
        string? replacement = null, string? flags = null, string? label = null,
        string? text = null, string? filename = null)
    {
        Address = address;
        Type = type;
        Pattern = pattern;
        Replacement = replacement;
        Flags = flags;
        Label = label;
        Text = text;
        Filename = filename;
        // Only Range-addressed commands need a unique ID for _rangeStartLine keying.
        if (address.Type == AddressType.Range)
            Id = System.Threading.Interlocked.Increment(ref _nextId);
    }

    // --- Factory methods for each command type ---

    /// <summary>Creates a substitute (s) command.</summary>
    public static SedCommand Substitute(SedAddress address, string pattern, string replacement, string? flags = null)
        => new(address, CommandType.Substitute, pattern, replacement, flags);

    /// <summary>Creates a delete (d) command that starts the next cycle.</summary>
    public static SedCommand Delete(SedAddress address) => new(address, CommandType.Delete);

    /// <summary>Creates a print (p) command that outputs the pattern space.</summary>
    public static SedCommand Print(SedAddress address) => new(address, CommandType.Print);
    /// <summary>Creates an append (a) command that outputs text after the current line.</summary>
    public static SedCommand Append(SedAddress address, string text) => new(address, CommandType.Append, text: text);
    /// <summary>Creates an insert (i) command that outputs text before the current line.</summary>
    public static SedCommand Insert(SedAddress address, string text) => new(address, CommandType.Insert, text: text);
    /// <summary>Creates a change (c) command that replaces matched lines with text and starts the next cycle.</summary>
    public static SedCommand Change(SedAddress address, string text) => new(address, CommandType.Change, text: text);
    public static SedCommand Branch(SedAddress address, string? label = null) => new(address, CommandType.Branch, label: label);
    public static SedCommand Test(SedAddress address, string? label = null) => new(address, CommandType.Test, label: label);
    public static SedCommand TestNot(SedAddress address, string? label = null) => new(address, CommandType.TestNot, label: label);
    public static SedCommand DefineLabel(string label) => new(SedAddress.All(), CommandType.Label, label: label);

    /// <summary>
    /// Create a simple command that takes no arguments beyond its address.
    /// Used for single-character commands like n, N, h, H, g, G, x, =, l, P, D.
    /// </summary>
    public static SedCommand SimpleCommand(SedAddress address, CommandType type) => new(address, type);

    /// <summary>
    /// Create a ReadFile (r) command that appends the contents of the named file after the current line.
    /// </summary>
    public static SedCommand ReadFile(SedAddress address, string filename) => new(address, CommandType.ReadFile, filename: filename);

    /// <summary>
    /// Create a WriteFile (w) command that writes the pattern space to the named file.
    /// </summary>
    public static SedCommand WriteFile(SedAddress address, string filename) => new(address, CommandType.WriteFile, filename: filename);

    /// <summary>
    /// Create a ReadOneLine (R) command that reads one line from the named file per match.
    /// </summary>
    public static SedCommand ReadOneLine(SedAddress address, string filename) => new(address, CommandType.ReadOneLine, filename: filename);

    /// <summary>
    /// Create a WriteFirstLine (W) command that writes the first line of the pattern space to the named file.
    /// </summary>
    public static SedCommand WriteFirstLine(SedAddress address, string filename) => new(address, CommandType.WriteFirstLine, filename: filename);

    /// <summary>
    /// Create a Quit (q) command that prints the pattern space (unless suppressed) then exits.
    /// </summary>
    public static SedCommand Quit(SedAddress address) => new(address, CommandType.Quit);

    /// <summary>
    /// Create a QuitNoprint (Q) command that exits immediately without printing the pattern space.
    /// </summary>
    public static SedCommand QuitNoprint(SedAddress address) => new(address, CommandType.QuitNoprint);

    /// <summary>
    /// Create a Transliterate (y) command, building lookup tables at construction time.
    /// The source and dest strings are the raw escape-encoded strings from the parser.
    /// GNU sed does NOT support ranges in y commands; this factory expands escape sequences
    /// only and validates equal length.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when source and dest have different lengths after expansion.</exception>
    public static SedCommand Transliterate(SedAddress address, string source, string dest)
    {
        var srcChars = ExpandTransliterateChars(source);
        var dstChars = ExpandTransliterateChars(dest);

        if (srcChars.Count != dstChars.Count)
            throw new ArgumentException(
                $"y command source and dest must be the same length after expansion " +
                $"(source={srcChars.Count}, dest={dstChars.Count}): y/{source}/{dest}/");

        // Build ASCII identity table (every char maps to itself by default)
        var asciiTable = new char[128];
        for (int i = 0; i < 128; i++)
            asciiTable[i] = (char)i;

        // Track which ASCII source chars have been mapped — cannot use asciiTable[s]==s
        // because that cannot distinguish "never mapped" from "mapped to identity (itself)".
        // Using a separate bool[] avoids misidentifying y/aa/aX/ as applying the X mapping.
        Span<bool> seen = stackalloc bool[128];

        Dictionary<char, char>? unicodeTable = null;

        for (int i = 0; i < srcChars.Count; i++)
        {
            char s = srcChars[i];
            char d = dstChars[i];
            if (s < 128)
            {
                // First mapping wins: only assign if this source char has never been mapped
                if (!seen[s])
                {
                    seen[s] = true;
                    asciiTable[s] = d;
                }
            }
            else
            {
                unicodeTable ??= new Dictionary<char, char>();
                if (!unicodeTable.ContainsKey(s))
                    unicodeTable[s] = d;
            }
        }

        return new TransliterateCommand(address, source, dest, asciiTable, unicodeTable);
    }

    /// <summary>
    /// Create a Block ({ }) command grouping zero or more commands under an optional address.
    /// </summary>
    /// <param name="address">Address that guards execution of the block (use SedAddress.All() for unconditional).</param>
    /// <param name="negated">When true, the block executes on lines that do NOT match the address.</param>
    /// <param name="block">The ordered list of commands inside the block.</param>
    public static SedCommand BlockCommand(SedAddress address, bool negated, IReadOnlyList<SedCommand> block)
    {
        var effectiveAddress = negated ? address.Negate() : address;
        return new BlockCommandImpl(effectiveAddress, block);
    }

    /// <summary>
    /// Expand a raw transliterate character list string, processing:
    ///   \n  -> newline
    ///   \t  -> tab
    ///   \\  -> backslash
    ///   \X  -> X  (delimiter escape - already stripped by parser, but handle literally)
    /// Note: GNU sed does NOT support character ranges in y commands.
    /// The '-' character is treated as a literal hyphen (e.g., y/a-c/X-Z/ maps a->X, -->-, c->Z).
    /// </summary>
    // Parse-time only -- called once per y command construction, not per line.
    // List<char> allocation here is acceptable; a char[] would require a second pass to size.
    internal static List<char> ExpandTransliterateChars(string raw)
    {
        var result = new List<char>(raw.Length);
        int i = 0;
        while (i < raw.Length)
        {
            char c = raw[i];
            if (c == '\\' && i + 1 < raw.Length)
            {
                char next = raw[i + 1];
                switch (next)
                {
                    case 'n': result.Add('\n'); break;
                    case 't': result.Add('\t'); break;
                    case '\\': result.Add('\\'); break;
                    default: result.Add(next); break;
                }
                i += 2;
            }
            else
            {
                result.Add(c);
                i++;
            }
        }
        return result;
    }
}

/// <summary>
/// Specialisation of SedCommand that carries pre-built transliterate lookup tables.
/// Inherits all base properties; adds the ASCII and Unicode tables used by ExecuteCommand.
/// </summary>
internal sealed class TransliterateCommand : SedCommand
{
    internal TransliterateCommand(SedAddress address, string source, string dest,
        char[] asciiTable, Dictionary<char, char>? unicodeTable)
        : base(address, CommandType.Transliterate, pattern: source, replacement: dest)
    {
        _asciiTable = asciiTable;
        _unicodeTable = unicodeTable;
    }

    internal readonly char[] _asciiTable;
    internal readonly Dictionary<char, char>? _unicodeTable;
}

/// <summary>
/// Specialisation of SedCommand for block ({ }) commands.
/// Carries the nested command list and a pre-computed local label index.
/// </summary>
internal sealed class BlockCommandImpl : SedCommand
{
    private readonly IReadOnlyList<SedCommand> _block;

    /// <summary>
    /// Pre-computed index from label name to command index within this block.
    /// Built once at parse time; used during ExecuteBlock for O(1) branch resolution.
    /// </summary>
    internal IReadOnlyDictionary<string, int> LocalLabels { get; }

    /// <summary>Initialises a block command.</summary>
    /// <param name="address">The address that guards execution of the block.</param>
    /// <param name="block">The ordered list of commands inside the block.</param>
    internal BlockCommandImpl(SedAddress address, IReadOnlyList<SedCommand> block)
        : base(address, CommandType.Block)
    {
        _block = block;
        var labels = new Dictionary<string, int>();
        for (int i = 0; i < block.Count; i++)
        {
            var cmd = block[i];
            if (cmd.Type == CommandType.Label && !string.IsNullOrEmpty(cmd.Label))
                labels[cmd.Label!] = i;
        }
        LocalLabels = labels;
    }

    public override IReadOnlyList<SedCommand> Block => _block;
}

/// <summary>
/// Actions that can result from command execution.
/// </summary>
public enum ExecutionAction
{
    Continue, NextCycle, RestartCycle, Branch, Quit, QuitNoprint
}

/// <summary>
/// Result of command execution, indicating what the engine should do next.
/// Implemented as a <c>readonly record struct</c> so that Continue/NextCycle/Quit/QuitNoprint
/// are returned as zero-allocation stack copies, and Branch creates a cheap struct value.
/// </summary>
public readonly record struct ExecutionResult
{
    /// <summary>The action the engine should take after a command executes.</summary>
    public ExecutionAction Action { get; init; }

    /// <summary>
    /// For <see cref="ExecutionAction.Branch"/> results, the target label name;
    /// <see langword="null"/> or empty string means branch to end of script cycle.
    /// </summary>
    public string? Label { get; init; }

    internal ExecutionResult(ExecutionAction action, string? label = null)
    {
        Action = action;
        Label = label;
    }

    private static readonly ExecutionResult s_continue = new(ExecutionAction.Continue);
    private static readonly ExecutionResult s_nextCycle = new(ExecutionAction.NextCycle);
    private static readonly ExecutionResult s_restartCycle = new(ExecutionAction.RestartCycle);
    private static readonly ExecutionResult s_quit = new(ExecutionAction.Quit);
    private static readonly ExecutionResult s_quitNoprint = new(ExecutionAction.QuitNoprint);

    /// <summary>Returns a result that continues execution with the next command.</summary>
    public static ExecutionResult Continue() => s_continue;

    /// <summary>Returns a result that ends the current command cycle and advances to the next input line.</summary>
    public static ExecutionResult NextCycle() => s_nextCycle;

    /// <summary>Returns a result that restarts the command cycle from the first command (used by D).</summary>
    public static ExecutionResult RestartCycle() => s_restartCycle;

    /// <summary>Returns a result that branches to the named label, or to end of cycle if <paramref name="label"/> is null or empty.</summary>
    public static ExecutionResult Branch(string? label) => new(ExecutionAction.Branch, label);

    /// <summary>Returns a result that quits processing after printing the current pattern space.</summary>
    public static ExecutionResult Quit() => s_quit;

    /// <summary>Returns a result that quits processing immediately without printing the current pattern space.</summary>
    public static ExecutionResult QuitNoprint() => s_quitNoprint;
}

#endregion

#region RegexTranslator - Shared BRE/ERE translation utilities

/// <summary>
/// Shared BRE/ERE regex translation utilities used by both the sed engine and the grep engine.
/// Provides pattern translation from POSIX BRE/ERE syntax to .NET regex syntax.
/// </summary>
internal static class RegexTranslator
{
    /// <summary>
    /// Characters that are LITERAL in BRE but SPECIAL in .NET regex.
    /// These must be escaped with backslash when emitting .NET regex output.
    /// In BRE, only the backslash-escaped forms of these are special:
    /// \( \) for grouping, \{ \} for intervals, \+ \? for quantifiers, \| for alternation.
    /// </summary>
    private const string BRELiteralChars = "(){}+?|";

    /// <summary>
    /// Translate a BRE (Basic Regular Expression) pattern to a .NET regex pattern
    /// using a single-pass character-by-character state machine tokenizer.
    ///
    /// BRE semantics differ from .NET regex in critical ways:
    /// - ( ) { } + ? | are LITERAL in BRE; only their escaped forms are special
    /// - \( \) are grouping operators (equivalent to unescaped parens in .NET)
    /// - \{ \} delimit interval expressions (equivalent to unescaped braces in .NET)
    /// - \+ \? are quantifiers (GNU extension, equivalent to unescaped +? in .NET)
    /// - \1-\9 are backreferences in patterns
    /// - \&lt; \&gt; are word boundaries (translated to \b)
    /// - \n \t are escape sequences for newline and tab
    /// - \w \W are word character classes (GNU extension, pass through)
    /// - [[:alpha:]] etc. are POSIX character classes inside bracket expressions
    /// </summary>
    /// <param name="pattern">The BRE pattern to translate.</param>
    /// <returns>The equivalent .NET regex pattern string.</returns>
    internal static string TranslateBREPattern(string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
            return pattern;

        var input = pattern.AsSpan();
        var output = new StringBuilder(pattern.Length + 16);
        int i = 0;

        while (i < input.Length)
        {
            char c = input[i];

            if (c == '\\')
            {
                i++;
                if (i >= input.Length)
                {
                    // Trailing backslash: emit escaped literal backslash for .NET regex
                    AppendRegexEscape(output, '\\');
                    break;
                }

                TranslateBREEscapeSequence(input, ref i, output);
            }
            else if (c == '[')
            {
                // Character class state: consume through the closing ]
                AppendCharacterClass(input, ref i, output);
                continue; // AppendCharacterClass advances i past the ]
            }
            else if (BRELiteralChars.IndexOf(c) >= 0)
            {
                // Characters that are literal in BRE but special in .NET regex:
                // escape them so .NET treats them as literal matches
                AppendRegexEscape(output, c);
            }
            else
            {
                // All other characters pass through unchanged:
                // . * ^ $ and regular text are the same in BRE and .NET regex
                output.Append(c);
            }

            i++;
        }

        return output.ToString();
    }

    /// <summary>
    /// Translate an ERE (Extended Regular Expression) pattern to a .NET regex pattern.
    /// In ERE, ( ) { } + ? | are special operators directly (no backslash needed).
    /// Backslash-escaping them makes them literal.
    /// </summary>
    /// <param name="pattern">The ERE pattern to translate.</param>
    /// <returns>The equivalent .NET regex pattern string.</returns>
    internal static string TranslateEREPattern(string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
            return pattern;

        var input = pattern.AsSpan();
        var output = new StringBuilder(pattern.Length + 16);
        int i = 0;

        while (i < input.Length)
        {
            char c = input[i];

            if (c == '\\')
            {
                i++;
                if (i >= input.Length)
                {
                    AppendRegexEscape(output, '\\');
                    break;
                }
                TranslateEREEscapeSequence(input, ref i, output);
            }
            else if (c == '[')
            {
                AppendCharacterClass(input, ref i, output);
                continue;
            }
            else
            {
                // In ERE, all special chars pass through directly to .NET regex.
                // ( ) { } + ? | ^ $ . * are all ERE operators and .NET operators.
                // [ is handled above. Nothing needs escaping.
                output.Append(c);
            }

            i++;
        }

        return output.ToString();
    }

    /// <summary>
    /// Translate an ERE escape sequence (the character after \) to .NET regex.
    /// In ERE, \( means literal '(', \) means literal ')', \{ means literal '{', etc.
    /// </summary>
    internal static void TranslateEREEscapeSequence(ReadOnlySpan<char> input, ref int pos, StringBuilder output)
    {
        char next = input[pos];

        switch (next)
        {
            // ERE: \( \) \{ \} \+ \? \| are literal characters
            case '(':
            case ')':
            case '{':
            case '}':
            case '+':
            case '?':
            case '|':
                AppendRegexEscape(output, next);
                break;

            // Word boundaries: \< and \> both translate to \b in .NET regex
            case '<':
            case '>':
                AppendRegexEscape(output, 'b');
                break;

            // Escape sequences: \n -> newline, \t -> tab
            case 'n':
                output.Append('\n');
                break;
            case 't':
                output.Append('\t');
                break;

            // GNU extensions: \w \W pass through as .NET \w \W
            case 'w':
            case 'W':
                AppendRegexEscape(output, next);
                break;

            // Backreferences: \1-\9 stay as \1-\9 in .NET regex patterns
            case '1': case '2': case '3': case '4': case '5':
            case '6': case '7': case '8': case '9':
                AppendRegexEscape(output, next);
                break;

            // Escaped literal backslash: \\ -> \\ in .NET regex
            case '\\':
                AppendRegexEscape(output, '\\');
                break;

            // Any other escaped character: pass through the escape.
            default:
                AppendRegexEscape(output, next);
                break;
        }
    }

    /// <summary>
    /// Translate a BRE escape sequence (the character after \) to .NET regex.
    /// Called when a backslash has been consumed and the next character is available.
    /// </summary>
    /// <param name="input">The full BRE pattern as a span.</param>
    /// <param name="pos">Position of the character after the backslash. May be advanced for intervals.</param>
    /// <param name="output">StringBuilder to append the .NET regex equivalent.</param>
    internal static void TranslateBREEscapeSequence(ReadOnlySpan<char> input, ref int pos, StringBuilder output)
    {
        char next = input[pos];

        switch (next)
        {
            // BRE grouping: \( \) become ( ) in .NET regex
            case '(':
            case ')':
                output.Append(next);
                break;

            // BRE interval: \{ starts an interval expression \{m,n\}
            case '{':
                pos++;
                AppendIntervalExpression(input, ref pos, output);
                break;

            // BRE quantifiers (GNU extension): \+ -> +, \? -> ?, \| -> |
            case '+':
            case '?':
            case '|':
                output.Append(next);
                break;

            // Word boundaries: \< and \> both translate to \b in .NET regex
            case '<':
            case '>':
                AppendRegexEscape(output, 'b');
                break;

            // Escape sequences: \n -> newline, \t -> tab
            case 'n':
                output.Append('\n');
                break;
            case 't':
                output.Append('\t');
                break;

            // GNU extensions: \w \W pass through as .NET \w \W
            case 'w':
            case 'W':
                AppendRegexEscape(output, next);
                break;

            // Backreferences: \1-\9 stay as \1-\9 in .NET regex patterns
            case '1': case '2': case '3': case '4': case '5':
            case '6': case '7': case '8': case '9':
                AppendRegexEscape(output, next);
                break;

            // Escaped literal backslash: \\ -> \\ in .NET regex (matches literal \)
            case '\\':
                AppendRegexEscape(output, '\\');
                break;

            // Any other escaped character: pass through the escape.
            // This handles \. \* \^ \$ \[ etc. which are already valid .NET regex escapes
            default:
                AppendRegexEscape(output, next);
                break;
        }
    }

    /// <summary>
    /// Append a backslash-escaped character to the output: outputs \ followed by the char.
    /// Used for emitting .NET regex escape sequences like \b, \w, \1, \\, etc.
    /// </summary>
    /// <param name="output">The StringBuilder to append to.</param>
    /// <param name="c">The character to escape.</param>
    internal static void AppendRegexEscape(StringBuilder output, char c)
    {
        output.Append('\\');
        output.Append(c);
    }

    /// <summary>
    /// Consume and translate a BRE character class expression starting at the [.
    /// Handles POSIX named classes like [:alpha:], negation with ^, and the special
    /// POSIX rule that ] as the first character (or after ^) is treated as a literal
    /// member of the class rather than the closing bracket.
    /// </summary>
    /// <param name="input">The full BRE pattern as a span.</param>
    /// <param name="pos">Current position at the opening [. Updated past the closing ].</param>
    /// <param name="output">StringBuilder to append the .NET regex equivalent.</param>
    internal static void AppendCharacterClass(ReadOnlySpan<char> input, ref int pos, StringBuilder output)
    {
        output.Append('[');
        pos++; // move past [

        // Handle negation: [^...]
        if (pos < input.Length && input[pos] == '^')
        {
            output.Append('^');
            pos++;
        }

        // Handle ] as first character in the class (literal ], not end of class)
        if (pos < input.Length && input[pos] == ']')
        {
            output.Append(']');
            pos++;
        }

        // Consume characters until the closing ]
        while (pos < input.Length && input[pos] != ']')
        {
            // Check for POSIX named class: [:name:]
            if (input[pos] == '[' && pos + 1 < input.Length && input[pos + 1] == ':')
            {
                int classStart = pos + 2;
                int classEnd = classStart;

                // Scan forward for the closing :]
                while (classEnd + 1 < input.Length)
                {
                    if (input[classEnd] == ':' && input[classEnd + 1] == ']')
                        break;
                    classEnd++;
                }

                if (classEnd + 1 < input.Length && input[classEnd] == ':' && input[classEnd + 1] == ']')
                {
                    var className = input.Slice(classStart, classEnd - classStart);
                    AppendPosixClass(className, output);
                    pos = classEnd + 2; // skip past :]
                    continue;
                }
            }

            // Regular character inside the class: pass through literally
            output.Append(input[pos]);
            pos++;
        }

        // If we reached end of input without finding ], the bracket is unclosed
        if (pos >= input.Length || input[pos] != ']')
        {
            throw new SedException("Unterminated bracket expression in pattern");
        }

        output.Append(']');
        pos++; // move past closing ]
    }

    /// <summary>
    /// Append the .NET regex equivalent of a POSIX named character class.
    /// These appear inside bracket expressions, for example [[:alpha:]] matches letters.
    /// The output is placed inside an existing [...] bracket, so it contributes character
    /// ranges to the enclosing character class.
    /// </summary>
    /// <param name="className">The POSIX class name (e.g., "alpha", "digit").</param>
    /// <param name="output">StringBuilder to append the translated character ranges.</param>
    internal static void AppendPosixClass(ReadOnlySpan<char> className, StringBuilder output)
    {
        // Each POSIX class maps to .NET regex character ranges inside [...]
        if (className.SequenceEqual("alpha".AsSpan()))
            output.Append("a-zA-Z");
        else if (className.SequenceEqual("digit".AsSpan()))
            output.Append("0-9");
        else if (className.SequenceEqual("alnum".AsSpan()))
            output.Append("a-zA-Z0-9");
        else if (className.SequenceEqual("space".AsSpan()))
            output.Append(" \\t\\n\\r\\f\\v");
        else if (className.SequenceEqual("upper".AsSpan()))
            output.Append("A-Z");
        else if (className.SequenceEqual("lower".AsSpan()))
            output.Append("a-z");
        else if (className.SequenceEqual("xdigit".AsSpan()))
            output.Append("0-9a-fA-F");
        else if (className.SequenceEqual("blank".AsSpan()))
            output.Append(" \\t");
        else if (className.SequenceEqual("punct".AsSpan()))
            output.Append("!-/:-@\\[-`{-~");
        else if (className.SequenceEqual("print".AsSpan()))
            output.Append(" -~");
        else if (className.SequenceEqual("graph".AsSpan()))
            output.Append("!-~");
        else if (className.SequenceEqual("cntrl".AsSpan()))
            output.Append("\\x00-\\x1f\\x7f");
        else
        {
            // Unknown POSIX class: emit as-is for diagnostic purposes
            output.Append("[:");
            for (int j = 0; j < className.Length; j++)
                output.Append(className[j]);
            output.Append(":]");
        }
    }

    /// <summary>
    /// Consume and translate a BRE interval expression.
    /// Called after \{ has been consumed. Reads content up to \} and outputs {content}
    /// for .NET regex. If the closing \} is not found, outputs literal \{ and resets
    /// the position to allow re-processing of the consumed characters.
    /// </summary>
    /// <param name="input">The full BRE pattern as a span.</param>
    /// <param name="pos">Current position after the { character. Updated past the closing \}.</param>
    /// <param name="output">StringBuilder to append the .NET regex equivalent.</param>
    internal static void AppendIntervalExpression(ReadOnlySpan<char> input, ref int pos, StringBuilder output)
    {
        var intervalContent = new StringBuilder(8);
        int start = pos;

        while (pos < input.Length)
        {
            if (input[pos] == '\\' && pos + 1 < input.Length && input[pos + 1] == '}')
            {
                // Found the closing \}: output {content} for .NET regex
                output.Append('{');
                output.Append(intervalContent);
                output.Append('}');
                pos++; // skip the }
                return;
            }

            intervalContent.Append(input[pos]);
            pos++;
        }

        // No matching \} found: treat the original \{ as literal characters
        AppendRegexEscape(output, '{');
        pos = start; // reset so the consumed chars are re-processed by the main loop
    }

    /// <summary>
    /// Translate a BRE replacement string to a .NET regex replacement string
    /// using a single-pass character-by-character approach.
    ///
    /// BRE replacement semantics:
    /// - &amp; references the entire match (translated to $0 for .NET)
    /// - \1-\9 are backreferences (translated to $1-$9 for .NET)
    /// - \n produces a literal newline character
    /// - \t produces a literal tab character
    /// - \\ produces a literal backslash
    /// - \&amp; produces a literal ampersand (suppresses &amp; special meaning)
    /// - All other characters pass through unchanged
    /// </summary>
    /// <param name="replacement">The BRE replacement string to translate.</param>
    /// <returns>The equivalent .NET regex replacement string.</returns>
    internal static string TranslateBREReplacementString(string replacement)
    {
        if (string.IsNullOrEmpty(replacement))
            return replacement;

        var input = replacement.AsSpan();
        var output = new StringBuilder(replacement.Length + 8);
        int i = 0;

        while (i < input.Length)
        {
            char c = input[i];

            if (c == '\\')
            {
                i++;
                if (i >= input.Length)
                {
                    output.Append('\\');
                    break;
                }

                char next = input[i];
                switch (next)
                {
                    case '1': case '2': case '3': case '4': case '5':
                    case '6': case '7': case '8': case '9':
                        output.Append('$');
                        output.Append(next);
                        break;
                    case '&':
                        output.Append('&');
                        break;
                    case 'n':
                        output.Append('\n');
                        break;
                    case 't':
                        output.Append('\t');
                        break;
                    case '\\':
                        output.Append('\\');
                        break;
                    default:
                        output.Append(next);
                        break;
                }
            }
            else if (c == '&')
            {
                output.Append('$');
                output.Append('0');
            }
            else
            {
                output.Append(c);
            }

            i++;
        }

        return output.ToString();
    }
}

#endregion

#region SedExecutionContext - Per-call mutable state

/// <summary>
/// Holds all mutable per-execution state for a single Transform() / Execute() call.
/// Created fresh for each call, ensuring thread safety when the same SedScript
/// is used concurrently from multiple threads.
/// </summary>
internal sealed class SedExecutionContext : IDisposable
{
    /// <summary>
    /// Per-command range activation state, keyed by command index.
    /// Toggle-based: activates when start address matches, deactivates when end address matches.
    /// Value: the line number where the range activated (0 = not active).
    /// </summary>
    internal readonly Dictionary<int, int> RangeStartLine = new();

    /// <summary>
    /// Reusable list for queuing append command text within a single ProcessLines cycle.
    /// </summary>
    internal readonly List<string> PendingAppends = new();

    /// <summary>
    /// Write file handles, keyed by filename. Opened/truncated before ProcessLines loop, closed in Dispose.
    /// </summary>
    internal readonly Dictionary<string, StreamWriter> WriteHandles = new();

    /// <summary>
    /// Read file handles for R (ReadOneLine) command, keyed by filename.
    /// Opened on first use, closed in Dispose.
    /// </summary>
    internal readonly Dictionary<string, StreamReader> ReadHandles = new();

    /// <summary>
    /// Pending r/R file content to be appended after the current line, parallel to PendingAppends.
    /// </summary>
    internal readonly List<string> PendingReads = new();

    /// <summary>
    /// Cache for r command file contents within a single Transform() call.
    /// Keyed by filename; null value means the file was not readable or was empty.
    /// </summary>
    internal readonly Dictionary<string, string?> ReadCache = new();

    /// <summary>
    /// Index of the last input line whose content was appended to the result buffer during ProcessLines.
    /// </summary>
    internal int LastPrintedLineIndex = -1;

    /// <summary>
    /// When true, the final output must end with a trailing newline (even if input had none).
    /// </summary>
    internal bool ForceTrailingNewlineOnFinal;

    internal bool PatternSpaceContaminatedByHoldOrN;

    /// <summary>
    /// True when GetHoldAppend (G) is the source of contamination.
    /// G-contamination is sticky: a substitution that removes embedded \n
    /// does NOT clear the trailing-newline obligation (GNU sed behaviour).
    /// </summary>
    internal bool PatternSpaceContaminatedByG;

    /// <summary>
    /// The line index on which hold space was last written (via h, H, or x).
    /// </summary>
    internal int HoldSpaceLineIndex = -1;

    /// <summary>
    /// The index of the line currently being processed in ProcessLines.
    /// </summary>
    internal int CurrentLineIndex;

    public void Dispose()
    {
        foreach (var kv in WriteHandles)
        {
            kv.Value.Flush();
            kv.Value.Dispose();
        }
        WriteHandles.Clear();
        foreach (var kv in ReadHandles)
            kv.Value.Dispose();
        ReadHandles.Clear();
    }
}

#endregion

#region SedScript - Core execution engine

/// <summary>
/// Represents a compiled sed script that can be executed multiple times.
/// Thread-safe: all per-execution mutable state is held in SedExecutionContext,
/// created fresh for each Transform()/Execute() call.
/// </summary>
public class SedScript
{
    private readonly List<SedCommand> _commands;
    private readonly Dictionary<string, int> _labels;
    private readonly bool _suppressDefaultOutput;
    private readonly bool _useEre;

    /// <summary>
    /// Static cache for compiled Regex objects used in range address components ("/pattern/").
    /// Shared across all SedScript instances; keyed by (raw pattern, isEre) to avoid cross-mode collisions.
    /// Thread-safe via ConcurrentDictionary -- no lock needed on reads.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, bool), Regex> _rangePatternCache = new();

    /// <summary>
    /// Cache for translated BRE patterns, keyed by original BRE pattern string.
    /// Populated at construction time; read-only during execution.
    /// </summary>
    private readonly Dictionary<string, string> _patternCache = new();

    /// <summary>
    /// Cache for translated BRE replacement strings, keyed by original replacement string.
    /// Populated at construction time; read-only during execution.
    /// </summary>
    private readonly Dictionary<string, string> _replacementCache = new();

    /// <summary>
    /// Cache for compiled Regex objects used in substitute execution, keyed by (pattern, options).
    /// Thread-safe via ConcurrentDictionary.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string, RegexOptions), Regex> _regexCache = new();

    /// <summary>
    /// Cache for compiled Regex objects used in Pattern-type address matching.
    /// Populated at construction time; read-only during execution.
    /// </summary>
    private readonly Dictionary<(string, bool), Regex> _addressPatternCache = new();

    public ReadOnlyCollection<SedCommand> Commands { get; }
    public bool SuppressDefaultOutput => _suppressDefaultOutput;

    public SedScript(IEnumerable<SedCommand> commands, bool suppressDefaultOutput = false, bool useEre = false)
    {
        _commands = new List<SedCommand>(commands);
        Commands = _commands.AsReadOnly();
        _suppressDefaultOutput = suppressDefaultOutput;
        _useEre = useEre;

        // Build label index for efficient branching (top-level only;
        // block-internal labels are resolved locally during block execution).
        _labels = new Dictionary<string, int>();
        for (int i = 0; i < _commands.Count; i++)
        {
            var command = _commands[i];
            if (command.Type == CommandType.Label && !string.IsNullOrEmpty(command.Label))
            {
                _labels[command.Label] = i;
            }
        }

        // Pre-translate all patterns and replacements at construction time
        // to avoid repeated tokenization during Transform calls.
        // Descend into Block commands recursively.
        PreWarmCaches(_commands);
    }

    /// <summary>
    /// Transform a single string using this sed script - main method for repeated use.
    /// Thread-safe: creates a fresh execution context per call.
    /// </summary>
    public string Transform(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        // Preserve original newline handling from working ned
        var hasTrailingNewline = input.EndsWith('\n');
        var lines = input.Split('\n');

        // Remove empty last line if input ended with newline
        if (hasTrailingNewline && lines.Length > 0 && string.IsNullOrEmpty(lines[^1]))
        {
            lines = lines[..^1];
        }

        using var ctx = new SedExecutionContext();
        var result = ProcessLines(ctx, lines);

        // Add trailing newline when:
        //   (a) the original input had a trailing newline, OR
        //   (b) the last-printed line was not the final input line
        //       (GNU sed always adds \n after any mid-input line that was output)
        // This mirrors GNU sed behaviour: a trailing \n appears unless both (1) the
        // input had no trailing \n AND (2) the last output came from the last input line.
        if (!string.IsNullOrEmpty(result) && (hasTrailingNewline || ctx.LastPrintedLineIndex != lines.Length - 1 || ctx.ForceTrailingNewlineOnFinal))
        {
            result += '\n';
        }

        return result;
    }

    /// <summary>
    /// Execute and return (output, exitCode) for API consistency with GrepScript and AwkScript.
    /// </summary>
    public (string Output, int ExitCode) Execute(string input)
    {
        string output = Transform(input);
        return (output, 0);
    }

    /// <summary>
    /// Streaming execution: reads from TextReader, writes to TextWriter.
    /// Returns exit code (always 0 for sed unless q with code).
    /// </summary>
    public int Execute(TextReader input, TextWriter output)
    {
        string result = Transform(input.ReadToEnd());
        output.Write(result);
        return 0;
    }

    /// <summary>
    /// Recursively pre-warm the BRE/ERE pattern/replacement caches for all Substitute commands
    /// and address Pattern-type addresses in the command list, descending into Block commands.
    /// </summary>
    private void PreWarmCaches(IReadOnlyList<SedCommand> commands)
    {
        for (int i = 0; i < commands.Count; i++)
        {
            var command = commands[i];

            // Pre-translate patterns on address components (Pattern type)
            PreWarmAddressPattern(command.Address);

            if (command.Type == CommandType.Substitute)
            {
                if (!string.IsNullOrEmpty(command.Pattern) && !_patternCache.ContainsKey(command.Pattern))
                    _patternCache[command.Pattern] = _useEre ? RegexTranslator.TranslateEREPattern(command.Pattern) : RegexTranslator.TranslateBREPattern(command.Pattern);
                if (command.Replacement != null && !_replacementCache.ContainsKey(command.Replacement))
                    _replacementCache[command.Replacement] = RegexTranslator.TranslateBREReplacementString(command.Replacement);
            }
            else if (command.Type == CommandType.Block && command.Block != null)
                PreWarmCaches(command.Block);
        }
    }

    /// <summary>
    /// Pre-warm _addressPatternCache for a single address; handles Range components too.
    /// </summary>
    private void PreWarmAddressPattern(SedAddress address)
    {
        if (address.Type == AddressType.Pattern && address.Value1 != null
            && !_addressPatternCache.ContainsKey((address.Value1, _useEre)))
        {
            string translated = _useEre ? RegexTranslator.TranslateEREPattern(address.Value1) : RegexTranslator.TranslateBREPattern(address.Value1);
            _addressPatternCache[(address.Value1, _useEre)] = new Regex(translated, RegexOptions.Compiled);
        }
        else if (address.Type == AddressType.Range)
        {
            // Value1 and Value2 may be "/pattern/" strings; pre-warm the static range cache
            if (address.Value1 != null)
                PreWarmRangeComponent(address.Value1);
            if (address.Value2 != null)
                PreWarmRangeComponent(address.Value2);
        }
    }

    /// <summary>
    /// Pre-warm the static _rangePatternCache for a range component that is a /pattern/ string.
    /// </summary>
    private void PreWarmRangeComponent(string component)
    {
        if (component.StartsWith('/') && component.EndsWith('/') && component.Length >= 2)
        {
            var raw = component.Substring(1, component.Length - 2);
            var key = (raw, _useEre);
            if (!_rangePatternCache.ContainsKey(key))
            {
                string translated = _useEre ? RegexTranslator.TranslateEREPattern(raw) : RegexTranslator.TranslateBREPattern(raw);
                _rangePatternCache[key] = new Regex(translated, RegexOptions.Compiled);
            }
        }
    }

    /// <summary>
    /// Handles inline commands that require direct access to the output buffer, line reader,
    /// and processing state -- the commands that cannot be dispatched through
    /// <see cref="ExecuteCommand"/> because they need to write to <paramref name="result"/>,
    /// read from <paramref name="lines"/>, or mutate <paramref name="lineIndex"/>.
    /// </summary>
    /// <returns>
    /// <list type="bullet">
    ///   <item><see cref="ExecutionResult.Continue()"/> -- command ran; advance to next command.</item>
    ///   <item><see cref="ExecutionResult.NextCycle()"/> -- start next input cycle (c, D-no-newline, d).</item>
    ///   <item><see cref="ExecutionResult.RestartCycle()"/> -- restart command list from top (D with newline).</item>
    ///   <item><see cref="ExecutionResult.QuitNoprint()"/> -- stop all processing (N/n at EOF).</item>
    ///   <item><see langword="null"/> -- command is not an inline command; caller must handle it.</item>
    /// </list>
    /// </returns>
    private ExecutionResult? ExecuteInlineCommand(
        SedExecutionContext ctx,
        SedCommand cmd,
        StringBuilder patternSpace,
        StringBuilder result,
        string[] lines,
        ref int lineIndex,
        ref int lineNumber,
        ref bool firstOutput,
        ref bool substitutionMadeThisCycle,
        ref string patternSpaceStr)
    {
        if (cmd.Type == CommandType.Insert)
        {
            string insertText = ExpandEscapes(cmd.Text ?? "");
            if (!firstOutput) result.Append('\n');
            result.Append(insertText);
            firstOutput = false;
            ctx.LastPrintedLineIndex = lineIndex;
            ctx.ForceTrailingNewlineOnFinal = insertText.Contains('\n');
            return ExecutionResult.Continue();
        }

        if (cmd.Type == CommandType.Append)
        {
            ctx.PendingAppends.Add(ExpandEscapes(cmd.Text ?? ""));
            return ExecutionResult.Continue();
        }

        if (cmd.Type == CommandType.Change)
        {
            bool emitNow = true;
            if (cmd.Address.Type == AddressType.Range)
            {
                ctx.RangeStartLine.TryGetValue(cmd.Id, out int rangeActive);
                emitNow = rangeActive == 0;
            }
            if (emitNow)
            {
                string changeText = ExpandEscapes(cmd.Text ?? "");
                if (!firstOutput) result.Append('\n');
                result.Append(changeText);
                firstOutput = false;
                ctx.LastPrintedLineIndex = lineIndex;
                ctx.ForceTrailingNewlineOnFinal = changeText.Contains('\n');
            }
            for (int cai = 0; cai < ctx.PendingAppends.Count; cai++)
            {
                if (!firstOutput) result.Append('\n');
                result.Append(ctx.PendingAppends[cai]);
                firstOutput = false;
                ctx.LastPrintedLineIndex = lineIndex;
                ctx.ForceTrailingNewlineOnFinal = ctx.PendingAppends[cai].Contains('\n');
            }
            ctx.PendingAppends.Clear();
            ctx.PendingReads.Clear();
            return ExecutionResult.NextCycle();
        }

        if (cmd.Type == CommandType.Print)
        {
            if (!firstOutput) result.Append('\n');
            result.Append(patternSpace);
            firstOutput = false;
            ctx.LastPrintedLineIndex = lineIndex;
            ctx.ForceTrailingNewlineOnFinal = ctx.PatternSpaceContaminatedByHoldOrN;
            return ExecutionResult.Continue();
        }

        if (cmd.Type == CommandType.PrintFirstLine)
        {
            if (!firstOutput) result.Append('\n');
            int newlinePos = -1;
            for (int ci = 0; ci < patternSpace.Length; ci++)
            {
                if (patternSpace[ci] == '\n') { newlinePos = ci; break; }
            }
            if (newlinePos >= 0)
            {
                for (int ci = 0; ci < newlinePos; ci++)
                    result.Append(patternSpace[ci]);
            }
            else
            {
                result.Append(patternSpace);
            }
            firstOutput = false;
            ctx.LastPrintedLineIndex = lineIndex;
            ctx.ForceTrailingNewlineOnFinal = newlinePos >= 0 ? _suppressDefaultOutput : ctx.PatternSpaceContaminatedByHoldOrN;
            return ExecutionResult.Continue();
        }

        if (cmd.Type == CommandType.LineNumber)
        {
            if (!firstOutput) result.Append('\n');
            result.Append(lineNumber);
            firstOutput = false;
            ctx.LastPrintedLineIndex = lineIndex;
            ctx.ForceTrailingNewlineOnFinal = true;
            return ExecutionResult.Continue();
        }

        if (cmd.Type == CommandType.List)
        {
            if (!firstOutput) result.Append('\n');
            AppendListOutput(result, patternSpace);
            firstOutput = false;
            ctx.LastPrintedLineIndex = lineIndex;
            ctx.ForceTrailingNewlineOnFinal = true;
            return ExecutionResult.Continue();
        }

        if (cmd.Type == CommandType.NextAppend)
        {
            if (lineIndex + 1 < lines.Length)
            {
                patternSpace.Append('\n');
                patternSpace.Append(lines[++lineIndex]);
                ctx.CurrentLineIndex = lineIndex;
                patternSpaceStr = patternSpace.ToString();
                lineNumber = lineIndex + 1;
            }
            else
            {
                // GNU sed always outputs pattern space at N-EOF, even with -n.
                // Pending 'a' appends are discarded (N-EOF is an abnormal exit).
                if (!firstOutput) result.Append('\n');
                result.Append(patternSpace);
                firstOutput = false;
                ctx.LastPrintedLineIndex = lineIndex;
                ctx.ForceTrailingNewlineOnFinal = ctx.PatternSpaceContaminatedByHoldOrN;
                ctx.PendingAppends.Clear();
                return ExecutionResult.QuitNoprint();
            }
            return ExecutionResult.Continue();
        }

        // n: print pattern space (unless -n), flush appends, advance to next input line
        if (cmd.Type == CommandType.Next)
        {
            if (!_suppressDefaultOutput)
            {
                if (!firstOutput) result.Append('\n');
                result.Append(patternSpace);
                firstOutput = false;
                ctx.LastPrintedLineIndex = lineIndex;
                ctx.ForceTrailingNewlineOnFinal = ctx.PatternSpaceContaminatedByHoldOrN;
            }
            for (int nai = 0; nai < ctx.PendingAppends.Count; nai++)
            {
                if (!firstOutput) result.Append('\n');
                result.Append(ctx.PendingAppends[nai]);
                firstOutput = false;
                ctx.LastPrintedLineIndex = lineIndex;
                ctx.ForceTrailingNewlineOnFinal = ctx.PendingAppends[nai].Contains('\n');
            }
            ctx.PendingAppends.Clear();
            if (lineIndex + 1 < lines.Length)
            {
                lineIndex++;
                lineNumber = lineIndex + 1;
                ctx.CurrentLineIndex = lineIndex;
                patternSpace.Clear();
                patternSpace.Append(lines[lineIndex]);
                patternSpaceStr = patternSpace.ToString();
                substitutionMadeThisCycle = false;
                ctx.PatternSpaceContaminatedByHoldOrN = false;
                ctx.PatternSpaceContaminatedByG = false;
                return ExecutionResult.Continue();
            }
            else
            {
                return ExecutionResult.QuitNoprint();
            }
        }

        // D: delete up to first embedded newline; if none, treat as d (NextCycle)
        if (cmd.Type == CommandType.DeleteFirstLine)
        {
            int dnl = -1;
            for (int di = 0; di < patternSpace.Length; di++)
            {
                if (patternSpace[di] == '\n') { dnl = di; break; }
            }
            if (dnl >= 0)
            {
                patternSpace.Remove(0, dnl + 1);
                patternSpaceStr = patternSpace.ToString();
                substitutionMadeThisCycle = false;
                return ExecutionResult.RestartCycle();
            }
            return ExecutionResult.NextCycle();
        }

        // s///p flag: when substitution succeeds and p flag is set, print the pattern space.
        if (cmd.Type == CommandType.Substitute)
        {
            bool thisSubMade = false;
            var subExecResult = ExecuteSubstitute(ctx, cmd, patternSpace, ref thisSubMade);
            if (thisSubMade)
            {
                substitutionMadeThisCycle = true;
                patternSpaceStr = patternSpace.ToString();
                // p flag: print the (modified) pattern space unconditionally when substitution matched.
                var flags = cmd.Flags ?? "";
                if (flags.Contains('p'))
                {
                    if (!firstOutput) result.Append('\n');
                    result.Append(patternSpace);
                    firstOutput = false;
                    ctx.LastPrintedLineIndex = lineIndex;
                    ctx.ForceTrailingNewlineOnFinal = ctx.PatternSpaceContaminatedByHoldOrN;
                }
            }
            return subExecResult;
        }

        return null;
    }

    /// <summary>
    /// Execute a block of commands (the body of a { } block).
    /// Returns the <see cref="ExecutionResult"/> to propagate to the caller.
    /// </summary>
    private ExecutionResult ExecuteBlock(
        SedExecutionContext ctx,
        BlockCommandImpl blkCmd,
        StringBuilder patternSpace,
        StringBuilder holdSpace,
        StringBuilder exchangeTemp,
        ref bool substitutionMadeThisCycle,
        ref bool firstOutput,
        StringBuilder result,
        string[] lines,
        ref int lineIndex,
        int lineNumber,
        int totalLines,
        ref string patternSpaceStr)
    {
        var block = blkCmd.Block;
        var localLabels = blkCmd.LocalLabels;

        int blockIndex = 0;
        while (blockIndex < block.Count)
        {
            var cmd = block[blockIndex];

            if (!AddressMatches(ctx, cmd, lineNumber, patternSpaceStr, totalLines))
            {
                blockIndex++;
                continue;
            }

            var inlineResult = ExecuteInlineCommand(ctx, cmd, patternSpace, result, lines,
                ref lineIndex, ref lineNumber, ref firstOutput, ref substitutionMadeThisCycle, ref patternSpaceStr);

            if (inlineResult.HasValue)
            {
                var ir = inlineResult.Value;
                if (ir.Action == ExecutionAction.Continue)
                {
                    blockIndex++;
                    continue;
                }
                // NextCycle, RestartCycle, QuitNoprint -- propagate to outer caller
                return ir;
            }

            // Nested block
            if (cmd.Type == CommandType.Block)
            {
                var nestedResult = ExecuteBlock(ctx, (BlockCommandImpl)cmd, patternSpace, holdSpace, exchangeTemp,
                    ref substitutionMadeThisCycle, ref firstOutput, result, lines, ref lineIndex,
                    lineNumber, totalLines, ref patternSpaceStr);
                if (nestedResult.Action != ExecutionAction.Continue)
                    return nestedResult;
                blockIndex++;
                continue;
            }

            var action = ExecuteCommand(ctx, cmd, patternSpace, holdSpace, exchangeTemp, ref substitutionMadeThisCycle, out bool patternModified);
            if (patternModified) patternSpaceStr = patternSpace.ToString();

            switch (action.Action)
            {
                case ExecutionAction.Continue:
                    break;
                case ExecutionAction.NextCycle:
                    return ExecutionResult.NextCycle();
                case ExecutionAction.Branch:
                    if (!string.IsNullOrEmpty(action.Label) && localLabels.TryGetValue(action.Label!, out int localLabelIndex))
                    {
                        blockIndex = localLabelIndex;
                        continue;
                    }
                    else if (string.IsNullOrEmpty(action.Label))
                    {
                        blockIndex = block.Count;
                        break;
                    }
                    else
                    {
                        // B2: unresolved label -- propagate to outer scope (POSIX cross-block branch)
                        return ExecutionResult.Branch(action.Label);
                    }
                case ExecutionAction.Quit:
                    return ExecutionResult.Quit();
                case ExecutionAction.QuitNoprint:
                    return ExecutionResult.QuitNoprint();
            }

            blockIndex++;
        }

        return ExecutionResult.Continue();
    }

    /// <summary>
    /// Recursively open (truncate) all write-target files from the command tree.
    /// </summary>
    private static void OpenWriteHandles(IReadOnlyList<SedCommand> commands, Dictionary<string, StreamWriter> handles)
    {
        for (int i = 0; i < commands.Count; i++)
        {
            var cmd = commands[i];
            bool isWriteCmd = cmd.Type == CommandType.WriteFile
                || cmd.Type == CommandType.WriteFirstLine
                || (cmd.Type == CommandType.Substitute && cmd.Filename != null);
            if (isWriteCmd && cmd.Filename != null)
            {
                if (!handles.ContainsKey(cmd.Filename))
                {
                    handles[cmd.Filename] = new StreamWriter(
                        new FileStream(cmd.Filename, FileMode.Create, FileAccess.Write, FileShare.Read))
                    {
                        AutoFlush = true
                    };
                }
            }
            // Recurse into blocks
            if (cmd.Block != null)
                OpenWriteHandles(cmd.Block, handles);
        }
    }

    /// <summary>
    /// Write a line to a StreamWriter, appending a newline character (<c>\n</c>).
    /// </summary>
    private static void WriteLineToHandle(StreamWriter writer, string line)
    {
        writer.Write(line);
        writer.Write('\n');
    }

    /// <summary>
    /// Write the contents of a <see cref="StringBuilder"/> to a StreamWriter, appending a newline character (<c>\n</c>).
    /// Avoids a <see cref="StringBuilder.ToString()"/> allocation.
    /// </summary>
    private static void WriteLineToHandle(StreamWriter writer, StringBuilder sb)
    {
        for (int i = 0; i < sb.Length; i++)
            writer.Write(sb[i]);
        writer.Write('\n');
    }

    /// <summary>
    /// Core processing logic: iterates lines and executes the command pipeline.
    /// </summary>
    private string ProcessLines(SedExecutionContext ctx, string[] lines)
    {
        var result = new StringBuilder();
        var patternSpace = new StringBuilder();
        var holdSpace = new StringBuilder();
        var exchangeTemp = new StringBuilder();
        bool firstOutput = true;
        bool substitutionMadeThisCycle = false;

        // Open (truncate) all write-target files before entering the line loop.
        // This ensures files are created/cleared even if no lines match.
        OpenWriteHandles(_commands, ctx.WriteHandles);

        try
        {

        int lineIndex = 0;
        for (; lineIndex < lines.Length; lineIndex++)
        {
            ctx.CurrentLineIndex = lineIndex;
            patternSpace.Clear();
            patternSpace.Append(lines[lineIndex]);
            substitutionMadeThisCycle = false;
            ctx.PatternSpaceContaminatedByHoldOrN = false;
            ctx.PatternSpaceContaminatedByG = false;
            string patternSpaceStr = patternSpace.ToString();

            int commandIndex = 0;
            while (commandIndex < _commands.Count)
            {
                var command = _commands[commandIndex];

                if (!AddressMatches(ctx, command, lineIndex + 1, patternSpaceStr, lines.Length))
                {
                    commandIndex++;
                    continue;
                }

                // Inline handlers that need access to result/firstOutput/lineIndex/pendingAppends.
                int lineNumber = lineIndex + 1;
                var inlineResult = ExecuteInlineCommand(ctx, command, patternSpace, result, lines,
                    ref lineIndex, ref lineNumber, ref firstOutput, ref substitutionMadeThisCycle, ref patternSpaceStr);

                if (inlineResult.HasValue)
                {
                    switch (inlineResult.Value.Action)
                    {
                        case ExecutionAction.Continue:
                            // n/N: after advancing, continue pipeline from the next command
                            commandIndex++;
                            continue;
                        case ExecutionAction.NextCycle:
                            for (int nai = 0; nai < ctx.PendingAppends.Count; nai++)
                            {
                                if (!firstOutput) result.Append('\n');
                                result.Append(ctx.PendingAppends[nai]);
                                firstOutput = false;
                                ctx.LastPrintedLineIndex = lineIndex;
                            }
                            ctx.PendingAppends.Clear();
                            goto NextLine;
                        case ExecutionAction.RestartCycle:
                            for (int nai = 0; nai < ctx.PendingAppends.Count; nai++)
                            {
                                if (!firstOutput) result.Append('\n');
                                result.Append(ctx.PendingAppends[nai]);
                                firstOutput = false;
                                ctx.LastPrintedLineIndex = lineIndex;
                            }
                            ctx.PendingAppends.Clear();
                            commandIndex = 0;
                            continue;
                        case ExecutionAction.QuitNoprint:
                            goto EndProcessing;
                    }
                }

                if (command.Type == CommandType.Block)
                {
                    var blockResult = ExecuteBlock(ctx, (BlockCommandImpl)command, patternSpace, holdSpace, exchangeTemp,
                        ref substitutionMadeThisCycle, ref firstOutput, result, lines, ref lineIndex,
                        lineIndex + 1, lines.Length, ref patternSpaceStr);
                    switch (blockResult.Action)
                    {
                        case ExecutionAction.NextCycle:
                            for (int nai = 0; nai < ctx.PendingAppends.Count; nai++)
                            {
                                if (!firstOutput) result.Append('\n');
                                result.Append(ctx.PendingAppends[nai]);
                                firstOutput = false;
                                ctx.LastPrintedLineIndex = lineIndex;
                            }
                            ctx.PendingAppends.Clear();
                            goto NextLine;
                        case ExecutionAction.RestartCycle:
                            for (int nai = 0; nai < ctx.PendingAppends.Count; nai++)
                            {
                                if (!firstOutput) result.Append('\n');
                                result.Append(ctx.PendingAppends[nai]);
                                firstOutput = false;
                                ctx.LastPrintedLineIndex = lineIndex;
                            }
                            ctx.PendingAppends.Clear();
                            commandIndex = 0;
                            continue;
                        case ExecutionAction.Branch:
                            if (!string.IsNullOrEmpty(blockResult.Label) && _labels.TryGetValue(blockResult.Label!, out int branchLabelIndex))
                            {
                                commandIndex = branchLabelIndex;
                                continue;
                            }
                            else if (string.IsNullOrEmpty(blockResult.Label))
                            {
                                commandIndex = _commands.Count;
                                break;
                            }
                            else
                            {
                                // Unknown top-level label: skip to end of cycle
                                commandIndex = _commands.Count;
                                break;
                            }
                        case ExecutionAction.Quit:
                            if (!_suppressDefaultOutput)
                            {
                                if (!firstOutput) result.Append('\n');
                                result.Append(patternSpace);
                                firstOutput = false;
                                ctx.LastPrintedLineIndex = lineIndex;
                                ctx.ForceTrailingNewlineOnFinal = ctx.PatternSpaceContaminatedByHoldOrN;
                            }
                            for (int bai = 0; bai < ctx.PendingAppends.Count; bai++)
                            {
                                if (!firstOutput) result.Append('\n');
                                result.Append(ctx.PendingAppends[bai]);
                                firstOutput = false;
                                ctx.LastPrintedLineIndex = lineIndex;
                                ctx.ForceTrailingNewlineOnFinal = ctx.PendingAppends[bai].Contains('\n');
                            }
                            ctx.PendingAppends.Clear();
                            for (int bri = 0; bri < ctx.PendingReads.Count; bri++)
                            {
                                if (!firstOutput) result.Append('\n');
                                result.Append(ctx.PendingReads[bri]);
                                firstOutput = false;
                                ctx.LastPrintedLineIndex = lineIndex;
                                ctx.ForceTrailingNewlineOnFinal = ctx.PendingReads[bri].Contains('\n');
                            }
                            ctx.PendingReads.Clear();
                            goto EndProcessing;
                        case ExecutionAction.QuitNoprint:
                            ctx.PendingAppends.Clear();
                            ctx.PendingReads.Clear();
                            goto EndProcessing;
                    }
                    commandIndex++;
                    continue;
                }

                var action = ExecuteCommand(ctx, command, patternSpace, holdSpace, exchangeTemp, ref substitutionMadeThisCycle, out bool patternModified);

                // Only refresh cached string when pattern space was actually modified.
                if (patternModified) patternSpaceStr = patternSpace.ToString();

                switch (action.Action)
                {
                    case ExecutionAction.Continue:
                        break;

                    case ExecutionAction.NextCycle:
                        // POSIX: 'a' text queued before 'd' must still be output
                        for (int nai = 0; nai < ctx.PendingAppends.Count; nai++)
                        {
                            if (!firstOutput) result.Append('\n');
                            result.Append(ctx.PendingAppends[nai]);
                            firstOutput = false;
                            ctx.LastPrintedLineIndex = lineIndex;
                        }
                        ctx.PendingAppends.Clear();
                        goto NextLine;
                    case ExecutionAction.Branch:
                        if (!string.IsNullOrEmpty(action.Label) && _labels.TryGetValue(action.Label!, out int labelIndex))
                        {
                            commandIndex = labelIndex;
                            continue;
                        }
                        else
                        {
                            // Branch without label jumps to end of command cycle (but still outputs)
                            commandIndex = _commands.Count;
                            break;
                        }

                    case ExecutionAction.Quit:
                        // q: print pattern space first (unless -n), then flush appends and pending reads
                        if (!_suppressDefaultOutput)
                        {
                            if (!firstOutput)
                                result.Append('\n');
                            result.Append(patternSpace);
                            firstOutput = false;
                            ctx.LastPrintedLineIndex = lineIndex;
                            ctx.ForceTrailingNewlineOnFinal = ctx.PatternSpaceContaminatedByHoldOrN;
                        }
                        for (int ai = 0; ai < ctx.PendingAppends.Count; ai++)
                        {
                            if (!firstOutput)
                                result.Append('\n');
                            result.Append(ctx.PendingAppends[ai]);
                            firstOutput = false;
                            ctx.LastPrintedLineIndex = lineIndex;
                            ctx.ForceTrailingNewlineOnFinal = ctx.PendingAppends[ai].Length > 0 && ctx.PendingAppends[ai][ctx.PendingAppends[ai].Length - 1] == '\n';
                        }
                        ctx.PendingAppends.Clear();
                        for (int ri = 0; ri < ctx.PendingReads.Count; ri++)
                        {
                            if (!firstOutput)
                                result.Append('\n');
                            result.Append(ctx.PendingReads[ri]);
                            firstOutput = false;
                            ctx.LastPrintedLineIndex = lineIndex;
                            ctx.ForceTrailingNewlineOnFinal = ctx.PendingReads[ri].Contains('\n');
                        }
                        ctx.PendingReads.Clear();
                        goto EndProcessing;

                    case ExecutionAction.QuitNoprint:
                        // Q: stop immediately, do NOT print pattern space or pending reads
                        ctx.PendingAppends.Clear();
                        ctx.PendingReads.Clear();
                        goto EndProcessing;
                }

                commandIndex++;
            }

            // Output pattern space unless suppressed
            if (!_suppressDefaultOutput)
            {
                if (!firstOutput)
                    result.Append('\n');
                result.Append(patternSpace);
                firstOutput = false;
                ctx.LastPrintedLineIndex = lineIndex;
                ctx.ForceTrailingNewlineOnFinal = ctx.PatternSpaceContaminatedByHoldOrN;
            }

            // Flush pending appends for this cycle (a commands queue text here)
            for (int ai = 0; ai < ctx.PendingAppends.Count; ai++)
            {
                if (!firstOutput)
                    result.Append('\n');
                result.Append(ctx.PendingAppends[ai]);
                firstOutput = false;
                ctx.LastPrintedLineIndex = lineIndex;
                // Append text ending with \n means it carries its own newline; otherwise no forced trailing newline
                ctx.ForceTrailingNewlineOnFinal = ctx.PendingAppends[ai].Length > 0 && ctx.PendingAppends[ai][ctx.PendingAppends[ai].Length - 1] == '\n';
            }

            // Flush pending r/R file content for this cycle
            for (int ri = 0; ri < ctx.PendingReads.Count; ri++)
            {
                if (!firstOutput)
                    result.Append('\n');
                result.Append(ctx.PendingReads[ri]);
                firstOutput = false;
                ctx.LastPrintedLineIndex = lineIndex;
                ctx.ForceTrailingNewlineOnFinal = ctx.PendingReads[ri].Contains('\n');
            }

            NextLine:
            // Delete (NextCycle) and Change drop pending appends -- clear here.
            ctx.PendingAppends.Clear();
            ctx.PendingReads.Clear();
        }

        EndProcessing:
        // Flush any pending appends (e.g., from N at EOF or final line with append)
        for (int ai = 0; ai < ctx.PendingAppends.Count; ai++)
        {
            if (!firstOutput)
                result.Append('\n');
            result.Append(ctx.PendingAppends[ai]);
            firstOutput = false;
            ctx.LastPrintedLineIndex = lineIndex;
            ctx.ForceTrailingNewlineOnFinal = ctx.PendingAppends[ai].Contains('\n');
        }
        // Flush any pending r/R reads at EOF
        for (int ri = 0; ri < ctx.PendingReads.Count; ri++)
        {
            if (!firstOutput)
                result.Append('\n');
            result.Append(ctx.PendingReads[ri]);
            firstOutput = false;
            ctx.LastPrintedLineIndex = lineIndex;
            ctx.ForceTrailingNewlineOnFinal = ctx.PendingReads[ri].Contains('\n');
        }
        ctx.PendingReads.Clear();

        return result.ToString();

        } // end try
        finally
        {
            // ctx.Dispose() handles file handle cleanup via the using statement in Transform(),
            // but we also need to clear PendingAppends here for the goto EndProcessing paths.
            ctx.PendingAppends.Clear();
        }
    }


    /// <summary>
    /// Check if an address matches the current line context.
    /// </summary>
    private bool AddressMatches(SedExecutionContext ctx, SedCommand command, int lineNumber, string lineContent, int totalLines)
    {
        var address = command.Address;
        bool matches = address.Type switch
        {
            AddressType.None => true,
            AddressType.LineNumber => lineNumber == address.CachedLineNumber,
            AddressType.LastLine => lineNumber == totalLines,
            AddressType.Pattern => MatchesPatternAddress(address.Value1!, lineContent),
            AddressType.Range => MatchesRange(ctx, address, command.Id, lineNumber, lineContent, totalLines),
            AddressType.Step => MatchesStep(address, lineNumber),
            _ => false
        };

        return address.Negated ? !matches : matches;
    }

    /// <summary>
    /// Toggle-based stateful range matching per POSIX spec.
    /// </summary>
    private bool MatchesRange(SedExecutionContext ctx, SedAddress address, int commandId, int lineNumber, string lineContent, int totalLines)
    {
        var startStr = address.Value1!;
        var endStr = address.Value2!;

        ctx.RangeStartLine.TryGetValue(commandId, out int activatedOnLine);
        bool isActive = activatedOnLine > 0;

        // Special GNU extension: "0" start allows end-pattern to match on line 1
        bool zeroStart = startStr == "0";

        if (!isActive)
        {
            // Check if start address matches current line
            bool startMatches;
            if (zeroStart)
            {
                startMatches = lineNumber == 1;
            }
            else
            {
                startMatches = MatchesAddressComponent(startStr, lineNumber, lineContent, totalLines);
            }

            if (!startMatches)
                return false;

            // Range activates on this line
            ctx.RangeStartLine[commandId] = lineNumber;

            // Check if end also matches on this same line to close immediately
            bool endMatchesNow = MatchesRangeEnd(endStr, lineNumber, lineContent, totalLines, lineNumber);
            if (endMatchesNow)
            {
                ctx.RangeStartLine[commandId] = 0;
            }
            return true;
        }
        else
        {
            // Currently in range -- check if end address matches to deactivate (inclusive)
            bool endMatches = MatchesRangeEnd(endStr, lineNumber, lineContent, totalLines, activatedOnLine);
            if (endMatches)
            {
                ctx.RangeStartLine[commandId] = 0; // deactivate
            }
            return true;
        }
    }

    /// <summary>
    /// Match a Pattern-type address against line content using the pre-warmed cache.
    /// </summary>
    private bool MatchesPatternAddress(string rawPattern, string lineContent)
    {
        var cacheKey = (rawPattern, _useEre);
        if (!_addressPatternCache.TryGetValue(cacheKey, out var regex))
        {
            string translated = _useEre ? RegexTranslator.TranslateEREPattern(rawPattern) : RegexTranslator.TranslateBREPattern(rawPattern);
            regex = new Regex(translated, RegexOptions.Compiled);
            _addressPatternCache[cacheKey] = regex;
        }
        return regex.IsMatch(lineContent);
    }

    /// <summary>
    /// Evaluate a range start/end address component string against the current line.
    /// </summary>
    private bool MatchesAddressComponent(string component, int lineNumber, string lineContent, int totalLines)
    {
        if (component == "$")
            return lineNumber == totalLines;
        if (component.StartsWith('/') && component.EndsWith('/'))
        {
            var raw = component.Substring(1, component.Length - 2);
            var key = (raw, _useEre);
            var compiled = _rangePatternCache.GetOrAdd(key, k =>
            {
                string translated = k.Item2 ? RegexTranslator.TranslateEREPattern(k.Item1) : RegexTranslator.TranslateBREPattern(k.Item1);
                return new Regex(translated, RegexOptions.Compiled);
            });
            return compiled.IsMatch(lineContent);
        }
        if (int.TryParse(component, out int n))
            return lineNumber == n;
        return false;
    }

    /// <summary>
    /// Evaluate a range end address component.
    /// </summary>
    private bool MatchesRangeEnd(string endStr, int lineNumber, string lineContent, int totalLines, int startLineNumber)
    {
        // GNU +N: match from startLine through startLine+N lines (inclusive)
        if (endStr.StartsWith('+') && int.TryParse(endStr.Substring(1), out int offset))
        {
            return lineNumber >= startLineNumber + offset;
        }

        // GNU ~N: match through next line that is a multiple of N (>= startLineNumber)
        if (endStr.StartsWith('~') && int.TryParse(endStr.Substring(1), out int multiple))
        {
            if (multiple <= 0) return true; // ~0 deactivates immediately
            int target = ((startLineNumber + multiple - 1) / multiple) * multiple;
            return lineNumber >= target;
        }

        return MatchesAddressComponent(endStr, lineNumber, lineContent, totalLines);
    }

    private static bool MatchesStep(SedAddress address, int lineNumber)
    {
        if (address.CachedStepValue == 0) return false;
        return (lineNumber - address.CachedStepStart) % address.CachedStepValue == 0
               && lineNumber >= address.CachedStepStart;
    }

    /// <summary>
    /// Returns true if the StringBuilder contains the specified character.
    /// </summary>
    private static bool StringBuilderContains(StringBuilder sb, char c)
    {
        for (int i = 0; i < sb.Length; i++)
        {
            if (sb[i] == c) return true;
        }
        return false;
    }


    /// <summary>
    /// Execute a single command - core sed functionality.
    /// </summary>
    private ExecutionResult ExecuteCommand(SedExecutionContext ctx, SedCommand command, StringBuilder patternSpace,
        StringBuilder holdSpace, StringBuilder exchangeTemp, ref bool substitutionMade, out bool patternModified)
    {
        switch (command.Type)
        {
            case CommandType.Substitute:
                bool thisSubMade = false;
                var subResult = ExecuteSubstitute(ctx, command, patternSpace, ref thisSubMade);
                if (thisSubMade) substitutionMade = true;
                patternModified = thisSubMade;
                return subResult;

            case CommandType.Delete:
                patternModified = false;
                return ExecutionResult.NextCycle();

            case CommandType.Print:
                patternModified = false;
                throw new InvalidOperationException("Print is handled inline in ProcessLines");

            case CommandType.Branch:
                patternModified = false;
                return ExecutionResult.Branch(command.Label);

            case CommandType.Test:
                patternModified = false;
                return substitutionMade ? ExecutionResult.Branch(command.Label) : ExecutionResult.Continue();

            case CommandType.TestNot:
                patternModified = false;
                return !substitutionMade ? ExecutionResult.Branch(command.Label) : ExecutionResult.Continue();

            case CommandType.Quit:
                patternModified = false;
                return ExecutionResult.Quit();

            case CommandType.QuitNoprint:
                patternModified = false;
                return ExecutionResult.QuitNoprint();

            case CommandType.Label:
                patternModified = false;
                return ExecutionResult.Continue();

            case CommandType.HoldCopy:
                holdSpace.Clear();
                holdSpace.Append(patternSpace);
                ctx.HoldSpaceLineIndex = ctx.CurrentLineIndex;
                patternModified = false;
                return ExecutionResult.Continue();

            case CommandType.HoldAppend:
                holdSpace.Append('\n');
                holdSpace.Append(patternSpace);
                ctx.HoldSpaceLineIndex = ctx.CurrentLineIndex;
                patternModified = false;
                return ExecutionResult.Continue();

            case CommandType.GetHold:
                bool holdHasNewline = StringBuilderContains(holdSpace, '\n');
                patternSpace.Clear();
                patternSpace.Append(holdSpace);
                patternModified = true;
                ctx.PatternSpaceContaminatedByHoldOrN = (ctx.HoldSpaceLineIndex != ctx.CurrentLineIndex) || holdHasNewline;
                return ExecutionResult.Continue();

            case CommandType.GetHoldAppend:
                patternSpace.Append('\n');
                patternSpace.Append(holdSpace);
                patternModified = true;
                ctx.PatternSpaceContaminatedByHoldOrN = true;
                ctx.PatternSpaceContaminatedByG = true;
                return ExecutionResult.Continue();

            case CommandType.Exchange:
                exchangeTemp.Clear();
                exchangeTemp.Append(patternSpace);
                patternSpace.Clear();
                patternSpace.Append(holdSpace);
                holdSpace.Clear();
                holdSpace.Append(exchangeTemp);
                ctx.HoldSpaceLineIndex = ctx.CurrentLineIndex;
                patternModified = true;
                ctx.PatternSpaceContaminatedByHoldOrN = StringBuilderContains(patternSpace, '\n');
                return ExecutionResult.Continue();

            case CommandType.Transliterate:
                var yResult = ExecuteTransliterate(command, patternSpace, out bool translitChanged);
                patternModified = translitChanged;
                return yResult;

            case CommandType.ReadFile:
                patternModified = false;
                if (command.Filename != null)
                {
                    if (!ctx.ReadCache.TryGetValue(command.Filename, out var cachedContent))
                    {
                        try
                        {
                            var raw = File.ReadAllText(command.Filename);
                            if (raw.Length > 0 && raw[raw.Length - 1] == '\n')
                                raw = raw.Substring(0, raw.Length - 1);
                            cachedContent = raw.Length > 0 ? raw : null;
                        }
                        catch (IOException) { cachedContent = null; }
                        catch (UnauthorizedAccessException) { cachedContent = null; }
                        catch (ArgumentException) { cachedContent = null; }
                        catch (NotSupportedException) { cachedContent = null; }
                        ctx.ReadCache[command.Filename] = cachedContent;
                    }
                    if (cachedContent != null)
                        ctx.PendingReads.Add(cachedContent);
                }
                return ExecutionResult.Continue();

            case CommandType.WriteFile:
                patternModified = false;
                if (command.Filename != null && ctx.WriteHandles.TryGetValue(command.Filename, out var ww))
                    WriteLineToHandle(ww, patternSpace);
                return ExecutionResult.Continue();

            case CommandType.ReadOneLine:
                patternModified = false;
                if (command.Filename != null)
                {
                    if (!ctx.ReadHandles.TryGetValue(command.Filename, out var rr))
                    {
                        try
                        {
                            rr = new StreamReader(command.Filename);
                            ctx.ReadHandles[command.Filename] = rr;
                        }
                        catch (IOException) { rr = null; }
                        catch (UnauthorizedAccessException) { rr = null; }
                        catch (ArgumentException) { rr = null; }
                        catch (NotSupportedException) { rr = null; }
                    }
                    if (rr != null)
                    {
                        var line = rr.ReadLine();
                        if (line != null)
                            ctx.PendingReads.Add(line);
                    }
                }
                return ExecutionResult.Continue();

            case CommandType.WriteFirstLine:
                patternModified = false;
                if (command.Filename != null && ctx.WriteHandles.TryGetValue(command.Filename, out var wf))
                {
                    int nlIdx = -1;
                    for (int wfi = 0; wfi < patternSpace.Length; wfi++)
                    {
                        if (patternSpace[wfi] == '\n') { nlIdx = wfi; break; }
                    }
                    if (nlIdx >= 0)
                        WriteLineToHandle(wf, patternSpace.ToString(0, nlIdx));
                    else
                        WriteLineToHandle(wf, patternSpace);
                }
                return ExecutionResult.Continue();

            default:
                patternModified = false;
                return ExecutionResult.Continue();
        }
    }

    /// <summary>
    /// Execute a y/source/dest/ transliterate command.
    /// </summary>
    private static ExecutionResult ExecuteTransliterate(SedCommand command, StringBuilder patternSpace, out bool changed)
    {
        if (command is not TransliterateCommand tc)
        {
            changed = false;
            return ExecutionResult.Continue();
        }

        int len = patternSpace.Length;
        if (len == 0)
        {
            changed = false;
            return ExecutionResult.Continue();
        }

        Span<char> buf = len <= 512 ? stackalloc char[len] : new char[len];
        patternSpace.CopyTo(0, buf, len);

        char[] ascii = tc._asciiTable;
        var unicode = tc._unicodeTable;
        bool anyChanged = false;

        for (int i = 0; i < len; i++)
        {
            char c = buf[i];
            if (c < 128)
            {
                char mapped = ascii[c];
                anyChanged |= (mapped != c);
                buf[i] = mapped;
            }
            else if (unicode != null && unicode.TryGetValue(c, out char replacement))
            {
                buf[i] = replacement;
                anyChanged = true;
            }
        }

        patternSpace.Clear();
        patternSpace.Append(buf);
        changed = anyChanged;
        return ExecutionResult.Continue();
    }

    /// <summary>
    /// Execute substitute command with BRE translation and flag handling.
    /// </summary>
    private ExecutionResult ExecuteSubstitute(SedExecutionContext ctx, SedCommand command, StringBuilder patternSpace, ref bool substitutionMade)
    {
        if (string.IsNullOrEmpty(command.Pattern) || command.Replacement == null)
            return ExecutionResult.Continue();

        var input = patternSpace.ToString();

        // Use cached translations
        if (!_patternCache.TryGetValue(command.Pattern, out var pattern))
        {
            pattern = _useEre ? RegexTranslator.TranslateEREPattern(command.Pattern) : RegexTranslator.TranslateBREPattern(command.Pattern);
            _patternCache[command.Pattern] = pattern;
        }

        var flags = command.Flags ?? "";

        var options = RegexOptions.None;
        if (flags.Contains('i', StringComparison.OrdinalIgnoreCase))
            options |= RegexOptions.IgnoreCase;

        try
        {
            string result;
            if (!_replacementCache.TryGetValue(command.Replacement, out var netReplacement))
            {
                netReplacement = RegexTranslator.TranslateBREReplacementString(command.Replacement);
                _replacementCache[command.Replacement] = netReplacement;
            }

            var cachedRegex = _regexCache.GetOrAdd((pattern, options), static key => new Regex(key.Item1, key.Item2));
            if (flags.Contains('g'))
            {
                result = cachedRegex.Replace(input, netReplacement);
            }
            else if (flags.Length > 0 && flags[0] >= '0' && flags[0] <= '9')
            {
                int occurrence = 0;
                int fi = 0;
                while (fi < flags.Length && flags[fi] >= '0' && flags[fi] <= '9')
                {
                    occurrence = occurrence * 10 + (flags[fi] - '0');
                    fi++;
                }
                result = ReplaceNthOccurrence(input, cachedRegex, netReplacement, occurrence);
            }
            else
            {
                result = cachedRegex.Replace(input, netReplacement, 1);
            }

            if (result != input)
            {
                patternSpace.Clear();
                patternSpace.Append(result);
                substitutionMade = true;
                if (ctx.PatternSpaceContaminatedByHoldOrN && !ctx.PatternSpaceContaminatedByG
                    && result.IndexOf('\n') < 0)
                {
                    ctx.PatternSpaceContaminatedByHoldOrN = false;
                }
                if (command.Filename != null && ctx.WriteHandles.TryGetValue(command.Filename, out var sw))
                    WriteLineToHandle(sw, result);
            }
        }
        catch (ArgumentException)
        {
            // Invalid regex - ignore gracefully
        }

        return ExecutionResult.Continue();
    }

    /// <summary>
    /// Replace the Nth occurrence of a pattern match.
    /// </summary>
    private string ReplaceNthOccurrence(string input, Regex regex, string replacement, int occurrence)
    {
        var matches = regex.Matches(input);

        if (occurrence > 0 && occurrence <= matches.Count)
        {
            var match = matches[occurrence - 1];
            var expanded = match.Result(replacement);
            return string.Concat(
                input.AsSpan(0, match.Index),
                expanded.AsSpan(),
                input.AsSpan(match.Index + match.Length));
        }

        return input;
    }

    /// <summary>
    /// Append the pattern space to the output buffer in visually unambiguous l-command form.
    /// </summary>
    private static void AppendListOutput(StringBuilder result, StringBuilder patternSpace)
    {
        const int FoldWidth = 70;
        int col = 0;

        static void EmitFold(StringBuilder result, ref int col)
        {
            result.Append('\\');
            result.Append('\n');
            col = 0;
        }

        static void Emit(char c, StringBuilder result, ref int col)
        {
            if (col >= FoldWidth - 1) EmitFold(result, ref col);
            result.Append(c);
            col++;
        }

        static void EmitEscape(ReadOnlySpan<char> esc, StringBuilder result, ref int col)
        {
            if (col + esc.Length >= FoldWidth) EmitFold(result, ref col);
            result.Append(esc);
            col += esc.Length;
        }

        Span<char> octBuf = stackalloc char[4];
        octBuf[0] = '\\';
        Span<byte> utf8Bytes = stackalloc byte[4];
        Span<char> pairBuf = stackalloc char[2];
        for (int i = 0; i < patternSpace.Length; i++)
        {
            char c = patternSpace[i];
            switch (c)
            {
                case '\\': EmitEscape("\\\\", result, ref col); break;
                case '\a': EmitEscape("\\a", result, ref col); break;
                case '\b': EmitEscape("\\b", result, ref col); break;
                case '\f': EmitEscape("\\f", result, ref col); break;
                case '\r': EmitEscape("\\r", result, ref col); break;
                case '\t': EmitEscape("\\t", result, ref col); break;
                case '\v': EmitEscape("\\v", result, ref col); break;
                case '\n': EmitEscape("\\n", result, ref col); break;
                default:
                    if (c < 0x20 || c == 0x7f)
                    {
                        int val = (int)c;
                        octBuf[1] = (char)('0' + ((val >> 6) & 7));
                        octBuf[2] = (char)('0' + ((val >> 3) & 7));
                        octBuf[3] = (char)('0' + (val & 7));
                        EmitEscape(octBuf, result, ref col);
                    }
                    else if (c >= 0x80)
                    {
                        int byteCount;
                        if (char.IsHighSurrogate(c) && i + 1 < patternSpace.Length && char.IsLowSurrogate(patternSpace[i + 1]))
                        {
                            char lowSurrogate = patternSpace[i + 1];
                            pairBuf[0] = c;
                            pairBuf[1] = lowSurrogate;
                            byteCount = Encoding.UTF8.GetBytes(pairBuf, utf8Bytes);
                            i++;
                        }
                        else
                        {
                            byteCount = Encoding.UTF8.GetBytes(new ReadOnlySpan<char>(in c), utf8Bytes);
                        }
                        for (int b = 0; b < byteCount; b++)
                        {
                            int bval = utf8Bytes[b];
                            octBuf[1] = (char)('0' + ((bval >> 6) & 7));
                            octBuf[2] = (char)('0' + ((bval >> 3) & 7));
                            octBuf[3] = (char)('0' + (bval & 7));
                            EmitEscape(octBuf, result, ref col);
                        }
                    }
                    else
                    {
                        Emit(c, result, ref col);
                    }
                    break;
            }
        }

        result.Append('$');
    }

    /// <summary>
    /// Expand GNU sed text-command escape sequences.
    /// </summary>
    private static string ExpandEscapes(string text)
    {
        if (text.IndexOf('\\') < 0)
            return text;

        var sb = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\\' && i + 1 < text.Length)
            {
                char next = text[i + 1];
                switch (next)
                {
                    case 'n': sb.Append('\n'); i += 2; break;
                    case 't': sb.Append('\t'); i += 2; break;
                    case 'a': sb.Append('\a'); i += 2; break;
                    case '\\': sb.Append('\\'); i += 2; break;
                    default: sb.Append(c); i++; break;
                }
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }
        return sb.ToString();
    }
}

#endregion

#region SedEngine - Facade for API consistency

/// <summary>
/// Static facade for sed operations, mirroring the GrepEngine and AwkEngine pattern.
/// Provides Compile() and Execute() entry points.
/// </summary>
public static class SedEngine
{
    /// <summary>
    /// Compile a sed script string into a reusable SedScript object.
    /// </summary>
    public static SedScript Compile(string script, bool suppressDefault = false, bool useEre = false)
        => SedParser.Parse(script, suppressDefault, useEre);

    /// <summary>
    /// Compile and execute a sed script on the given input text.
    /// Returns (output, exitCode) for consistency with GrepEngine and AwkEngine.
    /// </summary>
    public static (string Output, int ExitCode) Execute(string script, string input, bool suppressDefault = false, bool useEre = false)
    {
        return Compile(script, suppressDefault, useEre).Execute(input);
    }
}

#endregion

#region SedParser - String parsing

/// <summary>
/// Parser component that converts sed script strings into SedScript objects.
/// Uses single-pass index-based parsing for SplitCommands and recognizes
/// all standard sed command types. No LINQ or regex in parser hot paths.
/// </summary>
public static class SedParser
{
    /// <summary>
    /// Parse a sed script string into a SedScript object for repeated use.
    /// </summary>
    /// <param name="scriptText">The sed script text to parse.</param>
    /// <param name="suppressDefaultOutput">When true, suppresses automatic output (like sed -n).</param>
    /// <param name="useEre">When true, uses Extended Regular Expressions instead of BRE.</param>
    /// <returns>A compiled SedScript ready for transformation.</returns>
    /// <exception cref="SedException">Thrown when scriptText is null, empty, or whitespace.</exception>
    public static SedScript Parse(string scriptText, bool suppressDefaultOutput = false, bool useEre = false)
    {
        if (string.IsNullOrWhiteSpace(scriptText))
            throw new SedException("Script text cannot be empty");

        // Scan for -n flag lines in the script text (e.g. when tests embed "-n" as a line)
        // Lines that are exactly "-n" (trimmed) activate suppress mode and are removed from script.
        var scriptLines = scriptText.Split('\n');
        var filteredLines = new System.Text.StringBuilder();
        for (int i = 0; i < scriptLines.Length; i++)
        {
            string trimmed = scriptLines[i].Trim();
            if (trimmed == "-n")
            {
                suppressDefaultOutput = true;
            }
            else if (trimmed != "-e")  // -e is a separator token, skip it
            {
                if (filteredLines.Length > 0) filteredLines.Append('\n');
                filteredLines.Append(scriptLines[i]);
            }
        }
        string filtered = filteredLines.ToString();
        if (string.IsNullOrWhiteSpace(filtered))
        {
            // All lines were -n/-e tokens; return empty script
            return new SedScript(new List<SedCommand>(), suppressDefaultOutput, useEre);
        }

        int pos = 0;
        var commands = ParseCommandList(filtered, ref pos, filtered.Length, stopAtBrace: false);
        return new SedScript(commands, suppressDefaultOutput, useEre);
    }

    /// <summary>
    /// Recursive descent parser for a list of sed commands.
    /// </summary>
    private static List<SedCommand> ParseCommandList(string script, ref int pos, int length, bool stopAtBrace)
    {
        var commands = new List<SedCommand>();

        while (pos < length)
        {
            // Skip whitespace and separators
            char c = script[pos];
            if (c == ' ' || c == '\t' || c == ';' || c == '\n')
            {
                pos++;
                continue;
            }

            // End of block
            if (c == '}')
            {
                if (stopAtBrace)
                    pos++; // consume the '}'
                break;
            }

            int savedPos = pos;
            int afterAddress = SkipAddressPrefix(script, pos, length);
            int addressEndPos = afterAddress;
            bool negated = false;
            if (afterAddress < length && script[afterAddress] == '!')
            {
                negated = true;
                afterAddress++;
            }
            // Skip whitespace between address and command character
            while (afterAddress < length && (script[afterAddress] == ' ' || script[afterAddress] == '\t'))
                afterAddress++;

            if (afterAddress < length && script[afterAddress] == '{')
            {
                string addressStr = script.Substring(savedPos, addressEndPos - savedPos);
                pos = afterAddress + 1; // consume '{'

                // Parse the block body recursively
                var blockBody = ParseCommandList(script, ref pos, length, stopAtBrace: true);

                SedAddress blockAddress = string.IsNullOrWhiteSpace(addressStr)
                    ? SedAddress.All()
                    : ParseAddress(addressStr.Trim());

                var blockCmd = SedCommand.BlockCommand(blockAddress, negated,
                    new ReadOnlyCollection<SedCommand>(blockBody));
                commands.Add(blockCmd);
                continue;
            }

            // Ordinary command: scan to the next separator or block boundary
            var current = new StringBuilder();
            pos = savedPos;

            // Use ConsumeNextCommandString to extract a single command string
            pos = ConsumeNextCommandString(script, pos, length, current);
            string commandStr = current.ToString().Trim();
            if (commandStr.Length > 0)
            {
                var cmd = ParseSingleCommand(commandStr);
                if (cmd != null)
                    commands.Add(cmd);
            }
        }

        return commands;
    }

    /// <summary>
    /// Consume a single command token from the script.
    /// </summary>
    private static int ConsumeNextCommandString(string script, int pos, int length, StringBuilder current)
    {
        // Skip leading whitespace
        while (pos < length && (script[pos] == ' ' || script[pos] == '\t'))
            pos++;

        bool inFileArg = false;

        while (pos < length)
        {
            char c = script[pos];

            if (inFileArg)
            {
                if (c == '\n')
                {
                    pos++;
                    break;
                }
                if (c == '}')
                {
                    break;
                }
                current.Append(c);
                pos++;
                continue;
            }

            if (c == ';' || c == '\n')
            {
                pos++;
                break;
            }

            if (c == '}')
            {
                break;
            }

            int cmdLetterPos = FindDelimitedCommandPosition(script, current, pos, length);
            if (cmdLetterPos >= 0)
            {
                char commandLetter = script[cmdLetterPos];
                pos = ConsumeDelimitedCommand(script, current, pos, cmdLetterPos, commandLetter, length);
                if (pos < length && (script[pos] == ';' || script[pos] == '\n'))
                    pos++;
                break;
            }

            current.Append(c);
            pos++;

            if (c == ' ' && IsFileCommandBeforeSpace(current))
                inFileArg = true;
        }

        return pos;
    }

    /// <summary>
    /// Split a script string into individual command strings.
    /// </summary>
    private static List<string> SplitCommands(string scriptText)
    {
        var commands = new List<string>();
        var current = new StringBuilder();
        bool inFileArg = false;
        int i = 0;
        int length = scriptText.Length;

        while (i < length)
        {
            char c = scriptText[i];

            if (current.Length == 0 && (c == ' ' || c == '\t'))
            {
                i++;
                continue;
            }

            if (inFileArg)
            {
                if (c == '\n')
                {
                    FlushCurrentCommand(commands, current);
                    inFileArg = false;
                    i++;
                }
                else
                {
                    current.Append(c);
                    i++;
                }
                continue;
            }

            if (c == ';' || c == '\n')
            {
                FlushCurrentCommand(commands, current);
                i++;
                continue;
            }

            if (c == '{' || c == '}')
            {
                FlushCurrentCommand(commands, current);
                i++;
                continue;
            }

            int cmdLetterPos = FindDelimitedCommandPosition(scriptText, current, i, length);
            if (cmdLetterPos >= 0)
            {
                char commandLetter = scriptText[cmdLetterPos];
                i = ConsumeDelimitedCommand(scriptText, current, i, cmdLetterPos, commandLetter, length);
                continue;
            }

            current.Append(c);
            i++;

            if (c == ' ' && IsFileCommandBeforeSpace(current))
                inFileArg = true;
        }

        if (current.Length > 0)
        {
            commands.Add(current.ToString());
        }

        return commands;
    }

    private static void FlushCurrentCommand(List<string> commands, StringBuilder current)
    {
        if (current.Length > 0)
        {
            commands.Add(current.ToString());
            current.Clear();
        }
    }

    private static bool IsFileCommandBeforeSpace(StringBuilder current)
    {
        int len = current.Length;
        if (len < 2) return false;
        char cmdLetter = current[len - 2];
        if (cmdLetter != 'r' && cmdLetter != 'w' && cmdLetter != 'R' && cmdLetter != 'W')
            return false;
        if (len == 2) return true;
        char preceding = current[len - 3];
        return !char.IsLetter(preceding);
    }

    private static int FindDelimitedCommandPosition(string scriptText, StringBuilder current, int i, int length)
    {
        int cmdPos;

        if (current.Length == 0)
        {
            cmdPos = SkipAddressPrefix(scriptText, i, length);
            if (cmdPos < length && scriptText[cmdPos] == '!')
                cmdPos++;
        }
        else
        {
            cmdPos = i;
        }

        if (cmdPos >= length)
            return -1;

        char ch = scriptText[cmdPos];
        if (ch != 's' && ch != 'y')
            return -1;

        if ((cmdPos + 1) >= length || char.IsLetterOrDigit(scriptText[cmdPos + 1]))
            return -1;

        return cmdPos;
    }

    private static int ConsumeDelimitedCommand(string scriptText, StringBuilder current,
        int startPos, int cmdLetterPos, char commandLetter, int length)
    {
        int pos = startPos;

        while (pos <= cmdLetterPos && pos < length)
        {
            current.Append(scriptText[pos]);
            pos++;
        }

        if (pos >= length)
            return pos;

        char delimiter = scriptText[pos];
        current.Append(delimiter);
        pos++;

        int sectionsCrossed = 0;

        while (pos < length && sectionsCrossed < 2)
        {
            char ch = scriptText[pos];

            if (ch == '\\' && (pos + 1) < length)
            {
                current.Append(ch);
                pos++;
                current.Append(scriptText[pos]);
                pos++;
                continue;
            }

            if (ch == delimiter)
            {
                sectionsCrossed++;
            }

            current.Append(ch);
            pos++;
        }

        if (commandLetter == 's')
        {
            while (pos < length)
            {
                char ch = scriptText[pos];
                if (ch == ';' || ch == '\n' || ch == '}')
                    break;
                current.Append(ch);
                pos++;
            }
        }

        return pos;
    }

    private static int SkipSingleAddressComponent(string scriptText, int pos, int length)
    {
        if (pos >= length)
            return pos;

        if (scriptText[pos] >= '0' && scriptText[pos] <= '9')
        {
            while (pos < length && scriptText[pos] >= '0' && scriptText[pos] <= '9')
                pos++;
            return pos;
        }

        if (scriptText[pos] == '$')
            return pos + 1;

        if (scriptText[pos] == '/')
        {
            pos++;
            while (pos < length && scriptText[pos] != '/')
            {
                if (scriptText[pos] == '\\' && (pos + 1) < length)
                    pos++;
                pos++;
            }
            if (pos < length)
                pos++;
        }

        return pos;
    }

    private static int SkipAddressPrefix(string scriptText, int pos, int length)
    {
        pos = SkipSingleAddressComponent(scriptText, pos, length);

        if (pos < length && scriptText[pos] == ',')
        {
            pos++;
            pos = SkipSingleAddressComponent(scriptText, pos, length);
        }

        return pos;
    }

    private static SedCommand? ParseSingleCommand(string commandString)
    {
        if (commandString.Length == 0)
            return null;

        if (commandString[0] == ':')
        {
            var label = commandString.Length > 1 ? commandString.Substring(1).Trim() : "";
            return SedCommand.DefineLabel(label);
        }

        var (address, commandPart) = ExtractAddress(commandString);

        if (commandPart.Length == 0)
            return null;

        char cmdChar = commandPart[0];

        if (cmdChar == 's' && commandPart.Length > 1 && !char.IsLetterOrDigit(commandPart[1]))
        {
            return ParseSubstituteCommand(address, commandPart);
        }

        if (cmdChar == 'y' && commandPart.Length > 1 && !char.IsLetterOrDigit(commandPart[1]))
        {
            return ParseTransliterateCommand(address, commandPart);
        }

        switch (cmdChar)
        {
            case 'd': return SedCommand.Delete(address);
            case 'p': return SedCommand.Print(address);
            case 'P': return SedCommand.SimpleCommand(address, CommandType.PrintFirstLine);
            case 'n': return SedCommand.SimpleCommand(address, CommandType.Next);
            case 'N': return SedCommand.SimpleCommand(address, CommandType.NextAppend);
            case 'h': return SedCommand.SimpleCommand(address, CommandType.HoldCopy);
            case 'H': return SedCommand.SimpleCommand(address, CommandType.HoldAppend);
            case 'g': return SedCommand.SimpleCommand(address, CommandType.GetHold);
            case 'G': return SedCommand.SimpleCommand(address, CommandType.GetHoldAppend);
            case 'x': return SedCommand.SimpleCommand(address, CommandType.Exchange);
            case '=': return SedCommand.SimpleCommand(address, CommandType.LineNumber);
            case 'l': return SedCommand.SimpleCommand(address, CommandType.List);
            case 'D': return SedCommand.SimpleCommand(address, CommandType.DeleteFirstLine);
            case 'q': return SedCommand.Quit(address);
            case 'Q': return SedCommand.QuitNoprint(address);
        }

        string rest = commandPart.Length > 1 ? commandPart.Substring(1) : "";

        if (cmdChar == 'b' || cmdChar == 't' || cmdChar == 'T')
        {
            var label = rest.Trim();
            string? labelOrNull = label.Length > 0 ? label : null;
            return cmdChar switch
            {
                'b' => SedCommand.Branch(address, labelOrNull),
                't' => SedCommand.Test(address, labelOrNull),
                'T' => SedCommand.TestNot(address, labelOrNull),
                _ => null
            };
        }

        if (cmdChar == 'a' || cmdChar == 'i' || cmdChar == 'c')
        {
            string? text = null;
            if (rest.Length > 0 && rest[0] == '\\')
            {
                text = rest.Length > 1 ? rest.Substring(1) : "";
            }
            else if (rest.Length > 0 && rest[0] == ' ')
            {
                text = rest.Substring(1);
            }
            if (text != null)
            {
                return cmdChar switch
                {
                    'a' => SedCommand.Append(address, text),
                    'i' => SedCommand.Insert(address, text),
                    'c' => SedCommand.Change(address, text),
                    _ => null
                };
            }
        }

        if (cmdChar == 'r' || cmdChar == 'w' || cmdChar == 'R' || cmdChar == 'W')
        {
            var filename = rest.TrimStart();
            if (filename.Length == 0) return null;
            return cmdChar switch
            {
                'r' => SedCommand.ReadFile(address, filename),
                'w' => SedCommand.WriteFile(address, filename),
                'R' => SedCommand.ReadOneLine(address, filename),
                _   => SedCommand.WriteFirstLine(address, filename),
            };
        }

        return null;
    }

    private static SedAddress ParseAddress(string addressStr)
    {
        var (address, _) = ExtractAddress(addressStr + "p");
        return address;
    }

        /// <summary>
    /// Extract address portion from command string using index-based parsing.
    /// </summary>
    private static (SedAddress address, string commandPart) ExtractAddress(string commandString)
    {
        int pos = 0;
        int length = commandString.Length;

        var (addr1Type, addr1Value, newPos1) = ParseAddressComponent(commandString, pos, length);

        if (addr1Type == AddressType.None && newPos1 == pos)
            return (SedAddress.All(), commandString);

        pos = newPos1;

        if (addr1Type == AddressType.LineNumber && pos < length && commandString[pos] == '~')
        {
            pos++;
            int stepNumStart = pos;
            while (pos < length && commandString[pos] >= '0' && commandString[pos] <= '9')
                pos++;
            int stepStart = int.Parse(addr1Value!);
            int stepValue = pos > stepNumStart ? int.Parse(commandString.Substring(stepNumStart, pos - stepNumStart)) : 0;
            var stepAddress = SedAddress.Step(stepStart, stepValue);
            if (pos < length && commandString[pos] == '!')
            {
                stepAddress = stepAddress.Negate();
                pos++;
            }
            return (stepAddress, pos < length ? commandString.Substring(pos) : "");
        }

        if (pos < length && commandString[pos] == ',')
        {
            pos++;

            string endStr;
            if (pos < length && (commandString[pos] == '+' || commandString[pos] == '~'))
            {
                char gnuPrefix = commandString[pos];
                pos++;
                int numStart = pos;
                while (pos < length && commandString[pos] >= '0' && commandString[pos] <= '9')
                    pos++;
                endStr = gnuPrefix + commandString.Substring(numStart, pos - numStart);
            }
            else
            {
                var (addr2Type, addr2Value, newPos2) = ParseAddressComponent(commandString, pos, length);
                pos = newPos2;
                endStr = FormatAddressComponent(addr2Type, addr2Value);
            }

            string startStr = FormatAddressComponent(addr1Type, addr1Value);

            var rangeAddress = SedAddress.Range(startStr, endStr);

            if (pos < length && commandString[pos] == '!')
            {
                rangeAddress = rangeAddress.Negate();
                pos++;
            }

            return (rangeAddress, pos < length ? commandString.Substring(pos) : "");
        }

        SedAddress address = addr1Type switch
        {
            AddressType.LineNumber => SedAddress.LineNumber(int.Parse(addr1Value!)),
            AddressType.LastLine => SedAddress.LastLine(),
            AddressType.Pattern => SedAddress.Pattern(addr1Value!),
            _ => SedAddress.All()
        };

        if (address.Type == AddressType.None)
            return (address, commandString);

        if (pos < length && commandString[pos] == '!')
        {
            address = address.Negate();
            pos++;
        }

        return (address, pos < length ? commandString.Substring(pos) : "");
    }

    private static (AddressType type, string? value, int newPos) ParseAddressComponent(string input, int pos, int length)
    {
        if (pos >= length)
            return (AddressType.None, null, pos);

        char c = input[pos];

        if (c >= '0' && c <= '9')
        {
            int start = pos;
            while (pos < length && input[pos] >= '0' && input[pos] <= '9')
                pos++;
            return (AddressType.LineNumber, input.Substring(start, pos - start), pos);
        }

        if (c == '$')
            return (AddressType.LastLine, "$", pos + 1);

        if (c == '/')
        {
            pos++;
            var patternBuilder = new StringBuilder();
            while (pos < length && input[pos] != '/')
            {
                if (input[pos] == '\\' && (pos + 1) < length)
                {
                    patternBuilder.Append(input[pos]);
                    pos++;
                    patternBuilder.Append(input[pos]);
                    pos++;
                }
                else
                {
                    patternBuilder.Append(input[pos]);
                    pos++;
                }
            }
            if (pos < length)
                pos++;
            return (AddressType.Pattern, patternBuilder.ToString(), pos);
        }

        return (AddressType.None, null, pos);
    }

    private static string FormatAddressComponent(AddressType type, string? value)
    {
        if (type == AddressType.LastLine) return "$";
        if (type == AddressType.Pattern) return "/" + value + "/";
        return value ?? "";
    }

    /// <summary>
    /// Parse substitute command (s/pattern/replacement/flags).
    /// </summary>
    /// <exception cref="SedException">Thrown when the command format is invalid.</exception>
    private static SedCommand ParseSubstituteCommand(SedAddress address, string commandPart)
    {
        if (commandPart.Length < 4)
            throw new SedException($"Invalid substitute command: {commandPart}");

        char delimiter = commandPart[1];
        var parts = SplitByDelimiter(commandPart, 2, delimiter);

        if (parts.Count < 2)
            throw new SedException($"Invalid substitute command format: {commandPart}");

        int rawFlagsStart = FindRawFlagsPosition(commandPart, 2, delimiter);
        var rawFlags = rawFlagsStart >= 0 ? commandPart.Substring(rawFlagsStart) : "";

        string? writeFilename = null;
        string flags = rawFlags;
        int wIdx = -1;
        for (int fi = 0; fi < rawFlags.Length; fi++)
        {
            if (rawFlags[fi] == 'w')
            {
                wIdx = fi;
                break;
            }
        }
        if (wIdx >= 0)
        {
            flags = rawFlags.Substring(0, wIdx);
            int fnStart = wIdx + 1;
            if (fnStart < rawFlags.Length && rawFlags[fnStart] == ' ')
                fnStart++;
            writeFilename = fnStart < rawFlags.Length ? rawFlags.Substring(fnStart) : null;
        }

        return new SedCommand(address, CommandType.Substitute, parts[0], parts[1], flags, filename: writeFilename);
    }

    private static int FindRawFlagsPosition(string input, int startIndex, char delimiter)
    {
        int found = 0;
        int i = startIndex;
        int length = input.Length;
        while (i < length)
        {
            char c = input[i];
            if (c == '\\' && i + 1 < length)
            {
                i += 2;
            }
            else if (c == delimiter)
            {
                found++;
                i++;
                if (found == 2)
                    return i;
            }
            else
            {
                i++;
            }
        }
        return -1;
    }

    /// <summary>
    /// Parse transliterate command (y/source/dest/).
    /// </summary>
    /// <exception cref="SedException">Thrown when the command format is invalid.</exception>
    private static SedCommand ParseTransliterateCommand(SedAddress address, string commandPart)
    {
        if (commandPart.Length < 4)
            throw new SedException($"Invalid transliterate command: {commandPart}");

        char delimiter = commandPart[1];
        var parts = SplitByDelimiter(commandPart, 2, delimiter);

        if (parts.Count < 2)
            throw new SedException($"Invalid transliterate command format: {commandPart}");

        return SedCommand.Transliterate(address, parts[0], parts[1]);
    }

    private static List<string> SplitByDelimiter(string input, int startIndex, char delimiter)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        int i = startIndex;
        int length = input.Length;

        while (i < length)
        {
            char c = input[i];

            if (c == '\\' && (i + 1) < length)
            {
                current.Append(c);
                i++;
                current.Append(input[i]);
                i++;
            }
            else if (c == delimiter)
            {
                parts.Add(current.ToString());
                current.Clear();
                i++;
            }
            else
            {
                current.Append(c);
                i++;
            }
        }

        parts.Add(current.ToString());
        return parts;
    }
}

#endregion

#region FluentSed - Programmatic API

/// <summary>
/// Fluent API for building sed scripts programmatically.
/// </summary>
public class FluentSed
{
    private readonly List<SedCommand> _commands = new();
    private bool _suppressDefaultOutput = false;

    /// <summary>
    /// Create a new FluentSed builder instance
    /// </summary>
    public static FluentSed Create() => new();

    /// <summary>
    /// Suppress automatic output of the pattern space (equivalent to sed -n)
    /// </summary>
    public FluentSed SuppressDefaultOutput()
    {
        _suppressDefaultOutput = true;
        return this;
    }

    /// <summary>
    /// Add a substitute command that applies to all lines
    /// </summary>
    public FluentSed Substitute(string pattern, string replacement, string? flags = null)
    {
        _commands.Add(SedCommand.Substitute(SedAddress.All(), pattern, replacement, flags));
        return this;
    }

    /// <summary>
    /// Add a substitute command that applies to a specific line number
    /// </summary>
    public FluentSed Substitute(int lineNumber, string pattern, string replacement, string? flags = null)
    {
        _commands.Add(SedCommand.Substitute(SedAddress.LineNumber(lineNumber), pattern, replacement, flags));
        return this;
    }

    /// <summary>
    /// Add a delete command that applies to all lines
    /// </summary>
    public FluentSed Delete()
    {
        _commands.Add(SedCommand.Delete(SedAddress.All()));
        return this;
    }

    /// <summary>
    /// Add a delete command for a specific line number
    /// </summary>
    public FluentSed Delete(int lineNumber)
    {
        _commands.Add(SedCommand.Delete(SedAddress.LineNumber(lineNumber)));
        return this;
    }

    /// <summary>
    /// Add a delete command for lines matching a pattern
    /// </summary>
    public FluentSed DeleteMatching(string pattern)
    {
        _commands.Add(SedCommand.Delete(SedAddress.Pattern(pattern)));
        return this;
    }

    /// <summary>
    /// Add a print command that applies to all lines
    /// </summary>
    public FluentSed Print()
    {
        _commands.Add(SedCommand.Print(SedAddress.All()));
        return this;
    }

    /// <summary>
    /// Add a print command for a specific line number
    /// </summary>
    public FluentSed Print(int lineNumber)
    {
        _commands.Add(SedCommand.Print(SedAddress.LineNumber(lineNumber)));
        return this;
    }

    /// <summary>
    /// Build the final SedScript for repeated transformations.
    /// </summary>
    public SedScript Build()
    {
        return new SedScript(_commands, _suppressDefaultOutput);
    }

    /// <summary>
    /// Convenience method to build and immediately transform input.
    /// </summary>
    public string Transform(string input)
    {
        return Build().Transform(input);
    }
}

#endregion


#region Grep Engine

/// <summary>
/// Options controlling grep behaviour.
/// </summary>
public sealed class GrepOptions
{
    public bool UseERE { get; set; }
    public bool FixedStrings { get; set; }
    public bool IgnoreCase { get; set; }
    public bool InvertMatch { get; set; }
    public bool LineNumbers { get; set; }
    public bool Count { get; set; }
    public bool FilesWithMatches { get; set; }
    public bool FilesWithoutMatches { get; set; }
    public bool OnlyMatching { get; set; }
    public bool WholeWord { get; set; }
    public bool WholeLine { get; set; }
    public bool Quiet { get; set; }
    public bool SuppressErrors { get; set; }
    public bool ForceFilename { get; set; }
    public bool SuppressFilename { get; set; }
    public int MaxCount { get; set; }
    public int AfterContext { get; set; }
    public int BeforeContext { get; set; }
    public int BothContext { get; set; }
    public List<string> Patterns { get; } = new();
    public List<string> PatternFiles { get; } = new();
}

/// <summary>
/// Compiled grep script. Holds pre-compiled regex patterns and frozen option flags.
/// Thread-safe for concurrent use after construction. Supports both streaming
/// (TextReader/TextWriter) and string-based execution.
/// Exit codes: 0 = match found, 1 = no match, 2 = error.
/// </summary>
public sealed class GrepScript
{
    private readonly List<Regex> _patterns;
    private readonly bool _invertMatch;
    private readonly bool _lineNumbers;
    private readonly bool _count;
    private readonly bool _filesWithMatches;
    private readonly bool _filesWithoutMatches;
    private readonly bool _onlyMatching;
    private readonly bool _quiet;
    private readonly int _maxCount;
    private readonly int _afterContext;
    private readonly int _beforeContext;
    private readonly bool _showFilenameDefault;

    internal GrepScript(List<Regex> patterns, GrepOptions options)
    {
        _patterns = patterns;
        _invertMatch = options.InvertMatch;
        _lineNumbers = options.LineNumbers;
        _count = options.Count;
        _filesWithMatches = options.FilesWithMatches;
        _filesWithoutMatches = options.FilesWithoutMatches;
        _onlyMatching = options.OnlyMatching;
        _quiet = options.Quiet;
        _maxCount = options.MaxCount > 0 ? options.MaxCount : int.MaxValue;
        _afterContext = options.BothContext > 0 ? options.BothContext : options.AfterContext;
        _beforeContext = options.BothContext > 0 ? options.BothContext : options.BeforeContext;
        _showFilenameDefault = options.ForceFilename;
    }

    /// <summary>
    /// Streaming execution: reads line-at-a-time from TextReader, writes to TextWriter.
    /// Returns exit code (0=found, 1=not found, 2=error).
    /// </summary>
    public int Execute(TextReader input, TextWriter output, string? filename = null)
    {
        bool showFilename = filename != null && _showFilenameDefault;

        try
        {
            int matchCount = 0;
            int lineNum = 0;
            int lastPrintedLine = -1;
            bool needSeparator = false;
            int afterRemaining = 0;
            bool hasContext = _afterContext > 0 || _beforeContext > 0;

            // Ring buffer for before-context lines
            int ringSize = _beforeContext;
            string[]? ring = ringSize > 0 ? new string[ringSize] : null;
            int[]? ringLineNums = ringSize > 0 ? new int[ringSize] : null;
            int ringStart = 0; // index of oldest element
            int ringCount = 0; // number of valid elements

            string? line;
            while ((line = input.ReadLine()) != null)
            {
                lineNum++;

                // Check if we've already hit max matches and no after-context pending
                if (matchCount >= _maxCount && afterRemaining <= 0)
                {
                    if (!_count && !_quiet && !_filesWithMatches && !_filesWithoutMatches)
                        break;
                    if (_quiet)
                        break;
                    if (_filesWithMatches)
                        break;
                    if (_filesWithoutMatches)
                        break;
                    if (_count)
                        break;
                }

                bool matches = LineMatches(line);
                if (_invertMatch) matches = !matches;

                if (matches && matchCount < _maxCount)
                {
                    matchCount++;

                    if (_quiet)
                        return 0;

                    if (_filesWithMatches)
                    {
                        if (filename != null)
                            output.Write(filename + "\n");
                        return 0;
                    }

                    if (!_count && !_filesWithoutMatches)
                    {
                        // Flush before-context from ring buffer
                        if (ring != null && ringCount > 0)
                        {
                            // Print group separator if needed
                            if (hasContext && needSeparator)
                            {
                                // Only print separator if there's a gap between last printed and first context line
                                int firstContextLine = ringLineNums![((ringStart + ringSize - ringCount) % ringSize + ringSize) % ringSize];
                                if (lastPrintedLine >= 0 && firstContextLine > lastPrintedLine + 1)
                                    output.Write("--\n");
                            }

                            for (int ri = 0; ri < ringCount; ri++)
                            {
                                int idx = ((ringStart + ringSize - ringCount + ri) % ringSize + ringSize) % ringSize;
                                if (ringLineNums![idx] > lastPrintedLine)
                                {
                                    // Context lines use '--' separator instead of ':'
                                    if (showFilename)
                                    {
                                        output.Write(filename);
                                        output.Write('-');
                                    }
                                    if (_lineNumbers)
                                    {
                                        output.Write(ringLineNums[idx]);
                                        output.Write('-');
                                    }
                                    output.Write(ring![idx]);
                                    output.Write('\n');
                                    lastPrintedLine = ringLineNums[idx];
                                }
                            }
                            ringCount = 0;
                        }
                        else if (hasContext && needSeparator && lastPrintedLine >= 0 && lineNum > lastPrintedLine + 1)
                        {
                            output.Write("--\n");
                        }

                        if (_onlyMatching && !_invertMatch)
                        {
                            WriteOnlyMatching(output, line, lineNum, showFilename, filename);
                        }
                        else
                        {
                            WriteLine(output, line, lineNum, showFilename, filename, false);
                        }
                        lastPrintedLine = lineNum;
                        afterRemaining = _afterContext;
                        needSeparator = true;
                    }
                }
                else
                {
                    // Non-matching line
                    if (afterRemaining > 0 && !_count && !_filesWithoutMatches && !_quiet)
                    {
                        // Print as after-context
                        if (showFilename)
                        {
                            output.Write(filename);
                            output.Write('-');
                        }
                        if (_lineNumbers)
                        {
                            output.Write(lineNum);
                            output.Write('-');
                        }
                        output.Write(line);
                        output.Write('\n');
                        lastPrintedLine = lineNum;
                        afterRemaining--;
                    }
                    else if (ring != null)
                    {
                        // Add to before-context ring buffer
                        ring[ringStart] = line;
                        ringLineNums![ringStart] = lineNum;
                        ringStart = (ringStart + 1) % ringSize;
                        if (ringCount < ringSize) ringCount++;
                    }
                }
            }

            if (_filesWithoutMatches)
            {
                if (matchCount == 0 && filename != null)
                {
                    output.Write(filename + "\n");
                    return 0;
                }
                return 1;
            }

            if (_count)
            {
                if (showFilename)
                {
                    output.Write(filename);
                    output.Write(':');
                }
                output.Write(matchCount);
                output.Write('\n');
                return matchCount > 0 ? 0 : 1;
            }

            return matchCount > 0 ? 0 : 1;
        }
        catch (Exception)
        {
            return 2;
        }
    }

    /// <summary>
    /// Convenience: string-based execution for backward compatibility.
    /// Returns (output, exitCode).
    /// </summary>
    public (string Output, int ExitCode) Execute(string input, string? filename = null)
    {
        using var reader = new StringReader(input);
        using var writer = new StringWriter();
        int exitCode = Execute(reader, writer, filename);
        return (writer.ToString(), exitCode);
    }

    /// <summary>
    /// Execute grep over multiple files. Handles filename prefixing and aggregated exit codes.
    /// </summary>
    public (string Output, int ExitCode) ExecuteMultiFile((string Filename, string Content)[] files)
    {
        var sb = new StringBuilder();
        bool anyMatch = false;

        for (int i = 0; i < files.Length; i++)
        {
            var (output, exitCode) = Execute(files[i].Content, files[i].Filename);
            if (exitCode == 0) anyMatch = true;
            if (exitCode == 2) return (output, 2);
            sb.Append(output);
        }

        return (sb.ToString(), anyMatch ? 0 : 1);
    }

    /// <summary>
    /// Test whether a single line matches any of the compiled patterns.
    /// </summary>
    private bool LineMatches(string line)
    {
        for (int i = 0; i < _patterns.Count; i++)
        {
            if (_patterns[i].IsMatch(line))
                return true;
        }
        return false;
    }

    private void WriteLine(TextWriter output, string line, int lineNum,
        bool showFilename, string? filename, bool isContext)
    {
        if (showFilename)
        {
            output.Write(filename);
            output.Write(':');
        }
        if (_lineNumbers)
        {
            output.Write(lineNum);
            output.Write(':');
        }
        output.Write(line);
        output.Write('\n');
    }

    private void WriteOnlyMatching(TextWriter output, string line, int lineNum,
        bool showFilename, string? filename)
    {
        for (int pi = 0; pi < _patterns.Count; pi++)
        {
            var m = _patterns[pi].Match(line);
            while (m.Success)
            {
                if (showFilename)
                {
                    output.Write(filename);
                    output.Write(':');
                }
                if (_lineNumbers)
                {
                    output.Write(lineNum);
                    output.Write(':');
                }
                output.Write(m.Value);
                output.Write('\n');
                m = m.NextMatch();
            }
        }
    }
}

/// <summary>
/// High-performance grep engine supporting BRE, ERE, and fixed-string matching.
/// Reuses the BRE/ERE translators from RegexTranslator for pattern compilation.
/// Exit codes: 0 = match found, 1 = no match, 2 = error.
/// </summary>
public static class GrepEngine
{
    /// <summary>
    /// Compile a GrepScript from the given options.
    /// </summary>
    public static GrepScript Compile(GrepOptions options)
    {
        var patterns = BuildPatterns(options);
        if (patterns.Count == 0)
        {
            patterns.Add(new Regex("", RegexOptions.Compiled));
        }
        return new GrepScript(patterns, options);
    }

    /// <summary>
    /// Compile a GrepScript from a single pattern string with common options.
    /// </summary>
    public static GrepScript Compile(string pattern,
        bool useERE = false, bool fixedStrings = false,
        bool ignoreCase = false, bool invertMatch = false,
        bool lineNumbers = false, bool onlyMatching = false,
        bool wholeWord = false, bool wholeLine = false)
    {
        var options = new GrepOptions
        {
            UseERE = useERE,
            FixedStrings = fixedStrings,
            IgnoreCase = ignoreCase,
            InvertMatch = invertMatch,
            LineNumbers = lineNumbers,
            OnlyMatching = onlyMatching,
            WholeWord = wholeWord,
            WholeLine = wholeLine,
        };
        options.Patterns.Add(pattern);
        return Compile(options);
    }

    /// <summary>
    /// Execute grep on the given input text (from stdin) with no filename context.
    /// </summary>
    public static (string Output, int ExitCode) Execute(GrepOptions options, string input)
    {
        return Compile(options).Execute(input);
    }

    /// <summary>
    /// Execute grep on the given input text with an optional filename for output prefixing.
    /// </summary>
    public static (string Output, int ExitCode) Execute(GrepOptions options, string input, string? filename)
    {
        return Compile(options).Execute(input, filename);
    }

    /// <summary>
    /// Execute grep over multiple files.
    /// </summary>
    public static (string Output, int ExitCode) ExecuteMultiFile(GrepOptions options, (string Filename, string Content)[] files)
    {
        bool multiFile = files.Length > 1;

        var fileOpts = CloneOptions(options);
        if (multiFile && !options.SuppressFilename)
            fileOpts.ForceFilename = true;

        return Compile(fileOpts).ExecuteMultiFile(files);
    }

    /// <summary>
    /// Build compiled Regex patterns from the options.
    /// </summary>
    private static List<Regex> BuildPatterns(GrepOptions options)
    {
        var rawPatterns = new List<string>();

        for (int i = 0; i < options.Patterns.Count; i++)
            rawPatterns.Add(options.Patterns[i]);

        for (int i = 0; i < options.PatternFiles.Count; i++)
        {
            string content = File.ReadAllText(options.PatternFiles[i]);
            var filePatterns = content.Split('\n');
            for (int j = 0; j < filePatterns.Length; j++)
            {
                if (j == filePatterns.Length - 1 && filePatterns[j].Length == 0)
                    continue;
                rawPatterns.Add(filePatterns[j]);
            }
        }

        if (rawPatterns.Count == 0) return new List<Regex>();

        var regexOpts = RegexOptions.Compiled;
        if (options.IgnoreCase) regexOpts |= RegexOptions.IgnoreCase;

        var result = new List<Regex>(rawPatterns.Count);
        for (int i = 0; i < rawPatterns.Count; i++)
        {
            string translated = TranslatePattern(rawPatterns[i], options);

            if (options.WholeLine)
                translated = "^(?:" + translated + ")$";
            else if (options.WholeWord)
                translated = @"(?<!\w)(?:" + translated + @")(?!\w)";

            result.Add(new Regex(translated, regexOpts));
        }

        return result;
    }

    /// <summary>
    /// Translate a single raw pattern into .NET regex syntax based on mode (BRE/ERE/Fixed).
    /// </summary>
    private static string TranslatePattern(string pattern, GrepOptions options)
    {
        if (options.FixedStrings)
            return Regex.Escape(pattern);

        if (options.UseERE)
            return RegexTranslator.TranslateEREPattern(pattern);

        return RegexTranslator.TranslateBREPattern(pattern);
    }

    /// <summary>
    /// Create a shallow clone of GrepOptions with the same settings.
    /// </summary>
    internal static GrepOptions CloneOptions(GrepOptions src)
    {
        var dst = new GrepOptions
        {
            UseERE = src.UseERE,
            FixedStrings = src.FixedStrings,
            IgnoreCase = src.IgnoreCase,
            InvertMatch = src.InvertMatch,
            LineNumbers = src.LineNumbers,
            Count = src.Count,
            FilesWithMatches = src.FilesWithMatches,
            FilesWithoutMatches = src.FilesWithoutMatches,
            OnlyMatching = src.OnlyMatching,
            WholeWord = src.WholeWord,
            WholeLine = src.WholeLine,
            Quiet = src.Quiet,
            SuppressErrors = src.SuppressErrors,
            ForceFilename = src.ForceFilename,
            SuppressFilename = src.SuppressFilename,
            MaxCount = src.MaxCount,
            AfterContext = src.AfterContext,
            BeforeContext = src.BeforeContext,
            BothContext = src.BothContext,
        };
        for (int i = 0; i < src.Patterns.Count; i++)
            dst.Patterns.Add(src.Patterns[i]);
        for (int i = 0; i < src.PatternFiles.Count; i++)
            dst.PatternFiles.Add(src.PatternFiles[i]);
        return dst;
    }
}

#endregion
