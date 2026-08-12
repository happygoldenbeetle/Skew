using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Skew.Models;

namespace Skew.Controls;

public sealed partial class DownloadsPanel : UserControl
{
    public DownloadStore Store => DownloadStore.Shared;

    public DownloadsPanel()
    {
        this.InitializeComponent();
    }

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        Store.ShowDefaultFolder();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        Store.ClearFinished();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is DownloadItem item)
        {
            Store.Cancel(item);
        }
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is DownloadItem item)
        {
            Store.Reveal(item);
        }
    }

    private void Row_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((sender as Grid)?.DataContext is DownloadItem item)
        {
            Store.Open(item);
        }
    }

    private void Row_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid grid)
        {
            grid.Background = (Microsoft.UI.Xaml.Media.Brush)Resources["ButtonBackgroundPointerOver"];
        }
    }

    private void Row_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid grid)
        {
            grid.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
    }

    private void Row_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid grid)
        {
            grid.Background = (Microsoft.UI.Xaml.Media.Brush)Resources["ButtonBackgroundPressed"];
        }
    }

    private async void Row_DragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if ((sender as Grid)?.DataContext is DownloadItem item && item.IsComplete && !string.IsNullOrEmpty(item.Path))
        {
            try
            {
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(item.Path);
                args.Data.SetStorageItems(new[] { file });
            }
            catch { }
        }
    }
}
