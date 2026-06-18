using System.Drawing;
using System.Net.Http;

namespace PoeAncientsPriceHelper;

internal sealed class IconCache : IDisposable
{
    private const string ExaltedUrl =
        "https://web.poecdn.com/gen/image/WzI1LDE0LHsiZiI6IjJESXRlbXMvQ3VycmVuY3kvQ3VycmVuY3lBZGRNb2RUb1JhcmUiLCJzY2FsZSI6MSwicmVhbG0iOiJwb2UyIn1d/ad7c366789/CurrencyAddModToRare.png";
    private const string DivineUrl =
        "https://web.poecdn.com/gen/image/WzI1LDE0LHsiZiI6IjJESXRlbXMvQ3VycmVuY3kvQ3VycmVuY3lNb2RWYWx1ZXMiLCJzY2FsZSI6MSwicmVhbG0iOiJwb2UyIn1d/2986e220b3/CurrencyModValues.png";

    private readonly HttpClient _http;
    private readonly string _baseDir;

    public bool IsAvailable { get; private set; }
    public Bitmap? Divine { get; private set; }
    public Bitmap? Exalted { get; private set; }

    public IconCache(HttpClient http, string? baseDir = null)
    {
        _http = http;
        _baseDir = baseDir ?? AppContext.BaseDirectory;
    }

    public async Task LoadAsync()
    {
        try
        {
            var divPath = Path.Combine(_baseDir, "divine.png");
            var exPath = Path.Combine(_baseDir, "exalted.png");

            if (!File.Exists(divPath))
                await DownloadAsync(DivineUrl, divPath);
            if (!File.Exists(exPath))
                await DownloadAsync(ExaltedUrl, exPath);

            Divine = new Bitmap(divPath);
            Exalted = new Bitmap(exPath);
            IsAvailable = true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[IconCache] failed to load icons: {ex.Message}");
            IsAvailable = false;
        }
    }

    private async Task DownloadAsync(string url, string destPath)
    {
        var bytes = await _http.GetByteArrayAsync(url);
        await File.WriteAllBytesAsync(destPath, bytes);
    }

    public void Dispose()
    {
        Divine?.Dispose();
        Exalted?.Dispose();
        Divine = null;
        Exalted = null;
    }
}
