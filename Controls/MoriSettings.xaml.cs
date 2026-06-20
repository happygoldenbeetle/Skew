using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Mori.Models;
using System.ComponentModel;

namespace Mori.Controls;

public sealed partial class MoriSettings : UserControl
{
    public BrowserStore Store => BrowserStore.Shared;
    public BrowserSettings Settings => BrowserSettings.Shared;

    public MoriSettings()
    {
        this.InitializeComponent();
        Settings.PropertyChanged += Settings_PropertyChanged;
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
}
