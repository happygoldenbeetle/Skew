using System;
using System.Linq;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI;
using Mori.Models;

namespace Mori.Helpers;

public static class MenuBuilder
{
    public static void BuildTabMenu(MenuFlyout flyout, BrowserTab tab)
    {
        if (tab == null) return;
        flyout.Items.Clear();

        var store = BrowserStore.Shared;
        bool isPinned = store.IsPinned(tab.Id);

        // Pin / Unpin
        var pinItem = new MenuFlyoutItem
        {
            Text = isPinned ? "Unpin" : "Pin"
        };
        pinItem.Click += (s, e) => App.DispatcherQueue.TryEnqueue(() => store.TogglePin(tab.Id));
        flyout.Items.Add(pinItem);

        // Add to Folder
        if (store.Folders.Count > 0)
        {
            var folderMenu = new MenuFlyoutSubItem
            {
                Text = "Add to Folder"
            };
            foreach (var folder in store.Folders)
            {
                var folderItem = new MenuFlyoutItem { Text = folder.Name };
                var fId = folder.Id;
                folderItem.Click += (s, e) => App.DispatcherQueue.TryEnqueue(() => store.AddTabToFolder(tab.Id, fId));
                folderMenu.Items.Add(folderItem);
            }
            flyout.Items.Add(folderMenu);
        }

        // New Folder with Tab
        var newFolderItem = new MenuFlyoutItem
        {
            Text = "New Folder with Tab"
        };
        newFolderItem.Click += (s, e) => App.DispatcherQueue.TryEnqueue(() =>
        {
            var folder = store.AddFolderForEditing();
            store.AddTabToFolder(tab.Id, folder.Id);
        });
        flyout.Items.Add(newFolderItem);

        // Remove from Folder
        bool inFolder = store.Folders.Any(f => f.TabIds.Contains(tab.Id));
        if (inFolder)
        {
            var remFolderItem = new MenuFlyoutItem
            {
                Text = "Remove from Folder"
            };
            remFolderItem.Click += (s, e) => App.DispatcherQueue.TryEnqueue(() => store.RemoveTabFromFolders(tab.Id));
            flyout.Items.Add(remFolderItem);
        }

        flyout.Items.Add(new MenuFlyoutSeparator());

        // Duplicate Tab
        var dupItem = new MenuFlyoutItem
        {
            Text = "Duplicate Tab"
        };
        dupItem.Click += (s, e) => App.DispatcherQueue.TryEnqueue(() => store.DuplicateTab(tab.Id));
        flyout.Items.Add(dupItem);

        // Copy URL
        var copyUrlItem = new MenuFlyoutItem
        {
            Text = "Copy URL"
        };
        copyUrlItem.Click += (s, e) => App.DispatcherQueue.TryEnqueue(() => store.CopyUrl(tab.Id));
        flyout.Items.Add(copyUrlItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        // Reload
        var reloadItem = new MenuFlyoutItem
        {
            Text = "Reload"
        };
        reloadItem.Click += (s, e) => App.DispatcherQueue.TryEnqueue(() => tab.Reload());
        flyout.Items.Add(reloadItem);

        // Close Other Tabs
        var closeOtherItem = new MenuFlyoutItem
        {
            Text = "Close Other Tabs"
        };
        closeOtherItem.IsEnabled = store.Tabs.Any(t => t.Id != tab.Id && !store.IsPinned(t.Id));
        closeOtherItem.Click += (s, e) => App.DispatcherQueue.TryEnqueue(() => store.CloseOtherTabs(tab.Id));
        flyout.Items.Add(closeOtherItem);

        // Close Tabs to Right
        var closeRightItem = new MenuFlyoutItem
        {
            Text = "Close Tabs to Right"
        };
        closeRightItem.IsEnabled = store.HasClosableTabsToRight(tab.Id);
        closeRightItem.Click += (s, e) => App.DispatcherQueue.TryEnqueue(() => store.CloseTabsToRight(tab.Id));
        flyout.Items.Add(closeRightItem);

        // Close Tab
        var macRed = ColorHelper.FromArgb(255, 255, 59, 48); // #FF3B30
        var macRedPressed = ColorHelper.FromArgb(255, 217, 54, 43); // Darker red for press

        var closeItem = new MenuFlyoutItem
        {
            Text = "Close Tab",
            Foreground = new SolidColorBrush(macRed)
        };
        
        // Destructive macOS styling: red background with white text on hover
        closeItem.Resources["MenuFlyoutItemBackgroundPointerOver"] = new SolidColorBrush(macRed);
        closeItem.Resources["MenuFlyoutItemForegroundPointerOver"] = new SolidColorBrush(Colors.White);
        closeItem.Resources["MenuFlyoutItemBackgroundPressed"] = new SolidColorBrush(macRedPressed);
        closeItem.Resources["MenuFlyoutItemForegroundPressed"] = new SolidColorBrush(Colors.White);
        
        closeItem.Click += (s, e) => App.DispatcherQueue.TryEnqueue(() => store.CloseTab(tab.Id));
        flyout.Items.Add(closeItem);
    }

    public static void BuildSidebarMenu(MenuFlyout flyout)
    {
        var store = BrowserStore.Shared;
        flyout.Items.Clear();

        // New Tab
        var newTabItem = new MenuFlyoutItem { Text = "New Tab" };
        newTabItem.Click += (s, e) => App.DispatcherQueue.TryEnqueue(() => store.NewTab());
        flyout.Items.Add(newTabItem);

        // Add New Folder
        var newFolderSidebarItem = new MenuFlyoutItem { Text = "Add New Folder" };
        newFolderSidebarItem.Click += (s, e) => App.DispatcherQueue.TryEnqueue(() => store.AddFolderForEditing());
        flyout.Items.Add(newFolderSidebarItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        // Show/Hide AI Panel
        var aiPanelItem = new MenuFlyoutItem 
        { 
            Text = store.AiPanelVisible ? "Hide AI Panel" : "Show AI Panel"
        };
        aiPanelItem.Click += (s, e) => App.DispatcherQueue.TryEnqueue(() => store.ToggleAIPanel());
        flyout.Items.Add(aiPanelItem);

        // Sidebar Side
        var sideItem = new MenuFlyoutSubItem { Text = "Sidebar Side" };
        var leftItem = new MenuFlyoutItem { Text = "Left", Icon = store.SidebarOnLeft ? new FontIcon { Glyph = "\uE73E" } : null }; // CheckMark is fine to keep for selected state
        leftItem.Click += (s, e) => App.DispatcherQueue.TryEnqueue(() => store.SidebarOnLeft = true);
        var rightItem = new MenuFlyoutItem { Text = "Right", Icon = !store.SidebarOnLeft ? new FontIcon { Glyph = "\uE73E" } : null }; // CheckMark
        rightItem.Click += (s, e) => App.DispatcherQueue.TryEnqueue(() => store.SidebarOnLeft = false);
        sideItem.Items.Add(leftItem);
        sideItem.Items.Add(rightItem);
        flyout.Items.Add(sideItem);

        // Hide Sidebar
        var hideSidebarItem = new MenuFlyoutItem { Text = "Hide Sidebar" };
        hideSidebarItem.Click += (s, e) => App.DispatcherQueue.TryEnqueue(() => store.ToggleSidebar());
        flyout.Items.Add(hideSidebarItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        // Settings
        var settingsItem = new MenuFlyoutItem { Text = "Settings" };
        settingsItem.Click += (s, e) => App.DispatcherQueue.TryEnqueue(() => store.ToggleSettings());
        flyout.Items.Add(settingsItem);
    }
}
