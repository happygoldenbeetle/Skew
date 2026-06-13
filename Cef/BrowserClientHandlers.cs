using Xilium.CefGlue;

namespace Mori.Cef;

// Per-tab CEF handlers. CefGlue splits the single mac BrowserClient (which
// multiply-inherited every handler) into separate handler objects returned from
// BrowserClient.Get*Handler(). Each forwards to the BrowserClient's
// IBrowserViewDelegate, exactly as the mac handler methods did.

internal sealed class MoriLifeSpanHandler : CefLifeSpanHandler
{
    private readonly BrowserClient _client;
    public MoriLifeSpanHandler(BrowserClient client) => _client = client;

    protected override bool OnBeforePopup(
        CefBrowser browser, CefFrame frame, string targetUrl,
        string targetFrameName, CefWindowOpenDisposition targetDisposition,
        bool userGesture, CefPopupFeatures popupFeatures, CefWindowInfo windowInfo,
        ref CefClient client, CefBrowserSettings settings,
        ref CefDictionaryValue extraInfo, ref bool noJavascriptAccess)
    {
        // Route popups / target=_blank into Mori chrome instead of letting CEF
        // create a top-level native window (mac OnBeforePopup + OnOpenURLFromTab).
        bool consumed = _client.Delegate?.OnOpenUrlFromTab(targetUrl) ?? false;
        return consumed; // true => cancel the CEF-created popup.
    }

    protected override void OnAfterCreated(CefBrowser browser)
        => _client.Delegate?.OnAfterCreated(browser);

    protected override void OnBeforeClose(CefBrowser browser)
        => _client.Delegate?.OnBeforeClose(browser);
}

internal sealed class MoriLoadHandler : CefLoadHandler
{
    private readonly BrowserClient _client;
    public MoriLoadHandler(BrowserClient client) => _client = client;

    protected override void OnLoadingStateChange(
        CefBrowser browser, bool isLoading, bool canGoBack, bool canGoForward)
        => _client.Delegate?.OnLoadingStateChange(isLoading, canGoBack, canGoForward);

    protected override void OnLoadStart(
        CefBrowser browser, CefFrame frame, CefTransitionType transitionType)
    {
        // Inject the WebAuthn/passkey shim before page scripts run.
        BrowserClient.InjectPasskeyShim(frame);
        if (frame.IsMain)
            _client.Delegate?.OnLoadStart(frame.Url);
    }

    protected override void OnLoadEnd(CefBrowser browser, CefFrame frame, int httpStatusCode)
    {
        // Inject the media/PiP agent into each frame once it finishes loading.
        BrowserClient.InjectMediaAgent(frame);
        if (frame.IsMain)
            _client.Delegate?.OnLoadEnd(frame.Url, httpStatusCode);
    }

    protected override void OnLoadError(
        CefBrowser browser, CefFrame frame, CefErrorCode errorCode,
        string errorText, string failedUrl)
    {
        if (frame.IsMain)
            _client.Delegate?.OnLoadError((int)errorCode, errorText ?? "", failedUrl ?? "");
    }
}

internal sealed class MoriDisplayHandler : CefDisplayHandler
{
    private readonly BrowserClient _client;
    public MoriDisplayHandler(BrowserClient client) => _client = client;

    protected override void OnTitleChange(CefBrowser browser, string title)
        => _client.Delegate?.OnTitleChange(title ?? "");

    protected override void OnAddressChange(CefBrowser browser, CefFrame frame, string url)
    {
        if (frame.IsMain)
            _client.Delegate?.OnAddressChange(url ?? "");
    }

    protected override void OnFaviconUrlChange(CefBrowser browser, string[] iconUrls)
        => _client.Delegate?.OnFaviconUrlChange(iconUrls ?? Array.Empty<string>());

    protected override bool OnConsoleMessage(
        CefBrowser browser, CefLogSeverity level, string message, string source, int line)
    {
        if (message is not null &&
            (message.StartsWith("__MORI_MEDIA__", StringComparison.Ordinal) ||
             message.StartsWith("__MORI_EXT__", StringComparison.Ordinal)))
        {
            MoriBrowserHostChannel.HandleConsoleMarker(browser, message);
            return true;
        }
        return false;
    }
}

internal sealed class MoriDownloadHandler : CefDownloadHandler
{
    private readonly BrowserClient _client;
    public MoriDownloadHandler(BrowserClient client) => _client = client;

    protected override void OnBeforeDownload(
        CefBrowser browser, CefDownloadItem downloadItem, string suggestedName,
        CefBeforeDownloadCallback callback)
    {
        // Auto-save to the user's Downloads folder (mac auto-save behavior).
        string downloads = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string target = Path.Combine(downloads, "Downloads", suggestedName);
        callback.Continue(target, showDialog: false);
    }

    protected override void OnDownloadUpdated(
        CefBrowser browser, CefDownloadItem downloadItem, CefDownloadItemCallback callback)
    {
        uint id = downloadItem.Id;
        if (downloadItem.IsComplete || downloadItem.IsCanceled)
            BrowserClient.ForgetDownload(id);
        else
            BrowserClient.RegisterDownload(id, callback);

        MoriBrowserHostChannel.HandleDownloadUpdate(
            id, downloadItem.FullPath, downloadItem.PercentComplete,
            downloadItem.IsComplete, downloadItem.IsCanceled);
    }
}

internal sealed class MoriJSDialogHandler : CefJSDialogHandler
{
    private readonly BrowserClient _client;
    public MoriJSDialogHandler(BrowserClient client) => _client = client;

    protected override bool OnJSDialog(
        CefBrowser browser, string originUrl, CefJSDialogType dialogType,
        string messageText, string defaultPromptText, CefJSDialogCallback callback,
        out bool suppressMessage)
    {
        suppressMessage = false;
        MoriBrowserHostChannel.HandleJSDialog(
            browser, dialogType, messageText ?? "", defaultPromptText ?? "", callback);
        return true; // We handle presentation.
    }

    protected override bool OnBeforeUnloadDialog(
        CefBrowser browser, string messageText, bool isReload, CefJSDialogCallback callback)
    {
        MoriBrowserHostChannel.HandleBeforeUnloadDialog(browser, messageText ?? "", callback);
        return true;
    }

    protected override void OnResetDialogState(CefBrowser browser) { }
    protected override void OnDialogClosed(CefBrowser browser) { }
}

internal sealed class MoriFindHandler : CefFindHandler
{
    private readonly BrowserClient _client;
    public MoriFindHandler(BrowserClient client) => _client = client;

    protected override void OnFindResult(
        CefBrowser browser, int identifier, int count, CefRectangle selectionRect,
        int activeMatchOrdinal, bool finalUpdate)
        => _client.Delegate?.OnFindResult(count, activeMatchOrdinal);
}

internal sealed class MoriKeyboardHandler : CefKeyboardHandler
{
    private readonly BrowserClient _client;
    public MoriKeyboardHandler(BrowserClient client) => _client = client;

    protected override bool OnPreKeyEvent(
        CefBrowser browser, CefKeyEvent keyEvent, IntPtr osEvent, out bool isKeyboardShortcut)
    {
        isKeyboardShortcut = false;

        if (keyEvent.EventType != CefKeyEventType.RawKeyDown)
            return false;

        bool ctrl = (keyEvent.Modifiers & CefEventFlags.ControlDown) != 0;
        if (!ctrl)
            return false;

        bool handled = MoriBrowserHostChannel.HandleShortcut(
            keyEvent.WindowsKeyCode, keyEvent.Modifiers);
        return handled; // true => don't deliver to the page.
    }
}

internal sealed class MoriRequestHandler : CefRequestHandler
{
    private readonly BrowserClient _client;
    private readonly MoriResourceRequestHandler _resource = new();
    public MoriRequestHandler(BrowserClient client) => _client = client;

    protected override bool OnBeforeBrowse(
        CefBrowser browser, CefFrame frame, CefRequest request, bool userGesture,
        bool isRedirect)
    {
        if (frame.IsMain)
            _client.Delegate?.OnBeforeBrowse(request.Url, isRedirect, userGesture);
        return false; // allow navigation
    }

    protected override bool OnOpenUrlFromTab(
        CefBrowser browser, CefFrame frame, string targetUrl,
        CefWindowOpenDisposition targetDisposition, bool userGesture)
        => _client.Delegate?.OnOpenUrlFromTab(targetUrl) ?? false;

    protected override CefResourceRequestHandler? GetResourceRequestHandler(
        CefBrowser browser, CefFrame frame, CefRequest request, bool isNavigation,
        bool isDownload, string requestInitiator, ref bool disableDefaultHandling)
        => _resource;

    protected override bool GetAuthCredentials(
        CefBrowser browser, string originUrl, bool isProxy, string host, int port,
        string realm, string scheme, CefAuthCallback callback)
    {
        return MoriBrowserHostChannel.HandleAuthCredentials(
            browser, host, port, realm ?? "", isProxy, callback);
    }
}

internal sealed class MoriResourceRequestHandler : CefResourceRequestHandler
{
    protected override CefCookieAccessFilter? GetCookieAccessFilter(
        CefBrowser browser, CefFrame frame, CefRequest request)
        => null;
}

internal sealed class MoriContextMenuHandler : CefContextMenuHandler
{
    private readonly BrowserClient _client;
    public MoriContextMenuHandler(BrowserClient client) => _client = client;

    protected override void OnBeforeContextMenu(
        CefBrowser browser, CefFrame frame, CefContextMenuParams state, CefMenuModel model)
    {
    }

    protected override bool OnContextMenuCommand(
        CefBrowser browser, CefFrame frame, CefContextMenuParams state, int commandId,
        CefEventFlags eventFlags)
        => false;
}
