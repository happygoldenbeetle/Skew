namespace Mori.Models;

/// <summary>
/// A named, collapsible group of tabs in the sidebar (Arc/SigmaOS-style folder).
/// Direct port of TabFolder.swift.
/// </summary>
public class TabFolder
{
    public Guid Id { get; set; }
    public string Name { get; set; }
    public string Symbol { get; set; }
    public bool IsExpanded { get; set; }
    public List<Guid> TabIds { get; set; }

    public TabFolder(string name = "Folder", string symbol = "\uE8B7", bool isExpanded = true)
    {
        Id = Guid.NewGuid();
        Name = name;
        Symbol = symbol; // Segoe Fluent Icons folder glyph
        IsExpanded = isExpanded;
        TabIds = [];
    }
}
