using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Mori.Controls;

public sealed partial class MoriFindBar : UserControl
{
    public Mori.Models.BrowserStore Store => Mori.Models.BrowserStore.Shared;

    public string FormatMatchCount(int count, int ordinal)
    {
        if (count == 0) return "0 / 0";
        return $"{ordinal} / {count}";
    }

    public MoriFindBar()
    {
        InitializeComponent();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Mori.Models.BrowserStore.Shared.FindBarVisible = false;
        Mori.Models.BrowserStore.Shared.SelectedTab?.StopFinding(true);
    }

    public void FocusSearchBox()
    {
        FindTextBox.Focus(FocusState.Programmatic);
        FindTextBox.SelectAll();
    }

    private void FindTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string query = FindTextBox.Text;
        if (string.IsNullOrEmpty(query))
            Mori.Models.BrowserStore.Shared.SelectedTab?.StopFinding(true);
        else
            Mori.Models.BrowserStore.Shared.SelectedTab?.Find(query, true);
    }

    private void FindTextBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            bool forward = !Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            Mori.Models.BrowserStore.Shared.SelectedTab?.Find(FindTextBox.Text, forward);
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            Close_Click(this, null!);
            e.Handled = true;
        }
    }

    private void Prev_Click(object sender, RoutedEventArgs e) => Mori.Models.BrowserStore.Shared.SelectedTab?.Find(FindTextBox.Text, false);
    
    private void Next_Click(object sender, RoutedEventArgs e) => Mori.Models.BrowserStore.Shared.SelectedTab?.Find(FindTextBox.Text, true);
}
