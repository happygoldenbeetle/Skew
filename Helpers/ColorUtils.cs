using System;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Skew.Helpers;

public static class ColorUtils
{
    private static readonly System.Collections.Generic.Dictionary<string, Color> KnownColors = new()
    {
        { "youtube.com", Color.FromArgb(255, 255, 0, 0) },
        { "discord.com", Color.FromArgb(255, 88, 101, 242) },
        { "github.com", Color.FromArgb(255, 240, 246, 252) },
        { "news.ycombinator.com", Color.FromArgb(255, 255, 102, 0) },
        { "twitter.com", Color.FromArgb(255, 29, 155, 240) },
        { "x.com", Color.FromArgb(255, 255, 255, 255) },
        { "google.com", Color.FromArgb(255, 66, 133, 244) }
    };

    public static SolidColorBrush GetColorFromUrl(string urlString)
    {
        string host = GetHost(urlString) ?? "unknown";
        
        if (KnownColors.TryGetValue(host, out Color knownColor))
        {
            return new SolidColorBrush(knownColor);
        }
        
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
