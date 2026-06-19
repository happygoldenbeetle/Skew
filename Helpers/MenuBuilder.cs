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
            Text = isPinned ? "Unpin" : "Pin",
            Icon = new FontIcon { Glyph = isPinned ? "\uE77A" : "\uE718" }
        };
        pinItem.Click += (s, e) => App.DispatcherQueue.TryEnqueue(() => store.TogglePin(tab.Id));
        flyout.Items.Add(pinItem);

        // Add to Folder
        if (store.Folders.Count > 0)
        {
            var folderMenu = new MenuFlyoutSubItem
            {
                Text = "Add to Folder",
                Icon = new FontIcon { Glyph = "\uE8B7" }
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
            Text = "New Folder with Tab",
            Icon = new FontIcon { Glyph = "\uE8F4" }
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
                Text = "Remove from Folder",
                Icon = new FontIcon { Glyph = "\uE108" }
            };
            remFolderItem.Click += (s, e) => App.DispatcherQueue.TryEnqueue(() => store.RemoveTabFromFolders(tab.Id));
            flyout.Items.Add(remFolderItem);
        }

        flyout.Items.Add(new MenuFlyoutSeparator());

        // Duplicate Tab
        var dupItem = new MenuFlyoutItem
        {
            Text = "Duplicate Tab",
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        dupItem.Click += (s, e) => App.DispatcherQueue.TryEnqueue(() => store.DuplicateTab(tab.Id));
        flyout.Items.Add(dupItem);

        // Copy URL
        var copyUrlItem = new MenuFlyoutItem
        {
            Text = "Copy URL",
            Icon = new FontIcon { Glyph = "\uE71B" }
        };
        copyUrlItem.Click += (s, e) => App.DispatcherQueue.TryEnqueue(() => store.CopyUrl(tab.Id));
        flyout.Items.Add(copyUrlItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        // Reload
        var reloadItem = new MenuFlyoutItem
        {
            Text = "Reload",
            Icon = new FontIcon { Glyph = "\uE72C" }
        };
        reloadItem.Click += (s, e) => App.DispatcherQueue.TryEnqueue(() => tab.Reload());
        flyout.Items.Add(reloadItem);

        // Close Other Tabs
        var closeOtherItem = new MenuFlyoutItem
        {
            Text = "Close Other Tabs",
            Icon = new FontIcon { Glyph = "\uE8E6" }
        };
        closeOtherItem.IsEnabled = store.Tabs.Any(t => t.Id != tab.Id && !store.IsPinned(t.Id));
        closeOtherItem.Click += (s, e) => App.DispatcherQueue.TryEnqueue(() => store.CloseOtherTabs(tab.Id));
        flyout.Items.Add(closeOtherItem);

        // Close Tabs to Right
        var closeRightItem = new MenuFlyoutItem
        {
            Text = "Close Tabs to Right",
            Icon = new FontIcon { Glyph = "\uE8E4" }
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
            Icon = new FontIcon { Glyph = "\uE8BB" }, // E8BB is the perfectly centered standard Close icon
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
        var newTabItem = new MenuFlyoutItem { Text = "New Tab", Icon = new FontIcon { Glyph = "\uE710" } };
        newTabItem.Click += (s, e) => App.DispatcherQueue.TryEnqueue(() => store.NewTab());
        flyout.Items.Add(newTabItem);

        // Add New Folder
        var newFolderItem = new MenuFlyoutItem { Text = "Add New Folder", Icon = new FontIcon { Glyph = "\uE8F4" } };
        newFolderItem.Click += (s, e) => App.DispatcherQueue.TryEnqueue(() => store.AddFolderForEditing());
        flyout.Items.Add(newFolderItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        // Show/Hide AI Panel
        var aiPanelItem = new MenuFlyoutItem 
        { 
            Text = store.AiPanelVisible ? "Hide AI Panel" : "Show AI Panel",
            Icon = new PathIcon { Data = (Microsoft.UI.Xaml.Media.Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Microsoft.UI.Xaml.Media.Geometry), "F1 M3.025 5.623a.501.501 0 0 0 .95 0l.421-1.263 1.263-.421a.5.5 0 0 0 0-.948L4.396 2.57l-.421-1.263c-.137-.408-.812-.408-.949 0L2.605 2.57l-1.263.421a.5.5 0 0 0 0 .948l1.263.421zm13.5 3.18L11.99 7.01l-1.793-4.535c-.227-.572-1.168-.572-1.395 0L7.009 7.01 2.474 8.803a.75.75 0 0 0 0 1.394l4.535 1.793 1.793 4.535a.75.75 0 0 0 1.394 0l1.793-4.535 4.535-1.793a.751.751 0 0 0 .001-1.394") }
        };
        aiPanelItem.Click += (s, e) => App.DispatcherQueue.TryEnqueue(() => store.ToggleAIPanel());
        flyout.Items.Add(aiPanelItem);

        // Sidebar Side
        var sideItem = new MenuFlyoutSubItem 
        { 
            Text = "Sidebar Side", 
            Icon = new PathIcon { Data = (Microsoft.UI.Xaml.Media.Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Microsoft.UI.Xaml.Media.Geometry), "F1 M13.75 8.25a.75.75 0 0 1 0 1.5h-1.5a.75.75 0 0 1 0-1.5zM13.75 5.5a.75.75 0 0 1 0 1.5h-1.5a.75.75 0 0 1 0-1.5z M14.25 2A2.75 2.75 0 0 1 17 4.75v8.5A2.75 2.75 0 0 1 14.25 16H3.75A2.75 2.75 0 0 1 1 13.25v-8.5A2.75 2.75 0 0 1 3.75 2zM3.75 3.5c-.69 0-1.25.56-1.25 1.25v8.5c0 .69.56 1.25 1.25 1.25H9v-11zm6.75 11h3.75c.69 0 1.25-.56 1.25-1.25v-8.5c0-.69-.56-1.25-1.25-1.25H10.5z") }
        };
        var leftItem = new MenuFlyoutItem { Text = "Left", Icon = store.SidebarOnLeft ? new FontIcon { Glyph = "\uE73E" } : null }; // CheckMark
        leftItem.Click += (s, e) => App.DispatcherQueue.TryEnqueue(() => store.SidebarOnLeft = true);
        var rightItem = new MenuFlyoutItem { Text = "Right", Icon = !store.SidebarOnLeft ? new FontIcon { Glyph = "\uE73E" } : null }; // CheckMark
        rightItem.Click += (s, e) => App.DispatcherQueue.TryEnqueue(() => store.SidebarOnLeft = false);
        sideItem.Items.Add(leftItem);
        sideItem.Items.Add(rightItem);
        flyout.Items.Add(sideItem);

        // Hide Sidebar
        var hideSidebarItem = new MenuFlyoutItem 
        { 
            Text = "Hide Sidebar", 
            Icon = new FontIcon { Glyph = store.SidebarOnLeft ? "\uE76B" : "\uE76C" } // ClosePane (points left) or OpenPane (points right)
        };
        hideSidebarItem.Click += (s, e) => App.DispatcherQueue.TryEnqueue(() => store.ToggleSidebar());
        flyout.Items.Add(hideSidebarItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        // Settings
        var settingsItem = new MenuFlyoutItem { Text = "Settings", Icon = new FontIcon { Glyph = "\uE713" } };
        settingsItem.Click += (s, e) => App.DispatcherQueue.TryEnqueue(() => store.ToggleSettings());
        flyout.Items.Add(settingsItem);
    }
}
