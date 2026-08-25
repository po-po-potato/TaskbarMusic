using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace TaskbarMusic;

/// <summary>
/// LRC 歌词解析器：把 [mm:ss.xx]文本 拆成时间-文本对，并支持按当前进度查找当前行
/// </summary>
public static class LrcParser
{
    // 支持 [00:00.00] / [00:00.000] / [00:00:00] / [00:00] 等
    private static readonly Regex TimeTagRegex = new(@"\[(\d{1,2}):(\d{1,2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);

    public class LrcLine
    {
        public TimeSpan Time;
        public string Text = "";
    }

    /// <summary>解析 LRC 文本为按时间排序的歌词行列表</summary>
    public static List<LrcLine> Parse(string lrc)
    {
        var lines = new List<LrcLine>();
        if (string.IsNullOrWhiteSpace(lrc)) return lines;

        foreach (var rawLine in lrc.Split('\n'))
        {
            var line = rawLine.Trim('\r', '\n', ' ', '\t');
            if (string.IsNullOrEmpty(line)) continue;

            var matches = TimeTagRegex.Matches(line);
            if (matches.Count == 0) continue;

            // 把所有时间标签后面的文字（最后一个标签之后的那一段）当歌词
            var lastMatch = matches[matches.Count - 1];
            var text = line.Substring(lastMatch.Index + lastMatch.Length).Trim();
            if (string.IsNullOrEmpty(text)) continue; // 空行（含 [ti]/[ar] 这种元数据）跳过

            foreach (Match m in matches)
            {
                int min = int.Parse(m.Groups[1].Value);
                int sec = int.Parse(m.Groups[2].Value);
                int ms = 0;
                if (m.Groups[3].Success)
                {
                    var msStr = m.Groups[3].Value;
                    if (msStr.Length == 1) ms = int.Parse(msStr) * 100;
                    else if (msStr.Length == 2) ms = int.Parse(msStr) * 10;
                    else ms = int.Parse(msStr.Substring(0, 3));
                }
                lines.Add(new LrcLine
                {
                    Time = new TimeSpan(0, 0, min, sec, ms),
                    Text = text
                });
            }
        }

        lines.Sort((a, b) => a.Time.CompareTo(b.Time));
        return lines;
    }

    /// <summary>
    /// 在已排序的歌词列表中找出当前应该显示的行（≤ 当前时间的最后一行）。
    /// 找不到返回 null（比如还没到第一句）。
    /// </summary>
    public static LrcLine? FindCurrent(List<LrcLine> lines, TimeSpan now)
    {
        int idx = FindCurrentIndex(lines, now);
        return idx >= 0 ? lines[idx] : null;
    }

    /// <summary>
    /// 同 FindCurrent 但返回行索引（-1 表示没找到）。用于取"下一行时间戳"
    /// 计算当前行的剩余显示时长（跑马灯按此定速）。
    /// </summary>
    public static int FindCurrentIndex(List<LrcLine> lines, TimeSpan now)
    {
        if (lines == null || lines.Count == 0) return -1;
        // 二分查找
        int lo = 0, hi = lines.Count - 1, ans = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (lines[mid].Time <= now)
            {
                ans = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return ans;
    }

    /// <summary>
    /// 翻译对齐：在翻译行列表中找时间戳与原文行一致的翻译。
    /// 网易云 tlyric 的时间戳与原文逐行对齐，精确匹配足够；
    /// 找不到返回 null（该句无翻译）。
    /// </summary>
    public static string? FindTranslation(List<LrcLine> translated, TimeSpan time)
    {
        if (translated == null || translated.Count == 0) return null;
        foreach (var line in translated)
        {
            if (line.Time == time) return line.Text;
            if (line.Time > time) break; // 已排序，后面更不可能
        }
        return null;
    }

    /// <summary>
    /// 双语合并 LRC 检测与拆分：网易云不少歌曲的 lrc.lyric 把原文和翻译合并成
    /// 同时间戳的交替行（[原1, 译1, 原2, 译2...]）。E 模式会把翻译行误当"下一句"，
    /// 导致每次换句触发两次滚动（日→中→日），视觉混乱。
    /// 判定：同时间戳相邻行占比过半 → 双语合并。拆分：主序列=每时间戳第一行（原文），
    /// 副序列=每时间戳其余行（翻译）。
    /// 返回 true=已拆分（primary/translation 有效）；false=非双语（输出为原样+空）。
    /// </summary>
    public static bool TrySplitBilingual(List<LrcLine> lines,
        out List<LrcLine> primary, out List<LrcLine> translation)
    {
        primary = new List<LrcLine>();
        translation = new List<LrcLine>();
        if (lines == null || lines.Count < 4) return false;

        int dupCount = 0;
        for (int i = 1; i < lines.Count; i++)
        {
            if (lines[i].Time == lines[i - 1].Time) dupCount++;
        }
        if (dupCount * 2 < lines.Count) return false; // 不足一半同时间戳，非双语合并

        // 按时间戳分组：第一行进主序列，其余进翻译序列
        int k = 0;
        while (k < lines.Count)
        {
            var t = lines[k].Time;
            primary.Add(lines[k]);
            k++;
            while (k < lines.Count && lines[k].Time == t)
            {
                translation.Add(lines[k]);
                k++;
            }
        }
        return true;
    }
}
