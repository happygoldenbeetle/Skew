using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Skew.Models;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Skew.Controls;

public class LauncherItem
{
    public BrowserTab? Tab { get; set; }
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";

    /// <summary>
    /// What choosing this row navigates to, when it is not a tab: the
    /// suggestion's own text rather than whatever is in the box, since the two
    /// differ the moment you arrow down the list.
    /// </summary>
    public string? Query { get; set; }

    public bool IsSearchFallback => Tab == null;
    public bool IsNewTab => Title == "New Tab";

    public Visibility SearchIconVisibility => (IsSearchFallback || IsNewTab) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TabIconVisibility => (IsSearchFallback || IsNewTab) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility SubtitleVisibility => string.IsNullOrEmpty(Subtitle) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility SwitchToTabVisibility => Tab is not null && !IsNewTab ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The subtitle as the row shows it, after an em dash.</summary>
    public string SubtitleWithDash => string.IsNullOrEmpty(Subtitle) ? "" : "— " + Subtitle;
    public Microsoft.UI.Xaml.Media.ImageSource? TabFaviconSource
    {
        get
        {
            return Skew.Helpers.FaviconKit.Resolve(Tab?.FaviconUrl, Tab?.UrlString);
        }
    }
}

/// <summary>
/// The command palette / launcher — Spotlight-style search + tab switcher.
/// Port of LauncherOverlay.swift.
/// </summary>
public sealed partial class SkewLauncher : UserControl
{
    /// <summary>
    /// Rows the card will show, and the reason the list neither scrolls nor
    /// carries a height: it is always short enough to draw whole.
    /// </summary>
    private const int MaxResults = 5;

    /// <summary>
    /// How many of those five open tabs may claim, leaving room for what you
    /// typed and the completions of it.
    /// </summary>
    private const int TabResults = 3;

    private readonly System.Collections.ObjectModel.ObservableCollection<LauncherItem> _launcherItems = new();

    /// <summary>
    /// What the card measures with five rows in it: its two border edges, the
    /// 52 field, the 1 separator, and a list of 4 top padding plus five rows of
    /// 48 each carrying 4 below. The card is pinned so that this height lands
    /// centred, and anything shorter simply leaves room at the bottom.
    /// </summary>
    private const double FullCardHeight = 2 + 52 + 1 + (4 + (MaxResults * (48 + 4)));

    public SkewLauncher()
    {
        InitializeComponent();
        ResultsList.ItemsSource = _launcherItems;
    }

    private void ScrimGrid_SizeChanged(object sender, SizeChangedEventArgs e) => PositionCard();

    /// <summary>
    /// Put the card's top where it would be if the card were full, so a five-row
    /// palette is centred and a one-row palette has its field in the same place
    /// rather than halfway down.
    /// </summary>
    private void PositionCard()
    {
        double available = ScrimGrid.ActualHeight;
        if (available <= 0) return;

        // Small windows: start at the top rather than off the top edge.
        double top = Math.Max(0, (available - FullCardHeight) / 2);
        Card.Margin = new Thickness(24, top, 24, 0);
    }

    public void FocusSearchBox()
    {
        SearchBox.Text = "";
        SearchBox.Focus(FocusState.Programmatic);
        RefreshResults();

        // Stop the close first. A storyboard holds its final value when it
        // ends, so a hide that was still running — or had just finished — kept
        // the card part-way faded, and an opaque card read as a translucent
        // one. Reopening fast enough left it there for good.
        HideAnimation.Stop();
        ScrimGrid.Opacity = 1;
        Card.Opacity = 1;

        PositionCard();
        RevealAnimation.Begin();
    }

    private System.Action? _onHideCompleted;

    public void PlayHideAnimation(System.Action onCompleted)
    {
        _onHideCompleted = onCompleted;
        RevealAnimation.Stop();
        HideAnimation.Begin();
    }

    private void HideAnimation_Completed(object sender, object e)
    {
        _onHideCompleted?.Invoke();
        _onHideCompleted = null;

        // Hand the card back at full strength once it is out of sight, so the
        // faded-out values can never be what the next open starts from.
        HideAnimation.Stop();
        ScrimGrid.Opacity = 1;
        Card.Opacity = 1;
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Up)
        {
            if (ResultsList.SelectedIndex > 0)
                ResultsList.SelectedIndex--;
            else if (ResultsList.SelectedIndex == -1 && ResultsList.Items.Count > 0)
                ResultsList.SelectedIndex = ResultsList.Items.Count - 1;
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Down)
        {
            if (ResultsList.SelectedIndex < ResultsList.Items.Count - 1)
                ResultsList.SelectedIndex++;
            e.Handled = true;
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshResults();
    }

    private BrowserStore? GetStore()
    {
        return (App.Window as MainWindow)?.Store;
    }

    private void RefreshResults()
    {
        var store = GetStore();
        if (store is null) return;

        var text = SearchBox.Text?.Trim().ToLowerInvariant();
        var rawText = SearchBox.Text?.Trim();

        // Anything still in flight is about the previous keystroke.
        _suggestCts?.Cancel();
        _launcherItems.Clear();

        if (string.IsNullOrEmpty(text))
        {
            foreach (var t in store.Tabs.Where(t => t.HasBrowserView).Take(MaxResults))
                _launcherItems.Add(new LauncherItem { Tab = t, Title = t.Title ?? "" });
        }
        else
        {
            // Open tabs first, but not the whole list: the row naming what you
            // typed and the completions under it are why the palette is open,
            // and five rows is all there is.
            foreach (var t in store.Tabs.Where(t => (t.Title?.ToLowerInvariant().Contains(text) ?? false) || (t.UrlString?.ToLowerInvariant().Contains(text) ?? false)).Take(TabResults))
                _launcherItems.Add(new LauncherItem { Tab = t, Title = t.Title ?? "" });

            // Fallback action
            bool isUrl = rawText != null && (rawText.Contains("://") || rawText.StartsWith("about:") || (rawText.Contains('.') && !rawText.Contains(' ')));
            if (isUrl)
            {
                _launcherItems.Add(new LauncherItem { Title = rawText!, Subtitle = "Open URL", Query = rawText });
            }
            else
            {
                _launcherItems.Add(new LauncherItem { Title = rawText!, Subtitle = "Google Search", Query = rawText });

                _suggestCts = new CancellationTokenSource();
                _ = AppendSearchSuggestionsAsync(rawText!, _suggestCts.Token);
            }
        }

        if (_launcherItems.Count > 0)
        {
            ResultsList.SelectedIndex = 0;
        }
    }

    /// <summary>The suggest request for the keystroke being typed right now.</summary>
    private CancellationTokenSource? _suggestCts;

    /// <summary>
    /// One client for the control's lifetime — a new HttpClient per keystroke
    /// leaves a socket in TIME_WAIT for each one.
    /// </summary>
    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(4) };

    /// <summary>
    /// Complete what is being typed, from the same suggest endpoint the address
    /// bar of every Chromium browser uses. The rows arrive after the local ones
    /// are already on screen and never disturb the selection, so the list does
    /// not move under a hand that is on its way to Enter.
    ///
    /// <para>
    /// Failure is silence: no network, a refused request or a shape we did not
    /// expect all leave the tab and fallback rows exactly as they were.
    /// </para>
    /// </summary>
    private async Task AppendSearchSuggestionsAsync(string query, CancellationToken token)
    {
        try
        {
            // A short wait so a fast typist makes one request, not one per key.
            await Task.Delay(140, token);

            string url =
                "https://suggestqueries.google.com/complete/search?client=firefox&q=" +
                Uri.EscapeDataString(query);

            string json = await s_http.GetStringAsync(url, token);
            if (token.IsCancellationRequested) return;

            // ["typed", ["first", "second", …], …]
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array ||
                doc.RootElement.GetArrayLength() < 2)
                return;

            var list = doc.RootElement[1];
            if (list.ValueKind != JsonValueKind.Array) return;

            var suggestions = new List<string>();
            foreach (var entry in list.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.String) continue;
                string? s = entry.GetString();
                if (string.IsNullOrWhiteSpace(s)) continue;
                // The first suggestion is usually what was typed.
                if (string.Equals(s, query, StringComparison.OrdinalIgnoreCase)) continue;
                suggestions.Add(s);
                if (suggestions.Count == MaxResults) break;
            }

            if (suggestions.Count == 0 || token.IsCancellationRequested) return;

            DispatcherQueue.TryEnqueue(() =>
            {
                // The box may have moved on while this was in the air.
                if (token.IsCancellationRequested) return;
                if (!string.Equals(SearchBox.Text?.Trim(), query, StringComparison.Ordinal)) return;

                int selected = ResultsList.SelectedIndex;
                foreach (string s in suggestions)
                {
                    // Only into the room the local rows left.
                    if (_launcherItems.Count >= MaxResults) break;
                    _launcherItems.Add(new LauncherItem { Title = s, Subtitle = "Google Search", Query = s });
                }
                if (selected >= 0) ResultsList.SelectedIndex = selected;
            });
        }
        catch (Exception)
        {
            // Cancelled, offline, timed out or malformed — the list stands.
        }
    }

    private void Scrim_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        GetStore()?.DismissLauncher();
    }

    private void Card_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // Prevent click-through to scrim
        e.Handled = true;
    }

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            GetStore()?.DismissLauncher();
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Enter)
        {
            if (ResultsList.SelectedItem is LauncherItem item)
            {
                ExecuteLauncherItem(item);
            }
            else
            {
                var text = SearchBox.Text?.Trim();
                if (!string.IsNullOrEmpty(text))
                {
                    GetStore()?.NewTab(text);
                    GetStore()?.DismissLauncher();
                }
            }
            e.Handled = true;
        }
    }

    private void ExecuteLauncherItem(LauncherItem item)
    {
        if (item.IsSearchFallback)
        {
            // The row's own text first: arrowing onto a suggestion should open
            // that suggestion, not the half-typed thing still in the box.
            var text = item.Query ?? SearchBox.Text?.Trim();
            if (!string.IsNullOrEmpty(text))
            {
                GetStore()?.NewTab(text);
            }
        }
        else if (item.Tab != null)
        {
            GetStore()?.SelectTab(item.Tab.Id);
        }
        GetStore()?.DismissLauncher();
    }

    private void Result_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is LauncherItem item)
        {
            ExecuteLauncherItem(item);
        }
    }
}
