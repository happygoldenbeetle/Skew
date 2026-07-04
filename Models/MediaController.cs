using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Mori.Models;

/// <summary>
/// Aggregates media state broadcast by every tab's injected agent (the
/// <c>__MORI_MEDIA__</c> console markers) and exposes playback controls for the
/// sidebar player. The "active" source is whichever tab is most recently playing.
/// Port of MediaController.swift.
/// </summary>
public partial class MediaController : ObservableObject
{
    public static MediaController Shared { get; } = new();

    [ObservableProperty] private bool _hasMedia;
    [ObservableProperty] private bool _playing;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _artist = "";
    [ObservableProperty] private string _artwork = "";
    [ObservableProperty] private double _position;
    [ObservableProperty] private double _duration;
    [ObservableProperty] private bool _muted;
    [ObservableProperty] private bool _isVideo;
    [ObservableProperty] private bool _inPiP;
    [ObservableProperty] private bool _canPiP;

    private int _browserId;

    // ── Derived values the strip binds to ────────────────────────────────────

    public string DisplayTitle => string.IsNullOrEmpty(Title) ? "Playing" : Title;
    public bool HasArtist => !string.IsNullOrEmpty(Artist);
    public bool ShowPiP => CanPiP || InPiP;
    public double ProgressFraction => Duration > 0 ? Math.Clamp(Position / Duration, 0, 1) : 0;
    public string TimeLabel => Duration <= 0 ? Fmt(Position) : $"{Fmt(Position)} / {Fmt(Duration)}";

    partial void OnTitleChanged(string value) => OnPropertyChanged(nameof(DisplayTitle));
    partial void OnArtistChanged(string value) => OnPropertyChanged(nameof(HasArtist));
    partial void OnCanPiPChanged(bool value) => OnPropertyChanged(nameof(ShowPiP));
    partial void OnInPiPChanged(bool value) => OnPropertyChanged(nameof(ShowPiP));
    partial void OnPositionChanged(double value)
    {
        OnPropertyChanged(nameof(ProgressFraction));
        OnPropertyChanged(nameof(TimeLabel));
    }
    partial void OnDurationChanged(double value)
    {
        OnPropertyChanged(nameof(ProgressFraction));
        OnPropertyChanged(nameof(TimeLabel));
    }

    // ── Ingest ───────────────────────────────────────────────────────────────

    private readonly record struct Snap(
        bool Playing, string Title, string Artist, string Artwork,
        double Position, double Duration, bool Muted, bool IsVideo, bool InPiP, bool CanPiP);

    private readonly Dictionary<int, Snap> _byBrowser = new();
    private readonly List<int> _order = new(); // browser ids, most-recent last

    /// <summary>Parse one page's <c>__MORI_MEDIA__</c> payload and recompute the active source.</summary>
    public void Ingest(int browserId, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var o = doc.RootElement;

            bool has = o.TryGetProperty("hasMedia", out var hm) &&
                       hm.ValueKind == JsonValueKind.True;
            if (!has)
            {
                _byBrowser.Remove(browserId);
                _order.RemoveAll(x => x == browserId);
            }
            else
            {
                string GetS(string k) =>
                    o.TryGetProperty(k, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";
                double GetD(string k) =>
                    o.TryGetProperty(k, out var e) && e.ValueKind == JsonValueKind.Number ? e.GetDouble() : 0;
                bool GetB(string k) =>
                    o.TryGetProperty(k, out var e) && (e.ValueKind == JsonValueKind.True || e.ValueKind == JsonValueKind.False) && e.GetBoolean();

                // Tolerate the older payload shape (paused / currentTime).
                bool playing = o.TryGetProperty("playing", out var pl) && (pl.ValueKind == JsonValueKind.True || pl.ValueKind == JsonValueKind.False)
                    ? pl.GetBoolean()
                    : !GetB("paused");
                double pos = o.TryGetProperty("position", out var ps) && ps.ValueKind == JsonValueKind.Number
                    ? ps.GetDouble()
                    : GetD("currentTime");

                _byBrowser[browserId] = new Snap(
                    playing, GetS("title"), GetS("artist"), GetS("artwork"),
                    pos, GetD("duration"), GetB("muted"), GetB("isVideo"), GetB("inPiP"), GetB("canPiP"));
                _order.RemoveAll(x => x == browserId);
                _order.Add(browserId);
            }

            Recompute();
        }
        catch (JsonException)
        {
            // Malformed payload — ignore.
        }
    }

    private void Recompute()
    {
        // Walk most-recent first: the fallback is the most-recent source, but a
        // source that is actively playing wins (the most-recent playing one).
        Snap? chosenSnap = null;
        int chosenId = 0;
        for (int i = _order.Count - 1; i >= 0; i--)
        {
            if (!_byBrowser.TryGetValue(_order[i], out var s)) continue;
            if (chosenSnap is null) { chosenSnap = s; chosenId = _order[i]; }
            if (s.Playing) { chosenSnap = s; chosenId = _order[i]; break; }
        }

        if (chosenSnap is null)
        {
            _browserId = 0;
            HasMedia = false;
            Playing = false;
            return;
        }

        var snap = chosenSnap.Value;
        _browserId = chosenId;
        HasMedia = true;
        Playing = snap.Playing;
        Title = snap.Title;
        Artist = snap.Artist;
        Artwork = snap.Artwork;
        Position = snap.Position;
        Duration = snap.Duration;
        Muted = snap.Muted;
        IsVideo = snap.IsVideo;
        InPiP = snap.InPiP;
        CanPiP = snap.CanPiP;
    }

    // ── Controls ─────────────────────────────────────────────────────────────

    private void Command(string action, double value = 0)
    {
        var store = BrowserStore.Shared;
        var tab = store.Tabs.Concat(store.PinnedTabs).Concat(store.LooseTabs)
            .FirstOrDefault(t => t.HasBrowserView && t.BrowserView.BrowserIdentifier == _browserId);
        tab?.BrowserView.SendMediaCommand(action, value);
    }

    public void TogglePlay() => Command(Playing ? "pause" : "play");
    public void SkipForward() => Command("skip", 10);
    public void SkipBack() => Command("skip", -10);
    public void Seek(double seconds) => Command("seek", seconds);
    public void ToggleMute() => Command("mute");
    public void TogglePiP() => Command("pip");

    /// <summary>Bring the tab that owns the active media to the foreground.</summary>
    public void RevealOwningTab()
    {
        var store = BrowserStore.Shared;
        var tab = store.Tabs.Concat(store.PinnedTabs).Concat(store.LooseTabs)
            .FirstOrDefault(t => t.HasBrowserView && t.BrowserView.BrowserIdentifier == _browserId);
        if (tab is not null) store.SelectTab(tab.Id);
    }

    private static string Fmt(double t)
    {
        if (double.IsNaN(t) || double.IsInfinity(t) || t < 0) return "0:00";
        int total = (int)t;
        int m = total / 60, sec = total % 60;
        if (m >= 60) return $"{m / 60}:{m % 60:D2}:{sec:D2}";
        return $"{m}:{sec:D2}";
    }
}
