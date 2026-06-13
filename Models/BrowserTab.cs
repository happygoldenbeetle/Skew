using CommunityToolkit.Mvvm.ComponentModel;

namespace Mori.Models;

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
            if (UrlString.StartsWith("mori://")) return "";
            return UrlString;
        }
    }

    public BrowserTab(string url = "about:blank", string title = "New Tab")
    {
        Id = Guid.NewGuid();
        _title = title;
        _urlString = url;
        _accentColorBrush = Mori.Helpers.ColorUtils.GetColorFromUrl(url);
    }

    // ── CEF-backed browser view ──

    private Mori.Controls.MoriBrowserView? _browserView;

    /// <summary>
    /// The per-tab CEF browser view, created on first access. The host inserts
    /// this into the web-content card; the underlying browser is created lazily
    /// once the view is loaded and sized (mac MoriBrowserView lifecycle).
    /// </summary>
    public Mori.Controls.MoriBrowserView BrowserView
    {
        get
        {
            if (_browserView is null)
            {
                _browserView = new Mori.Controls.MoriBrowserView(UrlString);
                WireBrowserEvents(_browserView);
            }
            return _browserView;
        }
    }

    /// <summary>True once the browser view has been materialized for this tab.</summary>
    public bool HasBrowserView => _browserView is not null;

    private void WireBrowserEvents(Mori.Controls.MoriBrowserView view)
    {
        view.TitleChanged += t => Title = string.IsNullOrEmpty(t) ? "Untitled" : t;
        view.UrlChanged += u =>
        {
            UrlString = u;
            AccentColorBrush = Mori.Helpers.ColorUtils.GetColorFromUrl(u);
            OnPropertyChanged(nameof(DisplayUrl));
        };
        view.LoadingStateChanged += (loading, back, forward) =>
        {
            IsLoading = loading;
            CanGoBack = back;
            CanGoForward = forward;
        };
        view.FaviconUrlsChanged += urls =>
            FaviconUrl = urls.Count > 0 ? urls[0] : FaviconUrl;
        view.LoadFailed += (_, _) => { DidFail = true; IsLoading = false; };
        view.NavigationFinished += (_, _) => DidFail = false;
    }

    // ── Navigation (delegates to the CEF browser view) ──

    public void Load(string url)
    {
        UrlString = url;
        AccentColorBrush = Mori.Helpers.ColorUtils.GetColorFromUrl(url);
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

    /// <summary>Tear down the CEF browser when the tab closes.</summary>
    public void Dispose()
    {
        _browserView?.CloseBrowser();
        _browserView = null;
    }
}
