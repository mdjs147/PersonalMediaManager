using System.Linq;
using System.Text.Json;
using PersonalMediaManager.Application.Contracts;

namespace PersonalMediaManager.Infrastructure.External.Ai;

/// <summary>AI 提供商共用的提示词构造与 JSON 反解工具</summary>
/// <remarks>
/// 抽出为静态类（而非塞进 OpenAiCompatibleProviderBase）是因为 OllamaProvider 不走 OpenAI 协议但要复用同一套
/// system prompt 与 JSON 反解，避免「让 OllamaProvider 继承 OpenAI 基类」造成的强行耦合。
/// </remarks>
internal static class AiPromptHelpers
{
    /// <summary>瞬时错误判定阈值：首字节超时 &lt; 5s 视为瞬时（需求文档 §3.3.3）</summary>
    public static readonly TimeSpan TransientFirstByteThreshold = TimeSpan.FromSeconds(5);

    public const string SystemPrompt =
        "你是媒体文件名解析助手。仅输出 JSON：{\"title\":\"...\",\"year\":YYYY|null,\"type\":\"movie\"|\"tv\",\"season\":N|null,\"episode\":N|null,\"episodeEnd\":N|null,\"confidence\":0.0-1.0,\"aliases\":[\"...\"]}。" +
        "title 用中文优先，且必须非空——文件名 / 路径缺乏明确作品名时也要基于现有信息（父目录名、文件名主体、罗马音 / 拼音 / 代号）给出最佳猜测，并把 confidence 压到 0.3 以下；严禁返回空对象、空字符串或 title=null（那会被判为解析失败反而丢失你的判断）；" +
        "year 只从文件名 / 路径中明确写出的 4 位年份数字提取（如 (2026) / .2026. / 2026年）；文件名与路径都没有出现年份数字时必须填 null——严禁凭作品名用你已知的信息推断发行年：很多作品有多个年份的版本（重制 / 重启 / 多次剧场版），凭名字猜会锚定到错误版本（title 可基于线索翻译转写，但 year 属事实字段只能照抄文件里写的数字，不能补全）；type 在 movie/tv 二选一；" +
        "season/episode 仅 tv 有意义，movie 必须为 null；" +
        "若文件路径的某级目录名标明了季（如「Season 2」「S02」「第二季」「Specials」），以该目录季号为准，优先于文件名推断；" +
        "OVA / SP / 特典 / 特别篇 / OAD 等特别篇内容 season 输出 0（媒体库 Specials 季约定），episode 填特别篇序号；" +
        "episodeEnd 用于双集/多集合并文件（例 S01E08-E09 / [第08-09集] → episode=8, episodeEnd=9），单集为 null；" +
        "confidence 是你的把握 0~1；" +
        "aliases 是该作品用于检索的其它名称（最多 3 个，按检索价值排序）：优先原始语言名（日文原名 / 英文官方译名 / 罗马音 / 拼音），供中文 title 在影视库查不到时兜底匹配（国漫、日漫、冷门剧常只有原名条目）；仅填你确有把握的真实别名，没有就给空数组 []，严禁音译臆造或与 title 重复。" +
        "禁止输出解释、Markdown、代码块标记。";

    /// <summary>把解析入参分「文件名 / 文件路径 / 已有排查信息」三组构造 user prompt（空组省略）</summary>
    /// <remarks>
    /// 分组呈现让 AI 明确区分三类线索来源、各自可信度不同：
    /// - 文件名：主线索；
    /// - 文件路径：各级目录常含剧集名称与季号（如「剧名/Season 2」），价值高，配引导语提示 AI 重点参考、季目录优先于文件名推断季；
    /// - 已有排查信息：规则引擎初步提取，置信度低，仅作参考不可尽信。
    /// </remarks>
    public static string BuildUserPrompt(AiParseRequest request)
    {
        List<string> sections = [$"【文件名】\n{request.FileName}"];

        // 文件路径组：优先完整相对路径段（外层→内层）；缺省回退旧的单层父目录字段以兼容旧调用方
        List<string> pathLines = [];
        if (request.RelativeSegments is { Count: > 0 } segments)
        {
            for (int i = 0; i < segments.Count; i++)
            {
                // 段类型语义化：最外层 = 监控根的一级子目录；最内层 = 直接父目录
                string label = i == segments.Count - 1 ? "直接父目录" : $"上级目录-{segments.Count - i}";
                pathLines.Add($"{label}：{segments[i]}");
            }
        }
        else if (!string.IsNullOrWhiteSpace(request.ParentFolderName))
        {
            pathLines.Add($"父目录：{request.ParentFolderName}");
        }
        if (pathLines.Count > 0)
            sections.Add("【文件路径】（文件夹与各级目录常包含剧集名称与季号，如「剧名/Season 2」，请重点参考；季目录优先于文件名推断季）\n"
                + string.Join('\n', pathLines));

        // 已有排查信息组：规则引擎初判的 标题 / 年份 / 类型 / 季 / 集（低置信，仅供参考）——
        // 供 AI 对比修正而非从零判断（尤其 movie/tv 与季号：规则的数字模式匹配常比剧名识别更可靠，可与路径季目录交叉验证）
        List<string> hintLines = [];
        if (!string.IsNullOrWhiteSpace(request.RuleHintTitle)) hintLines.Add($"标题：{request.RuleHintTitle}");
        if (request.RuleHintYear.HasValue) hintLines.Add($"年份：{request.RuleHintYear}");
        if (!string.IsNullOrWhiteSpace(request.RuleHintType) && request.RuleHintType != "unknown")
            hintLines.Add($"类型：{request.RuleHintType}");
        if (request.RuleHintSeason.HasValue) hintLines.Add($"季：{request.RuleHintSeason}");
        if (request.RuleHintEpisode.HasValue)
        {
            string ep = request.RuleHintEpisodeEnd is int end && end > request.RuleHintEpisode
                ? $"{request.RuleHintEpisode}-{end}"
                : request.RuleHintEpisode.Value.ToString();
            hintLines.Add($"集：{ep}");
        }
        if (hintLines.Count > 0)
            sections.Add("【已有排查信息】（规则引擎初步提取，置信度低，仅供参考；如与文件名 / 路径矛盾以你的判断为准）\n"
                + string.Join('\n', hintLines));

        return string.Join("\n\n", sections);
    }

    /// <summary>把 BaseUrl 与协议固定路径拼成最终请求 URL，<b>智能去重重叠路径段</b>（容忍用户多填 /v1）</summary>
    /// <remarks>
    /// 用户常把 BaseUrl 多填了路径（如 <c>https://api.anthropic.com/v1</c> 甚至整条 <c>.../v1/messages</c>），
    /// 朴素拼接会得到 <c>/v1/v1/messages</c> 而 404。此处按「路径段重叠」去重：
    /// 若 BaseUrl 路径末尾若干段恰是 <paramref name="path"/> 的开头段（大小写不敏感，兼容 <c>/V1</c>），
    /// 则剥掉 BaseUrl 的重叠尾段、改用 path 的规范段（顺带把 <c>/V1</c> 纠正为 <c>/v1</c>）。示例：
    ///   <list type="bullet">
    ///     <item><c>https://api.anthropic.com</c> + <c>/v1/messages</c> → <c>https://api.anthropic.com/v1/messages</c></item>
    ///     <item><c>https://api.anthropic.com/v1</c> + <c>/v1/messages</c> → <c>https://api.anthropic.com/v1/messages</c></item>
    ///     <item><c>https://api.anthropic.com/v1/messages</c> + <c>/v1/messages</c> → <c>https://api.anthropic.com/v1/messages</c></item>
    ///     <item><c>http://host/relay/v1</c> + <c>/v1/messages</c> → <c>http://host/relay/v1/messages</c>（保留中转前缀）</item>
    ///     <item><c>https://relay.com/v2</c> + <c>/v1/chat/completions</c> → <c>https://relay.com/v2/chat/completions</c>（版本槽合并：保用户 v2 不强塞 v1）</item>
    ///   </list>
    /// 仅对「BaseUrl 末尾 == path 开头」的连续段去重，不跨段乱删；并对「两端均为版本号段但不同」做版本槽合并（用户版本优先，
    /// 兼顾「很多第三方要求带特定 /vN」）；BaseUrl 非合法绝对 URL 时退回朴素拼接（不冒险）。
    /// </remarks>
    public static string JoinUrl(string baseUrl, string path)
    {
        string trimmed = (baseUrl ?? string.Empty).TrimEnd('/');
        string p = path.StartsWith('/') ? path : "/" + path;

        // 非标准绝对 URL（少见）：退回朴素拼接，行为不变，不冒险改写
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri))
            return trimmed + p;

        string authority = uri.GetLeftPart(UriPartial.Authority);
        string[] baseSegs = SplitSegments(uri.AbsolutePath);
        string[] pathSegs = SplitSegments(p);

        // 版本槽合并：BaseUrl 末段与 path 首段都是「版本号段」(v1/v2/v3/v1beta…) 但不相等时，视为同一版本槽——
        // 保留用户显式版本（很多第三方要求带特定 /vN，如 /v2 或 /openai/v1），仅追加 path 余下的操作段，绝不强塞 /v1。
        if (baseSegs.Length > 0 && pathSegs.Length > 0
            && !string.Equals(baseSegs[^1], pathSegs[0], StringComparison.OrdinalIgnoreCase)
            && IsVersionSegment(baseSegs[^1]) && IsVersionSegment(pathSegs[0]))
            return BuildUrl(authority, baseSegs.Concat(pathSegs.Skip(1)));

        // 路径段重叠去重：BaseUrl 末尾若干段恰是 path 开头段（含同名版本段 /v1+/v1），剥掉重叠、用 path 规范段（顺带纠正 /V1→/v1）
        int overlap = TailHeadOverlap(baseSegs, pathSegs);
        return BuildUrl(authority, baseSegs.Take(baseSegs.Length - overlap).Concat(pathSegs));
    }

    /// <summary>把 authority 与路径段拼成 URL（空段则只返回 authority）</summary>
    private static string BuildUrl(string authority, IEnumerable<string> segs)
    {
        string merged = string.Join('/', segs);
        return merged.Length == 0 ? authority : authority + "/" + merged;
    }

    /// <summary>是否「版本号段」：v + 数字 起头（v1 / v2 / v10 / v1beta…），用于「版本槽合并」保留用户显式版本</summary>
    private static bool IsVersionSegment(string seg) =>
        seg.Length >= 2 && (seg[0] is 'v' or 'V') && char.IsDigit(seg[1]);

    /// <summary>按 '/' 切非空路径段</summary>
    private static string[] SplitSegments(string path) =>
        path.Split('/', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>求 BaseUrl 末尾与 path 开头连续相等的最大段数（大小写不敏感），用于去重重叠路径</summary>
    private static int TailHeadOverlap(string[] baseSegs, string[] pathSegs)
    {
        int max = Math.Min(baseSegs.Length, pathSegs.Length);
        for (int k = max; k >= 1; k--)
        {
            bool match = true;
            for (int i = 0; i < k && match; i++)
                match = string.Equals(baseSegs[baseSegs.Length - k + i], pathSegs[i], StringComparison.OrdinalIgnoreCase);
            if (match) return k;
        }
        return 0;
    }

    /// <summary>把 AI 文本里的 JSON 反解到 AiParseResult；容忍 Markdown 代码块包裹</summary>
    public static AiParseResult ParseContent(string content)
    {
        string json = StripMarkdownFences(content).Trim();
        if (string.IsNullOrWhiteSpace(json))
            throw new AiProviderLogicalException("AI 返回 content 为空");

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            string? title = GetStr(root, "title");
            string? type = GetStr(root, "type");
            int? year = TryGetInt(root, "year");
            int? season = TryGetInt(root, "season");
            int? episode = TryGetInt(root, "episode");
            int? episodeEnd = TryGetInt(root, "episodeEnd");
            double confidence = TryGetDouble(root, "confidence") ?? 0;
            IReadOnlyList<string>? aliases = TryGetSearchAliases(root, title);

            if (string.IsNullOrWhiteSpace(title))
                throw new AiProviderLogicalException("AI 返回缺少 title");
            if (string.IsNullOrWhiteSpace(type) || (type != "movie" && type != "tv"))
                throw new AiProviderLogicalException($"AI 返回 type 非法：{type ?? "<null>"}");

            // 数值范围守护：AI 可能输出越界值（season=2024 / episode=-1 / episode=99999 等），
            // 直通会在归档命名层（PlexNamingConventions：season ∈ [0,99]、episode ∈ [0,9999]）
            // 抛 ArgumentOutOfRangeException → 记录 Failed 且重试无解。越界一律置 null：
            // 置 null 让下游 ParseIncomplete 守护转人工审核（人工可救优于直接失败）。
            season = NullIfOutOfRange(season, 0, 99);
            episode = NullIfOutOfRange(episode, 0, 9999);
            episodeEnd = NullIfOutOfRange(episodeEnd, 0, 9999);
            year = NullIfOutOfRange(year, 1900, 2100);

            // movie 类型清空季集字段（AI 可能错填）；episodeEnd 必须 ≥ episode
            if (type == "movie")
            {
                season = null;
                episode = null;
                episodeEnd = null;
            }
            if (episodeEnd is int e && episode is int s && e < s)
            {
                episodeEnd = null; // 范围非法时丢弃 end，保留单集 start
            }

            return new AiParseResult(title!, year, type, season, episode, episodeEnd, Math.Clamp(confidence, 0, 1), aliases);
        }
        catch (JsonException ex)
        {
            throw new AiProviderLogicalException($"AI 返回 JSON 解析失败：{ex.Message}", inner: ex);
        }
    }

    /// <summary>字面溯源守护：AI 返回的 year 若未在文件名 / 路径任一处以独立数字出现，判为凭空推断 → 置 null</summary>
    /// <remarks>
    /// 根因：LLM 会给「文件名没写年份」的作品用世界知识补一个发行年，且多版本作品（重制 / 多次剧场版）
    /// 倾向锚定最早 / 最知名版本——如《攻壳机动队》2026 版文件（文件名无年份）被填 1995。该幻觉 year 危害三处：
    /// 作为 TMDB 检索参数搜到错误版本、污染候选年份维度打分（TmdbCandidateScorer 权重默认 0.3）、优先落进
    /// ParsedInfo.Year。规则引擎的 year 来自正则命名捕获（字面可信），AI 的 year 缺此约束，故在此补做字面溯源：
    /// 仅当该 4 位年份以「前后非数字」的独立形态出现在文件名 / 各级路径段 / 父目录名中才保留，否则置 null——
    /// 置 null 后年份维度记中性满分（不罚）、检索不带年、ParsedInfo 回退 TMDB 候选年，让 TMDB 靠标题 / 热度 /
    /// 语言消歧（多候选不决则转人工），远优于「自信地填错版本年」。
    /// 仅守 year：season / episode 有「单季自动补季」等正当非字面来源、title 允许基于线索翻译转写（见 SystemPrompt），
    /// 均不适用「数字必须在文件名出现」的校验；year 无合法的非字面来源，是唯一可做确定性溯源的事实字段。
    /// </remarks>
    public static AiParseResult GroundYear(AiParseResult result, AiParseRequest request)
    {
        if (result.Year is not int year) return result;              // AI 未给年份：无需校验
        return YearAppearsInSource(year, request) ? result : result with { Year = null };
    }

    /// <summary>年份是否以独立数字形态出现在文件名 / 路径段 / 父目录名任一处</summary>
    private static bool YearAppearsInSource(int year, AiParseRequest request)
    {
        string digits = year.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (AppearsAsStandaloneNumber(request.FileName, digits)) return true;
        if (request.RelativeSegments is { } segments)
            foreach (string seg in segments)
                if (AppearsAsStandaloneNumber(seg, digits)) return true;
        return AppearsAsStandaloneNumber(request.ParentFolderName, digits);
    }

    /// <summary>digits 是否作为「前后紧邻均非数字」的独立数字段出现在 text 中（防 119952 命中 1995）</summary>
    private static bool AppearsAsStandaloneNumber(string? text, string digits)
    {
        if (string.IsNullOrEmpty(text)) return false;
        int from = 0;
        while (true)
        {
            int idx = text.IndexOf(digits, from, StringComparison.Ordinal);
            if (idx < 0) return false;
            bool leftBoundary = idx == 0 || !char.IsDigit(text[idx - 1]);
            int after = idx + digits.Length;
            bool rightBoundary = after >= text.Length || !char.IsDigit(text[after]);
            if (leftBoundary && rightBoundary) return true;
            from = idx + 1;
        }
    }

    public static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    internal static string StripMarkdownFences(string s)
    {
        string t = s.Trim();
        if (t.StartsWith("```", StringComparison.Ordinal))
        {
            int firstNewline = t.IndexOf('\n');
            if (firstNewline > 0) t = t[(firstNewline + 1)..];
            if (t.EndsWith("```", StringComparison.Ordinal)) t = t[..^3];
        }
        return t;
    }

    private static string? GetStr(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out JsonElement v)) return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    /// <summary>数值越界置 null（null 透传）；供季/集/年份范围守护使用</summary>
    private static int? NullIfOutOfRange(int? value, int min, int max) =>
        value is int v && (v < min || v > max) ? null : value;

    private static int? TryGetInt(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out JsonElement v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt32(out int i) => i,
            JsonValueKind.String when int.TryParse(v.GetString(), out int i2) => i2,
            _ => null,
        };
    }

    private static double? TryGetDouble(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out JsonElement v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetDouble(out double d) => d,
            JsonValueKind.String when double.TryParse(v.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d2) => d2,
            _ => null,
        };
    }

    /// <summary>解析 aliases 字符串数组为 TMDB 检索别名候选（去空白/去重/排除与 title 雷同/限长限量）</summary>
    /// <remarks>
    /// AI 输出的 aliases 用于「中文 title 在 TMDB 查不到时」逐个兜底检索（国漫/日漫主条目常为原名）。
    /// 清洗：去首尾空白 → 丢空串 → 单条裁到 200 字符 → 不区分大小写去重且排除与 title 相同的 → 至多取前 3 条。
    /// 全部清洗后为空返回 null（与「无 aliases 字段」同义，下游 SearchAliases 可空）。非数组 / 缺字段同样返回 null。
    /// </remarks>
    private static IReadOnlyList<string>? TryGetSearchAliases(JsonElement el, string? title)
    {
        if (!el.TryGetProperty("aliases", out JsonElement arr) || arr.ValueKind != JsonValueKind.Array)
            return null;

        const int maxAliases = 3;
        const int maxLen = 200;
        List<string> result = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(title)) seen.Add(title.Trim());

        foreach (JsonElement item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            string? raw = item.GetString();
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string cleaned = Truncate(raw.Trim(), maxLen);
            if (seen.Add(cleaned)) result.Add(cleaned);
            if (result.Count >= maxAliases) break;
        }

        return result.Count == 0 ? null : result;
    }
}
