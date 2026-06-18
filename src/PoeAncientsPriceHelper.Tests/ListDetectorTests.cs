using System.Drawing;
using PoeAncientsPriceHelper;

namespace PoeAncientsPriceHelper.Tests;

public class ListDetectorTests
{
    private static Bitmap SolidBitmap(int w, int h, Color c)
    {
        var bmp = new Bitmap(w, h);
        using var g = Graphics.FromImage(bmp);
        g.Clear(c);
        return bmp;
    }

    [Fact]
    public void SampleAverage_ReturnsBrightStripAverage()
    {
        var detector = new ListDetector();
        using var bmp = SolidBitmap(100, 100, Color.FromArgb(187, 179, 162)); // #BBB3A2 — alloy panel
        var avg = detector.SampleAverage(bmp);
        Assert.Equal(187, avg.R);
        Assert.Equal(179, avg.G);
        Assert.Equal(162, avg.B);
    }

    [Fact]
    public void SampleAverage_ReturnsMediumBrightStripAverage()
    {
        var detector = new ListDetector();
        using var bmp = SolidBitmap(100, 100, Color.FromArgb(116, 103, 84)); // #746754 — rune panel, brightness 101
        var avg = detector.SampleAverage(bmp);
        Assert.Equal(116, avg.R);
        Assert.Equal(103, avg.G);
        Assert.Equal(84, avg.B);
    }

    [Fact]
    public void SampleAverage_ReturnsDarkStripAverage()
    {
        var detector = new ListDetector();
        using var bmp = SolidBitmap(100, 100, Color.FromArgb(6, 6, 6)); // game world terrain
        var avg = detector.SampleAverage(bmp);
        Assert.Equal(6, avg.R);
        Assert.Equal(6, avg.G);
        Assert.Equal(6, avg.B);
    }
}
