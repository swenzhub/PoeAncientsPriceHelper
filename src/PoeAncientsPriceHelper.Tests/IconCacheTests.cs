using System.IO;
using System.Net;
using System.Net.Http;
using PoeAncientsPriceHelper;

namespace PoeAncientsPriceHelper.Tests;

public class IconCacheTests
{
    private static readonly byte[] FakePng = CreateMinimalPng();

    [Fact]
    public async Task DownloadsAndWritesFiles_WhenMissing()
    {
        using var dir = new TempDir();
        var handler = new CountingFakeHttpHandler(FakePng);
        using var http = new HttpClient(handler);
        using var cache = new IconCache(http, dir.Path);

        await cache.LoadAsync();

        Assert.True(cache.IsAvailable);
        Assert.NotNull(cache.Divine);
        Assert.NotNull(cache.Exalted);
        Assert.True(File.Exists(Path.Combine(dir.Path, "divine.png")));
        Assert.True(File.Exists(Path.Combine(dir.Path, "exalted.png")));
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task LoadsFromDisk_WhenFilesExist_NoHttp()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(Path.Combine(dir.Path, "divine.png"), FakePng);
        File.WriteAllBytes(Path.Combine(dir.Path, "exalted.png"), FakePng);

        var handler = new CountingFakeHttpHandler(FakePng);
        using var http = new HttpClient(handler);
        using var cache = new IconCache(http, dir.Path);

        await cache.LoadAsync();

        Assert.True(cache.IsAvailable);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task SetsUnavailable_WhenDownloadFails()
    {
        using var dir = new TempDir();
        var handler = new FailingHttpHandler();
        using var http = new HttpClient(handler);
        using var cache = new IconCache(http, dir.Path);

        await cache.LoadAsync();

        Assert.False(cache.IsAvailable);
        Assert.Null(cache.Divine);
        Assert.Null(cache.Exalted);
    }

    private static byte[] CreateMinimalPng()
    {
        // 1x1 white PNG
        return Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwADhQGAWjR9awAAAABJRU5ErkJggg==");
    }
}
