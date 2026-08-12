using System;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Skew.Helpers
{
    public static class FaviconKit
    {
        public static ImageSource? Resolve(string? faviconUrl, string? pageUrl)
        {
            if (string.IsNullOrWhiteSpace(faviconUrl)) return null;

            try
            {
                // If the favicon URL is an SVG, load it natively with SvgImageSource!
                if (faviconUrl.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                {
                    return new SvgImageSource(new Uri(faviconUrl));
                }

                // Fallback to BitmapImage using the provided favicon URL
                var bmp = new BitmapImage(new Uri(faviconUrl));
                bmp.DecodePixelType = DecodePixelType.Logical;
                bmp.DecodePixelWidth = 32;
                bmp.DecodePixelHeight = 32;
                return bmp;
            }
            catch
            {
                return null;
            }
        }
    }
}
