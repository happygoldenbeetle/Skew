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
        
        // Inject extension content scripts.
        BrowserClient.InjectContentScripts(frame);

        // If this is an extension page (mori-extension://), inject the runtime
        // shim and background scripts so chrome.contextMenus etc. work.
        BrowserClient.InjectExtensionPageShim(frame);

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

        // Extension API bridge: intercept __MORI_EXTENSION__ prefixed messages
        const string kExtPrefix = "__MORI_EXTENSION__";
        if (message is not null && message.StartsWith(kExtPrefix, StringComparison.Ordinal))
        {
            try
            {
                var json = message.Substring(kExtPrefix.Length);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;
                var requestId = root.GetProperty("requestId").GetString() ?? "";
                var extensionId = root.GetProperty("extensionId").GetString() ?? "";
                var method = root.GetProperty("method").GetString() ?? "";
                var args = root.GetProperty("args");

                var response = ExtensionBridge.HandleRequest(requestId, extensionId, method, args);
                if (response != null)
                {
                    var responseJson = System.Text.Json.JsonSerializer.Serialize(response);
                    var js = $"if(window.__moriExtResolve)window.__moriExtResolve({responseJson});";

                    var frameIds = browser.GetFrameIdentifiers();
                    foreach (var fid in frameIds)
                    {
                        var f = browser.GetFrame(fid);
                        f?.ExecuteJavaScript(js, f.Url, 0);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Extension bridge error: {ex.Message}");
            }
            return true;
        }

        return false;
    }


    protected override bool OnCursorChange(CefBrowser browser, IntPtr cursorHandle, CefCursorType type, CefCursorInfo customCursorInfo)
    {
        _client.Delegate?.OnCursorChange(type);
        return true;
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
            id, downloadItem.Url, downloadItem.FullPath, downloadItem.ReceivedBytes, downloadItem.TotalBytes, downloadItem.PercentComplete, downloadItem.CurrentSpeed,
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
        bool alt = (keyEvent.Modifiers & CefEventFlags.AltDown) != 0;
        
        bool isSpecialSingleKey = keyEvent.WindowsKeyCode == (int)Windows.System.VirtualKey.F11 ||
                                  keyEvent.WindowsKeyCode == (int)Windows.System.VirtualKey.F12 ||
                                  keyEvent.WindowsKeyCode == (int)Windows.System.VirtualKey.Escape;

        if (!ctrl && !alt && !isSpecialSingleKey)
            return false;

        bool handled = MoriBrowserHostChannel.HandleShortcut(
            keyEvent.WindowsKeyCode, keyEvent.Modifiers);
        return handled; // true => don't deliver to the page.
    }
}

internal sealed class MoriFocusHandler : CefFocusHandler
{
    private readonly BrowserClient _client;
    public MoriFocusHandler(BrowserClient client) => _client = client;

    protected override void OnGotFocus(CefBrowser browser)
    {
        Mori.MainWindow.Instance.DispatcherQueue?.TryEnqueue(() =>
        {
            if (Mori.Models.BrowserStore.Shared.FindBarVisible)
            {
                Mori.Models.BrowserStore.Shared.FindBarVisible = false;
            }
        });
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
    // Map from CefMenuModel command ID → (extensionId, menuItemId) for dispatch
    private readonly Dictionary<int, (string extensionId, string itemId)> _extensionMenuMap = new();

    public MoriContextMenuHandler(BrowserClient client) => _client = client;

    protected override void OnBeforeContextMenu(
        CefBrowser browser, CefFrame frame, CefContextMenuParams state, CefMenuModel model)
    {
        bool isImage = state.HasImageContents;
        bool isLink = !string.IsNullOrEmpty(state.UnfilteredLinkUrl);

        if (isImage || isLink)
        {
            // Remove generic CEF items that don't belong on links/images
            model.Remove((int)CefMenuId.Back);
            model.Remove((int)CefMenuId.Forward);
            model.Remove((int)CefMenuId.Reload);
            model.Remove((int)CefMenuId.ReloadNoCache);
            model.Remove((int)CefMenuId.StopLoad);
            model.Remove((int)CefMenuId.Print);
            model.Remove((int)CefMenuId.ViewSource);
            model.Remove((int)CefMenuId.Undo);
            model.Remove((int)CefMenuId.Redo);
            model.Remove((int)CefMenuId.Cut);
            model.Remove((int)CefMenuId.Copy);
            model.Remove((int)CefMenuId.Paste);
            model.Remove((int)CefMenuId.Delete);
            model.Remove((int)CefMenuId.SelectAll);
            model.Remove((int)CefMenuId.Find);
        }

        if (isImage)
        {
            // Everything Chromium offered for an image goes; these are the six
            // that are actually wanted, in this order.
            model.Clear();
            model.AddItem((int)CefMenuId.CustomFirst + 4, "Open image in new tab");
            model.AddItem((int)CefMenuId.CustomFirst + 5, "Save image as...");
            model.AddItem((int)CefMenuId.CustomFirst + 13, "Copy image");
            model.AddItem((int)CefMenuId.CustomFirst + 6, "Copy image address");
            model.AddItem((int)CefMenuId.CustomFirst + 14, "Search Google for image");
        }

        if (isLink)
        {
            model.InsertSeparatorAt(0);
            model.InsertItemAt(0, (int)CefMenuId.CustomFirst + 3, "Copy link address");
            model.InsertItemAt(0, (int)CefMenuId.CustomFirst + 2, "Save link as...");
            model.InsertSeparatorAt(0);
            model.InsertItemAt(0, (int)CefMenuId.CustomFirst + 9, "Open link in incognito window");
            model.InsertItemAt(0, (int)CefMenuId.CustomFirst + 8, "Open link in new window");
            model.InsertItemAt(0, (int)CefMenuId.CustomFirst + 1, "Open link in new tab");
        }

        // ── Plain page click ──────────────────────────────────────────────
        //
        // Chromium's own page menu is a grab bag that changes with the build,
        // so it is replaced outright rather than patched. Only for a bare click:
        // a caret in a text box or a live selection still gets Chromium's
        // editing items, which are the ones that belong there.
        bool isEditable = (state.ContextMenuType & CefContextMenuTypeFlags.Editable) != 0;
        bool hasSelection = (state.ContextMenuType & CefContextMenuTypeFlags.Selection) != 0;

        if (!isImage && !isLink && !isEditable && !hasSelection)
        {
            model.Clear();

            model.AddItem((int)CefMenuId.Back, "Back");
            model.SetEnabled((int)CefMenuId.Back, browser.CanGoBack);
            model.AddItem((int)CefMenuId.Forward, "Forward");
            model.SetEnabled((int)CefMenuId.Forward, browser.CanGoForward);
            model.AddItem((int)CefMenuId.Reload, "Reload");

            model.AddSeparator();
            model.AddItem((int)CefMenuId.CustomFirst + 10, "Save as...");
            model.AddItem((int)CefMenuId.Print, "Print...");
            model.AddItem((int)CefMenuId.CustomFirst + 12, "Translate to English");

            model.AddSeparator();
            model.AddItem((int)CefMenuId.ViewSource, "View page source");
        }

        // Add Inspect at the bottom
        model.AddSeparator();
        model.AddItem((int)CefMenuId.CustomFirst + 7, "Inspect");

        // Clean up any consecutive/trailing separators left over from removing generic items
        bool lastWasSeparator = true; // true initially to strip leading separators
        for (int i = 0; i < (int)model.Count;)
        {
            nuint idx = (nuint)i;
            if (model.GetItemTypeAt(idx) == CefMenuItemType.Separator)
            {
                if (lastWasSeparator)
                {
                    model.RemoveAt(idx);
                }
                else
                {
                    lastWasSeparator = true;
                    i++;
                }
            }
            else
            {
                lastWasSeparator = false;
                i++;
            }
        }
        
        // Strip trailing separator
        if ((int)model.Count > 0 && model.GetItemTypeAt((nuint)((int)model.Count - 1)) == CefMenuItemType.Separator)
        {
            model.RemoveAt((nuint)((int)model.Count - 1));
        }

        // ── Extension context menu items ──────────────────────────────────
        try
        {
            _extensionMenuMap.Clear();
            string pageUrl = frame.Url ?? "";
            string? linkUrl = string.IsNullOrEmpty(state.UnfilteredLinkUrl) ? null : state.UnfilteredLinkUrl;
            string? mediaType = isImage ? "image" : null;

            var extItems = ExtensionBridge.GetMenuItemsForContext(pageUrl, linkUrl, mediaType);
            if (extItems.Count > 0)
            {
                model.AddSeparator();
                int cmdBase = (int)CefMenuId.CustomFirst + 100;
                for (int i = 0; i < extItems.Count && i < 50; i++)
                {
                    int cmdId = cmdBase + i;
                    // Substitute %s in title with selection text
                    var title = extItems[i].title;
                    var selText = state.SelectionText;
                    if (!string.IsNullOrEmpty(selText))
                        title = title.Replace("%s", selText);
                    
                    if (string.IsNullOrEmpty(title))
                        title = "Extension Item";

                    model.AddItem(cmdId, title);
                    _extensionMenuMap[cmdId] = (extItems[i].extensionId, extItems[i].itemId);
                }
            }
        }
        catch (Exception ex)
        {
            System.IO.File.AppendAllText("crash.log", $"[CRASH OnBeforeContextMenu] {ex}\n");
        }
    }

    protected override bool OnContextMenuCommand(
        CefBrowser browser, CefFrame frame, CefContextMenuParams state, int commandId, CefEventFlags eventFlags)
    {
        if (commandId == (int)CefMenuId.CustomFirst + 1) // Open link in new tab
        {
            _client.Delegate?.OnOpenUrlFromTab(state.UnfilteredLinkUrl);
            return true;
        }
        if (commandId == (int)CefMenuId.CustomFirst + 8) // Open link in new window
        {
            // Fallback to new tab for now since we are single-window
            _client.Delegate?.OnOpenUrlFromTab(state.UnfilteredLinkUrl);
            return true;
        }
        if (commandId == (int)CefMenuId.CustomFirst + 9) // Open link in incognito window
        {
            // Fallback to new tab for now
            _client.Delegate?.OnOpenUrlFromTab(state.UnfilteredLinkUrl);
            return true;
        }
        if (commandId == (int)CefMenuId.CustomFirst + 2) // Save link as
        {
            browser.GetHost().StartDownload(state.UnfilteredLinkUrl);
            return true;
        }
        if (commandId == (int)CefMenuId.CustomFirst + 3) // Copy link address
        {
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(state.UnfilteredLinkUrl);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            return true;
        }
        if (commandId == (int)CefMenuId.CustomFirst + 4) // Open image in new tab
        {
            _client.Delegate?.OnOpenUrlFromTab(state.SourceUrl);
            return true;
        }
        if (commandId == (int)CefMenuId.CustomFirst + 5) // Save image as
        {
            browser.GetHost().StartDownload(state.SourceUrl);
            return true;
        }
        if (commandId == (int)CefMenuId.CustomFirst + 6) // Copy image address
        {
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(state.SourceUrl);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            return true;
        }
        if (commandId == (int)CefMenuId.CustomFirst + 13) // Copy image
        {
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            try
            {
                // The bitmap itself, so it can be pasted into anything that
                // takes an image rather than only into a text field.
                dp.SetBitmap(
                    Windows.Storage.Streams.RandomAccessStreamReference.CreateFromUri(
                        new Uri(state.SourceUrl)));
            }
            catch (Exception)
            {
                // data: URIs and anything the URI parser refuses fall back to
                // the address, which is better than an empty clipboard.
                dp.SetText(state.SourceUrl);
            }
            try { Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp); } catch (Exception) { }
            return true;
        }
        if (commandId == (int)CefMenuId.CustomFirst + 14) // Search Google for image
        {
            _client.Delegate?.OnOpenUrlFromTab(
                "https://lens.google.com/uploadbyurl?url=" + Uri.EscapeDataString(state.SourceUrl));
            return true;
        }
        if (commandId == (int)CefMenuId.CustomFirst + 10) // Save as...
        {
            // The document, fetched again by the network stack — CEF has no
            // "save page with its resources", so this is the page itself.
            string pageUrl = frame.Url ?? "";
            if (!string.IsNullOrEmpty(pageUrl))
                browser.GetHost().StartDownload(pageUrl);
            return true;
        }
        if (commandId == (int)CefMenuId.CustomFirst + 12) // Translate to English
        {
            // Chromium's own translate is a Google service that CEF leaves out,
            // so this is the same service by its public route.
            string pageUrl = frame.Url ?? "";
            if (!string.IsNullOrEmpty(pageUrl))
            {
                _client.Delegate?.OnOpenUrlFromTab(
                    "https://translate.google.com/translate?sl=auto&tl=en&u=" +
                    Uri.EscapeDataString(pageUrl));
            }
            return true;
        }
        if (commandId == (int)CefMenuId.CustomFirst + 7) // Inspect
        {
            var windowInfo = CefWindowInfo.Create();
            windowInfo.SetAsPopup(App.WindowHandle, "DevTools");
            windowInfo.Style = (Xilium.CefGlue.Platform.Windows.WindowStyle)((uint)windowInfo.Style | 0x10000000); // WS_VISIBLE
            windowInfo.Bounds = new CefRectangle(0, 0, 800, 600);
            browser.GetHost().ShowDevTools(windowInfo, new BrowserClient(null!), new CefBrowserSettings(), new CefPoint(state.X, state.Y));
            return true;
        }

        // Extension context menu items
        if (_extensionMenuMap.TryGetValue(commandId, out var extInfo))
        {
            // Build the info object that chrome.contextMenus.onClicked expects
            var clickInfo = new Dictionary<string, object?>
            {
                ["menuItemId"] = extInfo.itemId,
                ["pageUrl"] = frame.Url ?? "",
            };
            if (!string.IsNullOrEmpty(state.UnfilteredLinkUrl))
                clickInfo["linkUrl"] = state.UnfilteredLinkUrl;
            if (state.HasImageContents && !string.IsNullOrEmpty(state.SourceUrl))
                clickInfo["srcUrl"] = state.SourceUrl;
            if (!string.IsNullOrEmpty(state.SelectionText))
                clickInfo["selectionText"] = state.SelectionText;

            var infoJson = System.Text.Json.JsonSerializer.Serialize(clickInfo);
            var tabJson = System.Text.Json.JsonSerializer.Serialize(new { id = 1, url = frame.Url ?? "" });
            var js = $"if(chrome&&chrome.contextMenus&&chrome.contextMenus.onClicked&&chrome.contextMenus.onClicked._fire)chrome.contextMenus.onClicked._fire({infoJson},{tabJson});";

            // Dispatch to all frames in this browser
            var frameIds = browser.GetFrameIdentifiers();
            foreach (var fid in frameIds)
            {
                var f = browser.GetFrame(fid);
                f?.ExecuteJavaScript(js, f.Url, 0);
            }
            return true;
        }

        return false;
    }

    protected override bool RunContextMenu(
        CefBrowser browser, CefFrame frame, CefContextMenuParams state, CefMenuModel model,
        CefRunContextMenuCallback callback)
    {
        // Mori's own pages get no page menu. The new tab page is chrome wearing
        // a page's clothes: Back, View page source and Inspect are all aimed at
        // a site, and there is no site here.
        string frameUrl = frame.Url ?? "";
        if (frameUrl.StartsWith("mori://", StringComparison.OrdinalIgnoreCase))
        {
            callback.Cancel();
            return true;
        }

        var items = ParseMenuModel(model);
        var args = new BrowserContextMenuEventArgs
        {
            X = state.X,
            Y = state.Y,
            Items = items,
            Callback = (cmdId) =>
            {
                if (cmdId.HasValue)
                    callback.Continue(cmdId.Value, CefEventFlags.None);
                else
                    callback.Cancel();
            }
        };

        _client.InvokeContextMenu(browser, frame, args);
        return true; // Return true to indicate we handled the context menu display
    }

    private List<ContextMenuItemModel> ParseMenuModel(CefMenuModel model)
    {
        var items = new List<ContextMenuItemModel>();
        for (int i = 0; i < (int)model.Count; i++)
        {
            nuint idx = (nuint)i;
            var item = new ContextMenuItemModel
            {
                CommandId = model.GetCommandIdAt(idx),
                Label = model.GetLabelAt(idx),
                Type = model.GetItemTypeAt(idx),
                IsEnabled = model.IsEnabledAt(idx),
                IsChecked = model.IsCheckedAt(idx),
                IsVisible = model.IsVisibleAt(idx)
            };

            if (item.Type == CefMenuItemType.SubMenu)
            {
                var subModel = model.GetSubMenuAt(idx);
                if (subModel != null)
                {
                    item.SubMenuItems = ParseMenuModel(subModel);
                }
            }
            items.Add(item);
        }
        return items;
    }

}
