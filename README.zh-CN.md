# TaskbarMusic

**[English](README.md) | 简体中文**

![License](https://img.shields.io/badge/license-MIT-blue)
![Platform](https://img.shields.io/badge/platform-Windows%2010%20%2B-lightgrey)
![.NET](https://img.shields.io/badge/.NET-9-purple)
![Build](https://github.com/po-po-potato/TaskbarMusic/actions/workflows/build.yml/badge.svg)

一个直接嵌入 Windows 任务栏的轻量音乐控制器 + 实时歌词显示。

## 它能做什么

TaskbarMusic 住在你的任务栏里——没有悬浮窗、没有额外的 Dock。通过 Windows SMTC 跟随系统里正在播放的任何音乐（Spotify、网易云音乐、QQ音乐、浏览器、本地播放器……），并把当前歌词直接渲染在任务栏上。

### 功能特性

- **任务栏内嵌**——通过 `SetParent` 嵌入 `Shell_TrayWnd`，explorer 重启 / DPI 变化 / 切换显示器都能自动恢复
- **全系统媒体跟随**——基于 SMTC，任何向 Windows 媒体控制上报的播放器都能识别
- **三种歌词展示模式**
  - **歌名 + 歌词**——上行歌名，下行当前句
  - **单行**——只显示当前句，大字号
  - **双行**——当前句 + 下一句预览，换句时 Apple Music 式平滑垂直滚动
- **七种换句过渡**——硬切 / 淡入 / 上滑 / 缩放 / 模糊 / 模糊缩放（Apple Music 风）/ 推挤（旧句推上、新句推入）
- **显示翻译**（单行模式）——歌词源含翻译数据时原文下方追加翻译行
- **跑马灯滚动**——超宽歌词行三段式滚动（句首停留 → 匀速滚动 → 句尾停留，与歌词时间轴对齐）
- **歌词来源**——网易云 API + LRCLIB 兜底；全局偏移微调（±2 秒，100ms 步进滑块）
- **背景跟随封面**——从专辑封面提取主色调
- **播控按钮**——鼠标悬停显示 播放/暂停/上一首/下一首
- **Fluent 设置窗口**——基于 [WPF UI](https://github.com/lepoco/wpfui)（WPF Gallery）组件构建：Win11 设置式左侧导航、深浅色实时跟随系统、背景材质可切换（纯色 / Mica / 亚克力）、记住窗口尺寸
- **字体自定义**——任意已安装字体与字号，实时预览

### 性能

为同类最轻而生：单进程、约 100 MB 工作集、无 WebView、无后台服务。

## 系统要求

- Windows 10 1809+ 或 Windows 11（x64）
- 下载 Release 版无需安装 .NET（自带运行时）；自行构建需要 .NET 9 SDK

> **Windows 10 注意事项**：核心功能（任务栏嵌入 / 媒体跟随 / 歌词）在 Win10 上均可使用，但设置窗的 Mica / 亚克力背景材质需要 **Windows 11 22H2+**，Win10 上会自动降级为纯色。此外项目主要在 Windows 11 上开发测试，Win10 兼容性理论上成立但未充分实测。

## 下载

前往 [Releases](https://github.com/po-po-potato/TaskbarMusic/releases) 下载最新版本。

首次运行可能触发 SmartScreen 警告（未签名）——点「更多信息」→「仍要运行」。

## 构建

```bash
git clone https://github.com/po-po-potato/TaskbarMusic.git
cd TaskbarMusic
dotnet build -c Release
```

需要 .NET 9 SDK。仓库里的 `build.bat` 是本地开发便利脚本（构建完自动启动）。

## 路线图

- 逐字卡拉OK高亮（YRC 逐字时间轴歌词）
- 间奏提示（纯音乐间奏期呼吸圆点）
- 多显示器副任务栏支持
- 歌词简繁转换

TaskbarMusic 专注于音乐体验。模块化的多功能工具（番茄钟、天气、搜索……）未来可能以独立项目、独立名字单独发布。

## License

[MIT](LICENSE)
