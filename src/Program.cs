using System.Drawing.Printing;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace ShelivoPrintAgent;

internal static class Program
{
    // Reported by /health, which is the only way to tell which build a till is
    // actually running. Read from the assembly rather than hardcoded here, so
    // it cannot drift from the version the installer registers -- both derive
    // from <Version> in the csproj.
    private static readonly string Version = ResolveVersion();

    private static string ResolveVersion()
    {
        var informational = typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational))
        {
            return typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        }

        // A build with SourceLink appends "+<commit sha>"; tills only need the
        // version itself.
        var plus = informational.IndexOf('+');
        return plus >= 0 ? informational[..plus] : informational;
    }

    // Origins allowed when PRINT_AGENT_ALLOWED_ORIGINS is unset: the POS's
    // dev, stage and prod hosts, plus a local ng serve. Baked in rather than
    // set per till, because nothing in the installer writes that variable --
    // leaving the default at localhost alone meant a double-clicked install
    // refused every real POS origin, and the remedy was a manual per-machine
    // step easily forgotten. Listing all three costs little: the listener is
    // loopback-only and every host here is Shelivo's own.
    private const string DefaultAllowedOrigins =
        "https://www.pos.shelivo.com," +
        "https://www.stage.pos.shelivo.com," +
        "https://www.dev.pos.shelivo.com," +
        "http://localhost:4200";

    private static async Task Main()
    {
        var port = int.TryParse(Environment.GetEnvironmentVariable("PRINT_AGENT_PORT"), out var parsedPort)
            ? parsedPort
            : 9200;

        // Origins allowed to call this agent, e.g. the POS web app's URL(s).
        // No per-request trust dialog like QZ Tray's -- anything on this
        // allowlist is simply allowed, silently, every time.
        var allowedOrigins = (Environment.GetEnvironmentVariable("PRINT_AGENT_ALLOWED_ORIGINS") ?? DefaultAllowedOrigins)
            .Split(',')
            .Select(o => o.Trim())
            .Where(o => o.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var listener = new HttpListener();
        // Bind loopback-only: no Windows Firewall "allow this app" prompt
        // ever appears, and the agent is unreachable from anything but this
        // same machine.
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        Console.WriteLine($"Print agent listening on http://127.0.0.1:{port}");
        Console.WriteLine($"Allowed origins: {string.Join(", ", allowedOrigins)}");

        while (true)
        {
            var context = await listener.GetContextAsync();
            _ = HandleRequestAsync(context, allowedOrigins);
        }
    }

    private static async Task HandleRequestAsync(HttpListenerContext context, HashSet<string> allowedOrigins)
    {
        var request = context.Request;
        var response = context.Response;

        ApplyCorsHeaders(request, response, allowedOrigins);

        try
        {
            if (request.HttpMethod == "OPTIONS")
            {
                response.StatusCode = 204;
                response.Close();
                return;
            }

            var path = request.Url?.AbsolutePath ?? "";

            if (request.HttpMethod == "GET" && path == "/health")
            {
                await WriteJsonAsync(response, 200, new { ok = true, version = Version });
                return;
            }

            if (request.HttpMethod == "GET" && path == "/printers")
            {
                var printers = PrinterSettings.InstalledPrinters.Cast<string>().ToArray();
                await WriteJsonAsync(response, 200, printers);
                return;
            }

            if (request.HttpMethod == "POST" && path == "/print")
            {
                await HandlePrintAsync(request, response);
                return;
            }

            response.StatusCode = 404;
            response.Close();
        }
        catch (Exception ex)
        {
            try
            {
                await WriteJsonAsync(response, 500, new { ok = false, error = ex.Message });
            }
            catch
            {
                // Response already closed/broken -- nothing more we can do.
            }
        }
    }

    private static async Task HandlePrintAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        string body;
        using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
        {
            body = await reader.ReadToEndAsync();
        }

        PrintRequestBody? payload;
        try
        {
            payload = JsonSerializer.Deserialize<PrintRequestBody>(
                body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            await WriteJsonAsync(response, 400, new { ok = false, error = "invalid JSON body" });
            return;
        }

        if (string.IsNullOrWhiteSpace(payload?.Printer))
        {
            await WriteJsonAsync(response, 400, new { ok = false, error = "printer is required" });
            return;
        }

        var bytes = ParseSequence(payload.Sequence);
        if (bytes == null)
        {
            await WriteJsonAsync(response, 400, new { ok = false, error = "sequence must be a comma-separated list of byte values (0-255)" });
            return;
        }

        try
        {
            RawPrinter.SendBytes(payload.Printer, bytes);
            await WriteJsonAsync(response, 200, new { ok = true });
        }
        catch (Exception ex)
        {
            await WriteJsonAsync(response, 500, new { ok = false, error = ex.Message });
        }
    }

    private static byte[]? ParseSequence(string? sequence)
    {
        if (string.IsNullOrWhiteSpace(sequence))
        {
            return null;
        }

        var parts = sequence.Split(',');
        var bytes = new byte[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i].Trim(), out var value) || value < 0 || value > 255)
            {
                return null;
            }
            bytes[i] = (byte)value;
        }
        return bytes;
    }

    // Chrome's Private Network Access check: a page served from a public/
    // HTTPS origin (production) calling a loopback address (this agent)
    // sends a preflight with this request header and requires this response
    // header back, separately from ordinary CORS -- without it the request
    // is silently blocked, even though the origin is already allowed below.
    private static void ApplyCorsHeaders(HttpListenerRequest request, HttpListenerResponse response, HashSet<string> allowedOrigins)
    {
        var origin = request.Headers["Origin"];
        if (!string.IsNullOrEmpty(origin) && allowedOrigins.Contains(origin))
        {
            response.Headers["Access-Control-Allow-Origin"] = origin;
            response.Headers["Vary"] = "Origin";
        }

        response.Headers["Access-Control-Allow-Methods"] = "GET,POST,OPTIONS";

        // Echo back whatever headers the preflight is asking permission for,
        // instead of a fixed list -- an app-wide interceptor (e.g. one that
        // attaches an Authorization header to every HttpClient request) can
        // add headers here at any time without our knowledge, and a hardcoded
        // allow-list breaks the moment that happens (as it did with the
        // app's AuthInterceptor). This agent only serves trusted local
        // origins already checked above, so being permissive on headers
        // carries no real risk.
        var requestedHeaders = request.Headers["Access-Control-Request-Headers"];
        response.Headers["Access-Control-Allow-Headers"] = string.IsNullOrEmpty(requestedHeaders)
            ? "Content-Type"
            : requestedHeaders;

        if (request.Headers["Access-Control-Request-Private-Network"] == "true")
        {
            response.Headers["Access-Control-Allow-Private-Network"] = "true";
        }
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, int statusCode, object data)
    {
        response.StatusCode = statusCode;
        response.ContentType = "application/json";
        var json = JsonSerializer.Serialize(data);
        var bytes = Encoding.UTF8.GetBytes(json);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.OutputStream.Close();
    }

    private sealed record PrintRequestBody(string? Printer, string? Sequence);
}
