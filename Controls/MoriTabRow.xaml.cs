using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Mori.Controls;

public sealed partial class MoriTabRow : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register("Title", typeof(string), typeof(MoriTabRow), new PropertyMetadata(string.Empty));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public static readonly DependencyProperty TabIdProperty =
        DependencyProperty.Register("TabId", typeof(Guid), typeof(MoriTabRow), new PropertyMetadata(Guid.Empty));

    public Guid TabId
    {
        get => (Guid)GetValue(TabIdProperty);
        set => SetValue(TabIdProperty, value);
    }

    public static readonly DependencyProperty IsSelectedTabProperty =
        DependencyProperty.Register("IsSelectedTab", typeof(bool), typeof(MoriTabRow), new PropertyMetadata(false, OnIsSelectedTabChanged));

    public bool IsSelectedTab
    {
        get => (bool)GetValue(IsSelectedTabProperty);
        set => SetValue(IsSelectedTabProperty, value);
    }

    public event EventHandler<Guid> SelectRequested;
    public event EventHandler<Guid> CloseRequested;
    public event EventHandler<Guid> PinRequested;
    public event EventHandler<FrameworkElement> ContextMenuRequested;

    private bool _isPointerOver;

    public MoriTabRow()
    {
        this.InitializeComponent();
        UpdateVisualState(false);
    }

    private static void OnIsSelectedTabChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MoriTabRow row)
        {
            row.UpdateVisualState(true);
        }
    }

    private void UserControl_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOver = true;
        UpdateVisualState(true);
    }

    private void UserControl_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOver = false;
        UpdateVisualState(true);
    }

    private void UpdateVisualState(bool useTransitions)
    {
        if (IsSelectedTab)
        {
            VisualStateManager.GoToState(this, "Selected", useTransitions);
            // Apply shadow depth
            TabShadow.Receivers.Clear(); // Can't easily bind shadow receivers directly here without a host, so we skip true drop shadow for now
        }
        else if (_isPointerOver)
        {
            VisualStateManager.GoToState(this, "PointerOver", useTransitions);
        }
        else
        {
            VisualStateManager.GoToState(this, "Normal", useTransitions);
        }
    }

    private void UserControl_Tapped(object sender, TappedRoutedEventArgs e)
    {
        SelectRequested?.Invoke(this, TabId);
        e.Handled = true;
    }

    private void UserControl_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        ContextMenuRequested?.Invoke(this, this);
        e.Handled = true;
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(this, TabId);
    }

    private void PinTab_Click(object sender, RoutedEventArgs e)
    {
        PinRequested?.Invoke(this, TabId);
    }
}
