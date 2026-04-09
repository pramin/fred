using FredDotNet;

namespace fred;

/// <summary>
/// fred - Unified CLI tool combining find, grep, sed, and awk.
///
/// Usage modes:
///   fred . -name "*.cs" -type f                          # Find only (like nfind)
///   fred . -name "*.cs" --grep "TODO"                    # Find + grep file contents
///   fred . -name "*.cs" -containing "TODO"               # Same as --grep
///   fred . -name "*.cs" --grep -i "ERROR"                # Find + grep with flags
///   fred . -name "*.cs" --sed 's/TODO/DONE/g'            # Find + sed transform
///   fred . -name "*.cs" --grep "TODO" --sed 's/TODO/DONE/g'  # Find + grep + sed
///   fred . -name "*.csv" -type f --awk -F, '{print $2}'  # Find + awk
///   echo "hello" | fred --sed 's/hello/world/'           # Stdin pipeline mode
///   fred . -name "*.cs" --sed -i 's/old/new/g'           # In-place edit
///   fred . -name "*.cs" --sed -i.bak 's/old/new/g'       # In-place with backup
///   fred . -name "*.cs" --sed --dry-run 's/old/new/g'    # Dry-run diff
///   fred . -name "*.cs" --json                           # JSON output
/// </summary>
public class Program
{
    public static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            Console.Error.WriteLine($"fred: {ex.Message}");
            return 2;
        }
    }

    internal static int Run(string[] args)
    {
        // Parse the arguments into phases
        var parsed = ParsePhases(args);

        bool hasFindArgs = parsed.FindArgs.Count > 0;
        bool hasGrep = parsed.GrepArgs != null;
        bool hasSed = parsed.SedScript != null;
        bool hasAwk = parsed.AwkProgram != null;
        bool isStdinMode = !hasFindArgs && !Console.IsInputRedirected ? false :
                           !hasFindArgs && Console.IsInputRedirected;

        if (!hasFindArgs && !isStdinMode && !hasGrep && !hasSed && !hasAwk)
        {
            Console.Error.WriteLine("fred: no arguments given");
            Console.Error.WriteLine("Usage: fred [path...] [find-options] [--grep PATTERN] [--sed SCRIPT] [--awk PROGRAM]");
            return 2;
        }

        // Stdin pipeline mode: no find args, stdin is piped
        if (isStdinMode)
        {
            return RunStdinPipeline(parsed.GrepArgs, parsed.SedScript, parsed.AwkProgram, parsed.AwkFieldSep);
        }

        // File-based mode: find files, then process
        var findArgsArray = new string[parsed.FindArgs.Count];
        for (int i = 0; i < parsed.FindArgs.Count; i++)
            findArgsArray[i] = parsed.FindArgs[i];

        var script = FindEngine.Compile(findArgsArray);
        var foundPaths = script.Execute();

        // Find-only mode
        if (!hasGrep && !hasSed && !hasAwk)
        {
            if (parsed.JsonOutput)
            {
                var result = new FredResult
                {
                    FilesSearched = foundPaths.Count,
                    FilesMatched = foundPaths.Count,
                };
                for (int i = 0; i < foundPaths.Count; i++)
                {
                    result.Matches.Add(new FredFileMatch { File = foundPaths[i] });
                }
                Console.WriteLine(result.ToJson());
                return foundPaths.Count > 0 ? 0 : 1;
            }

            for (int i = 0; i < foundPaths.Count; i++)
                Console.WriteLine(foundPaths[i]);
            return foundPaths.Count > 0 ? 0 : 1;
        }

        // Process files with grep/sed/awk
        return ProcessFiles(foundPaths, parsed);
    }

    private static int ProcessFiles(List<string> foundPaths, ParsedArgs parsed)
    {
        // Compile grep if needed
        GrepScript? grepScript = null;
        GrepScript? grepScriptForJson = null;
        if (parsed.GrepArgs != null)
        {
            var grepOptions = ParseGrepArgs(parsed.GrepArgs);
            grepScript = GrepEngine.Compile(grepOptions);

            // When JSON output is active, compile a second grep script
            // with line numbers enabled so we can extract actual line numbers
            if (parsed.JsonOutput)
            {
                var jsonGrepOptions = ParseGrepArgs(parsed.GrepArgs);
                jsonGrepOptions.LineNumbers = true;
                jsonGrepOptions.SuppressFilename = true;
                jsonGrepOptions.ForceFilename = false;
                grepScriptForJson = GrepEngine.Compile(jsonGrepOptions);
            }
        }

        // Compile sed if needed
        SedScript? compiledSed = null;
        if (parsed.SedScript != null)
        {
            compiledSed = SedParser.Parse(parsed.SedScript);
        }

        // Compile awk if needed
        AwkScript? compiledAwk = null;
        if (parsed.AwkProgram != null)
        {
            compiledAwk = AwkEngine.Compile(parsed.AwkProgram);
        }

        bool anyOutput = false;
        int filesModified = 0;
        int totalReplacements = 0;
        int filesSearched = 0;
        int filesMatched = 0;

        FredResult? jsonResult = parsed.JsonOutput ? new FredResult() : null;

        // Process each found file
        for (int fi = 0; fi < foundPaths.Count; fi++)
        {
            string filePath = foundPaths[fi];

            // Skip directories for content operations
            if (Directory.Exists(filePath) && !File.Exists(filePath))
                continue;

            if (!File.Exists(filePath))
                continue;

            filesSearched++;

            string content;
            try
            {
                content = File.ReadAllText(filePath);
            }
            catch (Exception)
            {
                continue;
            }

            string? textToTransform = null;

            if (grepScript != null)
            {
                // Grep the file contents
                var grepOutput = new StringWriter();
                int grepResult = grepScript.Execute(new StringReader(content), grepOutput, filePath);

                if (grepResult != 0)
                    continue; // No match in this file

                filesMatched++;
                string grepText = grepOutput.ToString();

                if (compiledSed != null && (parsed.InPlace || parsed.DryRun))
                {
                    // For in-place/dry-run with grep filter: apply sed to full file content
                    // The grep just filters which files to process
                    textToTransform = content;
                }
                else if (compiledSed != null)
                {
                    // Apply sed to grep output (stdout mode)
                    string transformed = compiledSed.Transform(grepText);
                    if (jsonResult != null)
                    {
                        // Get grep output with line numbers for JSON
                        string grepTextWithLines = RunGrepForJson(grepScriptForJson!, content);
                        AddJsonGrepSedMatches(jsonResult, filePath, grepTextWithLines, compiledSed);
                    }
                    else
                    {
                        Console.Write(transformed);
                    }
                    anyOutput = true;
                }
                else if (compiledAwk != null)
                {
                    var (result, _) = compiledAwk.Execute(grepText, parsed.AwkFieldSep);
                    if (jsonResult != null)
                    {
                        string grepTextWithLines = RunGrepForJson(grepScriptForJson!, content);
                        AddJsonGrepMatches(jsonResult, filePath, grepTextWithLines);
                    }
                    else
                    {
                        Console.Write(result);
                    }
                    anyOutput = true;
                }
                else
                {
                    // Just grep output
                    if (jsonResult != null)
                    {
                        string grepTextWithLines = RunGrepForJson(grepScriptForJson!, content);
                        AddJsonGrepMatches(jsonResult, filePath, grepTextWithLines);
                    }
                    else
                    {
                        Console.Write(grepText);
                    }
                    anyOutput = true;
                }
            }
            else if (compiledSed != null)
            {
                filesMatched++;
                textToTransform = content;
            }
            else if (compiledAwk != null)
            {
                filesMatched++;
                var (result, _) = compiledAwk.Execute(content, parsed.AwkFieldSep);
                if (jsonResult != null)
                {
                    AddJsonGrepMatches(jsonResult, filePath, result);
                }
                else
                {
                    Console.Write(result);
                }
                anyOutput = true;
            }

            // Handle sed transform (in-place, dry-run, or stdout)
            if (textToTransform != null && compiledSed != null)
            {
                string transformed = compiledSed.Transform(textToTransform);

                if (parsed.InPlace)
                {
                    if (textToTransform != transformed)
                    {
                        // Create backup if suffix specified
                        if (parsed.BackupSuffix != null)
                        {
                            File.WriteAllText(filePath + parsed.BackupSuffix, textToTransform);
                        }

                        File.WriteAllText(filePath, transformed);
                        filesModified++;
                        int changes = UnifiedDiff.CountChangedLines(textToTransform, transformed);
                        totalReplacements += changes;

                        if (jsonResult != null)
                        {
                            AddJsonSedMatches(jsonResult, filePath, textToTransform, transformed);
                        }
                    }
                    anyOutput = true;
                }
                else if (parsed.DryRun)
                {
                    string diff = UnifiedDiff.Generate(textToTransform, transformed, filePath, filePath);
                    if (diff.Length > 0)
                    {
                        filesModified++;
                        if (jsonResult != null)
                        {
                            AddJsonSedMatches(jsonResult, filePath, textToTransform, transformed);
                        }
                        else
                        {
                            Console.Write(diff);
                        }
                    }
                    anyOutput = true;
                }
                else
                {
                    // stdout mode
                    if (jsonResult != null)
                    {
                        AddJsonSedMatches(jsonResult, filePath, textToTransform, transformed);
                    }
                    else
                    {
                        Console.Write(transformed);
                    }
                    anyOutput = true;
                }
            }
        }

        // Write summary to stderr for in-place and dry-run modes
        if (parsed.InPlace && !parsed.JsonOutput)
        {
            Console.Error.WriteLine($"fred: modified {filesModified} files ({totalReplacements} replacements)");
        }
        else if (parsed.DryRun && !parsed.JsonOutput)
        {
            Console.Error.WriteLine($"fred: would modify {filesModified} files");
        }

        // Write JSON output
        if (jsonResult != null)
        {
            jsonResult.FilesSearched = filesSearched;
            jsonResult.FilesMatched = filesMatched;
            jsonResult.FilesModified = filesModified;
            Console.WriteLine(jsonResult.ToJson());
        }

        return anyOutput ? 0 : 1;
    }

    /// <summary>
    /// Run the JSON-specific grep script (with line numbers) against content
    /// and return the raw output text.
    /// </summary>
    private static string RunGrepForJson(GrepScript grepScript, string content)
    {
        var sw = new StringWriter();
        grepScript.Execute(new StringReader(content), sw);
        return sw.ToString();
    }

    /// <summary>
    /// Add JSON matches from sed output, comparing original and transformed line-by-line.
    /// Only includes lines that changed.
    /// </summary>
    private static void AddJsonSedMatches(FredResult result, string filePath, string original, string transformed)
    {
        var fileMatch = new FredFileMatch { File = filePath };
        string[] origLines = original.Split('\n');
        string[] modLines = transformed.Split('\n');

        for (int i = 0; i < origLines.Length; i++)
        {
            string origLine = origLines[i];
            string? modLine = (i < modLines.Length) ? modLines[i] : null;

            if (modLine != null && origLine != modLine)
            {
                fileMatch.Lines.Add(new FredLineMatch
                {
                    Number = i + 1,
                    Content = origLine,
                    Replacement = modLine,
                });
            }
        }

        if (fileMatch.Lines.Count > 0)
        {
            result.Matches.Add(fileMatch);
        }
    }

    /// <summary>
    /// Add JSON matches from grep output that includes line numbers.
    /// Parses "linenum:content" format from grep with -n flag.
    /// </summary>
    private static void AddJsonGrepMatches(FredResult result, string filePath, string grepOutput)
    {
        var fileMatch = new FredFileMatch { File = filePath };
        string[] lines = grepOutput.Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Length == 0 && i == lines.Length - 1)
                continue; // skip trailing empty line from split

            // Parse "linenum:content" format
            int colonIdx = line.IndexOf(':');
            if (colonIdx > 0 && int.TryParse(line.AsSpan(0, colonIdx), out int lineNum))
            {
                fileMatch.Lines.Add(new FredLineMatch
                {
                    Number = lineNum,
                    Content = line.Substring(colonIdx + 1),
                });
            }
            else
            {
                // Fallback: no line number prefix, use sequential
                fileMatch.Lines.Add(new FredLineMatch
                {
                    Number = i + 1,
                    Content = line,
                });
            }
        }

        if (fileMatch.Lines.Count > 0)
        {
            result.Matches.Add(fileMatch);
        }
    }

    /// <summary>
    /// Add JSON matches from grep+sed: grep output with line numbers,
    /// then apply sed to each matched line to show original and replacement.
    /// </summary>
    private static void AddJsonGrepSedMatches(FredResult result, string filePath, string grepOutput, SedScript sed)
    {
        var fileMatch = new FredFileMatch { File = filePath };
        string[] lines = grepOutput.Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Length == 0 && i == lines.Length - 1)
                continue;

            // Parse "linenum:content" format
            int colonIdx = line.IndexOf(':');
            int lineNum = i + 1;
            string content = line;

            if (colonIdx > 0 && int.TryParse(line.AsSpan(0, colonIdx), out int parsed))
            {
                lineNum = parsed;
                content = line.Substring(colonIdx + 1);
            }

            string transformed = sed.Transform(content + "\n").TrimEnd('\n');

            var match = new FredLineMatch
            {
                Number = lineNum,
                Content = content,
            };
            if (content != transformed)
            {
                match.Replacement = transformed;
            }
            fileMatch.Lines.Add(match);
        }

        if (fileMatch.Lines.Count > 0)
        {
            result.Matches.Add(fileMatch);
        }
    }

    private static int RunStdinPipeline(List<string>? grepArgs, string? sedScript, string? awkProgram, string? awkFieldSep)
    {
        var pipeline = FredPipeline.Create();

        if (grepArgs != null)
        {
            var grepOptions = ParseGrepArgs(grepArgs);
            pipeline.Grep(grepOptions);
        }

        if (sedScript != null)
        {
            pipeline.Sed(sedScript);
        }

        if (awkProgram != null)
        {
            pipeline.Awk(awkProgram, awkFieldSep);
        }

        pipeline.Execute(Console.In, Console.Out);
        return 0;
    }

    internal sealed class ParsedArgs
    {
        public List<string> FindArgs = new();
        public List<string>? GrepArgs;
        public string? SedScript;
        public string? AwkProgram;
        public string? AwkFieldSep;
        public bool InPlace;
        public string? BackupSuffix;
        public bool DryRun;
        public bool JsonOutput;
    }

    /// <summary>
    /// Parse fred arguments into phases: find args, grep args, sed script, awk program,
    /// plus flags for in-place editing, dry-run, and JSON output.
    /// </summary>
    internal static ParsedArgs ParsePhases(string[] args)
    {
        var parsed = new ParsedArgs();

        int i = 0;
        while (i < args.Length)
        {
            string arg = args[i];

            if (arg == "--json")
            {
                parsed.JsonOutput = true;
                i++;
                continue;
            }

            if (arg == "--grep" || arg == "-containing")
            {
                i++;
                parsed.GrepArgs = new List<string>();
                // Collect grep args until next phase marker or end
                while (i < args.Length && args[i] != "--sed" && args[i] != "--awk"
                    && args[i] != "--grep" && args[i] != "-containing" && args[i] != "--json")
                {
                    parsed.GrepArgs.Add(args[i]);
                    i++;
                }
                continue;
            }

            if (arg == "--sed")
            {
                i++;
                // Parse optional -i, -i.suffix, or --dry-run before the script
                while (i < args.Length)
                {
                    if (args[i] == "--dry-run")
                    {
                        parsed.DryRun = true;
                        i++;
                    }
                    else if (args[i] == "-i")
                    {
                        parsed.InPlace = true;
                        i++;
                    }
                    else if (args[i].StartsWith("-i") && args[i].Length > 2 && args[i][1] == 'i')
                    {
                        // -i.bak style: suffix follows immediately
                        parsed.InPlace = true;
                        parsed.BackupSuffix = args[i].Substring(2);
                        i++;
                    }
                    else
                    {
                        break;
                    }
                }

                if (i < args.Length)
                {
                    parsed.SedScript = args[i];
                    i++;
                }
                continue;
            }

            if (arg == "--awk")
            {
                i++;
                // Check for -F field separator
                if (i < args.Length && args[i].StartsWith("-F"))
                {
                    string fArg = args[i];
                    if (fArg.Length > 2)
                    {
                        parsed.AwkFieldSep = fArg.Substring(2);
                    }
                    else
                    {
                        i++;
                        if (i < args.Length)
                            parsed.AwkFieldSep = args[i];
                    }
                    i++;
                }
                if (i < args.Length)
                {
                    parsed.AwkProgram = args[i];
                    i++;
                }
                continue;
            }

            // Otherwise it's a find argument
            parsed.FindArgs.Add(arg);
            i++;
        }

        return parsed;
    }

    private static GrepOptions ParseGrepArgs(List<string> args)
    {
        var options = new GrepOptions();

        int i = 0;
        while (i < args.Count)
        {
            string arg = args[i];

            if (arg.Length >= 2 && arg[0] == '-' && arg[1] != '-')
            {
                for (int ci = 1; ci < arg.Length; ci++)
                {
                    switch (arg[ci])
                    {
                        case 'i': options.IgnoreCase = true; break;
                        case 'v': options.InvertMatch = true; break;
                        case 'n': options.LineNumbers = true; break;
                        case 'c': options.Count = true; break;
                        case 'l': options.FilesWithMatches = true; break;
                        case 'o': options.OnlyMatching = true; break;
                        case 'w': options.WholeWord = true; break;
                        case 'x': options.WholeLine = true; break;
                        case 'E': options.UseERE = true; break;
                        case 'F': options.FixedStrings = true; break;
                        case 'H': options.ForceFilename = true; break;
                        case 'h': options.SuppressFilename = true; break;
                    }
                }
                i++;
                continue;
            }

            // Positional: pattern
            options.Patterns.Add(arg);
            i++;
        }

        return options;
    }
}
