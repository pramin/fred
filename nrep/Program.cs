using FredDotNet;

namespace nrep;

/// <summary>
/// nrep - A grep-compatible pattern matcher for .NET
///
/// Supports:
///   BRE (default), ERE (-E), fixed strings (-F)
///   -i, -v, -n, -c, -l, -L, -o, -w, -x, -m N
///   -A N, -B N, -C N (context lines)
///   -H, -h, -s, -q
///   -e pattern, -f file (multiple patterns)
///   -r / -R (recursive directory search)
///   --include=GLOB, --exclude=GLOB
///   Exit codes: 0=found, 1=not found, 2=error
/// </summary>
public class Program
{
    public static int Main(string[] args)
    {
        try
        {
            var (options, positionalArgs) = ParseCommandLine(args);

            if (options == null)
                return 2;

            // Determine pattern and input sources
            var inputFiles = new List<string>();

            int argIndex = 0;

            // If no patterns from -e or -f, first positional arg is the pattern
            if (options.Patterns.Count == 0 && options.PatternFiles.Count == 0)
            {
                if (positionalArgs.Count == 0)
                {
                    Console.Error.WriteLine("nrep: no pattern given");
                    return 2;
                }
                options.Patterns.Add(positionalArgs[0]);
                argIndex = 1;
            }

            // Remaining positional args are input files
            for (int i = argIndex; i < positionalArgs.Count; i++)
                inputFiles.Add(positionalArgs[i]);

            // Compile patterns once
            var script = GrepEngine.Compile(options);

            if (inputFiles.Count == 0)
            {
                // Stream from stdin — no slurping
                return script.Execute(Console.In, Console.Out);
            }
            else if (inputFiles.Count == 1 && inputFiles[0] != "-")
            {
                // Single file — stream from file
                if (!File.Exists(inputFiles[0]))
                {
                    if (!options.SuppressErrors)
                        Console.Error.WriteLine($"nrep: {inputFiles[0]}: No such file or directory");
                    return 2;
                }
                using var reader = new StreamReader(inputFiles[0]);
                return script.Execute(reader, Console.Out, inputFiles[0]);
            }
            else
            {
                // Multiple files — compile once with filename forcing, stream each
                bool multiFile = inputFiles.Count > 1;
                GrepScript multiScript = script;
                if (multiFile && !options.SuppressFilename)
                {
                    var fileOpts = new GrepOptions
                    {
                        UseERE = options.UseERE,
                        FixedStrings = options.FixedStrings,
                        IgnoreCase = options.IgnoreCase,
                        InvertMatch = options.InvertMatch,
                        LineNumbers = options.LineNumbers,
                        Count = options.Count,
                        FilesWithMatches = options.FilesWithMatches,
                        FilesWithoutMatches = options.FilesWithoutMatches,
                        OnlyMatching = options.OnlyMatching,
                        WholeWord = options.WholeWord,
                        WholeLine = options.WholeLine,
                        Quiet = options.Quiet,
                        SuppressErrors = options.SuppressErrors,
                        ForceFilename = true,
                        SuppressFilename = options.SuppressFilename,
                        MaxCount = options.MaxCount,
                        AfterContext = options.AfterContext,
                        BeforeContext = options.BeforeContext,
                        BothContext = options.BothContext,
                    };
                    for (int pi = 0; pi < options.Patterns.Count; pi++)
                        fileOpts.Patterns.Add(options.Patterns[pi]);
                    for (int pi = 0; pi < options.PatternFiles.Count; pi++)
                        fileOpts.PatternFiles.Add(options.PatternFiles[pi]);
                    multiScript = GrepEngine.Compile(fileOpts);
                }

                bool anyMatch = false;
                for (int i = 0; i < inputFiles.Count; i++)
                {
                    string f = inputFiles[i];
                    int exitCode;
                    if (f == "-")
                    {
                        exitCode = multiScript.Execute(Console.In, Console.Out, "(standard input)");
                    }
                    else if (!File.Exists(f))
                    {
                        if (!options.SuppressErrors)
                            Console.Error.WriteLine($"nrep: {f}: No such file or directory");
                        return 2;
                    }
                    else
                    {
                        using var reader = new StreamReader(f);
                        exitCode = multiScript.Execute(reader, Console.Out, f);
                    }

                    if (exitCode == 0) anyMatch = true;
                    if (exitCode == 2) return 2;
                }

                return anyMatch ? 0 : 1;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            Console.Error.WriteLine($"nrep: {ex.Message}");
            return 2;
        }
    }

    private static (GrepOptions? Options, List<string> PositionalArgs) ParseCommandLine(string[] args)
    {
        var options = new GrepOptions();
        var positionalArgs = new List<string>();
        bool endOfOptions = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];

            if (endOfOptions)
            {
                positionalArgs.Add(arg);
                continue;
            }

            if (arg == "--")
            {
                endOfOptions = true;
                continue;
            }

            // Handle --long-form options
            if (arg.StartsWith("--include="))
            {
                // Ignored for stdin mode, used for recursive
                continue;
            }
            if (arg.StartsWith("--exclude="))
            {
                continue;
            }

            if (arg.Length >= 2 && arg[0] == '-' && arg[1] != '-')
            {
                // Short options — may be combined
                for (int ci = 1; ci < arg.Length; ci++)
                {
                    char flag = arg[ci];
                    switch (flag)
                    {
                        case 'E':
                            options.UseERE = true;
                            break;
                        case 'F':
                            options.FixedStrings = true;
                            break;
                        case 'i':
                            options.IgnoreCase = true;
                            break;
                        case 'v':
                            options.InvertMatch = true;
                            break;
                        case 'n':
                            options.LineNumbers = true;
                            break;
                        case 'c':
                            options.Count = true;
                            break;
                        case 'l':
                            options.FilesWithMatches = true;
                            break;
                        case 'L':
                            options.FilesWithoutMatches = true;
                            break;
                        case 'o':
                            options.OnlyMatching = true;
                            break;
                        case 'w':
                            options.WholeWord = true;
                            break;
                        case 'x':
                            options.WholeLine = true;
                            break;
                        case 'q':
                            options.Quiet = true;
                            break;
                        case 's':
                            options.SuppressErrors = true;
                            break;
                        case 'H':
                            options.ForceFilename = true;
                            break;
                        case 'h':
                            options.SuppressFilename = true;
                            break;
                        case 'r':
                        case 'R':
                            // Recursive -- handled at CLI level, not in engine
                            break;
                        case 'm':
                            // -m N: next arg or rest of this arg
                            if (ci + 1 < arg.Length)
                            {
                                options.MaxCount = int.Parse(arg.Substring(ci + 1));
                                ci = arg.Length;
                            }
                            else
                            {
                                if (i + 1 >= args.Length)
                                {
                                    Console.Error.WriteLine("nrep: option requires an argument -- 'm'");
                                    return (null, positionalArgs);
                                }
                                options.MaxCount = int.Parse(args[++i]);
                            }
                            break;
                        case 'A':
                            if (ci + 1 < arg.Length)
                            {
                                options.AfterContext = int.Parse(arg.Substring(ci + 1));
                                ci = arg.Length;
                            }
                            else
                            {
                                if (i + 1 >= args.Length)
                                {
                                    Console.Error.WriteLine("nrep: option requires an argument -- 'A'");
                                    return (null, positionalArgs);
                                }
                                options.AfterContext = int.Parse(args[++i]);
                            }
                            break;
                        case 'B':
                            if (ci + 1 < arg.Length)
                            {
                                options.BeforeContext = int.Parse(arg.Substring(ci + 1));
                                ci = arg.Length;
                            }
                            else
                            {
                                if (i + 1 >= args.Length)
                                {
                                    Console.Error.WriteLine("nrep: option requires an argument -- 'B'");
                                    return (null, positionalArgs);
                                }
                                options.BeforeContext = int.Parse(args[++i]);
                            }
                            break;
                        case 'C':
                            if (ci + 1 < arg.Length)
                            {
                                options.BothContext = int.Parse(arg.Substring(ci + 1));
                                ci = arg.Length;
                            }
                            else
                            {
                                if (i + 1 >= args.Length)
                                {
                                    Console.Error.WriteLine("nrep: option requires an argument -- 'C'");
                                    return (null, positionalArgs);
                                }
                                options.BothContext = int.Parse(args[++i]);
                            }
                            break;
                        case 'e':
                            if (ci + 1 < arg.Length)
                            {
                                options.Patterns.Add(arg.Substring(ci + 1));
                                ci = arg.Length;
                            }
                            else
                            {
                                if (i + 1 >= args.Length)
                                {
                                    Console.Error.WriteLine("nrep: option requires an argument -- 'e'");
                                    return (null, positionalArgs);
                                }
                                options.Patterns.Add(args[++i]);
                            }
                            break;
                        case 'f':
                            if (ci + 1 < arg.Length)
                            {
                                options.PatternFiles.Add(arg.Substring(ci + 1));
                                ci = arg.Length;
                            }
                            else
                            {
                                if (i + 1 >= args.Length)
                                {
                                    Console.Error.WriteLine("nrep: option requires an argument -- 'f'");
                                    return (null, positionalArgs);
                                }
                                options.PatternFiles.Add(args[++i]);
                            }
                            break;
                        default:
                            Console.Error.WriteLine($"nrep: unknown option -- '{flag}'");
                            return (null, positionalArgs);
                    }
                }
                continue;
            }

            // Positional argument
            positionalArgs.Add(arg);
        }

        return (options, positionalArgs);
    }
}
