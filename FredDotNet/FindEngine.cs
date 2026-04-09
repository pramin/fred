using System.Text;
using System.Text.RegularExpressions;

namespace FredDotNet;

/// <summary>
/// Exception thrown by FindEngine for invalid options or predicates.
/// </summary>
public sealed class FindException : Exception
{
    public FindException(string message) : base(message) { }
    public FindException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Types of predicates supported by the find engine.
/// </summary>
public enum FindPredicateType
{
    // Glob matching
    Name, IName, Path, IPath,
    // Type filter
    Type,
    // Size filter
    Size,
    // Time-based
    MTime, MMin, Newer,
    // Empty
    Empty,
    // Depth control (handled at options level, but kept for arg parsing)
    MaxDepth, MinDepth,
    // Logical
    Not, And, Or,
    // Actions
    Print, Print0, Prune,
    // Internal: grouping node
    Group,
    // Always-true (used as implicit default)
    True,
}

/// <summary>
/// A single predicate in a find expression tree.
/// Leaf predicates test a filesystem entry; logical predicates combine children.
/// </summary>
public sealed class FindPredicate
{
    public FindPredicateType Type { get; }

    /// <summary>Pattern string for Name/IName/Path/IPath, type char for Type, size spec for Size, etc.</summary>
    public string? Value { get; }

    /// <summary>Numeric value for time predicates (+N/-N/N parsed into magnitude).</summary>
    public long NumericValue { get; }

    /// <summary>Sign: +1 means greater-than, -1 means less-than, 0 means exactly.</summary>
    public int Sign { get; }

    /// <summary>Children for logical predicates (Not has 1 child, And/Or/Group have 2+).</summary>
    public List<FindPredicate> Children { get; } = new();

    /// <summary>Compiled regex for glob matching (Name/IName/Path/IPath).</summary>
    internal Regex? CompiledPattern { get; }

    public FindPredicate(FindPredicateType type, string? value = null, long numericValue = 0, int sign = 0)
    {
        Type = type;
        Value = value;
        NumericValue = numericValue;
        Sign = sign;

        // Pre-compile glob patterns
        if (type == FindPredicateType.Name || type == FindPredicateType.IName ||
            type == FindPredicateType.Path || type == FindPredicateType.IPath)
        {
            var opts = RegexOptions.Compiled | RegexOptions.Singleline;
            if (type == FindPredicateType.IName || type == FindPredicateType.IPath)
                opts |= RegexOptions.IgnoreCase;
            CompiledPattern = new Regex(GlobToRegex(value ?? "*"), opts);
        }
    }

    /// <summary>
    /// Convert a shell glob pattern to a regex pattern.
    /// Supports *, ?, and [abc] character classes.
    /// </summary>
    internal static string GlobToRegex(string glob)
    {
        var sb = new StringBuilder("^");
        for (int i = 0; i < glob.Length; i++)
        {
            char c = glob[i];
            switch (c)
            {
                case '*':
                    sb.Append(".*");
                    break;
                case '?':
                    sb.Append('.');
                    break;
                case '[':
                    int close = glob.IndexOf(']', i + 1);
                    if (close < 0)
                    {
                        sb.Append("\\[");
                    }
                    else
                    {
                        sb.Append('[');
                        string inner = glob.Substring(i + 1, close - i - 1);
                        // Handle negation: [!abc] -> [^abc]
                        if (inner.Length > 0 && inner[0] == '!')
                            sb.Append('^').Append(Regex.Escape(inner.Substring(1)).Replace("\\-", "-"));
                        else
                            sb.Append(Regex.Escape(inner).Replace("\\-", "-"));
                        sb.Append(']');
                        i = close;
                    }
                    break;
                case '.': case '(': case ')': case '+': case '{': case '}':
                case '^': case '$': case '|': case '\\':
                    sb.Append('\\').Append(c);
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        sb.Append('$');
        return sb.ToString();
    }
}

/// <summary>
/// Options for configuring a find operation.
/// </summary>
public sealed class FindOptions
{
    public List<string> StartPaths { get; } = new();
    public int MaxDepth { get; set; } = int.MaxValue;
    public int MinDepth { get; set; } = 0;
    public List<FindPredicate> Predicates { get; } = new();
    public bool Print0 { get; set; }
}

/// <summary>
/// Mutable context passed through evaluation for action side-effects.
/// Not exposed publicly; used internally during a single walk.
/// </summary>
internal sealed class EvalContext
{
    public List<string>? Results;
    public TextWriter? Output;
    public bool Print0;
    public int PrintCount;

    public void EmitPath(string path)
    {
        PrintCount++;
        if (Results != null)
            Results.Add(path);
        if (Output != null)
        {
            if (Print0)
            {
                Output.Write(path);
                Output.Write('\0');
            }
            else
            {
                Output.WriteLine(path);
            }
        }
    }
}

/// <summary>
/// A compiled, reusable, thread-safe find script.
/// Immutable after construction — safe to call Execute from multiple threads.
/// </summary>
public sealed class FindScript
{
    private readonly FindOptions _options;
    private readonly FindPredicate _rootPredicate;
    private readonly bool _hasPrintAction;

    internal FindScript(FindOptions options, FindPredicate rootPredicate)
    {
        _options = options;
        _rootPredicate = rootPredicate;
        _hasPrintAction = ContainsPrintAction(rootPredicate);
    }

    private static bool ContainsPrintAction(FindPredicate p)
    {
        if (p.Type == FindPredicateType.Print || p.Type == FindPredicateType.Print0)
            return true;
        for (int i = 0; i < p.Children.Count; i++)
        {
            if (ContainsPrintAction(p.Children[i]))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Execute the find and return all matching paths as a list.
    /// </summary>
    public List<string> Execute(string? startPath = null)
    {
        var results = new List<string>();
        var ctx = new EvalContext { Results = results, Print0 = _options.Print0 };
        var paths = new List<string>();
        if (startPath != null)
            paths.Add(startPath);
        else
            paths.AddRange(_options.StartPaths);

        if (paths.Count == 0)
            paths.Add(".");

        for (int i = 0; i < paths.Count; i++)
        {
            Walk(paths[i], 0, ctx);
        }
        return results;
    }

    /// <summary>
    /// Execute the find, writing matching paths to the given TextWriter.
    /// Returns the number of matches found.
    /// </summary>
    public int Execute(TextWriter output, string? startPath = null)
    {
        var ctx = new EvalContext { Output = output, Print0 = _options.Print0 };
        var paths = new List<string>();
        if (startPath != null)
            paths.Add(startPath);
        else
            paths.AddRange(_options.StartPaths);

        if (paths.Count == 0)
            paths.Add(".");

        for (int i = 0; i < paths.Count; i++)
        {
            Walk(paths[i], 0, ctx);
        }
        return ctx.PrintCount;
    }

    /// <summary>
    /// Process a single filesystem entry at the given depth.
    /// Tests the entry against the predicate tree, emits if matched,
    /// then recurses into subdirectories if applicable.
    /// Each entry is tested exactly once.
    /// </summary>
    private void Walk(string path, int depth, EvalContext ctx)
    {
        // Evaluate this entry if within depth bounds
        bool pruned = false;
        if (depth >= _options.MinDepth && depth <= _options.MaxDepth)
        {
            bool matched = Evaluate(_rootPredicate, path, depth, ref pruned, ctx);

            // If there are no explicit -print actions in the expression tree,
            // use the default behavior: print if the expression matched.
            if (!_hasPrintAction && matched)
            {
                ctx.EmitPath(path);
            }
            // If there ARE explicit -print actions, output happened as a side
            // effect during Evaluate — we do not print again here.
        }

        // If pruned or at max depth, don't descend
        if (pruned || depth >= _options.MaxDepth)
            return;

        // Only descend into directories (not symlinks)
        if (!Directory.Exists(path))
            return;

        // Check for directory symlinks — don't follow by default
        try
        {
            var dirInfo = new DirectoryInfo(path);
            if (depth > 0 && dirInfo.LinkTarget != null)
                return;
        }
        catch
        {
            return;
        }

        try
        {
            var entries = Directory.EnumerateFileSystemEntries(path);
            var sorted = new List<string>();
            foreach (var entry in entries)
                sorted.Add(entry);
            sorted.Sort(StringComparer.Ordinal);

            for (int i = 0; i < sorted.Count; i++)
            {
                Walk(sorted[i], depth + 1, ctx);
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
    }

    private bool Evaluate(FindPredicate predicate, string path, int depth, ref bool pruned, EvalContext ctx)
    {
        switch (predicate.Type)
        {
            case FindPredicateType.True:
                return true;

            case FindPredicateType.Name:
            case FindPredicateType.IName:
            {
                string name = System.IO.Path.GetFileName(path);
                return predicate.CompiledPattern!.IsMatch(name);
            }

            case FindPredicateType.Path:
            case FindPredicateType.IPath:
                return predicate.CompiledPattern!.IsMatch(path);

            case FindPredicateType.Type:
            {
                if (predicate.Value == null) return false;
                char typeChar = predicate.Value[0];
                return typeChar switch
                {
                    'f' => File.Exists(path) && !new FileInfo(path).Attributes.HasFlag(FileAttributes.Directory),
                    'd' => Directory.Exists(path),
                    'l' => IsSymlink(path),
                    _ => false,
                };
            }

            case FindPredicateType.Size:
            {
                if (!File.Exists(path) || Directory.Exists(path))
                    return false;
                long fileSize = new FileInfo(path).Length;
                long targetBytes = predicate.NumericValue;
                return CompareNumeric(fileSize, targetBytes, predicate.Sign);
            }

            case FindPredicateType.MTime:
            {
                DateTime mtime = GetModTime(path);
                if (mtime == DateTime.MinValue) return false;

                double daysAgo = (DateTime.UtcNow - mtime).TotalDays;
                long intDays = (long)daysAgo;
                return CompareNumeric(intDays, predicate.NumericValue, predicate.Sign);
            }

            case FindPredicateType.MMin:
            {
                DateTime mtime = GetModTime(path);
                if (mtime == DateTime.MinValue) return false;

                double minutesAgo = (DateTime.UtcNow - mtime).TotalMinutes;
                long intMinutes = (long)minutesAgo;
                return CompareNumeric(intMinutes, predicate.NumericValue, predicate.Sign);
            }

            case FindPredicateType.Newer:
            {
                if (predicate.Value == null) return false;
                DateTime mtime = GetModTime(path);
                if (mtime == DateTime.MinValue) return false;

                DateTime refTime = File.GetLastWriteTimeUtc(predicate.Value);
                return mtime > refTime;
            }

            case FindPredicateType.Empty:
            {
                if (Directory.Exists(path))
                {
                    if (File.Exists(path) && !new FileInfo(path).Attributes.HasFlag(FileAttributes.Directory))
                        return new FileInfo(path).Length == 0;

                    try
                    {
                        var enumerator = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
                        bool hasAny = enumerator.MoveNext();
                        enumerator.Dispose();
                        return !hasAny;
                    }
                    catch { return false; }
                }
                if (File.Exists(path))
                    return new FileInfo(path).Length == 0;
                return false;
            }

            case FindPredicateType.Not:
            {
                if (predicate.Children.Count == 0) return true;
                bool childPruned = false;
                bool childResult = Evaluate(predicate.Children[0], path, depth, ref childPruned, ctx);
                if (childPruned) pruned = true;
                return !childResult;
            }

            case FindPredicateType.And:
            {
                for (int i = 0; i < predicate.Children.Count; i++)
                {
                    if (!Evaluate(predicate.Children[i], path, depth, ref pruned, ctx))
                        return false;
                }
                return true;
            }

            case FindPredicateType.Or:
            {
                for (int i = 0; i < predicate.Children.Count; i++)
                {
                    if (Evaluate(predicate.Children[i], path, depth, ref pruned, ctx))
                        return true;
                }
                return false;
            }

            case FindPredicateType.Group:
            {
                if (predicate.Children.Count == 0) return true;
                if (predicate.Children.Count == 1)
                    return Evaluate(predicate.Children[0], path, depth, ref pruned, ctx);
                for (int i = 0; i < predicate.Children.Count; i++)
                {
                    if (!Evaluate(predicate.Children[i], path, depth, ref pruned, ctx))
                        return false;
                }
                return true;
            }

            case FindPredicateType.Print:
                // -print is an action: emit the path as a side effect, return true
                ctx.EmitPath(path);
                return true;

            case FindPredicateType.Print0:
                ctx.EmitPath(path);
                return true;

            case FindPredicateType.Prune:
                pruned = true;
                return true;

            default:
                return true;
        }
    }

    private static DateTime GetModTime(string path)
    {
        if (Directory.Exists(path))
            return Directory.GetLastWriteTimeUtc(path);
        if (File.Exists(path))
            return File.GetLastWriteTimeUtc(path);
        return DateTime.MinValue;
    }

    private static bool IsSymlink(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var fi = new FileInfo(path);
                return fi.LinkTarget != null;
            }
            if (Directory.Exists(path))
            {
                var di = new DirectoryInfo(path);
                return di.LinkTarget != null;
            }
        }
        catch { }
        return false;
    }

    private static bool CompareNumeric(long actual, long target, int sign)
    {
        if (sign > 0) return actual > target;
        if (sign < 0) return actual < target;
        return actual == target;
    }
}

/// <summary>
/// Static entry point for the find engine. Follows the compile-once pattern.
/// </summary>
public static class FindEngine
{
    /// <summary>
    /// Compile a FindScript from FindOptions.
    /// </summary>
    public static FindScript Compile(FindOptions options)
    {
        FindPredicate root;
        if (options.Predicates.Count == 0)
        {
            root = new FindPredicate(FindPredicateType.True);
        }
        else if (options.Predicates.Count == 1)
        {
            root = options.Predicates[0];
        }
        else
        {
            // Implicit AND of all predicates
            root = new FindPredicate(FindPredicateType.And);
            for (int i = 0; i < options.Predicates.Count; i++)
                root.Children.Add(options.Predicates[i]);
        }

        return new FindScript(options, root);
    }

    /// <summary>
    /// Compile a FindScript from find-style CLI arguments.
    /// </summary>
    public static FindScript Compile(string[] args)
    {
        var options = ParseArgs(args);
        return Compile(options);
    }

    /// <summary>
    /// Convenience: compile and execute in one call.
    /// </summary>
    public static List<string> Execute(FindOptions options, string startPath)
    {
        var script = Compile(options);
        return script.Execute(startPath);
    }

    /// <summary>
    /// Parse find-style CLI arguments into FindOptions.
    /// </summary>
    public static FindOptions ParseArgs(string[] args)
    {
        var options = new FindOptions();
        int i = 0;

        // Collect start paths (arguments before first predicate/option)
        while (i < args.Length)
        {
            string arg = args[i];
            if ((arg.Length > 0 && arg[0] == '-' && arg != "-") || arg == "(" || arg == ")" || arg == "!")
                break;
            options.StartPaths.Add(arg);
            i++;
        }

        // Parse predicates
        var predicates = ParsePredicateList(args, ref i, options);

        for (int p = 0; p < predicates.Count; p++)
            options.Predicates.Add(predicates[p]);

        return options;
    }

    private static List<FindPredicate> ParsePredicateList(string[] args, ref int i, FindOptions options)
    {
        var result = new List<FindPredicate>();

        while (i < args.Length)
        {
            if (args[i] == ")")
                break;

            var pred = ParseExpression(args, ref i, options);
            if (pred != null)
                result.Add(pred);
        }

        return result;
    }

    private static FindPredicate? ParseExpression(string[] args, ref int i, FindOptions options)
    {
        var left = ParseAndExpression(args, ref i, options);
        if (left == null) return null;

        while (i < args.Length && (args[i] == "-o" || args[i] == "-or"))
        {
            i++;
            var right = ParseAndExpression(args, ref i, options);
            if (right == null) break;

            var orNode = new FindPredicate(FindPredicateType.Or);
            orNode.Children.Add(left);
            orNode.Children.Add(right);
            left = orNode;
        }

        return left;
    }

    private static FindPredicate? ParseAndExpression(string[] args, ref int i, FindOptions options)
    {
        var left = ParseUnaryExpression(args, ref i, options);
        if (left == null) return null;

        while (i < args.Length && args[i] != ")" && args[i] != "-o" && args[i] != "-or")
        {
            if (args[i] == "-a" || args[i] == "-and")
            {
                i++;
            }

            if (i >= args.Length || args[i] == ")" || args[i] == "-o" || args[i] == "-or")
                break;

            var right = ParseUnaryExpression(args, ref i, options);
            if (right == null) break;

            var andNode = new FindPredicate(FindPredicateType.And);
            andNode.Children.Add(left);
            andNode.Children.Add(right);
            left = andNode;
        }

        return left;
    }

    private static FindPredicate? ParseUnaryExpression(string[] args, ref int i, FindOptions options)
    {
        if (i >= args.Length) return null;

        if (args[i] == "!" || args[i] == "-not")
        {
            i++;
            var child = ParseUnaryExpression(args, ref i, options);
            if (child == null)
                throw new FindException("Expected expression after '!'");
            var not = new FindPredicate(FindPredicateType.Not);
            not.Children.Add(child);
            return not;
        }

        if (args[i] == "(")
        {
            i++;
            var expr = ParseExpression(args, ref i, options);
            if (i < args.Length && args[i] == ")")
                i++;
            return expr;
        }

        return ParsePrimary(args, ref i, options);
    }

    private static FindPredicate? ParsePrimary(string[] args, ref int i, FindOptions options)
    {
        if (i >= args.Length) return null;

        string arg = args[i];

        switch (arg)
        {
            case "-name":
                i++;
                RequireArg(args, i, "-name");
                return new FindPredicate(FindPredicateType.Name, args[i++]);

            case "-iname":
                i++;
                RequireArg(args, i, "-iname");
                return new FindPredicate(FindPredicateType.IName, args[i++]);

            case "-path":
            case "-wholename":
                i++;
                RequireArg(args, i, arg);
                return new FindPredicate(FindPredicateType.Path, args[i++]);

            case "-ipath":
            case "-iwholename":
                i++;
                RequireArg(args, i, arg);
                return new FindPredicate(FindPredicateType.IPath, args[i++]);

            case "-type":
                i++;
                RequireArg(args, i, "-type");
                return new FindPredicate(FindPredicateType.Type, args[i++]);

            case "-size":
            {
                i++;
                RequireArg(args, i, "-size");
                string sizeStr = args[i++];
                var (sizeBytes, sign) = ParseSizeSpec(sizeStr);
                return new FindPredicate(FindPredicateType.Size, sizeStr, sizeBytes, sign);
            }

            case "-mtime":
            {
                i++;
                RequireArg(args, i, "-mtime");
                string val = args[i++];
                var (n, sign) = ParseSignedNumber(val);
                return new FindPredicate(FindPredicateType.MTime, val, n, sign);
            }

            case "-mmin":
            {
                i++;
                RequireArg(args, i, "-mmin");
                string val = args[i++];
                var (n, sign) = ParseSignedNumber(val);
                return new FindPredicate(FindPredicateType.MMin, val, n, sign);
            }

            case "-newer":
                i++;
                RequireArg(args, i, "-newer");
                return new FindPredicate(FindPredicateType.Newer, args[i++]);

            case "-empty":
                i++;
                return new FindPredicate(FindPredicateType.Empty);

            case "-maxdepth":
                i++;
                RequireArg(args, i, "-maxdepth");
                options.MaxDepth = int.Parse(args[i++]);
                return null;

            case "-mindepth":
                i++;
                RequireArg(args, i, "-mindepth");
                options.MinDepth = int.Parse(args[i++]);
                return null;

            case "-print":
                i++;
                return new FindPredicate(FindPredicateType.Print);

            case "-print0":
                i++;
                options.Print0 = true;
                return new FindPredicate(FindPredicateType.Print0);

            case "-prune":
                i++;
                return new FindPredicate(FindPredicateType.Prune);

            default:
                throw new FindException($"Unknown predicate: {arg}");
        }
    }

    private static void RequireArg(string[] args, int i, string flag)
    {
        if (i >= args.Length)
            throw new FindException($"Missing argument for {flag}");
    }

    internal static (long Bytes, int Sign) ParseSizeSpec(string spec)
    {
        int sign = 0;
        int start = 0;

        if (spec.Length > 0 && spec[0] == '+')
        {
            sign = 1;
            start = 1;
        }
        else if (spec.Length > 0 && spec[0] == '-')
        {
            sign = -1;
            start = 1;
        }

        long multiplier = 512; // Default: 512-byte blocks (POSIX)
        int end = spec.Length;

        if (spec.Length > start)
        {
            char suffix = spec[spec.Length - 1];
            switch (suffix)
            {
                case 'c': multiplier = 1; end--; break;
                case 'w': multiplier = 2; end--; break;
                case 'k': multiplier = 1024; end--; break;
                case 'M': multiplier = 1024 * 1024; end--; break;
                case 'G': multiplier = 1024L * 1024 * 1024; end--; break;
                default:
                    if (!char.IsDigit(suffix))
                        throw new FindException($"Invalid size suffix: {suffix}");
                    break;
            }
        }

        string numStr = spec.Substring(start, end - start);
        if (!long.TryParse(numStr, out long num))
            throw new FindException($"Invalid size specification: {spec}");

        return (num * multiplier, sign);
    }

    internal static (long Value, int Sign) ParseSignedNumber(string s)
    {
        int sign = 0;
        int start = 0;

        if (s.Length > 0 && s[0] == '+')
        {
            sign = 1;
            start = 1;
        }
        else if (s.Length > 0 && s[0] == '-')
        {
            sign = -1;
            start = 1;
        }

        if (!long.TryParse(s.Substring(start), out long val))
            throw new FindException($"Invalid numeric value: {s}");

        return (val, sign);
    }
}
