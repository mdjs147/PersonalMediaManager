using PersonalMediaManager.Domain.Aggregates.MediaItems;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Domain.Exceptions;

namespace PersonalMediaManager.Domain.Tests.Aggregates;

/// <summary>D.10：MediaItem 状态机穷举回归（全部非自循环组合 + 全部自循环）</summary>
/// <remarks>
/// 与现有 MediaItemStateMachineTests 互补：那里用 [InlineData] 覆盖典型路径，
/// 本类用 [Theory + MemberData] 穷举所有 (from, to) 对照 §6.2 期望表，单测一旦有人修改
/// AllowedTransitions 字典而不同步本表即立刻爆出来。
///
/// 表来源：MediaItem.AllowedTransitions（§6.2）
/// </remarks>
public sealed class MediaItemStateMachineExhaustiveTests
{
    private static readonly Dictionary<MediaItemStatus, HashSet<MediaItemStatus>> Expected = new()
    {
        [MediaItemStatus.Detected] = new() { MediaItemStatus.Queued, MediaItemStatus.Skipped, MediaItemStatus.Cancelled },
        [MediaItemStatus.Queued] = new() { MediaItemStatus.Parsing, MediaItemStatus.Cancelled },
        [MediaItemStatus.Parsing] = new() { MediaItemStatus.TmdbMatching, MediaItemStatus.AiParsing, MediaItemStatus.Cancelled },
        [MediaItemStatus.TmdbMatching] = new() { MediaItemStatus.Classifying, MediaItemStatus.AiParsing, MediaItemStatus.AwaitingReview, MediaItemStatus.Cancelled },
        [MediaItemStatus.AiParsing] = new() { MediaItemStatus.TmdbRematching, MediaItemStatus.Cancelled },
        [MediaItemStatus.TmdbRematching] = new() { MediaItemStatus.Classifying, MediaItemStatus.AwaitingReview, MediaItemStatus.Cancelled },
        [MediaItemStatus.Classifying] = new() { MediaItemStatus.Archiving, MediaItemStatus.AwaitingReview, MediaItemStatus.Cancelled },
        // Queued：「TMDB 未收录」（TmdbZeroResult）由 TmdbZeroResultRetrySweeper 每日自动重投回队列
        [MediaItemStatus.AwaitingReview] = new() { MediaItemStatus.Archiving, MediaItemStatus.Ignored, MediaItemStatus.Queued },
        [MediaItemStatus.Archiving] = new() { MediaItemStatus.Completed, MediaItemStatus.Skipped, MediaItemStatus.AwaitingReview },
        [MediaItemStatus.Completed] = new(),
        [MediaItemStatus.Skipped] = new(),
        [MediaItemStatus.Ignored] = new(),
        [MediaItemStatus.Cancelled] = new(),
        [MediaItemStatus.Failed] = new() { MediaItemStatus.Queued },
    };

    public static IEnumerable<object[]> AllNonSelfPairs()
    {
        MediaItemStatus[] all = Enum.GetValues<MediaItemStatus>();
        foreach (MediaItemStatus from in all)
        {
            foreach (MediaItemStatus to in all)
            {
                if (from == to) continue;
                bool legal = Expected[from].Contains(to);
                yield return new object[] { from, to, legal };
            }
        }
    }

    public static IEnumerable<object[]> AllSelfPairs()
    {
        foreach (MediaItemStatus s in Enum.GetValues<MediaItemStatus>())
        {
            yield return new object[] { s };
        }
    }

    [Theory]
    [MemberData(nameof(AllNonSelfPairs))]
    public void Exhaustive_NonSelfPair_Matches_Expected(MediaItemStatus from, MediaItemStatus to, bool legal)
    {
        MediaItem m = MediaItem.CreateDetected("/x/y.mkv", "y.mkv", 1024);
        DriveTo(m, from);

        Action act = () => m.Transition(to);
        if (legal)
        {
            act.Should().NotThrow($"§6.2 表声明 {from} → {to} 是合法转移");
            m.Status.Should().Be(to);
        }
        else
        {
            act.Should().Throw<DomainException>($"§6.2 表禁止 {from} → {to}");
            m.Status.Should().Be(from, "非法转移失败后状态应保持原状");
        }
    }

    [Theory]
    [MemberData(nameof(AllSelfPairs))]
    public void Exhaustive_SelfLoop_Always_Illegal(MediaItemStatus from)
    {
        MediaItem m = MediaItem.CreateDetected("/x/y.mkv", "y.mkv", 1024);
        DriveTo(m, from);

        Action act = () => m.Transition(from);
        act.Should().Throw<DomainException>("自循环（from == to）一律禁止");
    }

    /// <summary>把 m 推到目标 from 状态，全部走合法路径</summary>
    private static void DriveTo(MediaItem m, MediaItemStatus target)
    {
        if (m.Status == target) return;
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
                m.Transition(MediaItemStatus.Skipped); return; // Detected → Skipped 最短
            case MediaItemStatus.Ignored:
                DriveTo(m, MediaItemStatus.AwaitingReview); m.Transition(MediaItemStatus.Ignored); return;
            case MediaItemStatus.Cancelled:
                m.Transition(MediaItemStatus.Cancelled); return;
            case MediaItemStatus.Failed:
                DriveTo(m, MediaItemStatus.Parsing); m.MarkFailed("drive-to-failed"); return;
            default:
                throw new ArgumentOutOfRangeException(nameof(target));
        }
    }
}
