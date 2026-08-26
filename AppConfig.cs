using System;
using System.IO;
using System.Text.Json;

namespace TaskbarMusic;

/// <summary>
/// 歌词展示模式（2026-08-26 精简：删 B/SwapTitleArtist 与 A 只是内容互换使用率低；
/// 删 D/Translate 改为 ShowTranslation 独立复选框叠加到任意模式。
/// 枚举值号保留防旧 config 数字错位）
/// UI 显示名按内容行数命名：ReplaceArtist="歌名+歌词"、LyricOnly="单行"、Follow="双行"。
/// </summary>
public enum LyricDisplayMode
{
    /// <summary>歌名+歌词：上行歌名不动，歌词替换艺术家行（默认）</summary>
    ReplaceArtist = 0,

    // 1 = 原 SwapTitleArtist（已删）

    /// <summary>单行：只显示歌词，单行垂直居中、大字号</summary>
    LyricOnly = 2,

    // 3 = 原 Translate（已删，并入 ShowTranslation）

    /// <summary>双行：当前句+下一句预览，换句垂直滚动（Apple Music 式）</summary>
    Follow = 4,
}

/// <summary>换句过渡效果（行级；E 模式的垂直滚动是模式自带过渡，不在此列）</summary>
public enum LineTransition
{
    /// <summary>硬切（V1 原行为）</summary>
    None = 0,

    /// <summary>淡入：150ms opacity 0→1</summary>
    Fade = 1,

    /// <summary>上滑：150ms 淡入 + 从下方 3px 滑入</summary>
    Slide = 2,
}

/// <summary>设置窗背景材质（Win11 22H2+ DWM backdrop；切换后重开设置窗生效）</summary>
public enum WindowBackdrop
{
    /// <summary>纯色：Fluent 主题默认不透明背景，零 DWM 开销，resize 最流畅</summary>
    None = 0,

    /// <summary>Mica：采样壁纸着色。25H2 的 WPF 窗口上仅激活态渲染，深色桌面下很微妙</summary>
    Mica = 1,

    /// <summary>Acrylic：实时模糊背景，效果最明显但 resize 有开销</summary>
    Acrylic = 2,
}

/// <summary>
/// 应用配置：保存到 %APPDATA%\TaskbarMusic\config.json
/// </summary>
public class AppConfig
{
    /// <summary>窗口距任务栏左边缘的横向偏移（设备无关像素 DIP）</summary>
    public double OffsetX { get; set; } = 200;

    /// <summary>窗口宽度（DIP）</summary>
    public double Width { get; set; } = 360;

    /// <summary>窗口高度（DIP），通常与任务栏一致；运行时被任务栏实际高度覆盖</summary>
    public double Height { get; set; } = 40;

    /// <summary>背景色是否跟随封面主色调</summary>
    public bool BackgroundFollowCover { get; set; } = true;

    /// <summary>歌名/艺术家文字的字体名（系统已装的字体名）</summary>
    public string FontFamily { get; set; } = "Segoe UI";

    /// <summary>歌名字号</summary>
    public double TitleFontSize { get; set; } = 16;

    /// <summary>艺术家字号</summary>
    public double ArtistFontSize { get; set; } = 13;

    /// <summary>是否显示当前歌词（找不到歌词时回退显示艺术家）</summary>
    public bool ShowLyric { get; set; } = true;

    /// <summary>歌词展示模式（仅当 ShowLyric=true 时生效）</summary>
    public LyricDisplayMode LyricMode { get; set; } = LyricDisplayMode.ReplaceArtist;

    /// <summary>显示翻译（叠加项）：仅单行模式生效——原文下方追加翻译行，
    /// 取代原 D 模式（D 本质就是"原文/翻译"两行）。
    /// 其余模式两行已满塞翻译必挤掉原生内容，2026-08-26 定稿不做</summary>
    public bool ShowTranslation { get; set; } = false;

    /// <summary>"只显示歌词"模式下的字号（默认放大到歌名同级）</summary>
    public double LyricOnlyFontSize { get; set; } = 22;

    /// <summary>
    /// 暂停淡出补偿（秒）：点击暂停后音频还会淡出播放这段时间才真正停止，
    /// 但 SMTC 的 Paused 事件在淡出开始就到了——冻结位置需加上该补偿，
    /// 否则每次暂停歌词落后一点、多次累积。0=无淡出的播放器。
    /// </summary>
    public double PauseFadeOutSec { get; set; } = 0;

    /// <summary>歌词全局偏移（毫秒）：正值延后、负值提前，100ms 步进微调与演唱对齐。
    /// 2026-08-26 由 LyricOffsetSec（秒）迁移而来——Load 里做旧值换算</summary>
    public int LyricOffsetMs { get; set; } = 0;

    /// <summary>[已废弃] 旧单位秒的歌词偏移，仅作 Load 迁移读入，不再使用</summary>
    public double LyricOffsetSec { get; set; } = 0;

    /// <summary>换句过渡效果（C11）；默认淡入</summary>
    public LineTransition LineTransition { get; set; } = LineTransition.Fade;

    /// <summary>设置窗背景材质（提议做成设置项）；默认纯色</summary>
    public WindowBackdrop WindowBackdrop { get; set; } = WindowBackdrop.None;

    /// <summary>设置窗宽度（DIP）——关闭时保存实际值，打开时恢复（记忆用户拖动）</summary>
    public double SettingsWindowWidth { get; set; } = 1000;

    /// <summary>设置窗高度（DIP）——关闭时保存实际值，打开时恢复</summary>
    public double SettingsWindowHeight { get; set; } = 560;

    private static string ConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskbarMusic");

    private static string ConfigPath => Path.Combine(ConfigDir, "config.json");

    public static AppConfig Load()
    {
        AppConfig cfg = new();
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                cfg = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
            }
        }
        catch { /* 配置坏了就回默认值 */ }

        // 一次性迁移：旧 LyricOffsetSec（秒）→ LyricOffsetMs（毫秒）
        if (cfg.LyricOffsetMs == 0 && cfg.LyricOffsetSec != 0)
        {
            cfg.LyricOffsetMs = (int)(cfg.LyricOffsetSec * 1000);
            cfg.LyricOffsetSec = 0;
        }
        return cfg;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
        }
        catch { /* 保存失败不影响运行 */ }
    }
}
