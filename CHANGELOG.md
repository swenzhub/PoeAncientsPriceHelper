# Changelog

All notable changes to **Poe Ancients Price Helper** are documented here.
The format is loosely based on [Keep a Changelog](https://keepachangelog.com/),
and this project adheres to [Semantic Versioning](https://semver.org/).

## [2.0.1] — 2026-06-19

### Fixed

- **Overlay oversized / offset on a high-DPI secondary monitor** (#21) — on a multi-monitor setup
  where PoE runs on a monitor at a non-100% display scale (e.g. 125%) while another monitor is at
  100%, the price overlay (and the F3 debug boxes) were drawn ~1.25× too large and shifted. The v2.0
  layered-window overlay composes its scene in physical pixels and blits via `UpdateLayeredWindow`,
  which is only 1:1 if the overlay window is genuinely Per-Monitor-V2 aware; on the scaled monitor
  its effective DPI context was lower, so DWM upscaled the whole bitmap. Capture/calibration use raw
  physical APIs and were unaffected, so prices were read correctly but drawn in the wrong place. The
  overlay thread now pins `PER_MONITOR_AWARE_V2` before its window is created, and the form opts out
  of WinForms auto-scaling (`AutoScaleMode.None`). (Regression from the PR #20 rewrite; same class as
  the mixed-DPI fix in #8.)

### Packaging

- **`debug.cmd`** added to the distributable — double-click to run with diagnostics (console + a
  detailed `scan_log.txt`) for problem reports, without typing `--debug` by hand. README updated.

## [2.0.0] — 2026-06-19

A ground-up performance and stability overhaul. The app now uses a fraction of the CPU it used to,
captures frames via the GPU, and detects items instantly with the native Windows OCR engine.

### OCR

- **Replaced Tesseract with Windows.Media.Ocr** — the native WinRT OCR engine designed for on-screen
  text. Tesseract (a document-oriented engine) struggled with rendered game text; some items took
  seconds to appear or were never detected. Windows OCR detects all items instantly with no external
  dependencies (no traineddata files, no NuGet packages).
- **3x upscaling** for glyph accuracy on small fonts.

### Performance

- **Windows Graphics Capture (WGC) backend** — screen capture now runs on the GPU via D3D11 +
  WinRT interop (Vortice), cutting CPU usage dramatically compared to GDI `CopyFromScreen`.
  Falls back to GDI automatically per-frame if WGC is unavailable. Configurable via
  `CaptureBackend` in `config.json` (`"Auto"` / `"GDI"`).
- **Overlay render skip** — the overlay no longer repaints every cycle; it only redraws when the
  rows, panel state, or reading state actually change.
- **Cached render buffer** — avoids allocating a monitor-sized bitmap on every render.
- **Panel detection via LockBits** — `ListDetector` now uses `LockBits` + `Marshal.ReadByte`
  instead of 60 individual `GetPixel` calls per pass.
- **Resolution cache** — OCR'd names are resolved to price keys once, then cached (invalidated on
  each price refresh). Skips the dictionary scan + Levenshtein work on every subsequent pass.
- **Length-bucketed fuzzy index** — the fuzzy matcher only scans price keys within ±3 characters
  of the OCR'd name length, not the entire dictionary.
- **Pre-compiled regexes** — all `Regex` instances in the hot path are now `static readonly`
  with `RegexOptions.Compiled`.
- **Parallel price fetch** — all exchange types are fetched concurrently via `Task.WhenAll`
  over a single HTTP/2 connection instead of sequentially.
- **Throttled scan intervals** — OCR runs at 150ms (was 100ms) while the panel is open; idle
  polling runs at 300ms (was 150ms). Both still feel instant while cutting CPU load significantly.

### Stability

- **Overlay concurrency fixes** — `PriceOverlayManager` no longer holds a lock across cross-thread
  UI dispatches (deadlock risk), and all UI calls are wrapped in try/catch for
  `ObjectDisposedException` / `InvalidOperationException`.
- **League-switch lifecycle** — changing leagues now stops and disposes the scanner before
  reloading prices/icons, with a reentrancy guard to prevent overlapping reloads.
- **Atomic price snapshot** — `Prices` and `KeysByLength` are now published together in a single
  immutable `PriceSnapshot` record, preventing torn reads during background refresh.
- **Static flag reset** — `_dismissed` / `_showing` are reset in `Start()` so a stale loop can't
  clobber a new instance's state.
- **GDI handle leak** fixed in overlay rendering (`GetDC` / `CreateCompatibleDC` now inside `try`).
- **WGC resource management** — COM reference leaks fixed in `GraphicsCaptureItem` creation and
  `ID3D11Texture2D` frame access. Frame pool is recreated on monitor resolution change. WGC
  permanently falls back to GDI if initialization fails (no retry storm).
- **Stale overlay clearing** — the overlay automatically hides stale prices during loading screens.

### Fixed

- **Freeze when calibrating via the hotkey (#14)** — triggering calibration with the global hotkey
  while the game (or any other app) held the foreground left the calibration overlay stuck behind it
  with no way to confirm/cancel, blocking the UI thread indefinitely. The overlay now forcibly takes
  the foreground when shown (`AttachThreadInput` + `SetForegroundWindow`).
- **Per-Monitor-V2 DPI awareness kept in `app.manifest`** — DPI awareness must be declared in the
  manifest for this WPF-first app; the `<ApplicationHighDpiMode>` project property is a no-op here
  and would have regressed the mixed-DPI multi-monitor fix from #8.

### Code quality (YAGNI)

- Removed dead code: `ScreenCapture.cs`, `IsAllBlack`, `ReferencePixelColor`.
- Unified duplicate `NormalizeName` into a single `NameNormalizer` helper.
- Simplified `ListDetector` to return only the sampled color (threshold logic lives in `ScanEngine`).
- Extracted `WithForm(...)` helper in `PriceOverlayManager` to eliminate 5× duplicated lock+try/catch.
- Extracted `DisposeDevice()` in `WgcScreenCaptureBackend` to eliminate duplicated teardown.
- Removed empty `IDisposable` from `OcrScanner`, dead `Stop()` method, and one-line wrappers.

### Dependencies

- Target framework bumped to `net10.0-windows10.0.19041.0` (.NET 10).
- Added `Vortice.Direct3D11` and `Vortice.DXGI` for WGC interop.
- Removed `Tesseract` and `Tesseract.Data.English`.
- All other NuGet packages updated to latest stable.

## [1.1.8] — 2026-06-14

### Fixed
- Calibration on **mixed-DPI multi-monitor** setups (e.g. a 100% primary + a 175% secondary).
  Calibrating on the high-DPI monitor used to produce a wrong region — size shrunk by the scale
  factor, origin off-screen — so the overlay showed nothing. The app now declares Per-Monitor-V2 DPI
  awareness at startup and captures the calibration box in true physical screen pixels, so the region
  matches exactly where you drew it. (#8, reported & verified by @ljere)
- Calibration instructions now stay pinned to the primary monitor instead of occasionally rendering
  on the secondary screen.

## [1.1.7] — 2026-06-12

### Changed
- Price fetches now time out after 15s and retry on the next cycle instead of blocking for up to
  100s when poe.ninja is slow or a connection stalls.
- The config file is now written atomically, so a crash or power loss mid-save can no longer corrupt
  it and reset your region/hotkeys to defaults.
- The diagnostic `debug_ocr.png` dump is only written in debug mode, so normal users no longer get a
  file rewritten in their folder while a panel is open.
- Removed dead code and tightened shutdown so an in-flight fetch is cancelled cleanly.

Most of this hardening came out of a community code audit (thanks to @crichmond1989).

## [1.1.6] — 2026-06-12

### Added
- **Uncut gem prices** — uncut skill, spirit, and support gems are now priced by exact gem type
  **and level** (e.g. `Uncut Spirit Gem (Level 19)` priced as a level-19 spirit gem). A row shows
  **?** rather than a guessed price if the type or level can't be read cleanly, then fills in once a
  clean read arrives — neighbouring levels can differ several-fold in value.
- **Update notifications** — on startup the app checks GitHub for a newer release and shows an
  "Update available" link in the window when one exists.

## [1.1.5] — 2026-06-12

### Added
- **Configurable hotkeys** for Start/Stop, Debug overlay, and Calibrate, each independently
  rebindable (defaults unchanged: F5 / F3 / F4). Binding a key already used by another action is
  rejected. (#4, #6)
- **Minimize to tray** — minimizing sends the app to a system-tray icon and keeps scanning in the
  background; double-click (or right-click → Show) to restore, right-click → Exit to quit. (#2)
- **Multi-monitor support** — calibrate the region on any monitor, and the overlay appears on the
  monitor your game runs on instead of only the primary one. (#3)

### Fixed
- Overlay flicker — fixed a post-Escape re-flash where prices briefly reappeared after closing the
  panel, and added brightness hysteresis so the overlay no longer blinks near the detection
  threshold. (#5)

## [1.1.4] — 2026-06-09

### Changed
- Each price now sits on a subtle semi-transparent dark plate behind the icon and number, so values
  stay legible over busy in-game art. The overlay was reworked to true per-pixel transparency.

## [1.1.3] — 2026-06-08

### Added
- **Configurable Start/Stop hotkey** — F5 is now just the default; rebind it in-app and it takes effect
  immediately and persists.

### Changed
- Start/Stop now uses the same global key listener as the other hotkeys, so it no longer clashes with
  other apps that grab the same key.

## [1.1.2] — 2026-06-08

### Added
- **Hardcore league pricing** — the League dropdown now offers **HC Runes of Aldur**, with prices
  pulled from the matching poe.ninja economy and correctly denominated (softcore in divine, hardcore
  in exalted).
- The running version is now shown in the bottom-left of the window.

## [1.1.1] — 2026-06-08

### Added
- F5 hotkey to start/stop scanning without clicking the window.

### Fixed
- Prices now always use a `.` decimal separator on every system locale (previously showed e.g.
  `0,1` on comma-decimal regions).

## [1.1.0] — 2026-06-08

First public release.

### Added
- Live poe.ninja prices overlaid on the in-game currency list.
- One-time calibration; stack-aware pricing (e.g. `2 (0.5 each)`).
- Self-contained Windows x64 build.

[2.0.0]: https://github.com/pedro-quiterio/PoeAncientsPriceHelper/releases/tag/v2.0.0
[1.1.8]: https://github.com/pedro-quiterio/PoeAncientsPriceHelper/releases/tag/v1.1.8
[1.1.7]: https://github.com/pedro-quiterio/PoeAncientsPriceHelper/releases/tag/v1.1.7
[1.1.6]: https://github.com/pedro-quiterio/PoeAncientsPriceHelper/releases/tag/v1.1.6
[1.1.5]: https://github.com/pedro-quiterio/PoeAncientsPriceHelper/releases/tag/v1.1.5
[1.1.4]: https://github.com/pedro-quiterio/PoeAncientsPriceHelper/releases/tag/v1.1.4
[1.1.3]: https://github.com/pedro-quiterio/PoeAncientsPriceHelper/releases/tag/v1.1.3
[1.1.2]: https://github.com/pedro-quiterio/PoeAncientsPriceHelper/releases/tag/v1.1.2
[1.1.1]: https://github.com/pedro-quiterio/PoeAncientsPriceHelper/releases/tag/v1.1.1
[1.1.0]: https://github.com/pedro-quiterio/PoeAncientsPriceHelper/releases/tag/v1.1.0
