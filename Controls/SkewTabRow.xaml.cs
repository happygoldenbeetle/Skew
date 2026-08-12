using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Skew.Models;

namespace Skew.Controls;

public sealed partial class SkewTabRow : UserControl
{
    public static readonly DependencyProperty TabProperty =
        DependencyProperty.Register(
            nameof(Tab),
            typeof(BrowserTab),
            typeof(SkewTabRow),
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
        return Skew.Helpers.FaviconKit.Resolve(faviconUrl, pageUrl);
    }

    public SkewTabRow()
    {
        InitializeComponent();
        Loaded += SkewTabRow_Loaded;
        Unloaded += SkewTabRow_Unloaded;
        Theme.ThemeService.Instance.PropertyChanged += ThemeService_PropertyChanged;
    }

    private void SkewTabRow_Loaded(object sender, RoutedEventArgs e)
    {
        ApplySurfaceFills();
        UpdateVisualState();
    }

    private void SkewTabRow_Unloaded(object sender, RoutedEventArgs e)
    {
        if (Tab != null)
        {
            Tab.PropertyChanged -= Tab_PropertyChanged;
        }
        Theme.ThemeService.Instance.PropertyChanged -= ThemeService_PropertyChanged;
    }

    private void ThemeService_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Theme.ThemeService.Palette))
            ApplySurfaceFills();
    }

    /// <summary>
    /// Paint the hover/selected layers from TabSurface. These are plain
    /// white/black alphas over the translucent sidebar (TabRow.swift), which is
    /// why they are set here rather than bound to Fluent theme brushes.
    /// </summary>
    private void ApplySurfaceFills()
    {
        // Chosen colours rather than TabSurface's translucent washes, which were
        // alphas over the sidebar and so shifted with whatever sat behind them.
        HoverBackground.Background = Solid(0x2B, 0x35, 0x38);
        SelectedBackground.Background = Solid(0x55, 0x5E, 0x60);
    }

    private static Microsoft.UI.Xaml.Media.SolidColorBrush Solid(byte r, byte g, byte b)
        => new(Windows.UI.Color.FromArgb(0xFF, r, g, b));

    private static void OnTabChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SkewTabRow row)
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
            Skew.Helpers.MenuBuilder.BuildTabMenu(flyout, Tab);
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

    /// <summary>
    /// True when this row's tab is a member of a folder, where closing and
    /// discarding are two different things.
    /// </summary>
    private bool IsInFolder =>
        Tab is not null && BrowserStore.Shared.Folders.Any(f => f.Tabs.Contains(Tab));

    /// <summary>
    /// A folder row with a loaded browser closes it and keeps the saved entry;
    /// anything else — a loose tab, or a folder entry with nothing open — is
    /// discarded outright.
    /// </summary>
    private bool ClosesRatherThanDiscards => IsInFolder && Tab?.HasBrowserView == true;

    // Bound from the XAML. The parameter is the trigger, not the whole answer:
    // it is what makes these re-evaluate when a browser is built or torn down.
    public Visibility CloseGlyphVisibility(bool hasBrowserView)
        => ClosesRatherThanDiscards ? Visibility.Visible : Visibility.Collapsed;

    public Visibility DiscardGlyphVisibility(bool hasBrowserView)
        => ClosesRatherThanDiscards ? Visibility.Collapsed : Visibility.Visible;

    public string CloseTooltip(bool hasBrowserView)
        => ClosesRatherThanDiscards ? "Close tab"
         : IsInFolder ? "Remove from folder"
         : "Close tab";

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (Tab is null) return;

        var id = Tab.Id;
        // Read the state now: the click is dispatched, and by the time it runs
        // the row may already have been rebuilt underneath us.
        bool close = ClosesRatherThanDiscards;
        bool inFolder = IsInFolder;

        App.DispatcherQueue.TryEnqueue(() =>
        {
            if (!close && inFolder)
                BrowserStore.Shared.DeleteTabFromFolder(id);
            else
                BrowserStore.Shared.CloseTab(id);
        });
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        if (Tab is not null)
        {
            var id = Tab.Id;
            App.DispatcherQueue.TryEnqueue(() => BrowserStore.Shared.TogglePin(id));
        }
    }

    /// <summary>
    /// Raised while a sidebar tab drag is in flight. The Mac reveals the pinned
    /// grid's catch zone only for the duration of a drag (<c>draggingTabID !=
    /// nil</c> in Sidebar.swift), so the sidebar listens here rather than
    /// reserving the space permanently.
    /// </summary>
    public static event Action<bool>? TabDragActiveChanged;

    private void RootGrid_DragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (Tab == null) return;
        args.Data.SetText(Tab.Id.ToString());
        args.Data.Properties.Add("tabId", Tab.Id);
        args.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
        TabDragActiveChanged?.Invoke(true);
    }

    private void RootGrid_DropCompleted(UIElement sender, DropCompletedEventArgs args)
        => TabDragActiveChanged?.Invoke(false);

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
