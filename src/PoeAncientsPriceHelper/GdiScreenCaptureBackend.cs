using System.Drawing;
using System.Drawing.Imaging;

namespace PoeAncientsPriceHelper;

// GDI-based screen capture via Graphics.CopyFromScreen (BitBlt under the hood).
// Simple, universally compatible, but CPU-side and allocates a new Bitmap per call.
// Used as the default/fallback when WGC is unavailable.
internal sealed class GdiScreenCaptureBackend : IScreenCaptureBackend
{
    public Bitmap CaptureRegion(Rectangle r)
    {
        var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(r.X, r.Y, 0, 0, r.Size, CopyPixelOperation.SourceCopy);
        return bmp;
    }

    public void Dispose() { }
}
