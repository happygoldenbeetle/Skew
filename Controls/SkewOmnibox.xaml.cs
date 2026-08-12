using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Skew.Models;
using Skew.Cef;
using System.Linq;
using System.Collections.ObjectModel;

namespace Skew.Controls;

public sealed partial class SkewOmnibox : UserControl
{
    public ObservableCollection<BrowserExtension> PinnedExtensions { get; } = new();

    public SkewOmnibox()
    {
        InitializeComponent();
        Loaded += SkewOmnibox_Loaded;
        SyncPinnedExtensions();
        ExtensionStore.Shared.Extensions.CollectionChanged += (s, e) => 
        {
            DispatcherQueue.TryEnqueue(() => 
            {
                UpdateExtensionButtonVisibility();
                SyncPinnedExtensions();
            });
        };
    }

    private void SyncPinnedExtensions()
    {
        PinnedExtensions.Clear();
        foreach (var ext in ExtensionStore.Shared.Extensions.Where(e => e.Pinned))
        {
            PinnedExtensions.Add(ext);
        }
        PinnedExtensionItems.ItemsSource = PinnedExtensions;
        PinnedExtensionItems.Visibility = PinnedExtensions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public ObservableCollection<BrowserExtension> Extensions => ExtensionStore.Shared.Extensions;

    public static readonly DependencyProperty StoreProperty =
        DependencyProperty.Register(nameof(Store), typeof(BrowserStore), typeof(SkewOmnibox), new PropertyMetadata(null));

    public BrowserStore? Store
    {
        get => (BrowserStore?)GetValue(StoreProperty);
        set => SetValue(StoreProperty, value);
    }

    public static readonly DependencyProperty TabProperty =
        DependencyProperty.Register(nameof(Tab), typeof(BrowserTab), typeof(SkewOmnibox), new PropertyMetadata(null, OnTabChanged));

    public BrowserTab? Tab
    {
        get => (BrowserTab?)GetValue(TabProperty);
        set => SetValue(TabProperty, value);
    }

    private static void OnTabChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SkewOmnibox omnibox)
        {
            if (e.OldValue is BrowserTab oldTab)
                oldTab.PropertyChanged -= omnibox.Tab_PropertyChanged;

            if (e.NewValue is BrowserTab newTab)
            {
                newTab.PropertyChanged += omnibox.Tab_PropertyChanged;
                omnibox.RefreshFromTab();
            }
        }
    }

    private void SkewOmnibox_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshFromTab();
        ApplyIdleChrome();
        Theme.ThemeService.Instance.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(Theme.ThemeService.Palette) && !InputBox.FocusState.Equals(FocusState.Unfocused))
                return;
            if (args.PropertyName == nameof(Theme.ThemeService.Palette))
                ApplyIdleChrome();
        };
    }

    /// <summary>
    /// The resting capsule: hairline border at 0.35 of the palette border colour
    /// (Toolbar.swift). Without this the field kept Fluent's default control
    /// border, which is noticeably brighter than the Mac's.
    /// </summary>
    private void ApplyIdleChrome()
    {
        var p = Theme.ThemeService.Instance.Palette;
        OuterBorder.BorderBrush = p.Border.WithOpacity(0.35).ToBrush();
        OuterBorder.BorderThickness = new Thickness(1);
    }

    private void Tab_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BrowserTab.UrlString) || 
            e.PropertyName == nameof(BrowserTab.DisplayUrl))
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                UpdateSecurityIcon();
                UpdateExtensionButtonVisibility();
                if (!_isFocused)
                {
                    InputBox.Text = Tab?.DisplayUrl ?? "";
                }
            });
        }
        else if (e.PropertyName == nameof(BrowserTab.IsLoading))
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                UpdateLoadingState();
            });
        }
    }

    private void RefreshFromTab()
    {
        if (Tab == null) return;
        UpdateSecurityIcon();
        UpdateLoadingState();
        UpdateExtensionButtonVisibility();
        if (!_isFocused)
        {
            InputBox.Text = Tab.DisplayUrl;
        }
    }

    private void UpdateSecurityIcon()
    {
        if (Tab == null || string.IsNullOrEmpty(Tab.UrlString))
        {
            SecureIcon.IconSource = (Microsoft.UI.Xaml.Controls.IconSource)Resources["SearchGlassPath"];
            SecureIcon.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
            return;
        }

        if (Tab.UrlString.StartsWith("https"))
        {
            SecureIcon.IconSource = (Microsoft.UI.Xaml.Controls.IconSource)Resources["LockPath"];
            SecureIcon.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        }
        else if (Tab.UrlString.StartsWith("http"))
        {
            SecureIcon.IconSource = (Microsoft.UI.Xaml.Controls.IconSource)Resources["WarningPath"];
            SecureIcon.Foreground = (Brush)Application.Current.Resources["SystemFillColorCautionBrush"];
        }
        else
        {
            SecureIcon.IconSource = (Microsoft.UI.Xaml.Controls.IconSource)Resources["SearchGlassPath"];
            SecureIcon.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        }
    }

    private void UpdateLoadingState()
    {
        if (Tab == null) return;
        LoadingSpinner.IsActive = Tab.IsLoading;
        LoadingSpinner.Visibility = Tab.IsLoading ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool _isFocused = false;

    private void InputBox_GotFocus(object sender, RoutedEventArgs e)
    {
        _isFocused = true;
        
        // Visual States
        // Focused: solid input fill for legibility while typing, and the ring
        // colour at 0.55 / 1.5px — Toolbar.swift's focused omnibox treatment.
        var focusPalette = Theme.ThemeService.Instance.Palette;
        IdleBackground.Opacity = 0;
        FocusedBackground.Background = focusPalette.Background.ToBrush();
        FocusedBackground.Opacity = 1;
        OuterBorder.BorderBrush = focusPalette.Ring.WithOpacity(0.55).ToBrush();
        OuterBorder.BorderThickness = new Thickness(1.5);

        ExtensionsMenuButton.Visibility = Visibility.Collapsed;
        PinnedExtensionItems.Visibility = Visibility.Collapsed;
        AddExtensionButton.Visibility = Visibility.Collapsed;

        if (!string.IsNullOrEmpty(InputBox.Text))
        {
            ClearButton.Visibility = Visibility.Visible;
        }

        // Show full URL while editing
        if (Tab != null)
        {
            InputBox.Text = Tab.UrlString == "about:blank" ? "" : Tab.UrlString;
        }

        // Select all text natively
        if (InputBox.FindDescendant<TextBox>() is TextBox textBox)
        {
            textBox.TextAlignment = Microsoft.UI.Xaml.TextAlignment.Left;
            textBox.SelectAll();
        }

        RefreshSuggestions();
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        InputBox.Text = string.Empty;
        InputBox.Focus(FocusState.Programmatic);
    }

    private void InputBox_LostFocus(object sender, RoutedEventArgs e)
    {
        _isFocused = false;
        
        // Visual States
        // Idle: the quiet capsule with a hairline border at 0.35.
        var idlePalette = Theme.ThemeService.Instance.Palette;
        IdleBackground.Opacity = 0.5;
        FocusedBackground.Opacity = 0;
        OuterBorder.BorderBrush = idlePalette.Border.WithOpacity(0.35).ToBrush();
        OuterBorder.BorderThickness = new Thickness(1);
        ClearButton.Visibility = Visibility.Collapsed;

        UpdateExtensionButtonVisibility();
        SyncPinnedExtensions();

        // Snap back to canonical display URL when focus leaves
        if (Tab != null)
        {
            InputBox.Text = Tab.DisplayUrl;
        }
        
        if (InputBox.FindDescendant<TextBox>() is TextBox textBox)
        {
            textBox.TextAlignment = Microsoft.UI.Xaml.TextAlignment.Center;
        }
        
        InputBox.ItemsSource = null;
    }

    private void InputBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            if (_isFocused)
            {
                ClearButton.Visibility = string.IsNullOrEmpty(InputBox.Text) ? Visibility.Collapsed : Visibility.Visible;
            }
            RefreshSuggestions();
        }
    }

    private void RefreshSuggestions()
    {
        if (!_isFocused) return;
        var text = InputBox.Text;
        if (string.IsNullOrEmpty(text) || (Tab != null && text == Tab.UrlString))
        {
            InputBox.ItemsSource = null;
            return;
        }
        
        // var suggestions = HistoryStore.Shared.GetSuggestions(text, limit: 6);
        // InputBox.ItemsSource = suggestions.Select(s => new SuggestionItem { Title = s.Title, Url = s.Url }).ToList();
    }

    private void InputBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        string text = "";
        if (args.ChosenSuggestion is SuggestionItem item)
        {
            text = item.Url;
        }
        else
        {
            text = args.QueryText?.Trim() ?? "";
        }

        if (!string.IsNullOrEmpty(text))
        {
            Store?.Navigate(text);
        }
        
        // Focus web view to dismiss keyboard/omnibox focus
        // Store?.SelectedTab?.WebBrowser?.Focus(FocusState.Programmatic);
    }

    public void FocusProgrammatically()
    {
        InputBox.Focus(FocusState.Programmatic);
    }

    private void LeftClickArea_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        InputBox.Focus(FocusState.Programmatic);
        e.Handled = true;
    }

    private void UpdateExtensionButtonVisibility()
    {
        if (_isFocused) 
        {
            AddExtensionButton.Visibility = Visibility.Collapsed;
            ExtensionsMenuButton.Visibility = Visibility.Collapsed;
            return;
        }

        if (Tab != null && Tab.UrlString.Contains("chromewebstore.google.com/detail/"))
        {
            // Only show if not already installed
            bool isInstalled = ExtensionStore.Shared.Extensions.Any(ext => Tab.UrlString.EndsWith(ext.Id, System.StringComparison.OrdinalIgnoreCase) || Tab.UrlString.Contains(ext.Id, System.StringComparison.OrdinalIgnoreCase));
            AddExtensionButton.Visibility = isInstalled ? Visibility.Collapsed : Visibility.Visible;
        }
        else
        {
            AddExtensionButton.Visibility = Visibility.Collapsed;
        }

        ExtensionsMenuButton.Visibility = ExtensionStore.Shared.Extensions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ExtensionsFlyout_Opened(object sender, object e)
    {
        AllExtensionsList.ItemsSource = ExtensionStore.Shared.Extensions;
        MenuEmptyText.Visibility = ExtensionStore.Shared.Extensions.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RunExtension_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.Tag is string extensionId)
        {
            ExtensionsMenuButton.Flyout?.Hide();
            ExtensionBackgroundManager.Activate(extensionId);
        }
    }

    private void ManageExtensions_Click(object sender, RoutedEventArgs e)
    {
        ExtensionsMenuButton.Flyout?.Hide();
        if (Store != null) Store.SettingsVisible = true;
    }

    private async void PinExtensionToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string extId)
        {
            var ext = ExtensionStore.Shared.Extensions.FirstOrDefault(x => x.Id == extId);
            if (ext != null)
            {
                ext.Pinned = !ext.Pinned;
                await ExtensionStore.Shared.SaveExtensionsAsync();
                SyncPinnedExtensions();
                // Rebuild so the pin glyph (bound OneWay) re-reads the new state.
                AllExtensionsList.ItemsSource = null;
                AllExtensionsList.ItemsSource = ExtensionStore.Shared.Extensions;
            }
        }
    }

    private void PinnedExtensionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.Tag is string extensionId)
            ExtensionBackgroundManager.Activate(extensionId);
    }

    private async void AddExtensionButton_Click(object sender, RoutedEventArgs e)
    {
        if (Tab == null || string.IsNullOrEmpty(Tab.UrlString)) return;
        var btn = (Button)sender;
        btn.IsEnabled = false;
        
        var error = await ExtensionStore.Shared.BeginWebStoreInstallAsync(Tab.UrlString);
        
        btn.IsEnabled = true;
        if (error != null)
        {
            System.Diagnostics.Debug.WriteLine(error);
        }
        UpdateExtensionButtonVisibility();
    }
}

public class SuggestionItem
{
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public override string ToString() => string.IsNullOrEmpty(Title) ? Url : Title;
}

public static class VisualTreeExtensions
{
    public static T? FindDescendant<T>(this DependencyObject element) where T : DependencyObject
    {
        if (element == null) return null;
        if (element is T t) return t;

        int childrenCount = VisualTreeHelper.GetChildrenCount(element);
        for (int i = 0; i < childrenCount; i++)
        {
            var child = VisualTreeHelper.GetChild(element, i);
            if (child is T result) return result;

            var descendant = FindDescendant<T>(child);
            if (descendant != null) return descendant;
        }
        return null;
    }
}
