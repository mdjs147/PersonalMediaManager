using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Dtos.System;
using PersonalMediaManager.Application.Services.System;

namespace PersonalMediaManager.Infrastructure.Platform.System;

/// <summary>系统模块实现：版本/系统信息 + zip 导出 + zip 导入（防 Zip Slip）</summary>
/// <remarks>
/// 导出步骤：
/// 1. SQLite VACUUM INTO 临时文件（在线快照，确保不锁主库；源库取 <see cref="DatabaseFileLocation"/> 生效路径）
/// 2. ZipArchive 写入条目：pmm.db / keys/*
///
/// 导入步骤（「暂存 + 启动期换库」，详见 <see cref="ImportStaging"/>）：
/// 1. 解压到临时目录前先严格校验条目路径（防 Zip Slip）
/// 2. 校验 pmm.db 确为合法 SQLite，写到 &lt;db&gt;.import-pending —— 绝不在运行中热替换主库
///    （WAL 模式下旧 -wal/-shm + 页缓存会在 checkpoint/关闭时回写覆盖刚导入的库，导致「导入没作用」）
/// 3. 追加式合并 DataProtection 密钥环（进程内安全；缺密钥会让加密字段无法解密）
/// 4. 返回 RequiresRestart=true，由前端引导用户完整退出重启；真正换库发生在 Host 下次启动开库前
///
/// appsettings.json 自单 exe 改造起已嵌入 Host 程序集（只读出厂默认、不含用户数据），故导出/导入均不再涉及该条目；
/// 兼容旧备份包：导入时其内含的 appsettings.json 条目被忽略（无外置物理文件可覆盖）。
/// </remarks>
public sealed class SystemService : ISystemService
{
    private const string DbEntryName = "pmm.db";
    private const string KeysDirPrefix = "keys/";
    private const string BackupFilePrefix = "pmm-backup-";

    private readonly AppPaths _paths;
    private readonly DatabaseFileLocation _dbLocation;
    private readonly ILogger<SystemService> _logger;
    private readonly IVersionInfoProvider _versionInfo;
    private static readonly DateTimeOffset ProcessStartUtc = DateTimeOffset.UtcNow;

    public SystemService(AppPaths paths, DatabaseFileLocation dbLocation, ILogger<SystemService> logger, IVersionInfoProvider versionInfo)
    {
        _paths = paths;
        _dbLocation = dbLocation;
        _logger = logger;
        _versionInfo = versionInfo;
    }

    public async Task<SystemInfoResponse> GetInfoAsync(CancellationToken ct = default)
    {
        // 版本号走 IVersionInfoProvider 统一来源（4 套版本号 + db 实际状态）；
        // Version 字段 = Product（主版本号），保留作为前端旧字段的向后兼容
        VersionInfoResponse versions = await _versionInfo.GetFullAsync(ct).ConfigureAwait(false);

        return new SystemInfoResponse
        {
            Version = versions.Product,
            VersionInfo = versions,
            Os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows"
               : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macOS"
               : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "Linux"
               : "Unknown",
            OsVersion = Environment.OSVersion.VersionString,
            Architecture = RuntimeInformation.OSArchitecture.ToString(),
            RuntimeVersion = RuntimeInformation.FrameworkDescription,
            StartedAt = ProcessStartUtc,
            Uptime = DateTimeOffset.UtcNow - ProcessStartUtc,
            DataRoot = _paths.Root,
            DbSizeBytes = SafeFileSize(_dbLocation.Path),
            LogDirSizeBytes = SafeDirSize(_paths.LogDir),
            PostersCacheSizeBytes = SafeDirSize(_paths.PostersDir),
            LastSuccessfulBackupAt = GetLastBackupTime(),
        };
    }

    public async Task<ExportResult> ExportAsync(Stream output, CancellationToken ct = default)
    {
        string tempDbCopy = Path.Combine(Path.GetTempPath(), $"pmm-export-{Guid.NewGuid():N}.db");
        try
        {
            // SQLite VACUUM INTO：在线快照（不锁主库，输出为干净紧凑的副本）；源库取生效路径（override 感知）
            await using (SqliteConnection conn = new($"Data Source={_dbLocation.Path}"))
            {
                await conn.OpenAsync(ct);
                await using SqliteCommand cmd = conn.CreateCommand();
                cmd.CommandText = "VACUUM INTO @target";
                cmd.Parameters.AddWithValue("@target", tempDbCopy);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            // 写 zip：先 buffer 到内存（ZipArchive 内部用同步 Write，而 ASP.NET Core Response.Body 禁同步 IO）
            // 然后整体 CopyToAsync 到目标流。一份 pmm.db 一般 <100MB，内存压力可接受
            using MemoryStream buffer = new();
            using (ZipArchive archive = new(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                // 1. db 快照
                archive.CreateEntryFromFile(tempDbCopy, DbEntryName, CompressionLevel.Optimal);

                // 2. DataProtection 密钥环目录（appsettings.json 已嵌入程序集、只读出厂默认，不入备份）
                if (Directory.Exists(_paths.KeyRingDir))
                {
                    foreach (string file in Directory.EnumerateFiles(_paths.KeyRingDir, "*", SearchOption.AllDirectories))
                    {
                        string relative = Path.GetRelativePath(_paths.KeyRingDir, file).Replace('\\', '/');
                        archive.CreateEntryFromFile(file, KeysDirPrefix + relative, CompressionLevel.Optimal);
                    }
                }
            }

            buffer.Position = 0;
            await buffer.CopyToAsync(output, ct);

            string fileName = $"pmm-export-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.zip";
            long size = buffer.Length;
            _logger.LogInformation("导出系统快照完成 file={File} size={Size}", fileName, size);
            return new ExportResult { FileName = fileName, Size = size };
        }
        finally
        {
            try { if (File.Exists(tempDbCopy)) File.Delete(tempDbCopy); } catch { }
        }
    }

    public async Task<ImportResult> ImportAsync(Stream input, CancellationToken ct = default)
    {
        string tempExtractDir = Path.Combine(Path.GetTempPath(), $"pmm-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempExtractDir);
        try
        {
            // 1. 严格防 Zip Slip：枚举条目时校验解压目标在 tempExtractDir 内
            using (ZipArchive archive = new(input, ZipArchiveMode.Read, leaveOpen: true))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (string.IsNullOrWhiteSpace(entry.FullName)) continue;
                    string destination = Path.GetFullPath(Path.Combine(tempExtractDir, entry.FullName));
                    string normalizedRoot = tempExtractDir.EndsWith(Path.DirectorySeparatorChar)
                        ? tempExtractDir
                        : tempExtractDir + Path.DirectorySeparatorChar;
                    if (!destination.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new BusinessException($"导入包条目路径非法（Zip Slip 守卫触发）: {entry.FullName}");
                    }

                    // 目录条目
                    if (entry.FullName.EndsWith('/'))
                    {
                        Directory.CreateDirectory(destination);
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    await using FileStream fs = File.Create(destination);
                    await using Stream es = entry.Open();
                    await es.CopyToAsync(fs, ct);
                }
            }

            // 2. 必含 pmm.db，且必须是合法 SQLite（防把坏包/空文件暂存上去）
            string extractedDb = Path.Combine(tempExtractDir, DbEntryName);
            if (!File.Exists(extractedDb))
            {
                throw new BusinessException("导入包缺少 pmm.db 条目");
            }
            if (!ImportStaging.IsLikelySqlite(extractedDb))
            {
                throw new BusinessException("导入包的 pmm.db 不是合法的 SQLite 文件");
            }

            // 3. 暂存新库到 <db>.import-pending —— 绝不在运行中覆盖主库（WAL 模式热替换会被旧 WAL 回写覆盖）
            //    真正换库由 ImportStaging.ApplyPendingIfAny 在 Host 下次启动、开库之前原子完成
            string pendingPath = ImportStaging.PendingPathFor(_dbLocation.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(pendingPath)!);
            File.Copy(extractedDb, pendingPath, overwrite: true);

            // 4. 追加式合并 DataProtection 密钥环（进程内安全：DataProtection 仅读 keyring，新增文件无害）
            //    用合并而非「先清空再覆盖」：备份包可能只带当前活动密钥，清空会丢掉现库其它密钥导致解密失败
            string extractedKeysDir = Path.Combine(tempExtractDir, KeysDirPrefix.TrimEnd('/'));
            if (Directory.Exists(extractedKeysDir))
            {
                Directory.CreateDirectory(_paths.KeyRingDir);
                foreach (string file in Directory.EnumerateFiles(extractedKeysDir, "*", SearchOption.AllDirectories))
                {
                    string relative = Path.GetRelativePath(extractedKeysDir, file);
                    string target = Path.Combine(_paths.KeyRingDir, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target, overwrite: true);
                }
            }

            // appsettings.json 已嵌入程序集（只读出厂默认）：旧备份包若含该条目，此处一律忽略（无外置物理文件可覆盖）。

            _logger.LogInformation("导入快照已暂存（将于下次启动换库）pending={Pending}", pendingPath);
            return new ImportResult
            {
                Success = true,
                StagedPath = pendingPath,
                RequiresRestart = true,
                Message = "导入包已暂存，请完整退出并重启应用以完成换库（重启时旧库会自动备份为 pmm.db.preimport-*）",
            };
        }
        finally
        {
            try { if (Directory.Exists(tempExtractDir)) Directory.Delete(tempExtractDir, recursive: true); } catch { }
        }
    }

    public Task<IReadOnlyList<BackupFileInfo>> ListBackupsAsync(CancellationToken ct = default)
    {
        List<BackupFileInfo> list = new();
        if (Directory.Exists(_paths.BackupDir))
        {
            foreach (string path in Directory.EnumerateFiles(_paths.BackupDir, $"{BackupFilePrefix}*.zip"))
            {
                FileInfo fi;
                try { fi = new FileInfo(path); } catch { continue; }
                DateTimeOffset created = ParseBackupTimestamp(fi.Name)
                    ?? new DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero);
                list.Add(new BackupFileInfo(fi.Name, SafeLen(fi), created));
            }
        }
        // 文件名时间戳即字典序，倒序 = 最新在前
        IReadOnlyList<BackupFileInfo> result = list
            .OrderByDescending(b => b.FileName, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(result);
    }

    public async Task<ImportResult> RestoreBackupAsync(string fileName, CancellationToken ct = default)
    {
        string safe = ValidateBackupFileName(fileName);
        string fullPath = Path.Combine(_paths.BackupDir, safe);
        if (!File.Exists(fullPath))
        {
            throw new BusinessException("指定的备份文件不存在");
        }

        // 复用导入暂存：打开备份 zip 流，走与 POST /system/import 完全相同的「暂存 + 启动期换库」路径
        await using FileStream fs = File.OpenRead(fullPath);
        ImportResult result = await ImportAsync(fs, ct);
        _logger.LogWarning("已从备份恢复（暂存待重启换库）backup={File}", safe);
        return result;
    }

    /// <summary>最近一次成功备份时间：backups/ 内最新 pmm-backup-*.zip 的时间戳（解析失败回退文件最后写入时间）；无备份为 null</summary>
    private DateTimeOffset? GetLastBackupTime()
    {
        try
        {
            if (!Directory.Exists(_paths.BackupDir)) return null;
            string? newest = Directory.EnumerateFiles(_paths.BackupDir, $"{BackupFilePrefix}*.zip")
                .OrderByDescending(p => Path.GetFileName(p), StringComparer.Ordinal)
                .FirstOrDefault();
            if (newest is null) return null;
            return ParseBackupTimestamp(Path.GetFileName(newest))
                ?? new DateTimeOffset(File.GetLastWriteTimeUtc(newest), TimeSpan.Zero);
        }
        catch { return null; }
    }

    /// <summary>从 pmm-backup-yyyyMMddHHmmss.zip 文件名解析备份时间（UTC）；格式不符返 null</summary>
    private static DateTimeOffset? ParseBackupTimestamp(string fileName)
    {
        if (!fileName.StartsWith(BackupFilePrefix, StringComparison.Ordinal)
            || !fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        // 去前缀与 .zip 后余下应为 14 位 yyyyMMddHHmmss
        string ts = fileName[BackupFilePrefix.Length..^4];
        return DateTimeOffset.TryParseExact(ts, "yyyyMMddHHmmss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset parsed)
            ? parsed
            : null;
    }

    /// <summary>校验恢复用备份文件名：仅允许 backups/ 内 pmm-backup-*.zip 纯文件名，杜绝路径穿越 / 绝对路径 / 分隔符</summary>
    private static string ValidateBackupFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new BusinessException("未指定备份文件名");
        }
        string name = fileName.Trim();
        if (name.Contains('/') || name.Contains('\\') || name.Contains("..")
            || Path.IsPathRooted(name)
            || Path.GetFileName(name) != name
            || !name.StartsWith(BackupFilePrefix, StringComparison.Ordinal)
            || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new BusinessException("备份文件名非法");
        }
        return name;
    }

    private static long SafeLen(FileInfo fi)
    {
        try { return fi.Length; } catch { return 0; }
    }

    private static long SafeFileSize(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch { return 0; }
    }

    private static long SafeDirSize(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return 0;
            long total = 0;
            foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(f).Length; } catch { }
            }
            return total;
        }
        catch { return 0; }
    }
}
