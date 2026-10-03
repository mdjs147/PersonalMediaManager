using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Common.Diagnostics;
using PersonalMediaManager.Application.Contracts.LocalAi;
using PersonalMediaManager.Application.Dtos.LocalAi;
using PersonalMediaManager.Application.Services.LocalAi;

namespace PersonalMediaManager.Infrastructure.External.LocalAi;

/// <summary>单实例下载、子进程与推理生命周期</summary>
internal sealed class LocalAiRuntimeManager(ILocalAiSettingsService settingsService, ILocalAiModelStore downloader,
    ILocalAiProcessFactory processFactory, HttpClient localHttp) : ILocalAiRuntimeManager, ILocalAiInferenceClient, IDisposable
{
    private const int ResponseLimitBytes = 64 * 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _runCts;
    private CancellationTokenSource? _downloadCts;
    private ILocalAiChildProcess? _child;
    private Task? _monitor;
    private Task? _downloadTask;
    private LocalAiSettingsDto? _runningSettings;
    private string? _alias;
    private string _state = "Stopped";
    private string _message = "本地运行时尚未启动；llama-server 需另行安装";
    private string? _downloadId;
    private string _downloadState = "Idle";
    private string? _downloadError;
    private long _downloaded;
    private long? _downloadTotal;

    public Task<IReadOnlyList<LocalAiModelDto>> GetModelsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<LocalAiModelDto> result = LocalAiModelCatalog.Models.Select(model => new LocalAiModelDto(model.Id, model.Name,
            model.SourceUrl, model.Revision, model.SizeBytes, model.Sha256, model.CanDownload, model.UnavailableReason,
            downloader.IsPresent(model), model.CanVerify, model.FileName, downloader.PathFor(model), model.ConversionRevision)).ToArray();
        return Task.FromResult(result);
    }

    public async Task<LocalAiStatusDto> GetStatusAsync(CancellationToken ct = default)
    {
        LocalAiSettingsDto settings = await settingsService.GetAsync(ct);
        lock (_sync)
        {
            bool configured = File.Exists(settings.RuntimeExecutablePath);
            string message = !processFactory.PlatformSupported ? "本地运行时暂不支持当前平台或架构"
                : !configured ? "请先另行安装 llama-server，并配置有效的本机绝对路径" : _message;
            return new(_state, _runningSettings?.ModelId, configured, processFactory.PlatformSupported,
                message, _downloadId, _downloadState, _downloaded, _downloadTotal, _downloadError);
        }
    }

    public async Task DownloadAsync(string modelId, CancellationToken ct = default)
    {
        LocalAiModelArtifact model = LocalAiModelCatalog.Get(modelId);
        if (!model.CanDownload) throw new BusinessException(model.UnavailableReason!);
        await _gate.WaitAsync(ct);
        try
        {
            _lifetime.Token.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (_downloadTask is { IsCompleted: false }) throw new BusinessException("已有模型正在下载");
                if (_child is { HasExited: false }) throw new BusinessException("请先停止本地运行时再下载模型");
                _downloadCts?.Dispose();
                _downloadCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                _downloadCts.CancelAfter(TimeSpan.FromHours(1));
                CancellationToken token = _downloadCts.Token;
                _downloadId = model.Id;
                _downloadState = "Downloading";
                _downloadError = null;
                _downloaded = 0;
                _downloadTotal = model.SizeBytes;
                _downloadTask = Task.Run(() => DownloadCoreAsync(model, token), CancellationToken.None);
            }
        }
        finally { _gate.Release(); }
    }

    private async Task DownloadCoreAsync(LocalAiModelArtifact model, CancellationToken ct)
    {
        try
        {
            await downloader.DownloadAsync(model, bytes => { lock (_sync) _downloaded = bytes; }, ct);
            lock (_sync) _downloadState = "Completed";
        }
        catch (OperationCanceledException) { lock (_sync) _downloadState = "Cancelled"; }
        catch (Exception ex)
        {
            lock (_sync)
            {
                _downloadState = "Failed";
                _downloadError = ex is BusinessException ? ex.Message : "模型下载失败，请检查网络与磁盘后重试";
            }
        }
    }

    public async Task CancelDownloadAsync(CancellationToken ct = default)
    {
        Task? task;
        lock (_sync) { _downloadCts?.Cancel(); task = _downloadTask; }
        if (task is not null) await task.WaitAsync(ct);
    }

    public async Task UpdateSettingsAsync(LocalAiSettingsDto settings, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        settings.Validate();
        lock (_sync) _runCts?.Cancel();
        // 一旦取消旧运行令牌，就必须完成清理；网页断开不能留下失去监控的活子进程。
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            // 与启动、推理共享同一把锁，避免停止后持久化前被并发启动旧配置。
            await StopCoreAsync();
            ct.ThrowIfCancellationRequested();
            await settingsService.UpdateAsync(settings, ct);
        }
        finally { _gate.Release(); }
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            LocalAiSettingsDto settings = await settingsService.GetAsync(ct);
            settings.Validate();
            if (!processFactory.PlatformSupported) throw new BusinessException("本地运行时暂不支持当前平台或架构");
            if (!File.Exists(settings.RuntimeExecutablePath)) throw new BusinessException("请先另行安装 llama-server，并配置有效路径");
            lock (_sync)
            {
                if (_downloadTask is { IsCompleted: false }) throw new BusinessException("请等待模型下载完成");
                if (_child is { HasExited: false })
                {
                    if (_runningSettings == settings && _state == "Running") return;
                    throw new BusinessException("请先停止已有本地运行时");
                }
            }
            await StopCoreAsync();
            using CancellationTokenSource startup = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
            startup.CancelAfter(TimeSpan.FromSeconds(settings.StartupTimeoutSeconds));
            lock (_sync)
            {
                _runCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                _state = "Starting";
                _message = "正在校验模型并启动本地运行时";
            }
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(startup.Token, _runCts.Token);
            try
            {
                LocalAiModelArtifact model = LocalAiModelCatalog.Get(settings.ModelId);
                if (!await downloader.IsValidAsync(model, linked.Token)) throw new BusinessException("固定路径下的模型文件不存在，或大小 / SHA256 校验失败；原文件保留，解析将回退");
                // 先探测端口；不探测或复用他人已经启动的服务。
                if (!processFactory.IsPortAvailable(settings.Port)) throw new BusinessException("本地端口已被占用，请更换端口；不会连接现有服务");
                string alias = "pmm-" + Guid.NewGuid().ToString("N");
                ILocalAiChildProcess child = processFactory.Start(settings, downloader.PathFor(model), alias);
                lock (_sync) { _child = child; _alias = alias; _runningSettings = settings; }
                while (true)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    if (child.HasExited) throw new BusinessException("本地运行时启动后已退出，请检查运行时版本与依赖");
                    CheckMemory(child, settings);
                    if (await IsOwnedServerReadyAsync(settings.Port, alias, child, linked.Token)) break;
                    await Task.Delay(200, linked.Token);
                }
                lock (_sync)
                {
                    _state = "Running";
                    _message = "本地运行时健康，仅监听 127.0.0.1；内存保护为定期检测，并非系统硬限制";
                    _monitor = MonitorAsync(child, settings, _runCts.Token);
                }
            }
            catch (Exception ex)
            {
                await StopCoreAsync();
                string message = ex is BusinessException ? ex.Message : ex is OperationCanceledException
                    ? "本地运行时启动已取消或超时" : "本地运行时启动失败，请检查路径、版本与依赖";
                lock (_sync) { _state = "Faulted"; _message = message; }
                if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                throw new BusinessException(message);
            }
        }
        finally { _gate.Release(); }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        lock (_sync) _runCts?.Cancel();
        // 一旦收到停止请求，即使网页断开也完成自己子进程的清理。
        await _gate.WaitAsync(CancellationToken.None);
        try { await StopCoreAsync(); }
        finally { _gate.Release(); }
    }

    private async Task StopCoreAsync()
    {
        ILocalAiChildProcess? child;
        Task? monitor;
        lock (_sync) { _runCts?.Cancel(); child = _child; monitor = _monitor; }
        if (child is not null)
        {
            try { child.KillTree(); }
            catch (InvalidOperationException) { }
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            try { await child.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                lock (_sync) { _state = "Faulted"; _message = "本地子进程尚未退出，请重试停止"; }
                throw new BusinessException("本地子进程尚未退出，请重试停止");
            }
            if (monitor is not null) await monitor;
            child.Dispose();
        }
        lock (_sync)
        {
            _child = null;
            _monitor = null;
            _runCts?.Dispose();
            _runCts = null;
            _runningSettings = null;
            _alias = null;
            _state = "Stopped";
            _message = "本地运行时已停止";
        }
    }

    private async Task MonitorAsync(ILocalAiChildProcess child, LocalAiSettingsDto settings, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
                if (child.HasExited) throw new BusinessException("本地运行时已退出");
                CheckMemory(child, settings);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            lock (_sync)
            {
                _state = "Faulted";
                _message = "本地运行时已退出或超过内存保护阈值";
                _runCts?.Cancel();
            }
            try { child.KillTree(); } catch (InvalidOperationException) { }
        }
    }

    private static void CheckMemory(ILocalAiChildProcess child, LocalAiSettingsDto settings)
    {
        if (child.WorkingSetBytes > settings.MemoryLimitMb * 1024L * 1024)
            throw new BusinessException("本地运行时超过内存保护阈值，已停止");
    }

    private async Task<bool> IsOwnedServerReadyAsync(int port, string alias, ILocalAiChildProcess child, CancellationToken ct)
    {
        try
        {
            using CancellationTokenSource probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probe.CancelAfter(TimeSpan.FromSeconds(2));
            using HttpResponseMessage health = await localHttp.GetAsync(LoopbackUri(port, "health"), HttpCompletionOption.ResponseHeadersRead, probe.Token);
            if (!health.IsSuccessStatusCode || child.HasExited) return false;
            using HttpResponseMessage models = await localHttp.GetAsync(LoopbackUri(port, "v1/models"), HttpCompletionOption.ResponseHeadersRead, probe.Token);
            if (!models.IsSuccessStatusCode) return false;
            using JsonDocument json = JsonDocument.Parse(await ReadBoundedAsync(models, probe.Token));
            return !child.HasExited && json.RootElement.TryGetProperty("data", out JsonElement entries) && entries.ValueKind == JsonValueKind.Array
                && entries.EnumerateArray().Any(x => x.TryGetProperty("id", out JsonElement id) && id.GetString() == alias);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException or BusinessException)
        {
            ct.ThrowIfCancellationRequested();
            return false;
        }
    }

    public async Task<LocalAiInferenceResult> GenerateAsync(LocalAiInferenceRequest request, CancellationToken ct = default)
    {
        using IDisposable? diagnosticScope = ParseDiagnostics.CurrentRunId is null ? ParseDiagnostics.Begin("local_ai_runtime") : null;
        using IDisposable? physicalDiagnostic = ParseDiagnostics.CurrentRequestId is null ? ParseDiagnostics.BeginAiCall(Guid.NewGuid().ToString("N")) : null;
        Stopwatch elapsed = Stopwatch.StartNew();
        string? fingerprint = null;
        bool entered = false;
        bool attempted = false;
        LocalAiSettingsDto? settings = null;
        try
        {
            settings = await settingsService.GetAsync(ct);
            settings.Validate();
            if (settings.Mode == LocalAiMode.Disabled) return Failure("disabled");
            if (string.IsNullOrEmpty(request.SystemPrompt) || string.IsNullOrEmpty(request.UserPrompt)
                || request.SystemPrompt.Length + request.UserPrompt.Length > 16000 || request.MaxOutputTokens is < 1
                || request.AllowedSpanCount is < 0 or > 12
                || request.BatchSpanCounts is { } counts && (request.AllowedSpanCount is not null
                    || counts.Count is < 1 or > 2 || counts.Any(x => !Guid.TryParseExact(x.Key, "N", out _) || x.Value is < 0 or > 12)))
                return Failure("invalid_request");
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
            await _gate.WaitAsync(timeout.Token);
            entered = true;
            ILocalAiChildProcess child;
            string alias;
            CancellationToken runToken;
            lock (_sync)
            {
                if (_state != "Running" || _child is null || _child.HasExited || _runningSettings != settings || _alias is null || _runCts is null)
                    return Failure("not_ready");
                child = _child;
                alias = _alias;
                runToken = _runCts.Token;
            }
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, runToken);
            CheckMemory(child, settings);
            if (!await IsOwnedServerReadyAsync(settings.Port, alias, child, linked.Token)) return Failure("not_ready");
            int maxTokens = Math.Min(settings.MaxOutputTokens, request.MaxOutputTokens ?? settings.MaxOutputTokens);
            object responseFormat = request.BatchSpanCounts is null ? ResponseFormat(request.AllowedSpanCount)
                : BatchResponseFormat(request.BatchSpanCounts);
            object payload = new
            {
                model = alias,
                messages = new[] { new { role = "system", content = request.SystemPrompt }, new { role = "user", content = request.UserPrompt } },
                temperature = 0, max_tokens = maxTokens, stream = false, response_format = responseFormat,
            };
            DiagnosticText systemText = ParseDiagnostics.CaptureText(request.SystemPrompt);
            DiagnosticText userText = ParseDiagnostics.CaptureText(request.UserPrompt);
            DiagnosticText modelText = ParseDiagnostics.CaptureText(settings.ModelId, includeAtStandard: true);
            LocalAiModelArtifact artifact = LocalAiModelCatalog.Get(settings.ModelId);
            // 不能对原始 payload 生成诊断指纹，否则被省略的敏感值仍参与可导出的标识。
            fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                Protocol = "OpenAiCompatible", ModelSha256 = modelText.Sha256, ArtifactSha256 = artifact.Sha256, artifact.Revision,
                SystemSha256 = systemText.Sha256, UserSha256 = userText.Sha256,
                Temperature = 0, MaxTokens = maxTokens, Stream = false, ResponseFormat = responseFormat,
                settings.ContextTokens, settings.Threads, settings.TimeoutSeconds,
            }))));
            ParseDiagnostics.Emit("local_ai.runtime_request", new
            {
                Fingerprint = fingerprint, Stage = "prepared_not_yet_sent", Protocol = "OpenAiCompatible", Model = modelText,
                CatalogArtifactSha256 = artifact.Sha256, CatalogRevision = artifact.Revision,
                ArtifactVerification = "catalog_artifact_verified_at_startup",
                RuntimeModel = alias, Temperature = 0, MaxTokens = maxTokens, Stream = false,
                settings.TimeoutSeconds, settings.ContextTokens, settings.Threads, settings.MemoryLimitMb,
                ResponseFormat = responseFormat, System = systemText, User = userText,
            });
            using HttpRequestMessage message = new(HttpMethod.Post, LoopbackUri(settings.Port, "v1/chat/completions"))
            { Content = JsonContent.Create(payload) };
            await PersonalMediaManager.Infrastructure.External.Ai.AiDiagnosticHttp.RecordPreparedRequestAsync(message);
            linked.Token.ThrowIfCancellationRequested();
            attempted = true;
            Task<HttpResponseMessage> sending = localHttp.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            PersonalMediaManager.Infrastructure.External.Ai.AiDiagnosticHttp.RecordDispatch();
            using HttpResponseMessage response = await sending;
            ParseDiagnostics.Emit("local_ai.runtime_http", new
            { Fingerprint = fingerprint, HttpStatus = (int)response.StatusCode, ElapsedMs = elapsed.ElapsedMilliseconds });
            if (!response.IsSuccessStatusCode)
            {
                if (ParseDiagnostics.IsFull)
                {
                    // HTTP 失败已确定。诊断错误体使用独立短预算，不等待模型长预算或改变失败类别。
                    using CancellationTokenSource diagnosticRead = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                    diagnosticRead.CancelAfter(TimeSpan.FromMilliseconds(50));
                    try { await ReadBoundedAsync(response, diagnosticRead.Token, diagnostic: true); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        ct.ThrowIfCancellationRequested();
                        ParseDiagnostics.Emit("local_ai.error_body_omitted", new
                        {
                            reason = diagnosticRead.IsCancellationRequested ? "error_body_capture_budget_or_stop" : "error_body_capture_failed",
                            ExceptionType = ex.GetType().Name, HttpStatus = (int)response.StatusCode,
                        });
                    }
                    ct.ThrowIfCancellationRequested();
                }
                return Failure("http_error");
            }
            string raw = await ReadBoundedAsync(response, linked.Token, diagnostic: true);
            if (child.HasExited) return Failure("runtime_exited");
            using JsonDocument json = JsonDocument.Parse(raw);
            if (!json.RootElement.TryGetProperty("choices", out JsonElement choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() != 1)
                return Failure("invalid_response");
            JsonElement choice = choices[0];
            string? finish = choice.TryGetProperty("finish_reason", out JsonElement finishElement) ? finishElement.GetString() : null;
            // 业务只读取公开助手正文；Full 原包在独立隐私边界后保存，推理字段明确省略。
            string? content = choice.TryGetProperty("message", out JsonElement assistant)
                && assistant.TryGetProperty("content", out JsonElement text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
            ParseDiagnostics.Emit("local_ai.runtime_response", new
            {
                Fingerprint = fingerprint, FinishReason = ParseDiagnostics.CaptureText(finish, includeAtStandard: true),
                ElapsedMs = elapsed.ElapsedMilliseconds, Response = ParseDiagnostics.CaptureText(content),
            });
            if (finish != "stop") return Complete(new(null, finish == "length" ? "truncated" : "incomplete_response", finish, elapsed.ElapsedMilliseconds, settings.ModelId, attempted));
            if (string.IsNullOrWhiteSpace(content)) return Failure("empty_response");
            int? promptTokens = null, completionTokens = null;
            if (json.RootElement.TryGetProperty("usage", out JsonElement usage) && usage.ValueKind == JsonValueKind.Object)
            {
                if (usage.TryGetProperty("prompt_tokens", out JsonElement prompt) && prompt.ValueKind == JsonValueKind.Number
                    && prompt.TryGetInt32(out int p) && p >= 0) promptTokens = p;
                if (usage.TryGetProperty("completion_tokens", out JsonElement output) && output.ValueKind == JsonValueKind.Number
                    && output.TryGetInt32(out int c) && c >= 0) completionTokens = c;
            }
            return Complete(new(content, null, finish, elapsed.ElapsedMilliseconds, settings.ModelId, attempted,
                promptTokens, completionTokens, child.WorkingSetBytes));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            ParseDiagnostics.Emit("local_ai.runtime_cancelled", new
            { Fingerprint = fingerprint, ElapsedMs = elapsed.ElapsedMilliseconds, Attempted = attempted, CallerCancelled = true });
            throw;
        }
        catch (OperationCanceledException) { return Failure("timeout_or_stopped"); }
        catch (Exception ex)
        {
            ParseDiagnostics.Emit("local_ai.runtime_exception", new
            { Fingerprint = fingerprint, ExceptionType = ex.GetType().Name, ElapsedMs = elapsed.ElapsedMilliseconds });
            return Failure("local_unavailable");
        }
        finally { if (entered) _gate.Release(); }

        LocalAiInferenceResult Failure(string reason) => Complete(new(null, reason, null, elapsed.ElapsedMilliseconds, settings?.ModelId, attempted));
        LocalAiInferenceResult Complete(LocalAiInferenceResult result)
        {
            ParseDiagnostics.Emit("local_ai.runtime_completed", new
            {
                Fingerprint = fingerprint, result.Success, result.Attempted, result.FailureReason,
                ElapsedMs = elapsed.ElapsedMilliseconds,
            });
            return result;
        }
    }

    internal async Task ShutdownAsync()
    {
        _lifetime.Cancel();
        await CancelDownloadAsync();
        await StopAsync();
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        lock (_sync)
        {
            _downloadCts?.Cancel();
            _runCts?.Cancel();
            try { _child?.KillTree(); } catch (InvalidOperationException) { }
        }
        localHttp.Dispose();
    }

    private static Uri LoopbackUri(int port, string path) => new($"http://127.0.0.1:{port}/{path}");

    private static object ResponseFormat(int? allowedSpanCount)
    {
        if (allowedSpanCount is null) return new { type = "json_object" };
        int count = allowedSpanCount.Value;
        int?[] indices = new int?[count + 1];
        for (int i = 0; i < count; i++) indices[i + 1] = i;
        return new
        {
            type = "json_schema",
            json_schema = new
            {
                name = "literal_span_selection", strict = true,
                schema = new
                {
                    type = "object", properties = new { index = new { @enum = indices } },
                    required = new[] { "index" }, additionalProperties = false
                }
            }
        };
    }

    private static object BatchResponseFormat(IReadOnlyDictionary<string, int> counts)
    {
        int?[] indices = new int?[counts.Values.Max() + 1];
        for (int i = 1; i < indices.Length; i++) indices[i] = i - 1;
        return new
        {
            type = "json_schema", json_schema = new
            {
                name = "literal_span_batch_v1", strict = true,
                schema = new
                {
                    type = "object", properties = new
                    {
                        items = new
                        {
                            type = "array", minItems = counts.Count, maxItems = counts.Count,
                            items = new
                            {
                                type = "object", properties = new
                                {
                                    id = new { type = "string", @enum = counts.Keys.ToArray() },
                                    result = new { type = "object", properties = new { index = new { @enum = indices } },
                                        required = new[] { "index" }, additionalProperties = false }
                                },
                                required = new[] { "id", "result" }, additionalProperties = false
                            }
                        }
                    },
                    required = new[] { "items" }, additionalProperties = false
                }
            }
        };
    }

    private static async Task<string> ReadBoundedAsync(HttpResponseMessage response, CancellationToken ct, bool diagnostic = false)
    {
        if (diagnostic)
        {
            try { return await PersonalMediaManager.Infrastructure.External.Ai.AiResponseReader.ReadAsync(response.Content, ResponseLimitBytes, ct, httpStatus: (int)response.StatusCode); }
            catch (PersonalMediaManager.Application.Contracts.AiProviderLogicalException) { throw new BusinessException("本地模型响应超出大小限制"); }
        }
        if (response.Content.Headers.ContentLength is > ResponseLimitBytes) throw new BusinessException("本地模型响应超出大小限制");
        await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
        using MemoryStream output = new();
        byte[] buffer = new byte[4096];
        int count;
        while ((count = await stream.ReadAsync(buffer, ct)) != 0)
        {
            if (output.Length + count > ResponseLimitBytes) throw new BusinessException("本地模型响应超出大小限制");
            output.Write(buffer, 0, count);
        }
        return Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
    }
}
