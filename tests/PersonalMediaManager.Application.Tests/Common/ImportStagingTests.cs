using System;
using System.IO;
using System.Linq;
using System.Text;
using PersonalMediaManager.Application.Common;

namespace PersonalMediaManager.Application.Tests.Common;

/// <summary>ImportStaging「暂存 + 启动期换库」单测：直接守护「换库 + 删残留 WAL/SHM」这条修复</summary>
public sealed class ImportStagingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"pmm-staging-{Guid.NewGuid():N}");

    public ImportStagingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 清理失败不阻断 */ }
    }

    private static readonly byte[] Magic = "SQLite format 3\0"u8.ToArray();

    /// <summary>造一个能通过 IsLikelySqlite 的假库：魔数头 + 填充到 1KB + 偏移 512 处埋可校验标记</summary>
    private static string WriteFakeDb(string path, string marker)
    {
        byte[] buf = new byte[1024];
        Magic.CopyTo(buf, 0);
        Encoding.ASCII.GetBytes(marker).CopyTo(buf, 512);
        File.WriteAllBytes(path, buf);
        return path;
    }

    private static string ReadMarker(string path)
    {
        byte[] buf = File.ReadAllBytes(path);
        int end = 512;
        while (end < buf.Length && buf[end] != 0) end++;
        return Encoding.ASCII.GetString(buf, 512, end - 512);
    }

    [Fact(DisplayName = "无 pending → 返回 false 且不动现库")]
    public void NoPending_ReturnsFalse()
    {
        string db = Path.Combine(_dir, "pmm.db");
        WriteFakeDb(db, "LIVE");

        ImportStaging.ApplyPendingIfAny(db).Should().BeFalse();
        ReadMarker(db).Should().Be("LIVE");
    }

    [Fact(DisplayName = "合法 pending → 换库 + 备份现库 + 清除残留 WAL/SHM")]
    public void ValidPending_SwapsBacksUpAndClearsSidecars()
    {
        string db = Path.Combine(_dir, "pmm.db");
        WriteFakeDb(db, "OLD");
        File.WriteAllText(db + "-wal", "stale-wal");   // 旧 WAL：必须被删，否则启动期会回写覆盖新库
        File.WriteAllText(db + "-shm", "stale-shm");
        WriteFakeDb(ImportStaging.PendingPathFor(db), "NEW");

        ImportStaging.ApplyPendingIfAny(db).Should().BeTrue();

        ReadMarker(db).Should().Be("NEW", "主库应换成导入包内容");
        File.Exists(ImportStaging.PendingPathFor(db)).Should().BeFalse("pending 应被消费");
        File.Exists(db + "-wal").Should().BeFalse("残留 WAL 必须删除（这正是旧实现漏掉、导致导入被回退的根因）");
        File.Exists(db + "-shm").Should().BeFalse("残留 SHM 必须删除");

        string[] backups = Directory.GetFiles(_dir, "pmm.db.preimport-*");
        backups.Should().ContainSingle();
        ReadMarker(backups[0]).Should().Be("OLD", "备份应是换库前的现库");
    }

    [Fact(DisplayName = "现库不存在时合法 pending 仍换库（无需备份）")]
    public void ValidPending_NoExistingDb_StillSwaps()
    {
        string db = Path.Combine(_dir, "pmm.db");
        WriteFakeDb(ImportStaging.PendingPathFor(db), "NEW");

        ImportStaging.ApplyPendingIfAny(db).Should().BeTrue();
        ReadMarker(db).Should().Be("NEW");
        Directory.GetFiles(_dir, "pmm.db.preimport-*").Should().BeEmpty("现库不存在时无需备份");
    }

    [Fact(DisplayName = "非法 pending（非 SQLite）→ 不换库 + 现库不动 + pending 弃置为 .invalid")]
    public void InvalidPending_DoesNotTouchLiveDb()
    {
        string db = Path.Combine(_dir, "pmm.db");
        WriteFakeDb(db, "LIVE");
        // 600B 但魔数不符：走「magic 不匹配」分支（而非尺寸分支）
        File.WriteAllBytes(ImportStaging.PendingPathFor(db), Enumerable.Repeat((byte)'x', 600).ToArray());

        ImportStaging.ApplyPendingIfAny(db).Should().BeFalse();
        ReadMarker(db).Should().Be("LIVE", "非法 pending 绝不能覆盖现库");
        File.Exists(ImportStaging.PendingPathFor(db)).Should().BeFalse("非法 pending 应被挪走");
        Directory.GetFiles(_dir, "pmm.db.import-pending.invalid-*").Should().ContainSingle();
    }

    [Fact(DisplayName = "IsLikelySqlite：合法魔数+尺寸 → true；坏头/过小 → false")]
    public void IsLikelySqlite_Cases()
    {
        string good = WriteFakeDb(Path.Combine(_dir, "good.db"), "X");
        string badHeader = Path.Combine(_dir, "badheader.db");
        File.WriteAllBytes(badHeader, Enumerable.Repeat((byte)'z', 600).ToArray());
        string tooSmall = Path.Combine(_dir, "small.db");
        File.WriteAllBytes(tooSmall, Magic);   // 仅 16B，尺寸不足 512

        ImportStaging.IsLikelySqlite(good).Should().BeTrue();
        ImportStaging.IsLikelySqlite(badHeader).Should().BeFalse();
        ImportStaging.IsLikelySqlite(tooSmall).Should().BeFalse();
        ImportStaging.IsLikelySqlite(Path.Combine(_dir, "missing.db")).Should().BeFalse();
    }
}
