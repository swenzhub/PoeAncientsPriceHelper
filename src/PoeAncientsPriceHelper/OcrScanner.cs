using System.Drawing;
using System.Drawing.Imaging;
using Tesseract;

namespace PoeAncientsPriceHelper;

internal sealed record OcrRow(string NormalizedName, string RawText, int CenterY, int Multiplier = 1);

internal sealed class OcrScanner : IDisposable
{
    // Two engines because each is configured for a different page-segmentation mode (SingleColumn
    // vs SparseText). They run SEQUENTIALLY now — SingleColumn first, SparseText only as a fallback
    // when SingleColumn found few rows — but Tesseract engines are single-threaded internally, so
    // keeping separate instances means each keeps its own mode set without reconfiguring per pass.
    private readonly TesseractEngine _engineCol;
    private readonly TesseractEngine _engineSparse;
    private readonly Action<string>? _log;
    private readonly bool _debug;
    private const float MinConfidence = 10f;
    private const int UpscaleFactor = 3;
    private const int MinNameLength = 4;
    // A real row must contain a word at least this long. 4 (not 5) so two-short-word names
    // like "Void Flux" survive; OCR fragments are still mostly 1–3 char tokens.
    private const int MinWordLength = 4;

    // Pre-compiled regexes for StripLeadingNoise / ExtractMultiplier — these run on every OCR'd
    // line (~every 100ms while a panel is open), so avoiding the per-call recompile is a meaningful
    // saving on the hot path. (NormalizeName's regexes live in NameNormalizer.)
    private static readonly Regex MultiplierPattern = new(@"(?<![a-z0-9])(\d{1,3})\s*x(?![a-z0-9])", RegexOptions.Compiled);
    private static readonly Regex LeadingNoise = new(@"^(?:\S{1,2}\s+|\S*\d\S*\s+)+", RegexOptions.Compiled);
    private static readonly Regex QuantityMarker = new(@"(?<!\w)\d+\s*x\s+", RegexOptions.Compiled);
    private static readonly Regex LeadingNonAlpha = new(@"^[^a-z]+", RegexOptions.Compiled);

    // debug gates the diagnostic debug_ocr.png dump (see Scan) and CLI OCR-test raw-line logging.
    // App.DebugMode additionally enables raw-line logging for the live overlay when toggled at runtime.
    public OcrScanner(string tessdataDir, Action<string>? log = null, bool debug = false)
    {
        _engineCol = new TesseractEngine(tessdataDir, "eng", EngineMode.Default);
        _engineSparse = new TesseractEngine(tessdataDir, "eng", EngineMode.Default);
        _log = log;
        _debug = debug;
    }

    // Each row starts with ~3 cost-rune glyphs on the left, then "Nx ItemName". Cropping the
    // left IconColumnFraction removes the glyphs (which produce leading OCR garbage) while
    // keeping the quantity marker and the name. RightTrimFraction shaves the panel's right
    // border, which otherwise tacks stray characters onto the last word.
    // (internal so the overlay can draw a box matching exactly what is OCR'd.)
    internal const double IconColumnFraction = 0.30;
    internal const double RightTrimFraction = 0.02;

    public IReadOnlyList<OcrRow> Scan(Bitmap regionBitmap)
    {
        int leftCut = Math.Max(1, (int)(regionBitmap.Width * IconColumnFraction));
        int rightCut = (int)(regionBitmap.Width * RightTrimFraction);
        int cropW = Math.Max(1, regionBitmap.Width - leftCut - rightCut);
        using var cropped = CropBitmap(regionBitmap, leftCut, 0, cropW, regionBitmap.Height);
        using var inverted = Preprocess(cropped);
        using var upscaled = Upscale(inverted, UpscaleFactor);
        byte[] png = ToPng(upscaled);
        int height = regionBitmap.Height;

        // Run SingleColumn first; only fall back to SparseText if it found few rows. SingleColumn
        // reads ordinary lists cleanly; SparseText rescues panels whose strong beveled row dividers
        // make the other modes see only the top line. On a normal panel SingleColumn alone is
        // enough, so we skip the second pass (and its cost) most of the time — running the passes
        // sequentially is fine because the second only runs when the first was poor.
        var colRows = RunPass(_engineCol, png, PageSegMode.SingleColumn, height);
        IReadOnlyList<OcrRow> rows;
        if (colRows.Count >= 3)
        {
            rows = colRows;
        }
        else
        {
            var sparseRows = RunPass(_engineSparse, png, PageSegMode.SparseText, height);
            rows = MergeByPosition(colRows, sparseRows);
        }

        // When OCR catches few rows, dump the exact image fed to Tesseract for inspection. Debug-only:
        // for end users this would be needless disk churn (~every 100ms while a panel mis-detects).
        if (_debug && rows.Count <= 2)
        {
            try { upscaled.Save(Path.Combine(AppContext.BaseDirectory, "debug_ocr.png"), System.Drawing.Imaging.ImageFormat.Png); }
            catch { /* best-effort diagnostic */ }
        }
        return rows;
    }

    private IReadOnlyList<OcrRow> RunPass(TesseractEngine engine, byte[] png, PageSegMode mode, int regionHeight)
    {
        using var pix = Pix.LoadFromMemory(png);
        using var page = engine.Process(pix, mode);
        return ExtractRows(page, regionHeight, UpscaleFactor);
    }

    private static IReadOnlyList<OcrRow> MergeByPosition(IReadOnlyList<OcrRow> a, IReadOnlyList<OcrRow> b)
    {
        const int Tol = 25;   // px: reads within this vertical distance are the same row
        static int Letters(string s) { int c = 0; foreach (var ch in s) if (char.IsLetter(ch)) c++; return c; }

        var result = new List<OcrRow>(a);
        foreach (var rb in b)
        {
            int idx = -1;
            for (int i = 0; i < result.Count; i++)
                if (Math.Abs(result[i].CenterY - rb.CenterY) <= Tol) { idx = i; break; }
            if (idx < 0) result.Add(rb);
            else if (Letters(rb.NormalizedName) > Letters(result[idx].NormalizedName)) result[idx] = rb;
        }
        result.Sort((x, y) => x.CenterY.CompareTo(y.CenterY));
        return result;
    }

    private static Bitmap CropBitmap(Bitmap src, int x, int y, int w, int h)
    {
        var dst = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(dst);
        g.DrawImage(src, new Rectangle(0, 0, w, h), new Rectangle(x, y, w, h), GraphicsUnit.Pixel);
        return dst;
    }

    private IReadOnlyList<OcrRow> ExtractRows(Page page, int bitmapHeight, int scale = 1)
    {
        var rows = new List<OcrRow>();
        List<string>? diag = ShouldLogOcrDiagnostics ? [] : null;
        using var iter = page.GetIterator();
        iter.Begin();
        do
        {
            if (!iter.TryGetBoundingBox(PageIteratorLevel.TextLine, out var box)) continue;
            var text = iter.GetText(PageIteratorLevel.TextLine);
            float conf = iter.GetConfidence(PageIteratorLevel.TextLine);
            // Bounding box coords are in upscaled space — divide back to original coords
            int centerY = Math.Clamp((box.Y1 + (box.Y2 - box.Y1) / 2) / scale, 0, bitmapHeight - 1);

            string? reject = null;
            string normalized = "";
            int multiplier = 1;
            if (string.IsNullOrWhiteSpace(text)) reject = "empty";
            else if (conf < MinConfidence) reject = "lowconf";
            else
            {
                var normalizedRaw = NameNormalizer.Normalize(text);
                multiplier = ExtractMultiplier(normalizedRaw);
                normalized = StripLeadingNoise(normalizedRaw);
                if (normalized.Length < MinNameLength) reject = "short";
                else if (!HasLongWord(normalized, MinWordLength)) reject = "noword";
            }

            if (reject is null)
                rows.Add(new OcrRow(normalized, text.Trim(), centerY, multiplier));
            diag?.Add($"y={centerY} conf={conf:0} '{(text ?? "").Trim()}'{(reject is null ? "" : $" REJ:{reject}")}");
        }
        while (iter.Next(PageIteratorLevel.TextLine));

        // Diagnostic: when few rows survive, show every line Tesseract actually produced so we
        // can tell "Tesseract only saw 1 line" from "saw 5 but the filters dropped 4".
        if (rows.Count <= 2 && diag is { Count: > 0 })
            _log?.Invoke($"OCR raw {diag.Count} lines → " + string.Join(" | ", diag));

        return rows;
    }

    private static Bitmap Upscale(Bitmap src, int factor)
    {
        var dst = new Bitmap(src.Width * factor, src.Height * factor, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.DrawImage(src, 0, 0, dst.Width, dst.Height);
        return dst;
    }

    // The list shows a stack quantity as "Nx" before the item name ("1x", "2x", "14x").
    // Capture it so the price can be multiplied by the stack size. Read from the raw
    // normalized string BEFORE StripLeadingNoise removes the marker. Returns 1 when absent.
    internal static int ExtractMultiplier(string normalized)
    {
        var m = MultiplierPattern.Match(normalized);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var n) && n >= 1)
            return Math.Min(n, 999);
        return 1;
    }

    // Strip leading noise: short/numeric tokens ("e", "l8"), then anything before the first
    // quantity marker ("1x", "11x"), then remaining leading non-alpha chars.
    // e.g. "krogin 1x ancient rune of decay"  → "ancient rune of decay"
    // e.g. "e l8 n 1x the greatwolf"          → "the greatwolf"
    internal static string StripLeadingNoise(string normalized)
    {
        var s = LeadingNoise.Replace(normalized, "");
        // If a quantity marker still exists, drop everything before (and including) it
        var qm = QuantityMarker.Match(s);
        if (qm.Success) s = s.Substring(qm.Index + qm.Length);
        s = LeadingNonAlpha.Replace(s, "");
        return s.Trim();
    }

    private static bool HasLongWord(string normalized, int minLen)
    {
        int run = 0;
        foreach (char c in normalized)
        {
            if (char.IsLetter(c)) { if (++run >= minLen) return true; }
            else run = 0;
        }
        return false;
    }

    // Invert: PoE list panel has light text on dark background.
    // Tesseract works better with dark-on-light.
    private static Bitmap Preprocess(Bitmap src)
    {
        var dst = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(dst);
        g.DrawImage(src, 0, 0);
        InvertBitmap(dst);
        return dst;
    }

    private static void InvertBitmap(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
            ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
        try
        {
            int len = data.Stride * bmp.Height;
            var buf = new byte[len];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buf, 0, len);
            for (int i = 0; i < buf.Length; i++) buf[i] = (byte)(255 - buf[i]);
            System.Runtime.InteropServices.Marshal.Copy(buf, 0, data.Scan0, len);
        }
        finally { bmp.UnlockBits(data); }
    }

    private static byte[] ToPng(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        return ms.ToArray();
    }

    private bool ShouldLogOcrDiagnostics => _log is not null && (_debug || App.DebugMode);

    public void Dispose() { _engineCol.Dispose(); _engineSparse.Dispose(); }
}
