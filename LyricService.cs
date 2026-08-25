using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;

namespace TaskbarMusic;

/// <summary>歌词获取结果：原文 LRC + 翻译 LRC（可能为空）</summary>
public sealed record LyricResult(string Lrc, string? Translation)
{
    public bool HasLyric => !string.IsNullOrEmpty(Lrc);
}

/// <summary>
/// 歌词来源：网易云公开接口（主）+ LRCLIB 开放 API（兜底，免费无 key）。
/// 流程：search.163.com 搜歌名+艺术家 → 取 song.id → song/lyric 接口拿 LRC + tlyric（翻译）；
/// 网易云未命中 → LRCLIB /api/search 取 syncedLyric。
/// </summary>
public class LyricService
{
    private static readonly HttpClient Http = CreateHttpClient();
    private readonly ConcurrentDictionary<string, LyricResult> _cache = new();

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
        client.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");
        client.DefaultRequestHeaders.Add("Referer", "https://music.163.com/");
        return client;
    }

    /// <summary>拉取指定歌曲的歌词（已包含本地缓存）。失败返回空结果。</summary>
    public async Task<LyricResult> FetchLyricAsync(string title, string artist)
    {
        if (string.IsNullOrWhiteSpace(title)) return new LyricResult("", null);

        var key = $"{title}__{artist}".ToLowerInvariant();
        if (_cache.TryGetValue(key, out var cached)) return cached;

        LyricResult result = new("", null);
        try
        {
            result = await FetchFromNeteaseAsync(title, artist);
        }
        catch { /* 单源失败不阻断兜底 */ }

        if (!result.HasLyric)
        {
            try
            {
                result = await FetchFromLrclibAsync(title, artist);
            }
            catch { }
        }

        _cache[key] = result;
        return result;
    }

    // ===== 来源一：网易云（含翻译 tlyric）=====

    private async Task<LyricResult> FetchFromNeteaseAsync(string title, string artist)
    {
        var keyword = string.IsNullOrWhiteSpace(artist) ? title : $"{title} {artist}";
        var searchUrl = $"https://music.163.com/api/search/get?s={HttpUtility.UrlEncode(keyword)}&type=1&limit=10";
        var searchJson = await Http.GetStringAsync(searchUrl);

        long? songId = ExtractBestSongId(searchJson, title, artist);
        if (songId == null) return new LyricResult("", null);

        var lyricUrl = $"https://music.163.com/api/song/lyric?id={songId}&lv=1&kv=1&tv=-1";
        var lyricJson = await Http.GetStringAsync(lyricUrl);

        var lrc = ExtractField(lyricJson, "lrc");
        var tlyric = ExtractField(lyricJson, "tlyric");
        return new LyricResult(lrc ?? "", tlyric);
    }

    /// <summary>从 song/lyric 响应 JSON 中取 {lrc:{lyric:"..."}} / {tlyric:{lyric:"..."}} 字段</summary>
    private static string? ExtractField(string json, string rootField)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(rootField, out var obj) &&
                obj.TryGetProperty("lyric", out var lyric) &&
                lyric.ValueKind == JsonValueKind.String)
            {
                return lyric.GetString();
            }
        }
        catch { }
        return null;
    }

    private static long? ExtractBestSongId(string json, string wantTitle, string wantArtist)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("result", out var result)) return null;
            if (!result.TryGetProperty("songs", out var songs)) return null;
            if (songs.ValueKind != JsonValueKind.Array || songs.GetArrayLength() == 0) return null;

            string normWantT = Normalize(wantTitle);
            string normWantA = Normalize(wantArtist);

            long? firstId = null;
            foreach (var song in songs.EnumerateArray())
            {
                if (firstId == null && song.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var fid))
                    firstId = fid;

                if (!song.TryGetProperty("name", out var nameEl)) continue;
                var name = nameEl.GetString() ?? "";

                string artistsJoined = "";
                if (song.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in artists.EnumerateArray())
                    {
                        if (a.TryGetProperty("name", out var an))
                            artistsJoined += (an.GetString() ?? "") + " ";
                    }
                }

                // 标题包含 + 艺术家任意命中即认为是匹配
                if (Normalize(name).Contains(normWantT) &&
                    (string.IsNullOrEmpty(normWantA) || Normalize(artistsJoined).Contains(normWantA)))
                {
                    if (song.TryGetProperty("id", out var matchId) && matchId.TryGetInt64(out var mid))
                        return mid;
                }
            }

            // 没有最佳匹配则退回第一条
            return firstId;
        }
        catch
        {
            return null;
        }
    }

    // ===== 来源二：LRCLIB 兜底（免费无 key；只有原文，无翻译）=====

    private async Task<LyricResult> FetchFromLrclibAsync(string title, string artist)
    {
        var url = $"https://lrclib.net/api/search?track_name={HttpUtility.UrlEncode(title)}" +
                  (string.IsNullOrWhiteSpace(artist) ? "" : $"&artist_name={HttpUtility.UrlEncode(artist)}");
        var json = await Http.GetStringAsync(url);

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return new LyricResult("", null);

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (item.TryGetProperty("syncedLyric", out var synced) &&
                synced.ValueKind == JsonValueKind.String &&
                !string.IsNullOrEmpty(synced.GetString()))
            {
                return new LyricResult(synced.GetString()!, null);
            }
        }
        return new LyricResult("", null);
    }

    private static readonly Regex NonAlphanumeric = new(@"[\s\-_\(\)\[\]\{\}「」【】《》'""!?,。，。、·:：;；]+", RegexOptions.Compiled);

    /// <summary>归一化：去空白和常见标点、转小写，便于宽松比对</summary>
    private static string Normalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return NonAlphanumeric.Replace(s, "").ToLowerInvariant();
    }
}
