using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Skew.Models;
using System;
using System.ComponentModel;
using System.Linq;

namespace Skew.Controls;

public sealed partial class SkewSettings : UserControl
{
    public BrowserStore Store => BrowserStore.Shared;
    public BrowserSettings Settings => BrowserSettings.Shared;
    public ExtensionStore ExtensionsStore => ExtensionStore.Shared;

    public SkewSettings()
    {
        this.InitializeComponent();
        Settings.PropertyChanged += Settings_PropertyChanged;
        ExtensionsStore.Extensions.CollectionChanged += (_, _) => UpdateEmptyState();
        Loaded += (_, _) => UpdateEmptyState();
    }

    /// <summary>Show "No extensions installed." when the list is empty (mac parity).</summary>
    private void UpdateEmptyState()
    {
        if (NoExtensionsText is not null)
            NoExtensionsText.Visibility = ExtensionsStore.Extensions.Count == 0
                ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Settings_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Settings.SearchEngine))
        {
            Bindings.Update();
        }
    }

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        Store.SettingsVisible = false;
    }

    private void Scrim_Tapped(object sender, TappedRoutedEventArgs e)
    {
        Store.SettingsVisible = false;
    }

    public void FocusPanel()
    {
        DoneButton.Focus(FocusState.Programmatic);
    }

    // Two-Way binding helpers
    public int NewTabBehaviorIndex
    {
        get => (int)Settings.NewTabBehavior;
        set => Settings.NewTabBehavior = (NewTabBehavior)value;
    }

    public int SearchEngineIndex
    {
        get => (int)Settings.SearchEngine;
        set => Settings.SearchEngine = (SearchEngine)value;
    }

    public int ThemeIndex
    {
        get => (int)Settings.Theme;
        set => Settings.Theme = (ElementTheme)value;
    }

    public int SidebarPositionIndex
    {
        get => (int)Settings.SidebarPosition;
        set => Settings.SidebarPosition = (SidebarPosition)value;
    }

    public Visibility CustomSearchVisibility => Settings.SearchEngine == SearchEngine.Custom ? Visibility.Visible : Visibility.Collapsed;

    private async void LoadUnpackedExtension_Click(object sender, RoutedEventArgs e)
    {
        var folderPicker = new Windows.Storage.Pickers.FolderPicker();
        folderPicker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Desktop;
        folderPicker.FileTypeFilter.Add("*");
        
        WinRT.Interop.InitializeWithWindow.Initialize(folderPicker, App.WindowHandle);

        var folder = await folderPicker.PickSingleFolderAsync();
        if (folder != null)
        {
            var result = await ExtensionsStore.ImportExtensionAsync(folder.Path);
            if (result != null)
            {
                var dialog = new ContentDialog
                {
                    Title = "Extension Error",
                    Content = result,
                    CloseButtonText = "OK",
                    XamlRoot = this.XamlRoot
                };
                await dialog.ShowAsync();
            }
        }
    }

    private async void RemoveExtension_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id)
        {
            var ext = ExtensionsStore.Extensions.FirstOrDefault(x => x.Id == id);
            if (ext != null)
            {
                await ExtensionsStore.RemoveExtensionAsync(ext);
            }
        }
    }

    private async void ExtensionToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch ts && ts.Tag is string id)
        {
            var ext = ExtensionsStore.Extensions.FirstOrDefault(x => x.Id == id);
            if (ext != null && ext.Enabled != ts.IsOn)
            {
                await ExtensionsStore.SetEnabledAsync(ext, ts.IsOn);
            }
        }
    }

}
