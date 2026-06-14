using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Mori.Models;
using System.Linq;

namespace Mori.Controls;

public class LauncherItem
{
    public BrowserTab? Tab { get; set; }
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public bool IsSearchFallback => Tab == null;
    public bool IsNewTab => Title == "New Tab";

    public Visibility SearchIconVisibility => (IsSearchFallback || IsNewTab) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TabIconVisibility => (IsSearchFallback || IsNewTab) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility SubtitleVisibility => string.IsNullOrEmpty(Subtitle) ? Visibility.Collapsed : Visibility.Visible;
    public Microsoft.UI.Xaml.Media.ImageSource? TabFaviconSource
    {
        get
        {
            return Mori.Helpers.FaviconKit.Resolve(Tab?.FaviconUrl, Tab?.UrlString);
        }
    }
}

/// <summary>
/// The command palette / launcher — Spotlight-style search + tab switcher.
/// Port of LauncherOverlay.swift.
/// </summary>
public sealed partial class MoriLauncher : UserControl
{
    private readonly System.Collections.ObjectModel.ObservableCollection<LauncherItem> _launcherItems = new();

    public MoriLauncher()
    {
        InitializeComponent();
        ResultsList.ItemsSource = _launcherItems;
    }

    public void FocusSearchBox()
    {
        SearchBox.Text = "";
        SearchBox.Focus(FocusState.Programmatic);
        RefreshResults();
        RevealAnimation.Begin();
    }

    private System.Action? _onHideCompleted;

    public void PlayHideAnimation(System.Action onCompleted)
    {
        _onHideCompleted = onCompleted;
        HideAnimation.Begin();
    }

    private void HideAnimation_Completed(object sender, object e)
    {
        _onHideCompleted?.Invoke();
        _onHideCompleted = null;
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Up)
        {
            if (ResultsList.SelectedIndex > 0)
                ResultsList.SelectedIndex--;
            else if (ResultsList.SelectedIndex == -1 && ResultsList.Items.Count > 0)
                ResultsList.SelectedIndex = ResultsList.Items.Count - 1;
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Down)
        {
            if (ResultsList.SelectedIndex < ResultsList.Items.Count - 1)
                ResultsList.SelectedIndex++;
            e.Handled = true;
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshResults();
    }

    private BrowserStore? GetStore()
    {
        return (App.Window as MainWindow)?.Store;
    }

    private void RefreshResults()
    {
        var store = GetStore();
        if (store is null) return;

        var text = SearchBox.Text?.Trim().ToLowerInvariant();
        
        _launcherItems.Clear();

        if (string.IsNullOrEmpty(text))
        {
            foreach (var t in store.Tabs.Where(t => t.HasBrowserView).Take(7))
                _launcherItems.Add(new LauncherItem { Tab = t, Title = t.Title ?? "", Subtitle = t.DisplayUrl });
        }
        else
        {
            foreach (var t in store.Tabs.Where(t => (t.Title?.ToLowerInvariant().Contains(text) ?? false) || (t.UrlString?.ToLowerInvariant().Contains(text) ?? false)).Take(7))
                _launcherItems.Add(new LauncherItem { Tab = t, Title = t.Title ?? "", Subtitle = t.DisplayUrl });
        }

        if (_launcherItems.Count > 0)
        {
            ResultsList.SelectedIndex = 0;
        }
    }

    private void Scrim_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        GetStore()?.DismissLauncher();
    }

    private void Card_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // Prevent click-through to scrim
        e.Handled = true;
    }

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            GetStore()?.DismissLauncher();
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Enter)
        {
            if (ResultsList.SelectedItem is LauncherItem item)
            {
                ExecuteLauncherItem(item);
            }
            else
            {
                var text = SearchBox.Text?.Trim();
                if (!string.IsNullOrEmpty(text))
                {
                    GetStore()?.NewTab(text);
                    GetStore()?.DismissLauncher();
                }
            }
            e.Handled = true;
        }
    }

    private void ExecuteLauncherItem(LauncherItem item)
    {
        if (item.IsSearchFallback)
        {
            var text = SearchBox.Text?.Trim();
            if (!string.IsNullOrEmpty(text))
            {
                GetStore()?.NewTab($"https://google.com/search?q={System.Uri.EscapeDataString(text)}");
            }
        }
        else if (item.Tab != null)
        {
            GetStore()?.SelectTab(item.Tab.Id);
        }
        GetStore()?.DismissLauncher();
    }

    private void Result_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is LauncherItem item)
        {
            ExecuteLauncherItem(item);
        }
    }
}
