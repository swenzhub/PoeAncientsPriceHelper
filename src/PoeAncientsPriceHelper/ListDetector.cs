using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace PoeAncientsPriceHelper;

internal sealed class ListDetector
{
    private const int Cols = 12;
    // Sample the RIGHT portion only — the left ~25% icon column has dark gaps that pull the
    // average down on an open panel. Skipping it raises panel readings well above the dark
    // game world, widening the gap against bright-but-patchy outdoor backgrounds.
    private const double LeftFraction = 0.40;
    private const double RightFraction = 0.98;
    private static readonly double[] RowFractions = [0.20, 0.35, 0.50, 0.65, 0.80];

    // Averages a grid of pixels across the right portion of the region at several heights.
    // ScanEngine applies its own hysteresis thresholds to the sampled brightness.
    public Color SampleAverage(Bitmap regionBitmap)
    {
        int x0 = (int)(regionBitmap.Width * LeftFraction);
        int x1 = (int)(regionBitmap.Width * RightFraction);
        int span = Math.Max(1, x1 - x0);

        // Pre-compute sample coordinates
        var samplePoints = new List<(int x, int y)>();
        foreach (var yf in RowFractions)
        {
            int cy = Math.Clamp((int)(regionBitmap.Height * yf), 0, regionBitmap.Height - 1);
            for (int i = 0; i < Cols; i++)
            {
                int cx = Math.Clamp(x0 + (int)((i + 0.5) * span / Cols), 0, regionBitmap.Width - 1);
                samplePoints.Add((cx, cy));
            }
        }

        long r = 0, g = 0, b = 0;
        int bpp = System.Drawing.Image.GetPixelFormatSize(regionBitmap.PixelFormat) / 8;
        var data = regionBitmap.LockBits(
            new Rectangle(0, 0, regionBitmap.Width, regionBitmap.Height),
            ImageLockMode.ReadOnly, regionBitmap.PixelFormat);
        try
        {
            int stride = data.Stride;
            var scan0 = data.Scan0;
            foreach (var (cx, cy) in samplePoints)
            {
                int offset = cy * stride + cx * bpp;
                // Format24bppRgb is BGR; Format32bppRgb/Argb is BGRA
                byte blue = Marshal.ReadByte(scan0, offset);
                byte green = Marshal.ReadByte(scan0, offset + 1);
                byte red = Marshal.ReadByte(scan0, offset + 2);
                r += red; g += green; b += blue;
            }
        }
        finally { regionBitmap.UnlockBits(data); }

        int count = samplePoints.Count;
        return Color.FromArgb((int)(r / count), (int)(g / count), (int)(b / count));
    }
}
