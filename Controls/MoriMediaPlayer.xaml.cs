using System;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Mori.Models;
using Mori.Theme;

namespace Mori.Controls;

/// <summary>
/// The dynamic sidebar media player strip. 1:1 port of MediaPlayerStrip.swift —
/// artwork, scrubbable progress, skip ±10s, play/pause, mute, and PiP. Bound to
/// the shared <see cref="MediaController"/>.
/// </summary>
public sealed partial class MoriMediaPlayer : UserControl
{
    public MediaController Media => MediaController.Shared;

    private bool _scrubbing;
    private double _scrubValue;

    public MoriMediaPlayer()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ApplyTheme();
            SyncFromState();
        };
        Media.PropertyChanged += Media_PropertyChanged;
        ThemeService.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ThemeService.Palette))
                DispatcherQueue.TryEnqueue(ApplyTheme);
        };
    }

    /// <summary>x:Bind helper: bool → Visibility.</summary>
    public Visibility Vis(bool b) => b ? Visibility.Visible : Visibility.Collapsed;

    private void Media_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            switch (e.PropertyName)
            {
                case nameof(MediaController.Playing):
                    PlayPauseIcon.Glyph = Media.Playing ? "" : ""; // pause : play
                    break;
                case nameof(MediaController.Muted):
                    UpdateMute();
                    break;
                case nameof(MediaController.InPiP):
                    PiPIcon.Glyph = Media.InPiP ? "" : "";
                    break;
                case nameof(MediaController.IsVideo):
                    ArtFallbackIcon.Glyph = Media.IsVideo ? "" : ""; // video : music note
                    break;
                case nameof(MediaController.Artwork):
                    UpdateArtwork();
                    break;
                case nameof(MediaController.ProgressFraction):
                case nameof(MediaController.Position):
                case nameof(MediaController.Duration):
                    if (!_scrubbing) UpdateScrubber(Media.ProgressFraction);
                    break;
                case nameof(MediaController.HasMedia):
                    SyncFromState();
                    break;
            }
        });
    }

    private void SyncFromState()
    {
        PlayPauseIcon.Glyph = Media.Playing ? "" : "";
        PiPIcon.Glyph = Media.InPiP ? "" : "";
        ArtFallbackIcon.Glyph = Media.IsVideo ? "" : "";
        UpdateMute();
        UpdateArtwork();
        UpdateScrubber(Media.ProgressFraction);
    }

    private void UpdateMute()
    {
        MuteIcon.Glyph = Media.Muted ? "" : ""; // muted : volume
        MuteIcon.Foreground = Media.Muted ? PrimaryBrush() : MutedTransportBrush();
    }

    private void UpdateArtwork()
    {
        if (!string.IsNullOrEmpty(Media.Artwork) && Uri.TryCreate(Media.Artwork, UriKind.Absolute, out var uri))
        {
            ArtworkImage.Source = new BitmapImage(uri);
            ArtworkImage.Visibility = Visibility.Visible;
            ArtFallback.Visibility = Visibility.Collapsed;
        }
        else
        {
            ArtworkImage.Source = null;
            ArtworkImage.Visibility = Visibility.Collapsed;
            ArtFallback.Visibility = Visibility.Visible;
        }
    }

    // ── Scrubber ─────────────────────────────────────────────────────────────

    private void UpdateScrubber(double fraction)
    {
        double w = ScrubHit.ActualWidth;
        if (w <= 0) return;
        double x = Math.Clamp(fraction, 0, 1) * w;
        ScrubFill.Width = x;
        double thumb = _scrubbing ? 11 : 8;
        ScrubThumb.Width = thumb;
        ScrubThumb.Height = thumb;
        ScrubThumb.Margin = new Thickness(Math.Clamp(x - thumb / 2, 0, Math.Max(0, w - thumb)), 0, 0, 0);
    }

    private void Scrub_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateScrubber(_scrubbing ? ScrubFraction() : Media.ProgressFraction);

    private double ScrubFraction()
        => Media.Duration > 0 ? Math.Clamp(_scrubValue / Media.Duration, 0, 1) : 0;

    private void Scrub_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _scrubbing = true;
        ScrubHit.CapturePointer(e.Pointer);
        ApplyScrubFromPointer(e);
    }

    private void Scrub_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_scrubbing) return;
        ApplyScrubFromPointer(e);
    }

    private void Scrub_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_scrubbing) return;
        _scrubbing = false;
        ScrubHit.ReleasePointerCapture(e.Pointer);
        if (Media.Duration > 0) Media.Seek(_scrubValue);
        UpdateScrubber(Media.ProgressFraction);
    }

    private void ApplyScrubFromPointer(Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        double w = ScrubHit.ActualWidth;
        if (w <= 0) return;
        double px = e.GetCurrentPoint(ScrubHit).Position.X;
        double frac = Math.Clamp(px / w, 0, 1);
        _scrubValue = frac * Math.Max(Media.Duration, 0);
        UpdateScrubber(frac);
    }

    // ── Transport ────────────────────────────────────────────────────────────

    private void PlayPause_Click(object sender, RoutedEventArgs e) => Media.TogglePlay();
    private void SkipBack_Click(object sender, RoutedEventArgs e) => Media.SkipBack();
    private void SkipForward_Click(object sender, RoutedEventArgs e) => Media.SkipForward();
    private void Mute_Click(object sender, RoutedEventArgs e) => Media.ToggleMute();
    private void PiP_Click(object sender, RoutedEventArgs e) => Media.TogglePiP();
    private void Artwork_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) => Media.RevealOwningTab();

    // ── Theming ──────────────────────────────────────────────────────────────

    private static SolidColorBrush PrimaryBrush()
        => ThemeService.Instance.Palette.Primary.ToBrush();
    private static SolidColorBrush MutedTransportBrush()
        => ThemeService.Instance.Palette.SidebarForeground.WithOpacity(0.85).ToBrush();

    private void ApplyTheme()
    {
        var p = ThemeService.Instance.Palette;
        // Card surface: sidebarAccent @ 0.7 over the border @ 0.5 (matches mac).
        Card.Background = p.SidebarAccent.WithOpacity(0.7).ToBrush();
        Card.BorderBrush = p.SidebarBorder.WithOpacity(0.5).ToBrush();

        ArtworkBorder.Background = p.Muted.ToBrush();
        ArtworkBorder.BorderBrush = p.SidebarBorder.WithOpacity(0.4).ToBrush();
        ArtFallbackIcon.Foreground = p.MutedForeground.ToBrush();

        TitleText.Foreground = p.SidebarForeground.ToBrush();
        ArtistText.Foreground = p.MutedForeground.ToBrush();
        TimeText.Foreground = p.MutedForeground.ToBrush();

        ScrubTrack.Fill = p.Foreground.WithOpacity(0.12).ToBrush();
        ScrubFill.Fill = p.Primary.ToBrush();
        ScrubThumb.Fill = p.Primary.ToBrush();

        var transport = MutedTransportBrush();
        foreach (var icon in new[] { PiPIcon, (FontIcon)((Button)SkipBackButton).Content, (FontIcon)((Button)SkipForwardButton).Content })
            icon.Foreground = transport;
        PlayPauseIcon.Foreground = p.Primary.ToBrush();
        UpdateMute();
    }
}
