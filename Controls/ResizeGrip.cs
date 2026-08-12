using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace Skew.Controls;

/// <summary>
/// A drag strip that shows the horizontal resize cursor over it, and a line
/// under the pointer to say where the edge it moves actually is.
///
/// <para>
/// The strip is wider than the line on purpose: a 6pt target is comfortable to
/// grab, a 6pt line would read as a border. So the strip stays transparent and
/// only the hairline down its middle is drawn, fading in on hover the way Arc's
/// does. It stays lit for the length of the drag — <see cref="IsDragging"/> —
/// because the pointer is captured and wanders off the strip while dragging,
/// which would otherwise fade the line out mid-gesture.
/// </para>
///
/// <para>
/// A <see cref="UserControl"/> rather than a Border subclass, because the WinUI
/// primitives are sealed, and because <c>UIElement.ProtectedCursor</c> is
/// protected: a host cannot set the cursor on a plain element it owns, so the
/// element has to set its own.
/// </para>
/// </summary>
public sealed class ResizeGrip : UserControl
{
    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(120);

    /// <summary>3 device pixels at 125%, the scale this was picked at.</summary>
    private const double LineWidth = 2.4;

    /// <summary>
    /// Fixed rather than taken from the palette: this is a chosen colour, and it
    /// reads the same against either theme's chrome.
    /// </summary>
    private static readonly Windows.UI.Color LineColor =
        Windows.UI.Color.FromArgb(0xFF, 0x6E, 0x74, 0x7D);

    private readonly Border _line;
    private Thickness _lineInset;
    private bool _pointerOver;
    private bool _dragging;

    /// <summary>
    /// Inset of the drawn line inside the grab strip.
    ///
    /// <para>
    /// The default holds it a little clear of the strip's ends, which is what
    /// the peek card wants — its grip runs the height of a card that is already
    /// inset from the window. The docked grip runs the height of a column
    /// instead, and sets this to zero so that its own margin alone decides where
    /// the line starts and stops.
    /// </para>
    /// </summary>
    public Thickness LineInset
    {
        get => _lineInset;
        set
        {
            _lineInset = value;
            _line.Margin = value;
        }
    }

    public ResizeGrip()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);

        _line = new Border
        {
            Width = LineWidth,
            CornerRadius = new CornerRadius(LineWidth / 2),
            Background = new SolidColorBrush(LineColor),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(0, 6, 0, 6),
            Opacity = 0,
            IsHitTestVisible = false,
        };
        _lineInset = _line.Margin;

        // The transparent fill is what makes the whole strip hit-testable; an
        // element with no brush lets the pointer fall straight through to
        // whatever is behind it.
        Content = new Grid
        {
            Background = new SolidColorBrush(Colors.Transparent),
            Children = { _line },
        };

        PointerEntered += (_, _) => { _pointerOver = true; UpdateLine(); };
        PointerExited += (_, _) => { _pointerOver = false; UpdateLine(); };
    }

    /// <summary>
    /// Set by the host for the length of a drag, so the line does not fade out
    /// when the captured pointer leaves the strip.
    /// </summary>
    public bool IsDragging
    {
        get => _dragging;
        set
        {
            if (_dragging == value) return;
            _dragging = value;
            UpdateLine();
        }
    }

    private void UpdateLine()
    {
        double target = _dragging ? 1 : _pointerOver ? 0.7 : 0;

        var fade = new DoubleAnimation
        {
            To = target,
            Duration = new Duration(FadeDuration),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(fade, _line);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(fade);
        storyboard.Begin();
    }
}
