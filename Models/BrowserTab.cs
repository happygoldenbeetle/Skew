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

    // ── Navigation stubs (will be wired to CEF later) ──

    public void Load(string url)
    {
        UrlString = url;
        AccentColorBrush = Mori.Helpers.ColorUtils.GetColorFromUrl(url);
        DidFail = false;
        IsLoading = true;
        // Simulate load completion after a short delay
        _ = SimulateLoadAsync();
    }

    private async Task SimulateLoadAsync()
    {
        await Task.Delay(800);
        IsLoading = false;
        CanGoBack = true;
        OnPropertyChanged(nameof(DisplayUrl));
    }

    public void GoBack() => CanGoBack = false;
    public void GoForward() { }
    public void Reload() { IsLoading = true; _ = SimulateLoadAsync(); }
    public void Stop() => IsLoading = false;
}
