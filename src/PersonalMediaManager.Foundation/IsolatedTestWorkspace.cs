namespace PersonalMediaManager.Foundation;

/// <summary>
/// Owns a newly created temporary test directory. It cannot select an existing
/// directory and is not a production media, backup, or filesystem operation API.
/// </summary>
public sealed class IsolatedTestWorkspace : IDisposable
{
    private readonly object sync = new();
    private bool disposed;

    private IsolatedTestWorkspace(string rootPath) => RootPath = rootPath;

    public string RootPath { get; }
    public string DatabasePath => Path.Combine(RootPath, "foundation-tests.sqlite3");

    public static IsolatedTestWorkspace Create() =>
        new(Directory.CreateTempSubdirectory("pmm-dev001-").FullName);

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
            {
                return;
            }

            var root = new DirectoryInfo(RootPath);
            root.Refresh();
            RejectLink(root);
            if (root.Exists)
            {
                InspectOwnedTree(root);
                Directory.Delete(RootPath, recursive: true);
            }

            disposed = true;
        }
    }

    private static void InspectOwnedTree(DirectoryInfo directory)
    {
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            RejectLink(entry);
            if (entry is DirectoryInfo child)
            {
                InspectOwnedTree(child);
            }
        }
    }

    private static void RejectLink(FileSystemInfo entry)
    {
        if (entry.LinkTarget is not null ||
            (entry.Exists && (entry.Attributes & FileAttributes.ReparsePoint) != 0))
        {
            throw new IOException("Refusing to clean a test workspace containing a symbolic link or reparse point.");
        }
    }
}
