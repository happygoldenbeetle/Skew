using CommunityToolkit.Mvvm.ComponentModel;

namespace Skew.Models;

/// <summary>
/// One browser tab. Port of BrowserTab.swift — UI state only for now.
/// The native CEF browser view will be added in the backend phase.
/// </summary>
public partial class BrowserTab : ObservableObject
{
    public Guid Id { get; }

    [ObservableProperty]
    private string _title;

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    [ObservableProperty]
    private string _urlString;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _canGoBack;

    [ObservableProperty]
    private bool _canGoForward;

    [ObservableProperty]
    private string? _faviconUrl;

    [ObservableProperty]
    private bool _didFail;

    [ObservableProperty]
    private int _zoomPercent = 100;

    [ObservableProperty]
    private Microsoft.UI.Xaml.Media.SolidColorBrush _accentColorBrush;

    /// <summary>
    /// The address shown in the omnibox when the user is not editing.
    /// </summary>
    public string DisplayUrl
    {
        get
        {
            if (UrlString == "about:blank") return "";
            if (UrlString.StartsWith("skew://")) return "";
            if (System.Uri.TryCreate(UrlString, System.UriKind.Absolute, out var uri))
            {
                var host = uri.Host;
                if (host.StartsWith("www.")) host = host.Substring(4);
                return host;
            }
            return UrlString;
        }
    }

    public bool IsInternal => UrlString?.StartsWith("skew://") == true;

    public BrowserTab(string url = "about:blank", string title = "New Tab")
        : this(Guid.NewGuid(), url, title)
    {
    }

    /// <summary>
    /// Rebuild a tab with a known id. Session restore needs this: the saved file
    /// records pinned/folder/loose membership as tab ids, so a restored tab must
    /// come back under its original id for that membership to resolve.
    ///
    /// <para>
    /// <paramref name="faviconUrl"/> is what the tab last displayed. Without it a
    /// restored tab shows its initial letter instead of its icon, because the
    /// icon is normally only learned by loading the page — and a restored tab
    /// that is never selected never loads.
    /// </para>
    /// </summary>
    public BrowserTab(Guid id, string url, string title, string? faviconUrl = null)
    {
        Id = id;
        _title = title;
        _urlString = url;
        _faviconUrl = string.IsNullOrWhiteSpace(faviconUrl) ? DeriveFaviconUrl(url) : faviconUrl;
        _accentColorBrush = Skew.Helpers.ColorUtils.GetColorFromUrl(url);
    }

    /// <summary>
    /// A host-derived icon URL, used until the page reports its real favicon.
    /// Returns null for internal and non-http(s) pages, which have no icon to
    /// look up.
    /// </summary>
    internal static string? DeriveFaviconUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.StartsWith("skew://", StringComparison.Ordinal))
            return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return null;

        return $"https://www.google.com/s2/favicons?domain={uri.Host}&sz=128";
    }

    // ── CEF-backed browser view ──

    private Skew.Controls.SkewBrowserView? _browserView;

    /// <summary>
    /// The per-tab CEF browser view, created on first access. The host inserts
    /// this into the web-content card; the underlying browser is created lazily
    /// once the view is loaded and sized (mac SkewBrowserView lifecycle).
    /// </summary>
    public Skew.Controls.SkewBrowserView BrowserView
    {
        get
        {
            if (_browserView is null)
            {
                _browserView = new Skew.Controls.SkewBrowserView(UrlString);
                WireBrowserEvents(_browserView);
                OnPropertyChanged(nameof(HasBrowserView));
            }
            return _browserView;
        }
    }

    /// <summary>True once the browser view has been materialized for this tab.</summary>
    public bool HasBrowserView => _browserView is not null;

    private void WireBrowserEvents(Skew.Controls.SkewBrowserView view)
    {
        view.TitleChanged += t => Title = string.IsNullOrEmpty(t) ? "Untitled" : t;
        view.UrlChanged += u =>
        {
            UrlString = u;
            _accentColorBrush = Skew.Helpers.ColorUtils.GetColorFromUrl(u);
            OnPropertyChanged(nameof(DisplayUrl));
            OnPropertyChanged(nameof(IsInternal));
            OnPropertyChanged(nameof(AccentColorBrush));
            var derived = DeriveFaviconUrl(u);
            if (derived is not null)
                FaviconUrl = derived;
        };
        view.LoadingStateChanged += (isLoading, canGoBack, canGoForward) =>
        {
            IsLoading = isLoading;
            CanGoBack = canGoBack;
            CanGoForward = canGoForward;
        };
        view.FaviconUrlsChanged += urls =>
        {
            if (urls.Count == 0) return;
            string best = urls[0];
            foreach (var url in urls)
            {
                var lower = url.ToLower();
                if (lower.EndsWith(".svg")) { best = url; break; }
                if (lower.Contains("apple-touch-icon") && !best.ToLower().EndsWith(".svg")) { best = url; }
                if (lower.EndsWith(".png") && !best.ToLower().EndsWith(".svg") && !best.ToLower().Contains("apple-touch-icon")) { best = url; }
            }
            FaviconUrl = best;
        };
        view.LoadFailed += (_, _) => { DidFail = true; IsLoading = false; };
        view.NavigationFinished += (_, _) => DidFail = false;
        view.RequestsNewTab += url => Skew.Models.BrowserStore.Shared.NewTab(url);
        view.FindMatchUpdated += (count, ordinal) =>
        {
            Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().TryEnqueue(() =>
            {
                FindMatchCount = count;
                FindMatchOrdinal = ordinal;
            });
        };
    }

    [ObservableProperty] private int _findMatchCount;
    [ObservableProperty] private int _findMatchOrdinal;

    // ── Navigation (delegates to the CEF browser view) ──

    public void Load(string url)
    {
        UrlString = url;
        AccentColorBrush = Skew.Helpers.ColorUtils.GetColorFromUrl(url);
        DidFail = false;
        BrowserView.LoadUrl(url);


    }

    private async Task SimulateLoadAsync_Unused()
    {
        await Task.CompletedTask;
        IsLoading = false;
        CanGoBack = true;
        OnPropertyChanged(nameof(DisplayUrl));
    }

    public void GoBack() => _browserView?.GoBack();
    public void GoForward() => _browserView?.GoForward();
    public void Reload() => _browserView?.Reload();
    public void Stop() => _browserView?.StopLoading();
    public void ShowDevTools() => _browserView?.ShowDevTools();

    // Profile-wide, despite going through one tab: the cache and the cookie
    // jar belong to the request context every tab shares.
    public void ClearBrowserCache() => _browserView?.ClearBrowserCache();
    public void ClearBrowserCookies() => _browserView?.ClearBrowserCookies();

    /// <summary>Tear down the CEF browser when the tab closes.</summary>
    public void Dispose()
    {
        _browserView?.CloseBrowser();
        _browserView = null;
        OnPropertyChanged(nameof(HasBrowserView));
    }

    public void SyncZoom()
    {
        if (_browserView != null)
        {
            double rawLevel = _browserView.ZoomLevel;
            ZoomPercent = (int)Math.Round(Math.Pow(1.2, rawLevel) * 100);
        }
    }

    public void ZoomIn()
    {
        if (_browserView != null)
        {
            _browserView.ZoomIn();
            SyncZoom();
        }
    }

    public void ZoomOut()
    {
        if (_browserView != null)
        {
            _browserView.ZoomOut();
            SyncZoom();
        }
    }

    public void ZoomReset()
    {
        if (_browserView != null)
        {
            _browserView.ResetZoom();
            SyncZoom();
        }
    }

    public void Find(string text, bool forward) => _browserView?.FindText(text, forward);
    public void StopFinding(bool clearSelection) => _browserView?.StopFinding(clearSelection);
}
