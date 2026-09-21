# 更新日志 / Changelog

所有显著变更记录于此文件 / All notable changes will be documented in this file.
格式基于 [Keep a Changelog](https://keepachangelog.com/)，由 [Conventional Commits](https://www.conventionalcommits.org/) 生成。

## [v1.1.0] - 2026-09-21

### 中文

模块池大扩容：任务栏条从"音乐播放器"进化为"滚轮切换的多模块小显示屏"。

#### ✨ 新增

- **轮播（E6）**：滚轮切换模块（上=前一个 / 下=后一个，循环），120ms 纯淡入无位移；启用模块全部常驻运行（滚走不停服务）；当前模块落盘，重启恢复
- **番茄钟**：专注/休息循环、单击启停、双击重置、右键快速倒计时（1/5/10/25/45 分钟）；暂停态空心圆点视觉降级；到点闪色提醒；hover 浮层显示今日完成与阶段起止；条内布局按交互稿改版（左聚拢，低频计数下沉浮层）
- **天气**：Open-Meteo 数据源（免费无 key），当前天气 + 今明后三天预报；WMO 天气码映射 10 类内置矢量图标（晴/少云/多云/阴/雾/毛毛雨/雨/阵雨/雪/雷）；设置里城市搜索（geocoding 中文结果点选）；30 分钟刷新，断网显示缓存
- **财经**：自选股单股静态轮播（行情 5s 轮询与展示 8s 轮换双节奏解耦，不滚动）；当日分时 sparkline 随涨跌着色（红涨绿跌，中国行情惯例）；价格变动闪色；hover 暂停轮换 + 自选股列表；设置支持名称/拼音/代码搜索添加（腾讯 smartbox，权证自动过滤）
- **网速**：1 秒差分采样（IP Helper 路线，与 TrafficMonitor 同源）；多网卡粘性选择 + 手动锁定；今日累计流量跨重启持久化；显示风格可设置（小数位 / 单位制式：字节·比特·隐藏 / 箭头：实心·线条·无）
- **倒数日**：纪念日与手动倒计时事件合并承载（不接系统日历）；条内显示最近一项，天数颜色分层（>7 天白 / ≤7 天蓝 / 当天高亮）；每年重复项自动滚到下一次
- **多显示器（A7）**：多屏各挂一条、独立拖动、宽度全局共享；跨条状态同步（任何一条上的操作全局生效）；托盘与设置窗进程级管理（宿主条销毁不关窗）
- **浮层框架（E5）**：模块 hover 浮层统一机制（不抢焦点 / 离开 300ms 宽限收 / 滚轮切走立即收 / 锚定条上方）
- **设置**：深浅色模式（跟随系统/浅色/深色）+ 背景材质（纯色/Mica/亚克力）；每模块独立设置分区 + 导航图标；设置窗宽高记忆

#### 🐛 修复

- **滚轮卡住**：透明底模块（网速/倒数日等）空白区滚轮事件穿透丢失——分层窗口 alpha=0 像素被 Win32 hit-test 跳过；壳层根容器垫 #01FFFFFF 一处兜底所有透明模块
- **设置窗跳回常规**：显示器轮询每 5s 无条件广播导致分区被重置踢回首项；改为真实结构变化才广播 + 重建后恢复原选中分区
- **网速抖动**：同一物理网卡多绑定条目（VirtualBox NDIS 等）导致采样目标每秒来回切换、速率反复归零；改为粘性选择
- **条背景**：Wpf.Ui 主题应用对条窗口的背景污染防护

> 注：财经数据源为腾讯行情接口（非官方授权），合规结论出来前请自用。Release 产物为 self-contained 单文件可执行程序，无需安装 .NET 运行时。

---

### English

Module pool expansion: the bar evolves from a music widget into a wheel-switchable multi-module display.

#### ✨ Features

- **carousel (E6)**: wheel to switch modules (up = previous / down = next, looping), 120ms pure fade without displacement; all enabled modules stay resident (services keep running when scrolled away); active module persisted across restarts
- **pomodoro**: focus/break cycle, click to start/pause, double-click to reset, right-click quick timer (1/5/10/25/45 min); paused state with hollow-dot visual downgrade; flash alert on completion; hover overlay with today's count and phase start-end; bar layout per interaction spec (left-aligned, low-frequency stats moved to overlay)
- **weather**: Open-Meteo (free, no key), current + 3-day forecast; WMO code mapped to 10 built-in vector icons; city search in settings (geocoding); 30min refresh with offline cache
- **stocks**: single-stock static carousel (5s quote poll decoupled from 8s display rotation, no scrolling); intraday sparkline colored by change (red up / green down, CN convention); price-change flash; hover pauses rotation + watchlist; add by name/pinyin/code search (Tencent smartbox, warrants filtered)
- **netspeed**: 1s differential sampling (IP Helper, TrafficMonitor-aligned); sticky adapter selection + manual lock; today's traffic persisted; display style settings (decimal places / unit mode: bytes·bits·hidden / arrow: solid·outline·none)
- **countdown**: anniversaries + manual events in one carrier (no system calendar); bar shows nearest item with day-count color tiers (>7d white / <=7d blue / today highlighted); annual items roll to next occurrence
- **multi-monitor (A7)**: one bar per monitor, independent drag, globally shared width; cross-bar state sync (actions on any bar apply everywhere); tray and settings window managed at process level
- **overlay framework (E5)**: unified hover-popover mechanics for modules (no focus steal / 300ms grace close / instant close on wheel-away / anchored above the bar)
- **settings**: theme mode (system/light/dark) + backdrop (solid/Mica/acrylic); per-module settings sections with nav icons; window size memory

#### 🐛 Bug Fixes

- **stuck wheel**: transparent-background modules lost wheel events on blank areas — layered windows skip alpha=0 pixels in Win32 hit-testing; shell root now carries #01FFFFFF covering all transparent modules in one place
- **settings tab reset**: the 5s monitor rescan broadcast unconditionally and kicked the settings window back to the first section; now only real structural changes broadcast, and the selected section is restored after rebuilds
- **netspeed flapping**: multiple binding entries on one NIC (VirtualBox NDIS etc.) made the sampling target flip every second, zeroing the rate; sticky selection fixes it
- **bar background**: protection against Wpf.Ui theme application polluting the bar window

> Note: stock data comes from unofficial Tencent endpoints — personal use until compliance is settled. Release artifact is a self-contained single-file executable; no .NET runtime install required.

---

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
