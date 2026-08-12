using System;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Skew.Helpers
{
    public static class FilmGrainGenerator
    {
        public static WriteableBitmap GenerateNoise(int width, int height)
        {
            var bitmap = new WriteableBitmap(width, height);
            var random = new Random();
            
            // 4 bytes per pixel (BGRA)
            byte[] pixels = new byte[width * height * 4];
            
            for (int i = 0; i < pixels.Length; i += 4)
            {
                // Generate a random grayscale value
                byte val = (byte)random.Next(0, 256);
                pixels[i] = val;     // B
                pixels[i + 1] = val; // G
                pixels[i + 2] = val; // R
                pixels[i + 3] = 255; // A
            }

            // Copy to the WriteableBitmap's pixel buffer
            pixels.CopyTo(bitmap.PixelBuffer);
            
            return bitmap;
        }
    }
}
