using FredDotNet;

namespace ncurl;

/// <summary>
/// ncurl - A curl-compatible HTTP client for .NET.
///
/// Supports:
///   GET, POST, PUT, PATCH, DELETE, HEAD, OPTIONS
///   -H, -d, --json, -F, -u, --bearer
///   -o, -O, -i, -I, -s, -S, -v, -w, -D
///   -L, --max-redirs, -m, --connect-timeout
///   -k, --compressed, -f, -b, -c
///   --retry, --retry-delay, -x, -A, -e
///   Exit codes match curl conventions.
/// </summary>
public class Program
{
    /// <summary>Entry point for ncurl CLI.</summary>
    public static int Main(string[] args)
    {
        // Handle --help and --version before parsing
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--help" || args[i] == "-h")
            {
                PrintHelp();
                return 0;
            }
            if (args[i] == "--version" || args[i] == "-V")
            {
                Console.WriteLine("ncurl 1.0.0 (.NET)");
                return 0;
            }
        }

        if (args.Length == 0)
        {
            Console.Error.WriteLine("ncurl: try 'ncurl --help' for more information");
            return CurlExitCodes.FailedInit;
        }

        try
        {
            var script = CurlEngine.Compile(args);
            return script.Execute(Console.Out, Console.Error);
        }
        catch (CurlException ex)
        {
            Console.Error.WriteLine($"ncurl: {ex.Message}");
            return ex.ExitCode;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            Console.Error.WriteLine($"ncurl: {ex.Message}");
            return 1;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine(@"Usage: ncurl [options] URL

Transfer options:
  -X, --request METHOD     HTTP method (default: GET, or POST with -d)
  -H, --header ""K: V""     Add request header (repeatable)
  -d, --data DATA          Request body (sets POST if no -X)
  --data-raw DATA          Like -d but don't interpret @
  --data-binary DATA       Like -d but preserve newlines
  --data-urlencode DATA    URL-encode the data
  --json DATA              Shorthand for -d + JSON content type
  -F, --form ""key=value""   Multipart form data
  -u, --user USER:PASS     Basic auth credentials
  --bearer TOKEN           Bearer token auth

Output options:
  -o, --output FILE        Write output to file
  -O, --remote-name        Save with remote filename
  -i, --include            Include response headers in output
  -I, --head               Show headers only (HEAD request)
  -s, --silent             Silent mode
  -S, --show-error         Show errors even in silent mode
  -v, --verbose            Show request/response details on stderr
  -w, --write-out FORMAT   Output format string after transfer
  -D, --dump-header FILE   Write response headers to file

Connection options:
  -L, --location           Follow redirects
  --max-redirs N           Max redirect follows (default: 50)
  -m, --max-time SECS      Maximum total time
  --connect-timeout SECS   Connection timeout
  -k, --insecure           Skip TLS verification
  -x, --proxy URL          Use proxy
  --cacert FILE            CA certificate bundle
  --compressed             Request compressed response
  --retry N                Retry on transient failure
  --retry-delay SECS       Delay between retries
  -f, --fail               Fail silently on HTTP errors (exit 22)

Cookie options:
  -b, --cookie DATA        Send cookies (string or file)
  -c, --cookie-jar FILE    Save cookies to file

Misc:
  -A, --user-agent STRING  User-Agent header
  -e, --referer URL        Referer header
  --url URL                Explicit URL (alternative to positional)
  -h, --help               Show help
  -V, --version            Show version");
    }
}
