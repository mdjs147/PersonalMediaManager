namespace PersonalMediaManager.Server;

/// <summary>跨平台入口参数，不传递未知启动选项</summary>
public sealed record ServerOptions(string? DataDirectory, bool Headless, bool Help, string[] ConfigurationArgs)
{
    public static ServerOptions Parse(string[] args)
    {
        string? root = Environment.GetEnvironmentVariable("PMM_DATA_DIR");
        bool headless = false, help = false;
        List<string> configuration = [];
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--help": case "-h": help = true; break;
                case "--headless": headless = true; break;
                case "--data-dir": root = Value(); break;
                case "--port": configuration.Add("--Web:Port"); configuration.Add(Value()); break;
                case "--bind": configuration.Add("--Web:BindAddress"); configuration.Add(Value()); break;
                default: throw new ArgumentException($"未知参数：{args[i]}");
            }
            string Value()
            {
                if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException("启动参数缺少值");
                return args[i];
            }
        }
        if (root is not null && (!Path.IsPathFullyQualified(root) || string.IsNullOrWhiteSpace(root)))
            throw new ArgumentException("数据目录必须是绝对路径");
        return new(root is null ? null : Path.GetFullPath(root), headless, help, configuration.ToArray());
    }
}
