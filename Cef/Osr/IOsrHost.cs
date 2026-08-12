using Xilium.CefGlue;

namespace Skew.Cef.Osr;

/// <summary>
/// What <see cref="OsrRenderHandler"/> needs from the hosting XAML control.
/// Keeps the render handler free of any WinUI types, the same way
/// <see cref="IBrowserViewDelegate"/> keeps the client free of them.
///
/// <para>
/// Sizes cross this boundary in two different units and mixing them is the
/// classic offscreen-rendering bug. <see cref="GetViewRectDip"/> is in
/// device-independent pixels — Chromium multiplies it by
/// <see cref="DeviceScaleFactor"/> to decide how many real pixels to paint. The
/// buffers handed to <see cref="OnPaint"/> are therefore in device pixels.
/// </para>
/// </summary>
internal interface IOsrHost
{
    /// <summary>The control's logical size, in DIPs. Never zero-sized.</summary>
    CefRectangle GetViewRectDip();

    /// <summary>Monitor scale (1.0 at 96dpi, 1.5 at 150%, …).</summary>
    float DeviceScaleFactor { get; }

    /// <summary>A view or popup layer was repainted, in device pixels.</summary>
    void OnPaint(CefPaintElementType type, CefRectangle[] dirtyRects,
                 IntPtr buffer, int width, int height);

    /// <summary>A native popup widget opened or closed.</summary>
    void OnPopupShow(bool show);

    /// <summary>Where the popup sits, in DIPs relative to the view.</summary>
    void OnPopupSize(CefRectangle rectDip);

    /// <summary>Chromium asked for a different mouse cursor.</summary>
    void OnCursorChanged(CefCursorType type);

    /// <summary>Map a view-relative DIP point to a screen point.</summary>
    bool TryGetScreenPoint(int viewX, int viewY, out int screenX, out int screenY);

    /// <summary>The control's bounds on screen, in DIPs.</summary>
    CefRectangle GetRootScreenRectDip();
}
