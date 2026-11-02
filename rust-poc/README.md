# PriceRunner Panel — Rust proof of concept

The same tray panel as the .NET/Avalonia app, written in Rust to see what a native build costs and saves.
It reads and writes the same `state.json` (PascalCase, same fields), reuses the same thumbnail cache files,
and talks to the same PriceRunner endpoints, so a watchlist moves between the two apps unchanged.

## Stack

| Part | Choice | Why |
|---|---|---|
| UI | [Slint](https://slint.dev) 1.18, software renderer, winit backend | Declarative markup close to AXAML; draws on the CPU like release 0.4.7, so no GPU driver is loaded; `ListView` only creates the rows on screen |
| HTTP | ureq 3 (blocking, rustls, gzip) | One agent shared by short worker threads; no async runtime, no thread pool |
| JSON | serde + serde_json | `state.json` compatible with System.Text.Json's PascalCase output |
| Tray | tray-icon + muda | Icon, left-click toggle, context menu with a check item |
| Toasts | tauri-winrt-notification | Same WinRT toast API as the .NET app, with its own AppUserModelID |
| Images | image (jpeg/png/webp/ico) | Thumbnails decoded at 112 px on two worker threads |
| Windows | windows-sys, winreg, open | Single instance (mutex + event), DWM rounded corners, Run key, ShellExecute |

Build: `cargo build --release` (Rust 1.98, MSVC). Output: one 13 MB `pricerunner-panel.exe`, no runtime to install.

## What it does

- Search tab: debounced search of pricerunner.se, rows with picture, category/stores/rating, PriceRunner's drop
  and old price, condition chip, PIN/PINNED button.
- Black Friday tab: PriceRunner's deals with category/store filters (searchable popups), discount range and
  presets, active-filter chips, the refine box (top brands/stores), "Show more deals", search through the
  filtered deals (same scan limits), per-row verdicts and picked-store prices while a filter is on.
- Watchlist: cards with price, change since pinned, chips (variant, verdict, condition, looks used,
  PriceRunner's drop, lowest, target hit), step sparkline with low/latest marks, low–high meter, target price
  field, "Checked … ago", store/open/check/unpin buttons; variant groups boxed with a header and summary.
- Price tracking: checks on the chosen interval (and when due at start), 1.5 s between products, market
  history and reported drop refreshed every 12 h, group lookup, toasts for target reached / new low / drop ≥
  the configured percent, inflated drops ignored.
- Tray: open, check now, check for updates (opens the releases page), start with Windows (HKCU Run key,
  written only when toggled), quit. Closing or the ✕ hides to the tray; a second launch shows the window;
  `--startup` starts hidden. F5, Ctrl+F/1/2/3 work. The window is frameless, draggable by its header,
  resizable by its edges and the grip, and centred on first show.

## What it leaves out

- The self-updater (release check, download, staged apply) and the Start menu shortcut.
- Tooltips, the spinning refresh icon and the breathing status dot (the scan line still animates while busy).
- Transparent window corners: the CPU renderer has no alpha channel on Windows, so the panel is an opaque
  window and Windows 11's DWM rounds its corners and draws its shadow instead. Card shadows are borders.
- Accessibility (UI Automation names) — Slint's `accessibility` feature was left off to keep the build lean.

## Data and identity

To never collide with the installed .NET app, the proof of concept uses its own data folder
(`%LOCALAPPDATA%\PriceRunnerPanel-rs`, or `PRICERUNNERPANEL_DATA`), mutex and event names, AppUserModelID
and Run key value. Copy `state.json` (and the `images` folder) across to use the same watchlist.

## Scripted runs (for measurements)

`PRP_TEST_TAB=0|1|2` opens a tab at start, `PRP_TEST_QUERY=…` runs a search, `PRP_TEST_DEALS_PAGES=n`
loads n deal pages (`PRP_TEST_PAGE_EVERY_MS` apart), `PRP_TEST_SIZE=WxH` sets the window size.

## Benchmark

`benchmark/rust-poc-2026-11-02.md` compares this build with .NET release 0.4.7 on the same machine and data, with
screenshots of both apps.
