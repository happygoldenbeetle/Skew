using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace Mori.Cef;

/// <summary>
/// A bare native child <c>HWND</c> that CEF parents its browser window into via
/// <c>CefWindowInfo.SetAsChild</c>. This is the Windows analog of the NSView that
/// mac's MoriBrowserView hands to CEF.
///
/// <para>
/// WinUI 3 does not expose raw child HWNDs for its elements, so we create our own
/// child window under the top-level app HWND and keep it positioned/sized over the
/// XAML host panel. This is the classic "airspace" hosting approach; the panel in
/// XAML reserves layout space and this window is moved to match its on-screen
/// rectangle. Rounded-corner/shadow framing remains on the parent WinUI Border.
/// </para>
/// </summary>
internal sealed class HwndHostWindow : IDisposable
{
    private const string ClassName = "MoriCefHostWindow";
    private static readonly WndProcDelegate s_wndProc = StaticWndProc;
    private static ushort s_classAtom;

    private readonly nint _parent;
    private readonly FrameworkElement _hostElement;
    private readonly Controls.MoriBrowserView _owner;
    private nint _hwnd;

    public nint Handle => _hwnd;

    private HwndHostWindow(nint parent, FrameworkElement hostElement, Controls.MoriBrowserView owner)
    {
        _parent = parent;
        _hostElement = hostElement;
        _owner = owner;
    }

    public static HwndHostWindow Create(nint parent, FrameworkElement hostElement, Controls.MoriBrowserView owner)
    {
        var w = new HwndHostWindow(parent, hostElement, owner);
        w.CreateWindow();
        w.UpdateBounds();
        return w;
    }

    private void CreateWindow()
    {
        EnsureClassRegistered();

        _hwnd = CreateWindowExW(
            0,
            ClassName,
            string.Empty,
            WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN,
            0, 0, 1, 1,
            _parent,
            nint.Zero,
            GetModuleHandleW(null),
            nint.Zero);

        if (_hwnd == nint.Zero)
            throw new InvalidOperationException(
                $"CreateWindowExW failed (0x{Marshal.GetLastWin32Error():X}).");
    }

    /// <summary>The child window bounds in device pixels (CEF wants pixels).</summary>
    public RectInt32 PixelBounds { get; private set; }

    /// <summary>
    /// Reposition the child window to cover the host element's on-screen rect.
    /// Called on size/layout changes (mac _syncBrowserFrame).
    /// </summary>
    public void UpdateBounds()
    {
        if (_hwnd == nint.Zero)
            return;

        try
        {
            if (_hostElement.XamlRoot == null)
                return;

            double rasterScale = _hostElement.XamlRoot.RasterizationScale;

            // Position of the host element relative to the window content root.
            var transform = _hostElement.TransformToVisual(null);
            var origin = transform.TransformPoint(new Windows.Foundation.Point(0, 0));

            int x = (int)Math.Round(origin.X * rasterScale);
            int y = (int)Math.Round(origin.Y * rasterScale);
            int w = Math.Max(1, (int)Math.Round(_hostElement.ActualWidth * rasterScale));
            int h = Math.Max(1, (int)Math.Round(_hostElement.ActualHeight * rasterScale));

            PixelBounds = new RectInt32(0, 0, w, h);

            SetWindowPos(_hwnd, nint.Zero, x, y, w, h,
                SWP_NOZORDER | SWP_NOACTIVATE);
        }
        catch (Exception)
        {
            // The element is likely disconnected from the visual tree.
            // Calling TransformToVisual throws an ArgumentException in this case.
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(nint hwnd);

    public void SetVisible(bool visible)
    {
        System.IO.File.AppendAllText("visibility.log", $"SetVisible({visible}) called for handle {_hwnd}\n");
        if (_hwnd != nint.Zero)
        {
            ShowWindow(_hwnd, visible ? SW_SHOWNOACTIVATE : SW_HIDE);
            if (visible)
            {
                BringWindowToTop(_hwnd);
            }
        }
    }

    public void ResizeBrowserWindow(nint browserHwnd)
    {
        if (browserHwnd != nint.Zero && _hwnd != nint.Zero)
        {
            const uint SWP_SHOWWINDOW = 0x0040;
            SetWindowPos(browserHwnd, nint.Zero, 0, 0, PixelBounds.Width, PixelBounds.Height,
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOMOVE | SWP_SHOWWINDOW);
        }
    }

    public void Dispose()
    {
        if (_hwnd != nint.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = nint.Zero;
        }
    }

    private static void EnsureClassRegistered()
    {
        if (s_classAtom != 0)
            return;

        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_wndProc),
            hInstance = GetModuleHandleW(null),
            lpszClassName = ClassName,
            hbrBackground = nint.Zero,
        };
        s_classAtom = RegisterClassExW(ref wc);
        if (s_classAtom == 0)
            throw new InvalidOperationException(
                $"RegisterClassExW failed (0x{Marshal.GetLastWin32Error():X}).");
    }

    private static nint StaticWndProc(nint hwnd, uint msg, nint wParam, nint lParam)
        => DefWindowProcW(hwnd, msg, wParam, lParam);

    // ── Win32 interop ──────────────────────────────────────────────────────

    private delegate nint WndProcDelegate(nint hwnd, uint msg, nint wParam, nint lParam);

    private const int WS_CHILD = unchecked((int)0x40000000);
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_CLIPSIBLINGS = 0x04000000;
    private const int WS_CLIPCHILDREN = 0x02000000;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOMOVE = 0x0002;
    private const int SW_HIDE = 0;
    private const int SW_SHOWNOACTIVATE = 4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public nint hIconSm;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(
        uint exStyle, string className, string windowName, int style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hwnd, int cmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? moduleName);
}
