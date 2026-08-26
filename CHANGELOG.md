# Changelog

All notable changes to this project will be documented in this file.
Format based on [Keep a Changelog](https://keepachangelog.com/), generated from [Conventional Commits](https://www.conventionalcommits.org/).

## [v1.0.0] - 2026-08-26

Initial public release.

### 🚀 Features

- **lyrics**: 3 display modes (single line / two-line / follow-scroll), 8 line-change transitions (none / fade / slide / zoom / blur / blur-zoom / push / two-line block push)
- **lyrics**: PushPair two-line block push — old block pushes out the window top while the new block pushes in (SPlayer transition-group semantics, 400ms CubicEase EaseInOut)
- **lyrics**: per-line exit animation (120ms fade + 3px rise) paired with 180ms entrances for Fade/Slide/Zoom/Blur/BlurZoom
- **lyrics**: translation toggle, ±2s lyric offset with 100ms step
- **lyrics**: hover swaps to title/artist with a plain fade and freezes there
- **settings**: settings window rewritten with Wpf.Ui (WPF Gallery) components, including an About page
- **player**: media session integration, marquee scrolling for long lines, Everything search

### 🐛 Bug Fixes

- **lyrics**: exit-transition ghost pinned to the old line's actual scroll offset (long lines no longer jump back to the line start before fading out)
- **lyrics**: ghost rows inherit the configured FontFamily (CJK fallback synthetic bold made line 1 render ExtraBold)
- **lyrics**: hover no longer re-triggers PushPair on every lyric tick

### 📖 Documentation

- Bilingual README (English + 简体中文), Windows 10 compatibility notes

### ⚙️ Miscellaneous

- Open-source readiness: CI workflow, About page, MIT license metadata

> Note: release builds are self-contained single-file executables — no .NET runtime installation required.
