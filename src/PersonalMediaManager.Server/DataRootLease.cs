namespace PersonalMediaManager.Server;

/// <summary>独占数据根，避免多个服务同时执行迁移和导入</summary>
public sealed class DataRootLease : IDisposable
{
    private readonly FileStream _stream;

    public DataRootLease(string root)
    {
        string path = Path.Combine(root, ".server.lock");
        try
        {
            _stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new IOException("数据目录已被其他 Server 使用，或锁文件无法打开", ex);
        }
    }

    public void Dispose() => _stream.Dispose();
}
