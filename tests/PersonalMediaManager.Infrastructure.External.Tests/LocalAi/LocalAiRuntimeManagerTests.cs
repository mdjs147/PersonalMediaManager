using System.Diagnostics;
using System.Net;
using System.Text.Json;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Dtos.LocalAi;
using PersonalMediaManager.Application.Services.LocalAi;
using PersonalMediaManager.Infrastructure.External.LocalAi;

namespace PersonalMediaManager.Infrastructure.External.Tests.LocalAi;

public sealed partial class LocalAiRuntimeManagerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pmm-runtime-test-" + Guid.NewGuid().ToString("N"));
    private readonly Settings _settings;
    private readonly ProcessFactory _process = new();
    private readonly Store _store = new();
    private readonly LocalAiRuntimeManager _manager;
    private int _httpCalls;
    private Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? _completion;
    private string _finish = "stop";

    public LocalAiRuntimeManagerTests()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server");
        File.WriteAllText(path, string.Empty);
        _settings = new Settings(new() { Mode = LocalAiMode.AfterRules, RuntimeExecutablePath = path });
        _manager = new(_settings, _store, _process, new HttpClient(new Handler(RespondAsync)));
    }

    [Fact]
    public async Task OccupiedPort_DoesNotProbeOrStartOthers()
    {
        _process.PortAvailable = false;
        await Assert.ThrowsAsync<BusinessException>(() => _manager.StartAsync());
        _process.Starts.Should().Be(0);
        _httpCalls.Should().Be(0);
        _process.Child.Kills.Should().Be(0);
    }

    [Theory]
    [InlineData(LocalAiModelIds.Qwen)]
    [InlineData(LocalAiModelIds.Huihui)]
    public async Task InvalidModel_DoesNotStartProcess(string modelId)
    {
        _settings.Value = _settings.Value with { ModelId = modelId };
        _store.Valid = false;
        await Assert.ThrowsAsync<BusinessException>(() => _manager.StartAsync());
        _process.Starts.Should().Be(0);
        _httpCalls.Should().Be(0);
        (await _manager.GenerateAsync(new("指令", "数据"))).FailureReason.Should().Be("not_ready");
        (await _manager.GetStatusAsync()).State.Should().Be("Faulted");
    }

    [Fact]
    public async Task LocalOnlyHuihui_StartsAfterVerificationAndKeepsDisabledMode()
    {
        _settings.Value = _settings.Value with { ModelId = LocalAiModelIds.Huihui, Mode = LocalAiMode.Disabled };
        LocalAiModelDto model = (await _manager.GetModelsAsync()).Single(x => x.Id == LocalAiModelIds.Huihui);
        model.CanVerify.Should().BeTrue();
        model.CanDownload.Should().BeFalse();
        model.Installed.Should().BeTrue();
        model.LocalPath.Should().Be(_store.PathFor(LocalAiModelCatalog.Get(LocalAiModelIds.Huihui)));
        await Assert.ThrowsAsync<BusinessException>(() => _manager.DownloadAsync(LocalAiModelIds.Huihui));
        _store.Downloads.Should().Be(0);
        await _manager.StartAsync();
        _store.VerifiedModelId.Should().Be(LocalAiModelIds.Huihui);
        _process.StartedSettings!.ModelId.Should().Be(LocalAiModelIds.Huihui);
        (await _manager.GetStatusAsync()).State.Should().Be("Running");
        (await _manager.GenerateAsync(new("指令", "数据"))).FailureReason.Should().Be("disabled");
        _settings.Value.Mode.Should().Be(LocalAiMode.Disabled);
        _settings.Updates.Should().Be(0);
    }

    [Fact]
    public async Task SettingsSave_BlocksConcurrentStartUntilNewConfigurationIsPersisted()
    {
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _settings.BeforeUpdate = async () => { entered.SetResult(); await release.Task; };
        LocalAiSettingsDto updated = _settings.Value with { Port = 18123, Mode = LocalAiMode.Disabled };
        Task save = _manager.UpdateSettingsAsync(updated);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Task start = _manager.StartAsync();
        _process.Starts.Should().Be(0);
        release.SetResult();
        await save;
        await start;
        _process.StartedSettings.Should().Be(updated);
        _settings.Updates.Should().Be(1);
    }

    [Fact]
    public async Task AlreadyCancelledSaveDoesNotInterruptHealthyRuntime()
    {
        await _manager.StartAsync();
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _manager.UpdateSettingsAsync(
            _settings.Value with { Port = 18123 }, cancellation.Token));
        _process.Child.Kills.Should().Be(0);
        _settings.Updates.Should().Be(0);
        (await _manager.GetStatusAsync()).State.Should().Be("Running");
        (await _manager.GenerateAsync(new("指令", "数据"))).Success.Should().BeTrue();
    }

    [Fact]
    public async Task SaveCancellationWhileWaitingForInferenceStillCleansOwnProcess()
    {
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _completion = async (_, _) => { entered.SetResult(); await release.Task; return Completion(); };
        await _manager.StartAsync();
        Task<LocalAiInferenceResult> inference = _manager.GenerateAsync(new("指令", "数据"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        using CancellationTokenSource cancellation = new();
        Task save = _manager.UpdateSettingsAsync(_settings.Value with { Port = 18123 }, cancellation.Token);
        cancellation.Cancel(); release.SetResult();
        await inference;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => save);
        _settings.Updates.Should().Be(0);
        _process.Child.Kills.Should().Be(1);
        (await _manager.GetStatusAsync()).State.Should().Be("Stopped");
    }

    [Fact]
    public async Task UserCancelledInferencePropagatesWithoutStoppingRuntime()
    {
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _completion = async (_, ct) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); return Completion(); };
        await _manager.StartAsync();
        using CancellationTokenSource cancellation = new();
        Task<LocalAiInferenceResult> inference = _manager.GenerateAsync(new("指令", "数据"), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inference);
        _process.Child.Kills.Should().Be(0);
        (await _manager.GetStatusAsync()).State.Should().Be("Running");
    }

    [Fact]
    public async Task StartupCancellation_KillsOwnChild()
    {
        using CancellationTokenSource cancellation = new();
        _process.AfterStart = () => cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _manager.StartAsync(cancellation.Token));
        _process.Child.Kills.Should().Be(1);
        _process.Child.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task MemoryThresholdDuringStartup_KillsOwnChild()
    {
        _process.Child.WorkingSetBytes = 5L * 1024 * 1024 * 1024;
        await Assert.ThrowsAsync<BusinessException>(() => _manager.StartAsync());
        _process.Child.Kills.Should().Be(1);
    }

    [Fact]
    public async Task ExplicitStartInDisabledMode_DoesNotEnableInference()
    {
        _settings.Value = _settings.Value with { Mode = LocalAiMode.Disabled };
        await _manager.StartAsync();
        (await _manager.GetStatusAsync()).State.Should().Be("Running");
        LocalAiInferenceResult result = await _manager.GenerateAsync(new("指令", "数据"));
        result.FailureReason.Should().Be("disabled");
        result.Attempted.Should().BeFalse();
        _settings.Value.Mode.Should().Be(LocalAiMode.Disabled);
    }

    [Fact]
    public async Task Completion_IsLoopbackCredentialFreeAndBounded()
    {
        string? body = null;
        _completion = async (request, ct) =>
        {
            request.RequestUri!.Host.Should().Be("127.0.0.1");
            request.Headers.Authorization.Should().BeNull();
            body = await request.Content!.ReadAsStringAsync(ct);
            return Completion();
        };
        await _manager.StartAsync();
        LocalAiInferenceResult result = await _manager.GenerateAsync(new("指令", "数据", 9999));
        result.Success.Should().BeTrue();
        result.Attempted.Should().BeTrue();
        result.ModelId.Should().Be(LocalAiModelIds.Qwen);
        using JsonDocument json = JsonDocument.Parse(body!);
        json.RootElement.GetProperty("max_tokens").GetInt32().Should().Be(_settings.Value.MaxOutputTokens);
        json.RootElement.GetProperty("temperature").GetInt32().Should().Be(0);
        json.RootElement.GetProperty("stream").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("response_format").GetProperty("type").GetString().Should().Be("json_object");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(12)]
    public async Task SpanSelection_UsesOnlyFixedSchemaAndBoundedIndices(int count)
    {
        string? body = null;
        _completion = async (request, ct) => { body = await request.Content!.ReadAsStringAsync(ct); return Completion(); };
        await _manager.StartAsync();
        (await _manager.GenerateAsync(new("指令", "数据", 32, count))).Success.Should().BeTrue();
        using JsonDocument json = JsonDocument.Parse(body!);
        json.RootElement.GetProperty("max_tokens").GetInt32().Should().Be(32);
        json.RootElement.GetProperty("temperature").GetInt32().Should().Be(0);
        JsonElement format = json.RootElement.GetProperty("response_format");
        format.GetProperty("type").GetString().Should().Be("json_schema");
        JsonElement contract = format.GetProperty("json_schema");
        contract.GetProperty("name").GetString().Should().Be("literal_span_selection");
        contract.GetProperty("strict").GetBoolean().Should().BeTrue();
        JsonElement schema = contract.GetProperty("schema");
        schema.GetProperty("type").GetString().Should().Be("object");
        schema.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        schema.GetProperty("required").EnumerateArray().Select(x => x.GetString()).Should().Equal("index");
        JsonElement properties = schema.GetProperty("properties");
        properties.EnumerateObject().Select(x => x.Name).Should().Equal("index");
        JsonElement indices = properties.GetProperty("index").GetProperty("enum");
        indices.GetArrayLength().Should().Be(count + 1);
        indices[0].ValueKind.Should().Be(JsonValueKind.Null);
        indices.EnumerateArray().Skip(1).Select(x => x.GetInt32()).Should().Equal(Enumerable.Range(0, count));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(13)]
    public async Task InvalidSpanCount_DoesNotSendPrompt(int count)
    {
        await _manager.StartAsync();
        int calls = _httpCalls;
        LocalAiInferenceResult result = await _manager.GenerateAsync(new("指令", "数据", 32, count));
        result.FailureReason.Should().Be("invalid_request");
        result.Attempted.Should().BeFalse();
        _httpCalls.Should().Be(calls);
    }

    [Fact]
    public async Task TruncatedResponse_IsRejected()
    {
        _finish = "length";
        await _manager.StartAsync();
        LocalAiInferenceResult result = await _manager.GenerateAsync(new("指令", "数据"));
        result.Success.Should().BeFalse();
        result.Content.Should().BeNull();
        result.FailureReason.Should().Be("truncated");
        result.FinishReason.Should().Be("length");
        result.Attempted.Should().BeTrue();
    }

    [Fact]
    public async Task ChangedSettings_RequireRestartWithoutSendingPrompt()
    {
        await _manager.StartAsync();
        _settings.Value = _settings.Value with { Port = 18082 };
        LocalAiInferenceResult result = await _manager.GenerateAsync(new("指令", "数据"));
        result.FailureReason.Should().Be("not_ready");
        result.Attempted.Should().BeFalse();
    }

    [Fact]
    public async Task Stop_CancelsInflightInferenceAndKillsOwnedChild()
    {
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _completion = async (_, ct) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return Completion();
        };
        await _manager.StartAsync();
        Task<LocalAiInferenceResult> inference = _manager.GenerateAsync(new("指令", "数据"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await _manager.StopAsync();
        LocalAiInferenceResult result = await inference;
        result.Success.Should().BeFalse();
        result.Attempted.Should().BeTrue();
        _process.Child.Kills.Should().Be(1);
        _process.Child.Disposed.Should().BeTrue();
        (await _manager.GetStatusAsync()).State.Should().Be("Stopped");
    }

    [Fact]
    public async Task Shutdown_KillsOwnProcessAndNeverEnablesMode()
    {
        await _manager.StartAsync();
        await _manager.ShutdownAsync();
        _process.Child.Kills.Should().Be(1);
        _settings.Updates.Should().Be(0);
    }

    [Fact]
    public async Task OversizedOrHtmlResponse_FailsWithoutEchoingResponse()
    {
        _completion = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(new string('x', 70 * 1024)) });
        await _manager.StartAsync();
        LocalAiInferenceResult result = await _manager.GenerateAsync(new("指令", "数据"));
        result.FailureReason.Should().Be("local_unavailable");
        result.Content.Should().BeNull();
        result.Attempted.Should().BeTrue();
    }

    [Fact]
    public void ProcessArguments_AreFixedAndSecretsNotInherited()
    {
        ProcessStartInfo info = LocalAiProcessFactory.CreateStartInfo(_settings.Value, _settings.Value.RuntimeExecutablePath,
            Path.Combine(_directory, "模型 有空格.gguf"), "pmm-test");
        info.UseShellExecute.Should().BeFalse();
        info.Arguments.Should().BeEmpty();
        info.ArgumentList.Should().Contain("127.0.0.1").And.Contain("--offline").And.Contain("--no-webui");
        info.ArgumentList.Should().Contain(Path.Combine(_directory, "模型 有空格.gguf"));
        info.Environment.Keys.Should().OnlyContain(key => new[] { "PATH", "SystemRoot", "WINDIR", "TEMP", "TMP", "LANG" }.Contains(key));
    }

    private Task<HttpResponseMessage> RespondAsync(HttpRequestMessage request, CancellationToken ct)
    {
        _httpCalls++;
        if (request.RequestUri!.AbsolutePath == "/health") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        if (request.RequestUri.AbsolutePath == "/v1/models") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(new { data = new[] { new { id = _process.Alias } } })) });
        return _completion?.Invoke(request, ct) ?? Task.FromResult(Completion());
    }

    private HttpResponseMessage Completion() => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = _finish, message = new { content = "{\"candidates\":[]}" } } } })) };

    public void Dispose()
    {
        _manager.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private sealed class Settings(LocalAiSettingsDto value) : ILocalAiSettingsService
    {
        public LocalAiSettingsDto Value { get; set; } = value;
        public int Updates { get; private set; }
        public Func<Task>? BeforeUpdate { get; set; }
        public Task<LocalAiSettingsDto> GetAsync(CancellationToken ct = default) => Task.FromResult(Value);
        public async Task UpdateAsync(LocalAiSettingsDto settings, CancellationToken ct = default)
        {
            if (BeforeUpdate is not null) await BeforeUpdate();
            Updates++;
            Value = settings;
        }
    }
    private sealed class Store : ILocalAiModelStore
    {
        public bool Valid { get; set; } = true;
        public string? VerifiedModelId { get; private set; }
        public int Downloads { get; private set; }
        public string PathFor(LocalAiModelArtifact model) => "model.gguf";
        public bool IsPresent(LocalAiModelArtifact model) => Valid;
        public Task<bool> IsValidAsync(LocalAiModelArtifact model, CancellationToken ct)
        { VerifiedModelId = model.Id; return Task.FromResult(Valid); }
        public Task DownloadAsync(LocalAiModelArtifact model, Action<long> progress, CancellationToken ct)
        { Downloads++; return Task.CompletedTask; }
    }
    private sealed class ProcessFactory : ILocalAiProcessFactory
    {
        public bool PlatformSupported => true;
        public bool PortAvailable { get; set; } = true;
        public bool IsPortAvailable(int port) => PortAvailable;
        public Child Child { get; } = new();
        public int Starts { get; private set; }
        public string? Alias { get; private set; }
        public LocalAiSettingsDto? StartedSettings { get; private set; }
        public Action? AfterStart { get; set; }
        public ILocalAiChildProcess Start(LocalAiSettingsDto settings, string modelPath, string alias)
        { Starts++; Alias = alias; StartedSettings = settings; AfterStart?.Invoke(); return Child; }
    }
    private sealed class Child : ILocalAiChildProcess
    {
        public bool HasExited { get; private set; }
        public long WorkingSetBytes { get; set; } = 1;
        public int Kills { get; private set; }
        public bool Disposed { get; private set; }
        public void KillTree() { if (!HasExited) { Kills++; HasExited = true; } }
        public Task WaitForExitAsync(CancellationToken ct) => Task.CompletedTask;
        public void Dispose() => Disposed = true;
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    }
}
