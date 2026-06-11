using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Mori.Models;

namespace Mori.Controls;

/// <summary>
/// The command palette / launcher — Spotlight-style search + tab switcher.
/// Port of LauncherOverlay.swift.
/// </summary>
public sealed partial class MoriLauncher : UserControl
{
    public MoriLauncher()
    {
        InitializeComponent();
    }

    public void FocusSearchBox()
    {
        SearchBox.Text = "";
        SearchBox.Focus(FocusState.Programmatic);
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

        // Show open tabs as results
        ResultsList.ItemsSource = store.Tabs;
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
            var text = SearchBox.Text?.Trim();
            if (!string.IsNullOrEmpty(text))
            {
                GetStore()?.Navigate(text);
                GetStore()?.DismissLauncher();
            }
            e.Handled = true;
        }
    }

    private void Result_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is BrowserTab tab)
        {
            GetStore()?.SelectTab(tab.Id);
            GetStore()?.DismissLauncher();
        }
    }
}
