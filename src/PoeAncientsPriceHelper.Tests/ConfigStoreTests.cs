using System.Drawing;
using System.IO;
using System.Linq;
using PoeAncientsPriceHelper;

namespace PoeAncientsPriceHelper.Tests;

public class ConfigStoreTests
{
    [Fact]
    public void Load_ReturnsDefaults_WhenFileDoesNotExist()
    {
        using var dir = new TempDir();
        var cfg = LoadFrom(dir.Path);
        Assert.Equal("Runes of Aldur", cfg.LeagueName);
        Assert.Equal(8, cfg.OverlayXOffset);
        Assert.Equal("custom_prices.json", cfg.CustomPricesPath);
        Assert.Equal("VcF5", cfg.StartStopHotkey);
        Assert.False(cfg.IsCalibrated);
    }

    [Fact]
    public void StartStopHotkey_RoundTrips()
    {
        using var dir = new TempDir();
        SaveTo(dir.Path, new AppConfig { StartStopHotkey = "VcF7" });
        Assert.Equal("VcF7", LoadFrom(dir.Path).StartStopHotkey);
    }

    [Fact]
    public void RoundTrip_AllFields()
    {
        using var dir = new TempDir();
        var original = new AppConfig
        {
            LeagueName = "Test League",
            RegionX = 10, RegionY = 20, RegionWidth = 300, RegionHeight = 400,
            OverlayXOffset = 16,
            CustomPricesPath = "my_prices.json"
        };
        SaveTo(dir.Path, original);
        var loaded = LoadFrom(dir.Path);
        Assert.Equal("Test League", loaded.LeagueName);
        Assert.Equal(new Rectangle(10, 20, 300, 400), loaded.RegionRect);
        Assert.Equal(16, loaded.OverlayXOffset);
        Assert.Equal("my_prices.json", loaded.CustomPricesPath);
    }

    [Fact]
    public void AvailableLeagues_NotDuplicated_OnRoundTrip()
    {
        // Newtonsoft's ObjectCreationHandling.Auto appends a deserialized list onto a pre-populated
        // default, doubling entries. AvailableLeagues is [JsonIgnore]'d to stay code-only and avoid it.
        using var dir = new TempDir();
        SaveTo(dir.Path, new AppConfig());
        var loaded = LoadFrom(dir.Path);
        Assert.Equal(new AppConfig().AvailableLeagues, loaded.AvailableLeagues);
        Assert.Equal(loaded.AvailableLeagues.Count, loaded.AvailableLeagues.Distinct().Count());
    }

    [Fact]
    public void Load_ReturnsDefaults_WhenJsonMalformed()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "config.json"), "{ invalid json !!!");
        var cfg = LoadFrom(dir.Path);
        Assert.Equal("Runes of Aldur", cfg.LeagueName);
    }

    [Fact]
    public void Save_OverwritesExisting_AndLeavesNoTempFile()
    {
        using var dir = new TempDir();
        SaveTo(dir.Path, new AppConfig { LeagueName = "First" });
        SaveTo(dir.Path, new AppConfig { LeagueName = "Second" }); // exercises the File.Replace path
        Assert.Equal("Second", LoadFrom(dir.Path).LeagueName);
        // The atomic-swap temp file must not linger next to the real config.
        Assert.False(File.Exists(Path.Combine(dir.Path, "config.json.tmp")));
    }

    // Exercise the real ConfigStore (its path is injectable for exactly this reason) rather than
    // reimplementing the round-trip, so these tests cover the production load/save code.
    private static AppConfig LoadFrom(string dir) => ConfigStore.Load(dir);

    private static void SaveTo(string dir, AppConfig cfg) => ConfigStore.Save(cfg, dir);
}
