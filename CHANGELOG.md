# 更新日志 / Changelog

所有显著变更记录于此文件 / All notable changes will be documented in this file.
格式基于 [Keep a Changelog](https://keepachangelog.com/)，由 [Conventional Commits](https://www.conventionalcommits.org/) 生成。

## [v1.0.0] - 2026-08-26

### 中文

首次公开发布。

#### ✨ 新增

- **歌词**：3 种显示模式（单行 / 双行 / 跟随滚动），8 种换句过渡（硬切 / 淡入 / 上滑 / 缩放 / 模糊 / 模糊缩放 / 推挤 / 双行整块推挤）
- **歌词**：双行整块推挤（PushPair）——旧两行整块推出窗口顶、新两行从窗口底推入（SPlayer transition-group 语义，400ms CubicEase EaseInOut）
- **歌词**：逐行退场动画（120ms 淡出 + 3px 上浮）搭配 180ms 入场（淡入/上滑/缩放/模糊/模糊缩放）
- **歌词**：翻译开关、±2s 歌词偏移（100ms 步进）
- **歌词**：hover 时切为歌名/艺术家展示（普通淡入）并保持固定
- **设置**：设置窗用 Wpf.Ui（WPF Gallery）组件重构，新增"关于"页
- **播放器**：系统媒体会话集成、长句跑马灯滚动、Everything 搜索

#### 🐛 修复

- **歌词**：换句退场 Ghost 钉在旧行真实滚动位置（长句不再先跳回句首再淡出）
- **歌词**：Ghost 行继承配置字体（CJK 回退字体的合成加粗曾使行1 看起来像 ExtraBold）
- **歌词**：hover 期间歌词 tick 不再反复触发双行推挤

#### 📖 文档

- 中英双语 README，Windows 10 兼容性说明

#### ⚙️ 其他

- 开源准备：CI 工作流、关于页、MIT 许可证元数据

> 注：Release 产物为 self-contained 单文件可执行程序，无需安装 .NET 运行时。

---

### English

Initial public release.

#### ✨ Features

- **lyrics**: 3 display modes (single line / two-line / follow-scroll), 8 line-change transitions (none / fade / slide / zoom / blur / blur-zoom / push / two-line block push)
- **lyrics**: PushPair two-line block push — old block pushes out the window top while the new block pushes in (SPlayer transition-group semantics, 400ms CubicEase EaseInOut)
- **lyrics**: per-line exit animation (120ms fade + 3px rise) paired with 180ms entrances for Fade/Slide/Zoom/Blur/BlurZoom
- **lyrics**: translation toggle, ±2s lyric offset with 100ms step
- **lyrics**: hover swaps to title/artist with a plain fade and freezes there
- **settings**: settings window rewritten with Wpf.Ui (WPF Gallery) components, including an About page
- **player**: media session integration, marquee scrolling for long lines, Everything search

#### 🐛 Bug Fixes

- **lyrics**: exit-transition ghost pinned to the old line's actual scroll offset (long lines no longer jump back to the line start before fading out)
- **lyrics**: ghost rows inherit the configured FontFamily (CJK fallback synthetic bold made line 1 render ExtraBold)
- **lyrics**: hover no longer re-triggers PushPair on every lyric tick

#### 📖 Documentation

- Bilingual README (English + 简体中文), Windows 10 compatibility notes

#### ⚙️ Miscellaneous

- Open-source readiness: CI workflow, About page, MIT license metadata

> Note: release builds are self-contained single-file executables — no .NET runtime installation required.
