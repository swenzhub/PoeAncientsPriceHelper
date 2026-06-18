using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PoeAncientsPriceHelper;

// DivineValue / ExaltedValue are the PER-UNIT prices; Multiplier is the stack size read from
// the "Nx" marker. The overlay shows total (unit × multiplier) with the unit price in parentheses.
// Name is the normalized item name (used to confirm/lock a row across OCR passes).
// ExactMatch = the name matched a price key exactly (not via prefix/fuzzy) — high confidence,
// so it can lock on the first read instead of needing a second confirming read.

internal sealed record PriceRow(int CenterY, string OcrText, decimal DivineValue, decimal ExaltedValue, bool HasPrice, int Multiplier = 1, string Name = "", bool ExactMatch = false);

internal sealed class PriceOverlayForm : Form
{
    private IReadOnlyList<PriceRow> _rows = [];
    private bool _panelOpen;
    private bool _reading;  // panel detected, prices not yet resolved → show a "reading…" hint
    private bool _debug;   // F3 toggles the diagnostic boxes/region/"?" text; prices show regardless
    // Snapshot of the last rendered state — UpdateState is called ~10x/s by the scan loop, so we skip
    // the (relatively expensive) RenderLayered pass when nothing actually changed.
    private IReadOnlyList<PriceRow> _lastRenderedRows = [];
    private bool _lastPanelOpen;
    private bool _lastReading;
    private readonly IconCache _icons;
    private readonly Rectangle _regionRect;
    private readonly int _xOffset;
    private readonly Font _priceFont = new("Consolas", 20, FontStyle.Bold);
    private readonly Font _debugFont = new("Consolas", 18, FontStyle.Regular);
    private const int IconSize = 38;
    private const int RowHalfHeight = 25;

    public PriceOverlayForm(Rectangle screenBounds, Rectangle regionRect, int xOffset, IconCache icons)
    {
        _regionRect = regionRect;
        _xOffset = xOffset;
        _icons = icons;
        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Bounds = screenBounds;
        // Per-pixel alpha via UpdateLayeredWindow (see RenderLayered) — NOT color-key transparency,
        // so backdrops can be genuinely semi-transparent. Pixels are pushed manually; WM_PAINT is unused.
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_LAYERED = 0x00080000;
            const int WS_EX_TRANSPARENT = 0x00000020;
            const int WS_EX_NOACTIVATE = 0x08000000;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    public void UpdateState(IReadOnlyList<PriceRow> rows, bool panelOpen, bool reading)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => UpdateState(rows, panelOpen, reading)); return; }
        _rows = rows;
        _panelOpen = panelOpen;
        _reading = reading;

        // ApplyVisibility must run on every visibility transition (show/hide) even if the rows are
        // unchanged; RenderLayered only needs to run when something the user can see actually changed.
        bool visibilityChanged = (_panelOpen || _reading || _debug) != Visible;
        bool rowsChanged = !RowsEqual(_rows, _lastRenderedRows);
        bool stateChanged = _panelOpen != _lastPanelOpen || _reading != _lastReading;

        if (visibilityChanged || rowsChanged || stateChanged)
        {
            ApplyVisibility();
            if (Visible) RenderLayered();
            _lastRenderedRows = _rows.ToArray();  // snapshot to avoid aliasing the scan loop's buffer
            _lastPanelOpen = _panelOpen;
            _lastReading = _reading;
        }
    }

    // PriceRow is a record, so SequenceEqual gives structural (value) equality across all fields.
    private static bool RowsEqual(IReadOnlyList<PriceRow> a, IReadOnlyList<PriceRow> b)
        => a.SequenceEqual(b);

    // F3 toggles debug visuals (row boxes, region outline, OCR "?" text). Prices are unaffected.
    public void ToggleDebug()
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(ToggleDebug); return; }
        _debug = !_debug;
        _lastRenderedRows = [];   // invalidate cache so the next UpdateState forces a fresh render
        ApplyVisibility();
        if (Visible) RenderLayered();
    }

    private void ApplyVisibility()
    {
        // Visible when prices are ready, while reading (to show the hint), or in debug mode.
        bool shouldShow = _panelOpen || _reading || _debug;
        if (shouldShow && !Visible) { Show(); ForceTopmost(); }
        // Clear the rows as we hide so a later re-show can't briefly repaint the previous encounter's
        // prices before the scan loop pushes fresh state (#5).
        else if (!shouldShow && Visible) { _rows = []; Hide(); }
    }

    // Hide the window right now, off the hotkey thread — instant ESC/close response without
    // waiting for the (slower, OCR-bound) scan loop to come around. Debug mode keeps it visible.
    public void HideNow()
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(HideNow); return; }
        _panelOpen = false;
        _reading = false;
        _rows = [];   // drop stale prices immediately so debug-mode (still visible) can't repaint them
        // Reset the cached state so the next UpdateState forces a render instead of being skipped.
        _lastPanelOpen = false;
        _lastReading = false;
        _lastRenderedRows = [];
        ApplyVisibility();
        if (Visible) RenderLayered();
    }

    // WM_PAINT is unused — pixels come from RenderLayered/UpdateLayeredWindow. Suppress the default
    // background erase/paint so WinForms never flashes an opaque fill over the layered content.
    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e) { }

    // Composite the whole scene into a 32-bpp ARGB bitmap and blit it as a per-pixel-alpha layered
    // window. Called whenever state changes (instead of Invalidate). Cheap enough: updates are driven
    // by the scan loop / hotkeys, not a render clock.
    private void RenderLayered()
    {
        if (!IsHandleCreated || IsDisposed || !Visible) return;
        int w = Bounds.Width, h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit; // grayscale AA carries alpha cleanly
            PaintScene(g);
        }

        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
        IntPtr oldBitmap = SelectObject(memDc, hBitmap);
        try
        {
            var size = new SIZE { cx = w, cy = h };
            var src = new POINT { x = 0, y = 0 };
            var dst = new POINT { x = Bounds.Left, y = Bounds.Top };
            var blend = new BLENDFUNCTION
            {
                BlendOp = AC_SRC_OVER, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA,
            };
            UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            SelectObject(memDc, oldBitmap);
            DeleteObject(hBitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private void PaintScene(Graphics g)
    {
        // All geometry below is in absolute screen coords (region/price positions). The layered bitmap
        // is form-local, and the form may sit on a non-primary monitor (origin != 0,0), so shift the
        // whole scene by the form origin to map absolute coords into the bitmap (#3). On the primary
        // monitor at (0,0) this is a no-op.
        g.TranslateTransform(-Bounds.Left, -Bounds.Top);

        // Debug-only: outline of the calibrated region (orange=not detected, green=detected).
        if (_debug)
        {
            var borderColor = _panelOpen ? Color.LimeGreen : Color.Orange;
            using var borderPen = new Pen(borderColor, 2);
            g.DrawRectangle(borderPen, _regionRect);
        }

        if (!_panelOpen) return;

        int priceX = _regionRect.Right + _xOffset;

        // Box geometry matches the slice fed to OCR (left glyph column cut, right border trimmed).
        int ocrLeft = _regionRect.Left + (int)(_regionRect.Width * OcrScanner.IconColumnFraction);
        int ocrRight = _regionRect.Right - (int)(_regionRect.Width * OcrScanner.RightTrimFraction);

        // Identify the most valuable priced row (by total = unit × multiplier, in divine terms)
        // so it can be highlighted. Only meaningful when more than one item is priced.
        PriceRow? topRow = null;
        int pricedCount = 0;
        decimal topValue = -1m;
        foreach (var row in _rows)
        {
            if (!row.HasPrice) continue;
            pricedCount++;
            decimal value = row.DivineValue * Math.Max(1, row.Multiplier);
            if (value > topValue) { topValue = value; topRow = row; }
        }

        foreach (var row in _rows)
        {
            int screenY = _regionRect.Top + row.CenterY;

            // Debug layer: per-row boxes + the OCR text for rows that didn't resolve to a price.
            if (_debug)
            {
                var rowBox = new Rectangle(ocrLeft, screenY - RowHalfHeight, ocrRight - ocrLeft, RowHalfHeight * 2);
                if (row.HasPrice)
                {
                    using var greenPen = new Pen(Color.LimeGreen, 1);
                    g.DrawRectangle(greenPen, rowBox);
                }
                else
                {
                    using var yellowPen = new Pen(Color.Yellow, 1) { DashStyle = DashStyle.Dash };
                    g.DrawRectangle(yellowPen, rowBox);
                    using var grayBrush = new SolidBrush(Color.FromArgb(200, Color.Gray));
                    g.DrawString($"? {row.OcrText}", _debugFont, grayBrush, priceX, screenY - 7);
                }
            }

            // Always-on layer: the price (icon + number) for any priced row, boxes or not.
            if (row.HasPrice)
            {
                bool isTop = pricedCount > 1 && ReferenceEquals(row, topRow);
                DrawPrice(g, row, priceX, screenY, isTop);
            }
        }
    }

    private void DrawPrice(Graphics g, PriceRow row, int x, int screenY, bool highlightTop)
    {
        int iconY = screenY - IconSize / 2;
        int mult = Math.Max(1, row.Multiplier);
        // Currency choice is per-unit so single-item display is unchanged.
        bool useDivine = row.DivineValue >= 1.0m;
        decimal unit = useDivine ? row.DivineValue : row.ExaltedValue;
        decimal total = unit * mult;
        string fmt = useDivine ? "0.00" : "0.#";
        // Always format prices with a '.' decimal separator regardless of the machine's locale —
        // PoE prices are universally written with a dot, and it avoids "0,1"-style confusion on
        // comma-decimal locales (e.g. pt-BR).
        var inv = System.Globalization.CultureInfo.InvariantCulture;

        // Multiple items: show total, then per-each price in parentheses.
        string label = mult > 1
            ? $"{total.ToString(fmt, inv)} ({unit.ToString(fmt, inv)} each)"
            : total.ToString(fmt, inv);

        DrawBackdrop(g, x, screenY, IconSize + 2 + TextWidth(g, label));
        DrawIcon(g, useDivine ? _icons.Divine : _icons.Exalted, useDivine ? "d" : "ex", x, iconY);

        // Most valuable row → bright green; otherwise gold (divine) / white (exalted).
        var color = highlightTop ? Color.FromArgb(80, 255, 120) : (useDivine ? Color.Gold : Color.White);
        using var brush = new SolidBrush(color);
        // Vertically center the (now smaller) text against the row, not the icon top.
        int textY = screenY - _priceFont.Height / 2;
        g.DrawString(label, _priceFont, brush, x + IconSize + 2, textY);
    }

    private int TextWidth(Graphics g, string s) => (int)Math.Ceiling(g.MeasureString(s, _priceFont).Width);

    // A rounded, semi-transparent slate plate behind the icon + price so they read clearly over busy
    // art — the game shows faintly through it (see RenderLayered for the per-pixel-alpha window).
    private void DrawBackdrop(Graphics g, int x, int centerY, int contentWidth)
    {
        const int padX = 6, padY = 3, radius = 6;
        int h = Math.Max(IconSize, _priceFont.Height) + padY * 2;
        var rect = new Rectangle(x - padX, centerY - h / 2, contentWidth + padX * 2, h);
        var prev = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundedRect(rect, radius);
        // Premultiplied because the layered window expects premultiplied alpha; opaque text/icons
        // drawn on top are unaffected (premultiplied == straight at full alpha).
        using var bg = new SolidBrush(Premultiply(Color.FromArgb(150, 55, 55, 64)));
        g.FillPath(bg, path);
        g.SmoothingMode = prev;
    }

    private static Color Premultiply(Color c) =>
        Color.FromArgb(c.A, c.R * c.A / 255, c.G * c.A / 255, c.B * c.A / 255);

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private void DrawIcon(Graphics g, Bitmap? icon, string fallback, int x, int y)
    {
        if (icon != null && _icons.IsAvailable)
            g.DrawImage(icon, new Rectangle(x, y, IconSize, IconSize));
        else
        {
            using var brush = new SolidBrush(Color.White);
            g.DrawString(fallback, _priceFont, brush, x, y);
        }
    }

    protected override void OnShown(EventArgs e) { base.OnShown(e); ForceTopmost(); RenderLayered(); }

    public void ForceTopmost()
    {
        if (IsDisposed || !IsHandleCreated || !Visible) return;
        if (InvokeRequired) { BeginInvoke(ForceTopmost); return; }
        // SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE — no SWP_SHOWWINDOW (0x40) which would un-hide a hidden form
        SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0002 | 0x0001 | 0x0010);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _priceFont.Dispose(); _debugFont.Dispose(); }
        base.Dispose(disposing);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    // --- Per-pixel-alpha layered window plumbing ---
    private const int ULW_ALPHA = 0x02;
    private const byte AC_SRC_OVER = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] private struct BLENDFUNCTION
    {
        public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(
        IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc,
        ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
}

internal static class PriceOverlayManager
{
    private static PriceOverlayForm? _form;
    private static Thread? _thread;
    private static readonly object _lock = new();

    public static void EnsureVisible(Rectangle regionRect, int xOffset, IconCache icons)
    {
        lock (_lock)
        {
            if (_form is not null && !_form.IsDisposed)
            {
                var existing = _form;
                // The form can be disposed between the IsDisposed check and the Invoke (the UI thread
                // may close it at any time); swallow those races rather than crashing the caller.
                try { existing.Invoke(() => { if (!existing.IsDisposed && !existing.Visible) existing.Show(); }); }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
                return;
            }

            // Host the overlay on the monitor that contains the calibrated region (#3), not always the
            // primary. Sized to just that monitor, so the per-frame layered bitmap stays one-monitor
            // small (no perf regression) while prices land on the monitor PoE runs on.
            var screen = Screen.FromRectangle(regionRect).Bounds;
            using var ready = new ManualResetEventSlim(false);
            _thread = new Thread(() =>
            {
                var f = new PriceOverlayForm(screen, regionRect, xOffset, icons);
                f.Shown += (_, _) => ready.Set();
                _form = f;
                System.Windows.Forms.Application.Run(f);
                lock (_lock) _form = null;
            }) { IsBackground = true, Name = "PriceOverlay-STA" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            ready.Wait(TimeSpan.FromSeconds(2));
        }
    }

    // Capture the form reference under the lock, then release it before calling into WinForms.
    // Holding _lock across cross-thread UI dispatch can deadlock during teardown.
    private static void WithForm(Action<PriceOverlayForm> action)
    {
        PriceOverlayForm? f;
        lock (_lock) { f = _form; }
        if (f is null || f.IsDisposed) return;
        try { action(f); }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    public static void Hide() =>
        WithForm(f => f.Invoke(() => { if (!f.IsDisposed) f.Close(); }));

    public static void UpdateState(IReadOnlyList<PriceRow> rows, bool panelOpen, bool reading) =>
        WithForm(f => f.UpdateState(rows, panelOpen, reading));

    public static void ForceTopmost() => WithForm(f => f.ForceTopmost());

    public static void ToggleDebug() => WithForm(f => f.ToggleDebug());

    public static void HideNow() => WithForm(f => f.HideNow());
}
