using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Application.Contracts;

/// <summary>外部字幕源客户端契约（多源策略接口）</summary>
/// <remarks>
/// 「按关键词搜字幕 → 看候选文件清单 → 选择下载」三步原语 + 连通性测试；一个实现对应一个外部字幕源
/// （值 = <see cref="SubtitleProviderType"/>，策略自报枚举键，照 <see cref="IAiProtocol"/> 模式）。
/// 首发实现：Infrastructure.External/Subtitles/AssrtSubtitleProvider（伪·射手网 api.assrt.net）。
///
/// 配置传入：token / baseUrl / 超时 / 代理由调用方（Persistence 层设置服务）读库解密后封进
/// <see cref="SubtitleProviderEndpoint"/> 显式传入 —— External 实现绝不读库（照 <see cref="ITmdbClient"/> 模式）。
///
/// 失败语义：搜索 / 详情 / 下载失败一律抛 <see cref="SubtitleProviderException"/>（中文 message，可带 HttpStatus）；
/// 仅 <see cref="TestAsync"/> 例外 —— 网络失败归集为 Success=false 不抛（照 TmdbConnectivityTester）。
/// </remarks>
public interface ISubtitleProvider
{
    /// <summary>字幕源类型（Registry 按枚举键路由用，策略自报）</summary>
    SubtitleProviderType Provider { get; }

    /// <summary>按关键词搜索字幕（无结果返回空集）</summary>
    Task<IReadOnlyList<SubtitleSearchEntry>> SearchAsync(string keyword, SubtitleProviderEndpoint endpoint, CancellationToken ct = default);

    /// <summary>读单条字幕的文件清单（压缩包逐项展开；字幕不存在抛异常）</summary>
    Task<SubtitleDetailEntry> GetFilesAsync(string subtitleId, SubtitleProviderEndpoint endpoint, CancellationToken ct = default);

    /// <summary>下载字幕文件字节（直链 GET；超 10MB / 空内容抛异常）</summary>
    Task<byte[]> DownloadAsync(string fileUrl, SubtitleProviderEndpoint endpoint, CancellationToken ct = default);

    /// <summary>连通性 + 配额测试（网络失败不抛，归集为 Success=false）</summary>
    Task<SubtitleProviderTestResult> TestAsync(SubtitleProviderEndpoint endpoint, CancellationToken ct = default);
}

/// <summary>字幕源路由门面（按枚举键取具体策略）</summary>
/// <remarks>
/// 实现在 Infrastructure.External/Subtitles/SubtitleProviderRegistry（照 AiProtocolParser 字典路由门面）：
/// 构造收 IEnumerable&lt;ISubtitleProvider&gt; 建字典；未注册的枚举值抛 <see cref="SubtitleProviderException"/>。
/// </remarks>
public interface ISubtitleProviderRegistry
{
    /// <summary>按字幕源类型取策略实现（未注册抛异常）</summary>
    ISubtitleProvider Resolve(SubtitleProviderType type);
}

/// <summary>字幕源调用端配置（调用方读库解密后显式传入）</summary>
/// <param name="Token">明文 API token；调用前已由 IProtectedFieldService 解密，null/空 = 未配置</param>
/// <param name="BaseUrl">字幕源 API 根地址（如 https://api.assrt.net）</param>
/// <param name="TimeoutSeconds">单次请求总超时（秒，含建立连接 + 读完整响应体）</param>
/// <param name="UseProxy">是否选代理 named client；实际是否走代理还要看系统代理总开关 + bypass 命中</param>
public sealed record SubtitleProviderEndpoint(
    string? Token,
    string BaseUrl,
    int TimeoutSeconds,
    bool UseProxy);

/// <summary>字幕搜索候选条目</summary>
/// <param name="SubtitleId">源站字幕 Id（读详情 / 下载用）</param>
/// <param name="Name">字幕名称（源站 native_name，空时回退视频名）</param>
/// <param name="VideoName">关联视频名（源站标注，可空）</param>
/// <param name="LanguageHint">源站语言描述（如「简体」「双语」，仅供展示参考，非 ISO 码）</param>
/// <param name="Format">字幕格式描述（如 srt / ass，可空）</param>
/// <param name="UploadTime">源站上传时间原文（不做时区换算，可空）</param>
/// <param name="VoteScore">源站评分（可空）</param>
public sealed record SubtitleSearchEntry(
    string SubtitleId,
    string Name,
    string? VideoName,
    string? LanguageHint,
    string? Format,
    string? UploadTime,
    double? VoteScore);

/// <summary>单条字幕的文件清单（详情展开结果）</summary>
/// <param name="SubtitleId">源站字幕 Id</param>
/// <param name="LanguageHint">源站语言描述（可空）</param>
/// <param name="Files">可下载文件清单（压缩包逐项展开；可能为空集 = 无可下载直链）</param>
public sealed record SubtitleDetailEntry(
    string SubtitleId,
    string? LanguageHint,
    IReadOnlyList<SubtitleFileEntry> Files);

/// <summary>字幕文件条目</summary>
/// <param name="Index">文件序号（压缩包展开 0..n-1；-1 = 顶层单文件，非压缩包展开）</param>
/// <param name="FileName">文件名（语言识别 / 落盘命名用）</param>
/// <param name="Url">下载直链（仅 External 层内部消费，服务层响应不外泄）</param>
/// <param name="SizeBytes">字节数（源站描述串解析不了则为 null）</param>
public sealed record SubtitleFileEntry(
    int Index,
    string FileName,
    string Url,
    long? SizeBytes);

/// <summary>字幕源连通性 + 配额测试结果</summary>
/// <param name="Success">是否连通且鉴权有效</param>
/// <param name="Quota">剩余配额（源站返回则填，否则 null）</param>
/// <param name="ElapsedMilliseconds">耗时（毫秒）</param>
/// <param name="ErrorMessage">失败原因（中文；成功为 null）</param>
public sealed record SubtitleProviderTestResult(
    bool Success,
    int? Quota,
    long ElapsedMilliseconds,
    string? ErrorMessage);

/// <summary>字幕源客户端异常</summary>
/// <remarks>照 TmdbClientException 模式：中文 message + 可选 HTTP 状态码 + 可选内部异常。</remarks>
public sealed class SubtitleProviderException : Exception
{
    public SubtitleProviderException(string message, int? httpStatus = null, Exception? inner = null) : base(message, inner)
    {
        HttpStatus = httpStatus;
    }

    /// <summary>关联的 HTTP 状态码（无 HTTP 上下文时为 null）</summary>
    public int? HttpStatus { get; }
}
