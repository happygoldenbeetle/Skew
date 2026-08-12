using Windows.UI;

namespace Skew.Theme;

/// <summary>
/// Color-space helpers ported from the Mac app's OKLCH.swift.
/// Converts oklch(L, C, h) authored in globals.css to sRGB with
/// full fidelity, avoiding eyeballed hex approximations.
/// </summary>
public static class ColorMath
{
    /// <summary>
    /// Convert oklch(L, C, h) to sRGB components in 0…1.
    /// L in 0…1, C chroma, h in degrees.
    /// Mirrors the CSS Color 4 reference: OKLCH → OKLab → linear-sRGB → gamma sRGB.
    /// </summary>
    public static (double R, double G, double B) OklchToSrgb(double L, double C, double h)
    {
        double hr = h * Math.PI / 180.0;
        double a = C * Math.Cos(hr);
        double b = C * Math.Sin(hr);

        // OKLab → LMS (nonlinear)
        double l_ = L + 0.3963377774 * a + 0.2158037573 * b;
        double m_ = L - 0.1055613458 * a - 0.0638541728 * b;
        double s_ = L - 0.0894841775 * a - 1.2914855480 * b;

        double l = l_ * l_ * l_;
        double m = m_ * m_ * m_;
        double s = s_ * s_ * s_;

        // LMS → linear sRGB
        double rLin = 4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s;
        double gLin = -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s;
        double bLin = -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s;

        return (GammaEncode(rLin), GammaEncode(gLin), GammaEncode(bLin));
    }

    /// <summary>
    /// Linear sRGB → gamma-encoded sRGB, clamped to display range.
    /// </summary>
    private static double GammaEncode(double c)
    {
        double clamped = Math.Clamp(c, 0.0, 1.0);
        return clamped <= 0.0031308
            ? 12.92 * clamped
            : 1.055 * Math.Pow(clamped, 1.0 / 2.4) - 0.055;
    }
}

/// <summary>
/// A theme color token authored as hex or OKLCH — the two shapes used
/// across globals.css. Port of TokenColor from the Mac app.
/// </summary>
public readonly struct TokenColor
{
    public double R { get; }
    public double G { get; }
    public double B { get; }
    public double A { get; }

    public TokenColor(double r, double g, double b, double a = 1.0)
    {
        R = r; G = g; B = b; A = a;
    }

    /// <summary>
    /// Create from hex string: #rgb, #rrggbb, or #rrggbbaa.
    /// </summary>
    public static TokenColor FromHex(string hex, double alpha = 1.0)
    {
        var s = hex.Trim().TrimStart('#');
        if (s.Length == 3)
            s = $"{s[0]}{s[0]}{s[1]}{s[1]}{s[2]}{s[2]}";

        ulong value = Convert.ToUInt64(s, 16);
        if (s.Length == 8)
        {
            return new TokenColor(
                ((value >> 24) & 0xFF) / 255.0,
                ((value >> 16) & 0xFF) / 255.0,
                ((value >> 8) & 0xFF) / 255.0,
                (value & 0xFF) / 255.0
            );
        }
        return new TokenColor(
            ((value >> 16) & 0xFF) / 255.0,
            ((value >> 8) & 0xFF) / 255.0,
            (value & 0xFF) / 255.0,
            alpha
        );
    }

    /// <summary>
    /// Create from oklch(L, C, h) with optional alpha.
    /// </summary>
    public static TokenColor FromOklch(double L, double C, double h, double alpha = 1.0)
    {
        var (r, g, b) = ColorMath.OklchToSrgb(L, C, h);
        return new TokenColor(r, g, b, alpha);
    }

    /// <summary>
    /// Convert to WinUI Color.
    /// </summary>
    public Color ToColor()
    {
        return Color.FromArgb(
            (byte)(A * 255),
            (byte)(R * 255),
            (byte)(G * 255),
            (byte)(B * 255)
        );
    }

    /// <summary>
    /// Convert to WinUI SolidColorBrush.
    /// </summary>
    public Microsoft.UI.Xaml.Media.SolidColorBrush ToBrush()
    {
        return new Microsoft.UI.Xaml.Media.SolidColorBrush(ToColor());
    }

    /// <summary>
    /// Return a copy with a different alpha.
    /// </summary>
    public TokenColor WithOpacity(double opacity)
        => new(R, G, B, opacity);
}
