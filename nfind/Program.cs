using FredDotNet;

namespace nfind;

/// <summary>
/// nfind - A find-compatible file search tool for .NET
///
/// Supports:
///   -name, -iname, -path, -ipath (glob matching)
///   -type f/d/l (file type filtering)
///   -size +/-N with suffixes c,k,M,G
///   -mtime, -mmin, -newer (time-based)
///   -empty (empty files/directories)
///   -maxdepth N, -mindepth N (depth control)
///   -not/!, -and/-a, -or/-o, ( ) (logical operators)
///   -print, -print0 (output modes)
///   -prune (skip directories)
///   Exit codes: 0=found matches, 1=no matches (but no errors), 2=error
/// </summary>
public class Program
{
    public static int Main(string[] args)
    {
        try
        {
            var script = FindEngine.Compile(args);
            int count = script.Execute(Console.Out);
            return 0;
        }
        catch (FindException ex)
        {
            Console.Error.WriteLine($"nfind: {ex.Message}");
            return 1;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            Console.Error.WriteLine($"nfind: {ex.Message}");
            return 1;
        }
    }
}
