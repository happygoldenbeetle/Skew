using System.Collections.Concurrent;
using Xilium.CefGlue;

namespace Mori.Cef;

/// <summary>
/// CefClient implementation for a single browser (tab). Direct port of the mac
/// BrowserClient (App/BrowserClient.h/.mm).
///
/// <para>
/// All navigation/display state is forwarded to an <see cref="IBrowserViewDelegate"/>,
/// which the view layer (<see cref="Controls.MoriBrowserView"/>) implements to
/// drive the WinUI chrome. CefGlue invokes these handlers on the CEF UI thread;
/// the delegate implementation is responsible for marshalling to the WinUI
/// dispatcher before touching UI (see <see cref="IBrowserViewDelegate"/>).
/// </para>
/// </summary>
public sealed class BrowserClient : CefClient
{
    // Process-wide auto-PiP preference, read when injecting the media agent into
    // newly loaded frames (mac MoriSetAutoPiPEnabled / MoriAutoPiPEnabled).
    private static volatile bool s_autoPiPEnabled;
    public static void SetAutoPiPEnabled(bool enabled) => s_autoPiPEnabled = enabled;
    public static bool AutoPiPEnabled => s_autoPiPEnabled;

    // Live download callbacks keyed by CEF download id, so a tab-less request can
    // be canceled later (mac MoriCancelDownload).
    private static readonly ConcurrentDictionary<uint, CefDownloadItemCallback> s_downloads = new();

    public static bool CancelDownload(uint downloadId)
    {
        if (s_downloads.TryGetValue(downloadId, out var cb))
        {
            cb.Cancel();
            return true;
        }
        return false;
    }

    private IBrowserViewDelegate? _delegate; // not owned; cleared via Detach.
    private int _extensionTabId = -1;

    private readonly MoriLifeSpanHandler _lifeSpan;
    private readonly MoriLoadHandler _load;
    private readonly MoriDisplayHandler _display;
    private readonly MoriDownloadHandler _download;
    private readonly MoriJSDialogHandler _jsDialog;
    private readonly MoriFindHandler _find;
    private readonly MoriKeyboardHandler _keyboard;
    private readonly MoriRequestHandler _request;
    private readonly MoriContextMenuHandler _contextMenu;

    public BrowserClient(IBrowserViewDelegate viewDelegate)
    {
        _delegate = viewDelegate;
        _lifeSpan = new MoriLifeSpanHandler(this);
        _load = new MoriLoadHandler(this);
        _display = new MoriDisplayHandler(this);
        _download = new MoriDownloadHandler(this);
        _jsDialog = new MoriJSDialogHandler(this);
        _find = new MoriFindHandler(this);
        _keyboard = new MoriKeyboardHandler(this);
        _request = new MoriRequestHandler(this);
        _contextMenu = new MoriContextMenuHandler(this);
    }

    /// <summary>Detach when the hosting view goes away to avoid dangling callbacks.</summary>
    public void DetachDelegate() => _delegate = null;

    public void SetExtensionTabId(int tabId) => _extensionTabId = tabId;
    internal int ExtensionTabId => _extensionTabId;
    internal IBrowserViewDelegate? Delegate => _delegate;

    // ── CefClient handler accessors ──────────────────────────────────────

    protected override CefLifeSpanHandler GetLifeSpanHandler() => _lifeSpan;
    protected override CefLoadHandler GetLoadHandler() => _load;
    protected override CefDisplayHandler GetDisplayHandler() => _display;
    protected override CefDownloadHandler GetDownloadHandler() => _download;
    protected override CefJSDialogHandler GetJSDialogHandler() => _jsDialog;
    protected override CefFindHandler GetFindHandler() => _find;
    protected override CefKeyboardHandler GetKeyboardHandler() => _keyboard;
    protected override CefRequestHandler GetRequestHandler() => _request;
    protected override CefContextMenuHandler GetContextMenuHandler() => _contextMenu;

    // ── Agent injection helpers (mac OnLoadStart/OnLoadEnd) ───────────────

    internal static void InjectPasskeyShim(CefFrame frame)
    {
        // Injected as early as possible — before page scripts can capture the
        // original navigator.credentials methods (mac OnLoadStart).
        string js = MoriAgentScripts.PasskeyAgent;
        frame.ExecuteJavaScript(js, frame.Url, 0);
    }

    internal static void InjectMediaAgent(CefFrame frame)
    {
        // Injected once a frame finishes loading (mac OnLoadEnd). The auto-PiP
        // attribute is only set when the process-wide preference is enabled.
        string js = MoriAgentScripts.MediaAgent(s_autoPiPEnabled);
        frame.ExecuteJavaScript(js, frame.Url, 0);
    }

    internal static void RegisterDownload(uint id, CefDownloadItemCallback callback)
        => s_downloads[id] = callback;

    internal static void ForgetDownload(uint id) => s_downloads.TryRemove(id, out _);
}
