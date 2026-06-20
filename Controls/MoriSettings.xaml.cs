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

    private void Settings_PropertyChanged(object sender, PropertyChangedEventArgs e)
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
    public int NewTabBehaviorIndex => (int)Settings.NewTabBehavior;
    public void SetNewTabBehaviorIndex(int index) => Settings.NewTabBehavior = (NewTabBehavior)index;

    public int SearchEngineIndex => (int)Settings.SearchEngine;
    public void SetSearchEngineIndex(int index) => Settings.SearchEngine = (SearchEngine)index;

    public int ThemeIndex => (int)Settings.Theme;
    public void SetThemeIndex(int index) => Settings.Theme = (ElementTheme)index;

    public int SidebarPositionIndex => (int)Settings.SidebarPosition;
    public void SetSidebarPositionIndex(int index) => Settings.SidebarPosition = (SidebarPosition)index;

    public Visibility CustomSearchVisibility => Settings.SearchEngine == SearchEngine.Custom ? Visibility.Visible : Visibility.Collapsed;
}
