using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xilium.CefGlue;

namespace Skew.Cef;

/// <summary>
/// Provides the privileged network boundary normally supplied by Chromium's
/// extension process. A request is allowed only when the remote server's CORS
/// preflight explicitly accepts the calling chrome-extension origin.
/// </summary>
internal static class ExtensionNativeFetch
{
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        UseCookies = false
    }) { Timeout = TimeSpan.FromSeconds(20) };

    private const int MaxResponseBytes = 5 * 1024 * 1024;

    public static Dictionary<string, object?> Start(
        CefBrowser browser, string requestId, string extensionId, JsonElement args)
    {
        if (!args.TryGetProperty("url", out JsonElement urlElement) ||
            !Uri.TryCreate(urlElement.GetString(), UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
            return Error(requestId, "Extension fetch requires a valid HTTPS URL.");

        string method = args.TryGetProperty("method", out JsonElement methodElement)
            ? methodElement.GetString()?.ToUpperInvariant() ?? "GET" : "GET";
        if (method is not ("GET" or "HEAD" or "POST" or "PUT" or "PATCH" or "DELETE"))
            return Error(requestId, "Extension fetch method is not supported.");

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (args.TryGetProperty("headers", out JsonElement headersElement) &&
            headersElement.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in headersElement.EnumerateObject())
            {
                if (IsForbiddenHeader(property.Name)) continue;
                if (property.Value.ValueKind == JsonValueKind.String)
                    headers[property.Name] = property.Value.GetString() ?? string.Empty;
            }
        }
        string? body = args.TryGetProperty("body", out JsonElement bodyElement) &&
            bodyElement.ValueKind == JsonValueKind.String ? bodyElement.GetString() : null;

        _ = CompleteAsync(browser, requestId, extensionId, uri, method, headers, body);
        return new Dictionary<string, object?>
        {
            ["requestId"] = requestId,
            ["deferred"] = true
        };
    }

    private static async Task CompleteAsync(
        CefBrowser browser, string requestId, string extensionId, Uri uri,
        string method, Dictionary<string, string> headers, string? body)
    {
        Dictionary<string, object?> response;
        try
        {
            await EnsurePublicHostAsync(uri).ConfigureAwait(false);
            string extensionOrigin = $"chrome-extension://{extensionId}";

            using (var preflight = new HttpRequestMessage(HttpMethod.Options, uri))
            {
                preflight.Headers.TryAddWithoutValidation("Origin", extensionOrigin);
                preflight.Headers.TryAddWithoutValidation("Access-Control-Request-Method", method);
                if (headers.Count > 0)
                    preflight.Headers.TryAddWithoutValidation(
                        "Access-Control-Request-Headers", string.Join(",", headers.Keys));
                using HttpResponseMessage permission = await Client.SendAsync(preflight)
                    .ConfigureAwait(false);
                string? allowedOrigin = permission.Headers.TryGetValues(
                    "Access-Control-Allow-Origin", out IEnumerable<string>? values)
                    ? values.FirstOrDefault() : null;
                if (!permission.IsSuccessStatusCode ||
                    !(allowedOrigin == "*" || string.Equals(
                        allowedOrigin, extensionOrigin, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException(
                        "The remote server did not authorize this extension origin.");
            }

            using var request = new HttpRequestMessage(new HttpMethod(method), uri);
            foreach ((string name, string value) in headers)
            {
                if (!request.Headers.TryAddWithoutValidation(name, value))
                {
                    request.Content ??= new StringContent(body ?? string.Empty, Encoding.UTF8);
                    request.Content.Headers.TryAddWithoutValidation(name, value);
                }
            }
            if (body is not null && request.Content is null)
                request.Content = new StringContent(body, Encoding.UTF8);

            using HttpResponseMessage result = await Client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            byte[] bytes = await result.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            if (bytes.Length > MaxResponseBytes)
                throw new InvalidOperationException("Extension fetch response exceeded 5 MB.");

            var resultHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in result.Headers.Concat(result.Content.Headers))
                resultHeaders[header.Key] = string.Join(", ", header.Value);
            string charset = result.Content.Headers.ContentType?.CharSet ?? "utf-8";
            Encoding encoding;
            try { encoding = Encoding.GetEncoding(charset.Trim('"')); }
            catch { encoding = Encoding.UTF8; }

            response = Success(requestId, new
            {
                status = (int)result.StatusCode,
                statusText = result.ReasonPhrase ?? string.Empty,
                headers = resultHeaders,
                body = encoding.GetString(bytes),
                url = uri.AbsoluteUri
            });
            ExtensionDiagnostics.Write("network", extensionId,
                $"Native fetch to {uri.Host} completed with HTTP {(int)result.StatusCode}.");
        }
        catch (Exception ex)
        {
            ExtensionDiagnostics.Write("network-error", extensionId,
                $"Native fetch to {uri.Host} failed: {ex.Message}");
            response = Error(requestId, ex.Message);
        }

        Resolve(browser, response);
    }

    private static async Task EnsurePublicHostAsync(Uri uri)
    {
        if (uri.IsLoopback || uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Extension fetch cannot access a local address.");
        IPAddress[] addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost).ConfigureAwait(false);
        if (addresses.Length == 0 || addresses.Any(IsPrivateAddress))
            throw new InvalidOperationException("Extension fetch cannot access a private address.");
    }

    private static bool IsPrivateAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = address.GetAddressBytes();
            return b[0] == 10 || b[0] == 127 ||
                b[0] == 169 && b[1] == 254 ||
                b[0] == 172 && b[1] is >= 16 and <= 31 ||
                b[0] == 192 && b[1] == 168;
        }
        return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal ||
            address.Equals(IPAddress.IPv6Loopback);
    }

    private static bool IsForbiddenHeader(string name) => name.Equals("origin", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("referer", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("host", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("cookie", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("authorization", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("connection", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("content-length", StringComparison.OrdinalIgnoreCase);

    private static void Resolve(CefBrowser browser, Dictionary<string, object?> response)
    {
        string json = JsonSerializer.Serialize(response);
        foreach (long frameId in browser.GetFrameIdentifiers())
        {
            CefFrame? frame = browser.GetFrame(frameId);
            frame?.ExecuteJavaScript(
                $"if(window.__skewExtResolve)window.__skewExtResolve({json});", frame.Url, 0);
        }
    }

    private static Dictionary<string, object?> Success(string requestId, object? result) => new()
    {
        ["requestId"] = requestId,
        ["result"] = result
    };

    private static Dictionary<string, object?> Error(string requestId, string error) => new()
    {
        ["requestId"] = requestId,
        ["error"] = error
    };
}
