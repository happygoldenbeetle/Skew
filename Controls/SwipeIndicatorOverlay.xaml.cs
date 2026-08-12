using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace Skew.Controls;

public sealed partial class SwipeIndicatorOverlay : UserControl
{
    public SwipeIndicatorOverlay()
    {
        this.InitializeComponent();
    }

    public void UpdateSize(double width, double height)
    {
        Container.Width = width;
        Container.Height = height;
        if (Container.Clip is RectangleGeometry clipGeometry)
        {
            clipGeometry.Rect = new Windows.Foundation.Rect(0, 0, width, height);
        }
    }

    private Storyboard? _outStoryboard;

    public void UpdateProgress(double delta)
    {
        if (_outStoryboard != null)
        {
            _outStoryboard.Stop();
            _outStoryboard = null;
        }

        bool isBack = delta < 0;
        
        // Add a non-linear resistance to the stretch
        double rawProgress = Math.Abs(delta) / 400.0;
        // Ease out quad for the stretch feel:
        double progress = Math.Min(1.0, 1.0 - Math.Pow(1.0 - Math.Min(rawProgress, 1.0), 2));
        
        Indicator.Opacity = Math.Min(1.0, progress * 3.0);
        
        if (isBack)
        {
            Indicator.HorizontalAlignment = HorizontalAlignment.Left;
            Indicator.Margin = new Thickness(-24, 0, 0, 0);
            IndicatorIcon.Glyph = "\uE72B";
            IndicatorTransform.X = progress * 32; // Slide out further
            IconTransform.X = progress * 12; // Parallax internal slide
        }
        else
        {
            Indicator.HorizontalAlignment = HorizontalAlignment.Right;
            Indicator.Margin = new Thickness(0, 0, -24, 0);
            IndicatorIcon.Glyph = "\uE72A";
            IndicatorTransform.X = -(progress * 32);
            IconTransform.X = -(progress * 12);
        }
        
        // Premium stretch: from circle 48 to pill 100
        Indicator.Width = 48 + (progress * 52);
            
        // Threshold snap feedback
        if (Math.Abs(delta) >= 300)
        {
            Indicator.Background = App.Current.Resources["AccentFillColorDefaultBrush"] as Brush;
            Indicator.Height = 54;
            Indicator.CornerRadius = new CornerRadius(27);
            
            // Limit the rubber band stretch to max +15px using asymptotic curve
            double overshoot = Math.Abs(delta) - 300;
            double rubberBand = 15.0 * (1.0 - Math.Exp(-overshoot / 250.0));
            Indicator.Width = 100 + rubberBand;
            
            IndicatorIcon.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
        }
        else
        {
            Indicator.Background = this.Resources["TranslucentPillBrush"] as Brush;
            Indicator.Height = 48;
            Indicator.CornerRadius = new CornerRadius(24);
            IndicatorIcon.Foreground = App.Current.Resources["TextFillColorPrimaryBrush"] as Brush;
        }
    }

    public void AnimateOut(bool navigated)
    {
        if (_outStoryboard != null)
        {
            _outStoryboard.Stop();
        }

        _outStoryboard = new Storyboard();
        
        var outEasing = navigated 
            ? (EasingFunctionBase)new ExponentialEase { EasingMode = EasingMode.EaseOut }
            : (EasingFunctionBase)new ExponentialEase { EasingMode = EasingMode.EaseOut };
            
        int durationMs = navigated ? 200 : 300;

        var opacityAnim = new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = outEasing
        };
        Storyboard.SetTarget(opacityAnim, Indicator);
        Storyboard.SetTargetProperty(opacityAnim, "Opacity");
        _outStoryboard.Children.Add(opacityAnim);

        var widthAnim = new DoubleAnimation
        {
            To = 48,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = outEasing
        };
        Storyboard.SetTarget(widthAnim, Indicator);
        Storyboard.SetTargetProperty(widthAnim, "Width");
        _outStoryboard.Children.Add(widthAnim);

        var translateAnim = new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = outEasing
        };
        Storyboard.SetTarget(translateAnim, IndicatorTransform);
        Storyboard.SetTargetProperty(translateAnim, "X");
        _outStoryboard.Children.Add(translateAnim);
        
        var iconTranslateAnim = new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = outEasing
        };
        Storyboard.SetTarget(iconTranslateAnim, IconTransform);
        Storyboard.SetTargetProperty(iconTranslateAnim, "X");
        _outStoryboard.Children.Add(iconTranslateAnim);

        if (Indicator.Height > 48)
        {
            var heightAnim = new DoubleAnimation
            {
                To = 48,
                Duration = TimeSpan.FromMilliseconds(durationMs),
                EasingFunction = outEasing
            };
            Storyboard.SetTarget(heightAnim, Indicator);
            Storyboard.SetTargetProperty(heightAnim, "Height");
            _outStoryboard.Children.Add(heightAnim);
        }

        _outStoryboard.Completed += (s, e) =>
        {
            Indicator.Background = this.Resources["TranslucentPillBrush"] as Brush;
            IndicatorIcon.Foreground = App.Current.Resources["TextFillColorPrimaryBrush"] as Brush;
            Indicator.CornerRadius = new CornerRadius(24);
        };

        _outStoryboard.Begin();
    }
}
