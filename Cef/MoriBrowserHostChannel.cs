using Xilium.CefGlue;

namespace Mori.Cef;

/// <summary>
/// Host-side sink for CEF callbacks that need native UI or app-level routing
/// (JS dialogs, keyboard shortcuts, downloads, auth, console markers).
///
/// <para>
/// On mac these were handled inline in BrowserClient.mm because the CEF UI
/// thread is the AppKit main thread. On Windows the handlers may run off the
/// WinUI dispatcher, so this channel is the single, CEF-free seam the app wires
/// up at startup; every callback is hopped onto the WinUI dispatcher by the
/// registered handlers themselves.
/// </para>
///
/// <para>
/// The CEF layer never references XAML; the app layer registers delegates here.
/// </para>
/// </summary>
public static class MoriBrowserHostChannel
{
    /// <summary>Return true if the app consumed the shortcut (mac OnPreKeyEvent).</summary>
    public static Func<int, CefEventFlags, bool>? ShortcutHandler;

    /// <summary>Present a JS alert/confirm/prompt natively, then continue the callback.</summary>
    public static Action<CefBrowser, CefJSDialogType, string, string, CefJSDialogCallback>? JSDialogHandler;

    /// <summary>Present a beforeunload confirmation, then continue the callback.</summary>
    public static Action<CefBrowser, string, CefJSDialogCallback>? BeforeUnloadHandler;

    /// <summary>Report download progress (id, url, path, received, total, percent 0-100, speed, complete, canceled).</summary>
    public static Action<uint, string, string, long, long, int, long, bool, bool>? DownloadUpdateHandler;

    /// <summary>Present an auth prompt; return true if handled asynchronously.</summary>
    public static Func<CefBrowser, string, int, string, bool, CefAuthCallback, bool>? AuthHandler;

    /// <summary>Structured console markers from injected agents (media/extension).</summary>
    public static Action<CefBrowser, string>? ConsoleMarkerHandler;

    internal static bool HandleShortcut(int windowsKeyCode, CefEventFlags modifiers)
        => ShortcutHandler?.Invoke(windowsKeyCode, modifiers) ?? false;

    internal static void HandleJSDialog(
        CefBrowser browser, CefJSDialogType type, string message, string defaultPrompt,
        CefJSDialogCallback callback)
    {
        if (JSDialogHandler is { } h)
            h(browser, type, message, defaultPrompt, callback);
        else
            callback.Continue(false, null); // No host UI wired: dismiss safely.
    }

    internal static void HandleBeforeUnloadDialog(
        CefBrowser browser, string message, CefJSDialogCallback callback)
    {
        if (BeforeUnloadHandler is { } h)
            h(browser, message, callback);
        else
            callback.Continue(true, null);
    }

    internal static void HandleDownloadUpdate(
        uint id, string url, string path, long received, long total, int percent, long speed, bool complete, bool canceled)
        => DownloadUpdateHandler?.Invoke(id, url ?? "", path ?? "", received, total, percent, speed, complete, canceled);

    internal static bool HandleAuthCredentials(
        CefBrowser browser, string host, int port, string realm, bool isProxy,
        CefAuthCallback callback)
    {
        if (AuthHandler is { } h)
            return h(browser, host, port, realm, isProxy, callback);
        callback.Cancel();
        return true;
    }

    internal static void HandleConsoleMarker(CefBrowser browser, string message)
        => ConsoleMarkerHandler?.Invoke(browser, message);
}
