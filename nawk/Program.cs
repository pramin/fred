using FredDotNet;

namespace nawk;

/// <summary>
/// nawk - An AWK-compatible text processing tool for .NET
///
/// Supports:
///   -F fs   Field separator
///   -v var=val  Variable assignment
///   -f progfile  Read program from file
///   'program' [file ...]  AWK program as argument
///   Exit codes: 0=success, 1=error, 2=usage error
/// </summary>
public class Program
{
    public static int Main(string[] args)
    {
        try
        {
            // Read stdin if available
            string? stdinInput = null;
            if (!Console.IsInputRedirected)
            {
                stdinInput = "";
            }
            else
            {
                stdinInput = Console.In.ReadToEnd();
            }

            var (output, exitCode) = ExecuteFromArgs(args, stdinInput);
            if (output.Length > 0)
                Console.Write(output);
            return exitCode;
        }
        catch (AwkException ex)
        {
            Console.Error.WriteLine($"nawk: {ex.Message}");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"nawk: {ex.Message}");
            return 2;
        }
    }

    /// <summary>
    /// Execute AWK from command-line arguments.
    /// Returns (output, exitCode).
    /// </summary>
    private static (string Output, int ExitCode) ExecuteFromArgs(string[] args, string? stdinInput)
    {
        string? program = null;
        string? fieldSeparator = null;
        string? programFile = null;
        var variables = new Dictionary<string, string>();
        var inputFiles = new List<string>();

        int i = 0;
        while (i < args.Length)
        {
            string arg = args[i];
            if (arg == "-F" && i + 1 < args.Length)
            {
                fieldSeparator = args[++i];
            }
            else if (arg.StartsWith("-F") && arg.Length > 2)
            {
                fieldSeparator = arg[2..];
            }
            else if (arg == "-v" && i + 1 < args.Length)
            {
                string varg = args[++i];
                int eq = varg.IndexOf('=');
                if (eq >= 0)
                    variables[varg[..eq]] = varg[(eq + 1)..];
            }
            else if (arg == "-f" && i + 1 < args.Length)
            {
                programFile = args[++i];
            }
            else if (arg == "--")
            {
                i++;
                break;
            }
            else if (arg.StartsWith("-") && arg.Length > 1 && program == null)
            {
                // Unknown flag, skip
                i++;
                continue;
            }
            else if (program == null && programFile == null)
            {
                program = arg;
            }
            else
            {
                inputFiles.Add(arg);
            }
            i++;
        }

        // Remaining args are input files
        while (i < args.Length)
            inputFiles.Add(args[i++]);

        // Load program from file if -f was used
        if (programFile != null)
            program = File.ReadAllText(programFile);

        if (program == null)
            throw new AwkException("No AWK program specified");

        var script = AwkEngine.Compile(program);
        var vars = new Dictionary<string, string>(variables);
        if (fieldSeparator != null)
            vars["FS"] = fieldSeparator;

        if (inputFiles.Count > 0)
        {
            return script.Execute(inputFiles.ToArray(), variables: vars);
        }
        else
        {
            return script.Execute(stdinInput ?? "", variables: vars);
        }
    }
}
