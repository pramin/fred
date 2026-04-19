# ncurl — curl-compatible HTTP client for .NET

## Overview

ncurl is a curl-compatible HTTP client. It follows Fred's compile-once pattern: `CurlEngine.Compile(args)` returns a `CurlScript` with a pre-built `HttpRequestMessage` that can be executed multiple times. The CLI (`ncurl`) is a drop-in replacement for `/usr/bin/curl` for the most common operations.

## Scope — What to implement

Focus on what LLMs actually use curl for: fetching URLs, testing APIs, posting JSON.

### Tier 1 — Must have (oracle-tested)
- **Basic GET**: `curl URL`
- **HTTP methods**: `-X GET|POST|PUT|PATCH|DELETE|HEAD|OPTIONS`
- **Headers**: `-H "Name: Value"` (multiple)
- **Request body**: `-d "data"`, `-d @file`, `--data-raw`, `--data-binary`
- **JSON shorthand**: `--json '{"key":"value"}'` (sets Content-Type + Accept)
- **Output**: `-o file`, `-O` (remote name)
- **Silent/quiet**: `-s`, `-S` (show errors with silent)
- **Include headers in output**: `-i`
- **Head request**: `-I` (HEAD method, show headers only)
- **Follow redirects**: `-L` (with `--max-redirs N`)
- **User-Agent**: `-A "agent"`
- **Timeout**: `--connect-timeout N`, `-m N` / `--max-time N`
- **HTTP auth**: `-u user:password` (Basic), `--bearer token`
- **URL encoding**: `--data-urlencode "key=value"`
- **Form upload**: `-F "field=value"`, `-F "file=@path"`
- **Response code only**: `-w "%{http_code}"` (write-out, common format codes)
- **Insecure**: `-k` / `--insecure` (skip TLS verification)
- **Verbose**: `-v` (show request/response headers on stderr)
- **Compressed**: `--compressed` (Accept-Encoding: gzip, deflate, br)
- **Cookie**: `-b "name=value"`, `-b file`, `-c file` (cookie jar)
- **Retry**: `--retry N`, `--retry-delay N`
- **Proxy**: `-x proxy_url`
- **Certificate**: `--cacert file`

### Exit codes (match curl)
- `0` — success
- `1` — unsupported protocol
- `2` — failed to initialize
- `3` — malformed URL
- `6` — couldn't resolve host
- `7` — couldn't connect
- `22` — HTTP error (with `--fail` / `-f`)
- `28` — timeout
- `35` — TLS/SSL error
- `56` — failure in receiving data

### CLI flags summary
```
ncurl [options] URL [URL...]

Transfer options:
  -X, --request METHOD     HTTP method (default: GET, or POST with -d)
  -H, --header "K: V"     Add request header (repeatable)
  -d, --data DATA          Request body (sets POST if no -X)
  --data-raw DATA          Like -d but don't interpret @
  --data-binary DATA       Like -d but preserve newlines
  --data-urlencode DATA    URL-encode the data
  --json DATA              Shorthand for -d + JSON content type
  -F, --form "key=value"   Multipart form data
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
  -V, --version            Show version
```

## Architecture

```
FredDotNet/
  CurlEngine.cs      # Arg parser, HttpClient wrapper, response formatting

ncurl/
  Program.cs          # CLI: parse args, compile, execute

CurlValidation.Tests/
  CurlOracleTests.cs  # ncurl vs /usr/bin/curl comparison
```

### Engine API

```csharp
public static class CurlEngine
{
    /// <summary>Compile curl arguments into a reusable script.</summary>
    public static CurlScript Compile(string[] args);

    /// <summary>Compile from structured options.</summary>
    public static CurlScript Compile(CurlOptions options);
}

public sealed class CurlScript
{
    /// <summary>Execute the HTTP request, returning (output, exitCode).</summary>
    public (string Output, int ExitCode) Execute();

    /// <summary>Execute with streaming output.</summary>
    public int Execute(TextWriter output, TextWriter? errorOutput = null);

    /// <summary>Execute async.</summary>
    public Task<(string Output, int ExitCode)> ExecuteAsync();
}

public sealed class CurlOptions
{
    public string Url { get; set; } = "";
    public string Method { get; set; } = "GET";
    public List<string> Headers { get; } = new();
    public string? Data { get; set; }
    public bool RawData { get; set; }
    public bool BinaryData { get; set; }
    public string? JsonData { get; set; }
    public List<(string Key, string Value, bool IsFile)> FormFields { get; } = new();
    public string? OutputFile { get; set; }
    public bool UseRemoteName { get; set; }
    public bool IncludeHeaders { get; set; }
    public bool HeadOnly { get; set; }
    public bool Silent { get; set; }
    public bool ShowError { get; set; }
    public bool Verbose { get; set; }
    public bool FollowRedirects { get; set; }
    public int MaxRedirects { get; set; } = 50;
    public int MaxTimeSeconds { get; set; }
    public int ConnectTimeoutSeconds { get; set; }
    public bool Insecure { get; set; }
    public string? UserAgent { get; set; }
    public string? BasicAuth { get; set; }
    public string? BearerToken { get; set; }
    public bool Compressed { get; set; }
    public bool FailOnError { get; set; }
    public string? WriteOutFormat { get; set; }
    public string? CookieString { get; set; }
    public string? CookieFile { get; set; }
    public string? CookieJar { get; set; }
    public string? Proxy { get; set; }
    public int Retry { get; set; }
    public int RetryDelay { get; set; }
    public string? Referer { get; set; }
    public string? DumpHeaderFile { get; set; }
    public List<string> DataUrlencode { get; } = new();
}

public sealed class CurlException : Exception
{
    public CurlException(string message) : base(message) { }
    public CurlException(string message, int exitCode) : base(message) { ExitCode = exitCode; }
    public int ExitCode { get; }
}
```

### Internal design

- Uses `HttpClient` (singleton, pooled connections)
- `HttpClientHandler` for proxy, TLS, cookies, redirects, compression
- Arg parsing is character-by-character (no regex), matching curl's actual parsing
- Thread-safe: `CurlScript` holds an immutable request template; fresh `HttpRequestMessage` per `Execute()`
- `--write-out` format string parsed at compile time, evaluated at execution time
- Cookie jar I/O uses Netscape cookie file format (same as curl)
- No LINQ on hot paths

### Response formatting

```
-i output:
HTTP/1.1 200 OK
Content-Type: application/json
Content-Length: 42

{"result": "data"}

-v output (stderr):
> GET /api HTTP/1.1
> Host: example.com
> User-Agent: ncurl/1.0
>
< HTTP/1.1 200 OK
< Content-Type: application/json
<

-w "%{http_code}" output:
200
```

## Oracle testing

Oracle tests for curl are trickier — they need a real HTTP server. Use a local test server.

```csharp
[TestFixture]
[Parallelizable(ParallelScope.Children)]
public class CurlOracleTests
{
    private const string CurlPath = "/usr/bin/curl";
    private string _ncurlBin = string.Empty;
    private HttpListener _server = null!;
    private string _baseUrl = "";

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        // Build ncurl once
        // Start a local HTTP test server on a random port
        _server = new HttpListener();
        _server.Prefixes.Add("http://localhost:{port}/");
        _server.Start();
        // Background task to handle requests with known responses
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _server.Stop();
    }

    private (string Output, int ExitCode) RunCurl(params string[] args)
    {
        var curlResult = RunProcess(CurlPath, args);
        var ncurlResult = RunProcess(_ncurlBin, args);
        Assert.That(ncurlResult.ExitCode, Is.EqualTo(curlResult.ExitCode));
        // Compare output (may need to normalize headers/dates)
        return curlResult;
    }
}
```

### Test categories (target: 100+ tests)
1. **Basic GET** (~10): simple URL, HTTPS, query params, different paths
2. **HTTP methods** (~10): POST, PUT, PATCH, DELETE, HEAD, OPTIONS
3. **Headers** (~10): custom headers, multiple -H, Content-Type
4. **Request body** (~10): -d string, -d @file, --json, --data-urlencode
5. **Output control** (~10): -o file, -i, -I, -s, -w format
6. **Redirects** (~5): -L, --max-redirs, redirect chains
7. **Auth** (~5): -u basic, --bearer token
8. **TLS/connection** (~5): -k, --connect-timeout, --max-time
9. **Forms** (~5): -F field=value, -F file=@path, multipart
10. **Cookies** (~5): -b send, -c jar
11. **Retry** (~5): --retry on 5xx, --retry-delay
12. **Verbose** (~5): -v output format on stderr
13. **Compressed** (~5): --compressed with gzip response
14. **Error handling** (~10): bad URL, timeout, connection refused, -f on 4xx
15. **Edge cases** (~5): empty body, binary response, large response, unicode
