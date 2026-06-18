using System.Drawing;

namespace PoeAncientsPriceHelper;

// Abstraction over screen capture so the scan loop is decoupled from the capture mechanism.
// Implementations: GdiScreenCaptureBackend (CopyFromScreen), WgcScreenCaptureBackend (GPU WGC).
// The backend captures a screen region and returns a Bitmap for OCR / brightness detection.
internal interface IScreenCaptureBackend : IDisposable
{
    Bitmap CaptureRegion(Rectangle region);
}
