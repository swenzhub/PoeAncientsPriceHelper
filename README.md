# Poe Ancients Price Helper

A lightweight screen overlay for **Path of Exile 2**. It watches a calibrated region of your screen,
reads the currency / reward list with OCR, looks up live prices from [poe.ninja](https://poe.ninja/poe2),
and draws a click-through price overlay next to each item — so you never have to alt-tab to check what a
stack is worth.

## Features

- **Live prices** next to each list row, sourced from poe.ninja (auto-refreshed every 30 minutes).
- **Stack-aware** — shows the total and the per-item price, e.g. `2 (0.5 each)`.
- **Uncut gems** (skill / spirit / support) priced by exact type **and level** — a row shows `?`
  rather than a guessed price if the gem type or level can't be read cleanly (neighbouring levels
  can differ several-fold, so a wrong-level price would be misleading).
- **Update notifications** — checks GitHub on startup and shows a link in the app when a newer
  release is available.
- **Click-through overlay** that never gets in the way of the game.
- **One-time calibration** — just drag a box around the in-game list panel.
- **Hotkeys:** `F4` recalibrate · `F3` debug boxes · `Esc` / `Ctrl+Click` hide.

## Download & run

Grab the latest `PoeAncientsPriceHelper-vX.Y.Z-win-x64.zip` from the
[**Releases**](../../releases) page, unzip it anywhere, and double-click **`Start.cmd`**.
No install and no .NET runtime required — it's a self-contained Windows x64 build.

Full usage instructions (with screenshots) are in the `README.html` included in the download.

> Windows SmartScreen may warn that the app is unsigned — click **More info → Run anyway**.

## Build from source

Requires the .NET 8 SDK.

```sh
# run tests
dotnet test src/PoeAncientsPriceHelper.Tests/

# build a self-contained release
dotnet publish src/PoeAncientsPriceHelper/ -c Release -r win-x64 --self-contained true -o publish
```

## Tech

WPF (settings window) + WinForms (overlay), Tesseract OCR, .NET 8 (`net8.0-windows`).

## Support

If this tool saves you some alt-tabbing, there's a **☕ Buy me a coffee** button right in the app.
Thanks!

## Disclaimer for those who seem to be troubled by it.. 
Yes it was greatly helped by claude :D never the less it works and its free!
