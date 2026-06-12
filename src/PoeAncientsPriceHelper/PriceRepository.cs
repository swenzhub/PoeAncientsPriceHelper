using System.Collections.ObjectModel;
using System.Net.Http;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PoeAncientsPriceHelper;

// DivineValue  = price in divine orbs (primaryValue from API)
// ExaltedValue = DivineValue * core.rates.exalted (computed, for display when < 1 divine)
internal sealed record PriceEntry(decimal DivineValue, decimal ExaltedValue);

internal sealed class PriceRepository : IDisposable
{
    private readonly HttpClient _http;
    private volatile IReadOnlyDictionary<string, PriceEntry> _prices =
        new ReadOnlyDictionary<string, PriceEntry>(new Dictionary<string, PriceEntry>());
    private System.Threading.Timer? _timer;
    // Cancelled on Dispose so a fetch in flight at shutdown (or one stuck behind the HttpClient
    // timeout) is abandoned cleanly instead of running on against a disposed client.
    private readonly CancellationTokenSource _cts = new();

    public IReadOnlyDictionary<string, PriceEntry> Prices => _prices;
    public DateTime? LastFetchedAt { get; private set; }
    public int ItemCount => _prices.Count;

    // Raised after every successful fetch (initial + each 30-min background refresh) so the UI can
    // refresh its "last fetch" label — which otherwise stays frozen at the startup time. Fires on a
    // thread-pool thread; subscribers must marshal to the UI thread.
    public event Action? PricesUpdated;

    // UncutGems shares the exact same response shape as the others: a root items[] maps each
    // line id (e.g. "uncut-spirit-gem-19") to a display name that already carries the level
    // ("Uncut Spirit Gem (Level 19)"), which NormalizeName reduces to "uncut spirit gem level 19" —
    // the same string the OCR produces. So no special parsing is needed; matching safety (pinning
    // the gem type + level) lives in ScanEngine.BuildPriceRows.
    private static readonly string[] ExchangeTypes = ["Verisium", "Runes", "Expedition", "Currency", "UncutGems"];

    public PriceRepository(HttpClient http) => _http = http;

    public async Task InitialFetchAsync(AppConfig config)
    {
        await FetchAndMergeAsync(config, _cts.Token);
    }

    public void StartAutoRefresh(AppConfig config)
    {
        _timer?.Dispose();
        _timer = new System.Threading.Timer(_ => Task.Run(() => FetchAndMergeAsync(config, _cts.Token)),
            null, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));
    }

    private async Task FetchAndMergeAsync(AppConfig config, CancellationToken ct)
    {
        try
        {
            var dict = new Dictionary<string, PriceEntry>();
            foreach (var type in ExchangeTypes)
            {
                var entries = await FetchTypeAsync(config.LeagueName, type, ct);
                foreach (var (name, entry) in entries)
                    dict[name] = entry;
            }
            ApplyCustomOverride(dict, config.CustomPricesPath);
            _prices = new ReadOnlyDictionary<string, PriceEntry>(dict);
            LastFetchedAt = DateTime.Now;
            PricesUpdated?.Invoke();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down — abandon this cycle quietly. (A timeout, by contrast, is not
            // cancellation-requested, so it falls through to the log below.)
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PriceRepository] fetch failed: {ex.Message}");
        }
    }

    private async Task<Dictionary<string, PriceEntry>> FetchTypeAsync(string league, string type, CancellationToken ct)
    {
        var slug = league.Replace(" ", "").ToLowerInvariant();
        var typeSlug = type.ToLowerInvariant();
        var url = $"https://poe.ninja/poe2/api/economy/exchange/current/overview?league={Uri.EscapeDataString(league)}&type={type}";

        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/148.0.0.0 Safari/537.36");
        req.Headers.TryAddWithoutValidation("Referer",
            $"https://poe.ninja/poe2/economy/{slug}/{typeSlug}");

        var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"[PriceRepository] {type}: HTTP {(int)resp.StatusCode}");
            return [];
        }

        var json = await resp.Content.ReadAsStringAsync(ct);
        return ParseResponse(json);
    }

    // API shape (exchange/current/overview):
    //   items[]   → { id, name }             — display name lookup
    //   lines[]   → { id, primaryValue }     — price in the league's PRIMARY currency
    //   core.primary  → "divine" | "exalted" — which currency primaryValue is denominated in
    //   core.rates    → { exalted, divine, chaos } — how many of each currency equal 1 primary
    // The primary currency differs by league: Softcore prices in divines, Hardcore prices in
    // exalted (divine is too valuable there). So derive both divine- and exalted-denominated
    // values from primaryValue via the rates, rather than assuming primaryValue is divines.
    private static Dictionary<string, PriceEntry> ParseResponse(string json)
    {
        var result = new Dictionary<string, PriceEntry>();
        try
        {
            var obj = JObject.Parse(json);

            // id → display name
            var nameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (obj["items"] is JArray itemsArr)
                foreach (var item in itemsArr)
                {
                    var id = item["id"]?.Value<string>();
                    var name = item["name"]?.Value<string>();
                    if (id is not null && name is not null) nameMap[id] = name;
                }

            // rates[x] = how many x equal 1 unit of the primary currency. When the primary IS
            // divine/exalted, its own rate is implicitly 1 (and absent from the rates object).
            var core = obj["core"];
            var primary = core?["primary"]?.Value<string>() ?? "divine";
            var rates = core?["rates"];
            var divinePerPrimary = primary == "divine" ? 1m : rates?["divine"]?.Value<decimal>() ?? 0m;
            var exaltedPerPrimary = primary == "exalted" ? 1m : rates?["exalted"]?.Value<decimal>() ?? 1m;

            if (obj["lines"] is not JArray lines) return result;
            foreach (var line in lines)
            {
                var id = line["id"]?.Value<string>();
                if (id is null || !nameMap.TryGetValue(id, out var name)) continue;
                var primaryValue = line["primaryValue"]?.Value<decimal>() ?? 0m;
                var divineValue = primaryValue * divinePerPrimary;
                var exaltedValue = Math.Round(primaryValue * exaltedPerPrimary, 1);
                var key = NormalizeName(name);
                if (!string.IsNullOrEmpty(key))
                    result[key] = new PriceEntry(divineValue, exaltedValue);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PriceRepository] parse failed: {ex.Message}");
        }
        return result;
    }

    private static void ApplyCustomOverride(Dictionary<string, PriceEntry> dict, string path)
    {
        try
        {
            var fullPath = Path.IsPathRooted(path)
                ? path
                : Path.Combine(AppContext.BaseDirectory, path);
            if (!File.Exists(fullPath)) return;
            var json = File.ReadAllText(fullPath);
            var overrides = JsonConvert.DeserializeObject<Dictionary<string, CustomPriceEntry>>(json);
            if (overrides is null) return;
            foreach (var (rawKey, entry) in overrides)
            {
                var key = NormalizeName(rawKey);
                if (!string.IsNullOrEmpty(key))
                    dict[key] = new PriceEntry(entry.DivineValue, entry.ExaltedValue);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PriceRepository] custom override failed: {ex.Message}");
        }
    }

    internal static string NormalizeName(string name)
    {
        var s = name.ToLowerInvariant();
        s = Regex.Replace(s, @"[^\w\s]", " ");
        s = Regex.Replace(s, @"\s+", " ");
        return s.Trim();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _timer?.Dispose();
        _timer = null;
        _cts.Dispose();
    }

    private sealed class CustomPriceEntry
    {
        public decimal DivineValue { get; set; }
        public decimal ExaltedValue { get; set; }
    }
}
