using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Mori.Models;
using Mori.Theme;

namespace Mori.Controls;

/// <summary>
/// The vertical sidebar — Arc/SigmaOS-inspired. Top-to-bottom:
/// header (nav + omnibox), pinned tile grid, collapsible folders,
/// loose tabs, and a bottom action bar.
/// Port of Sidebar.swift from the Mac app.
/// </summary>
public sealed partial class MoriSidebar : UserControl
{
    private BrowserStore? _store;
    public BrowserStore? Store
    {
        get => _store;
        set
        {
            if (_store != null)
            {
                _store.PropertyChanged -= Store_PropertyChanged;
                if (_store.SelectedTab != null)
                    _store.SelectedTab.PropertyChanged -= SelectedTab_PropertyChanged;
            }
            _store = value;
            if (_store != null)
            {
                _store.PropertyChanged += Store_PropertyChanged;
                if (_store.SelectedTab != null)
                    _store.SelectedTab.PropertyChanged += SelectedTab_PropertyChanged;
            }
            RefreshUI();
        }
    }

    public bool HasPins => Store?.PinnedTabs.Count > 0;

    public MoriSidebar()
    {
        InitializeComponent();
        Loaded += MoriSidebar_Loaded;
    }

    private void MoriSidebar_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshUI();
    }

    private void Store_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BrowserStore.SidebarOnLeft))
        {
            UpdatePositionIcons();
        }
        else if (e.PropertyName == nameof(BrowserStore.SelectedTab))
        {
            if (sender is BrowserStore oldStore && oldStore.SelectedTab != null)
            {
                // This is slightly tricky: 'sender' is the store.
                // We actually want to detach from the old selected tab.
                // Since this fires AFTER it changed, we can't get the old one easily.
                // But we handle attaching to the new one here.
            }
            
            // To properly manage the SelectedTab PropertyChanged event:
            if (Store != null)
            {
                // Remove from all tabs just to be safe if we can't track the old one easily
                foreach (var tab in Store.Tabs) tab.PropertyChanged -= SelectedTab_PropertyChanged;
                foreach (var tab in Store.PinnedTabs) tab.PropertyChanged -= SelectedTab_PropertyChanged;
                
                if (Store.SelectedTab != null)
                    Store.SelectedTab.PropertyChanged += SelectedTab_PropertyChanged;
            }

            RefreshUI();
        }
    }

    private void SelectedTab_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BrowserTab.DisplayUrl) || e.PropertyName == nameof(BrowserTab.UrlString))
        {
            if (Store?.SelectedTab != null)
            {
                OmniboxField.Text = Store.SelectedTab.DisplayUrl;
            }
        }
        else if (e.PropertyName == nameof(BrowserTab.CanGoBack) || e.PropertyName == nameof(BrowserTab.CanGoForward))
        {
            BackButton.IsEnabled = Store?.SelectedTab?.CanGoBack ?? false;
            ForwardButton.IsEnabled = Store?.SelectedTab?.CanGoForward ?? false;
        }
    }

    /// <summary>
    /// Rebuild the ItemsRepeater data sources from the store.
    /// </summary>
    public void RefreshUI()
    {
        if (Store is null) return;

        PinnedGrid.ItemsSource = Store.PinnedTabs;
        FolderList.ItemsSource = Store.Folders;
        LooseTabList.ItemsSource = Store.LooseTabs;

        // Update omnibox with selected tab URL
        if (Store.SelectedTab is not null)
        {
            OmniboxField.Text = Store.SelectedTab.DisplayUrl;
        }

        // Update nav button states
        BackButton.IsEnabled = Store.SelectedTab?.CanGoBack ?? false;
        ForwardButton.IsEnabled = Store.SelectedTab?.CanGoForward ?? false;
        
        UpdatePositionIcons();
    }

    private void RootGrid_RightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        var flyout = new Microsoft.UI.Xaml.Controls.MenuFlyout();
        Mori.Helpers.MenuBuilder.BuildSidebarMenu(flyout);
        flyout.ShowAt((Microsoft.UI.Xaml.FrameworkElement)sender, e.GetPosition((Microsoft.UI.Xaml.FrameworkElement)sender));
        e.Handled = true;
    }

    private void UpdatePositionIcons()
    {
        if (Store == null) return;
        
        PositionIcon.Glyph = Store.SidebarOnLeft ? "\uE90C" : "\uE90D"; // DockLeft / DockRight
        if (SidebarToggleIcon.RenderTransform is Microsoft.UI.Xaml.Media.ScaleTransform st)
        {
            st.ScaleX = Store.SidebarOnLeft ? -1 : 1;
        }
    }

    public void FocusOmnibox()
    {
        OmniboxField.Focus(FocusState.Programmatic);
    }

    // ── Event Handlers ──

    private void SidebarToggle_Click(object sender, RoutedEventArgs e)
        => Store?.ToggleSidebar();

    private void Back_Click(object sender, RoutedEventArgs e)
        => Store?.GoBack();

    private void Forward_Click(object sender, RoutedEventArgs e)
        => Store?.GoForward();

    private void Reload_Click(object sender, RoutedEventArgs e)
        => Store?.Reload();

    private void Omnibox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var text = args.QueryText?.Trim();
        if (!string.IsNullOrEmpty(text))
        {
            Store?.Navigate(text);
        }
    }

    private void NewTab_Click(object sender, RoutedEventArgs e)
        => Store?.PresentLauncher();

    private void PinnedTile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is Guid tabId)
            Store?.SelectTab(tabId);
    }

    private void TabRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is Guid tabId)
            Store?.SelectTab(tabId);
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        Guid? tabId = null;
        if (sender is Button btn && btn.Tag is Guid id1) tabId = id1;
        else if (sender is MenuFlyoutItem item && item.Tag is Guid id2) tabId = id2;

        if (tabId.HasValue)
        {
            Store?.CloseTab(tabId.Value);
            RefreshUI();
        }
    }

    private void PinTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is Guid tabId)
        {
            Store?.TogglePin(tabId);
            RefreshUI();
        }
    }

    private void FolderHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is Guid folderId)
        {
            Store?.ToggleFolder(folderId);
            RefreshUI();
        }
    }

    // ── Drag & Drop Resizing Math ──
    private void PinnedGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (PinnedGrid.ItemsPanelRoot is ItemsWrapGrid wrapGrid)
        {
            double availableWidth = e.NewSize.Width;
            if (availableWidth <= 0) return;

            // Tile min width is 56, plus 6 for the right margin.
            double minCellWidth = 56 + 6;

            int columns = (int)(availableWidth / minCellWidth);
            if (columns < 1) columns = 1;

            // Divide the space evenly so it perfectly stretches.
            wrapGrid.ItemWidth = availableWidth / columns;
            wrapGrid.ItemHeight = 42 + 6; // tile height + bottom margin
        }
    }

    private void AIToggle_Click(object sender, RoutedEventArgs e)
        => Store?.ToggleAIPanel();

    private void ThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        ThemeService.Instance.ToggleTheme();
        if (XamlRoot?.Content is FrameworkElement root)
        {
            root.RequestedTheme = ThemeService.Instance.IsDark
                ? ElementTheme.Dark
                : ElementTheme.Light;
        }
        var pathData = ThemeService.Instance.IsDark 
            ? "F1 M 12.9928 7.95122 a 0.583333 0.583333 0 0 0 -0.651778 -0.0287778 A 4.27778 4.27778 0 0 1 10.1111 8.55556 a 4.28244 4.28244 0 0 1 -4.27778 -4.27778 c 0 -0.891333 0.275333 -1.74767 0.795667 -2.478 a 0.583333 0.583333 0 0 0 -0.582556 -0.912333 A 6.22222 6.22222 0 0 0 0.972222 7 c 0 3.43078 2.79144 6.22222 6.22222 6.22222 a 6.21833 6.21833 0 0 0 6.01611 -4.65578 0.583333 0.583333 0 0 0 -0.217778 -0.614444" 
            : "F1 M 7 11.6667 a 0.583333 0.583333 0 0 1 0.583333 0.583333 v 0.777778 a 0.583333 0.583333 0 0 1 -1.16667 0 v -0.777778 A 0.583333 0.583333 0 0 1 7 11.6667 m -4.12456 -1.36656 a 0.582556 0.582556 0 1 1 0.824444 0.824444 l -0.549889 0.550667 a 0.579444 0.579444 0 0 1 -0.824444 0 0.583333 0.583333 0 0 1 0 -0.824444 z m 7.42389 0 a 0.583333 0.583333 0 0 1 0.824444 0 l 0.549889 0.549889 a 0.583333 0.583333 0 0 1 -0.824444 0.824444 l -0.549889 -0.549111 a 0.583333 0.583333 0 0 1 0 -0.824444 M 7 3.11111 a 3.88889 3.88889 0 1 1 0 7.77778 A 3.88889 3.88889 0 0 1 7 3.11111 M 1.75 6.41667 a 0.583333 0.583333 0 0 1 0 1.16667 h -0.777778 a 0.583333 0.583333 0 0 1 0 -1.16667 z m 11.2778 0 a 0.583333 0.583333 0 0 1 0 1.16667 h -0.777778 a 0.583333 0.583333 0 0 1 0 -1.16667 z M 2.32556 2.32556 a 0.583333 0.583333 0 0 1 0.824444 0 l 0.549889 0.549111 a 0.583333 0.583333 0 0 1 -0.824444 0.824444 L 2.32556 3.15 a 0.583333 0.583333 0 0 1 0 -0.824444 m 8.52444 0 a 0.583333 0.583333 0 0 1 0.824444 0.824444 l -0.549889 0.549889 a 0.578667 0.578667 0 0 1 -0.824444 0 0.583333 0.583333 0 0 1 0 -0.824444 z M 7 0.388889 a 0.583333 0.583333 0 0 1 0.583333 0.583333 v 0.777778 a 0.583333 0.583333 0 0 1 -1.16667 0 v -0.777778 A 0.583333 0.583333 0 0 1 7 0.388889";
        ThemeIcon.Data = (Microsoft.UI.Xaml.Media.Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Microsoft.UI.Xaml.Media.Geometry), pathData);
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
        => Store?.ToggleSettings();

    private void PositionToggle_Click(object sender, RoutedEventArgs e)
        => Store?.ToggleSidebarPosition();

    private Microsoft.UI.Xaml.Window _themeWindow;

    private void GlassPickerToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_themeWindow != null)
        {
            _themeWindow.Activate();
            return;
        }

        _themeWindow = new Microsoft.UI.Xaml.Window();
        _themeWindow.Title = "Theme Picker";
        _themeWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(380, 650));
        _themeWindow.SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();

        var presenter = _themeWindow.AppWindow.Presenter as Microsoft.UI.Windowing.OverlappedPresenter;
        if (presenter != null)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
        }

        var picker = new Microsoft.UI.Xaml.Controls.ColorPicker
        {
            IsAlphaEnabled = false, // We'll use our own explicit slider for clarity
            Color = Microsoft.UI.ColorHelper.FromArgb(77, 255, 255, 255), // #4DFFFFFF
            Margin = new Microsoft.UI.Xaml.Thickness(20, 20, 20, 0),
            HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Center
        };

        var opacitySlider = new Microsoft.UI.Xaml.Controls.Slider
        {
            Minimum = 0,
            Maximum = 100,
            Value = 30,
            Header = "Glass Frosting Opacity (%)",
            Margin = new Microsoft.UI.Xaml.Thickness(40, 10, 40, 20)
        };

        picker.ColorChanged += (s, args) =>
        {
            var color = args.NewColor;
            color.A = (byte)(opacitySlider.Value / 100.0 * 255.0);
            MainWindow.Instance?.UpdateGlobalTint(color);
        };

        opacitySlider.ValueChanged += (s, args) =>
        {
            var color = picker.Color;
            color.A = (byte)(args.NewValue / 100.0 * 255.0);
            MainWindow.Instance?.UpdateGlobalTint(color);
        };

        var root = new Microsoft.UI.Xaml.Controls.StackPanel();
        root.Children.Add(picker);
        root.Children.Add(opacitySlider);
        _themeWindow.Content = root;

        _themeWindow.Closed += (s, args) => _themeWindow = null;
        _themeWindow.Activate();
    }

    private void RenameFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is Guid folderId)
        {
            var folder = Store?.Folders.FirstOrDefault(f => f.Id == folderId);
            if (folder != null)
            {
                folder.IsRenaming = true;
            }
        }
    }

    private void DeleteFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is Guid folderId)
        {
            Store?.DeleteFolder(folderId);
        }
    }

    private void RenameFolder_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb)
        {
            tb.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (s, dp) =>
            {
                if (tb.Visibility == Visibility.Visible)
                {
                    tb.SelectAll();
                    tb.Focus(FocusState.Programmatic);
                }
            });
            
            if (tb.Visibility == Visibility.Visible)
            {
                tb.SelectAll();
                tb.Focus(FocusState.Programmatic);
            }
        }
    }

    private void RenameFolder_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && tb.Tag is Guid folderId)
        {
            var folder = Store?.Folders.FirstOrDefault(f => f.Id == folderId);
            if (folder != null)
            {
                folder.IsRenaming = false;
            }
        }
    }

    private void RenameFolder_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter || e.Key == Windows.System.VirtualKey.Escape)
        {
            if (sender is TextBox tb && tb.Tag is Guid folderId)
            {
                var folder = Store?.Folders.FirstOrDefault(f => f.Id == folderId);
                if (folder != null)
                {
                    folder.IsRenaming = false;
                }
            }
            e.Handled = true;
        }
    }

    private void FolderHeader_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsGlyphVisible = true;
        
        string folderName = "folder";
        if (sender is FrameworkElement fe)
        {
            // Highlight the folder header
            fe.Background = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["SubtleFillColorSecondaryBrush"];

            if (fe.Tag is Guid folderId && Store != null)
            {
                var folder = Store.Folders.FirstOrDefault(f => f.Id == folderId);
                if (folder != null)
                {
                    folderName = folder.Name;
                }
            }
        }
        
        e.DragUIOverride.Caption = $"Add to {folderName}";
    }

    private void FolderHeader_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement fe)
        {
            // Restore transparent background
            fe.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
    }

    private async void FolderHeader_Drop(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement feDrop)
        {
            // Restore transparent background
            feDrop.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
            
            if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
            {
                var text = await e.DataView.GetTextAsync();
                if (Guid.TryParse(text, out Guid draggedTabId))
                {
                    if (feDrop.Tag is Guid folderId && Store != null)
                    {
                        var folder = Store.Folders.FirstOrDefault(f => f.Id == folderId);
                        if (folder != null)
                        {
                            Store.MoveTab(draggedTabId, new FolderTarget(folderId, int.MaxValue));
                        }
                    }
                }
            }
        }
    }
}
