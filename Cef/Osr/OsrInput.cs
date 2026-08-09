using Microsoft.UI.Input;
using Windows.System;
using Xilium.CefGlue;

namespace Mori.Cef.Osr;

/// <summary>
/// Translates WinUI input into Chromium input events.
///
/// <para>
/// Windowed CEF received real window messages and needed none of this. In
/// windowless mode nothing routes input for us: Chromium only sees what we hand
/// it through <c>SendMouseClickEvent</c> / <c>SendKeyEvent</c>. Coordinates
/// crossing this boundary are in DIPs — Chromium applies the device scale
/// factor from <c>GetScreenInfo</c> itself, so scaling here would double-apply.
/// </para>
/// </summary>
internal static class OsrInput
{
    /// <summary>Windows' double-click window, in milliseconds.</summary>
    private const long MultiClickIntervalMs = 500;

    /// <summary>Slop, in DIPs, within which repeated clicks still count as a chain.</summary>
    private const double MultiClickSlop = 4.0;

    /// <summary>
    /// Current keyboard/mouse modifier state as Chromium's flag set. Chromium
    /// relies on these for shift-click, ctrl-scroll zoom, and text selection.
    /// </summary>
    public static CefEventFlags GetModifiers(PointerPointProperties? mouse = null)
    {
        var flags = CefEventFlags.None;

        if (IsDown(VirtualKey.Shift)) flags |= CefEventFlags.ShiftDown;
        if (IsDown(VirtualKey.Control)) flags |= CefEventFlags.ControlDown;
        if (IsDown(VirtualKey.Menu)) flags |= CefEventFlags.AltDown;
        if (IsLocked(VirtualKey.CapitalLock)) flags |= CefEventFlags.CapsLockOn;
        if (IsLocked(VirtualKey.NumberKeyLock)) flags |= CefEventFlags.NumLockOn;

        if (mouse is not null)
        {
            if (mouse.IsLeftButtonPressed) flags |= CefEventFlags.LeftMouseButton;
            if (mouse.IsMiddleButtonPressed) flags |= CefEventFlags.MiddleMouseButton;
            if (mouse.IsRightButtonPressed) flags |= CefEventFlags.RightMouseButton;
        }

        return flags;
    }

    private static bool IsDown(VirtualKey key)
        => (InputKeyboardSource.GetKeyStateForCurrentThread(key)
            & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;

    private static bool IsLocked(VirtualKey key)
        => (InputKeyboardSource.GetKeyStateForCurrentThread(key)
            & Windows.UI.Core.CoreVirtualKeyStates.Locked) != 0;

    /// <summary>Which button a pointer update refers to, if any.</summary>
    public static CefMouseButtonType? ButtonOf(PointerUpdateKind kind) => kind switch
    {
        PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased
            => CefMouseButtonType.Left,
        PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased
            => CefMouseButtonType.Right,
        PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased
            => CefMouseButtonType.Middle,
        _ => null,
    };

    /// <summary>
    /// Tracks click chains so Chromium gets the click count it needs for
    /// double-click word selection and triple-click paragraph selection.
    /// </summary>
    public sealed class ClickCounter
    {
        private CefMouseButtonType _lastButton;
        private long _lastTicks;
        private double _lastX, _lastY;
        private int _count;

        public int Register(CefMouseButtonType button, double x, double y)
        {
            long now = Environment.TickCount64;
            bool chained =
                button == _lastButton &&
                now - _lastTicks <= MultiClickIntervalMs &&
                Math.Abs(x - _lastX) <= MultiClickSlop &&
                Math.Abs(y - _lastY) <= MultiClickSlop;

            _count = chained ? Math.Min(_count + 1, 3) : 1;
            _lastButton = button;
            _lastTicks = now;
            _lastX = x;
            _lastY = y;
            return _count;
        }

        public void Reset() => _count = 0;
    }

    /// <summary>
    /// Build the key event Chromium expects. <paramref name="isKeyUp"/> selects
    /// KeyUp; otherwise RawKeyDown is sent, which is what a real browser sends
    /// before the character event.
    /// </summary>
    public static CefKeyEvent KeyEvent(VirtualKey key, bool isKeyUp, bool isRepeat)
    {
        var flags = GetModifiers();
        if (isRepeat)
            flags |= CefEventFlags.IsRepeat;

        return new CefKeyEvent
        {
            EventType = isKeyUp ? CefKeyEventType.KeyUp : CefKeyEventType.RawKeyDown,
            WindowsKeyCode = (int)key,
            NativeKeyCode = (int)key,
            Modifiers = flags,
            // Alt-modified keys are "system keys" to Chromium; without this,
            // Alt+Left/Right never reach the page's shortcut handling.
            IsSystemKey = (flags & CefEventFlags.AltDown) != 0 &&
                          key != VirtualKey.Menu,
        };
    }

    /// <summary>
    /// The character event that follows a key press. Sent separately from the
    /// raw key event — typing breaks entirely if only one of the two is sent.
    /// </summary>
    public static CefKeyEvent CharEvent(char character)
        => new()
        {
            EventType = CefKeyEventType.Char,
            WindowsKeyCode = character,
            NativeKeyCode = character,
            Character = character,
            UnmodifiedCharacter = character,
            Modifiers = GetModifiers(),
        };
}
