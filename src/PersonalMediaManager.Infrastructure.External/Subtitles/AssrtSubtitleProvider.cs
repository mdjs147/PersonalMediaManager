using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Infrastructure.External.DependencyInjection;
using PersonalMediaManager.Infrastructure.External.Tmdb;

namespace PersonalMediaManager.Infrastructure.External.Subtitles;

/// <summary>Assrt（伪·射手网）字幕源客户端</summary>
/// <remarks>
/// API（api.assrt.net）：搜索 GET /v1/sub/search?q={kw}&amp;cnt=15&amp;pos=0 /
/// 详情 GET /v1/sub/detail?id={id} / 配额 GET /v1/user/quota；文件下载走详情返回的直链（与 API 域名可能不同）。
///
/// 鉴权：token 放 Authorization: Bearer 头不放 query —— query 会进访问/代理日志，Bearer 已被 SensitiveDataRedactor 脱敏；
/// 直链下载<b>不带</b> token（直链多为第三方分发主机，防 token 外泄）。
///
/// 限流：进程级令牌桶 容量 1 / 3.2s 补给（Assrt 上限 20 次/分钟，留余量），每次出站请求（含下载）先 Consume，
/// 复用 Tmdb 目录的 <see cref="TokenBucketRateLimiter"/>（仅 using 引用，不挪文件）。
///
/// 超时：per-request 用 endpoint.TimeoutSeconds 链接 CTS；HttpClient.Timeout 置 Infinite —— 默认 100s 会截断
/// 120s 档配置，且链接 CTS 能覆盖流式读响应体阶段。
///
/// 安全阀：下载按 Content-Length 与实际读取双重计数，超 10MB 抛异常（字幕远小于此，超限视为异常源）；空内容抛异常。
/// 失败语义：HTTP 非 2xx / status!=0 / JSON 解析失败抛 <see cref="SubtitleProviderException"/>；仅 TestAsync 归集不抛。
/// 代理：按 endpoint.UseProxy 在 SubtitleDirect / SubtitleProxy 两个 named client 间二选一（照 AI 双轨模式）。
/// </remarks>
internal sealed class AssrtSubtitleProvider : ISubtitleProvider
{
    /// <summary>单文件下载大小上限（10MB）</summary>
    public const long MaxDownloadBytes = 10L * 1024 * 1024;

    /// <summary>宽容反序列化配置（大小写不敏感 + 数字容忍字符串形态，源站字段类型不稳）</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>filelist 大小描述串匹配（"1.2MB" / "456 KB" 形态；纯数字串另行 TryParse）</summary>
    private static readonly Regex SizePattern = new(
        @"^(\d+(?:\.\d+)?)\s*(B|KB|MB|GB)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<AssrtSubtitleProvider> _logger;
    private readonly TokenBucketRateLimiter _rateLimiter;

    /// <summary>构造注入（限流器由 DI / 测试显式传入，测试可换零延迟桶，照 TmdbClient 模式）</summary>
    public AssrtSubtitleProvider(
        IHttpClientFactory httpFactory,
        ILogger<AssrtSubtitleProvider> logger,
        TokenBucketRateLimiter rateLimiter)
    {
        _httpFactory = httpFactory;
        _logger = logger;
        _rateLimiter = rateLimiter;
    }

    public SubtitleProviderType Provider => SubtitleProviderType.Assrt;

    public async Task<IReadOnlyList<SubtitleSearchEntry>> SearchAsync(string keyword, SubtitleProviderEndpoint endpoint, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(keyword))
            throw new SubtitleProviderException("搜索关键词不能为空");
        string baseUrl = RequireBaseUrl(endpoint);
        RequireToken(endpoint);

        string url = $"{baseUrl}/v1/sub/search?q={Uri.EscapeDataString(keyword)}&cnt=15&pos=0";
        AssrtResponse resp = await SendApiAsync(url, endpoint, "搜索", ct);

        List<AssrtSubItem>? subs = resp.Sub?.Subs;
        if (subs is null || subs.Count == 0) return [];

        List<SubtitleSearchEntry> entries = new(subs.Count);
        foreach (AssrtSubItem item in subs)
        {
            if (item.Id is null) continue; // 无 id 无法读详情 / 下载，跳过
            string id = item.Id.Value.ToString(CultureInfo.InvariantCulture);
            string name = !string.IsNullOrWhiteSpace(item.NativeName) ? item.NativeName
                : !string.IsNullOrWhiteSpace(item.VideoName) ? item.VideoName
                : $"字幕 {id}";
            entries.Add(new SubtitleSearchEntry(id, name, item.VideoName, item.Lang?.Desc, item.SubType, item.UploadTime, item.VoteScore));
        }
        return entries;
    }

    public async Task<SubtitleDetailEntry> GetFilesAsync(string subtitleId, SubtitleProviderEndpoint endpoint, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(subtitleId))
            throw new SubtitleProviderException("字幕 Id 不能为空");
        string baseUrl = RequireBaseUrl(endpoint);
        RequireToken(endpoint);

        string url = $"{baseUrl}/v1/sub/detail?id={Uri.EscapeDataString(subtitleId)}";
        AssrtResponse resp = await SendApiAsync(url, endpoint, "详情", ct);

        List<AssrtSubItem>? subs = resp.Sub?.Subs;
        if (subs is null || subs.Count == 0)
            throw new SubtitleProviderException($"字幕不存在或已删除（id={subtitleId}）");

        AssrtSubItem detail = subs[0];
        List<SubtitleFileEntry> files = [];
        if (detail.FileList is { Count: > 0 })
        {
            for (int i = 0; i < detail.FileList.Count; i++)
            {
                AssrtFileItem file = detail.FileList[i];
                if (string.IsNullOrWhiteSpace(file.Url)) continue; // 无直链条目跳过；Index 保留原数组位置，保证下载时按 Index 重取一致
                string fileName = !string.IsNullOrWhiteSpace(file.F) ? file.F
                    : !string.IsNullOrWhiteSpace(detail.FileName) ? detail.FileName
                    : $"字幕文件{i + 1}";
                files.Add(new SubtitleFileEntry(i, fileName, file.Url, ParseSizeDescription(file.S)));
            }
        }
        // filelist 空（或全部无直链）且顶层直链非空 → 顶层单文件，按 Index=-1 约定回退
        if (files.Count == 0 && !string.IsNullOrWhiteSpace(detail.Url))
        {
            string fileName = !string.IsNullOrWhiteSpace(detail.FileName) ? detail.FileName : $"字幕 {subtitleId}";
            files.Add(new SubtitleFileEntry(-1, fileName, detail.Url, detail.Size));
        }
        return new SubtitleDetailEntry(subtitleId, detail.Lang?.Desc, files);
    }

    public async Task<byte[]> DownloadAsync(string fileUrl, SubtitleProviderEndpoint endpoint, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(fileUrl)
            || !Uri.TryCreate(fileUrl, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new SubtitleProviderException("字幕直链无效（必须为 http/https 绝对地址）");
        }

        await _rateLimiter.ConsumeAsync(ct);

        HttpClient client = CreateClient(endpoint);
        using CancellationTokenSource cts = LinkTimeout(endpoint, ct);
        // 直链多在第三方分发主机，刻意不带 Authorization 头（防 token 外泄）
        using HttpRequestMessage req = new(HttpMethod.Get, uri);

        HttpResponseMessage resp;
        try
        {
            resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        }
        catch (HttpRequestException ex)
        {
            throw new SubtitleProviderException($"字幕文件下载网络异常：{ex.Message}", inner: ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new SubtitleProviderException($"字幕文件下载超时（{endpoint.TimeoutSeconds}s）", inner: ex);
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
                throw new SubtitleProviderException($"字幕文件下载失败：HTTP {(int)resp.StatusCode}", (int)resp.StatusCode);

            // 安全阀一：声明长度超限直接拒绝（不读 body）
            long? contentLength = resp.Content.Headers.ContentLength;
            if (contentLength is > MaxDownloadBytes)
                throw new SubtitleProviderException($"字幕文件过大（声明 {contentLength} 字节，上限 {MaxDownloadBytes} 字节）");

            byte[] data;
            try
            {
                // 安全阀二：流式读边读边计数（覆盖无 Content-Length 的 chunked 响应）
                await using Stream stream = await resp.Content.ReadAsStreamAsync(cts.Token);
                using MemoryStream buffer = new();
                byte[] chunk = new byte[81920];
                int read;
                while ((read = await stream.ReadAsync(chunk, cts.Token)) > 0)
                {
                    buffer.Write(chunk, 0, read);
                    if (buffer.Length > MaxDownloadBytes)
                        throw new SubtitleProviderException($"字幕文件过大（实际读取超过上限 {MaxDownloadBytes} 字节）");
                }
                data = buffer.ToArray();
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new SubtitleProviderException($"字幕文件下载超时（{endpoint.TimeoutSeconds}s）", inner: ex);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                throw new SubtitleProviderException($"字幕文件读取网络异常：{ex.Message}", inner: ex);
            }

            if (data.Length == 0)
                throw new SubtitleProviderException("字幕文件内容为空");
            return data;
        }
    }

    public async Task<SubtitleProviderTestResult> TestAsync(SubtitleProviderEndpoint endpoint, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(endpoint.BaseUrl))
            return new SubtitleProviderTestResult(false, null, 0, "BaseUrl 不能为空");
        if (string.IsNullOrWhiteSpace(endpoint.Token))
            return new SubtitleProviderTestResult(false, null, 0, "Token 未配置");

        string url = $"{endpoint.BaseUrl.TrimEnd('/')}/v1/user/quota";
        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            AssrtResponse resp = await SendApiAsync(url, endpoint, "配额查询", ct);
            sw.Stop();
            return new SubtitleProviderTestResult(true, resp.User?.Quota, sw.ElapsedMilliseconds, null);
        }
        catch (Exception ex)
        {
            // 连通性测试不抛：任何异常（网络 / 超时 / status!=0 / 解析失败）归集为失败结果（照 TmdbConnectivityTester）
            sw.Stop();
            _logger.LogWarning(ex, "Assrt 连通性探测异常");
            return new SubtitleProviderTestResult(false, null, sw.ElapsedMilliseconds, ex.Message);
        }
    }

    // ---------- 内部辅助 ----------

    /// <summary>发 Assrt API GET 并完成通用校验（HTTP 状态 → JSON 反序列化 → 业务 status）</summary>
    private async Task<AssrtResponse> SendApiAsync(string url, SubtitleProviderEndpoint endpoint, string operation, CancellationToken ct)
    {
        await _rateLimiter.ConsumeAsync(ct);

        HttpClient client = CreateClient(endpoint);
        using CancellationTokenSource cts = LinkTimeout(endpoint, ct);
        using HttpRequestMessage req = new(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.Token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage resp;
        try
        {
            resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        }
        catch (HttpRequestException ex)
        {
            throw new SubtitleProviderException($"Assrt {operation}请求网络异常：{ex.Message}", inner: ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new SubtitleProviderException($"Assrt {operation}请求超时（{endpoint.TimeoutSeconds}s）", inner: ex);
        }

        using (resp)
        {
            string body;
            try
            {
                body = await resp.Content.ReadAsStringAsync(cts.Token);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new SubtitleProviderException($"Assrt {operation}响应读取超时（{endpoint.TimeoutSeconds}s）", inner: ex);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                throw new SubtitleProviderException($"Assrt {operation}响应读取网络异常：{ex.Message}", inner: ex);
            }

            if (!resp.IsSuccessStatusCode)
                throw new SubtitleProviderException($"Assrt {operation}失败：HTTP {(int)resp.StatusCode}", (int)resp.StatusCode);

            AssrtResponse? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<AssrtResponse>(body, JsonOptions);
            }
            catch (JsonException ex)
            {
                throw new SubtitleProviderException($"Assrt {operation}响应 JSON 解析失败：{ex.Message}", (int)resp.StatusCode, ex);
            }
            if (parsed is null)
                throw new SubtitleProviderException($"Assrt {operation}响应为空");

            // status!=0 = 业务失败（常见：token 无效 / 请求过频 / 配额耗尽），errmsg 透传进中文消息
            if (parsed.Status != 0)
                throw new SubtitleProviderException(
                    $"Assrt {operation}返回错误（status={parsed.Status}，errmsg={parsed.ErrMsg ?? "无"}）", (int)resp.StatusCode);

            return parsed;
        }
    }

    /// <summary>按 endpoint.UseProxy 选直连 / 代理 named client（照 AI 双轨模式）</summary>
    private HttpClient CreateClient(SubtitleProviderEndpoint endpoint)
    {
        HttpClient client = _httpFactory.CreateClient(endpoint.UseProxy
            ? ServiceCollectionExtensions.SubtitleHttpClientProxy
            : ServiceCollectionExtensions.SubtitleHttpClientDirect);
        // 默认 100s Timeout 会截断 120s 档配置，且不覆盖流式读 body；置 Infinite 改由 LinkTimeout 的 CTS 全程治理
        client.Timeout = Timeout.InfiniteTimeSpan;
        return client;
    }

    /// <summary>构造 per-request 超时 CTS（链接调用方 ct；秒数下限 1 防呆）</summary>
    private static CancellationTokenSource LinkTimeout(SubtitleProviderEndpoint endpoint, CancellationToken ct)
    {
        CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, endpoint.TimeoutSeconds)));
        return cts;
    }

    /// <summary>校验并规整 BaseUrl（去尾部斜杠；空抛异常）</summary>
    private static string RequireBaseUrl(SubtitleProviderEndpoint endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint.BaseUrl))
            throw new SubtitleProviderException("字幕源 BaseUrl 不能为空");
        return endpoint.BaseUrl.TrimEnd('/');
    }

    /// <summary>校验 Token 已配置（Assrt 全部 API 都要鉴权；直链下载除外）</summary>
    private static void RequireToken(SubtitleProviderEndpoint endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint.Token))
            throw new SubtitleProviderException("字幕源 Token 未配置");
    }

    /// <summary>解析 filelist 的大小描述串（"123" / "456KB" / "1.2MB"）；解析不了返回 null</summary>
    private static long? ParseSizeDescription(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string s = text.Trim();
        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long bytes))
            return bytes >= 0 ? bytes : null;
        Match m = SizePattern.Match(s);
        if (!m.Success) return null;
        if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            return null;
        double factor = m.Groups[2].Value.ToUpperInvariant() switch
        {
            "B" => 1d,
            "KB" => 1024d,
            "MB" => 1024d * 1024,
            "GB" => 1024d * 1024 * 1024,
            _ => 0d,
        };
        return factor > 0 ? (long)Math.Round(value * factor) : null;
    }

    // ---------- Assrt 响应内部 DTO（私有嵌套类，仅本客户端反序列化用） ----------

    /// <summary>顶层响应（status=0 成功；非 0 失败可带 errmsg）</summary>
    private sealed class AssrtResponse
    {
        [JsonPropertyName("status")] public int Status { get; set; }
        [JsonPropertyName("errmsg")] public string? ErrMsg { get; set; }
        [JsonPropertyName("sub")] public AssrtSubNode? Sub { get; set; }
        [JsonPropertyName("user")] public AssrtUserNode? User { get; set; }
    }

    /// <summary>sub 节点（搜索 / 详情共用 subs 数组）</summary>
    private sealed class AssrtSubNode
    {
        [JsonPropertyName("subs")] public List<AssrtSubItem>? Subs { get; set; }
    }

    /// <summary>字幕条目（搜索条目与详情条目字段并集，宽容可空）</summary>
    private sealed class AssrtSubItem
    {
        [JsonPropertyName("id")] public long? Id { get; set; }
        [JsonPropertyName("native_name")] public string? NativeName { get; set; }
        [JsonPropertyName("videoname")] public string? VideoName { get; set; }
        [JsonPropertyName("lang")] public AssrtLangNode? Lang { get; set; }
        [JsonPropertyName("subtype")] public string? SubType { get; set; }
        [JsonPropertyName("upload_time")] public string? UploadTime { get; set; }
        [JsonPropertyName("vote_score")] public double? VoteScore { get; set; }
        [JsonPropertyName("filename")] public string? FileName { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("size")] public long? Size { get; set; }
        [JsonPropertyName("filelist")] public List<AssrtFileItem>? FileList { get; set; }
    }

    /// <summary>lang 节点（desc = 语言描述，如「简体」「双语」）</summary>
    private sealed class AssrtLangNode
    {
        [JsonPropertyName("desc")] public string? Desc { get; set; }
    }

    /// <summary>filelist 条目（f=文件名 / s=大小描述串 / url=直链）</summary>
    private sealed class AssrtFileItem
    {
        [JsonPropertyName("f")] public string? F { get; set; }
        [JsonPropertyName("s")] public string? S { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
    }

    /// <summary>user 节点（quota = 剩余配额）</summary>
    private sealed class AssrtUserNode
    {
        [JsonPropertyName("quota")] public int? Quota { get; set; }
    }
}
