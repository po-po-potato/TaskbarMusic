# TaskbarMusic

**[English](README.md) | 简体中文**

![License](https://img.shields.io/badge/license-MIT-blue)
![Platform](https://img.shields.io/badge/platform-Windows%2010%20%2B-lightgrey)
![.NET](https://img.shields.io/badge/.NET-9-purple)

一个直接嵌入 Windows 任务栏的轻量音乐控制器 + 实时歌词显示。

## 它能做什么

TaskbarMusic 住在你的任务栏里——没有悬浮窗、没有额外的 Dock。通过 Windows SMTC 跟随系统里正在播放的任何音乐（Spotify、网易云音乐、QQ音乐、浏览器、本地播放器……），并把当前歌词直接渲染在任务栏上。

### 功能特性

- **任务栏内嵌**——通过 `SetParent` 嵌入 `Shell_TrayWnd`，explorer 重启 / DPI 变化 / 切换显示器都能自动恢复
- **全系统媒体跟随**——基于 SMTC，任何向 Windows 媒体控制上报的播放器都能识别
- **五种歌词显示模式**
  - **A** — 歌名 + 当前歌词
  - **B** — 歌词在上，歌名在下
  - **C** — 纯歌词（大字号单行）
  - **D** — 双语歌词：原文 + 翻译
  - **E** — Apple Music 式跟随模式：当前句 + 下一句预览，换句时平滑垂直滚动
- **跑马灯滚动**——超宽歌词行三段式滚动（句首停留 → 匀速滚动 → 句尾停留，与歌词时间轴对齐）
- **歌词来源**——网易云 API + LRCLIB 兜底；全局偏移微调（±10 秒）
- **背景跟随封面**——从专辑封面提取主色调
- **播控按钮**——鼠标悬停显示 播放/暂停/上一首/下一首
- **Fluent 设置窗口**——.NET 9 Fluent 主题、深浅色跟随系统、背景材质可切换（纯色 / Mica / 亚克力）
- **字体自定义**——任意已安装字体与字号

### 性能

为同类最轻而生：单进程、约 170 MB 工作集、无 WebView、无后台服务。

## 系统要求

- Windows 10 1809+ 或 Windows 11（x64）
- 下载 Release 版无需安装 .NET（自带运行时）；自行构建需要 .NET 9 SDK

> **Windows 10 注意事项**：核心功能（任务栏嵌入 / 媒体跟随 / 歌词）在 Win10 上均可使用，但设置窗的 Mica / 亚克力背景材质需要 **Windows 11 22H2+**，Win10 上会自动降级为纯色。此外项目主要在 Windows 11 上开发测试，Win10 兼容性理论上成立但未充分实测。

## 下载

前往 [Releases](https://github.com/po-po-potato/TaskbarMusic/releases) 下载 `TaskbarMusic.exe`（自包含单文件，约 187 MB，无需任何依赖，下载即用）。

首次运行可能触发 SmartScreen 警告（未签名）——点「更多信息」→「仍要运行」。

## 构建

```bash
git clone https://github.com/po-po-potato/TaskbarMusic.git
cd TaskbarMusic
./build.bat        # 或：dotnet build -c Release
```

构建脚本每次用唯一的 `BUILD_ID` 作为中间目录（见 `Directory.Build.props`），避免中间产物目录被锁。

## 路线图

TaskbarMusic 专注于音乐体验。模块化的多功能工具（番茄钟、天气、搜索……）未来可能以独立项目、独立名字单独发布。

## License

[MIT](LICENSE)
