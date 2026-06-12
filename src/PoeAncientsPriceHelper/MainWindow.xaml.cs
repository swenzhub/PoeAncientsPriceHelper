using System.Net.Http;
using System.Windows;
using MahApps.Metro.Controls;
using SharpHook.Data;

namespace PoeAncientsPriceHelper;

public partial class MainWindow : MetroWindow
{
    private AppConfig _config = new();
    private PriceRepository? _repo;
    private IconCache? _icons;
    private ScanEngine? _engine;
    // 15s cap so a stalled poe.ninja/poecdn connection can't hang a whole fetch cycle for the
    // default 100s. Per-fetch cancellation (shutdown) is handled inside PriceRepository.
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private bool _loading;

    // Minimize-to-tray (#2). The window hides to a tray icon on minimize and restores from it; the X
    // button still fully exits. Scanning is independent of this window, so it keeps running in the tray.
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private bool _trayBalloonShown;

    // All three hotkeys (Start/Stop, Debug, Calibrate) now live on the App-level SharpHook hook and are
    // user-configurable — no Win32 RegisterHotKey here anymore.

    public MainWindow()
    {
        InitializeComponent();
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        if (v is not null) VersionLabel.Text = $"v{v.Major}.{v.Minor}.{v.Build}";
        Loaded += OnLoaded;
        StateChanged += OnStateChanged;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _config = ConfigStore.Load();
        PopulateFields();
        await StartupAsync();
        // Fire-and-forget, once per launch (not inside StartupAsync, which re-runs on league change).
        // A slow/hung GitHub response must never delay the price fetch or the Start button.
        _ = CheckForUpdatesAsync();
    }

    // Quietly check GitHub for a newer release on startup. On success with a higher version, show a
    // red "update available" link; on any failure (offline, rate-limited, API shape change) do nothing.
    private string? _updateUrl;

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var current = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            if (current is null) return;

            var req = new HttpRequestMessage(HttpMethod.Get,
                "https://api.github.com/repos/pedro-quiterio/PoeAncientsPriceHelper/releases/latest");
            req.Headers.TryAddWithoutValidation("User-Agent", "PoeAncientsPriceHelper");  // GitHub 403s without one
            req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");

            var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return;

            var json = await resp.Content.ReadAsStringAsync();
            var obj = Newtonsoft.Json.Linq.JObject.Parse(json);
            var tag = (string?)obj["tag_name"];
            if (string.IsNullOrWhiteSpace(tag) || !Version.TryParse(tag.TrimStart('v', 'V'), out var latest))
                return;

            // Compare Major.Minor.Build only (ignore revision); only flag a genuinely newer release.
            var cur = new Version(current.Major, current.Minor, Math.Max(current.Build, 0));
            var rem = new Version(latest.Major, latest.Minor, Math.Max(latest.Build, 0));
            if (rem <= cur) return;

            _updateUrl = (string?)obj["html_url"];
            Dispatcher.BeginInvoke(() =>
            {
                UpdateLink.Text = $"⬆ Update available: v{rem.Major}.{rem.Minor}.{rem.Build} — click to download";
                UpdateLink.Visibility = Visibility.Visible;
            });
        }
        catch { /* offline / rate-limited / shape change — fail silently, leave the link hidden */ }
    }

    private void UpdateLink_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (string.IsNullOrEmpty(_updateUrl)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_updateUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Update] failed to open browser: {ex.Message}");
        }
    }

    private void PopulateFields()
    {
        _loading = true;
        LeagueBox.ItemsSource = _config.AvailableLeagues;
        LeagueBox.SelectedItem = _config.AvailableLeagues.Contains(_config.LeagueName)
            ? _config.LeagueName
            : _config.AvailableLeagues.FirstOrDefault();
        // Arm the global hook with all three persisted bindings and mirror them into the labels.
        var startStop = HotkeyBinding.Parse(_config.StartStopHotkey);
        var debug = HotkeyBinding.Parse(_config.DebugHotkey);
        var calibrate = HotkeyBinding.Parse(_config.CalibrateHotkey);
        HotkeyLabel.Text = HotkeyBinding.Display(startStop);
        DebugHotkeyLabel.Text = HotkeyBinding.Display(debug);
        CalibrateHotkeyLabel.Text = HotkeyBinding.Display(calibrate);
        App.SetStartStopKey(startStop);
        App.SetDebugKey(debug);
        App.SetCalibrateKey(calibrate);
        UpdateRegionLabel();
        _loading = false;
    }

    private void UpdateRegionLabel()
    {
        RegionLabel.Text = _config.IsCalibrated
            ? $"x={_config.RegionX} y={_config.RegionY} {_config.RegionWidth}×{_config.RegionHeight}"
            : "Not calibrated";
    }

    private async Task StartupAsync()
    {
        StatusLabel.Text = "Fetching prices from poe.ninja…";
        StartStopButton.IsEnabled = false;

        _repo?.Dispose();
        _icons?.Dispose();

        _repo = new PriceRepository(_http);
        _repo.PricesUpdated += OnPricesUpdated;   // keep the "last fetch" label live on each refresh
        _icons = new IconCache(_http);

        await Task.WhenAll(
            _repo.InitialFetchAsync(_config),
            _icons.LoadAsync());

        _repo.StartAutoRefresh(_config);

        UpdateStatusLabel();
        StartStopButton.IsEnabled = _config.IsCalibrated;
    }

    // The 30-min background refresh fires on a thread-pool thread — marshal to the UI thread
    // before touching the label. (Previously the label was set once at startup and never updated,
    // so it stayed frozen at the launch-time fetch even though prices kept refreshing.)
    private void OnPricesUpdated() => Dispatcher.BeginInvoke(UpdateStatusLabel);

    private void UpdateStatusLabel()
    {
        if (_repo is null) return;
        string fetched = _repo.LastFetchedAt is { } t ? t.ToString("MMM d HH:mm") : "never";
        StatusLabel.Text = $"{_repo.ItemCount} items loaded  ·  last fetch {fetched}";
    }

    private void DonateButton_Click(object sender, RoutedEventArgs e)
    {
        const string url = "https://www.paypal.com/donate/?business=pedro.levi.magic%40gmail.com&currency_code=USD&item_name=PoeAncientsPriceHelper";
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Donate] failed to open browser: {ex.Message}");
        }
    }

    // internal so the App-level hook (configurable Calibrate key) can trigger it too.
    internal void RunCalibration()
    {
        var rect = CalibrationOverlay.RunOnStaThread();
        if (rect is null) return;
        _config.RegionRect = rect.Value;
        ConfigStore.Save(_config);
        Dispatcher.Invoke(() =>
        {
            UpdateRegionLabel();
            StartStopButton.IsEnabled = _config.IsCalibrated;
        });
    }

    private void CalibrateButton_Click(object sender, RoutedEventArgs e) => RunCalibration();

    private void StartStopButton_Click(object sender, RoutedEventArgs e) => ToggleStartStop();

    // Shared by the Start/Stop button and the configurable global hotkey (invoked via App, marshalled
    // to the UI thread). internal so the App-level hook can reach it.
    internal void ToggleStartStop()
    {
        if (_engine is null)
        {
            // The hotkey can fire even when the button is disabled — don't start until we're ready.
            if (!_config.IsCalibrated || _repo is null || _icons is null) return;
            _engine = new ScanEngine(_config, _repo, _icons);
            _engine.Start();
            StartStopButton.Content = "Stop";
            StartStopButton.Background = System.Windows.Media.Brushes.DarkRed;
        }
        else
        {
            _engine.StopAndWait(TimeSpan.FromSeconds(2));
            _engine.Dispose();
            _engine = null;
            StartStopButton.Content = "Start";
            StartStopButton.Background = System.Windows.Media.Brushes.DarkGreen;
        }
    }

    // Minimize → hide the window and drop to the tray (scanning keeps running). Restore/Exit live on
    // the tray icon. The X button is unaffected and still quits via Window_Closing.
    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized) return;
        EnsureTrayIcon();
        _trayIcon!.Visible = true;
        Hide();   // remove the taskbar button; the tray icon is now the way back
        if (!_trayBalloonShown)
        {
            _trayIcon.ShowBalloonTip(3000, "Poe Ancients Price Helper",
                "Still running — double-click the tray icon to restore.",
                System.Windows.Forms.ToolTipIcon.Info);
            _trayBalloonShown = true;
        }
    }

    private void EnsureTrayIcon()
    {
        if (_trayIcon is not null) return;
        var exe = Environment.ProcessPath;
        var icon = exe is not null
            ? System.Drawing.Icon.ExtractAssociatedIcon(exe)
            : System.Drawing.SystemIcons.Application;
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = icon,
            Text = "Poe Ancients Price Helper",
            Visible = false,
        };
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Show", null, (_, _) => RestoreFromTray());
        menu.Items.Add("Exit", null, (_, _) => ExitFromTray());
        _trayIcon.ContextMenuStrip = menu;
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        if (_trayIcon is not null) _trayIcon.Visible = false;
    }

    private void ExitFromTray()
    {
        if (_trayIcon is not null) _trayIcon.Visible = false;
        Close();   // routes through Window_Closing for the normal shutdown/cleanup
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _trayIcon?.Dispose();
        _engine?.StopAndWait(TimeSpan.FromSeconds(2));
        _engine?.Dispose();
        _repo?.Dispose();
        _icons?.Dispose();
        _http.Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    private async void LeagueBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loading || LeagueBox.SelectedItem is not string league || league == _config.LeagueName) return;
        _config.LeagueName = league;
        ConfigStore.Save(_config);
        await StartupAsync();   // re-fetch prices for the newly selected league
    }

    // The rebind in progress: which action, and the button/label to update. Only one runs at a time.
    private HotkeyBinding.Action _rebindAction;
    private System.Windows.Controls.Button? _rebindButton;
    private System.Windows.Controls.TextBlock? _rebindLabel;

    private void RebindButton_Click(object sender, RoutedEventArgs e) =>
        BeginRebind(HotkeyBinding.Action.StartStop, RebindButton, HotkeyLabel);

    private void RebindDebugButton_Click(object sender, RoutedEventArgs e) =>
        BeginRebind(HotkeyBinding.Action.Debug, RebindDebugButton, DebugHotkeyLabel);

    private void RebindCalibrateButton_Click(object sender, RoutedEventArgs e) =>
        BeginRebind(HotkeyBinding.Action.Calibrate, RebindCalibrateButton, CalibrateHotkeyLabel);

    private void BeginRebind(HotkeyBinding.Action action, System.Windows.Controls.Button button,
                             System.Windows.Controls.TextBlock label)
    {
        _rebindAction = action;
        _rebindButton = button;
        _rebindLabel = label;
        // Disable all three rebind buttons so a second capture can't start mid-rebind.
        SetRebindButtonsEnabled(false);
        button.Content = "Press a key… (Esc to cancel)";
        App.BeginHotkeyCapture(action, OnHotkeyCaptured);   // outcome arrives on the UI thread
    }

    // Invoked (marshalled to the UI thread) when the global hook resolves a rebind capture.
    private void OnHotkeyCaptured(App.CaptureOutcome outcome, KeyCode code)
    {
        switch (outcome)
        {
            case App.CaptureOutcome.Captured:
                switch (_rebindAction)
                {
                    case HotkeyBinding.Action.StartStop:
                        _config.StartStopHotkey = HotkeyBinding.ToStorage(code);
                        App.SetStartStopKey(code);
                        break;
                    case HotkeyBinding.Action.Debug:
                        _config.DebugHotkey = HotkeyBinding.ToStorage(code);
                        App.SetDebugKey(code);
                        break;
                    case HotkeyBinding.Action.Calibrate:
                        _config.CalibrateHotkey = HotkeyBinding.ToStorage(code);
                        App.SetCalibrateKey(code);
                        break;
                }
                ConfigStore.Save(_config);
                if (_rebindLabel is not null) _rebindLabel.Text = HotkeyBinding.Display(code);
                EndRebind();
                break;
            case App.CaptureOutcome.Reserved:
                // Still listening — tell the user why that key won't take, keep the prompt up.
                if (_rebindButton is not null)
                    _rebindButton.Content = $"{HotkeyBinding.Display(code)} is in use — try another";
                break;
            case App.CaptureOutcome.Cancelled:
                EndRebind();
                break;
        }
    }

    private void EndRebind()
    {
        if (_rebindButton is not null) _rebindButton.Content = "Rebind";
        _rebindButton = null;
        _rebindLabel = null;
        SetRebindButtonsEnabled(true);
    }

    private void SetRebindButtonsEnabled(bool enabled)
    {
        RebindButton.IsEnabled = enabled;
        RebindDebugButton.IsEnabled = enabled;
        RebindCalibrateButton.IsEnabled = enabled;
    }
}
