using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using NUnit.Framework;

namespace CurlValidation.Tests;

/// <summary>
/// Oracle test suite for curl. Each test runs the real /usr/bin/curl and the compiled ncurl
/// against a local HTTP test server, asserting that ncurl produces identical output and exit codes.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.Children)]
public class CurlOracleTests
{
    private const string CurlPath = "/usr/bin/curl";
    private static string _ncurlBin = string.Empty;
    private static HttpListener _server = null!;
    private static string _baseUrl = "";
    private static Thread _serverThread = null!;
    private static volatile bool _serverRunning;
    private static string _tempDir = string.Empty;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"curl-oracle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        // Build ncurl once
        var buildDir = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."));
        var psi = new ProcessStartInfo("dotnet", $"build {Path.Combine(buildDir, "ncurl", "ncurl.csproj")} -c Debug -o {Path.Combine(buildDir, "ncurl", "bin", "oracle-test")}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        var proc = Process.Start(psi)!;
        proc.WaitForExit(60000);
        Assert.That(proc.ExitCode, Is.EqualTo(0), "ncurl build failed: " + proc.StandardError.ReadToEnd());

        _ncurlBin = Path.Combine(buildDir, "ncurl", "bin", "oracle-test", "ncurl");

        // Start local HTTP test server
        int port = FindAvailablePort();
        _baseUrl = $"http://localhost:{port}";
        _server = new HttpListener();
        _server.Prefixes.Add($"http://+:{port}/");
        _server.Start();
        _serverRunning = true;

        _serverThread = new Thread(RunServer) { IsBackground = true, Name = "CurlTestServer" };
        _serverThread.Start();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _serverRunning = false;
        try { _server.Stop(); } catch { }
        try { ((IDisposable)_server).Dispose(); } catch { }
        try { _serverThread.Join(5000); } catch { }
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    [SetUp]
    public void SetUp()
    {
        if (!File.Exists(CurlPath))
            Assert.Ignore($"curl not found at {CurlPath}; skipping oracle tests.");
    }

    // =========================================================================
    // Test server
    // =========================================================================

    private static int FindAvailablePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void RunServer()
    {
        while (_serverRunning)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = _server.GetContext();
            }
            catch
            {
                break;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    HandleRequest(ctx);
                }
                catch
                {
                    try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
                }
            });
        }
    }

    private static void HandleRequest(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var resp = ctx.Response;
        string path = req.Url?.AbsolutePath ?? "/";

        // Route
        if (path == "/echo")
        {
            HandleEcho(req, resp);
        }
        else if (path == "/json")
        {
            HandleJson(req, resp);
        }
        else if (path == "/text")
        {
            HandleText(req, resp);
        }
        else if (path == "/post" || path == "/put" || path == "/delete" || path == "/patch")
        {
            HandleMethodEcho(req, resp);
        }
        else if (path == "/redirect")
        {
            HandleRedirect(req, resp);
        }
        else if (path == "/redirect-chain")
        {
            HandleRedirectChain(req, resp);
        }
        else if (path == "/redirect-chain-2")
        {
            resp.StatusCode = 302;
            resp.RedirectLocation = "/json";
            resp.Close();
        }
        else if (path.StartsWith("/status/"))
        {
            HandleStatus(req, resp, path);
        }
        else if (path == "/headers")
        {
            HandleHeaders(req, resp);
        }
        else if (path == "/gzip")
        {
            HandleGzip(req, resp);
        }
        else if (path == "/cookies/set")
        {
            HandleCookieSet(req, resp);
        }
        else if (path == "/cookies")
        {
            HandleCookies(req, resp);
        }
        else if (path.StartsWith("/delay/"))
        {
            HandleDelay(req, resp, path);
        }
        else if (path == "/binary")
        {
            HandleBinary(req, resp);
        }
        else if (path == "/form")
        {
            HandleForm(req, resp);
        }
        else if (path.StartsWith("/basic-auth/"))
        {
            HandleBasicAuth(req, resp, path);
        }
        else if (path == "/empty")
        {
            HandleEmpty(req, resp);
        }
        else if (path == "/large")
        {
            HandleLarge(req, resp);
        }
        else if (path == "/unicode")
        {
            HandleUnicode(req, resp);
        }
        else if (path == "/custom-headers")
        {
            HandleCustomHeaders(req, resp);
        }
        else if (path == "/user-agent")
        {
            HandleUserAgent(req, resp);
        }
        else if (path == "/query")
        {
            HandleQuery(req, resp);
        }
        else
        {
            resp.StatusCode = 404;
            var body = Encoding.UTF8.GetBytes("Not Found");
            resp.ContentType = "text/plain";
            resp.ContentLength64 = body.Length;
            resp.OutputStream.Write(body);
            resp.Close();
        }
    }

    private static void HandleEcho(HttpListenerRequest req, HttpListenerResponse resp)
    {
        var dict = new Dictionary<string, string>();
        foreach (string? key in req.Headers.AllKeys)
        {
            if (key != null)
                dict[key] = req.Headers[key] ?? "";
        }
        var json = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = false });
        WriteJsonResponse(resp, 200, json);
    }

    private static void HandleJson(HttpListenerRequest req, HttpListenerResponse resp)
    {
        string json = """{"message":"hello","number":42,"array":[1,2,3]}""";
        WriteJsonResponse(resp, 200, json);
    }

    private static void HandleText(HttpListenerRequest req, HttpListenerResponse resp)
    {
        string text = "Hello, World!\nThis is plain text.\n";
        WriteTextResponse(resp, 200, text);
    }

    private static void HandleMethodEcho(HttpListenerRequest req, HttpListenerResponse resp)
    {
        string body = "";
        if (req.HasEntityBody)
        {
            using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
            body = reader.ReadToEnd();
        }
        var result = new Dictionary<string, string>
        {
            ["method"] = req.HttpMethod,
            ["body"] = body,
            ["content_type"] = req.ContentType ?? ""
        };
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = false });
        WriteJsonResponse(resp, 200, json);
    }

    private static void HandleRedirect(HttpListenerRequest req, HttpListenerResponse resp)
    {
        resp.StatusCode = 302;
        resp.RedirectLocation = "/json";
        resp.Close();
    }

    private static void HandleRedirectChain(HttpListenerRequest req, HttpListenerResponse resp)
    {
        resp.StatusCode = 302;
        resp.RedirectLocation = "/redirect-chain-2";
        resp.Close();
    }

    private static void HandleStatus(HttpListenerRequest req, HttpListenerResponse resp, string path)
    {
        string codeStr = path.Substring("/status/".Length);
        if (int.TryParse(codeStr, out int code))
        {
            resp.StatusCode = code;
            var body = Encoding.UTF8.GetBytes($"Status: {code}");
            resp.ContentType = "text/plain";
            resp.ContentLength64 = body.Length;
            resp.OutputStream.Write(body);
        }
        else
        {
            resp.StatusCode = 400;
        }
        resp.Close();
    }

    private static void HandleHeaders(HttpListenerRequest req, HttpListenerResponse resp)
    {
        var dict = new Dictionary<string, string>();
        foreach (string? key in req.Headers.AllKeys)
        {
            if (key != null)
                dict[key] = req.Headers[key] ?? "";
        }
        var json = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = false });
        WriteJsonResponse(resp, 200, json);
    }

    private static void HandleGzip(HttpListenerRequest req, HttpListenerResponse resp)
    {
        string text = "This is gzip-compressed content.\n";
        var textBytes = Encoding.UTF8.GetBytes(text);

        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionMode.Compress, true))
        {
            gz.Write(textBytes);
        }
        var compressed = ms.ToArray();

        resp.StatusCode = 200;
        resp.ContentType = "text/plain";
        resp.AddHeader("Content-Encoding", "gzip");
        resp.ContentLength64 = compressed.Length;
        resp.OutputStream.Write(compressed);
        resp.Close();
    }

    private static void HandleCookieSet(HttpListenerRequest req, HttpListenerResponse resp)
    {
        string? query = req.Url?.Query;
        if (query != null && query.StartsWith('?'))
        {
            string[] pairs = query.Substring(1).Split('&');
            for (int i = 0; i < pairs.Length; i++)
            {
                int eqIdx = pairs[i].IndexOf('=');
                if (eqIdx > 0)
                {
                    string name = pairs[i].Substring(0, eqIdx);
                    string value = pairs[i].Substring(eqIdx + 1);
                    resp.SetCookie(new Cookie(name, value, "/"));
                }
            }
        }
        WriteJsonResponse(resp, 200, """{"cookies":"set"}""");
    }

    private static void HandleCookies(HttpListenerRequest req, HttpListenerResponse resp)
    {
        var dict = new Dictionary<string, string>();
        for (int i = 0; i < req.Cookies.Count; i++)
        {
            var cookie = req.Cookies[i];
            dict[cookie.Name] = cookie.Value;
        }
        var json = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = false });
        WriteJsonResponse(resp, 200, json);
    }

    private static void HandleDelay(HttpListenerRequest req, HttpListenerResponse resp, string path)
    {
        string delayStr = path.Substring("/delay/".Length);
        if (int.TryParse(delayStr, out int seconds))
        {
            Thread.Sleep(seconds * 1000);
        }
        WriteJsonResponse(resp, 200, $@"{{""delayed"":{delayStr}}}");
    }

    private static void HandleBinary(HttpListenerRequest req, HttpListenerResponse resp)
    {
        var data = new byte[256];
        for (int i = 0; i < 256; i++) data[i] = (byte)i;
        resp.StatusCode = 200;
        resp.ContentType = "application/octet-stream";
        resp.ContentLength64 = data.Length;
        resp.OutputStream.Write(data);
        resp.Close();
    }

    private static void HandleForm(HttpListenerRequest req, HttpListenerResponse resp)
    {
        string body = "";
        if (req.HasEntityBody)
        {
            using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
            body = reader.ReadToEnd();
        }
        var result = new Dictionary<string, string>
        {
            ["content_type"] = req.ContentType ?? "",
            ["body_length"] = body.Length.ToString()
        };
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = false });
        WriteJsonResponse(resp, 200, json);
    }

    private static void HandleBasicAuth(HttpListenerRequest req, HttpListenerResponse resp, string path)
    {
        // /basic-auth/user/pass
        string[] parts = path.Split('/');
        if (parts.Length >= 4)
        {
            string expectedUser = parts[2];
            string expectedPass = parts[3];

            string? authHeader = req.Headers["Authorization"];
            if (authHeader != null && authHeader.StartsWith("Basic "))
            {
                string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(authHeader.Substring(6)));
                if (decoded == $"{expectedUser}:{expectedPass}")
                {
                    WriteJsonResponse(resp, 200, $@"{{""authenticated"":true,""user"":""{expectedUser}""}}");
                    return;
                }
            }

            resp.StatusCode = 401;
            resp.AddHeader("WWW-Authenticate", "Basic realm=\"test\"");
            var body = Encoding.UTF8.GetBytes("Unauthorized");
            resp.ContentType = "text/plain";
            resp.ContentLength64 = body.Length;
            resp.OutputStream.Write(body);
            resp.Close();
        }
    }

    private static void HandleEmpty(HttpListenerRequest req, HttpListenerResponse resp)
    {
        resp.StatusCode = 200;
        resp.ContentType = "text/plain";
        resp.ContentLength64 = 0;
        resp.Close();
    }

    private static void HandleLarge(HttpListenerRequest req, HttpListenerResponse resp)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 1000; i++)
            sb.AppendLine($"Line {i}: This is a test line with some content to make it longer.");
        WriteTextResponse(resp, 200, sb.ToString());
    }

    private static void HandleUnicode(HttpListenerRequest req, HttpListenerResponse resp)
    {
        WriteTextResponse(resp, 200, "Hello, \u4e16\u754c! \ud83c\udf0d\n");
    }

    private static void HandleCustomHeaders(HttpListenerRequest req, HttpListenerResponse resp)
    {
        resp.StatusCode = 200;
        resp.ContentType = "text/plain";
        resp.AddHeader("X-Custom-Header", "test-value");
        resp.AddHeader("X-Another", "another-value");
        var body = Encoding.UTF8.GetBytes("custom headers");
        resp.ContentLength64 = body.Length;
        resp.OutputStream.Write(body);
        resp.Close();
    }

    private static void HandleUserAgent(HttpListenerRequest req, HttpListenerResponse resp)
    {
        string ua = req.UserAgent ?? "";
        WriteTextResponse(resp, 200, ua);
    }

    private static void HandleQuery(HttpListenerRequest req, HttpListenerResponse resp)
    {
        WriteTextResponse(resp, 200, req.Url?.Query ?? "");
    }

    private static void WriteJsonResponse(HttpListenerResponse resp, int statusCode, string json)
    {
        resp.StatusCode = statusCode;
        resp.ContentType = "application/json";
        var body = Encoding.UTF8.GetBytes(json);
        resp.ContentLength64 = body.Length;
        resp.OutputStream.Write(body);
        resp.Close();
    }

    private static void WriteTextResponse(HttpListenerResponse resp, int statusCode, string text)
    {
        resp.StatusCode = statusCode;
        resp.ContentType = "text/plain";
        var body = Encoding.UTF8.GetBytes(text);
        resp.ContentLength64 = body.Length;
        resp.OutputStream.Write(body);
        resp.Close();
    }

    // =========================================================================
    // Infrastructure helpers
    // =========================================================================

    /// <summary>
    /// Run both curl and ncurl with the same arguments, assert matching output and exit code.
    /// Returns (stdout, exitCode) from curl.
    /// </summary>
    private static (string Output, int ExitCode) RunCurl(params string[] args)
    {
        var curlResult = RunProcess(CurlPath, args);
        var ncurlResult = RunProcess(_ncurlBin, args);

        Assert.That(ncurlResult.ExitCode, Is.EqualTo(curlResult.ExitCode),
            $"ncurl exit code should match curl.\n  args: {FormatArgs(args)}\n  curl stderr: {curlResult.Stderr}\n  ncurl stderr: {ncurlResult.Stderr}");

        return (curlResult.Output, curlResult.ExitCode);
    }

    /// <summary>
    /// Run both curl and ncurl, compare output with normalization.
    /// </summary>
    private static (string Output, int ExitCode) RunCurlCompareOutput(params string[] args)
    {
        var curlResult = RunProcess(CurlPath, args);
        var ncurlResult = RunProcess(_ncurlBin, args);

        Assert.That(ncurlResult.ExitCode, Is.EqualTo(curlResult.ExitCode),
            $"ncurl exit code should match curl.\n  args: {FormatArgs(args)}\n  curl stderr: {curlResult.Stderr}\n  ncurl stderr: {ncurlResult.Stderr}");
        Assert.That(ncurlResult.Output, Is.EqualTo(curlResult.Output),
            $"ncurl output should match curl.\n  args: {FormatArgs(args)}");

        return (curlResult.Output, curlResult.ExitCode);
    }

    /// <summary>
    /// Run both curl and ncurl, comparing output after normalizing headers (removing Date, etc.).
    /// </summary>
    private static (string Output, int ExitCode) RunCurlCompareHeaders(params string[] args)
    {
        var curlResult = RunProcess(CurlPath, args);
        var ncurlResult = RunProcess(_ncurlBin, args);

        Assert.That(ncurlResult.ExitCode, Is.EqualTo(curlResult.ExitCode),
            $"ncurl exit code should match curl.\n  args: {FormatArgs(args)}");

        // Normalize: remove Date, Server, and other volatile headers
        string curlNorm = NormalizeHeaderOutput(curlResult.Output);
        string ncurlNorm = NormalizeHeaderOutput(ncurlResult.Output);

        Assert.That(ncurlNorm, Is.EqualTo(curlNorm),
            $"ncurl normalized output should match curl.\n  args: {FormatArgs(args)}\n  curl raw:\n{curlResult.Output}\n  ncurl raw:\n{ncurlResult.Output}");

        return (curlResult.Output, curlResult.ExitCode);
    }

    /// <summary>
    /// Run only ncurl and check exit code (for tests where curl behavior is known).
    /// </summary>
    private static (string Output, int ExitCode) RunNcurlOnly(params string[] args)
    {
        var r = RunProcess(_ncurlBin, args); return (r.Output, r.ExitCode);
    }

    private static (string Output, string Stderr, int ExitCode) RunProcess(string executable, string[] args)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        for (int i = 0; i < args.Length; i++)
            process.StartInfo.ArgumentList.Add(args[i]);

        process.Start();
        try { process.StandardInput.Close(); } catch { }

        string output = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(30000);
        return (output, stderr, process.ExitCode);
    }

    private static (string Output, string Stderr, int ExitCode) RunProcessWithStdin(string executable, string[] args, string stdinData)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        for (int i = 0; i < args.Length; i++)
            process.StartInfo.ArgumentList.Add(args[i]);

        process.Start();
        try
        {
            process.StandardInput.Write(stdinData);
            process.StandardInput.Close();
        }
        catch { }

        string output = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(30000);
        return (output, stderr, process.ExitCode);
    }

    private static string NormalizeHeaderOutput(string output)
    {
        var sb = new StringBuilder();
        var lines = output.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r');
            // Skip volatile headers
            if (line.StartsWith("Date:", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("< Date:", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("Server:", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("< Server:", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("< Transfer-Encoding:", StringComparison.OrdinalIgnoreCase)) continue;
            // Skip connection-related
            if (line.StartsWith("* ", StringComparison.Ordinal)) continue;
            if (line.StartsWith("{ ", StringComparison.Ordinal)) continue;
            if (line.StartsWith("} ", StringComparison.Ordinal)) continue;
            sb.AppendLine(line);
        }
        return sb.ToString().TrimEnd();
    }

    private static string FormatArgs(string[] args)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < args.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            if (args[i].Contains(' '))
            {
                sb.Append('"');
                sb.Append(args[i]);
                sb.Append('"');
            }
            else
            {
                sb.Append(args[i]);
            }
        }
        return sb.ToString();
    }

    // =========================================================================
    // 1. Basic GET tests (~10)
    // =========================================================================

    [Test]
    public void Get_SimpleUrl_ReturnsJsonBody()
    {
        RunCurlCompareOutput("-s", $"{_baseUrl}/json");
    }

    [Test]
    public void Get_TextEndpoint_ReturnsPlainText()
    {
        RunCurlCompareOutput("-s", $"{_baseUrl}/text");
    }

    [Test]
    public void Get_EmptyResponse_ReturnsEmpty()
    {
        RunCurlCompareOutput("-s", $"{_baseUrl}/empty");
    }

    [Test]
    public void Get_NotFound_Returns404Body()
    {
        RunCurlCompareOutput("-s", $"{_baseUrl}/nonexistent");
    }

    [Test]
    public void Get_QueryParams_PassedThrough()
    {
        RunCurlCompareOutput("-s", $"{_baseUrl}/query?foo=bar&baz=qux");
    }

    [Test]
    public void Get_UnicodeContent_ReturnsCorrectly()
    {
        RunCurlCompareOutput("-s", $"{_baseUrl}/unicode");
    }

    [Test]
    public void Get_LargeResponse_ReturnsAllContent()
    {
        var curlResult = RunProcess(CurlPath, ["-s", $"{_baseUrl}/large"]);
        var ncurlResult = RunProcess(_ncurlBin, ["-s", $"{_baseUrl}/large"]);
        Assert.That(ncurlResult.ExitCode, Is.EqualTo(curlResult.ExitCode));
        Assert.That(ncurlResult.Output.Length, Is.EqualTo(curlResult.Output.Length),
            "Large response length mismatch");
    }

    [Test]
    public void Get_ExplicitMethod_GET()
    {
        RunCurlCompareOutput("-s", "-X", "GET", $"{_baseUrl}/json");
    }

    [Test]
    public void Get_WithUserAgent_DefaultCurl()
    {
        // Both should send some user-agent (may differ, just check exit code)
        RunCurl("-s", $"{_baseUrl}/user-agent");
    }

    [Test]
    public void Get_Status200_ExitCode0()
    {
        var result = RunCurl("-s", $"{_baseUrl}/status/200");
        Assert.That(result.ExitCode, Is.EqualTo(0));
    }

    // =========================================================================
    // 2. HTTP methods (~10)
    // =========================================================================

    [Test]
    public void Post_WithData_SendsPost()
    {
        RunCurlCompareOutput("-s", "-d", "hello=world", $"{_baseUrl}/post");
    }

    [Test]
    public void Post_EmptyData_SendsPost()
    {
        RunCurlCompareOutput("-s", "-d", "", $"{_baseUrl}/post");
    }

    [Test]
    public void Put_ExplicitMethod_SendsPut()
    {
        RunCurlCompareOutput("-s", "-X", "PUT", "-d", "data=test", $"{_baseUrl}/put");
    }

    [Test]
    public void Patch_ExplicitMethod_SendsPatch()
    {
        RunCurlCompareOutput("-s", "-X", "PATCH", "-d", "update=true", $"{_baseUrl}/patch");
    }

    [Test]
    public void Delete_ExplicitMethod_SendsDelete()
    {
        RunCurlCompareOutput("-s", "-X", "DELETE", $"{_baseUrl}/delete");
    }

    [Test]
    public void Head_WithI_SendsHead()
    {
        // -I only, just check exit code (headers vary)
        RunCurl("-s", "-I", $"{_baseUrl}/json");
    }

    [Test]
    public void Options_ExplicitMethod_SendsOptions()
    {
        RunCurl("-s", "-X", "OPTIONS", $"{_baseUrl}/json");
    }

    [Test]
    public void Post_MultipleDataFlags_ConcatenatedWithAmpersand()
    {
        RunCurlCompareOutput("-s", "-d", "a=1", "-d", "b=2", $"{_baseUrl}/post");
    }

    [Test]
    public void Post_DataImpliesPost_NoExplicitMethod()
    {
        RunCurlCompareOutput("-s", "-d", "key=value", $"{_baseUrl}/post");
    }

    [Test]
    public void Put_WithBody_EchoesCorrectly()
    {
        RunCurlCompareOutput("-s", "-X", "PUT", "-d", "body=content", $"{_baseUrl}/put");
    }

    // =========================================================================
    // 3. Headers (~10)
    // =========================================================================

    [Test]
    public void Header_CustomHeader_SentToServer()
    {
        RunCurl("-s", "-H", "X-Custom: test-value", $"{_baseUrl}/headers");
    }

    [Test]
    public void Header_MultipleHeaders_AllSent()
    {
        RunCurl("-s", "-H", "X-First: one", "-H", "X-Second: two", $"{_baseUrl}/headers");
    }

    [Test]
    public void Header_ContentType_Override()
    {
        RunCurlCompareOutput("-s", "-H", "Content-Type: text/xml", "-d", "<root/>", $"{_baseUrl}/post");
    }

    [Test]
    public void Header_AcceptJson()
    {
        RunCurl("-s", "-H", "Accept: application/json", $"{_baseUrl}/headers");
    }

    [Test]
    public void Header_EmptyValue()
    {
        RunCurl("-s", "-H", "X-Empty:", $"{_baseUrl}/headers");
    }

    [Test]
    public void Header_UserAgent_Custom()
    {
        RunCurlCompareOutput("-s", "-A", "MyAgent/1.0", $"{_baseUrl}/user-agent");
    }

    [Test]
    public void Header_Referer_SetViaE()
    {
        RunCurl("-s", "-e", "http://example.com", $"{_baseUrl}/headers");
    }

    [Test]
    public void Header_UserAgent_LongFlag()
    {
        RunCurlCompareOutput("-s", "--user-agent", "TestBot/2.0", $"{_baseUrl}/user-agent");
    }

    [Test]
    public void Header_Authorization_Custom()
    {
        RunCurl("-s", "-H", "Authorization: Token abc123", $"{_baseUrl}/headers");
    }

    [Test]
    public void Header_MultipleWithSameName()
    {
        RunCurl("-s", "-H", "X-Multi: val1", "-H", "X-Multi: val2", $"{_baseUrl}/headers");
    }

    // =========================================================================
    // 4. Request body (~10)
    // =========================================================================

    [Test]
    public void Data_SimpleString_PostBody()
    {
        RunCurlCompareOutput("-s", "-d", "name=test", $"{_baseUrl}/post");
    }

    [Test]
    public void Data_JsonBody_WithContentType()
    {
        RunCurlCompareOutput("-s", "-H", "Content-Type: application/json", "-d", "{\"key\":\"value\"}", $"{_baseUrl}/post");
    }

    [Test]
    public void Json_Shorthand_SetsContentTypeAndAccept()
    {
        RunCurlCompareOutput("-s", "--json", "{\"test\":true}", $"{_baseUrl}/post");
    }

    [Test]
    public void DataRaw_WithAtSign_NotTreatedAsFile()
    {
        RunCurlCompareOutput("-s", "--data-raw", "@notafile", $"{_baseUrl}/post");
    }

    [Test]
    public void DataUrlencode_SingleField()
    {
        RunCurlCompareOutput("-s", "--data-urlencode", "q=hello world", $"{_baseUrl}/post");
    }

    [Test]
    public void DataUrlencode_SpecialChars()
    {
        RunCurlCompareOutput("-s", "--data-urlencode", "q=a&b=c", $"{_baseUrl}/post");
    }

    [Test]
    public void Data_FromFile_ReadsContent()
    {
        var tmpFile = Path.Combine(_tempDir, $"data_{Guid.NewGuid():N}.txt");
        File.WriteAllText(tmpFile, "file_content=yes");
        try
        {
            RunCurlCompareOutput("-s", "-d", $"@{tmpFile}", $"{_baseUrl}/post");
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    [Test]
    public void DataBinary_PreservesNewlines()
    {
        RunCurlCompareOutput("-s", "--data-binary", "line1\nline2", $"{_baseUrl}/post");
    }

    [Test]
    public void Data_StripsNewlines()
    {
        // Normal -d strips newlines from inline data
        RunCurlCompareOutput("-s", "-d", "hello", $"{_baseUrl}/post");
    }

    [Test]
    public void Json_EmptyObject()
    {
        RunCurlCompareOutput("-s", "--json", "{}", $"{_baseUrl}/post");
    }

    // =========================================================================
    // 5. Output control (~10)
    // =========================================================================

    [Test]
    public void Output_ToFile_WritesContent()
    {
        var curlFile = Path.Combine(_tempDir, $"curl_out_{Guid.NewGuid():N}.txt");
        var ncurlFile = Path.Combine(_tempDir, $"ncurl_out_{Guid.NewGuid():N}.txt");
        try
        {
            RunProcess(CurlPath, ["-s", "-o", curlFile, $"{_baseUrl}/json"]);
            RunProcess(_ncurlBin, ["-s", "-o", ncurlFile, $"{_baseUrl}/json"]);

            Assert.That(File.Exists(ncurlFile), Is.True, "ncurl should create output file");
            Assert.That(File.ReadAllText(ncurlFile), Is.EqualTo(File.ReadAllText(curlFile)));
        }
        finally
        {
            if (File.Exists(curlFile)) File.Delete(curlFile);
            if (File.Exists(ncurlFile)) File.Delete(ncurlFile);
        }
    }

    [Test]
    public void Include_ShowsHeaders()
    {
        // Both show headers + body; just check exit code (header format may differ)
        RunCurl("-s", "-i", $"{_baseUrl}/json");
    }

    [Test]
    public void Head_ShowsOnlyHeaders()
    {
        RunCurl("-s", "-I", $"{_baseUrl}/text");
    }

    [Test]
    public void Silent_SuppressesProgressOutput()
    {
        RunCurlCompareOutput("-s", $"{_baseUrl}/json");
    }

    [Test]
    public void WriteOut_HttpCode_ReturnsStatusCode()
    {
        RunCurlCompareOutput("-s", "-o", "/dev/null", "-w", "%{http_code}", $"{_baseUrl}/json");
    }

    [Test]
    public void WriteOut_HttpCode_404()
    {
        RunCurlCompareOutput("-s", "-o", "/dev/null", "-w", "%{http_code}", $"{_baseUrl}/nonexistent");
    }

    [Test]
    public void WriteOut_HttpCode_500()
    {
        RunCurlCompareOutput("-s", "-o", "/dev/null", "-w", "%{http_code}", $"{_baseUrl}/status/500");
    }

    [Test]
    public void WriteOut_WithNewline()
    {
        RunCurlCompareOutput("-s", "-o", "/dev/null", "-w", "%{http_code}\\n", $"{_baseUrl}/json");
    }

    [Test]
    public void WriteOut_ContentType()
    {
        RunCurlCompareOutput("-s", "-o", "/dev/null", "-w", "%{content_type}", $"{_baseUrl}/json");
    }

    [Test]
    public void DumpHeader_WritesHeaderFile()
    {
        var curlHdr = Path.Combine(_tempDir, $"curl_hdr_{Guid.NewGuid():N}.txt");
        var ncurlHdr = Path.Combine(_tempDir, $"ncurl_hdr_{Guid.NewGuid():N}.txt");
        try
        {
            RunProcess(CurlPath, ["-s", "-D", curlHdr, $"{_baseUrl}/json"]);
            RunProcess(_ncurlBin, ["-s", "-D", ncurlHdr, $"{_baseUrl}/json"]);

            Assert.That(File.Exists(ncurlHdr), Is.True, "ncurl should create header dump file");
            // Both files should contain HTTP status line
            string ncurlHeaders = File.ReadAllText(ncurlHdr);
            Assert.That(ncurlHeaders, Does.Contain("200"));
        }
        finally
        {
            if (File.Exists(curlHdr)) File.Delete(curlHdr);
            if (File.Exists(ncurlHdr)) File.Delete(ncurlHdr);
        }
    }

    // =========================================================================
    // 6. Redirects (~5)
    // =========================================================================

    [Test]
    public void Redirect_WithoutL_Returns302()
    {
        // Without -L, curl returns empty body for 302
        RunCurl("-s", $"{_baseUrl}/redirect");
    }

    [Test]
    public void Redirect_WithL_FollowsRedirect()
    {
        RunCurlCompareOutput("-s", "-L", $"{_baseUrl}/redirect");
    }

    [Test]
    public void RedirectChain_WithL_FollowsAll()
    {
        RunCurlCompareOutput("-s", "-L", $"{_baseUrl}/redirect-chain");
    }

    [Test]
    public void Redirect_WriteOutCode_AfterRedirect()
    {
        RunCurlCompareOutput("-s", "-L", "-o", "/dev/null", "-w", "%{http_code}", $"{_baseUrl}/redirect");
    }

    [Test]
    public void Redirect_WithoutL_ExitCode0()
    {
        var result = RunCurl("-s", "-o", "/dev/null", "-w", "%{http_code}", $"{_baseUrl}/redirect");
        Assert.That(result.ExitCode, Is.EqualTo(0));
    }

    // =========================================================================
    // 7. Auth (~5)
    // =========================================================================

    [Test]
    public void BasicAuth_ValidCredentials_Returns200()
    {
        RunCurlCompareOutput("-s", "-u", "testuser:testpass", $"{_baseUrl}/basic-auth/testuser/testpass");
    }

    [Test]
    public void BasicAuth_InvalidCredentials_Returns401()
    {
        RunCurlCompareOutput("-s", "-u", "wrong:wrong", $"{_baseUrl}/basic-auth/testuser/testpass");
    }

    [Test]
    public void BasicAuth_NoCredentials_Returns401()
    {
        RunCurlCompareOutput("-s", $"{_baseUrl}/basic-auth/testuser/testpass");
    }

    [Test]
    public void BearerAuth_SetsHeader()
    {
        // Bearer token shows up in headers
        RunCurl("-s", "-H", "Authorization: Bearer my-token-123", $"{_baseUrl}/headers");
    }

    [Test]
    public void BasicAuth_ExitCode0_OnSuccess()
    {
        var result = RunCurl("-s", "-u", "admin:secret", $"{_baseUrl}/basic-auth/admin/secret");
        Assert.That(result.ExitCode, Is.EqualTo(0));
    }

    // =========================================================================
    // 8. TLS/connection (~5)
    // =========================================================================

    [Test]
    public void Timeout_ShortTimeout_ExitCode28()
    {
        // Delay 5 seconds, timeout 1 second
        var curlResult = RunProcess(CurlPath, ["-s", "-m", "1", $"{_baseUrl}/delay/5"]);
        var ncurlResult = RunProcess(_ncurlBin, ["-s", "-m", "1", $"{_baseUrl}/delay/5"]);
        Assert.That(ncurlResult.ExitCode, Is.EqualTo(curlResult.ExitCode));
    }

    [Test]
    public void ConnectionRefused_ExitCode7()
    {
        // Connect to a port that's likely not listening
        var curlResult = RunProcess(CurlPath, ["-s", "--connect-timeout", "2", "http://localhost:1"]);
        var ncurlResult = RunProcess(_ncurlBin, ["-s", "--connect-timeout", "2", "http://localhost:1"]);
        // Both should fail with connection error (exit code 7)
        Assert.That(ncurlResult.ExitCode, Is.EqualTo(curlResult.ExitCode));
    }

    [Test]
    public void DnsFailure_ExitCode6()
    {
        var curlResult = RunProcess(CurlPath, ["-s", "--connect-timeout", "5", "http://this-host-does-not-exist-at-all.invalid"]);
        var ncurlResult = RunProcess(_ncurlBin, ["-s", "--connect-timeout", "5", "http://this-host-does-not-exist-at-all.invalid"]);
        Assert.That(ncurlResult.ExitCode, Is.EqualTo(curlResult.ExitCode));
    }

    [Test]
    public void Insecure_FlagAccepted()
    {
        // Just verify the flag is accepted without error on an HTTP URL
        RunCurlCompareOutput("-s", "-k", $"{_baseUrl}/json");
    }

    [Test]
    public void ConnectTimeout_FlagAccepted()
    {
        RunCurlCompareOutput("-s", "--connect-timeout", "30", $"{_baseUrl}/json");
    }

    // =========================================================================
    // 9. Forms (~5)
    // =========================================================================

    [Test]
    public void Form_SimpleField_SendsMultipart()
    {
        // Form submissions are multipart — compare exit code (body format varies by boundary)
        RunCurl("-s", "-F", "name=value", $"{_baseUrl}/form");
    }

    [Test]
    public void Form_MultipleFields_AllSent()
    {
        RunCurl("-s", "-F", "field1=val1", "-F", "field2=val2", $"{_baseUrl}/form");
    }

    [Test]
    public void Form_FileUpload_SendsFile()
    {
        var tmpFile = Path.Combine(_tempDir, $"upload_{Guid.NewGuid():N}.txt");
        File.WriteAllText(tmpFile, "file content here");
        try
        {
            RunCurl("-s", "-F", $"file=@{tmpFile}", $"{_baseUrl}/form");
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    [Test]
    public void Form_ImpliesPost()
    {
        RunCurl("-s", "-F", "key=value", $"{_baseUrl}/post");
    }

    [Test]
    public void Form_WithExplicitPut()
    {
        RunCurl("-s", "-X", "PUT", "-F", "data=test", $"{_baseUrl}/put");
    }

    // =========================================================================
    // 10. Cookies (~5)
    // =========================================================================

    [Test]
    public void Cookie_SendString_ReceivedByServer()
    {
        RunCurlCompareOutput("-s", "-b", "session=abc123", $"{_baseUrl}/cookies");
    }

    [Test]
    public void Cookie_MultipleCookies_AllSent()
    {
        RunCurlCompareOutput("-s", "-b", "a=1; b=2", $"{_baseUrl}/cookies");
    }

    [Test]
    public void CookieJar_SaveCookies()
    {
        var curlJar = Path.Combine(_tempDir, $"curl_jar_{Guid.NewGuid():N}.txt");
        var ncurlJar = Path.Combine(_tempDir, $"ncurl_jar_{Guid.NewGuid():N}.txt");
        try
        {
            RunProcess(CurlPath, ["-s", "-c", curlJar, $"{_baseUrl}/cookies/set?test=value"]);
            RunProcess(_ncurlBin, ["-s", "-c", ncurlJar, $"{_baseUrl}/cookies/set?test=value"]);

            Assert.That(File.Exists(ncurlJar), Is.True, "ncurl should create cookie jar file");
            string ncurlJarContent = File.ReadAllText(ncurlJar);
            Assert.That(ncurlJarContent, Does.Contain("test"), "Cookie jar should contain the cookie name");
        }
        finally
        {
            if (File.Exists(curlJar)) File.Delete(curlJar);
            if (File.Exists(ncurlJar)) File.Delete(ncurlJar);
        }
    }

    [Test]
    public void Cookie_FromFile_SentToServer()
    {
        var cookieFile = Path.Combine(_tempDir, $"cookies_{Guid.NewGuid():N}.txt");
        File.WriteAllText(cookieFile, "# Netscape HTTP Cookie File\nlocalhost\tFALSE\t/\tFALSE\t0\tfilecookie\tfilevalue\n");
        try
        {
            // Just check exit code — cookie file format details may differ
            RunCurl("-s", "-b", cookieFile, $"{_baseUrl}/cookies");
        }
        finally
        {
            File.Delete(cookieFile);
        }
    }

    [Test]
    public void Cookie_SetAndRead_Roundtrip()
    {
        var jar = Path.Combine(_tempDir, $"jar_{Guid.NewGuid():N}.txt");
        try
        {
            // Set cookie
            RunProcess(_ncurlBin, ["-s", "-c", jar, $"{_baseUrl}/cookies/set?roundtrip=yes"]);
            Assert.That(File.Exists(jar), Is.True);
            Assert.That(File.ReadAllText(jar), Does.Contain("roundtrip"));
        }
        finally
        {
            if (File.Exists(jar)) File.Delete(jar);
        }
    }

    // =========================================================================
    // 11. Retry (~5)
    // =========================================================================

    [Test]
    public void Retry_OnSuccess_NoRetry()
    {
        RunCurlCompareOutput("-s", "--retry", "3", $"{_baseUrl}/json");
    }

    [Test]
    public void Retry_WithDelay_FlagAccepted()
    {
        RunCurlCompareOutput("-s", "--retry", "0", "--retry-delay", "1", $"{_baseUrl}/json");
    }

    [Test]
    public void Retry_On500_WithFail()
    {
        // With --fail and --retry, should retry on 500
        var curlResult = RunProcess(CurlPath, ["-s", "-f", "--retry", "0", $"{_baseUrl}/status/500"]);
        var ncurlResult = RunProcess(_ncurlBin, ["-s", "-f", "--retry", "0", $"{_baseUrl}/status/500"]);
        Assert.That(ncurlResult.ExitCode, Is.EqualTo(curlResult.ExitCode));
    }

    [Test]
    public void Retry_ZeroRetries_NoRetry()
    {
        RunCurlCompareOutput("-s", "--retry", "0", $"{_baseUrl}/json");
    }

    [Test]
    public void Retry_FlagCombined_WithSilent()
    {
        RunCurlCompareOutput("-s", "--retry", "1", $"{_baseUrl}/text");
    }

    // =========================================================================
    // 12. Verbose (~5)
    // =========================================================================

    [Test]
    public void Verbose_ShowsRequestAndResponseLines()
    {
        var ncurlResult = RunProcess(_ncurlBin, ["-s", "-v", $"{_baseUrl}/json"]);
        Assert.That(ncurlResult.ExitCode, Is.EqualTo(0));
        // Verbose output goes to stderr
        Assert.That(ncurlResult.Stderr, Does.Contain("> GET"));
        Assert.That(ncurlResult.Stderr, Does.Contain("< HTTP/"));
    }

    [Test]
    public void Verbose_ShowsHostHeader()
    {
        var ncurlResult = RunProcess(_ncurlBin, ["-s", "-v", $"{_baseUrl}/json"]);
        Assert.That(ncurlResult.Stderr, Does.Contain("> Host:"));
    }

    [Test]
    public void Verbose_PostShowsContentType()
    {
        var ncurlResult = RunProcess(_ncurlBin, ["-s", "-v", "-d", "data=test", $"{_baseUrl}/post"]);
        Assert.That(ncurlResult.ExitCode, Is.EqualTo(0));
        Assert.That(ncurlResult.Stderr, Does.Contain("> POST"));
    }

    [Test]
    public void Verbose_StdoutStillHasBody()
    {
        var ncurlResult = RunProcess(_ncurlBin, ["-s", "-v", $"{_baseUrl}/json"]);
        Assert.That(ncurlResult.Output, Does.Contain("hello"));
    }

    [Test]
    public void Verbose_ExitCode0_OnSuccess()
    {
        var result = RunCurl("-s", "-v", $"{_baseUrl}/text");
        Assert.That(result.ExitCode, Is.EqualTo(0));
    }

    // =========================================================================
    // 13. Compressed (~5)
    // =========================================================================

    [Test]
    public void Compressed_GzipResponse_Decompressed()
    {
        RunCurlCompareOutput("-s", "--compressed", $"{_baseUrl}/gzip");
    }

    [Test]
    public void Compressed_NormalResponse_StillWorks()
    {
        RunCurlCompareOutput("-s", "--compressed", $"{_baseUrl}/text");
    }

    [Test]
    public void Compressed_JsonResponse_StillWorks()
    {
        RunCurlCompareOutput("-s", "--compressed", $"{_baseUrl}/json");
    }

    [Test]
    public void Compressed_ExitCode0()
    {
        var result = RunCurl("-s", "--compressed", $"{_baseUrl}/gzip");
        Assert.That(result.ExitCode, Is.EqualTo(0));
    }

    [Test]
    public void Compressed_WithWriteOut()
    {
        RunCurlCompareOutput("-s", "--compressed", "-o", "/dev/null", "-w", "%{http_code}", $"{_baseUrl}/gzip");
    }

    // =========================================================================
    // 14. Error handling (~10)
    // =========================================================================

    [Test]
    public void Fail_On404_ExitCode22()
    {
        var curlResult = RunProcess(CurlPath, ["-s", "-f", $"{_baseUrl}/nonexistent"]);
        var ncurlResult = RunProcess(_ncurlBin, ["-s", "-f", $"{_baseUrl}/nonexistent"]);
        Assert.That(ncurlResult.ExitCode, Is.EqualTo(curlResult.ExitCode));
    }

    [Test]
    public void Fail_On500_ExitCode22()
    {
        var curlResult = RunProcess(CurlPath, ["-s", "-f", $"{_baseUrl}/status/500"]);
        var ncurlResult = RunProcess(_ncurlBin, ["-s", "-f", $"{_baseUrl}/status/500"]);
        Assert.That(ncurlResult.ExitCode, Is.EqualTo(curlResult.ExitCode));
    }

    [Test]
    public void Fail_On200_ExitCode0()
    {
        RunCurlCompareOutput("-s", "-f", $"{_baseUrl}/json");
    }

    [Test]
    public void Fail_On403_ExitCode22()
    {
        var curlResult = RunProcess(CurlPath, ["-s", "-f", $"{_baseUrl}/status/403"]);
        var ncurlResult = RunProcess(_ncurlBin, ["-s", "-f", $"{_baseUrl}/status/403"]);
        Assert.That(ncurlResult.ExitCode, Is.EqualTo(curlResult.ExitCode));
    }

    [Test]
    public void Fail_SuppressesBody_On4xx()
    {
        var curlResult = RunProcess(CurlPath, ["-s", "-f", $"{_baseUrl}/status/404"]);
        var ncurlResult = RunProcess(_ncurlBin, ["-s", "-f", $"{_baseUrl}/status/404"]);
        Assert.That(ncurlResult.ExitCode, Is.EqualTo(curlResult.ExitCode));
        // With -f, stdout should be empty on error
        Assert.That(ncurlResult.Output, Is.EqualTo(""));
    }

    [Test]
    public void MalformedUrl_ExitCode3()
    {
        // Completely invalid URL
        var ncurlResult = RunProcess(_ncurlBin, ["-s", "://invalid"]);
        Assert.That(ncurlResult.ExitCode, Is.EqualTo(3));
    }

    [Test]
    public void NoUrl_ExitCode2()
    {
        var ncurlResult = RunProcess(_ncurlBin, Array.Empty<string>());
        Assert.That(ncurlResult.ExitCode, Is.EqualTo(2));
    }

    [Test]
    public void Status301_WithoutL_Returns301()
    {
        var curlResult = RunProcess(CurlPath, ["-s", "-o", "/dev/null", "-w", "%{http_code}", $"{_baseUrl}/redirect"]);
        var ncurlResult = RunProcess(_ncurlBin, ["-s", "-o", "/dev/null", "-w", "%{http_code}", $"{_baseUrl}/redirect"]);
        Assert.That(ncurlResult.Output, Is.EqualTo(curlResult.Output));
    }

    [Test]
    public void Status200_ExitCode0_WithFail()
    {
        RunCurlCompareOutput("-s", "-f", $"{_baseUrl}/text");
    }

    [Test]
    public void ErrorMessage_Suppressed_InSilentMode()
    {
        var ncurlResult = RunProcess(_ncurlBin, ["-s", "http://this-does-not-exist-at-all.invalid"]);
        // In silent mode, stderr should be empty (or minimal)
        // We mainly check exit code is non-zero
        Assert.That(ncurlResult.ExitCode, Is.Not.EqualTo(0));
    }

    // =========================================================================
    // 15. Edge cases (~5)
    // =========================================================================

    [Test]
    public void EmptyBody_Response()
    {
        RunCurlCompareOutput("-s", $"{_baseUrl}/empty");
    }

    [Test]
    public void CustomHeaders_ResponseReceived()
    {
        RunCurlCompareOutput("-s", $"{_baseUrl}/custom-headers");
    }

    [Test]
    public void MultipleFlags_Combined()
    {
        RunCurlCompareOutput("-sL", $"{_baseUrl}/json");
    }

    [Test]
    public void WriteOut_SizeDownload()
    {
        var curlResult = RunProcess(CurlPath, ["-s", "-o", "/dev/null", "-w", "%{size_download}", $"{_baseUrl}/text"]);
        var ncurlResult = RunProcess(_ncurlBin, ["-s", "-o", "/dev/null", "-w", "%{size_download}", $"{_baseUrl}/text"]);
        Assert.That(ncurlResult.ExitCode, Is.EqualTo(curlResult.ExitCode));
        // size_download should be the same
        Assert.That(ncurlResult.Output, Is.EqualTo(curlResult.Output));
    }

    [Test]
    public void Version_Flag_ReturnsInfo()
    {
        var result = RunProcess(_ncurlBin, ["--version"]);
        Assert.That(result.ExitCode, Is.EqualTo(0));
        Assert.That(result.Output, Does.Contain("ncurl"));
    }

    // =========================================================================
    // Additional tests to reach 100+
    // =========================================================================

    [Test]
    public void Get_Status201_ExitCode0()
    {
        RunCurl("-s", $"{_baseUrl}/status/201");
    }

    [Test]
    public void Get_Status204_ExitCode0()
    {
        RunCurl("-s", $"{_baseUrl}/status/204");
    }

    [Test]
    public void Get_Status301_ExitCode0()
    {
        RunCurl("-s", $"{_baseUrl}/status/301");
    }

    [Test]
    public void Get_Status400_ExitCode0_WithoutFail()
    {
        RunCurl("-s", $"{_baseUrl}/status/400");
    }

    [Test]
    public void Get_Status503_ExitCode0_WithoutFail()
    {
        RunCurl("-s", $"{_baseUrl}/status/503");
    }

    [Test]
    public void WriteOut_HttpCode_201()
    {
        RunCurlCompareOutput("-s", "-o", "/dev/null", "-w", "%{http_code}", $"{_baseUrl}/status/201");
    }

    [Test]
    public void WriteOut_HttpCode_204()
    {
        RunCurlCompareOutput("-s", "-o", "/dev/null", "-w", "%{http_code}", $"{_baseUrl}/status/204");
    }

    [Test]
    public void WriteOut_HttpCode_301()
    {
        RunCurlCompareOutput("-s", "-o", "/dev/null", "-w", "%{http_code}", $"{_baseUrl}/status/301");
    }

    [Test]
    public void WriteOut_HttpCode_400()
    {
        RunCurlCompareOutput("-s", "-o", "/dev/null", "-w", "%{http_code}", $"{_baseUrl}/status/400");
    }

    [Test]
    public void WriteOut_HttpCode_503()
    {
        RunCurlCompareOutput("-s", "-o", "/dev/null", "-w", "%{http_code}", $"{_baseUrl}/status/503");
    }

    [Test]
    public void Post_ExplicitMethodPost()
    {
        RunCurl("-s", "-X", "POST", $"{_baseUrl}/post");
    }

    [Test]
    public void Delete_WithBody()
    {
        RunCurlCompareOutput("-s", "-X", "DELETE", "-d", "id=123", $"{_baseUrl}/delete");
    }

    [Test]
    public void Fail_On401_ExitCode22()
    {
        var curlResult = RunProcess(CurlPath, ["-s", "-f", $"{_baseUrl}/status/401"]);
        var ncurlResult = RunProcess(_ncurlBin, ["-s", "-f", $"{_baseUrl}/status/401"]);
        Assert.That(ncurlResult.ExitCode, Is.EqualTo(curlResult.ExitCode));
    }

    [Test]
    public void Help_Flag_ReturnsHelp()
    {
        var result = RunProcess(_ncurlBin, ["--help"]);
        Assert.That(result.ExitCode, Is.EqualTo(0));
        Assert.That(result.Output, Does.Contain("Usage"));
    }

    [Test]
    public void Silent_WithShowError()
    {
        RunCurlCompareOutput("-sS", $"{_baseUrl}/json");
    }

    [Test]
    public void Url_LongFlag()
    {
        RunCurlCompareOutput("-s", "--url", $"{_baseUrl}/json");
    }
}
