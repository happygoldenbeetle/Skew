using System.Linq;
using System.Text;
using System.Text.Json;
using Xilium.CefGlue;

namespace Mori.Cef;

/// <summary>
/// Scheme handler factory + resource handler for Mori's custom schemes. Port of
/// the C++ scheme-serving logic in mac App/CefAppImpl.mm.
/// </summary>
public sealed class MoriExtensionSchemeHandlerFactory : CefSchemeHandlerFactory
{
    protected override CefResourceHandler Create(
        CefBrowser browser, CefFrame frame, string schemeName, CefRequest request)
        => new MoriExtensionResourceHandler();
}

/// <summary>Serves Mori's internal pages (mori://newtab/, etc.).</summary>
public sealed class MoriInternalSchemeHandlerFactory : CefSchemeHandlerFactory
{
    protected override CefResourceHandler Create(
        CefBrowser browser, CefFrame frame, string schemeName, CefRequest request)
        => new MoriInternalResourceHandler();
}

/// <summary>
/// Shared helpers for resolving and validating extension asset paths.
/// </summary>
internal static class MoriExtensionCatalog
{
    private const string EnvironmentCatalogKey = "MORI_EXTENSION_CATALOG_JSON";

    public static string? EnabledExtensionRootForId(string? extensionId)
    {
        if (string.IsNullOrEmpty(extensionId))
            return null;

        var ext = Mori.Models.ExtensionStore.Shared.Extensions.FirstOrDefault(x => string.Equals(x.Id, extensionId, StringComparison.OrdinalIgnoreCase));
        if (ext != null && ext.Enabled)
        {
            return ext.Path;
        }

        return null;
    }

    public static string? SafeExtensionFilePath(string? root, string? requestPath)
    {
        if (string.IsNullOrEmpty(root))
            return null;

        string relative = requestPath ?? string.Empty;
        relative = relative.TrimStart('/');
        relative = Uri.UnescapeDataString(relative);
        if (relative.Length == 0)
            return null;

        string rootResolved = Path.GetFullPath(root);
        string candidate = Path.GetFullPath(Path.Combine(rootResolved, relative));

        string requiredPrefix = rootResolved.EndsWith(Path.DirectorySeparatorChar)
            ? rootResolved
            : rootResolved + Path.DirectorySeparatorChar;

        if (!string.Equals(candidate, rootResolved, StringComparison.OrdinalIgnoreCase) &&
            !candidate.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase))
            return null;

        return File.Exists(candidate) ? candidate : null;
    }

    public static string MimeTypeForPath(string filePath)
    {
        string ext = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
        if (ext.Length == 0)
            return "application/octet-stream";
        string mime = CefRuntime.GetMimeType(ext);
        return string.IsNullOrEmpty(mime) ? "application/octet-stream" : mime;
    }
}

/// <summary>
/// Base class implementing the CefResourceHandler read loop over an in-memory
/// byte buffer.
/// </summary>
internal abstract class MoriBufferedResourceHandler : CefResourceHandler
{
    private byte[] _data = Array.Empty<byte>();
    private int _offset;
    private int _status = 200;
    private string _statusText = "OK";
    private string _mimeType = "application/octet-stream";

    protected abstract bool Resolve(CefRequest request);

    protected void SetResponse(int status, string statusText, string mimeType, byte[] body)
    {
        _status = status;
        _statusText = statusText;
        _mimeType = mimeType;
        _data = body;
        _offset = 0;
    }

    protected void SetTextResponse(int status, string statusText, string mimeType, string body)
        => SetResponse(status, statusText, mimeType, Encoding.UTF8.GetBytes(body));

    protected override bool Open(CefRequest request, out bool handleRequest, CefCallback callback)
    {
        bool resolved = Resolve(request);
        if (!resolved)
            SetTextResponse(404, "Not Found", "text/plain", "Not found");

        handleRequest = true; // We always complete synchronously.
        return true;
    }

    protected override void GetResponseHeaders(
        CefResponse response, out long responseLength, out string? redirectUrl)
    {
        response.Status = _status;
        response.StatusText = _statusText;
        response.MimeType = _mimeType;

        var headers = response.GetHeaderMap();
        headers["Cache-Control"] = "no-store";
        response.SetHeaderMap(headers);

        responseLength = _data.Length;
        redirectUrl = null;
    }

    protected override bool Read(
        Stream dataOut, int bytesToRead, out int bytesRead, CefResourceReadCallback callback)
    {
        int remaining = _data.Length - _offset;
        if (remaining <= 0)
        {
            bytesRead = 0;
            return false; // Done.
        }

        int toCopy = Math.Min(bytesToRead, remaining);
        dataOut.Write(_data, _offset, toCopy);
        _offset += toCopy;
        bytesRead = toCopy;
        return true;
    }

    protected override bool Skip(
        long bytesToSkip, out long bytesSkipped, CefResourceSkipCallback callback)
    {
        int remaining = _data.Length - _offset;
        int toSkip = (int)Math.Min(bytesToSkip, remaining);
        _offset += toSkip;
        bytesSkipped = toSkip;
        return true;
    }

    protected override void Cancel() { }
}

/// <summary>Serves <see cref="MoriSchemes.ExtensionScheme"/> requests.</summary>
internal sealed class MoriExtensionResourceHandler : MoriBufferedResourceHandler
{
    protected override bool Resolve(CefRequest request)
    {
        var uri = new Uri(request.Url);
        string extensionId = uri.Host;
        string requestPath = uri.AbsolutePath;

        string? root = MoriExtensionCatalog.EnabledExtensionRootForId(extensionId);
        if (root is null)
        {
            SetTextResponse(404, "Not Found", "text/plain",
                "Extension is not enabled.");
            return true;
        }

        if (string.Equals(requestPath, MoriSchemes.ExtensionBackgroundPath,
                StringComparison.OrdinalIgnoreCase))
        {
            string bg = BuildBackgroundHtml(extensionId);
            SetTextResponse(200, "OK", "text/html", bg);
            return true;
        }

        string? filePath = MoriExtensionCatalog.SafeExtensionFilePath(root, requestPath);
        if (filePath is null)
        {
            SetTextResponse(404, "Not Found", "text/plain", "Not found");
            return true;
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(filePath);
        }
        catch (IOException)
        {
            SetTextResponse(500, "Internal Server Error", "text/plain", "Read error");
            return true;
        }

        string mime = MoriExtensionCatalog.MimeTypeForPath(filePath);

        if (mime.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
        {
            string html = Encoding.UTF8.GetString(bytes);
            string? runtime = ExtensionRuntimeBridge.ExtensionPageRuntimeJs(extensionId);
            if (runtime is not null)
                html = InjectRuntime(html, runtime);
            SetTextResponse(200, "OK", "text/html", html);
            return true;
        }

        SetResponse(200, "OK", mime, bytes);
        return true;
    }

    private static string BuildBackgroundHtml(string extensionId)
    {
        string? runtime = ExtensionRuntimeBridge.ExtensionPageRuntimeJs(extensionId);
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html><head><meta charset=\"utf-8\"></head><body>");
        if (runtime is not null)
            sb.Append("<script>").Append(runtime).Append("</script>");
        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static string InjectRuntime(string html, string runtimeJs)
    {
        string tag = "<script>" + runtimeJs + "</script>";
        int headIdx = html.IndexOf("<head", StringComparison.OrdinalIgnoreCase);
        if (headIdx >= 0)
        {
            int close = html.IndexOf('>', headIdx);
            if (close >= 0)
                return html.Insert(close + 1, tag);
        }
        return tag + html;
    }
}

/// <summary>Serves <see cref="MoriSchemes.InternalScheme"/> requests.</summary>
internal sealed class MoriInternalResourceHandler : MoriBufferedResourceHandler
{
    protected override bool Resolve(CefRequest request)
    {
        var uri = new Uri(request.Url);
        string page = uri.Host;

        string root = Path.Combine(AppContext.BaseDirectory, "Assets", "Internal");
        string relative = page.Length == 0 ? "newtab" : page;
        string candidate = Path.GetFullPath(Path.Combine(root, relative + ".html"));

        if (candidate.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) &&
            File.Exists(candidate))
        {
            string html = File.ReadAllText(candidate);
            string theme = Theme.ThemeService.Instance.IsDark ? "dark" : "light";
            html = html.Replace("<html lang=\"en\">", $"<html lang=\"en\" data-theme=\"{theme}\">");
            SetResponse(200, "OK", "text/html", Encoding.UTF8.GetBytes(html));
            return true;
        }

        // Fallback minimal new-tab shell so navigation never dead-ends.
        SetTextResponse(200, "OK", "text/html",
            "<!doctype html><html><head><meta charset=\"utf-8\"><title>New Tab</title>" +
            "<style>html,body{height:100%;margin:0;background:#1b1b1b}</style></head>" +
            "<body></body></html>");
        return true;
    }
}
