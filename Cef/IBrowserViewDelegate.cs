using Xilium.CefGlue;

namespace Mori.Cef;

/// <summary>
/// Pure C# sink the hosting view (MoriBrowserView) implements. Direct port of
/// the mac BrowserClientDelegate (BrowserClient.h).
///
/// <para>
/// CefGlue invokes <see cref="BrowserClient"/> handlers on the CEF UI thread.
/// Unlike the mac build — where the CEF UI thread IS the AppKit main thread —
/// on Windows we run an external message pump on the WinUI thread, but callbacks
/// may still arrive off the dispatcher. The implementation of this interface is
/// responsible for marshalling to <c>App.DispatcherQueue</c> before touching UI
/// state, exactly as the mac ViewClientDelegate hops to the main thread.
/// </para>
///
/// <para>
/// This interface is intentionally free of any heavyweight CEF types in its
/// "leaf" signatures (it uses plain <see cref="string"/>/<see cref="bool"/>),
/// so the view layer never needs to understand Chromium. The single
/// <see cref="CefBrowser"/> handles in the lifecycle callbacks are unavoidable
/// because the view must retain the browser to drive it.
/// </para>
/// </summary>
public interface IBrowserViewDelegate
{
    void OnAfterCreated(CefBrowser browser);
    void OnBeforeClose(CefBrowser browser);

    void OnTitleChange(string title);
    void OnAddressChange(string url);
    void OnLoadingStateChange(bool isLoading, bool canGoBack, bool canGoForward);
    void OnFaviconUrlChange(IReadOnlyList<string> iconUrls);

    void OnBeforeBrowse(string url, bool isRedirect, bool userGesture);
    void OnLoadStart(string url);
    void OnLoadEnd(string url, int httpStatusCode);
    void OnLoadError(int errorCode, string errorText, string failedUrl);

    /// <summary>
    /// A popup / target=_blank URL that should be routed into Mori chrome as a
    /// new tab instead of a CEF-created top-level native window. Return true to
    /// indicate Mori consumed it.
    /// </summary>
    bool OnOpenUrlFromTab(string targetUrl);

    /// <summary>Find-in-page progress: total match count and 1-based active index.</summary>
    void OnFindResult(int count, int activeMatchOrdinal);
}
