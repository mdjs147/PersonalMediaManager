using PersonalMediaManager.Application.Services.Parse;

namespace PersonalMediaManager.Application.Tests.Parse;

/// <summary>FolderSeriesCache — 进程内文件夹级 series 复用缓存</summary>
public sealed class FolderSeriesCacheTests
{
    private static FolderSeriesEntry Entry(int id = 500) => new(id, "tv", "Show A", 2020, 0.85);

    [Fact]
    public void Set_Then_TryGet_Returns_Same_Entry()
    {
        FolderSeriesCache cache = new();
        cache.Set(@"X:\media\Show A", Entry());

        FolderSeriesEntry? got = cache.TryGet(@"X:\media\Show A");
        got.Should().NotBeNull();
        got!.TmdbId.Should().Be(500);
        got.MediaType.Should().Be("tv");
        got.Title.Should().Be("Show A");
    }

    [Fact]
    public void TryGet_Unknown_Folder_Returns_Null()
    {
        FolderSeriesCache cache = new();
        cache.TryGet(@"X:\media\Nope").Should().BeNull();
    }

    [Fact]
    public void Key_Is_Case_Insensitive()
    {
        // Windows 路径大小写不敏感：同一目录的不同大小写应命中同一条
        FolderSeriesCache cache = new();
        cache.Set(@"X:\Media\Show A", Entry());
        cache.TryGet(@"x:\media\show a").Should().NotBeNull();
    }

    [Fact]
    public void Set_Overwrites_Existing()
    {
        FolderSeriesCache cache = new();
        cache.Set(@"X:\media\Show A", Entry(100));
        cache.Set(@"X:\media\Show A", Entry(200));
        cache.TryGet(@"X:\media\Show A")!.TmdbId.Should().Be(200);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Null_Or_Empty_Path_Is_Safe(string? path)
    {
        FolderSeriesCache cache = new();
        cache.Set(path!, Entry());           // 不抛
        cache.TryGet(path!).Should().BeNull();
    }

    // ---------- Remove（审核纠错失效） ----------

    [Fact]
    public void Set_Then_Remove_TryGet_Returns_Null()
    {
        FolderSeriesCache cache = new();
        cache.Set(@"X:\media\Show A", Entry());

        cache.Remove(@"X:\media\Show A");

        cache.TryGet(@"X:\media\Show A").Should().BeNull("纠错失效后同目录不得再复用旧 series");
    }

    [Fact]
    public void Remove_Is_Case_Insensitive()
    {
        // 与 Set / TryGet 同走 OrdinalIgnoreCase：Windows 路径大小写不敏感，不同大小写也要能失效同一条
        FolderSeriesCache cache = new();
        cache.Set(@"X:\Media\Show A", Entry());

        cache.Remove(@"x:\media\show a");

        cache.TryGet(@"X:\Media\Show A").Should().BeNull();
    }

    [Theory]
    [InlineData(@"X:\media\Nope")]
    [InlineData("")]
    [InlineData(null)]
    public void Remove_Unknown_Or_Empty_Path_Is_Safe(string? path)
    {
        FolderSeriesCache cache = new();
        cache.Set(@"X:\media\Show A", Entry());

        cache.Remove(path!);                 // 不抛、幂等

        cache.TryGet(@"X:\media\Show A").Should().NotBeNull("移除未知 / 空键不得误删其它条目");
    }

    [Fact]
    public void Remove_Key_Built_From_File_Directory_Matches_Set_Key()
    {
        // 钉住两侧键构造一致：写入方（ProcessFileService.TryFolderKey）与失效方（ReviewService.InvalidateFolderCache）
        // 均以 Path.GetDirectoryName(文件完整路径) 为键 —— 同一文件推导出的键必须能命中同一条缓存
        FolderSeriesCache cache = new();
        const string episodeFile = @"X:\media\Show A\Show.A.S01E01.mkv";
        string folderKey = Path.GetDirectoryName(episodeFile)!;
        cache.Set(folderKey, Entry());

        cache.Remove(Path.GetDirectoryName(episodeFile)!);

        cache.TryGet(folderKey).Should().BeNull();
    }
}
