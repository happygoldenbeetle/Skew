using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Mori.Models;

namespace Mori.Controls;

public sealed partial class MoriSettings : UserControl
{
    public BrowserStore Store => BrowserStore.Shared;
    public BrowserSettings Settings => BrowserSettings.Shared;

    public MoriSettings()
    {
        this.InitializeComponent();
    }

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        Store.SettingsVisible = false;
    }

    private void Scrim_Tapped(object sender, TappedRoutedEventArgs e)
    {
        Store.SettingsVisible = false;
    }
}
