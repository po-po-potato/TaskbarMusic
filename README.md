# TaskbarMusic

A lightweight music controller and live lyrics display embedded directly into the Windows taskbar.

![License](https://img.shields.io/badge/license-MIT-blue)
![Platform](https://img.shields.io/badge/platform-Windows%2010%201809%2B-lightgrey)
![.NET](https://img.shields.io/badge/.NET-9-purple)

## What it does

TaskbarMusic lives inside your taskbar — no floating windows, no extra docks. It follows whatever is playing on your system (Spotify, NetEase Cloud Music, QQ Music, browsers, local players...) via Windows SMTC, and renders the current song's lyrics right on the taskbar.

### Features

- **Taskbar-embedded** — uses `SetParent` to live inside `Shell_TrayWnd`; survives taskbar/explorer restarts, DPI changes and display switches
- **System-wide media follow** — SMTC-based, works with any player that reports to Windows media controls
- **Five lyric display modes**
  - **A** — song title + current lyric line
  - **B** — lyric on top, title below
  - **C** — lyrics only (large font, single line)
  - **D** — bilingual: original + translation
  - **E** — Apple Music-style follow mode: current line + next line preview with smooth vertical scroll animation
- **Marquee scrolling** for lines wider than the bar (three-phase: hold → scroll → hold, anchored to line timing)
- **Lyric sources** — NetEase Cloud Music API with LRCLIB fallback; global offset fine-tuning (±10 s)
- **Cover-following background** — extracts dominant color from album art
- **Playback controls** — play/pause, prev/next on hover
- **Fluent settings window** — .NET 9 Fluent theme, light/dark follows system, background material switchable (Solid / Mica / Acrylic)
- **Font customization** — any installed font family and sizes

### Performance

Designed to be the lightest in its category: single process, ~170 MB working set, no webview, no background services.

## Requirements

- Windows 10 1809+ or Windows 11
- .NET 9 runtime (or build self-contained)

## Build

```bash
git clone https://github.com/po-po-potato/TaskbarMusic.git
cd TaskbarMusic
./build.bat        # or: dotnet build -c Release
```

The build script pins a unique `BUILD_ID` per run (see `Directory.Build.props`) to avoid locked intermediate directories.

## Roadmap

TaskbarMusic focuses on the music experience. A modular multi-tool (pomodoro, weather, search...) may ship separately under a different name.

## License

[MIT](LICENSE)
