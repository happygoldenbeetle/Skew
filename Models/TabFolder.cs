using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace Mori.Models;

/// <summary>
/// A named, collapsible group of tabs in the sidebar (Arc/SigmaOS-style folder).
/// Direct port of TabFolder.swift.
/// </summary>
public partial class TabFolder : ObservableObject
{
    public Guid Id { get; }

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _symbol;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpandedVisibility))]
    [NotifyPropertyChangedFor(nameof(ChevronGlyph))]
    [NotifyPropertyChangedFor(nameof(ShowsActiveDots))]
    [NotifyPropertyChangedFor(nameof(CollapsedOpenTabsVisibility))]
    private bool _isExpanded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RenameVisibility))]
    [NotifyPropertyChangedFor(nameof(NormalVisibility))]
    private bool _isRenaming;

    public Microsoft.UI.Xaml.Visibility ExpandedVisibility => IsExpanded ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>
    /// Everything an expanded folder shows below its header: the rows, the gap
    /// above them, and the end cap that sits under the last one.
    ///
    /// <para>
    /// Collapsed rather than merely empty, so an expanded folder with nothing in
    /// it takes up no height at all — the folder plays its animation and the
    /// list below does not move, which is what Arc does. Merely empty is not
    /// enough: a visible child of zero height still collects the list's spacing,
    /// and the end cap's 12pt was the whole height of an empty folder.
    /// </para>
    ///
    /// <para>
    /// An empty folder still takes a drop — its header is a target in its own
    /// right — and the end cap's other job, appending after the last row, only
    /// means anything once there is one.
    /// </para>
    /// </summary>
    public Microsoft.UI.Xaml.Visibility ContentVisibility =>
        Tabs.Count > 0 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility RenameVisibility => IsRenaming ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility NormalVisibility => !IsRenaming ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public string ChevronGlyph => IsExpanded ? "\uE70D" : "\uE76C"; // ChevronDown : ChevronRight

    /// <summary>
    /// True when the folder is shut but has tabs open inside it.
    ///
    /// <para>
    /// Openness, not selection. The Mac keyed this on holding the *active* tab
    /// (FolderRow in Sidebar.swift), which meant the dots vanished the moment
    /// the user switched to a tab elsewhere even though the folder still had
    /// pages loaded — the one state the mark exists to report.
    /// </para>
    /// </summary>
    public bool ShowsActiveDots => !IsExpanded && HasOpenTabs;

    public ObservableCollection<BrowserTab> Tabs { get; } = [];

    /// <summary>
    /// The folder's tabs that are actually loaded, in folder order.
    ///
    /// <para>
    /// A folder is a saved group: closing a tab inside one tears down its
    /// browser but leaves the tab in the folder, ready to revive on the next
    /// select (see BrowserStore.CloseTab). So membership and openness are
    /// different things, and this is the second one — the tabs that stay on
    /// screen under a collapsed folder, the way Arc keeps a folder's open tabs
    /// in reach without expanding it.
    /// </para>
    /// </summary>
    public ObservableCollection<BrowserTab> OpenTabs { get; } = [];

    /// <summary>Anything to close — gates the folder's close-all control.</summary>
    public bool HasOpenTabs => OpenTabs.Count > 0;

    public Microsoft.UI.Xaml.Visibility CloseAllVisibility =>
        HasOpenTabs ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>
    /// Open tabs show under the header only while the folder is shut; expanded,
    /// the full list already includes them and showing both would double them up.
    /// </summary>
    public Microsoft.UI.Xaml.Visibility CollapsedOpenTabsVisibility =>
        !IsExpanded && HasOpenTabs
            ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>
    /// Rebuild <see cref="OpenTabs"/> from the folder's membership.
    ///
    /// <para>
    /// Rebuilt rather than patched: a tab's openness flips on load and on close,
    /// membership changes by drag, and the orders have to agree. These lists are
    /// a handful of rows.
    /// </para>
    /// </summary>
    private void SyncOpenTabs()
    {
        var open = Tabs.Where(t => t.HasBrowserView).ToList();
        if (OpenTabs.SequenceEqual(open)) return;

        OpenTabs.Clear();
        foreach (var tab in open) OpenTabs.Add(tab);

        OnPropertyChanged(nameof(HasOpenTabs));
        OnPropertyChanged(nameof(CloseAllVisibility));
        OnPropertyChanged(nameof(CollapsedOpenTabsVisibility));
        OnPropertyChanged(nameof(ShowsActiveDots));
    }

    public TabFolder(string name = "Folder", string symbol = "\uE8B7", bool isExpanded = false)
        : this(Guid.NewGuid(), name, symbol, isExpanded)
    {
    }

    /// <summary>
    /// Rebuild a folder with a known id, so a restored folder keeps the identity
    /// the sidebar's drop targets and context menu items key off.
    /// </summary>
    public TabFolder(Guid id, string name, string symbol, bool isExpanded)
    {
        Id = id;
        Name = name;
        Symbol = symbol;
        IsExpanded = isExpanded;

        // The dots depend on the selection state of the contained tabs, so the
        // folder has to re-publish when membership or selection changes.
        Tabs.CollectionChanged += (_, e) =>
        {
            if (e.OldItems is not null)
                foreach (BrowserTab t in e.OldItems) t.PropertyChanged -= Tab_PropertyChanged;
            if (e.NewItems is not null)
                foreach (BrowserTab t in e.NewItems) t.PropertyChanged += Tab_PropertyChanged;
            OnPropertyChanged(nameof(ShowsActiveDots));
            // The folder's contents appear with the first tab and go with the last.
            OnPropertyChanged(nameof(ContentVisibility));
            SyncOpenTabs();
        };
    }

    private void Tab_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Openness changes on both sides: a browser is built on first select,
        // and torn down on close while the tab stays in the folder. Selection is
        // no longer watched here — nothing the folder draws depends on it.
        if (e.PropertyName == nameof(BrowserTab.HasBrowserView))
            SyncOpenTabs();
    }
}
