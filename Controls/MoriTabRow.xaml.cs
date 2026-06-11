using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Mori.Models;

namespace Mori.Controls;

public sealed partial class MoriTabRow : UserControl
{
    public static readonly DependencyProperty TabProperty =
        DependencyProperty.Register(
            nameof(Tab),
            typeof(BrowserTab),
            typeof(MoriTabRow),
            new PropertyMetadata(null, OnTabChanged));

    public BrowserTab Tab
    {
        get => (BrowserTab)GetValue(TabProperty);
        set => SetValue(TabProperty, value);
    }

    private bool _isPointerOver;

    public Visibility GetVisibility(bool b) => b ? Visibility.Visible : Visibility.Collapsed;
    public Visibility GetInverseVisibility(bool b) => b ? Visibility.Collapsed : Visibility.Visible;

    public MoriTabRow()
    {
        InitializeComponent();
        Loaded += MoriTabRow_Loaded;
        Unloaded += MoriTabRow_Unloaded;
    }

    private void MoriTabRow_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateVisualState();
    }

    private void MoriTabRow_Unloaded(object sender, RoutedEventArgs e)
    {
        if (Tab != null)
        {
            Tab.PropertyChanged -= Tab_PropertyChanged;
        }
    }

    private static void OnTabChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MoriTabRow row)
        {
            if (e.OldValue is BrowserTab oldTab)
            {
                oldTab.PropertyChanged -= row.Tab_PropertyChanged;
            }

            if (e.NewValue is BrowserTab newTab)
            {
                newTab.PropertyChanged += row.Tab_PropertyChanged;
            }

            row.Bindings.Update();
            row.UpdateVisualState();
        }
    }

    private void Tab_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BrowserTab.IsSelected))
        {
            UpdateVisualState();
        }
    }

    private void RootGrid_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOver = true;
        UpdateVisualState();
    }

    private void RootGrid_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOver = false;
        UpdateVisualState();
    }

    private void UpdateVisualState()
    {
        if (Tab == null) return;

        if (Tab.IsSelected)
        {
            VisualStateManager.GoToState(this, "Selected", true);
        }
        else if (_isPointerOver)
        {
            VisualStateManager.GoToState(this, "PointerOver", true);
        }
        else
        {
            VisualStateManager.GoToState(this, "Normal", true);
        }
    }

    private void RootGrid_Tapped(object sender, TappedRoutedEventArgs e)
    {
        BrowserStore.Shared.SelectTab(Tab.Id);
    }

    private void RootGrid_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        // Handled implicitly by ContextFlyout
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        BrowserStore.Shared.CloseTab(Tab.Id);
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        BrowserStore.Shared.TogglePin(Tab.Id);
    }
}
