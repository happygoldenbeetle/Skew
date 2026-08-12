using System.Runtime.InteropServices;
using Xilium.CefGlue;

namespace Skew.Cef.Osr;

/// <summary>
/// Holds the offscreen BGRA frame Chromium paints into, and composites the
/// popup layer (native <c>&lt;select&gt;</c> dropdowns, autofill) over it.
///
/// <para>
/// In windowed mode Chromium owned a child HWND and drew itself; nothing could
/// be layered over it, which is why the Mac chrome (rounded card, translucent
/// peek sidebar, launcher scrim, film grain) had no Windows equivalent. In
/// windowless mode Chromium hands us raw pixels here and the page becomes
/// ordinary XAML content — clippable, animatable, and drawable over.
/// </para>
///
/// <para>
/// Two layers are kept because CEF paints them independently: <c>PET_VIEW</c> is
/// the page, <c>PET_POPUP</c> is the floating widget. The popup must never be
/// blended into the view buffer, or it would smear once dismissed — it is
/// overlaid at present time instead.
/// </para>
/// </summary>
internal sealed class OsrSurface : IDisposable
{
    private const int BytesPerPixel = 4; // BGRA8

    private readonly object _sync = new();

    private IntPtr _viewBuffer;
    private int _viewWidth;
    private int _viewHeight;

    private IntPtr _popupBuffer;
    private int _popupWidth;
    private int _popupHeight;

    private CefRectangle _popupRect;
    private bool _popupVisible;
    private bool _disposed;

    /// <summary>Device-pixel width of the current view layer.</summary>
    public int Width { get { lock (_sync) return _viewWidth; } }

    /// <summary>Device-pixel height of the current view layer.</summary>
    public int Height { get { lock (_sync) return _viewHeight; } }

    /// <summary>True once at least one view frame has arrived.</summary>
    public bool HasFrame { get { lock (_sync) return _viewBuffer != IntPtr.Zero; } }

    /// <summary>
    /// Absorb one CEF paint. <paramref name="buffer"/> is only valid for the
    /// duration of the callback, so it is always copied.
    /// </summary>
    public void Absorb(CefPaintElementType type, CefRectangle[] dirtyRects,
                       IntPtr buffer, int width, int height)
    {
        if (buffer == IntPtr.Zero || width <= 0 || height <= 0)
            return;

        lock (_sync)
        {
            if (_disposed)
                return;

            if (type == CefPaintElementType.Popup)
            {
                EnsureCapacity(ref _popupBuffer, ref _popupWidth, ref _popupHeight, width, height);
                // Popups are small and repaint wholesale; a full copy is cheaper
                // than walking dirty rects.
                CopyWhole(buffer, _popupBuffer, width, height);
                return;
            }

            bool resized = width != _viewWidth || height != _viewHeight;
            EnsureCapacity(ref _viewBuffer, ref _viewWidth, ref _viewHeight, width, height);

            // On resize CEF sends a full-surface dirty rect, but be defensive:
            // a partial copy into a freshly allocated buffer would leave garbage.
            if (resized || dirtyRects is null || dirtyRects.Length == 0)
            {
                CopyWhole(buffer, _viewBuffer, width, height);
                return;
            }

            foreach (var r in dirtyRects)
                CopyRect(buffer, _viewBuffer, width, height, r);
        }
    }

    public void SetPopupVisible(bool visible)
    {
        lock (_sync)
        {
            _popupVisible = visible;
            if (!visible)
                _popupRect = default;
        }
    }

    public void SetPopupRect(CefRectangle rect)
    {
        lock (_sync)
            _popupRect = rect;
    }

    /// <summary>
    /// Compose view + popup into <paramref name="destination"/>, which must be a
    /// tightly packed BGRA buffer of exactly <see cref="Width"/> ×
    /// <see cref="Height"/>. Returns false if there is nothing to present.
    /// </summary>
    public unsafe bool Present(IntPtr destination, int destWidth, int destHeight)
    {
        if (destination == IntPtr.Zero)
            return false;

        lock (_sync)
        {
            if (_disposed || _viewBuffer == IntPtr.Zero)
                return false;
            if (destWidth != _viewWidth || destHeight != _viewHeight)
                return false; // caller must resize its bitmap first

            long bytes = (long)_viewWidth * _viewHeight * BytesPerPixel;
            Buffer.MemoryCopy((void*)_viewBuffer, (void*)destination, bytes, bytes);

            if (_popupVisible && _popupBuffer != IntPtr.Zero &&
                _popupRect.Width > 0 && _popupRect.Height > 0)
            {
                OverlayPopup(destination, destWidth, destHeight);
            }

            return true;
        }
    }

    /// <summary>Blend the popup layer over the composed frame, clipped to it.</summary>
    private unsafe void OverlayPopup(IntPtr destination, int destWidth, int destHeight)
    {
        int px = _popupRect.X;
        int py = _popupRect.Y;
        int pw = Math.Min(_popupWidth, _popupRect.Width);
        int ph = Math.Min(_popupHeight, _popupRect.Height);

        // Clip against the destination surface.
        int srcX = px < 0 ? -px : 0;
        int srcY = py < 0 ? -py : 0;
        int dstX = Math.Max(px, 0);
        int dstY = Math.Max(py, 0);
        int copyW = Math.Min(pw - srcX, destWidth - dstX);
        int copyH = Math.Min(ph - srcY, destHeight - dstY);
        if (copyW <= 0 || copyH <= 0)
            return;

        byte* src = (byte*)_popupBuffer;
        byte* dst = (byte*)destination;
        long srcStride = (long)_popupWidth * BytesPerPixel;
        long dstStride = (long)destWidth * BytesPerPixel;
        long rowBytes = (long)copyW * BytesPerPixel;

        for (int row = 0; row < copyH; row++)
        {
            Buffer.MemoryCopy(
                src + (srcY + row) * srcStride + (long)srcX * BytesPerPixel,
                dst + (dstY + row) * dstStride + (long)dstX * BytesPerPixel,
                rowBytes, rowBytes);
        }
    }

    private static unsafe void CopyWhole(IntPtr src, IntPtr dst, int width, int height)
    {
        long bytes = (long)width * height * BytesPerPixel;
        Buffer.MemoryCopy((void*)src, (void*)dst, bytes, bytes);
    }

    private static unsafe void CopyRect(IntPtr src, IntPtr dst, int width, int height, CefRectangle r)
    {
        int x = Math.Max(r.X, 0);
        int y = Math.Max(r.Y, 0);
        int w = Math.Min(r.Width, width - x);
        int h = Math.Min(r.Height, height - y);
        if (w <= 0 || h <= 0)
            return;

        byte* s = (byte*)src;
        byte* d = (byte*)dst;
        long stride = (long)width * BytesPerPixel;
        long rowBytes = (long)w * BytesPerPixel;
        long offset = (long)x * BytesPerPixel;

        for (int row = 0; row < h; row++)
        {
            long o = (y + row) * stride + offset;
            Buffer.MemoryCopy(s + o, d + o, rowBytes, rowBytes);
        }
    }

    private static void EnsureCapacity(ref IntPtr buffer, ref int curW, ref int curH, int w, int h)
    {
        if (buffer != IntPtr.Zero && curW == w && curH == h)
            return;

        if (buffer != IntPtr.Zero)
            Marshal.FreeHGlobal(buffer);

        buffer = Marshal.AllocHGlobal(w * h * BytesPerPixel);
        curW = w;
        curH = h;

        // Start transparent so a partially painted first frame doesn't flash noise.
        unsafe
        {
            new Span<byte>((void*)buffer, w * h * BytesPerPixel).Clear();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;

            if (_viewBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_viewBuffer);
                _viewBuffer = IntPtr.Zero;
            }
            if (_popupBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_popupBuffer);
                _popupBuffer = IntPtr.Zero;
            }
            _viewWidth = _viewHeight = _popupWidth = _popupHeight = 0;
        }
    }
}
