using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Dtos.LocalAi;

namespace PersonalMediaManager.Infrastructure.External.LocalAi;

internal interface ILocalAiChildProcess : IDisposable
{
    bool HasExited { get; }
    long WorkingSetBytes { get; }
    void KillTree();
    Task WaitForExitAsync(CancellationToken ct);
}

internal interface ILocalAiProcessFactory
{
    bool PlatformSupported { get; }
    bool IsPortAvailable(int port);
    ILocalAiChildProcess Start(LocalAiSettingsDto settings, string modelPath, string alias);
}

/// <summary>使用固定参数启动自行安装的 llama-server</summary>
internal sealed class LocalAiProcessFactory : ILocalAiProcessFactory
{
    public bool PlatformSupported => (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        && RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64;

    public bool IsPortAvailable(int port)
    {
        try
        {
            using Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.ExclusiveAddressUse = true;
            socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
            return true;
        }
        catch (SocketException) { return false; }
    }

    public ILocalAiChildProcess Start(LocalAiSettingsDto settings, string modelPath, string alias)
    {
        settings.Validate();
        if (!PlatformSupported) throw new BusinessException("本地运行时暂不支持当前平台或架构");
        string executable = settings.RuntimeExecutablePath;
        if (!File.Exists(executable)) throw new BusinessException("请先另行安装 llama-server，并配置有效路径");
        // 允许包管理器的文件符号链接，但最终目标仍必须是同名运行时。
        FileSystemInfo? target = new FileInfo(executable).ResolveLinkTarget(returnFinalTarget: true);
        if (target is not null)
        {
            executable = target.FullName;
            (settings with { RuntimeExecutablePath = executable }).Validate();
        }
        ProcessStartInfo info = CreateStartInfo(settings, executable, modelPath, alias);
        Process process = new() { StartInfo = info };
        try
        {
            if (!process.Start()) throw new BusinessException("本地运行时启动失败");
            return new ChildProcess(process);
        }
        catch { process.Dispose(); throw; }
    }

    internal static ProcessStartInfo CreateStartInfo(LocalAiSettingsDto settings, string executable, string modelPath, string alias)
    {
        ProcessStartInfo info = new(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        // 不继承 HF/代理/API 凭据或 LLAMA_ARG_* 覆盖，只保留操作系统启动所需项。
        Dictionary<string, string?> inherited = new(info.Environment);
        info.Environment.Clear();
        foreach (string name in new[] { "PATH", "SystemRoot", "WINDIR", "TEMP", "TMP", "LANG" })
            if (inherited.TryGetValue(name, out string? value)) info.Environment[name] = value;
        string[] args = ["--model", modelPath, "--host", "127.0.0.1", "--port", settings.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--alias", alias, "--threads", Math.Min(settings.Threads, Math.Max(1, Environment.ProcessorCount)).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--threads-batch", Math.Min(settings.Threads, Math.Max(1, Environment.ProcessorCount)).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--ctx-size", settings.ContextTokens.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--n-predict", settings.MaxOutputTokens.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--parallel", "1", "--threads-http", "2", "--batch-size", "128", "--ubatch-size", "128",
            "--n-gpu-layers", "0", "--offline", "--no-webui", "--no-context-shift"];
        foreach (string arg in args) info.ArgumentList.Add(arg);
        return info;
    }

    private sealed class ChildProcess : ILocalAiChildProcess
    {
        private readonly Process _process;
        public ChildProcess(Process process)
        {
            _process = process;
            // 持续丢弃输出，避免管道阻塞或日志包含媒体路径；不用 ReadToEnd 累积内存。
            _ = DrainAsync(process.StandardOutput);
            _ = DrainAsync(process.StandardError);
        }
        public bool HasExited => _process.HasExited;
        public long WorkingSetBytes { get { _process.Refresh(); return _process.WorkingSet64; } }
        public void KillTree() { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        public Task WaitForExitAsync(CancellationToken ct) => _process.WaitForExitAsync(ct);
        public void Dispose() => _process.Dispose();
        private static async Task DrainAsync(StreamReader reader)
        {
            try
            {
                char[] buffer = new char[2048];
                while (await reader.ReadAsync(buffer) != 0) { }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        }
    }
}
