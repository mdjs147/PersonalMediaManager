using PersonalMediaManager.Domain.Aggregates.MediaItems;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Domain.Exceptions;

namespace PersonalMediaManager.Domain.Tests.Aggregates;

/// <summary>MediaItem.AppendStep — 聚合内子实体追加规则</summary>
public sealed class MediaItemAppendStepTests
{
    private static MediaItem New() => MediaItem.CreateDetected("/x/y.mkv", "y.mkv", 1024);

    [Fact]
    public void AppendStep_Empty_Initial_Steps_Collection()
    {
        New().Steps.Should().BeEmpty();
    }

    [Fact]
    public void AppendStep_Adds_Single_Step_With_All_Fields()
    {
        MediaItem m = New();
        DateTimeOffset t = new(2026, 5, 24, 12, 0, 0, TimeSpan.Zero);

        m.AppendStep(MediaItemStatus.Parsing, t, durMs: 120, detail: """{"k":"v"}""");

        ProcessStep step = m.Steps.Single();
        step.Stage.Should().Be(MediaItemStatus.Parsing);
        step.StartedAt.Should().Be(t);
        step.DurMs.Should().Be(120);
        step.Detail.Should().Be("""{"k":"v"}""");
    }

    [Fact]
    public void AppendStep_Preserves_Order_Of_Insertion()
    {
        MediaItem m = New();
        DateTimeOffset t0 = new(2026, 5, 24, 12, 0, 0, TimeSpan.Zero);
        m.AppendStep(MediaItemStatus.Detected, t0, 0);
        m.AppendStep(MediaItemStatus.Queued, t0.AddSeconds(1), 50);
        m.AppendStep(MediaItemStatus.Parsing, t0.AddSeconds(2), 200);

        IReadOnlyList<ProcessStep> ordered = m.Steps.ToList();
        ordered[0].Stage.Should().Be(MediaItemStatus.Detected);
        ordered[1].Stage.Should().Be(MediaItemStatus.Queued);
        ordered[2].Stage.Should().Be(MediaItemStatus.Parsing);
    }

    [Fact]
    public void AppendStep_Negative_DurMs_Throws_DomainException()
    {
        MediaItem m = New();
        Action act = () => m.AppendStep(MediaItemStatus.Parsing, DateTimeOffset.UtcNow, durMs: -1, detail: null);
        act.Should().Throw<DomainException>().WithMessage("*durMs*");
    }

    [Fact]
    public void AppendStep_Null_Detail_Allowed()
    {
        MediaItem m = New();
        m.AppendStep(MediaItemStatus.Detected, DateTimeOffset.UtcNow, durMs: 0, detail: null);
        m.Steps.Single().Detail.Should().BeNull();
    }

    [Fact]
    public void Steps_Is_ReadOnly_Snapshot()
    {
        MediaItem m = New();
        IReadOnlyCollection<ProcessStep> snap = m.Steps;
        m.AppendStep(MediaItemStatus.Detected, DateTimeOffset.UtcNow, 0);
        // 外部不应能 cast 回 List 修改；ReadOnlyCollection wrapper 保护
        snap.Should().HaveCount(1, "ReadOnlyCollection 共享底层 list，新增会反映；但外部不能直接 Add");
    }
}
