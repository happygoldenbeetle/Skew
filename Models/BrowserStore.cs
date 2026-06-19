using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Mori.Models;

/// <summary>
/// The single source of truth for all browser UI state.
/// Port of BrowserStore.swift — UI-driven subset for the shell phase.
/// </summary>
public partial class BrowserStore : ObservableObject
{
    public static BrowserStore Shared { get; } = new();

    // ── Tab collections ──

    public ObservableCollection<BrowserTab> Tabs { get; } = [];
    public ObservableCollection<BrowserTab> PinnedTabs { get; } = [];
    public ObservableCollection<TabFolder> Folders { get; } = [];

    /// <summary>
    /// Loose tabs: not pinned and not in any folder.
    /// </summary>
    public ObservableCollection<BrowserTab> LooseTabs { get; } = [];

    [ObservableProperty]
    private BrowserTab? _selectedTab;

    [ObservableProperty]
    private Guid? _selectedTabId;

    // ── UI state ──

    [ObservableProperty]
    private bool _sidebarVisible = true;

    [ObservableProperty]
    private bool _aiPanelVisible;

    [ObservableProperty]
    private bool _launcherVisible;

    partial void OnLauncherVisibleChanged(bool value)
    {
        if (value)
        {
            SelectedTab?.SyncZoom();
        }
    }

    [ObservableProperty]
    private bool _settingsVisible;

    [ObservableProperty]
    private bool _downloadsVisible;

    [ObservableProperty]
    private bool _findBarVisible;

    [ObservableProperty]
    private bool _sidebarOnLeft;



    // ── Initialization ──

    public BrowserStore()
    {
        // Start with a home tab
        var homeTab = new BrowserTab("mori://newtab/", "New Tab");
        Tabs.Add(homeTab);
        LooseTabs.Add(homeTab);
        SelectTab(homeTab.Id);

        // Add sample data for UI development
        AddSampleData();
    }

    private void AddSampleData()
    {
        // Pinned tabs
        var pin1 = new BrowserTab("https://github.com", "GitHub");
        pin1.FaviconUrl = "https://github.githubassets.com/favicons/favicon.svg";
        var pin2 = new BrowserTab("https://youtube.com", "YouTube");
        pin2.FaviconUrl = "https://www.youtube.com/s/desktop/favicon.ico";
        var pin3 = new BrowserTab("https://discord.com", "Discord");
        pin3.FaviconUrl = "https://discord.com/assets/favicon.ico";

        Tabs.Add(pin1);
        Tabs.Add(pin2);
        Tabs.Add(pin3);
        PinnedTabs.Add(pin1);
        PinnedTabs.Add(pin2);
        PinnedTabs.Add(pin3);
    }

    // ── Tab actions ──

    [RelayCommand]
    public void SelectTab(Guid tabId)
    {
        System.IO.File.AppendAllText("crash.log", $"SelectTab called for {tabId}\n");
        var tab = Tabs.FirstOrDefault(t => t.Id == tabId) ?? PinnedTabs.FirstOrDefault(t => t.Id == tabId);
        
        if (tab is null)
        {
            var folder = Folders.FirstOrDefault(f => f.Tabs.Any(t => t.Id == tabId));
            tab = folder?.Tabs.FirstOrDefault(t => t.Id == tabId);
        }

        if (tab is null) {
            System.IO.File.AppendAllText("crash.log", $"Tab {tabId} not found\n");
            return;
        }

        System.IO.File.AppendAllText("crash.log", $"Tab found: {tab.Title}. Checking Tabs.Contains\n");
        if (!Tabs.Contains(tab))
            Tabs.Add(tab);

        System.IO.File.AppendAllText("crash.log", $"Setting SelectedTab to {tab.Title}\n");
        SelectedTab = tab;
        SelectedTabId = tab.Id;

        foreach (var t in Tabs)
        {
            t.IsSelected = (t.Id == tabId);
        }
        foreach (var p in PinnedTabs)
        {
            p.IsSelected = (p.Id == tabId);
        }
    }

    public BrowserTab NewTab(string url = "mori://newtab/")
    {
        string formatted = FormatUrl(url);
        var tab = new BrowserTab(formatted);
        Tabs.Add(tab);
        LooseTabs.Add(tab);
        SelectTab(tab.Id);
        return tab;
    }

    [RelayCommand]
    public void CloseTab(Guid tabId)
    {
        var tab = Tabs.FirstOrDefault(t => t.Id == tabId) ?? PinnedTabs.FirstOrDefault(t => t.Id == tabId);
        if (tab is null)
        {
            var folder = Folders.FirstOrDefault(f => f.Tabs.Any(t => t.Id == tabId));
            tab = folder?.Tabs.FirstOrDefault(t => t.Id == tabId);
        }
        if (tab is null) return;

        Tabs.Remove(tab);
        // Do NOT remove from PinnedTabs here; pinned tabs stay across closes.
        LooseTabs.Remove(tab);

        // Intentionally do not remove from Folders!
        // This allows folders to act as persistent saved groups.
        // The tab.Dispose() call below unloads the CEF browser from memory,
        // and it will seamlessly revive itself the next time SelectTab accesses its BrowserView.

        tab.Dispose(); // tear down the per-tab CEF browser

        // Select adjacent tab if the closed one was selected
        if (SelectedTabId == tabId)
        {
            SelectTab(Tabs.LastOrDefault()?.Id ?? Guid.Empty);
        }
    }

    [RelayCommand]
    public void ToggleSidebarPosition()
    {
        SidebarOnLeft = !SidebarOnLeft;
    }

    [RelayCommand]
    public void Navigate(string input)
    {
        if (SelectedTab is null) return;
        SelectedTab.Load(FormatUrl(input));
    }

    public string FormatUrl(string input)
    {
        if (input == "mori://newtab/" || input.StartsWith("mori-extension://")) return input;
        
        string url = input;
        if (!input.Contains("://") && !input.StartsWith("about:"))
        {
            if (input.Contains('.') && !input.Contains(' '))
                url = $"https://{input}";
            else
                url = $"https://www.google.com/search?q={Uri.EscapeDataString(input)}";
        }
        return url;
    }

    [RelayCommand]
    public void PinTab(Guid tabId)
    {
        var tab = Tabs.FirstOrDefault(t => t.Id == tabId);
        if (tab is null) return;
        if (!PinnedTabs.Contains(tab))
        {
            PinnedTabs.Add(tab);
            LooseTabs.Remove(tab);
        }
    }

    [RelayCommand]
    public void UnpinTab(Guid tabId)
    {
        var tab = PinnedTabs.FirstOrDefault(t => t.Id == tabId);
        if (tab is null) return;
        
        PinnedTabs.Remove(tab);
        if (Tabs.Contains(tab))
            LooseTabs.Add(tab); // Add to loose tabs if it's currently open
    }

    // ── Navigation ──

    [RelayCommand]
    public void GoBack() => SelectedTab?.GoBack();

    [RelayCommand]
    public void GoForward() => SelectedTab?.GoForward();

    [RelayCommand]
    public void Reload() => SelectedTab?.Reload();

    [RelayCommand]
    public void Stop() => SelectedTab?.Stop();

    // ── Sidebar ──

    [RelayCommand]
    public void ToggleSidebar() => SidebarVisible = !SidebarVisible;

    [RelayCommand]
    public void ToggleAIPanel() => AiPanelVisible = !AiPanelVisible;

    [RelayCommand]
    public void ToggleLauncher() => LauncherVisible = !LauncherVisible;

    [RelayCommand]
    public void PresentLauncher() => LauncherVisible = true;

    [RelayCommand]
    public void DismissLauncher() => LauncherVisible = false;

    [RelayCommand]
    public void ToggleSettings() => SettingsVisible = !SettingsVisible;

    [RelayCommand]
    public void ToggleDownloads() => DownloadsVisible = !DownloadsVisible;

    [RelayCommand]
    public void ToggleFindBar() => FindBarVisible = !FindBarVisible;

    // ── Pin/Folder operations ──

    [RelayCommand]
    public void TogglePin(Guid tabId)
    {
        var tab = Tabs.FirstOrDefault(t => t.Id == tabId) ?? PinnedTabs.FirstOrDefault(t => t.Id == tabId);
        if (tab is null) return;

        if (PinnedTabs.Contains(tab))
        {
            PinnedTabs.Remove(tab);
            if (Tabs.Contains(tab))
                LooseTabs.Add(tab);
        }
        else
        {
            LooseTabs.Remove(tab);
            foreach (var folder in Folders)
                folder.Tabs.Remove(tab);
            PinnedTabs.Add(tab);
        }
    }

    public bool IsPinned(Guid tabId) => PinnedTabs.Any(t => t.Id == tabId);

    [RelayCommand]
    public void ToggleFolder(Guid folderId)
    {
        var folder = Folders.FirstOrDefault(f => f.Id == folderId);
        if (folder is not null)
            folder.IsExpanded = !folder.IsExpanded;
    }

    public List<BrowserTab> TabsInFolder(Guid folderId)
    {
        var folder = Folders.FirstOrDefault(f => f.Id == folderId);
        if (folder is null) return [];
        return folder.Tabs.ToList();
    }

    public TabFolder AddFolder(string name = "New Folder")
    {
        var folder = new TabFolder(name);
        Folders.Add(folder);
        return folder;
    }

    public TabFolder AddFolderForEditing()
    {
        var folder = new TabFolder("New Folder");
        Folders.Add(folder);
        folder.IsRenaming = true;
        return folder;
    }

    public void AddTabToFolder(Guid tabId, Guid folderId)
    {
        var tab = Tabs.FirstOrDefault(t => t.Id == tabId);
        if (tab is null) return;
        var folder = Folders.FirstOrDefault(f => f.Id == folderId);
        if (folder is null) return;

        RemoveTabFromFolders(tabId);
        folder.Tabs.Add(tab);
        if (PinnedTabs.Contains(tab)) PinnedTabs.Remove(tab);
        if (LooseTabs.Contains(tab)) LooseTabs.Remove(tab);
        folder.IsExpanded = true;
    }

    [RelayCommand]
    public void RemoveTabFromFolders(Guid tabId)
    {
        var tab = Tabs.FirstOrDefault(t => t.Id == tabId);
        if (tab is null) return;

        foreach (var folder in Folders)
        {
            folder.Tabs.Remove(tab);
        }
        
        if (!PinnedTabs.Contains(tab) && !LooseTabs.Contains(tab))
        {
            LooseTabs.Add(tab);
        }
    }

    [RelayCommand]
    public void DuplicateTab(Guid tabId)
    {
        var tab = Tabs.FirstOrDefault(t => t.Id == tabId) ?? PinnedTabs.FirstOrDefault(t => t.Id == tabId);
        if (tab is null) return;
        
        NewTab(tab.UrlString);
    }

    [RelayCommand]
    public void CopyUrl(Guid tabId)
    {
        var tab = Tabs.FirstOrDefault(t => t.Id == tabId) ?? PinnedTabs.FirstOrDefault(t => t.Id == tabId);
        if (tab is null) return;

        var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
        dataPackage.SetText(tab.UrlString);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
    }

    [RelayCommand]
    public void CloseOtherTabs(Guid tabId)
    {
        var tabsToClose = Tabs.Where(t => t.Id != tabId && !IsPinned(t.Id)).ToList();
        foreach (var tab in tabsToClose)
        {
            CloseTab(tab.Id);
        }
    }

    public bool HasClosableTabsToRight(Guid tabId)
    {
        var index = LooseTabs.Select((t, i) => new { t.Id, i }).FirstOrDefault(x => x.Id == tabId)?.i ?? -1;
        if (index == -1) return false;
        return index < LooseTabs.Count - 1;
    }

    [RelayCommand]
    public void CloseTabsToRight(Guid tabId)
    {
        var index = LooseTabs.Select((t, i) => new { t.Id, i }).FirstOrDefault(x => x.Id == tabId)?.i ?? -1;
        if (index == -1) return;
        
        var tabsToClose = LooseTabs.Skip(index + 1).ToList();
        foreach (var tab in tabsToClose)
        {
            CloseTab(tab.Id);
        }
    }

    [RelayCommand]
    public void DeleteFolder(Guid folderId)
    {
        var folder = Folders.FirstOrDefault(f => f.Id == folderId);
        if (folder is null) return;

        // Move tabs back to loose
        foreach (var tab in folder.Tabs.ToList())
        {
            if (!PinnedTabs.Contains(tab) && !LooseTabs.Contains(tab))
                LooseTabs.Add(tab);
        }
        Folders.Remove(folder);
    }

    public void MoveTab(Guid tabId, DropTarget target)
    {
        var tab = Tabs.FirstOrDefault(t => t.Id == tabId);
        if (tab == null) return;

        int sourcePinnedIndex = PinnedTabs.IndexOf(tab);
        int sourceLooseIndex = LooseTabs.IndexOf(tab);
        var sourceFolder = Folders.FirstOrDefault(f => f.Tabs.Contains(tab));
        int sourceFolderIndex = sourceFolder?.Tabs.IndexOf(tab) ?? -1;

        // 1. Detach
        if (sourcePinnedIndex >= 0) PinnedTabs.RemoveAt(sourcePinnedIndex);
        if (sourceLooseIndex >= 0) LooseTabs.RemoveAt(sourceLooseIndex);
        sourceFolder?.Tabs.Remove(tab);

        // Helper to adjust index if we are inserting into the same list AFTER the removed item
        int AdjustIndex(int index, int? oldIndex)
        {
            if (oldIndex.HasValue && oldIndex.Value >= 0 && oldIndex.Value < index)
            {
                return index - 1;
            }
            return index;
        }

        // 2. Attach
        if (target is PinnedTarget p)
        {
            int idx = AdjustIndex(p.Index, sourcePinnedIndex);
            idx = Math.Max(0, Math.Min(idx, PinnedTabs.Count));
            PinnedTabs.Insert(idx, tab);
        }
        else if (target is FolderTarget f)
        {
            var folder = Folders.FirstOrDefault(x => x.Id == f.FolderId);
            if (folder != null)
            {
                int? oldIdx = sourceFolder?.Id == f.FolderId ? sourceFolderIndex : null;
                int idx = AdjustIndex(f.Index, oldIdx);
                idx = Math.Max(0, Math.Min(idx, folder.Tabs.Count));
                folder.Tabs.Insert(idx, tab);
                folder.IsExpanded = true;
            }
        }
        else if (target is LooseTarget l)
        {
            int idx = AdjustIndex(l.Index, sourceLooseIndex);
            idx = Math.Max(0, Math.Min(idx, LooseTabs.Count));
            LooseTabs.Insert(idx, tab);
        }
    }
}

public abstract record DropTarget;
public record PinnedTarget(int Index) : DropTarget;
public record FolderTarget(Guid FolderId, int Index) : DropTarget;
public record LooseTarget(int Index) : DropTarget;
