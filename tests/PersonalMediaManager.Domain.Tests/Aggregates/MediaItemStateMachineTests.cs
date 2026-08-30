using PersonalMediaManager.Domain.Aggregates.MediaItems;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Domain.Exceptions;

namespace PersonalMediaManager.Domain.Tests.Aggregates;

/// <summary>MediaItem 状态机：合法转移成功 + 非法转移抛 DomainException</summary>
/// <remarks>
/// D1.1 + D1.6：合法/非法转移按需求文档 §6.2 校对；
/// D.10 阶段会用 [Theory] 穷举全部状态组合，这里先覆盖典型流转路径 + ≥5 条非法。
/// </remarks>
public sealed class MediaItemStateMachineTests
{
    private static MediaItem New() => MediaItem.CreateDetected("/x/y.mkv", "y.mkv", 1024);

    [Fact]
    public void Created_InitialStatus_IsDetected()
    {
        MediaItem m = New();
        m.Status.Should().Be(MediaItemStatus.Detected);
        m.IsTerminal().Should().BeFalse();
    }

    [Theory]
    [InlineData(MediaItemStatus.Detected, MediaItemStatus.Queued)]
    [InlineData(MediaItemStatus.Detected, MediaItemStatus.Skipped)]
    [InlineData(MediaItemStatus.Queued, MediaItemStatus.Parsing)]
    [InlineData(MediaItemStatus.Queued, MediaItemStatus.Cancelled)]
    [InlineData(MediaItemStatus.Parsing, MediaItemStatus.TmdbMatching)]
    [InlineData(MediaItemStatus.Parsing, MediaItemStatus.AiParsing)]
    [InlineData(MediaItemStatus.TmdbMatching, MediaItemStatus.Classifying)]
    [InlineData(MediaItemStatus.TmdbMatching, MediaItemStatus.AiParsing)]
    [InlineData(MediaItemStatus.AiParsing, MediaItemStatus.TmdbRematching)]
    [InlineData(MediaItemStatus.TmdbRematching, MediaItemStatus.Classifying)]
    [InlineData(MediaItemStatus.TmdbRematching, MediaItemStatus.AwaitingReview)]
    [InlineData(MediaItemStatus.Classifying, MediaItemStatus.Archiving)]
    [InlineData(MediaItemStatus.Classifying, MediaItemStatus.AwaitingReview)]
    [InlineData(MediaItemStatus.AwaitingReview, MediaItemStatus.Archiving)]
    [InlineData(MediaItemStatus.AwaitingReview, MediaItemStatus.Ignored)]
    [InlineData(MediaItemStatus.AwaitingReview, MediaItemStatus.Queued)] // TMDB 未收录每日自动重投回队列
    [InlineData(MediaItemStatus.Archiving, MediaItemStatus.Completed)]
    [InlineData(MediaItemStatus.Archiving, MediaItemStatus.Skipped)]
    [InlineData(MediaItemStatus.Archiving, MediaItemStatus.AwaitingReview)] // 同名冲突「询问」策略退回人工裁定
    [InlineData(MediaItemStatus.Failed, MediaItemStatus.Queued)]
    public void Transition_LegalEdge_Succeeds(MediaItemStatus from, MediaItemStatus to)
    {
        MediaItem m = New();
        DriveTo(m, from);
        m.Transition(to);
        m.Status.Should().Be(to);
    }

    [Theory]
    [InlineData(MediaItemStatus.Detected, MediaItemStatus.Parsing)]      // 跳过 Queued
    [InlineData(MediaItemStatus.Detected, MediaItemStatus.Archiving)]    // 跨大步
    [InlineData(MediaItemStatus.Queued, MediaItemStatus.Archiving)]      // 跳过 Parsing
    [InlineData(MediaItemStatus.Parsing, MediaItemStatus.Completed)]     // 跨终态
    [InlineData(MediaItemStatus.Completed, MediaItemStatus.Queued)]      // 终态再启
    [InlineData(MediaItemStatus.Skipped, MediaItemStatus.Queued)]        // 终态再启
    [InlineData(MediaItemStatus.Ignored, MediaItemStatus.Archiving)]     // 终态再启
    [InlineData(MediaItemStatus.Cancelled, MediaItemStatus.Queued)]      // 已取消终态再启
    [InlineData(MediaItemStatus.AwaitingReview, MediaItemStatus.Parsing)] // 反向回流
    public void Transition_IllegalEdge_ThrowsDomainException(MediaItemStatus from, MediaItemStatus to)
    {
        MediaItem m = New();
        DriveTo(m, from);
        Action act = () => m.Transition(to);
        act.Should().Throw<DomainException>().WithMessage("*非法转移*");
    }

    [Fact]
    public void Transition_SelfLoop_ThrowsDomainException()
    {
        MediaItem m = New();
        Action act = () => m.Transition(MediaItemStatus.Detected);
        act.Should().Throw<DomainException>().WithMessage("*自循环*");
    }

    [Fact]
    public void Transition_ToCompleted_SetsArchivedAt()
    {
        MediaItem m = New();
        DriveTo(m, MediaItemStatus.Archiving);
        m.ArchivedAt.Should().BeNull();
        m.Transition(MediaItemStatus.Completed);
        m.ArchivedAt.Should().NotBeNull();
        m.IsTerminal().Should().BeTrue();
    }

    [Fact]
    public void MarkFailed_FromAnyNonTerminal_AccumulatesAttemptAndError()
    {
        MediaItem m = New();
        DriveTo(m, MediaItemStatus.TmdbMatching);
        m.MarkFailed("TMDB 超时");
        m.Status.Should().Be(MediaItemStatus.Failed);
        m.ErrorMessage.Should().Be("TMDB 超时");
        m.AttemptCount.Should().Be(1);
        m.LastAttemptAt.Should().NotBeNull();
    }

    [Fact]
    public void MarkFailed_FromTerminal_ThrowsDomainException()
    {
        MediaItem m = New();
        DriveTo(m, MediaItemStatus.Completed);
        Action act = () => m.MarkFailed("重复失败");
        act.Should().Throw<DomainException>().WithMessage("*终态不可再失败*");
    }

    [Fact]
    public void Failed_CanBeRescannedToQueued()
    {
        MediaItem m = New();
        DriveTo(m, MediaItemStatus.Parsing);
        m.MarkFailed("IO error");
        m.Transition(MediaItemStatus.Queued); // History.Rescan
        m.Status.Should().Be(MediaItemStatus.Queued);
    }

    [Theory]
    [InlineData(MediaItemStatus.Failed)]
    [InlineData(MediaItemStatus.Skipped)]
    [InlineData(MediaItemStatus.AwaitingReview)]
    public void BeginManualArchive_FromAllowedState_EntersArchiving(MediaItemStatus from)
    {
        MediaItem m = New();
        DriveTo(m, from);
        m.BeginManualArchive();
        m.Status.Should().Be(MediaItemStatus.Archiving);
        // 置 Archiving 后仍可正常收尾到 Completed（逃生口不破坏后续状态机）
        m.Transition(MediaItemStatus.Completed);
        m.Status.Should().Be(MediaItemStatus.Completed);
    }

    [Theory]
    [InlineData(MediaItemStatus.Completed)]   // 已归档完成
    [InlineData(MediaItemStatus.Ignored)]     // 已人工忽略
    [InlineData(MediaItemStatus.Queued)]      // 在途
    [InlineData(MediaItemStatus.Parsing)]     // 在途
    [InlineData(MediaItemStatus.Archiving)]   // 正在归档
    public void BeginManualArchive_FromDisallowedState_ThrowsDomainException(MediaItemStatus from)
    {
        MediaItem m = New();
        DriveTo(m, from);
        Action act = () => m.BeginManualArchive();
        act.Should().Throw<DomainException>().WithMessage("*手动归档*");
    }

    /// <summary>把聚合驱到任意目标状态（仅使用合法路径）；测试辅助</summary>
    private static void DriveTo(MediaItem m, MediaItemStatus target)
    {
        if (m.Status == target) return;
        // 简单路径表（覆盖测试用例需要的所有 from 点）
        switch (target)
        {
            case MediaItemStatus.Detected: return;
            case MediaItemStatus.Queued:
                m.Transition(MediaItemStatus.Queued); return;
            case MediaItemStatus.Parsing:
                m.Transition(MediaItemStatus.Queued); m.Transition(MediaItemStatus.Parsing); return;
            case MediaItemStatus.TmdbMatching:
                DriveTo(m, MediaItemStatus.Parsing); m.Transition(MediaItemStatus.TmdbMatching); return;
            case MediaItemStatus.AiParsing:
                DriveTo(m, MediaItemStatus.Parsing); m.Transition(MediaItemStatus.AiParsing); return;
            case MediaItemStatus.TmdbRematching:
                DriveTo(m, MediaItemStatus.AiParsing); m.Transition(MediaItemStatus.TmdbRematching); return;
            case MediaItemStatus.Classifying:
                DriveTo(m, MediaItemStatus.TmdbMatching); m.Transition(MediaItemStatus.Classifying); return;
            case MediaItemStatus.AwaitingReview:
                DriveTo(m, MediaItemStatus.Classifying); m.Transition(MediaItemStatus.AwaitingReview); return;
            case MediaItemStatus.Archiving:
                DriveTo(m, MediaItemStatus.Classifying); m.Transition(MediaItemStatus.Archiving); return;
            case MediaItemStatus.Completed:
                DriveTo(m, MediaItemStatus.Archiving); m.Transition(MediaItemStatus.Completed); return;
            case MediaItemStatus.Skipped:
                // 优先走 Detected → Skipped（最短）
                if (m.Status == MediaItemStatus.Detected) { m.Transition(MediaItemStatus.Skipped); return; }
                DriveTo(m, MediaItemStatus.Archiving); m.Transition(MediaItemStatus.Skipped); return;
            case MediaItemStatus.Ignored:
                DriveTo(m, MediaItemStatus.AwaitingReview); m.Transition(MediaItemStatus.Ignored); return;
            case MediaItemStatus.Cancelled:
                m.Transition(MediaItemStatus.Cancelled); return;
            case MediaItemStatus.Failed:
                DriveTo(m, MediaItemStatus.Parsing); m.MarkFailed("test"); return;
            default:
                throw new ArgumentOutOfRangeException(nameof(target));
        }
    }
}
