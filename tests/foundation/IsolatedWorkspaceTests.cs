using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PersonalMediaManager.Foundation;
using Xunit;

namespace PersonalMediaManager.Foundation.Tests;

public sealed class IsolatedWorkspaceTests
{
    [Fact]
    public void CreationIsUniqueEmptyAndDoesNotCreateADatabase()
    {
        using var first = IsolatedTestWorkspace.Create();
        using var second = IsolatedTestWorkspace.Create();
        Assert.NotEqual(first.RootPath, second.RootPath);
        foreach (var workspace in new[] { first, second })
        {
            Assert.True(Path.IsPathFullyQualified(workspace.RootPath));
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), workspace.RootPath, StringComparison.Ordinal);
            Assert.True(Directory.Exists(workspace.RootPath));
            Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.RootPath));
            Assert.Equal(workspace.RootPath, Path.GetDirectoryName(workspace.DatabasePath));
            Assert.False(File.Exists(workspace.DatabasePath));
        }
    }

    [Fact]
    public void DisposalOnlyRemovesItsOwnWorkspaceAndIsIdempotent()
    {
        using var survivor = IsolatedTestWorkspace.Create();
        var removed = IsolatedTestWorkspace.Create();
        var marker = Path.Combine(survivor.RootPath, "test-marker.txt");
        File.WriteAllText(marker, "fresh-test-only");
        Directory.CreateDirectory(Path.Combine(removed.RootPath, "nested"));
        File.WriteAllText(Path.Combine(removed.RootPath, "nested", "temporary.txt"), "temporary");
        removed.Dispose();
        removed.Dispose();
        Assert.False(Directory.Exists(removed.RootPath));
        Assert.Equal("fresh-test-only", File.ReadAllText(marker));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisposalRefusesChildLinksWithoutTouchingTheirTargets(bool directoryLink)
    {
        using var target = IsolatedTestWorkspace.Create();
        var workspace = IsolatedTestWorkspace.Create();
        var marker = Path.Combine(target.RootPath, "preserve.txt");
        File.WriteAllText(marker, "new-isolated-evidence");
        var localEvidence = Path.Combine(workspace.RootPath, "retained.txt");
        File.WriteAllText(localEvidence, "keep-on-refusal");
        var nested = Directory.CreateDirectory(Path.Combine(workspace.RootPath, "nested"));
        var link = Path.Combine(nested.FullName, "untrusted-link");
        if (directoryLink)
        {
            Directory.CreateSymbolicLink(link, target.RootPath);
        }
        else
        {
            File.CreateSymbolicLink(link, marker);
        }

        try
        {
            var failure = Record.Exception(workspace.Dispose);
            Assert.NotNull(failure);
            Assert.True(failure is IOException or InvalidOperationException, failure.ToString());
            Assert.True(Directory.Exists(workspace.RootPath));
            Assert.Equal("new-isolated-evidence", File.ReadAllText(marker));
            Assert.Equal("keep-on-refusal", File.ReadAllText(localEvidence));
            Assert.True(File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint));
        }
        finally
        {
            // Delete only the link created by this test, never its target.
            if (directoryLink)
            {
                Directory.Delete(link);
            }
            else
            {
                File.Delete(link);
            }

            workspace.Dispose();
        }
    }

    [Fact]
    public void DisposalRefusesAReplacedRootLink()
    {
        using var target = IsolatedTestWorkspace.Create();
        var workspace = IsolatedTestWorkspace.Create();
        var marker = Path.Combine(target.RootPath, "preserve.txt");
        File.WriteAllText(marker, "new-target-evidence");
        Directory.Delete(workspace.RootPath);
        Directory.CreateSymbolicLink(workspace.RootPath, target.RootPath);
        try
        {
            var failure = Record.Exception(workspace.Dispose);
            Assert.NotNull(failure);
            Assert.True(failure is IOException or InvalidOperationException, failure.ToString());
            Assert.Equal("new-target-evidence", File.ReadAllText(marker));
            Assert.True(File.GetAttributes(workspace.RootPath).HasFlag(FileAttributes.ReparsePoint));
        }
        finally
        {
            Directory.Delete(workspace.RootPath);
            workspace.Dispose();
        }
    }

    [Fact]
    public async Task FreshSqliteRoundTripIsConfinedAndIndependent()
    {
        using var first = IsolatedTestWorkspace.Create();
        using var second = IsolatedTestWorkspace.Create();
        await CreateAndCheckDatabase(first, "first-test");
        await CreateAndCheckDatabase(second, "second-test");
        Assert.NotEqual(first.DatabasePath, second.DatabasePath);
        Assert.True(File.Exists(first.DatabasePath));
        Assert.True(File.Exists(second.DatabasePath));
        // Reopen each independently: no connection pool or inherited database is used.
        await using var firstConnection = NewConnection(first);
        await using var firstContext = NewContext(firstConnection);
        Assert.Equal("first-test", (await firstContext.Probes.SingleAsync()).Value);
    }

    private static async Task CreateAndCheckDatabase(IsolatedTestWorkspace workspace, string value)
    {
        Assert.False(File.Exists(workspace.DatabasePath));
        await using var connection = NewConnection(workspace);
        await using var context = NewContext(connection);
        Assert.True(await context.Database.EnsureCreatedAsync());
        Assert.Empty(await context.Probes.ToListAsync());
        context.Probes.Add(new Probe { Value = value });
        Assert.Equal(1, await context.SaveChangesAsync());
        context.ChangeTracker.Clear();
        Assert.Equal(value, (await context.Probes.SingleAsync()).Value);
    }

    private static SqliteConnection NewConnection(IsolatedTestWorkspace workspace) => new(
        new SqliteConnectionStringBuilder
        {
            DataSource = workspace.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());

    private static ProbeContext NewContext(SqliteConnection connection) => new(
        new DbContextOptionsBuilder<ProbeContext>().UseSqlite(connection).Options);

    private sealed class ProbeContext(DbContextOptions<ProbeContext> options) : DbContext(options)
    {
        public DbSet<Probe> Probes => Set<Probe>();
    }

    private sealed class Probe
    {
        public int Id { get; set; }
        public required string Value { get; set; }
    }
}
