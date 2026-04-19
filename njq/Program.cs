using FredDotNet;

namespace njq;

/// <summary>
/// njq - A jq-compatible JSON processor for .NET.
///
/// Usage: njq [OPTIONS] FILTER [FILE...]
///
/// Options:
///   -r, --raw-output     Output raw strings (no quotes)
///   -R, --raw-input      Read each line as a JSON string
///   -n, --null-input     Don't read input, use null
///   -c, --compact-output Compact output (no pretty printing)
///   -e, --exit-status    Exit 1 if last output is false/null
///   -s, --slurp          Read all inputs into an array
///   -S, --sort-keys      Sort object keys in output
///   -j, --join-output    Like -r but no trailing newline
///   --tab                Use tabs for indentation
///   --indent N           Set indentation level (default: 2)
///   --arg name value     Bind $name to string value
///   --argjson name value Bind $name to JSON value
///   --args               Remaining args are string values
///   --jsonargs           Remaining args are JSON values
///
/// Exit codes:
///   0  Success
///   1  Last output was false/null (with -e)
///   2  Usage error
///   5  System error
/// </summary>
public class Program
{
    /// <summary>Entry point for the njq CLI tool.</summary>
    public static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (JqException ex)
        {
            Console.Error.WriteLine($"jq: error: {ex.Message}");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"jq: error: {ex.Message}");
            return 5;
        }
    }

    private static int Run(string[] args)
    {
        var options = new JqOptions();
        string? filter = null;
        var inputFiles = new List<string>();
        bool argsMode = false;
        bool jsonArgsMode = false;
        var positionalArgs = new List<string>();

        int i = 0;
        while (i < args.Length)
        {
            string arg = args[i];

            if (argsMode)
            {
                positionalArgs.Add(arg);
                i++;
                continue;
            }
            if (jsonArgsMode)
            {
                positionalArgs.Add(arg);
                i++;
                continue;
            }

            if (arg == "--")
            {
                i++;
                break;
            }

            if (arg == "--args")
            {
                argsMode = true;
                i++;
                continue;
            }

            if (arg == "--jsonargs")
            {
                jsonArgsMode = true;
                i++;
                continue;
            }

            if (arg == "-r" || arg == "--raw-output")
            {
                options.RawOutput = true;
            }
            else if (arg == "-R" || arg == "--raw-input")
            {
                options.RawInput = true;
            }
            else if (arg == "-n" || arg == "--null-input")
            {
                options.NullInput = true;
            }
            else if (arg == "-c" || arg == "--compact-output")
            {
                options.CompactOutput = true;
            }
            else if (arg == "-e" || arg == "--exit-status")
            {
                options.ExitStatus = true;
            }
            else if (arg == "-s" || arg == "--slurp")
            {
                options.Slurp = true;
            }
            else if (arg == "-S" || arg == "--sort-keys")
            {
                options.SortKeys = true;
            }
            else if (arg == "-j" || arg == "--join-output")
            {
                options.JoinOutput = true;
                options.RawOutput = true;
            }
            else if (arg == "--tab")
            {
                options.UseTabs = true;
            }
            else if (arg == "--indent")
            {
                i++;
                if (i >= args.Length) { Console.Error.WriteLine("jq: --indent requires an argument"); return 2; }
                if (!int.TryParse(args[i], out int indent)) { Console.Error.WriteLine("jq: invalid --indent value"); return 2; }
                options.IndentWidth = indent;
            }
            else if (arg == "--arg")
            {
                if (i + 2 >= args.Length) { Console.Error.WriteLine("jq: --arg requires name and value"); return 2; }
                options.StringArgs[args[i + 1]] = args[i + 2];
                i += 2;
            }
            else if (arg == "--argjson")
            {
                if (i + 2 >= args.Length) { Console.Error.WriteLine("jq: --argjson requires name and value"); return 2; }
                options.JsonArgs[args[i + 1]] = args[i + 2];
                i += 2;
            }
            else if (arg.StartsWith("-") && arg.Length > 1 && filter == null)
            {
                // Handle combined flags like -rc
                bool allValid = true;
                for (int ci = 1; ci < arg.Length; ci++)
                {
                    switch (arg[ci])
                    {
                        case 'r': options.RawOutput = true; break;
                        case 'R': options.RawInput = true; break;
                        case 'n': options.NullInput = true; break;
                        case 'c': options.CompactOutput = true; break;
                        case 'e': options.ExitStatus = true; break;
                        case 's': options.Slurp = true; break;
                        case 'S': options.SortKeys = true; break;
                        case 'j': options.JoinOutput = true; options.RawOutput = true; break;
                        default: allValid = false; break;
                    }
                }
                if (!allValid)
                {
                    // Treat as filter expression
                    filter = arg;
                }
            }
            else if (filter == null)
            {
                filter = arg;
            }
            else
            {
                inputFiles.Add(arg);
            }

            i++;
        }

        // Remaining args after --
        while (i < args.Length)
            inputFiles.Add(args[i++]);

        // Handle --args and --jsonargs positional arguments
        if (argsMode)
        {
            // Bind as $ARGS.positional
            // Actually, --args makes remaining args available as $ARGS
            for (int ai = 0; ai < positionalArgs.Count; ai++)
                options.StringArgs["ARGS_" + ai] = positionalArgs[ai];
        }
        if (jsonArgsMode)
        {
            for (int ai = 0; ai < positionalArgs.Count; ai++)
                options.JsonArgs["ARGS_" + ai] = positionalArgs[ai];
        }

        if (filter == null)
        {
            Console.Error.WriteLine("jq - commandline JSON processor");
            Console.Error.WriteLine("Usage: njq [OPTIONS] FILTER [FILE...]");
            return 2;
        }

        JqScript script;
        try
        {
            script = JqEngine.Compile(filter);
        }
        catch (JqException ex)
        {
            Console.Error.WriteLine($"jq: error: {ex.Message}");
            return 2;
        }

        string jsonInput;
        if (inputFiles.Count > 0)
        {
            var sb = new System.Text.StringBuilder();
            for (int fi = 0; fi < inputFiles.Count; fi++)
            {
                try
                {
                    sb.Append(File.ReadAllText(inputFiles[fi]));
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"jq: error: {ex.Message}");
                    return 5;
                }
            }
            jsonInput = sb.ToString();
        }
        else if (options.NullInput)
        {
            jsonInput = "";
        }
        else if (Console.IsInputRedirected)
        {
            jsonInput = Console.In.ReadToEnd();
        }
        else
        {
            jsonInput = "";
            options.NullInput = true;
        }

        var (output, exitCode) = script.Execute(jsonInput, options);
        if (output.Length > 0)
            Console.Write(output);
        return exitCode;
    }
}
