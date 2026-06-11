using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Mori.Models;

namespace Mori.Controls;

public sealed partial class MoriDownloads : UserControl
{
    public BrowserStore Store => BrowserStore.Shared;
    public DownloadStore Downloads => DownloadStore.Shared;

    public MoriDownloads()
    {
        this.InitializeComponent();
    }

    private void Scrim_Tapped(object sender, TappedRoutedEventArgs e)
    {
        Store.DownloadsVisible = false;
    }

    public Visibility HasNoDownloads(int count) => count == 0 ? Visibility.Visible : Visibility.Collapsed;
}
