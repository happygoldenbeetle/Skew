using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Mori.Controls;

public sealed partial class MoriFindBar : UserControl
{
    public MoriFindBar()
    {
        InitializeComponent();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Visibility = Visibility.Collapsed;
    }
}
