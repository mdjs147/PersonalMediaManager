using System.Net;
using System.Security.Cryptography;
using PersonalMediaManager.Application.Common;

namespace PersonalMediaManager.Infrastructure.External.LocalAi;

internal interface ILocalAiModelStore
{
    string PathFor(LocalAiModelArtifact model);
    bool IsPresent(LocalAiModelArtifact model);
    Task<bool> IsValidAsync(LocalAiModelArtifact model, CancellationToken ct);
    Task DownloadAsync(LocalAiModelArtifact model, Action<long> progress, CancellationToken ct);
}

/// <summary>仅下载固定制品并校验后原子发布</summary>
internal sealed class LocalAiModelDownloader(HttpClient http, string directory, Func<long>? availableBytes = null) : ILocalAiModelStore, IDisposable
{
    private const long MaxModelBytes = 2L * 1024 * 1024 * 1024;
    private const long DiskReserveBytes = 256L * 1024 * 1024;
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "huggingface.co", "cas-bridge.xethub.hf.co", "cdn-lfs.huggingface.co",
        "cdn-lfs.hf.co", "cdn-lfs-us-1.hf.co", "cdn-lfs-eu-1.hf.co"
    };

    public string PathFor(LocalAiModelArtifact model) => Path.Combine(directory, model.FileName);

    public bool IsPresent(LocalAiModelArtifact model)
    {
        PrivateFileSystem.RejectSymbolicLink(directory);
        string path = PathFor(model);
        PrivateFileSystem.RejectSymbolicLink(path);
        return model.CanVerify && File.Exists(path) && new FileInfo(path).Length == model.SizeBytes;
    }

    public async Task<bool> IsValidAsync(LocalAiModelArtifact model, CancellationToken ct)
    {
        if (!IsPresent(model)) return false;
        await using FileStream file = new(PathFor(model), FileMode.Open, FileAccess.Read, FileShare.Read);
        string digest = Convert.ToHexString(await SHA256.HashDataAsync(file, ct));
        return string.Equals(digest, model.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    public async Task DownloadAsync(LocalAiModelArtifact model, Action<long> progress, CancellationToken ct)
    {
        if (!model.CanDownload || model.SizeBytes > MaxModelBytes)
            throw new BusinessException(model.UnavailableReason ?? "模型制品超出下载限制");
        PrivateFileSystem.EnsureDirectory(directory);
        string destination = PathFor(model);
        if (await IsValidAsync(model, ct)) { progress(model.SizeBytes!.Value); return; }
        // 不覆盖已有文件；损坏的已发布文件需用户先移走，失败临时文件会自动清理。
        if (File.Exists(destination)) throw new BusinessException("模型文件校验失败，请先移走损坏的模型文件后重试");
        EnsureDiskSpace(model.SizeBytes!.Value);
        string temporary = Path.Combine(directory, ".download-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using HttpResponseMessage response = await OpenAsync(model.DownloadUri!, ct);
            if (response.Content.Headers.ContentLength is long declared && declared != model.SizeBytes)
                throw new BusinessException("模型下载大小与固定制品不符");
            await using Stream input = await response.Content.ReadAsStreamAsync(ct);
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            await using (FileStream output = PrivateFileSystem.CreateNew(temporary))
            {
                byte[] buffer = new byte[64 * 1024];
                int count;
                while ((count = await input.ReadAsync(buffer, ct)) != 0)
                {
                    total += count;
                    if (total > model.SizeBytes || total > MaxModelBytes)
                        throw new BusinessException("模型下载超出固定大小上限");
                    if (total % (16 * 1024 * 1024) < count) EnsureDiskSpace(model.SizeBytes.Value - total);
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), ct);
                    progress(total);
                }
                if (total != model.SizeBytes || !string.Equals(Convert.ToHexString(hash.GetHashAndReset()), model.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new BusinessException("模型下载 SHA256 或大小校验失败");
                await output.FlushAsync(ct);
                output.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            PrivateFileSystem.RejectSymbolicLink(destination);
            File.Move(temporary, destination, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private async Task<HttpResponseMessage> OpenAsync(Uri uri, CancellationToken ct)
    {
        for (int redirects = 0; redirects <= 4; redirects++)
        {
            if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || !AllowedHosts.Contains(uri.IdnHost))
                throw new BusinessException("模型下载重定向不在允许列表中");
            using HttpRequestMessage request = new(HttpMethod.Get, uri);
            HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.RequestMessage?.RequestUri is Uri actual && actual != uri)
            {
                response.Dispose();
                throw new BusinessException("模型下载禁止自动重定向");
            }
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.TemporaryRedirect
                or HttpStatusCode.PermanentRedirect or HttpStatusCode.SeeOther)
            {
                Uri? location = response.Headers.Location;
                response.Dispose();
                if (location is null) throw new BusinessException("模型下载重定向缺少目标");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                response.Dispose();
                throw new BusinessException("模型下载服务暂不可用，请稍后重试");
            }
            return response;
        }
        throw new BusinessException("模型下载重定向次数超出限制");
    }

    private void EnsureDiskSpace(long remaining)
    {
        long free = availableBytes?.Invoke() ?? DriveInfo.GetDrives()
            .Where(d => directory.StartsWith(d.Name, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            .OrderByDescending(d => d.Name.Length).First().AvailableFreeSpace;
        if (free < remaining + DiskReserveBytes) throw new BusinessException("模型下载磁盘空间不足（须另留 256 MiB）");
    }

    public void Dispose() => http.Dispose();
}
