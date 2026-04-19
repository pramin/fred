using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace FredDotNet;

/// <summary>
/// Exit codes matching curl conventions.
/// </summary>
public static class CurlExitCodes
{
    /// <summary>Success.</summary>
    public const int Success = 0;
    /// <summary>Unsupported protocol.</summary>
    public const int UnsupportedProtocol = 1;
    /// <summary>Failed to initialize.</summary>
    public const int FailedInit = 2;
    /// <summary>Malformed URL.</summary>
    public const int MalformedUrl = 3;
    /// <summary>Could not resolve host.</summary>
    public const int CouldNotResolveHost = 6;
    /// <summary>Could not connect.</summary>
    public const int CouldNotConnect = 7;
    /// <summary>HTTP error (with --fail).</summary>
    public const int HttpError = 22;
    /// <summary>Operation timed out.</summary>
    public const int Timeout = 28;
    /// <summary>TLS/SSL error.</summary>
    public const int TlsError = 35;
    /// <summary>Failure in receiving data.</summary>
    public const int RecvError = 56;
}

/// <summary>
/// Exception type for curl errors, carrying an exit code.
/// </summary>
public sealed class CurlException : Exception
{
    /// <summary>Initializes a new CurlException with a message.</summary>
    public CurlException(string message) : base(message) { }

    /// <summary>Initializes a new CurlException with a message and exit code.</summary>
    public CurlException(string message, int exitCode) : base(message) { ExitCode = exitCode; }

    /// <summary>Initializes a new CurlException with a message, exit code, and inner exception.</summary>
    public CurlException(string message, int exitCode, Exception innerException) : base(message, innerException) { ExitCode = exitCode; }

    /// <summary>The curl-compatible exit code.</summary>
    public int ExitCode { get; }
}

/// <summary>
/// Options for a curl request, corresponding to curl command-line flags.
/// </summary>
public sealed class CurlOptions
{
    /// <summary>The target URL.</summary>
    public string Url { get; set; } = "";

    /// <summary>HTTP method (GET, POST, PUT, etc.).</summary>
    public string? Method { get; set; }

    /// <summary>Request headers (-H).</summary>
    public List<string> Headers { get; } = new();

    /// <summary>Request body data (-d).</summary>
    public string? Data { get; set; }

    /// <summary>If true, don't interpret @ in data (--data-raw).</summary>
    public bool RawData { get; set; }

    /// <summary>If true, preserve newlines in data (--data-binary).</summary>
    public bool BinaryData { get; set; }

    /// <summary>JSON data shorthand (--json).</summary>
    public string? JsonData { get; set; }

    /// <summary>Multipart form fields (-F).</summary>
    public List<(string Key, string Value, bool IsFile)> FormFields { get; } = new();

    /// <summary>Output file path (-o).</summary>
    public string? OutputFile { get; set; }

    /// <summary>Use remote filename (-O).</summary>
    public bool UseRemoteName { get; set; }

    /// <summary>Include response headers in output (-i).</summary>
    public bool IncludeHeaders { get; set; }

    /// <summary>HEAD request, show headers only (-I).</summary>
    public bool HeadOnly { get; set; }

    /// <summary>Silent mode (-s).</summary>
    public bool Silent { get; set; }

    /// <summary>Show errors even in silent mode (-S).</summary>
    public bool ShowError { get; set; }

    /// <summary>Verbose output (-v).</summary>
    public bool Verbose { get; set; }

    /// <summary>Follow redirects (-L).</summary>
    public bool FollowRedirects { get; set; }

    /// <summary>Maximum number of redirects (--max-redirs).</summary>
    public int MaxRedirects { get; set; } = 50;

    /// <summary>Maximum total time in seconds (-m / --max-time).</summary>
    public int MaxTimeSeconds { get; set; }

    /// <summary>Connection timeout in seconds (--connect-timeout).</summary>
    public int ConnectTimeoutSeconds { get; set; }

    /// <summary>Skip TLS verification (-k).</summary>
    public bool Insecure { get; set; }

    /// <summary>User-Agent header (-A).</summary>
    public string? UserAgent { get; set; }

    /// <summary>Basic auth credentials (-u user:pass).</summary>
    public string? BasicAuth { get; set; }

    /// <summary>Bearer token auth (--bearer).</summary>
    public string? BearerToken { get; set; }

    /// <summary>Request compressed response (--compressed).</summary>
    public bool Compressed { get; set; }

    /// <summary>Fail silently on HTTP errors (-f).</summary>
    public bool FailOnError { get; set; }

    /// <summary>Write-out format string (-w).</summary>
    public string? WriteOutFormat { get; set; }

    /// <summary>Cookie string to send (-b with string).</summary>
    public string? CookieString { get; set; }

    /// <summary>Cookie file to read (-b with file).</summary>
    public string? CookieFile { get; set; }

    /// <summary>Cookie jar file to save (-c).</summary>
    public string? CookieJar { get; set; }

    /// <summary>Proxy URL (-x).</summary>
    public string? Proxy { get; set; }

    /// <summary>Number of retries (--retry).</summary>
    public int Retry { get; set; }

    /// <summary>Delay between retries in seconds (--retry-delay).</summary>
    public int RetryDelay { get; set; }

    /// <summary>Referer header (-e).</summary>
    public string? Referer { get; set; }

    /// <summary>Dump response headers to file (-D).</summary>
    public string? DumpHeaderFile { get; set; }

    /// <summary>URL-encoded data fields (--data-urlencode).</summary>
    public List<string> DataUrlencode { get; } = new();

    /// <summary>CA certificate file path (--cacert).</summary>
    public string? CaCert { get; set; }

    /// <summary>Whether method was explicitly set via -X.</summary>
    public bool MethodExplicitlySet { get; set; }
}

/// <summary>
/// Compiled curl script. Thread-safe: creates a fresh HttpRequestMessage per Execute() call.
/// </summary>
public sealed class CurlScript
{
    private readonly CurlOptions _options;
    private readonly HttpClient _client;
    private readonly CookieContainer? _cookieContainer;

    internal CurlScript(CurlOptions options, HttpClient client, CookieContainer? cookieContainer)
    {
        _options = options;
        _client = client;
        _cookieContainer = cookieContainer;
    }

    /// <summary>
    /// Execute the HTTP request synchronously, returning output and exit code.
    /// </summary>
    public (string Output, int ExitCode) Execute()
    {
        return ExecuteAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Execute the HTTP request asynchronously, returning output and exit code.
    /// </summary>
    public async Task<(string Output, int ExitCode)> ExecuteAsync()
    {
        using var stdoutWriter = new StringWriter();
        using var stderrWriter = new StringWriter();
        int exitCode = await ExecuteCoreAsync(stdoutWriter, stderrWriter).ConfigureAwait(false);
        return (stdoutWriter.ToString(), exitCode);
    }

    /// <summary>
    /// Execute with streaming output to the provided writers.
    /// </summary>
    public int Execute(TextWriter output, TextWriter? errorOutput = null)
    {
        return ExecuteCoreAsync(output, errorOutput ?? TextWriter.Null).GetAwaiter().GetResult();
    }

    private async Task<int> ExecuteCoreAsync(TextWriter output, TextWriter errorOutput)
    {
        int retryCount = _options.Retry;
        int retryDelay = _options.RetryDelay;
        int attempt = 0;

        while (true)
        {
            attempt++;
            var (exitCode, shouldRetry) = await ExecuteSingleAttemptAsync(output, errorOutput).ConfigureAwait(false);

            if (!shouldRetry || attempt > retryCount)
                return exitCode;

            if (retryDelay > 0)
                await Task.Delay(retryDelay * 1000).ConfigureAwait(false);
        }
    }

    private async Task<(int ExitCode, bool ShouldRetry)> ExecuteSingleAttemptAsync(TextWriter output, TextWriter errorOutput)
    {
        HttpRequestMessage request;
        try
        {
            request = BuildRequest();
        }
        catch (CurlException ex)
        {
            if (!_options.Silent || _options.ShowError)
                await errorOutput.WriteLineAsync($"ncurl: {ex.Message}").ConfigureAwait(false);
            return (ex.ExitCode, false);
        }

        HttpResponseMessage response;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (_options.Verbose)
            {
                await WriteVerboseRequest(request, errorOutput).ConfigureAwait(false);
            }

            if (_options.MaxTimeSeconds > 0)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_options.MaxTimeSeconds));
                response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            }
            else
            {
                response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            }
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException || ex.CancellationToken.IsCancellationRequested)
        {
            if (!_options.Silent || _options.ShowError)
                await errorOutput.WriteLineAsync($"ncurl: (28) Operation timed out").ConfigureAwait(false);
            return (CurlExitCodes.Timeout, true);
        }
        catch (HttpRequestException ex) when (IsConnectionRefused(ex))
        {
            if (!_options.Silent || _options.ShowError)
                await errorOutput.WriteLineAsync($"ncurl: (7) Failed to connect to {GetHostFromUrl(_options.Url)}: Connection refused").ConfigureAwait(false);
            return (CurlExitCodes.CouldNotConnect, true);
        }
        catch (HttpRequestException ex) when (IsDnsError(ex))
        {
            if (!_options.Silent || _options.ShowError)
                await errorOutput.WriteLineAsync($"ncurl: (6) Could not resolve host: {GetHostFromUrl(_options.Url)}").ConfigureAwait(false);
            return (CurlExitCodes.CouldNotResolveHost, true);
        }
        catch (HttpRequestException ex) when (IsTlsError(ex))
        {
            if (!_options.Silent || _options.ShowError)
                await errorOutput.WriteLineAsync($"ncurl: (35) SSL/TLS connection error").ConfigureAwait(false);
            return (CurlExitCodes.TlsError, false);
        }
        catch (HttpRequestException ex)
        {
            if (!_options.Silent || _options.ShowError)
                await errorOutput.WriteLineAsync($"ncurl: (56) Failure in receiving data: {ex.Message}").ConfigureAwait(false);
            return (CurlExitCodes.RecvError, true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            if (!_options.Silent || _options.ShowError)
                await errorOutput.WriteLineAsync($"ncurl: (56) {ex.Message}").ConfigureAwait(false);
            return (CurlExitCodes.RecvError, false);
        }

        stopwatch.Stop();

        using (response)
        {
            if (_options.Verbose)
            {
                await WriteVerboseResponse(response, errorOutput).ConfigureAwait(false);
            }

            int statusCode = (int)response.StatusCode;

            // --fail: exit 22 on 4xx/5xx, suppress body
            if (_options.FailOnError && statusCode >= 400)
            {
                if (!_options.Silent || _options.ShowError)
                    await errorOutput.WriteLineAsync($"ncurl: (22) The requested URL returned error: {statusCode}").ConfigureAwait(false);
                bool retryable = statusCode >= 500;
                return (CurlExitCodes.HttpError, retryable);
            }

            // Write headers to dump file if requested
            if (_options.DumpHeaderFile != null)
            {
                var headerSb = new StringBuilder();
                AppendStatusLine(headerSb, response);
                AppendResponseHeaders(headerSb, response);
                headerSb.Append("\r\n");
                await File.WriteAllTextAsync(_options.DumpHeaderFile, headerSb.ToString()).ConfigureAwait(false);
            }

            // Build output
            var outputSb = new StringBuilder();

            if (_options.IncludeHeaders || _options.HeadOnly)
            {
                AppendStatusLine(outputSb, response);
                AppendResponseHeaders(outputSb, response);
                outputSb.Append("\r\n");
            }

            string bodyText = "";
            long bodySize = 0;

            if (!_options.HeadOnly || _options.MethodExplicitlySet)
            {
                var bodyBytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                bodySize = bodyBytes.Length;
                bodyText = Encoding.UTF8.GetString(bodyBytes);
                if (!_options.HeadOnly)
                {
                    outputSb.Append(bodyText);
                }
            }
            else
            {
                bodySize = 0;
            }

            // Save cookies
            if (_options.CookieJar != null && _cookieContainer != null)
            {
                SaveCookieJar(_options.CookieJar, _cookieContainer, _options.Url);
            }

            // Write main output
            string mainOutput = outputSb.ToString();

            if (_options.OutputFile != null)
            {
                await File.WriteAllTextAsync(_options.OutputFile, mainOutput).ConfigureAwait(false);
            }
            else if (_options.UseRemoteName)
            {
                string remoteName = GetRemoteName(_options.Url);
                await File.WriteAllTextAsync(remoteName, mainOutput).ConfigureAwait(false);
            }
            else
            {
                await output.WriteAsync(mainOutput).ConfigureAwait(false);
            }

            // Write-out format
            if (_options.WriteOutFormat != null)
            {
                string writeOut = FormatWriteOut(_options.WriteOutFormat, response, bodySize, stopwatch.Elapsed);
                await output.WriteAsync(writeOut).ConfigureAwait(false);
            }

            return (CurlExitCodes.Success, false);
        }
    }

    private HttpRequestMessage BuildRequest()
    {
        if (string.IsNullOrEmpty(_options.Url))
            throw new CurlException("no URL specified", CurlExitCodes.MalformedUrl);

        Uri uri;
        try
        {
            string url = _options.Url;
            // If URL has no scheme, default to http://
            if (!url.Contains("://"))
                url = "http://" + url;
            uri = new Uri(url);
        }
        catch (UriFormatException)
        {
            throw new CurlException($"URL rejected: Malformed input to a URL function", CurlExitCodes.MalformedUrl);
        }

        // Determine method
        string method;
        if (_options.Method != null)
        {
            method = _options.Method.ToUpperInvariant();
        }
        else if (_options.HeadOnly)
        {
            method = "HEAD";
        }
        else if (_options.Data != null || _options.JsonData != null || _options.FormFields.Count > 0 || _options.DataUrlencode.Count > 0)
        {
            method = "POST";
        }
        else
        {
            method = "GET";
        }

        var request = new HttpRequestMessage(new HttpMethod(method), uri);

        // Headers
        for (int i = 0; i < _options.Headers.Count; i++)
        {
            string header = _options.Headers[i];
            int colonIdx = header.IndexOf(':');
            if (colonIdx > 0)
            {
                string name = header.Substring(0, colonIdx).Trim();
                string value = header.Substring(colonIdx + 1).Trim();
                // Some headers must be set on content, but we'll try request headers first
                if (!request.Headers.TryAddWithoutValidation(name, value))
                {
                    // Will be set on content if we have content
                }
            }
        }

        // User-Agent
        if (_options.UserAgent != null)
        {
            request.Headers.UserAgent.Clear();
            request.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);
        }

        // Referer
        if (_options.Referer != null)
        {
            request.Headers.Referrer = new Uri(_options.Referer, UriKind.RelativeOrAbsolute);
        }

        // Auth
        if (_options.BasicAuth != null)
        {
            string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(_options.BasicAuth));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", encoded);
        }
        else if (_options.BearerToken != null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.BearerToken);
        }

        // Cookies (when not using CookieContainer for -b with a string)
        if (_options.CookieString != null && _cookieContainer == null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", _options.CookieString);
        }

        // Request body
        if (_options.JsonData != null)
        {
            var jsonContent = new ByteArrayContent(Encoding.UTF8.GetBytes(_options.JsonData));
            jsonContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Content = jsonContent;
            if (!HasHeader(_options.Headers, "Accept"))
            {
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            }
        }
        else if (_options.DataUrlencode.Count > 0)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < _options.DataUrlencode.Count; i++)
            {
                if (i > 0) sb.Append('&');
                string field = _options.DataUrlencode[i];
                int eqIdx = field.IndexOf('=');
                if (eqIdx >= 0)
                {
                    sb.Append(CurlUrlEncode(field.Substring(0, eqIdx)));
                    sb.Append('=');
                    sb.Append(CurlUrlEncode(field.Substring(eqIdx + 1)));
                }
                else
                {
                    sb.Append(CurlUrlEncode(field));
                }
            }
            // Also append -d data if present
            if (_options.Data != null)
            {
                if (sb.Length > 0) sb.Append('&');
                sb.Append(_options.Data);
            }
            var urlContent = new ByteArrayContent(Encoding.UTF8.GetBytes(sb.ToString()));
            urlContent.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
            request.Content = urlContent;
        }
        else if (_options.Data != null)
        {
            string data = _options.Data;
            // Handle @file reference (unless RawData)
            if (!_options.RawData && data.StartsWith('@'))
            {
                string filePath = data.Substring(1);
                if (filePath == "-")
                {
                    data = Console.In.ReadToEnd();
                }
                else if (File.Exists(filePath))
                {
                    data = File.ReadAllText(filePath);
                }
                else
                {
                    throw new CurlException($"can't read file '{filePath}'", CurlExitCodes.FailedInit);
                }
            }

            if (!_options.BinaryData)
            {
                // Standard -d strips newlines
                data = data.Replace("\r\n", "").Replace("\n", "").Replace("\r", "");
            }

            // Check if Content-Type was explicitly set
            string? contentType = GetExplicitContentType(_options.Headers);
            if (contentType != null)
            {
                var explicitContent = new ByteArrayContent(Encoding.UTF8.GetBytes(data));
                explicitContent.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
                request.Content = explicitContent;
            }
            else
            {
                var formContent = new ByteArrayContent(Encoding.UTF8.GetBytes(data));
                formContent.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
                request.Content = formContent;
            }
        }
        else if (_options.FormFields.Count > 0)
        {
            var multipart = new MultipartFormDataContent();
            for (int i = 0; i < _options.FormFields.Count; i++)
            {
                var (key, value, isFile) = _options.FormFields[i];
                if (isFile)
                {
                    if (!File.Exists(value))
                        throw new CurlException($"can't read file '{value}'", CurlExitCodes.FailedInit);
                    var fileBytes = File.ReadAllBytes(value);
                    var fileContent = new ByteArrayContent(fileBytes);
                    fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    multipart.Add(fileContent, key, Path.GetFileName(value));
                }
                else
                {
                    multipart.Add(new StringContent(value), key);
                }
            }
            request.Content = multipart;
        }

        // Set content headers that were specified via -H
        if (request.Content != null)
        {
            for (int i = 0; i < _options.Headers.Count; i++)
            {
                string header = _options.Headers[i];
                int colonIdx = header.IndexOf(':');
                if (colonIdx > 0)
                {
                    string name = header.Substring(0, colonIdx).Trim();
                    string value = header.Substring(colonIdx + 1).Trim();
                    if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase))
                    {
                        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(value);
                    }
                }
            }
        }

        // Compressed
        if (_options.Compressed && !HasHeader(_options.Headers, "Accept-Encoding"))
        {
            request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate, br");
        }

        return request;
    }

    /// <summary>
    /// URL-encode a string using curl's conventions (spaces as +, per application/x-www-form-urlencoded).
    /// </summary>
    private static string CurlUrlEncode(string value)
    {
        // Uri.EscapeDataString encodes spaces as %20; curl uses + for --data-urlencode
        return Uri.EscapeDataString(value).Replace("%20", "+");
    }

        private static bool HasHeader(List<string> headers, string name)
    {
        for (int i = 0; i < headers.Count; i++)
        {
            int colonIdx = headers[i].IndexOf(':');
            if (colonIdx > 0)
            {
                string hName = headers[i].Substring(0, colonIdx).Trim();
                if (string.Equals(hName, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    private static string? GetExplicitContentType(List<string> headers)
    {
        for (int i = 0; i < headers.Count; i++)
        {
            int colonIdx = headers[i].IndexOf(':');
            if (colonIdx > 0)
            {
                string hName = headers[i].Substring(0, colonIdx).Trim();
                if (string.Equals(hName, "Content-Type", StringComparison.OrdinalIgnoreCase))
                    return headers[i].Substring(colonIdx + 1).Trim();
            }
        }
        return null;
    }

    private static void AppendStatusLine(StringBuilder sb, HttpResponseMessage response)
    {
        string protocol = response.Version.Major switch
        {
            2 => "HTTP/2",
            3 => "HTTP/3",
            _ => $"HTTP/{response.Version.Major}.{response.Version.Minor}"
        };
        sb.Append(protocol);
        sb.Append(' ');
        sb.Append((int)response.StatusCode);
        string reasonPhrase = response.ReasonPhrase ?? response.StatusCode.ToString();
        if (!string.IsNullOrEmpty(reasonPhrase))
        {
            sb.Append(' ');
            sb.Append(reasonPhrase);
        }
        sb.Append("\r\n");
    }

    private static void AppendResponseHeaders(StringBuilder sb, HttpResponseMessage response)
    {
        foreach (var header in response.Headers)
        {
            foreach (var val in header.Value)
            {
                sb.Append(header.Key);
                sb.Append(": ");
                sb.Append(val);
                sb.Append("\r\n");
            }
        }
        if (response.Content != null)
        {
            foreach (var header in response.Content.Headers)
            {
                foreach (var val in header.Value)
                {
                    sb.Append(header.Key);
                    sb.Append(": ");
                    sb.Append(val);
                    sb.Append("\r\n");
                }
            }
        }
    }

    private static async Task WriteVerboseRequest(HttpRequestMessage request, TextWriter errorOutput)
    {
        string path = request.RequestUri?.PathAndQuery ?? "/";
        await errorOutput.WriteLineAsync($"> {request.Method} {path} HTTP/1.1").ConfigureAwait(false);
        if (request.RequestUri != null)
            await errorOutput.WriteLineAsync($"> Host: {request.RequestUri.Host}").ConfigureAwait(false);

        foreach (var header in request.Headers)
        {
            foreach (var val in header.Value)
            {
                await errorOutput.WriteLineAsync($"> {header.Key}: {val}").ConfigureAwait(false);
            }
        }

        if (request.Content != null)
        {
            foreach (var header in request.Content.Headers)
            {
                foreach (var val in header.Value)
                {
                    await errorOutput.WriteLineAsync($"> {header.Key}: {val}").ConfigureAwait(false);
                }
            }
        }

        await errorOutput.WriteLineAsync(">").ConfigureAwait(false);
    }

    private static async Task WriteVerboseResponse(HttpResponseMessage response, TextWriter errorOutput)
    {
        string protocol = response.Version.Major switch
        {
            2 => "HTTP/2",
            3 => "HTTP/3",
            _ => $"HTTP/{response.Version.Major}.{response.Version.Minor}"
        };
        string reasonPhrase = response.ReasonPhrase ?? response.StatusCode.ToString();
        await errorOutput.WriteLineAsync($"< {protocol} {(int)response.StatusCode} {reasonPhrase}").ConfigureAwait(false);

        foreach (var header in response.Headers)
        {
            foreach (var val in header.Value)
            {
                await errorOutput.WriteLineAsync($"< {header.Key}: {val}").ConfigureAwait(false);
            }
        }
        if (response.Content != null)
        {
            foreach (var header in response.Content.Headers)
            {
                foreach (var val in header.Value)
                {
                    await errorOutput.WriteLineAsync($"< {header.Key}: {val}").ConfigureAwait(false);
                }
            }
        }
        await errorOutput.WriteLineAsync("<").ConfigureAwait(false);
    }

    private static string FormatWriteOut(string format, HttpResponseMessage response, long downloadSize, TimeSpan elapsed)
    {
        var sb = new StringBuilder(format.Length);
        for (int i = 0; i < format.Length; i++)
        {
            if (format[i] == '%' && i + 1 < format.Length && format[i + 1] == '{')
            {
                int closeIdx = format.IndexOf('}', i + 2);
                if (closeIdx > 0)
                {
                    string varName = format.Substring(i + 2, closeIdx - i - 2);
                    sb.Append(varName switch
                    {
                        "http_code" => ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture),
                        "response_code" => ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture),
                        "size_download" => downloadSize.ToString(CultureInfo.InvariantCulture),
                        "time_total" => elapsed.TotalSeconds.ToString("F6", CultureInfo.InvariantCulture),
                        "url_effective" => response.RequestMessage?.RequestUri?.ToString() ?? "",
                        "content_type" => response.Content?.Headers.ContentType?.ToString() ?? "",
                        "redirect_url" => response.Headers.Location?.ToString() ?? "",
                        _ => $"%{{{varName}}}"
                    });
                    i = closeIdx;
                    continue;
                }
            }
            else if (format[i] == '\\' && i + 1 < format.Length)
            {
                char next = format[i + 1];
                if (next == 'n')
                {
                    sb.Append('\n');
                    i++;
                    continue;
                }
                else if (next == 't')
                {
                    sb.Append('\t');
                    i++;
                    continue;
                }
                else if (next == 'r')
                {
                    sb.Append('\r');
                    i++;
                    continue;
                }
                else if (next == '\\')
                {
                    sb.Append('\\');
                    i++;
                    continue;
                }
            }
            sb.Append(format[i]);
        }
        return sb.ToString();
    }

    private static string GetRemoteName(string url)
    {
        try
        {
            var uri = new Uri(url.Contains("://") ? url : "http://" + url);
            string path = uri.AbsolutePath;
            int lastSlash = path.LastIndexOf('/');
            if (lastSlash >= 0 && lastSlash < path.Length - 1)
                return path.Substring(lastSlash + 1);
            return "index.html";
        }
        catch
        {
            return "index.html";
        }
    }

    private static string GetHostFromUrl(string url)
    {
        try
        {
            var uri = new Uri(url.Contains("://") ? url : "http://" + url);
            return uri.Host;
        }
        catch
        {
            return url;
        }
    }

    private static bool IsConnectionRefused(HttpRequestException ex)
    {
        // Check inner exception for socket errors
        if (ex.InnerException is System.Net.Sockets.SocketException se)
            return se.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionRefused;
        string msg = ex.ToString();
        return msg.Contains("Connection refused", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("ConnectFailure", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDnsError(HttpRequestException ex)
    {
        if (ex.InnerException is System.Net.Sockets.SocketException se)
            return se.SocketErrorCode == System.Net.Sockets.SocketError.HostNotFound;
        string msg = ex.ToString();
        return msg.Contains("Name or service not known", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("No such host", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("NameResolutionFailure", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTlsError(HttpRequestException ex)
    {
        return ex.InnerException is System.Security.Authentication.AuthenticationException;
    }

    private static void SaveCookieJar(string path, CookieContainer container, string url)
    {
        try
        {
            var uri = new Uri(url.Contains("://") ? url : "http://" + url);
            var cookies = container.GetCookies(uri);
            var sb = new StringBuilder();
            sb.AppendLine("# Netscape HTTP Cookie File");
            sb.AppendLine("# https://curl.se/docs/http-cookies.html");
            sb.AppendLine("# This file was generated by ncurl. Edit at your own risk.");
            sb.AppendLine();
            foreach (Cookie cookie in cookies)
            {
                string domain = cookie.Domain.StartsWith('.') ? cookie.Domain : "." + cookie.Domain;
                string includeSubdomains = cookie.Domain.StartsWith('.') ? "TRUE" : "FALSE";
                string secure = cookie.Secure ? "TRUE" : "FALSE";
                long expires = cookie.Expires == DateTime.MinValue ? 0 : new DateTimeOffset(cookie.Expires).ToUnixTimeSeconds();
                sb.Append(domain);
                sb.Append('\t');
                sb.Append(includeSubdomains);
                sb.Append('\t');
                sb.Append(cookie.Path);
                sb.Append('\t');
                sb.Append(secure);
                sb.Append('\t');
                sb.Append(expires);
                sb.Append('\t');
                sb.Append(cookie.Name);
                sb.Append('\t');
                sb.AppendLine(cookie.Value);
            }
            File.WriteAllText(path, sb.ToString());
        }
        catch
        {
            // Best effort cookie saving, like real curl
        }
    }
}

/// <summary>
/// Compiles curl command-line arguments into reusable CurlScript objects.
/// </summary>
public static class CurlEngine
{
    /// <summary>
    /// Compile curl arguments into a reusable script.
    /// </summary>
    /// <param name="args">Command-line arguments matching curl syntax.</param>
    /// <returns>A compiled CurlScript ready for execution.</returns>
    public static CurlScript Compile(string[] args)
    {
        var options = ParseArgs(args);
        return Compile(options);
    }

    /// <summary>
    /// Compile from structured options.
    /// </summary>
    /// <param name="options">Curl options to compile.</param>
    /// <returns>A compiled CurlScript ready for execution.</returns>
    public static CurlScript Compile(CurlOptions options)
    {
        CookieContainer? cookieContainer = null;

        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = options.Compressed
                ? DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
                : DecompressionMethods.None,
            AllowAutoRedirect = options.FollowRedirects,
            MaxAutomaticRedirections = options.MaxRedirects,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };

        if (options.ConnectTimeoutSeconds > 0)
        {
            handler.ConnectTimeout = TimeSpan.FromSeconds(options.ConnectTimeoutSeconds);
        }

        if (options.Insecure)
        {
            handler.SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (sender, cert, chain, errors) => true
            };
        }

        if (options.CaCert != null)
        {
            handler.SslOptions ??= new SslClientAuthenticationOptions();
            handler.SslOptions.RemoteCertificateValidationCallback = (sender, cert, chain, errors) =>
            {
                if (errors == SslPolicyErrors.None) return true;
                // Use custom CA cert
                try
                {
                    var caCert = X509CertificateLoader.LoadCertificateFromFile(options.CaCert);
                    if (chain != null)
                    {
                        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                        chain.ChainPolicy.CustomTrustStore.Add(caCert);
                        return chain.Build((X509Certificate2)cert!);
                    }
                }
                catch { }
                return false;
            };
        }

        if (options.Proxy != null)
        {
            handler.Proxy = new WebProxy(options.Proxy);
            handler.UseProxy = true;
        }

        // Cookie handling
        if (options.CookieJar != null || options.CookieFile != null ||
            (options.CookieString != null && File.Exists(options.CookieString)))
        {
            cookieContainer = new CookieContainer();
            handler.CookieContainer = cookieContainer;
            handler.UseCookies = true;

            // If CookieString looks like a file path, treat it as cookie file
            string? cookieFile = options.CookieFile;
            if (cookieFile == null && options.CookieString != null && File.Exists(options.CookieString))
            {
                cookieFile = options.CookieString;
                // Clear cookie string since we're using the file
                options.CookieString = null;
            }

            if (cookieFile != null && File.Exists(cookieFile))
            {
                LoadCookieFile(cookieContainer, cookieFile, options.Url);
            }
        }
        else if (options.CookieString != null)
        {
            // Simple cookie string — set via header, don't use CookieContainer
        }

        var client = new HttpClient(handler, disposeHandler: true);

        if (options.MaxTimeSeconds > 0)
        {
            client.Timeout = TimeSpan.FromSeconds(options.MaxTimeSeconds + 5); // slightly higher than our manual timeout
        }

        return new CurlScript(options, client, cookieContainer);
    }

    /// <summary>
    /// Parse command-line arguments into CurlOptions.
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>Parsed CurlOptions.</returns>
    public static CurlOptions ParseArgs(string[] args)
    {
        var options = new CurlOptions();
        bool endOfOptions = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];

            if (endOfOptions)
            {
                if (string.IsNullOrEmpty(options.Url))
                    options.Url = arg;
                continue;
            }

            if (arg == "--")
            {
                endOfOptions = true;
                continue;
            }

            // Long options
            if (arg.StartsWith("--"))
            {
                // Handle --option=value form
                string optName;
                string? optValue = null;
                int eqIdx = arg.IndexOf('=');
                if (eqIdx > 0)
                {
                    optName = arg.Substring(2, eqIdx - 2);
                    optValue = arg.Substring(eqIdx + 1);
                }
                else
                {
                    optName = arg.Substring(2);
                }

                switch (optName)
                {
                    case "request":
                        options.Method = optValue ?? GetNextArg(args, ref i);
                        options.MethodExplicitlySet = true;
                        break;
                    case "header":
                        options.Headers.Add(optValue ?? GetNextArg(args, ref i));
                        break;
                    case "data":
                        AppendData(options, optValue ?? GetNextArg(args, ref i));
                        break;
                    case "data-raw":
                        options.RawData = true;
                        AppendData(options, optValue ?? GetNextArg(args, ref i));
                        break;
                    case "data-binary":
                        options.BinaryData = true;
                        AppendData(options, optValue ?? GetNextArg(args, ref i));
                        break;
                    case "data-urlencode":
                        options.DataUrlencode.Add(optValue ?? GetNextArg(args, ref i));
                        break;
                    case "json":
                        options.JsonData = optValue ?? GetNextArg(args, ref i);
                        break;
                    case "form":
                        ParseFormField(options, optValue ?? GetNextArg(args, ref i));
                        break;
                    case "output":
                        options.OutputFile = optValue ?? GetNextArg(args, ref i);
                        break;
                    case "remote-name":
                        options.UseRemoteName = true;
                        break;
                    case "include":
                        options.IncludeHeaders = true;
                        break;
                    case "head":
                        options.HeadOnly = true;
                        break;
                    case "silent":
                        options.Silent = true;
                        break;
                    case "show-error":
                        options.ShowError = true;
                        break;
                    case "verbose":
                        options.Verbose = true;
                        break;
                    case "location":
                        options.FollowRedirects = true;
                        break;
                    case "max-redirs":
                        options.MaxRedirects = int.Parse(optValue ?? GetNextArg(args, ref i), CultureInfo.InvariantCulture);
                        break;
                    case "max-time":
                        options.MaxTimeSeconds = int.Parse(optValue ?? GetNextArg(args, ref i), CultureInfo.InvariantCulture);
                        break;
                    case "connect-timeout":
                        options.ConnectTimeoutSeconds = int.Parse(optValue ?? GetNextArg(args, ref i), CultureInfo.InvariantCulture);
                        break;
                    case "insecure":
                        options.Insecure = true;
                        break;
                    case "user-agent":
                        options.UserAgent = optValue ?? GetNextArg(args, ref i);
                        break;
                    case "user":
                        options.BasicAuth = optValue ?? GetNextArg(args, ref i);
                        break;
                    case "bearer":
                        options.BearerToken = optValue ?? GetNextArg(args, ref i);
                        break;
                    case "compressed":
                        options.Compressed = true;
                        break;
                    case "fail":
                        options.FailOnError = true;
                        break;
                    case "write-out":
                        options.WriteOutFormat = optValue ?? GetNextArg(args, ref i);
                        break;
                    case "cookie":
                        {
                            string val = optValue ?? GetNextArg(args, ref i);
                            // If it looks like a file, use it as cookie file
                            if (File.Exists(val))
                                options.CookieFile = val;
                            else
                                options.CookieString = val;
                        }
                        break;
                    case "cookie-jar":
                        options.CookieJar = optValue ?? GetNextArg(args, ref i);
                        break;
                    case "proxy":
                        options.Proxy = optValue ?? GetNextArg(args, ref i);
                        break;
                    case "retry":
                        options.Retry = int.Parse(optValue ?? GetNextArg(args, ref i), CultureInfo.InvariantCulture);
                        break;
                    case "retry-delay":
                        options.RetryDelay = int.Parse(optValue ?? GetNextArg(args, ref i), CultureInfo.InvariantCulture);
                        break;
                    case "referer":
                        options.Referer = optValue ?? GetNextArg(args, ref i);
                        break;
                    case "dump-header":
                        options.DumpHeaderFile = optValue ?? GetNextArg(args, ref i);
                        break;
                    case "cacert":
                        options.CaCert = optValue ?? GetNextArg(args, ref i);
                        break;
                    case "url":
                        options.Url = optValue ?? GetNextArg(args, ref i);
                        break;
                    case "help":
                    case "version":
                        // Handled at CLI level
                        break;
                    default:
                        // Unknown long option — ignore for compatibility
                        break;
                }
                continue;
            }

            // Short options
            if (arg.Length >= 2 && arg[0] == '-')
            {
                for (int ci = 1; ci < arg.Length; ci++)
                {
                    char flag = arg[ci];
                    switch (flag)
                    {
                        case 'X':
                            options.Method = GetRemainingOrNext(arg, ci, args, ref i);
                            options.MethodExplicitlySet = true;
                            ci = arg.Length;
                            break;
                        case 'H':
                            options.Headers.Add(GetRemainingOrNext(arg, ci, args, ref i));
                            ci = arg.Length;
                            break;
                        case 'd':
                            AppendData(options, GetRemainingOrNext(arg, ci, args, ref i));
                            ci = arg.Length;
                            break;
                        case 'F':
                            ParseFormField(options, GetRemainingOrNext(arg, ci, args, ref i));
                            ci = arg.Length;
                            break;
                        case 'o':
                            options.OutputFile = GetRemainingOrNext(arg, ci, args, ref i);
                            ci = arg.Length;
                            break;
                        case 'O':
                            options.UseRemoteName = true;
                            break;
                        case 'i':
                            options.IncludeHeaders = true;
                            break;
                        case 'I':
                            options.HeadOnly = true;
                            break;
                        case 's':
                            options.Silent = true;
                            break;
                        case 'S':
                            options.ShowError = true;
                            break;
                        case 'v':
                            options.Verbose = true;
                            break;
                        case 'L':
                            options.FollowRedirects = true;
                            break;
                        case 'm':
                            options.MaxTimeSeconds = int.Parse(GetRemainingOrNext(arg, ci, args, ref i), CultureInfo.InvariantCulture);
                            ci = arg.Length;
                            break;
                        case 'k':
                            options.Insecure = true;
                            break;
                        case 'u':
                            options.BasicAuth = GetRemainingOrNext(arg, ci, args, ref i);
                            ci = arg.Length;
                            break;
                        case 'A':
                            options.UserAgent = GetRemainingOrNext(arg, ci, args, ref i);
                            ci = arg.Length;
                            break;
                        case 'e':
                            options.Referer = GetRemainingOrNext(arg, ci, args, ref i);
                            ci = arg.Length;
                            break;
                        case 'f':
                            options.FailOnError = true;
                            break;
                        case 'w':
                            options.WriteOutFormat = GetRemainingOrNext(arg, ci, args, ref i);
                            ci = arg.Length;
                            break;
                        case 'b':
                            {
                                string val = GetRemainingOrNext(arg, ci, args, ref i);
                                if (File.Exists(val))
                                    options.CookieFile = val;
                                else
                                    options.CookieString = val;
                                ci = arg.Length;
                            }
                            break;
                        case 'c':
                            options.CookieJar = GetRemainingOrNext(arg, ci, args, ref i);
                            ci = arg.Length;
                            break;
                        case 'x':
                            options.Proxy = GetRemainingOrNext(arg, ci, args, ref i);
                            ci = arg.Length;
                            break;
                        case 'D':
                            options.DumpHeaderFile = GetRemainingOrNext(arg, ci, args, ref i);
                            ci = arg.Length;
                            break;
                        default:
                            // Unknown short option — ignore for compatibility
                            break;
                    }
                }
                continue;
            }

            // Positional argument — URL
            if (string.IsNullOrEmpty(options.Url))
                options.Url = arg;
        }

        return options;
    }

    private static string GetNextArg(string[] args, ref int i)
    {
        i++;
        if (i >= args.Length)
            throw new CurlException("option requires an argument", CurlExitCodes.FailedInit);
        return args[i];
    }

    private static string GetRemainingOrNext(string arg, int charIndex, string[] args, ref int i)
    {
        if (charIndex + 1 < arg.Length)
            return arg.Substring(charIndex + 1);
        return GetNextArg(args, ref i);
    }

    private static void AppendData(CurlOptions options, string data)
    {
        if (options.Data == null)
        {
            options.Data = data;
        }
        else
        {
            // Multiple -d flags are joined with &
            options.Data = options.Data + "&" + data;
        }
    }

    private static void ParseFormField(CurlOptions options, string field)
    {
        int eqIdx = field.IndexOf('=');
        if (eqIdx < 0)
        {
            options.FormFields.Add((field, "", false));
            return;
        }

        string key = field.Substring(0, eqIdx);
        string value = field.Substring(eqIdx + 1);

        if (value.StartsWith('@'))
        {
            options.FormFields.Add((key, value.Substring(1), true));
        }
        else
        {
            options.FormFields.Add((key, value, false));
        }
    }

    private static void LoadCookieFile(CookieContainer container, string path, string url)
    {
        try
        {
            string[] lines = File.ReadAllLines(path);
            Uri? baseUri = null;
            try
            {
                baseUri = new Uri(url.Contains("://") ? url : "http://" + url);
            }
            catch { return; }

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line[0] == '#')
                    continue;

                // Netscape format: domain\tincludeSubdomains\tpath\tsecure\texpires\tname\tvalue
                string[] parts = line.Split('\t');
                if (parts.Length >= 7)
                {
                    try
                    {
                        var cookie = new Cookie(parts[5], parts[6], parts[2], parts[0].TrimStart('.'));
                        cookie.Secure = parts[3] == "TRUE";
                        container.Add(baseUri, cookie);
                    }
                    catch { }
                }
            }
        }
        catch { }
    }
}
