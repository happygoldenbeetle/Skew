using Xilium.CefGlue;

namespace Skew.Cef.Osr;

/// <summary>
/// Windowless (offscreen) render sink. This is the piece that has no macOS
/// counterpart: on the Mac, CEF draws into an NSView that already composites
/// correctly inside the SwiftUI tree, so no render handler is needed at all. On
/// Windows a hosted browser window would sit above every XAML layer, so instead
/// we ask Chromium to render into memory and present the frames ourselves.
///
/// <para>
/// Every callback here arrives on the CEF UI thread. Because
/// <see cref="CefRuntimeHost"/> drives <c>DoMessageLoopWork</c> from the WinUI
/// dispatcher, that thread is the UI thread — but the host still guards its own
/// state rather than relying on that coincidence.
/// </para>
/// </summary>
internal sealed class OsrRenderHandler : CefRenderHandler
{
    private readonly IOsrHost _host;

    public OsrRenderHandler(IOsrHost host) => _host = host;

    // ── Geometry ──────────────────────────────────────────────────────────

    protected override void GetViewRect(CefBrowser browser, out CefRectangle rect)
    {
        rect = _host.GetViewRectDip();

        // A zero-sized view makes Chromium skip compositing entirely and the tab
        // never paints again, even after a later resize. Clamp to 1x1.
        if (rect.Width <= 0) rect.Width = 1;
        if (rect.Height <= 0) rect.Height = 1;
    }

    protected override bool GetScreenInfo(CefBrowser browser, CefScreenInfo screenInfo)
    {
        var view = _host.GetViewRectDip();
        if (view.Width <= 0) view.Width = 1;
        if (view.Height <= 0) view.Height = 1;

        screenInfo.DeviceScaleFactor = _host.DeviceScaleFactor;
        screenInfo.Depth = 32;
        screenInfo.DepthPerComponent = 8;
        screenInfo.IsMonochrome = false;
        screenInfo.Rectangle = _host.GetRootScreenRectDip();
        screenInfo.AvailableRectangle = screenInfo.Rectangle;
        return true;
    }

    protected override bool GetRootScreenRect(CefBrowser browser, ref CefRectangle rect)
    {
        rect = _host.GetRootScreenRectDip();
        return rect.Width > 0 && rect.Height > 0;
    }

    protected override bool GetScreenPoint(CefBrowser browser, int viewX, int viewY,
                                           ref int screenX, ref int screenY)
    {
        if (!_host.TryGetScreenPoint(viewX, viewY, out int x, out int y))
            return false;
        screenX = x;
        screenY = y;
        return true;
    }

    // ── Painting ──────────────────────────────────────────────────────────

    protected override void OnPaint(CefBrowser browser, CefPaintElementType type,
                                    CefRectangle[] dirtyRects, IntPtr buffer,
                                    int width, int height)
        => _host.OnPaint(type, dirtyRects, buffer, width, height);

    /// <summary>
    /// GPU path. Only ever called when the browser was created with shared
    /// textures enabled; we stay on the software path for now, so this is
    /// deliberately inert rather than throwing.
    /// </summary>
    protected override void OnAcceleratedPaint(CefBrowser browser, CefPaintElementType type,
                                               CefRectangle[] dirtyRects, IntPtr sharedHandle)
    {
    }

    // ── Native popup widgets (select dropdowns, autofill, colour pickers) ──

    protected override void OnPopupShow(CefBrowser browser, bool show)
        => _host.OnPopupShow(show);

    protected override void OnPopupSize(CefBrowser browser, CefRectangle rect)
        => _host.OnPopupSize(rect);

    // ── Cursor ────────────────────────────────────────────────────────────
    //
    // In CEF 120 the cursor notification lives on CefDisplayHandler, not here;
    // BrowserClient forwards it through IBrowserViewDelegate.OnCursorChange.

    // ── Not used ──────────────────────────────────────────────────────────

    protected override CefAccessibilityHandler GetAccessibilityHandler() => null!;

    protected override void OnScrollOffsetChanged(CefBrowser browser, double x, double y)
    {
    }

    protected override void OnImeCompositionRangeChanged(CefBrowser browser, CefRange selectedRange,
                                                         CefRectangle[] characterBounds)
    {
    }
}
