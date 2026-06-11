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

        // Folder with tabs
        var devFolder = new TabFolder("Development", "\uE943", true);
        Folders.Add(devFolder);

        var tab1 = new BrowserTab("https://learn.microsoft.com", "Microsoft Learn");
        tab1.FaviconUrl = "https://learn.microsoft.com/favicon.ico";
        var tab2 = new BrowserTab("https://stackoverflow.com", "Stack Overflow");
        tab2.FaviconUrl = "https://cdn.sstatic.net/Sites/stackoverflow/Img/favicon.ico";

        Tabs.Add(tab1);
        Tabs.Add(tab2);
        devFolder.TabIds.Add(tab1.Id);
        devFolder.TabIds.Add(tab2.Id);

        // Loose tabs
        var loose1 = new BrowserTab("https://reddit.com/r/programming", "r/programming");
        loose1.FaviconUrl = "https://www.reddit.com/favicon.ico";
        var loose2 = new BrowserTab("https://news.ycombinator.com", "Hacker News");

        Tabs.Add(loose1);
        Tabs.Add(loose2);
        LooseTabs.Add(loose1);
        LooseTabs.Add(loose2);
    }

    // ── Tab actions ──

    [RelayCommand]
    public void SelectTab(Guid tabId)
    {
        SelectedTab = Tabs.FirstOrDefault(t => t.Id == tabId);
        SelectedTabId = SelectedTab?.Id;
    }

    public BrowserTab NewTab(string url = "mori://newtab/")
    {
        var tab = new BrowserTab(url);
        Tabs.Add(tab);
        LooseTabs.Add(tab);
        SelectTab(tab.Id);
        return tab;
    }

    [RelayCommand]
    public void CloseTab(Guid tabId)
    {
        var tab = Tabs.FirstOrDefault(t => t.Id == tabId);
        if (tab is null) return;

        Tabs.Remove(tab);
        PinnedTabs.Remove(tab);
        LooseTabs.Remove(tab);

        foreach (var folder in Folders)
            folder.TabIds.Remove(tabId);

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

        string url = input;
        if (!input.Contains("://") && !input.StartsWith("about:"))
        {
            if (input.Contains('.') && !input.Contains(' '))
                url = $"https://{input}";
            else
                url = $"https://www.google.com/search?q={Uri.EscapeDataString(input)}";
        }
        SelectedTab.Load(url);
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
        var tab = Tabs.FirstOrDefault(t => t.Id == tabId);
        if (tab is null) return;

        if (PinnedTabs.Contains(tab))
        {
            PinnedTabs.Remove(tab);
            LooseTabs.Add(tab);
        }
        else
        {
            LooseTabs.Remove(tab);
            foreach (var folder in Folders)
                folder.TabIds.Remove(tabId);
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
        return folder.TabIds
            .Select(id => Tabs.FirstOrDefault(t => t.Id == id))
            .Where(t => t is not null)
            .Cast<BrowserTab>()
            .ToList();
    }

    public TabFolder AddFolder(string name = "New Folder")
    {
        var folder = new TabFolder(name);
        Folders.Add(folder);
        return folder;
    }

    [RelayCommand]
    public void DeleteFolder(Guid folderId)
    {
        var folder = Folders.FirstOrDefault(f => f.Id == folderId);
        if (folder is null) return;

        // Move tabs back to loose
        foreach (var tabId in folder.TabIds)
        {
            var tab = Tabs.FirstOrDefault(t => t.Id == tabId);
            if (tab is not null && !PinnedTabs.Contains(tab) && !LooseTabs.Contains(tab))
                LooseTabs.Add(tab);
        }
        Folders.Remove(folder);
    }
}
