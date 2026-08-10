using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Mori.Controls;

/// <summary>
/// A drag strip that shows the horizontal resize cursor while the pointer is
/// over it.
///
/// <para>
/// This exists only because <c>UIElement.ProtectedCursor</c> is protected: a
/// host cannot set the cursor on a plain <see cref="Border"/> it owns, so the
/// element has to set its own. Setting it once at construction is enough — the
/// framework applies it whenever the pointer enters, with no hover handlers.
/// </para>
///
/// <para>
/// A <see cref="UserControl"/> rather than a Border subclass, because the WinUI
/// primitives are sealed. The transparent child is what makes the whole strip
/// hit-testable; an element with no brush would let the pointer fall straight
/// through to the sidebar behind it.
/// </para>
/// </summary>
public sealed class ResizeGrip : UserControl
{
    public ResizeGrip()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        Content = new Border { Background = new SolidColorBrush(Colors.Transparent) };
    }
}
