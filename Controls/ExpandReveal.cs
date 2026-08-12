using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Skew.Theme;
using Windows.Foundation;

namespace Skew.Controls;

/// <summary>
/// A container that reveals its content downward instead of snapping it into
/// place: opening grows the box from nothing to the content's natural height
/// while the content slides down into it, closing runs the same motion in
/// reverse. What the sidebar's folders do when they expand.
///
/// <para>
/// The height is a real layout height, not a transform, because everything
/// below the folder has to move with it. That makes this a dependent animation
/// — a layout pass per frame — which is affordable for a folder's handful of
/// rows and would not be for a long list.
/// </para>
///
/// <para>
/// Takes a single child. The slide is a transform on that child rather than on
/// this element, so it travels inside the clip: put it on the container and the
/// content would ride up over the folder header instead of out from under it.
/// </para>
/// </summary>
public sealed class ExpandReveal : Grid
{
    /// <summary>How far the content travels as it comes in, in DIPs.</summary>
    private const double SlideDistance = 10;

    public static readonly DependencyProperty IsOpenProperty =
        DependencyProperty.Register(nameof(IsOpen), typeof(bool), typeof(ExpandReveal),
            new PropertyMetadata(false, OnIsOpenChanged));

    public bool IsOpen
    {
        get => (bool)GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    private readonly TranslateTransform _slide = new();
    private Storyboard? _running;
    private bool _loaded;

    public ExpandReveal()
    {
        // Clipped to its own box, so a partly open reveal shows part of the
        // content rather than all of it overflowing into the rows below.
        SizeChanged += (_, e) => Clip = new RectangleGeometry
        {
            Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height),
        };

        Loaded += (_, _) =>
        {
            _loaded = true;
            // x:Bind sets IsOpen while the template is being realized, before
            // there is anything to measure, so the opening state is applied
            // here instead — without animation, since nothing has been seen yet.
            ApplyState(animate: false);
        };
    }

    private static void OnIsOpenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var reveal = (ExpandReveal)d;
        if (reveal._loaded) reveal.ApplyState(animate: true);
    }

    private void ApplyState(bool animate)
    {
        _running?.Stop();
        _running = null;

        UIElement? content = Children.Count > 0 ? Children[0] : null;
        if (content is not null && content.RenderTransform != _slide)
            content.RenderTransform = _slide;

        if (!animate)
        {
            Visibility = IsOpen ? Visibility.Visible : Visibility.Collapsed;
            Height = IsOpen ? double.NaN : 0;
            Opacity = IsOpen ? 1 : 0;
            _slide.Y = IsOpen ? 0 : -SlideDistance;
            return;
        }

        double from = Visibility == Visibility.Visible ? ActualHeight : 0;
        Visibility = Visibility.Visible;

        double to = IsOpen ? NaturalHeight(content) : 0;
        Height = from;

        var sb = new Storyboard();
        // Height is a layout property, so this one has to be marked dependent
        // or it is dropped silently.
        Add(sb, this, "Height", from, to, dependent: true);
        Add(sb, this, "Opacity", Opacity, IsOpen ? 1 : 0);
        Add(sb, _slide, "Y", _slide.Y, IsOpen ? 0 : -SlideDistance);

        sb.Completed += (_, _) =>
        {
            if (_running != sb) return;
            _running = null;

            if (IsOpen)
            {
                // Back to auto, so a tab added to an open folder still resizes
                // it — a height frozen at the animated value would clip it.
                Height = double.NaN;
            }
            else
            {
                Height = 0;
                // Collapsed, not just zero-height: a StackPanel skips its
                // spacing for a collapsed child, and the drop zone inside stops
                // being a target.
                Visibility = Visibility.Collapsed;
            }
        };

        _running = sb;
        sb.Begin();
    }

    /// <summary>
    /// The height the content wants, measured free of the height being animated.
    /// </summary>
    private double NaturalHeight(UIElement? content)
    {
        if (content is null) return 0;

        double width = ActualWidth;
        if (width <= 0 && Parent is FrameworkElement parent) width = parent.ActualWidth;
        if (width <= 0) width = double.PositiveInfinity;

        content.Measure(new Size(width, double.PositiveInfinity));
        return content.DesiredSize.Height;
    }

    private static void Add(Storyboard sb, DependencyObject target, string property,
                            double from, double to, bool dependent = false)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(SkewMotion.Reveal),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            EnableDependentAnimation = dependent,
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        sb.Children.Add(animation);
    }
}
