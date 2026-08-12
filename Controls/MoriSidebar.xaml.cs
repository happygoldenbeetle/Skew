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
                _store.PinnedTabs.CollectionChanged -= PinnedTabs_CollectionChanged;
            }
            _store = value;
            if (_store != null)
            {
                _store.PropertyChanged += Store_PropertyChanged;
                // The empty-grid catch zone appears and disappears with the pins.
                _store.PinnedTabs.CollectionChanged += PinnedTabs_CollectionChanged;
            }
            RefreshUI();
        }
    }

    public bool HasPins => Store?.PinnedTabs.Count > 0;

    /// <summary>
    /// Whether this copy of the sidebar is the one on screen.
    ///
    /// <para>
    /// There are two, bound to the same store: the docked sidebar and the one
    /// inside the peek card. Both build the same folder rows, so a folder being
    /// renamed opens a field in <em>both</em> — and both used to reach for
    /// focus. The second one to take it made the first lose it, and losing focus
    /// ends a rename, which is why the field shut the instant it opened however
    /// the focus timing was arranged. Only the live copy takes focus now.
    /// </para>
    /// </summary>
    public bool IsLive { get; set; } = true;

    /// <summary>
    /// Space above the first row.
    ///
    /// <para>
    /// Zero for the docked sidebar, whose top edge is the title bar's underside
    /// and whose pinned row is meant to start on the same line as the web card
    /// beside it. The peek copy sets it back to the inset the sides use, since
    /// it floats as a card with a top edge of its own and content pinned to that
    /// edge reads as a rendering fault rather than as alignment.
    /// </para>
    /// </summary>
    public static readonly DependencyProperty TopInsetProperty =
        DependencyProperty.Register(nameof(TopInset), typeof(double), typeof(MoriSidebar),
            new PropertyMetadata(0d, OnTopInsetChanged));

    public double TopInset
    {
        get => (double)GetValue(TopInsetProperty);
        set => SetValue(TopInsetProperty, value);
    }

    private static void OnTopInsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MoriSidebar sidebar && sidebar.ListStack is not null)
        {
            var padding = sidebar.ListStack.Padding;
            sidebar.ListStack.Padding = new Thickness(
                padding.Left, sidebar.TopInset, padding.Right, padding.Bottom);
        }
    }

    private bool _tabDragActive;

    private void PinnedTabs_CollectionChanged(object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        UpdatePinnedDropZone();
    }

    /// <summary>
    /// The pinned catch zone exists only while a drag is in flight and there is
    /// nothing pinned yet — matching <c>!pinnedTabs.isEmpty || draggingTabID
    /// != nil</c> in Sidebar.swift. Showing it unconditionally would reserve 40pt
    /// of empty space above New Tab that the Mac never shows.
    /// </summary>
    private void UpdatePinnedDropZone()
    {
        bool empty = (Store?.PinnedTabs.Count ?? 0) == 0;
        PinnedDropZone.Visibility = empty && _tabDragActive
            ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Shared media controller — the media strip binds its visibility here.</summary>
    public MediaController Media => MediaController.Shared;

    /// <summary>x:Bind helper: show the media strip only while a tab is playing.</summary>
    public Visibility MediaVisibility(bool hasMedia)
        => hasMedia ? Visibility.Visible : Visibility.Collapsed;

    // Downloads — the button, its pulse and its flyout — moved to the title
    // bar; MainWindow owns that state now.

    public MoriSidebar()
    {
        InitializeComponent();
        Loaded += MoriSidebar_Loaded;
    }

    private void MoriSidebar_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshUI();

        MoriTabRow.TabDragActiveChanged += OnTabDragActiveChanged;
        Unloaded += (_, _) => MoriTabRow.TabDragActiveChanged -= OnTabDragActiveChanged;
    }

    private void OnTabDragActiveChanged(bool active)
    {
        _tabDragActive = active;
        DispatcherQueue.TryEnqueue(UpdatePinnedDropZone);
    }

    private void Store_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BrowserStore.SelectedTab))
        {
            RefreshUI();
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

        UpdatePinnedDropZone();
    }


    private void RootGrid_RightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        var flyout = new Microsoft.UI.Xaml.Controls.MenuFlyout();
        Mori.Helpers.MenuBuilder.BuildSidebarMenu(flyout);
        flyout.ShowAt((Microsoft.UI.Xaml.FrameworkElement)sender, e.GetPosition((Microsoft.UI.Xaml.FrameworkElement)sender));
        e.Handled = true;
    }

    // -- Event Handlers --
    //
    // SidebarToggle_Click and UpdatePositionIcons went with the toggle button;
    // the latter existed only to mirror its glyph toward the docked edge.
    //
    // Back_Click, Forward_Click, Reload_Click and FocusOmnibox went with the
    // header, along with the SelectedTab subscription that kept the nav buttons
    // enabled and swapped the reload glyph for stop. Store.GoBack/GoForward/
    // Reload/Stop are untouched, so a title-bar chrome can call them directly.

    private void NewTab_Click(object sender, RoutedEventArgs e)
        => Store?.PresentLauncher();

    // ── Bottom-bar add menu ───────────────────────────────────────────────

    /// <summary>The launcher, which is also what Ctrl+T opens.</summary>
    private void NewTabMenu_Click(object sender, RoutedEventArgs e)
        => Store?.PresentLauncher();

    /// <summary>A folder with its name field already live, as the menu does.</summary>
    private void NewFolderMenu_Click(object sender, RoutedEventArgs e)
        => Store?.AddFolderForEditing();

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
            // No RefreshUI: IsExpanded raises PropertyChanged and the folder's
            // own bindings follow it. Reassigning the ItemsSource here rebuilt
            // every folder container instead, which threw away the reveal
            // mid-animation and put the rows back to snapping in and out.
            Store?.ToggleFolder(folderId);
        }
    }

    // The pinned grid sizes itself: PinnedTileGrid computes its columns and cell
    // width during measure, from the width it is handed. Driving that from here
    // meant reacting to SizeChanged, which fires a pass too late — see
    // PinnedTileGrid for the flicker that caused.



    // AIToggle_Click, ThemeToggle_Click and Settings_Click are gone with their
    // buttons. Settings is still reachable from the sidebar context menu, and
    // light/dark from Settings itself.




    private void NewTabInFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is Guid folderId)
        {
            Store?.NewTabInFolder(folderId);
        }
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

    /// <summary>
    /// Fade the folder's close-all control in and out with the pointer.
    ///
    /// <para>
    /// Found by name off the header's content rather than held in a field: the
    /// header lives in a DataTemplate, so there is one of these per folder and
    /// no single element to reference. Same shape as a tab row's close button.
    /// </para>
    /// </summary>
    private void FolderHeader_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        => SetFolderCloseAllOpacity(sender, 1);

    private void FolderHeader_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        => SetFolderCloseAllOpacity(sender, 0);

    private static void SetFolderCloseAllOpacity(object sender, double opacity)
    {
        if (sender is Button header &&
            header.Content is FrameworkElement content &&
            content.FindName("FolderCloseAll") is Button closeAll)
        {
            closeAll.Opacity = opacity;
        }
    }

    // Clear appears with the pointer anywhere in the sidebar, and the New Tab
    // row's shortcut only while that row is under it — one is about the list,
    // the other about the row.
    // Visibility, not opacity: hidden, Clear has to give its column back so the
    // rule runs the full width.
    private void Sidebar_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        => ClearTabsButton.Visibility = Visibility.Visible;

    private void Sidebar_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        => ClearTabsButton.Visibility = Visibility.Collapsed;

    private void NewTabButton_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        => NewTabShortcut.Opacity = 1;

    private void NewTabButton_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        => NewTabShortcut.Opacity = 0;

    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush ClearRest =
        new(Windows.UI.Color.FromArgb(0xFF, 0x65, 0x6D, 0x6E));

    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush ClearHover =
        new(Windows.UI.Color.FromArgb(0xFF, 0xCD, 0xD0, 0xD0));

    private void ClearTabs_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        => SetClearBrush(ClearHover);

    private void ClearTabs_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        => SetClearBrush(ClearRest);

    /// <summary>The arrow is stroked and the label filled, so both are set.</summary>
    private void SetClearBrush(Microsoft.UI.Xaml.Media.Brush brush)
    {
        ClearTabsText.Foreground = brush;
        ClearTabsArrow.Stroke = brush;
    }

    private void ClearTabs_Click(object sender, RoutedEventArgs e)
        => Store?.ClearLooseTabs();

    private void CloseFolderTabs_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is Guid folderId)
            Store?.CloseOpenTabsInFolder(folderId);
    }

    private void DeleteFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is Guid folderId)
        {
            Store?.DeleteFolder(folderId);
        }
    }

    /// <summary>
    /// The rename field that currently holds focus, or null while one is on its
    /// way to being focused. Renaming ends on losing focus, and the field cannot
    /// tell the user clicking away from the menu handing focus back — so a
    /// LostFocus for a field not in here is ignored as the latter.
    /// </summary>
    private TextBox? _renameBox;

    /// <summary>
    /// A field whose focus is already queued.
    ///
    /// <para>
    /// Loaded runs more than once for the same field — the containers are built,
    /// then built again — so the visibility callback registered there ends up
    /// registered twice and the focus is queued twice. The second call made the
    /// first lose focus, and losing focus is what ends a rename: the field
    /// closed a few milliseconds after opening, with the old name intact.
    /// </para>
    /// </summary>
    private TextBox? _renamePending;

    private void RenameFolder_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb) return;

        tb.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) =>
        {
            if (tb.Visibility == Visibility.Visible) BeginRename(tb);
        });

        if (tb.Visibility == Visibility.Visible) BeginRename(tb);
    }

    /// <summary>
    /// Take focus once the menu that started the rename has finished with it.
    ///
    /// <para>
    /// Focusing straight away looked like it worked and did not: a closing
    /// flyout restores focus to whatever it was opened from, which happens after
    /// this runs. The field would light up, lose focus to the folder button a
    /// moment later, and the LostFocus handler would end the rename before a key
    /// could be pressed — the field flickering once and the name never changing.
    /// Queued at low priority, this lands after the flyout is done.
    /// </para>
    /// </summary>
    private void BeginRename(TextBox tb)
    {
        // The off-screen copy shows the same field and must not fight for focus.
        if (!IsLive) return;
        if (ReferenceEquals(_renamePending, tb)) return;

        _renamePending = tb;
        _renameBox = null;

        tb.DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                _renamePending = null;
                if (tb.Visibility != Visibility.Visible) return;

                tb.SelectAll();
                tb.Focus(FocusState.Programmatic);
                _renameBox = tb;
            });
    }

    private void RenameFolder_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb || tb.Tag is not Guid folderId) return;

        // Not the field the user is in yet — this is the menu's focus restore.
        if (!ReferenceEquals(_renameBox, tb)) return;

        // Still the field: focus was re-taken, not given up. Focusing an element
        // that already has focus raises LostFocus on the way, and ending the
        // rename there closed the field the moment it opened.
        if (ReferenceEquals(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot), tb))
            return;

        _renameBox = null;

        var folder = Store?.Folders.FirstOrDefault(f => f.Id == folderId);
        if (folder != null)
            folder.IsRenaming = false;
    }

    private void RenameFolder_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter || e.Key == Windows.System.VirtualKey.Escape)
        {
            if (sender is TextBox tb && tb.Tag is Guid folderId)
            {
                // Cleared first: the focus move below raises LostFocus, which
                // would otherwise end a rename that has already ended.
                _renameBox = null;

                var folder = Store?.Folders.FirstOrDefault(f => f.Id == folderId);
                if (folder != null)
                {
                    folder.IsRenaming = false;
                }
            }
            this.Focus(FocusState.Programmatic);
            e.Handled = true;
        }
    }

    private void FolderHeader_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsGlyphVisible = true;
        
        string folderName = "folder";
        string verb = "Add to";

        if (e.DataView.Properties.TryGetValue("tabId", out var tIdObj) && tIdObj is Guid tabId && Store != null)
        {
            // If it's already in ANY folder, use "Move"
            if (Store.Folders.Any(f => f.Tabs.Any(t => t.Id == tabId)))
            {
                verb = "Move to";
            }
        }

        if (sender is Microsoft.UI.Xaml.Controls.Control fe)
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
        
        e.DragUIOverride.Caption = $"{verb} {folderName}";
    }

    private void FolderHeader_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Microsoft.UI.Xaml.Controls.Control fe)
        {
            // Restore transparent background
            fe.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
    }

    private async void FolderHeader_Drop(object sender, DragEventArgs e)
    {
        if (sender is Microsoft.UI.Xaml.Controls.Control feDrop)
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
