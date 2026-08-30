using System.Text.RegularExpressions;

namespace PersonalMediaManager.Domain.Aggregates.ParseTasks;

/// <summary>剧名线索探测（纯函数）：文件名与各级路径段剥离技术噪音后是否还剩可识别剧名文本</summary>
/// <remarks>
/// 用于「免 AI 拦截 ④」：文件名与<b>所有</b>路径段去掉 发布组标签 / 季集标记 / 画质编码音频来源等技术词 / 纯数字 / hash 后，
/// 若全部剥空（无 CJK、无长度≥3 的连续拉丁词），说明输入根本没有作品名——AI 兜底也无从下手，宜直接转人工省一次升级链。
/// <para><b>保守优先</b>：判定偏向「有线索」——只要文件名或任一路径段剥离后还剩实质文本就返回 false（照常走 AI）；
/// 漏剥某技术词是安全方向（剩文本 → 走 AI），只有把真剧名误当噪音剥掉才危险，而剧名不匹配这些技术模式，故安全。
/// 调用方须再叠加「TMDB 全零候选」双保险后才可据此转人工。</para>
/// </remarks>
public static partial class MediaTitleClue
{
    /// <summary>文件名（去扩展名）与所有路径段剥离技术噪音后是否<b>全部</b>无实质剧名文本</summary>
    public static bool HasNoTitleClue(string fileName, IReadOnlyList<string>? segments)
    {
        if (HasSubstance(StripExtension(fileName))) return false;
        if (segments is not null)
            foreach (string s in segments)
                if (HasSubstance(s)) return false;
        return true;
    }

    /// <summary>去掉扩展名（仅当点在末尾 5 字符内，避免误伤剧名里的点）</summary>
    private static string StripExtension(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot > 0 && name.Length - dot <= 5 ? name[..dot] : name;
    }

    /// <summary>剥离 发布组/hash 括号段 → 季集标记 → 技术词 → 长 hex，剩余交 HasSubstance 判定</summary>
    private static string StripNoise(string s)
    {
        s = s.Replace('_', ' ').Replace('.', ' ');  // _ . 归一为空格（修 \b 词边界：S01E06_4K 之间本无边界剥不掉；- + 保留给 WEB-DL / H-264 等技术词）
        s = BracketRe().Replace(s, " ");   // [..] 【..】 (..)：发布组 / CRC / 备注
        s = SeasonEpRe().Replace(s, " ");  // SxxExx / EP12 / 第 N 季集话期
        s = TechRe().Replace(s, " ");      // 画质 / 编码 / 音频 / 来源 / 字幕语言 / 容器
        s = HexRe().Replace(s, " ");       // 8+ 位 hex（CRC / hash）
        return s;
    }

    /// <summary>是否含实质剧名文本：原串含任意 CJK（中/日/韩）即算——剧名常在发布组括号内（如「[组名][机动战士高达][01]」），
    /// 不能先剥括号把它丢掉，故 CJK 在剥离前对原串判定；纯拉丁串才剥离技术噪音后看有无长度≥3 的连续拉丁字母词</summary>
    private static bool HasSubstance(string s)
    {
        foreach (char c in s)
        {
            // CJK 统一表意 / 日文平假名片假名 / 韩文音节
            if ((c >= 0x4E00 && c <= 0x9FFF) || (c >= 0x3040 && c <= 0x30FF) || (c >= 0xAC00 && c <= 0xD7A3))
                return true;
        }
        return LatinWordRe().IsMatch(StripNoise(s));
    }

    [GeneratedRegex(@"[\[\(【].*?[\]\)】]")]
    private static partial Regex BracketRe();

    [GeneratedRegex(@"(?i)\bS\d{1,3}(E\d{1,4}(-?E?\d{1,4})?)?\b|\bE\d{1,4}\b|\bEP\d{1,4}\b|第\s*\d+\s*[季集話话期部]")]
    private static partial Regex SeasonEpRe();

    [GeneratedRegex(@"(?i)\b(4K|8K|2160P|1080P|720P|480P|\d{2,3}FPS|HEVC|AVC|H\.?26[45]|X26[45]|MAIN10P?|10BIT|8BIT|HDR10?|SDR|DV|AAC|FLAC|DTS(-?HD)?|AC3|EAC3|TRUEHD|OPUS|BDRIP|BLURAY|BDMV|WEB-?DL|WEBRIP|HDTV|REMUX|HQ|HD|UHD|MP4|MKV|AVI|GB|JPSC|CHS|CHT|BIG5|GBK|SC|TC|简繁?|繁体?)\b")]
    private static partial Regex TechRe();

    [GeneratedRegex(@"\b[0-9A-Fa-f]{8,}\b")]
    private static partial Regex HexRe();

    [GeneratedRegex(@"[A-Za-z]{3,}")]
    private static partial Regex LatinWordRe();
}
