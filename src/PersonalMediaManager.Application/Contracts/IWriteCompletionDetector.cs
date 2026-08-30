namespace PersonalMediaManager.Application.Contracts;

/// <summary>文件写入完成检测（D4.6）— 稳定性 5s 等待 + .complete 哨兵识别</summary>
/// <remarks>
/// 触发场景：FileWatcherWorker 收到 Created/Changed 事件后立即调；本类决定「文件何时算写完，可以开始解析」。
/// 两种判定（OR 关系，任一满足即返回）：
/// 1. **.complete 哨兵**：同名文件加 .complete 后缀存在（如 movie.mkv.complete），下载器明确通知写完 → 立即返回
/// 2. **稳定性窗口**：连续 StableSeconds 秒内文件大小未变 → 视为写完
/// 默认 StableSeconds = 5（需求文档 §3.6 文件操作）；可在 IFileWatcherOptions（C 阶段已落地）覆盖。
/// 文件不存在 / 中途消失 → false。
/// </remarks>
public interface IWriteCompletionDetector
{
    /// <summary>等待文件写入完成；超时返回 false（调用方记日志 + 跳过本次事件）</summary>
    /// <param name="filePath">要检测的文件</param>
    /// <param name="stableSeconds">稳定窗口秒数（默认 5）</param>
    /// <param name="timeoutSeconds">总等待上限秒数（默认 300，防长尾下载阻塞队列）</param>
    Task<bool> WaitUntilCompleteAsync(
        string filePath,
        int stableSeconds = 5,
        int timeoutSeconds = 300,
        CancellationToken ct = default);
}
