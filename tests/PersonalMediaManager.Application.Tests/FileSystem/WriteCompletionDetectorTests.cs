using Microsoft.Extensions.Logging.Abstractions;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Infrastructure.Platform.FileSystem;

namespace PersonalMediaManager.Application.Tests.FileSystem;

/// <summary>WriteCompletionDetector（D4.6）— 哨兵 / 稳定窗口 / 超时</summary>
/// <remarks>
/// 测试用 stableSeconds=2、timeoutSeconds=8 加速跑（避免单测等 5+ 秒）。
/// 真实生产用默认 5/300。
/// </remarks>
public sealed class WriteCompletionDetectorTests : IDisposable
{
    private readonly string _workDir;
    private readonly IWriteCompletionDetector _sut;

    public WriteCompletionDetectorTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"pmm-detect-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
        _sut = new WriteCompletionDetector(NullLogger<WriteCompletionDetector>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task SentinelPresent_ReturnsImmediately()
    {
        string file = Path.Combine(_workDir, "movie.mkv");
        await File.WriteAllTextAsync(file, "in-progress");
        await File.WriteAllTextAsync(file + ".complete", "");

        DateTimeOffset start = DateTimeOffset.UtcNow;
        bool ok = await _sut.WaitUntilCompleteAsync(file, stableSeconds: 10, timeoutSeconds: 30);
        TimeSpan elapsed = DateTimeOffset.UtcNow - start;

        ok.Should().BeTrue();
        elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2), "哨兵存在应立即返回，不等稳定窗口");
    }

    [Fact]
    public async Task StableForN_Seconds_ReturnsTrue()
    {
        string file = Path.Combine(_workDir, "stable.mkv");
        await File.WriteAllBytesAsync(file, new byte[1024]);

        bool ok = await _sut.WaitUntilCompleteAsync(file, stableSeconds: 2, timeoutSeconds: 10);
        ok.Should().BeTrue();
    }

    [Fact]
    public async Task FileGrows_DoesNotReturnUntilStable()
    {
        string file = Path.Combine(_workDir, "grow.mkv");
        await File.WriteAllBytesAsync(file, new byte[100]);

        // 后台 500ms 追加字节：早于探测器 @T=1s 的第 2 次轮询，远离所有轮询边界，避免竞态。
        // 时序（探测器轮询 @ T=0/1/2/3，间隔 1s）：
        //   T=0   轮询：len=100, stableCount=1
        //   T≈0.5 后台追加 → len=200
        //   T=1   轮询：len=200 ≠ 100 → 重置 stableCount=1
        //   T=2   轮询：len=200, stableCount=2
        //   T=3   轮询：len=200, stableCount=3 → 返回，elapsed≈3s
        // 无增长基线 elapsed≈2s（轮询 3 次于 T=0/1/2）；> 2.5s 证明确实因增长多走了一个轮询周期。
        _ = Task.Run(async () =>
        {
            await Task.Delay(500);
            await using FileStream fs = new(file, FileMode.Append);
            await fs.WriteAsync(new byte[100]);
        });

        DateTimeOffset start = DateTimeOffset.UtcNow;
        bool ok = await _sut.WaitUntilCompleteAsync(file, stableSeconds: 3, timeoutSeconds: 15);
        TimeSpan elapsed = DateTimeOffset.UtcNow - start;

        ok.Should().BeTrue();
        elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(2.5), "中途增长会重置稳定计数 → 比无增长基线（≈2s）至少多一个 1s 轮询周期");
    }

    [Fact]
    public async Task NeverStable_TimesOut()
    {
        string file = Path.Combine(_workDir, "noisy.mkv");
        await File.WriteAllBytesAsync(file, new byte[1]);

        CancellationTokenSource backgroundCts = new();
        _ = Task.Run(async () =>
        {
            while (!backgroundCts.IsCancellationRequested)
            {
                try
                {
                    await using FileStream fs = new(file, FileMode.Append);
                    await fs.WriteAsync(new byte[1]);
                }
                catch { /* 文件被删等 — 忽略 */ }
                await Task.Delay(500);
            }
        });

        try
        {
            bool ok = await _sut.WaitUntilCompleteAsync(file, stableSeconds: 3, timeoutSeconds: 4);
            ok.Should().BeFalse("4s 总超时内永远不稳定");
        }
        finally
        {
            backgroundCts.Cancel();
            await Task.Delay(200);
        }
    }

    [Fact]
    public async Task FileMissing_ShortCircuits_False_Immediately()
    {
        // 源文件不存在（被删 / 移走 / 重命名）→ 立即短路返回 false，绝不空轮询等满超时窗口
        // （旧实现等满 timeoutSeconds 才返回：被删文件的僵尸记录每次重排都白等 5 分钟、拖住串行队列）
        string file = Path.Combine(_workDir, "ghost.mkv");

        DateTimeOffset start = DateTimeOffset.UtcNow;
        bool ok = await _sut.WaitUntilCompleteAsync(file, stableSeconds: 1, timeoutSeconds: 30);
        TimeSpan elapsed = DateTimeOffset.UtcNow - start;

        ok.Should().BeFalse("文件不存在 → 短路返回 false");
        elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2), "不存在的文件应立即返回，不等 30s 超时窗口");
    }

    [Fact]
    public async Task FileMissing_WithSentinelPresent_StillReturnsFalse()
    {
        // 哨兵在而源文件不在：源已消失，返回 true 只会让下游对空文件白跑一遍 → 仍按消失短路 false
        string file = Path.Combine(_workDir, "vanished.mkv");
        await File.WriteAllTextAsync(file + ".complete", "");

        bool ok = await _sut.WaitUntilCompleteAsync(file, stableSeconds: 1, timeoutSeconds: 5);

        ok.Should().BeFalse("源文件不存在时即使哨兵在场也应判定失败");
    }

    [Fact]
    public async Task EmptyPath_Throws()
    {
        await ((Func<Task>)(() => _sut.WaitUntilCompleteAsync("")))
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task CancellationToken_StopsWaiting()
    {
        string file = Path.Combine(_workDir, "anything.mkv");
        await File.WriteAllBytesAsync(file, new byte[1]);
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(1));

        // stableSeconds=10 远超 cts 触发时间
        bool ok = await _sut.WaitUntilCompleteAsync(file, stableSeconds: 10, timeoutSeconds: 60, ct: cts.Token);
        ok.Should().BeFalse();
    }
}
