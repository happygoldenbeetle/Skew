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
    private bool _isExpanded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RenameVisibility))]
    [NotifyPropertyChangedFor(nameof(NormalVisibility))]
    private bool _isRenaming;

    public Microsoft.UI.Xaml.Visibility ExpandedVisibility => IsExpanded ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility RenameVisibility => IsRenaming ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility NormalVisibility => !IsRenaming ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public string ChevronGlyph => IsExpanded ? "\uE70D" : "\uE76C"; // ChevronDown : ChevronRight

    public ObservableCollection<BrowserTab> Tabs { get; } = [];

    public TabFolder(string name = "Folder", string symbol = "\uE8B7", bool isExpanded = true)
    {
        Name = name;
        Symbol = symbol;
        IsExpanded = isExpanded;
    }
}
