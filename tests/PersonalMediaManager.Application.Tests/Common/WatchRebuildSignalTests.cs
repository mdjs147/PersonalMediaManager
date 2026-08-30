using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;

namespace PersonalMediaManager.Application.Tests.Common;

/// <summary>WatchRebuildSignal — 监控重建信号总线（无界 Channel）发布 / 读取语义</summary>
public sealed class WatchRebuildSignalTests
{
    [Fact]
    public async Task Publish_Then_Read_Roundtrip()
    {
        WatchRebuildSignal signal = new();
        signal.Publish(new WatchRebuildItem(WatchChangeKind.ShareRecovered, 42, @"\\nas\share"));

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(2));
        WatchRebuildItem item = await signal.Reader.ReadAsync(cts.Token);

        item.Kind.Should().Be(WatchChangeKind.ShareRecovered);
        item.FolderId.Should().Be(42);
        item.Path.Should().Be(@"\\nas\share");
    }

    [Fact]
    public void Publish_MultipleItems_PreservesFifoOrder()
    {
        WatchRebuildSignal signal = new();
        signal.Publish(new WatchRebuildItem(WatchChangeKind.FolderCreated, 1));
        signal.Publish(new WatchRebuildItem(WatchChangeKind.FolderUpdated, 2));
        signal.Publish(new WatchRebuildItem(WatchChangeKind.FolderDeleted, 3));

        signal.Reader.TryRead(out WatchRebuildItem? a).Should().BeTrue();
        signal.Reader.TryRead(out WatchRebuildItem? b).Should().BeTrue();
        signal.Reader.TryRead(out WatchRebuildItem? c).Should().BeTrue();

        a!.Kind.Should().Be(WatchChangeKind.FolderCreated);
        b!.Kind.Should().Be(WatchChangeKind.FolderUpdated);
        c!.Kind.Should().Be(WatchChangeKind.FolderDeleted);
        signal.Reader.TryRead(out _).Should().BeFalse("读完应为空");
    }

    [Fact]
    public void Publish_WithoutReader_NeverBlocks()
    {
        // 无界 Channel：消费者未启动 / 已落后时生产侧也不阻塞（FSW 回调线程 / 请求线程安全）
        // 注：SingleReader 无界 Channel 不支持 Reader.Count，改 TryRead 排空计数
        WatchRebuildSignal signal = new();
        for (int i = 0; i < 1000; i++)
        {
            signal.Publish(new WatchRebuildItem(WatchChangeKind.WatcherFaulted, i));
        }

        int drained = 0;
        while (signal.Reader.TryRead(out _))
        {
            drained++;
        }
        drained.Should().Be(1000, "1000 次 Publish 应全部入队成功且不阻塞");
    }
}
