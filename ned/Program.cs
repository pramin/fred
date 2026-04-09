using System.Text;
using FredDotNet;

namespace ned;

/// <summary>
/// ned - A sed-compatible stream editor for .NET
///
/// Supports:
///   Multiple -e scripts and -f script files (applied in declaration order)
///   -i[suffix]  in-place editing with optional backup suffix
///   -E / -r     ERE (Extended Regular Expression) mode
///   -s          separate file mode (line numbers and $ reset per file)
///   -z          NUL-separated record mode (\0 instead of \n)
///   --          end-of-options marker
/// </summary>
public class Program
{
    public static int Main(string[] args)
    {
        try
        {
            var options = ParseCommandLine(args);

            if (options.ShowHelp)
            {
                ShowHelp();
                return 0;
            }

            if (options.ShowVersion)
            {
                ShowVersion();
                return 0;
            }

            if (options.ScriptParts.Count == 0)
            {
                Console.Error.WriteLine("ned: no script given");
                return 1;
            }

            // Reject -i with stdin: in-place editing requires real file paths.
            if (options.InPlace && options.InputFiles.Count == 0)
            {
                Console.Error.WriteLine("ned: -i may not be used with stdin");
                return 1;
            }

            // Join all script parts in declaration order with newline separator.
            string combinedScript = string.Join("\n", options.ScriptParts);

            // Compile the script once; reuse for each file / stream.
            var sedScript = SedParser.Parse(combinedScript, options.SuppressDefaultOutput, options.UseEre);

            if (options.InputFiles.Count == 0)
            {
                // Stdin mode — no in-place editing possible.
                string result = ProcessInput(Console.In.ReadToEnd(), sedScript, options.NulSeparated);
                Console.Write(result);
            }
            else if (options.InPlace)
            {
                // In-place editing: process each file independently.
                // Two-phase: first compute all results and write to temp files,
                // then rename atomically so a mid-batch failure leaves earlier
                // files already committed and remaining files untouched.

                // Phase 1: validate, process, write to temp files.
                // Collect (originalPath, tempPath, backupPath) before any rename.
                var tempPaths = new List<(string Original, string Temp)>(options.InputFiles.Count);
                try
                {
                    for (int fi = 0; fi < options.InputFiles.Count; fi++)
                    {
                        string file = options.InputFiles[fi];
                        if (file == "-")
                        {
                            Console.Error.WriteLine("ned: -i may not be used with stdin ('-')");
                            return 1;
                        }
                        if (!File.Exists(file))
                        {
                            Console.Error.WriteLine($"ned: can't open input file {file}: No such file or directory");
                            return 1;
                        }

                        string original = File.ReadAllText(file);
                        // SedScript.Transform() resets all state on each call,
                        // so the same instance can be reused across files.
                        string result = ProcessInput(original, sedScript, options.NulSeparated);

                        string dir = Path.GetDirectoryName(Path.GetFullPath(file)) ?? ".";
                        string tmp = Path.Combine(dir, Path.GetRandomFileName());
                        File.WriteAllText(tmp, result);

                        // Preserve Unix file permissions on the temp file before rename.
                        // Only available on non-Windows platforms (.NET 7+).
                        if (!OperatingSystem.IsWindows())
                        {
                            try
                            {
                                var mode = File.GetUnixFileMode(file);
                                File.SetUnixFileMode(tmp, mode);
                            }
                            catch (IOException)
                            {
                                // Best-effort: if permissions can't be read/set, continue.
                            }
                        }

                        tempPaths.Add((file, tmp));
                    }

                    // Phase 2: backup (if requested) then rename each temp file into place.
                    for (int fi = 0; fi < tempPaths.Count; fi++)
                    {
                        var (file, tmp) = tempPaths[fi];

                        // Write backup before overwriting.
                        if (!string.IsNullOrEmpty(options.BackupSuffix))
                        {
                            File.Copy(file, file + options.BackupSuffix, overwrite: true);
                        }

                        // Atomic rename of temp file over original.
                        File.Move(tmp, file, overwrite: true);
                    }
                }
                catch
                {
                    // Best-effort cleanup: remove any temp files that were written during Phase 1
                    // but not yet renamed into place during Phase 2. Errors here are ignored so
                    // the original exception propagates cleanly to the caller.
                    for (int fi = 0; fi < tempPaths.Count; fi++)
                    {
                        string tmp = tempPaths[fi].Temp;
                        if (File.Exists(tmp))
                            try { File.Delete(tmp); } catch { /* best-effort */ }
                    }
                    throw;
                }
            }
            else if (options.Separate)
            {
                // Separate mode: each file processed independently with fresh state.
                // Validate all paths first so we fail before producing any output.
                for (int fi = 0; fi < options.InputFiles.Count; fi++)
                {
                    string file = options.InputFiles[fi];
                    if (file != "-" && !File.Exists(file))
                    {
                        Console.Error.WriteLine($"ned: can't open input file {file}: No such file or directory");
                        return 1;
                    }
                }

                for (int fi = 0; fi < options.InputFiles.Count; fi++)
                {
                    string file = options.InputFiles[fi];
                    string input = file == "-"
                        ? Console.In.ReadToEnd()
                        : File.ReadAllText(file);
                    // SedScript.Transform() resets all state on each call,
                    // so the same instance can be reused across files.
                    string result = ProcessInput(input, sedScript, options.NulSeparated);
                    Console.Write(result);
                }
            }
            else
            {
                // Default: process all files as a single continuous stream so that
                // $ matches the very last line across all files combined.
                // NOTE: All file content is loaded into memory at once. For very large
                // inputs this may be a concern; streaming line-by-line would require
                // lookahead to detect $ across file boundaries.

                // First pass: validate all file paths exist.
                for (int fi = 0; fi < options.InputFiles.Count; fi++)
                {
                    string file = options.InputFiles[fi];
                    if (file != "-" && !File.Exists(file))
                    {
                        Console.Error.WriteLine($"ned: can't open input file {file}: No such file or directory");
                        return 1;
                    }
                }

                // Second pass: accumulate all content into one buffer.
                var combined = new StringBuilder();
                for (int fi = 0; fi < options.InputFiles.Count; fi++)
                {
                    string file = options.InputFiles[fi];
                    combined.Append(file == "-" ? Console.In.ReadToEnd() : File.ReadAllText(file));
                }

                string result = ProcessInput(combined.ToString(), sedScript, options.NulSeparated);
                Console.Write(result);
            }

            return 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
#if DEBUG
            Console.Error.WriteLine($"ned: {ex}");
#else
            Console.Error.WriteLine($"ned: {ex.Message}");
#endif
            return 1;
        }
    }

    /// <summary>
    /// Process input through the compiled SedScript, handling NUL-separated mode.
    ///
    /// In NUL mode (-z) the engine treats \0 as the record separator instead of \n.
    /// Implementation: replace all \0 with \n via <see cref="string.Replace(char,char)"/>
    /// (zero extra allocation), call Transform() once on the full buffer (preserving shared
    /// state — hold space, line counters, range activation — across records), then restore
    /// \n -> \0 in the output. Trailing \0 is stripped when the original input had none,
    /// matching GNU sed -z behaviour.
    ///
    /// Calling Transform() once is critical: splitting into per-record calls would
    /// reset hold space and line counts between records, breaking multi-line scripts.
    /// </summary>
    private static string ProcessInput(string input, SedScript sedScript, bool nulSeparated)
    {
        if (!nulSeparated)
            return sedScript.Transform(input);

        // Build a \n-normalised copy and record which positions held \0.
        // Most inputs will have no \0 even when -z is active, so check first.
        int firstNul = input.IndexOf('\0');
        if (firstNul < 0)
            return sedScript.Transform(input);

        bool trailingNul = input.Length > 0 && input[^1] == '\0';

        // Normalise: \0 -> \n for processing, then restore \n -> \0 in output.
        // string.Replace(char,char) avoids any extra allocation from StringBuilder.
        string processed = sedScript.Transform(input.Replace('\0', '\n'));

        // Replace ALL \n in the output with \0 (matching GNU sed -z behaviour),
        // then strip the trailing \0 iff the original input had no trailing NUL.
        string result = processed.Replace('\n', '\0');

        // Remove spurious trailing \0 if the original input had no trailing NUL.
        if (!trailingNul && result.Length > 0 && result[^1] == '\0')
            result = result[..^1];

        return result;
    }

    /// <summary>
    /// Parse command line arguments into NedOptions.
    /// Supports GNU sed conventions including -i[suffix] (adjacent, no space).
    ///
    /// Note: -i and -i[suffix] are matched before the combined short-flag loop
    /// because 'i' followed by arbitrary characters would otherwise be misread
    /// as a sequence of single-char flags.
    /// </summary>
    private static NedOptions ParseCommandLine(string[] args)
    {
        var options = new NedOptions();
        bool endOfOptions = false;
        bool scriptPositionalConsumed = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];

            if (endOfOptions)
            {
                options.InputFiles.Add(arg);
                continue;
            }

            if (arg == "--")
            {
                endOfOptions = true;
                continue;
            }

            if (arg == "--help")
            {
                options.ShowHelp = true;
                continue;
            }

            if (arg == "--version")
            {
                options.ShowVersion = true;
                continue;
            }

            if (arg.Length >= 2 && arg[0] == '-')
            {
                // Handle known long-form single-char flags first.
                if (arg == "-n")
                {
                    options.SuppressDefaultOutput = true;
                    continue;
                }
                if (arg == "-E" || arg == "-r")
                {
                    options.UseEre = true;
                    continue;
                }
                if (arg == "-s")
                {
                    options.Separate = true;
                    continue;
                }
                if (arg == "-z")
                {
                    options.NulSeparated = true;
                    continue;
                }
                if (arg == "-e")
                {
                    if (i + 1 >= args.Length)
                        throw new ArgumentException("option requires an argument -- 'e'");
                    options.ScriptParts.Add(args[++i]);
                    continue;
                }
                if (arg == "-f")
                {
                    if (i + 1 >= args.Length)
                        throw new ArgumentException("option requires an argument -- 'f'");
                    string scriptFile = args[++i];
                    if (!File.Exists(scriptFile))
                        throw new FileNotFoundException($"can't read {scriptFile}: No such file or directory");
                    options.ScriptParts.Add(File.ReadAllText(scriptFile));
                    continue;
                }

                // -i[suffix] — suffix is adjacent (GNU convention).
                // "-i" alone means in-place with no backup.
                // "-i.bak" means in-place with .bak backup.
                // Note: "-is" is intentionally treated as -i with suffix "s", not -i combined with -s.
                // This matches GNU sed behaviour where -i is always parsed as -i[suffix] first.
                if (arg == "-i")
                {
                    options.InPlace = true;
                    options.BackupSuffix = null;
                    continue;
                }
                if (arg.Length > 2 && arg[1] == 'i')
                {
                    options.InPlace = true;
                    options.BackupSuffix = arg.Substring(2);
                    continue;
                }

                // Handle combined short flags: -ne 'script', -nE, etc.
                // Walk char by char; 'e' and 'f' consume the next arg.
                for (int ci = 1; ci < arg.Length; ci++)
                {
                    char flag = arg[ci];
                    switch (flag)
                    {
                        case 'n':
                            options.SuppressDefaultOutput = true;
                            break;
                        case 'E':
                        case 'r':
                            options.UseEre = true;
                            break;
                        case 's':
                            options.Separate = true;
                            break;
                        case 'z':
                            options.NulSeparated = true;
                            break;
                        case 'e':
                            // Remainder of this arg is the script, or consume next arg.
                            if (ci + 1 < arg.Length)
                            {
                                options.ScriptParts.Add(arg.Substring(ci + 1));
                                ci = arg.Length; // consumed rest of arg
                            }
                            else
                            {
                                if (i + 1 >= args.Length)
                                    throw new ArgumentException("option requires an argument -- 'e'");
                                options.ScriptParts.Add(args[++i]);
                            }
                            break;
                        case 'f':
                            string scriptFile;
                            if (ci + 1 < arg.Length)
                            {
                                scriptFile = arg.Substring(ci + 1);
                                ci = arg.Length;
                            }
                            else
                            {
                                if (i + 1 >= args.Length)
                                    throw new ArgumentException("option requires an argument -- 'f'");
                                scriptFile = args[++i];
                            }
                            if (!File.Exists(scriptFile))
                                throw new FileNotFoundException($"can't read {scriptFile}: No such file or directory");
                            options.ScriptParts.Add(File.ReadAllText(scriptFile));
                            break;
                        case 'i':
                            // -ni.bak: suffix is rest of this arg after 'i'
                            options.InPlace = true;
                            if (ci + 1 < arg.Length)
                            {
                                options.BackupSuffix = arg.Substring(ci + 1);
                                ci = arg.Length;
                            }
                            else
                            {
                                options.BackupSuffix = null;
                            }
                            break;
                        default:
                            throw new ArgumentException($"Unknown option: -{flag}");
                    }
                }
                continue;
            }

            // Non-flag argument: first one is the script (if not already set via -e/-f),
            // subsequent ones are input files.
            if (options.ScriptParts.Count == 0 && !scriptPositionalConsumed)
            {
                options.ScriptParts.Add(arg);
                scriptPositionalConsumed = true;
            }
            else
            {
                options.InputFiles.Add(arg);
            }
        }

        return options;
    }

    private static void ShowHelp()
    {
        Console.WriteLine("ned - A sed-compatible stream editor for .NET");
        Console.WriteLine();
        Console.WriteLine("Usage: ned [OPTIONS] script [file...]");
        Console.WriteLine("       ned [OPTIONS] -e script [-e script ...] [file...]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -n            Suppress automatic printing of pattern space");
        Console.WriteLine("  -e script     Add script to commands to be executed");
        Console.WriteLine("  -f file       Read script from file");
        Console.WriteLine("  -i[suffix]    Edit files in-place (optional backup suffix, adjacent)");
        Console.WriteLine("  -E, -r        Use Extended Regular Expressions (ERE)");
        Console.WriteLine("  -s            Treat files separately (line numbers reset per file;");
        Console.WriteLine("                use for large multi-file inputs to avoid full concatenation)");
        Console.WriteLine("  -z            Use NUL as record separator instead of newline");
        Console.WriteLine("  --            End of options");
        Console.WriteLine("  --help        Display this help message");
        Console.WriteLine("  --version     Display version information");
    }

    private static void ShowVersion()
    {
        string version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "1.0.0";
        Console.WriteLine($"ned (SedDotNet) {version}");
        Console.WriteLine("A .NET implementation of sed with POSIX compatibility");
    }
}

/// <summary>
/// Parsed command line options for ned.
/// </summary>
public class NedOptions
{
    /// <summary>Ordered list of script text fragments from -e and -f arguments.</summary>
    public List<string> ScriptParts { get; } = new();

    /// <summary>Input file paths (non-option arguments after the positional script).</summary>
    public List<string> InputFiles { get; } = new();

    public bool SuppressDefaultOutput { get; set; }
    public bool InPlace { get; set; }
    public string? BackupSuffix { get; set; }
    public bool UseEre { get; set; }
    public bool Separate { get; set; }
    public bool NulSeparated { get; set; }
    public bool ShowHelp { get; set; }
    public bool ShowVersion { get; set; }
}
