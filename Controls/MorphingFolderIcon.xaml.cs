using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Mori.Theme;

namespace Mori.Controls;

/// <summary>
/// A folder icon that morphs between a closed folder and an open "pocket folder"
/// by shearing two stacked vector panels in opposite directions. Port of
/// MorphingFolderIcon.swift.
///
/// <para>
/// A single 0→1 progress drives both panels so they splay apart together. The
/// Swift version expresses this as an animatable GeometryEffect; here it is a
/// <see cref="CompositeTransform"/> per panel, animated by a storyboard. The
/// transform order differs slightly — WinUI applies scale, then skew, then
/// translate, where the Swift matrix translates before skewing — but at these
/// magnitudes the skew contributes well under half a pixel to the offset.
/// </para>
/// </summary>
public sealed partial class MorphingFolderIcon : UserControl
{
    // Splay constants, in the 32x32 design space, straight from the Swift source.
    private const double BackAngleDegrees = 16;
    private const double BackDx = -4;
    private const double BackDy = 2;
    private const double FrontAngleDegrees = -16;
    private const double FrontDx = 8;
    private const double FrontDy = 2;
    private const double OpenScale = 0.85; // 1 - 0.15 * progress

    private static readonly TimeSpan MorphDuration = TimeSpan.FromMilliseconds(300);

    public static readonly DependencyProperty IsOpenProperty =
        DependencyProperty.Register(nameof(IsOpen), typeof(bool), typeof(MorphingFolderIcon),
            new PropertyMetadata(false, OnStateChanged));

    public static readonly DependencyProperty ShowsDotsProperty =
        DependencyProperty.Register(nameof(ShowsDots), typeof(bool), typeof(MorphingFolderIcon),
            new PropertyMetadata(false, OnStateChanged));

    /// <summary>Expanded folder == open pocket.</summary>
    public bool IsOpen
    {
        get => (bool)GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    /// <summary>Collapsed folder that holds the active tab → show dots.</summary>
    public bool ShowsDots
    {
        get => (bool)GetValue(ShowsDotsProperty);
        set => SetValue(ShowsDotsProperty, value);
    }

    public MorphingFolderIcon()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ApplyPalette();
            ApplyState(animate: false);
        };
        ThemeService.Instance.PropertyChanged += ThemeService_PropertyChanged;
        Unloaded += (_, _) => ThemeService.Instance.PropertyChanged -= ThemeService_PropertyChanged;
    }

    private void ThemeService_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ThemeService.Palette))
            ApplyPalette();
    }

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MorphingFolderIcon icon)
            icon.ApplyState(animate: true);
    }

    /// <summary>
    /// Colours from FolderRow in Sidebar.swift: the panels are primary washes,
    /// the outline and glyph are sidebar foreground, and the front pocket's base
    /// is the opaque sidebar colour so it occludes the rear panel when open.
    /// </summary>
    private void ApplyPalette()
    {
        var p = ThemeService.Instance.Palette;

        BackPanel.Fill = p.Primary.WithOpacity(0.32).ToBrush();
        BackPanel.Stroke = p.SidebarForeground.WithOpacity(0.55).ToBrush();

        FrontBase.Fill = p.Sidebar.ToBrush();
        FrontTint.Fill = p.Primary.WithOpacity(0.18).ToBrush();
        FrontStroke.Stroke = p.SidebarForeground.WithOpacity(0.55).ToBrush();

        Dots.Fill = p.SidebarForeground.WithOpacity(0.55).ToBrush();
    }

    private void ApplyState(bool animate)
    {
        double progress = IsOpen ? 1 : 0;
        double scale = 1 - (1 - OpenScale) * progress;

        if (!animate)
        {
            SetSplit(BackSplit, progress, BackAngleDegrees, BackDx, BackDy, scale);
            SetSplit(FrontSplit, progress, FrontAngleDegrees, FrontDx, FrontDy, scale);
            Dots.Opacity = ShowsDots ? 1 : 0;
            return;
        }

        var sb = new Storyboard();
        // timingCurve(0.42, 0, 0, 1) — a strong decelerate, matching the Swift curve.
        var spline = new KeySpline { ControlPoint1 = new(0.42, 0), ControlPoint2 = new(0, 1) };

        AnimateSplit(sb, spline, BackSplit, progress, BackAngleDegrees, BackDx, BackDy, scale);
        AnimateSplit(sb, spline, FrontSplit, progress, FrontAngleDegrees, FrontDx, FrontDy, scale);

        AddAnimation(sb, spline, Dots, "Opacity", ShowsDots ? 1 : 0, dependent: false);
        sb.Begin();
    }

    private static void SetSplit(CompositeTransform t, double progress, double angle,
                                 double dx, double dy, double scale)
    {
        t.ScaleX = scale;
        t.ScaleY = scale;
        t.SkewX = angle * progress;
        t.TranslateX = dx * progress;
        t.TranslateY = dy * progress;
    }

    private static void AnimateSplit(Storyboard sb, KeySpline spline, CompositeTransform t,
                                     double progress, double angle, double dx, double dy, double scale)
    {
        AddAnimation(sb, spline, t, "ScaleX", scale);
        AddAnimation(sb, spline, t, "ScaleY", scale);
        AddAnimation(sb, spline, t, "SkewX", angle * progress);
        AddAnimation(sb, spline, t, "TranslateX", dx * progress);
        AddAnimation(sb, spline, t, "TranslateY", dy * progress);
    }

    private static void AddAnimation(Storyboard sb, KeySpline spline, DependencyObject target,
                                     string property, double to, bool dependent = true)
    {
        var frames = new DoubleAnimationUsingKeyFrames();
        frames.KeyFrames.Add(new SplineDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(MorphDuration),
            Value = to,
            KeySpline = spline,
        });
        frames.EnableDependentAnimation = dependent;
        Storyboard.SetTarget(frames, target);
        Storyboard.SetTargetProperty(frames, property);
        sb.Children.Add(frames);
    }
}
