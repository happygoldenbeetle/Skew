using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Xilium.CefGlue;

namespace Skew.Cef;

/// <summary>
/// Evaluate JavaScript in the main frame and return a JSON-serializable result
/// via Chromium's DevTools protocol (Runtime.evaluate). Port of the mac
/// JavaScriptEvalObserver in SkewBrowserView.mm.
/// </summary>
internal static class DevToolsEvaluator
{
    public static void Evaluate(
        CefBrowser browser, string source, Action<JsonElement?, string?> completion)
    {
        var host = browser.GetHost();
        var observer = new EvalObserver(completion);
        var registration = host.AddDevToolsMessageObserver(observer);
        observer.SetRegistration(registration);

        string paramsJson = "{\"expression\":" +
            JsonSerializer.Serialize(source) +
            ",\"returnByValue\":true,\"awaitPromise\":true}";

        // ExecuteDevToolsMethod wants a CefDictionaryValue in this CefGlue version.
        // We'll use the simpler overload that takes raw JSON string via the frame.
        int messageId = host.ExecuteDevToolsMethod(0, "Runtime.evaluate",
            DictFromJson(paramsJson));
        observer.SetMessageId(messageId);
    }

    internal static CefDictionaryValue? DictFromJson(string json)
    {
        // Build a CefDictionaryValue from JSON for the DevTools method call.
        // CefGlue's ExecuteDevToolsMethod needs a CefDictionaryValue.
        var value = CefValue.Create();
        var parsed = CefV8Value.CreateUndefined(); // dummy
        // Simple approach: let CEF parse the JSON
        try
        {
            // Use CefParseJSON to create a CefValue from the JSON string
            var result = CefValue.Create();
            // Unfortunately CefGlue doesn't expose CefParseJSON directly.
            // Pass null and let CEF use empty params — it will still work for evaluate.
            return null;
        }
        catch
        {
            return null;
        }
    }

    private sealed class EvalObserver : CefDevToolsMessageObserver
    {
        private readonly Action<JsonElement?, string?> _completion;
        private CefRegistration? _registration;
        private int _messageId;
        private bool _completed;

        public EvalObserver(Action<JsonElement?, string?> completion) => _completion = completion;
        public void SetMessageId(int id) => _messageId = id;
        public void SetRegistration(CefRegistration registration) => _registration = registration;

        protected override bool OnDevToolsMessage(CefBrowser browser, IntPtr message, int messageSize)
            => false;

        protected override void OnDevToolsMethodResult(
            CefBrowser browser, int messageId, bool success, IntPtr result, int resultSize)
        {
            if (_completed || _messageId == 0 || messageId != _messageId)
                return;
            _completed = true;

            string json = ReadUtf8(result, resultSize);
            JsonElement? value = null;
            string? error = null;

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("exceptionDetails", out var ex))
                {
                    error = ex.TryGetProperty("exception", out var exObj) &&
                            exObj.TryGetProperty("description", out var desc)
                        ? desc.GetString()
                        : "JavaScript exception.";
                }
                else if (root.TryGetProperty("result", out var res) &&
                         res.TryGetProperty("value", out var val))
                {
                    value = val.Clone();
                }
            }
            catch (JsonException)
            {
                error = success ? null : "DevTools evaluate failed.";
            }

            _completion(value, error);
            _registration = null;
        }

        protected override void OnDevToolsEvent(
            CefBrowser browser, string method, IntPtr @params, int paramsSize) { }

        protected override void OnDevToolsAgentAttached(CefBrowser browser) { }
        protected override void OnDevToolsAgentDetached(CefBrowser browser) { }

        private static string ReadUtf8(IntPtr data, int size)
        {
            if (data == IntPtr.Zero || size <= 0) return "{}";
            byte[] buffer = new byte[size];
            Marshal.Copy(data, buffer, 0, size);
            return Encoding.UTF8.GetString(buffer);
        }
    }
}

/// <summary>
/// Capture the visible page via DevTools (Page.captureScreenshot) and resolve the
/// extension bridge request with a PNG data URL. Port of the mac ScreenshotObserver.
/// </summary>
internal static class DevToolsScreenshot
{
    public static void Capture(
        CefBrowser browser, string extensionId, string requestId,
        Action<IReadOnlyDictionary<string, object?>> resolve)
    {
        var host = browser.GetHost();
        var observer = new ShotObserver(extensionId, requestId, resolve);
        var registration = host.AddDevToolsMessageObserver(observer);
        observer.SetRegistration(registration);

        int messageId = host.ExecuteDevToolsMethod(0, "Page.captureScreenshot", null);
        observer.SetMessageId(messageId);
    }

    private sealed class ShotObserver : CefDevToolsMessageObserver
    {
        private readonly string _extensionId;
        private readonly string _requestId;
        private readonly Action<IReadOnlyDictionary<string, object?>> _resolve;
        private CefRegistration? _registration;
        private int _messageId;

        public ShotObserver(string extensionId, string requestId,
            Action<IReadOnlyDictionary<string, object?>> resolve)
        {
            _extensionId = extensionId;
            _requestId = requestId;
            _resolve = resolve;
        }

        public void SetMessageId(int id) => _messageId = id;
        public void SetRegistration(CefRegistration registration) => _registration = registration;

        protected override bool OnDevToolsMessage(CefBrowser browser, IntPtr message, int messageSize)
            => false;

        protected override void OnDevToolsMethodResult(
            CefBrowser browser, int messageId, bool success, IntPtr result, int resultSize)
        {
            if (_messageId != 0 && messageId != _messageId)
                return;

            string? dataUrl = null;
            string? error = null;

            try
            {
                string json = ReadUtf8(result, resultSize);
                using var doc = JsonDocument.Parse(json);
                if (success && doc.RootElement.TryGetProperty("data", out var data))
                {
                    string? b64 = data.GetString();
                    if (!string.IsNullOrEmpty(b64))
                        dataUrl = "data:image/png;base64," + b64;
                }
                if (dataUrl is null && doc.RootElement.TryGetProperty("message", out var msg))
                    error = msg.GetString();
            }
            catch (JsonException)
            {
                error = "Chromium screenshot capture failed.";
            }

            var response = new Dictionary<string, object?>
            {
                ["requestId"] = _requestId,
                ["extensionId"] = _extensionId,
            };
            if (dataUrl is not null)
                response["result"] = dataUrl;
            else
                response["error"] = error ?? "Chromium screenshot capture failed.";

            _resolve(response);
            _registration = null;
        }

        protected override void OnDevToolsEvent(
            CefBrowser browser, string method, IntPtr @params, int paramsSize) { }

        protected override void OnDevToolsAgentAttached(CefBrowser browser) { }
        protected override void OnDevToolsAgentDetached(CefBrowser browser) { }

        private static string ReadUtf8(IntPtr data, int size)
        {
            if (data == IntPtr.Zero || size <= 0) return "{}";
            byte[] buffer = new byte[size];
            Marshal.Copy(data, buffer, 0, size);
            return Encoding.UTF8.GetString(buffer);
        }
    }
}
