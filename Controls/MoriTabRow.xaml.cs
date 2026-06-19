using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Mori.Models;

namespace Mori.Controls;

public sealed partial class MoriTabRow : UserControl
{
    public static readonly DependencyProperty TabProperty =
        DependencyProperty.Register(
            nameof(Tab),
            typeof(BrowserTab),
            typeof(MoriTabRow),
            new PropertyMetadata(null, OnTabChanged));

    public BrowserTab Tab
    {
        get => (BrowserTab)GetValue(TabProperty);
        set => SetValue(TabProperty, value);
    }

    private bool _isPointerOver;

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

    public string GetInitial(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "?";
        var trimmed = title.Trim();
        if (trimmed.Length == 0) return "?";
        return trimmed.Substring(0, 1).ToUpperInvariant();
    }

    public Visibility GetInternalIconVisibility(BrowserTab tab)
    {
        if (tab == null || tab.IsLoading) return Visibility.Collapsed;
        return tab.IsInternal ? Visibility.Visible : Visibility.Collapsed;
    }

    public Microsoft.UI.Xaml.Media.ImageSource? GetFaviconSource(string? faviconUrl, string? pageUrl)
    {
        if (faviconUrl == null && pageUrl == null) return null;
        
        // Pass to our centralized Favicon resolver which handles SVG brand lookups!
        return Mori.Helpers.FaviconKit.Resolve(faviconUrl, pageUrl);
    }

    public MoriTabRow()
    {
        InitializeComponent();
        Loaded += MoriTabRow_Loaded;
        Unloaded += MoriTabRow_Unloaded;
    }

    private void MoriTabRow_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateVisualState();
    }

    private void MoriTabRow_Unloaded(object sender, RoutedEventArgs e)
    {
        if (Tab != null)
        {
            Tab.PropertyChanged -= Tab_PropertyChanged;
        }
    }

    private static void OnTabChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MoriTabRow row)
        {
            if (e.OldValue is BrowserTab oldTab)
            {
                oldTab.PropertyChanged -= row.Tab_PropertyChanged;
            }

            if (e.NewValue is BrowserTab newTab)
            {
                newTab.PropertyChanged += row.Tab_PropertyChanged;
            }

            row.Bindings.Update();
            row.UpdateVisualState();
        }
    }

    private void Tab_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BrowserTab.IsSelected))
        {
            UpdateVisualState();
        }
        else if (e.PropertyName == nameof(BrowserTab.FaviconUrl) ||
                 e.PropertyName == nameof(BrowserTab.IsInternal) ||
                 e.PropertyName == nameof(BrowserTab.IsLoading))
        {
            Bindings.Update();
        }
    }




    private void RootGrid_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOver = true;
        UpdateVisualState();
    }

    private void RootGrid_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOver = false;
        VisualStateManager.GoToState(this, "Released", true);
        UpdateVisualState();
    }

    private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        VisualStateManager.GoToState(this, "Pressed", true);
    }

    private void RootGrid_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        VisualStateManager.GoToState(this, "Released", true);
    }

    private void UpdateVisualState()
    {
        if (Tab == null) return;

        if (Tab.IsSelected)
        {
            VisualStateManager.GoToState(this, "Selected", true);
        }
        else if (_isPointerOver)
        {
            VisualStateManager.GoToState(this, "PointerOver", true);
        }
        else
        {
            VisualStateManager.GoToState(this, "Normal", true);
        }
    }

    private void RootGrid_Tapped(object sender, TappedRoutedEventArgs e)
    {
        BrowserStore.Shared.SelectTab(Tab.Id);
    }

    private void RootGrid_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (Tab is not null)
        {
            var flyout = new MenuFlyout();
            Mori.Helpers.MenuBuilder.BuildTabMenu(flyout, Tab);
            flyout.ShowAt((FrameworkElement)sender, e.GetPosition((FrameworkElement)sender));
            e.Handled = true;
        }
    }

    private void PinTab_Click(object sender, RoutedEventArgs e)
    {
        if (Tab is not null)
        {
            BrowserStore.Shared.PinTab(Tab.Id);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (Tab is not null)
        {
            var id = Tab.Id;
            App.DispatcherQueue.TryEnqueue(() => BrowserStore.Shared.CloseTab(id));
        }
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        if (Tab is not null)
        {
            var id = Tab.Id;
            App.DispatcherQueue.TryEnqueue(() => BrowserStore.Shared.TogglePin(id));
        }
    }

    private void RootGrid_DragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (Tab == null) return;
        args.Data.SetText(Tab.Id.ToString());
        args.Data.Properties.Add("tabId", Tab.Id);
        args.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
    }

    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
        e.DragUIOverride.IsCaptionVisible = false;
        e.DragUIOverride.IsGlyphVisible = false;
    }

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
        {
            var text = await e.DataView.GetTextAsync();
            if (Guid.TryParse(text, out Guid draggedTabId))
            {
                var store = MainWindow.Instance?.Store;
                if (store == null || Tab == null) return;

                int looseIndex = store.LooseTabs.IndexOf(this.Tab);
                if (looseIndex >= 0)
                {
                    store.MoveTab(draggedTabId, new LooseTarget(looseIndex));
                    return;
                }

                var folder = store.Folders.FirstOrDefault(f => f.Tabs.Contains(this.Tab));
                if (folder != null)
                {
                    store.MoveTab(draggedTabId, new FolderTarget(folder.Id, folder.Tabs.IndexOf(this.Tab)));
                    return;
                }
            }
        }
    }
}
