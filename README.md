# TaskbarMusic

**English | [简体中文](README.zh-CN.md)**

A lightweight music controller and live lyrics display embedded directly into the Windows taskbar.

![License](https://img.shields.io/badge/license-MIT-blue)
![Platform](https://img.shields.io/badge/platform-Windows%2010%20%2B-lightgrey)
![.NET](https://img.shields.io/badge/.NET-9-purple)
![Build](https://github.com/po-po-potato/TaskbarMusic/actions/workflows/build.yml/badge.svg)

## What it does

TaskbarMusic lives inside your taskbar — no floating windows, no extra docks. It follows whatever is playing on your system (Spotify, NetEase Cloud Music, QQ Music, browsers, local players...) via Windows SMTC, and renders the current song's lyrics right on the taskbar.

### Features

- **Taskbar-embedded** — uses `SetParent` to live inside `Shell_TrayWnd`; survives taskbar/explorer restarts, DPI changes and display switches
- **System-wide media follow** — SMTC-based, works with any player that reports to Windows media controls
- **Three lyric display modes**
  - **Title + Lyric** — song title on top, current line below
  - **Single line** — the current line only, large font
  - **Two lines** — current line + next-line preview, Apple Music-style smooth vertical scroll on line change
- **Seven line-change transitions** — hard cut, fade, slide, zoom, blur, blur+zoom (Apple Music-style) and push (old line slides out, new line slides in)
- **Optional translation line** (single-line mode) when the lyric source provides translated lyrics
- **Marquee scrolling** for lines wider than the bar (three-phase: hold → scroll → hold, anchored to line timing)
- **Lyric sources** — NetEase Cloud Music API with LRCLIB fallback; global offset fine-tuning (±2 s in 100 ms steps)
- **Cover-following background** — extracts dominant color from album art
- **Playback controls** — play/pause, prev/next on hover
- **Fluent settings window** — built with [WPF UI](https://github.com/lepoco/wpfui) (WPF Gallery) components: Windows 11 Settings-style navigation, light/dark follows the system live, background material switchable (Solid / Mica / Acrylic), remembered size
- **Font customization** — any installed font family and sizes, live preview

### Performance

Designed to be the lightest in its category: single process, ~100 MB working set, no webview, no background services.

## Requirements

- Windows 10 1809+ or Windows 11 (x64)
- .NET 9 runtime — or just grab the self-contained build from [Releases](https://github.com/po-po-potato/TaskbarMusic/releases) (no runtime install needed)

> **Windows 10 note**: core features (taskbar embedding, media follow, lyrics) work on Win10, but the Mica / Acrylic settings-window materials require **Windows 11 22H2+** and silently fall back to solid color on Win10. The project is developed and tested mainly on Windows 11; Win10 compatibility is expected but not fully validated.

## Build

```bash
git clone https://github.com/po-po-potato/TaskbarMusic.git
cd TaskbarMusic
dotnet build -c Release
```

Requires the .NET 9 SDK. `build.bat` is a convenience script for local development on Windows (it also launches the app after building).

## Roadmap

- Word-by-word karaoke highlight (YRC timed lyrics)
- Interlude indicator (breathing dots during instrumental gaps)
- Secondary taskbar (multi-monitor) support
- Simplified/Traditional Chinese conversion for lyrics

TaskbarMusic focuses on the music experience. A modular multi-tool (pomodoro, weather, search...) may ship separately under a different name.

## License

[MIT](LICENSE)
