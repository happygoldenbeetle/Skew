using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Mori.Controls;

public sealed partial class MoriAIPanel : UserControl
{
    public MoriAIPanel()
    {
        InitializeComponent();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        // Find the MainWindow and toggle AI panel via store
        if (App.Window is MainWindow mw)
            mw.Store.ToggleAIPanel();
    }
}
