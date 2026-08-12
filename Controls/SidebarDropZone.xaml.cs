using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Skew.Models;
using Windows.ApplicationModel.DataTransfer;

namespace Skew.Controls;

/// <summary>Which list a <see cref="SidebarDropZone"/> appends to.</summary>
public enum SidebarDropZoneKind
{
    /// <summary>Append to the pinned grid.</summary>
    Pinned,
    /// <summary>Append to the loose (unfiled) list.</summary>
    Loose,
    /// <summary>Append to the folder named by <see cref="SidebarDropZone.FolderId"/>.</summary>
    Folder,
}

/// <summary>
/// An always-present drop target that catches tabs dropped into empty space.
/// Port of SidebarDropCatchZone (Sidebar.swift).
///
/// <para>
/// The Mac places three of these: under an empty pinned grid, as an end cap
/// inside every expanded folder (so empty folders can still receive tabs, and so
/// a tab can be appended after the last row), and below the loose list. Only the
/// rows themselves accepted drops here before, which made the empty regions dead.
/// </para>
/// </summary>
public sealed partial class SidebarDropZone : UserControl
{
    public static readonly DependencyProperty KindProperty =
        DependencyProperty.Register(nameof(Kind), typeof(SidebarDropZoneKind), typeof(SidebarDropZone),
            new PropertyMetadata(SidebarDropZoneKind.Loose));

    public static readonly DependencyProperty FolderIdProperty =
        DependencyProperty.Register(nameof(FolderId), typeof(Guid), typeof(SidebarDropZone),
            new PropertyMetadata(Guid.Empty));

    public static readonly DependencyProperty ZoneCornerRadiusProperty =
        DependencyProperty.Register(nameof(ZoneCornerRadius), typeof(double), typeof(SidebarDropZone),
            new PropertyMetadata(2.4, OnCornerRadiusChanged));

    public SidebarDropZoneKind Kind
    {
        get => (SidebarDropZoneKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    /// <summary>Only meaningful when <see cref="Kind"/> is Folder.</summary>
    public Guid FolderId
    {
        get => (Guid)GetValue(FolderIdProperty);
        set => SetValue(FolderIdProperty, value);
    }

    public static readonly DependencyProperty OnlyWhileDraggingProperty =
        DependencyProperty.Register(nameof(OnlyWhileDragging), typeof(bool), typeof(SidebarDropZone),
            new PropertyMetadata(false, OnOnlyWhileDraggingChanged));

    /// <summary>
    /// Take up no room unless a tab is being dragged.
    ///
    /// <para>
    /// A zone that only matters mid-drag but reserves its height at all times
    /// reads as a gap. The end cap under a folder's last row is the case that
    /// showed: 12 of it plus the 8 between folders left an expanded folder
    /// sitting a long way clear of the next one, for a target nothing was
    /// aiming at.
    /// </para>
    ///
    /// <para>
    /// Self-managed rather than driven by the sidebar, because these live inside
    /// a folder's DataTemplate — there is one per folder and no single element
    /// to reach for. The pinned zone above is set by the sidebar instead, since
    /// its condition includes whether anything is pinned.
    /// </para>
    /// </summary>
    public bool OnlyWhileDragging
    {
        get => (bool)GetValue(OnlyWhileDraggingProperty);
        set => SetValue(OnlyWhileDraggingProperty, value);
    }

    private static void OnOnlyWhileDraggingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not SidebarDropZone zone) return;

        if ((bool)e.NewValue)
        {
            zone.Visibility = Visibility.Collapsed;
            SkewTabRow.TabDragActiveChanged += zone.OnTabDragActiveChanged;
            zone.Unloaded += zone.DetachDragWatch;
        }
        else
        {
            SkewTabRow.TabDragActiveChanged -= zone.OnTabDragActiveChanged;
            zone.Unloaded -= zone.DetachDragWatch;
            zone.Visibility = Visibility.Visible;
        }
    }

    private void DetachDragWatch(object sender, RoutedEventArgs e)
        => SkewTabRow.TabDragActiveChanged -= OnTabDragActiveChanged;

    private void OnTabDragActiveChanged(bool active)
        => DispatcherQueue.TryEnqueue(() =>
            Visibility = active ? Visibility.Visible : Visibility.Collapsed);

    /// <summary>
    /// Radius of the targeting wash. The Mac uses TabSurface.radius under the
    /// pinned grid and Radius.sm elsewhere.
    /// </summary>
    public double ZoneCornerRadius
    {
        get => (double)GetValue(ZoneCornerRadiusProperty);
        set => SetValue(ZoneCornerRadiusProperty, value);
    }

    public SidebarDropZone()
    {
        InitializeComponent();
        Loaded += (_, _) => Surface.CornerRadius = new CornerRadius(ZoneCornerRadius);
    }

    private static void OnCornerRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SidebarDropZone zone)
            zone.Surface.CornerRadius = new CornerRadius((double)e.NewValue);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Move;
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.Caption = Kind switch
        {
            SidebarDropZoneKind.Pinned => "Pin tab",
            SidebarDropZoneKind.Folder => "Add to folder",
            _ => "Move here",
        };

        // sidebarForeground @ 0.08, matching SidebarDropCatchZone's targeted fill.
        Surface.Background = Theme.ThemeService.Instance.Palette
            .SidebarForeground.WithOpacity(0.08).ToBrush();
    }

    private void OnDragLeave(object sender, DragEventArgs e) => ClearHighlight();

    private async void OnDrop(object sender, DragEventArgs e)
    {
        ClearHighlight();

        var store = BrowserStore.Shared;
        if (!e.DataView.Contains(StandardDataFormats.Text))
            return;

        var text = await e.DataView.GetTextAsync();
        if (!Guid.TryParse(text, out Guid tabId))
            return;

        DropTarget target = Kind switch
        {
            SidebarDropZoneKind.Pinned => new PinnedTarget(store.PinnedTabs.Count),
            SidebarDropZoneKind.Folder => new FolderTarget(FolderId, int.MaxValue),
            _ => new LooseTarget(store.LooseTabs.Count),
        };

        store.MoveTab(tabId, target);
    }

    private void ClearHighlight()
        => Surface.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
}
