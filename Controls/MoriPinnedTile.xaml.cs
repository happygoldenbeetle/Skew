using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Mori.Models;

namespace Mori.Controls;

public sealed partial class MoriPinnedTile : UserControl
{
    public static readonly DependencyProperty TabProperty = DependencyProperty.Register(
        nameof(Tab),
        typeof(BrowserTab),
        typeof(MoriPinnedTile),
        new PropertyMetadata(null, OnTabChanged));

    public BrowserTab Tab
    {
        get => (BrowserTab)GetValue(TabProperty);
        set => SetValue(TabProperty, value);
    }

    private bool _isHovering;

    public Visibility GetVisibility(bool b) => b ? Visibility.Visible : Visibility.Collapsed;
    public Visibility GetInverseVisibility(bool b) => b ? Visibility.Collapsed : Visibility.Visible;

    public Visibility GetFaviconVisibility(BrowserTab tab)
    {
        if (tab == null || tab.IsLoading || tab.IsInternal) return Visibility.Collapsed;
        return !string.IsNullOrEmpty(tab.FaviconUrl) ? Visibility.Visible : Visibility.Collapsed;
    }

    public Visibility GetFallbackVisibility(BrowserTab tab)
    {
        if (tab == null || tab.IsLoading || tab.IsInternal) return Visibility.Collapsed;
        return string.IsNullOrEmpty(tab.FaviconUrl) ? Visibility.Visible : Visibility.Collapsed;
    }

    public Visibility GetInternalIconVisibility(BrowserTab tab)
    {
        if (tab == null || tab.IsLoading) return Visibility.Collapsed;
        return tab.IsInternal ? Visibility.Visible : Visibility.Collapsed;
    }

    public Microsoft.UI.Xaml.Media.ImageSource? GetFaviconSource(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        try { return new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(url)); }
        catch { return null; }
    }

    public MoriPinnedTile()
    {
        InitializeComponent();
    }

    private static void OnTabChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MoriPinnedTile tile)
        {
            if (e.OldValue is BrowserTab oldTab)
            {
                oldTab.PropertyChanged -= tile.Tab_PropertyChanged;
            }
            if (e.NewValue is BrowserTab newTab)
            {
                newTab.PropertyChanged += tile.Tab_PropertyChanged;
            }
            tile.UpdateVisualState();
        }
    }

    private void Tab_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BrowserTab.IsSelected))
        {
            UpdateVisualState();
        }
    }

    private void RootGrid_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isHovering = true;
        UpdateVisualState();
    }

    private void RootGrid_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isHovering = false;
        UpdateVisualState();
    }

    private void RootGrid_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (Tab is not null)
        {
            BrowserStore.Shared.SelectTab(Tab.Id);
        }
    }

    private void RootGrid_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        // ContextMenu is shown via ContextFlyout on the RootGrid.
    }

    private void UpdateVisualState()
    {
        if (Tab?.IsSelected == true)
        {
            VisualStateManager.GoToState(this, "Selected", true);
        }
        else if (_isHovering)
        {
            VisualStateManager.GoToState(this, "PointerOver", true);
        }
        else
        {
            VisualStateManager.GoToState(this, "Normal", true);
        }
    }

    // ── Bindings Helpers ──

    // ── Context Menu Actions ──

    private void UnpinTab_Click(object sender, RoutedEventArgs e)
    {
        if (Tab is not null)
        {
            var id = Tab.Id;
            App.DispatcherQueue.TryEnqueue(() => BrowserStore.Shared.UnpinTab(id));
        }
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (Tab is not null)
        {
            var id = Tab.Id;
            App.DispatcherQueue.TryEnqueue(() => BrowserStore.Shared.CloseTab(id));
        }
    }
}
