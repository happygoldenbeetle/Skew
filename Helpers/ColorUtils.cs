using System;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Mori.Helpers;

public static class ColorUtils
{
    public static SolidColorBrush GetColorFromUrl(string urlString)
    {
        string host = GetHost(urlString) ?? "unknown";
        
        ulong hash = 5381;
        foreach (char c in host)
        {
            hash = ((hash << 5) + hash) + (ulong)c; /* hash * 33 + c */
        }

        double hue = (hash % 360);
        double saturation = 0.62;
        double brightness = 0.80;

        return new SolidColorBrush(HsbToRgb(hue, saturation, brightness));
    }

    private static string? GetHost(string urlString)
    {
        if (string.IsNullOrEmpty(urlString)) return null;
        if (Uri.TryCreate(urlString, UriKind.Absolute, out Uri? uri))
        {
            string host = uri.Host.ToLowerInvariant();
            if (host.StartsWith("www.")) host = host.Substring(4);
            return host;
        }
        return null;
    }

    public static Color HsbToRgb(double hue, double saturation, double brightness)
    {
        double r = 0, g = 0, b = 0;

        if (saturation == 0)
        {
            r = g = b = brightness;
        }
        else
        {
            double h = hue / 60;
            int i = (int)Math.Floor(h);
            double f = h - i;
            double p = brightness * (1 - saturation);
            double q = brightness * (1 - saturation * f);
            double t = brightness * (1 - saturation * (1 - f));

            switch (i)
            {
                case 0: r = brightness; g = t; b = p; break;
                case 1: r = q; g = brightness; b = p; break;
                case 2: r = p; g = brightness; b = t; break;
                case 3: r = p; g = q; b = brightness; break;
                case 4: r = t; g = p; b = brightness; break;
                default: r = brightness; g = p; b = q; break;
            }
        }

        return Color.FromArgb(
            255,
            (byte)Math.Round(r * 255),
            (byte)Math.Round(g * 255),
            (byte)Math.Round(b * 255)
        );
    }
}
