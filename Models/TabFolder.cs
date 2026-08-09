using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace Mori.Models;

/// <summary>
/// A named, collapsible group of tabs in the sidebar (Arc/SigmaOS-style folder).
/// Direct port of TabFolder.swift.
/// </summary>
public partial class TabFolder : ObservableObject
{
    public Guid Id { get; } = Guid.NewGuid();

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _symbol;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpandedVisibility))]
    [NotifyPropertyChangedFor(nameof(ChevronGlyph))]
    [NotifyPropertyChangedFor(nameof(ShowsActiveDots))]
    private bool _isExpanded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RenameVisibility))]
    [NotifyPropertyChangedFor(nameof(NormalVisibility))]
    private bool _isRenaming;

    public Microsoft.UI.Xaml.Visibility ExpandedVisibility => IsExpanded ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility RenameVisibility => IsRenaming ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility NormalVisibility => !IsRenaming ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public string ChevronGlyph => IsExpanded ? "\uE70D" : "\uE76C"; // ChevronDown : ChevronRight

    /// <summary>
    /// True when the folder is collapsed but holds the active tab. The Mac shows
    /// three dots inside the closed folder in that case (FolderRow in
    /// Sidebar.swift) instead of the folder's glyph, so a collapsed folder still
    /// signals that the current tab lives inside it.
    /// </summary>
    public bool ShowsActiveDots => !IsExpanded && Tabs.Any(t => t.IsSelected);

    public ObservableCollection<BrowserTab> Tabs { get; } = [];

    public TabFolder(string name = "Folder", string symbol = "\uE8B7", bool isExpanded = false)
    {
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
        };
    }

    private void Tab_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BrowserTab.IsSelected))
            OnPropertyChanged(nameof(ShowsActiveDots));
    }
}
