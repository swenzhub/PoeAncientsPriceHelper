using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace PoeAncientsPriceHelper;

// Full-screen drag-to-select overlay. User draws a rectangle over the list panel.
// Returns the selected RegionRect and the reference pixel color sampled at its centre.
internal sealed class CalibrationOverlay : Form
{
    public Rectangle RegionRectResult { get; private set; }

    private Point? _dragStart;
    private Rectangle _currentDrag;
    private Rectangle _confirmedRect;
    private readonly Bitmap _screenSnapshot;

    public CalibrationOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        Opacity = 0.4;
        DoubleBuffered = true;
        KeyPreview = true;
        Cursor = Cursors.Cross;
        Text = "PoeAncientsPriceHelper - Calibration";

        // Span the whole virtual desktop (all monitors) so the region can be drawn on any of them, not
        // just the primary (#3). On multi-monitor setups VirtualScreen.Location can be negative (a
        // monitor left/above the primary); mouse points are form-client coords, so the selected rect is
        // offset by Bounds.Location back into absolute screen coords in OnMouseUp. WindowState.Maximized
        // would only fill one monitor, so it's gone — Bounds drives the size instead.
        var bounds = SystemInformation.VirtualScreen;
        Bounds = bounds;

        _screenSnapshot = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(_screenSnapshot);
        g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _screenSnapshot.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        { _dragStart = e.Location; _currentDrag = Rectangle.Empty; }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragStart is { } s)
        {
            _currentDrag = Rectangle.FromLTRB(
                Math.Min(s.X, e.X), Math.Min(s.Y, e.Y),
                Math.Max(s.X, e.X), Math.Max(s.Y, e.Y));
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_dragStart is null || _currentDrag.Width < 3 || _currentDrag.Height < 3)
        { _dragStart = null; _currentDrag = Rectangle.Empty; Invalidate(); return; }
        _dragStart = null;
        _confirmedRect = _currentDrag;
        Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); return; }
        if ((e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) && _confirmedRect.Width > 0)
        {
            // _confirmedRect is form-client coords; shift by the form origin (= virtual-screen origin)
            // to get absolute screen coords, which is what ScreenCapture/overlay positioning expect.
            RegionRectResult = _confirmedRect with { X = _confirmedRect.X + Bounds.X, Y = _confirmedRect.Y + Bounds.Y };
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        using var titleFont = new Font("Segoe UI", 18, FontStyle.Bold);
        using var subFont = new Font("Segoe UI", 12, FontStyle.Regular);
        using var fg = new SolidBrush(Color.White);
        g.DrawString("Drag a box around the item list panel, then press ENTER to confirm. ESC to cancel.",
            titleFont, fg, 30, 30);
        if (_currentDrag.Width > 0)
        {
            using var pen = new Pen(Color.OrangeRed, 2) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
            g.DrawRectangle(pen, _currentDrag);
        }
        if (_confirmedRect.Width > 0)
        {
            using var pen2 = new Pen(Color.LimeGreen, 3);
            g.DrawRectangle(pen2, _confirmedRect);
            g.DrawString("Press ENTER to confirm, drag to redo", subFont, fg,
                _confirmedRect.Left, _confirmedRect.Bottom + 6);
        }
    }

    public static Rectangle? RunOnStaThread()
    {
        Rectangle? result = null;
        var thread = new Thread(() =>
        {
            System.Windows.Forms.Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            using var form = new CalibrationOverlay();
            if (form.ShowDialog() == DialogResult.OK)
                result = form.RegionRectResult;
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }
}
