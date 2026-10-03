using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Common.Diagnostics;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.DependencyInjection;
using PersonalMediaManager.Application.Dtos.Ai;
using PersonalMediaManager.Application.Services.Audit;
using PersonalMediaManager.Application.Services.Parse;
using PersonalMediaManager.Application.Services.Webhook;
using PersonalMediaManager.Domain.Aggregates.AiProviders;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Host.HostedServices;
using PersonalMediaManager.Infrastructure.External.DependencyInjection;
using PersonalMediaManager.Infrastructure.Persistence;
using PersonalMediaManager.Infrastructure.Persistence.DependencyInjection;

namespace PersonalMediaManager.Host.Tests.HostedServices;

/// <summary>大批次经过真实工作器、编排、解析与 HTTP 协议</summary>
public sealed class LargeAiBatchWorkerTests
{
    private const int ItemCount = 128;
    private const long ProviderId = 41;
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkerMapsAll128ReversedResultsAndSplitsOnlyWhenProviderBudgetRequires(bool useAdvancedPreset)
    {
        AiProviderResolution route = Resolution(ProviderId, useAdvancedPreset ? "deepseek-flash" : "synthetic-model",
            useAdvancedPreset ? "https://api.deepseek.com" : "https://fixture.invalid");
        AiProviderBatchSettingsDto limits = useAdvancedPreset
            ? AiBatchProviderPresets.Describe(route.ProviderId, route.Name, route.Type, route.Endpoint.BaseUrl,
                route.Endpoint.Model).AdvancedSettings!
            : ProviderLimits(route, ItemCount);
        limits.Should().NotBeNull();
        limits.BatchSize.Should().Be(ItemCount);
        // 全局仍为单条，工作器与解析器必须读到同一份绑定提供商的覆盖。
        AiBatchSettingsDto settings = new() { ExternalBatchSize = 1, MaxWaitMilliseconds = 1000,
            ProviderSettings = [limits] };
        await using Harness harness = new(settings, _ => route);

        await harness.RunAsync();

        AssertLifecycle(harness);
        AssertSuccessfulItems(harness, Enumerable.Range(0, ItemCount));
        AssertEnvelopeIdentity(harness);
        PhysicalRequest[] requests = harness.Handler.Requests.ToArray();
        if (useAdvancedPreset)
        {
            requests.Length.Should().BeGreaterThan(1, "128 是条数上限，预设输出预算仍须拆包");
            requests.Should().OnlyContain(request => request.Items.Length < ItemCount);
            harness.State.Events.Should().Contain(value => value.Name == "ai.batch_split");
        }
        else
        {
            requests.Should().ContainSingle("自定义预算足够时不能仍被旧的小批上限截断");
            requests[0].Items.Should().HaveCount(ItemCount);
        }
        requests.Should().OnlyContain(request => request.IsBatch && request.MaxTokens <= limits.MaxOutputTokens);
        harness.State.Continued.Should().Equal(Enumerable.Range(0, ItemCount));
        harness.State.Audits.Should().HaveCount(ItemCount);
        harness.State.Audits.Select(entry => entry.MediaItemId).Should().OnlyHaveUniqueItems();
        harness.State.Audits.Select(entry => entry.ChainId).Should().OnlyHaveUniqueItems();
        harness.State.Audits.Sum(entry => entry.PromptTokens ?? 0).Should().Be(11 * requests.Length);
        harness.State.Audits.Sum(entry => entry.CompletionTokens ?? 0).Should().Be(7 * requests.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkerDoesNotCombineDifferentProvidersOrModels(bool changeModelOnly)
    {
        AiProviderResolution first = Resolution(ProviderId, "synthetic-a");
        AiProviderResolution second = changeModelOnly
            ? Resolution(ProviderId, "synthetic-b")
            : Resolution(ProviderId + 1, "synthetic-a");
        await using Harness harness = new(CustomSettings(), index => index % 2 == 0 ? first : second);

        await harness.RunAsync();

        AssertLifecycle(harness);
        AssertSuccessfulItems(harness, Enumerable.Range(0, ItemCount));
        AssertEnvelopeIdentity(harness);
        PhysicalRequest[] requests = harness.Handler.Requests.ToArray();
        requests.Should().HaveCount(2);
        foreach (PhysicalRequest request in requests)
        {
            request.Items.Should().HaveCount(ItemCount / 2);
            request.Items.Select(item => harness.State.Work[item.Index].Route.ProviderId).Should().OnlyContain(id => id == request.ProviderId);
            request.Items.Select(item => harness.State.Work[item.Index].Route.Endpoint.Model).Should().OnlyContain(model => model == request.Model);
            request.Items.Select(item => item.Index % 2).Distinct().Should().ContainSingle();
        }
        if (changeModelOnly) requests.Select(request => request.Model).Should().BeEquivalentTo(new[] { "synthetic-a", "synthetic-b" });
        else requests.Select(request => request.ProviderId).Should().BeEquivalentTo(new[] { ProviderId, ProviderId + 1 });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationOrHttp429DoesNotFanOutInto128FallbackRequests(bool cancelAll)
    {
        await using Harness harness = new(CustomSettings(), _ => Resolution(ProviderId, "synthetic-model"),
            cancelAll ? ReplyMode.WaitForCancellation : ReplyMode.RateLimited);
        Task[] cancelledCompletions = [];

        await harness.RunAsync(cancelAll ? async () =>
        {
            await harness.Handler.Entered.Task.WaitAsync(TestTimeout);
            harness.Handler.Requests.Single().Items.Should().HaveCount(ItemCount);
            cancelledCompletions = harness.State.Work.Select(work => harness.Cancellation.RequestCancellation(work.Path)
                ?? throw new InvalidOperationException("128 个工作项都应已登记独立取消令牌")).ToArray();
            await Task.WhenAll(cancelledCompletions).WaitAsync(TestTimeout);
        } : null);

        AssertLifecycle(harness);
        harness.Handler.Requests.Should().ContainSingle("取消和 429 都不得立即放大成逐项 HTTP 回退");
        harness.Handler.Requests.Single().Items.Should().HaveCount(ItemCount);
        if (cancelAll)
        {
            harness.State.Cancelled.Order().Should().Equal(Enumerable.Range(0, ItemCount));
            harness.State.Outcomes.Should().BeEmpty();
            cancelledCompletions.Should().OnlyContain(task => task.IsCompletedSuccessfully);
            harness.Handler.CancelledRequests.Should().Be(1);
        }
        else
        {
            harness.State.Cancelled.Should().BeEmpty();
            harness.State.Outcomes.Should().HaveCount(ItemCount);
            harness.State.Outcomes.Values.Should().OnlyContain(result => !result.Success
                && result.Attempts != null && result.Attempts.Count == 1 && result.Attempts[0].ErrorType == "RateLimit");
            harness.State.Audits.Should().HaveCount(ItemCount);
            harness.State.Audits.Should().OnlyContain(entry => entry.HttpStatus == 429 && entry.ErrorType == "RateLimit");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SqliteLastQuotaAllowsOnePhysicalChunkAndOwnerCancellationDoesNotCancelSiblings(bool cancelOwner)
    {
        string connectionString = $"Data Source=large-ai-quota-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using SqliteConnection keeper = new(connectionString);
        await keeper.OpenAsync();
        // 保留 128 个准备槽，但每个物理包仅允许两项，强制覆盖发送前的多包额度检查。
        AiProviderResolution route = Resolution(ProviderId, "synthetic-model");
        AiBatchSettingsDto settings = CustomSettings() with { ProviderSettings = [ProviderLimits(route, 2)] };
        await using Harness harness = new(settings, _ => route, connectionString: connectionString);
        IDbContextFactory<PmmDbContext> factory = harness.Provider.GetRequiredService<IDbContextFactory<PmmDbContext>>();
        await using (PmmDbContext context = await factory.CreateDbContextAsync())
        {
            await context.Database.EnsureCreatedAsync();
            context.ParseAiProviders.Add(new ParseAiProvider { Id = ProviderId, Name = "合成剩余一次额度",
                Type = AiProviderType.OpenAiCompatible, BaseUrl = route.Endpoint.BaseUrl, Model = route.Endpoint.Model,
                Enabled = true, QuotaCallLimit = 8, QuotaUsedCalls = 7, QuotaUsedTokens = 100 });
            await context.SaveChangesAsync();
        }
        Task? ownerCompletion = null;
        int? ownerIndex = null;
        if (cancelOwner)
            harness.Handler.BeforeReply = request =>
            {
                ownerIndex = request.Items[0].Index;
                ownerCompletion = harness.Cancellation.RequestCancellation(harness.State.Work[ownerIndex.Value].Path);
            };

        await harness.RunAsync();

        AssertLifecycle(harness);
        PhysicalRequest sent = harness.Handler.Requests.Should().ContainSingle("额度必须在下一包进入 HTTP handler 前被拒绝").Which;
        sent.Items.Should().HaveCount(2);
        int[] expectedSuccess = sent.Items.Select(item => item.Index).Where(index => index != ownerIndex).ToArray();
        AssertSuccessfulItems(harness, expectedSuccess);
        harness.State.Outcomes.Values.Count(result => !result.Success).Should().Be(ItemCount - sent.Items.Length);
        harness.State.Outcomes.Where(pair => !pair.Value.Success).Select(pair => pair.Key)
            .Should().BeEquivalentTo(Enumerable.Range(0, ItemCount).Except(sent.Items.Select(item => item.Index)));
        harness.State.Outcomes.Values.Where(result => !result.Success).Should().OnlyContain(result =>
            result.Attempts != null && result.Attempts.Count == 1 && result.Attempts[0].ErrorType == "RateLimit");
        if (cancelOwner)
        {
            ownerCompletion.Should().NotBeNull();
            await ownerCompletion!.WaitAsync(TestTimeout);
            harness.State.Cancelled.Should().Equal(ownerIndex!.Value);
            harness.Handler.CancelledRequests.Should().Be(0, "取消共享请求所属项不得取消仍等待结果的兄弟项");
            harness.State.Outcomes.Should().HaveCount(ItemCount - 1);
        }
        else
        {
            harness.State.Cancelled.Should().BeEmpty();
            harness.State.Outcomes.Should().HaveCount(ItemCount);
        }
        await using PmmDbContext verify = await factory.CreateDbContextAsync();
        ParseAiProvider stored = await verify.ParseAiProviders.AsNoTracking().SingleAsync();
        stored.QuotaUsedCalls.Should().Be(8);
        stored.QuotaUsedTokens.Should().Be(118, "共享包的 11+7 token 只结算一次，owner 取消也不得遗漏或重复");
        stored.QuotaExceededAt.Should().NotBeNull();
    }

    [Fact]
    public async Task DeferredFallbackSurvivesSlowSerialContinuationWithoutTimingOutAndEscalating()
    {
        AiProviderResolution primary = Resolution(ProviderId, "synthetic-primary");
        primary = primary with { Endpoint = primary.Endpoint with { TimeoutSeconds = 1 } };
        AiProviderResolution secondary = Resolution(ProviderId + 1, "synthetic-secondary");
        State state = new(_ => primary);
        StubHttpHandler handler = new(state, ReplyMode.MalformedBatch);
        AiBatchSettingsDto settings = CustomSettings() with { ExternalBatchSize = 6 };
        IAiBatchSettingsService batchSettings = Substitute.For<IAiBatchSettingsService>();
        batchSettings.GetAsync(Arg.Any<CancellationToken>()).Returns(settings);
        IAiProviderResolver resolver = Substitute.For<IAiProviderResolver>();
        resolver.ResolveOrderedAsync(Arg.Any<CancellationToken>()).Returns(new[] { primary, secondary });
        ServiceCollection services = new();
        services.AddLogging();
        services.AddApplication();
        services.AddInfrastructureExternal();
        services.RemoveAll<IHttpClientFactory>();
        services.AddSingleton<IHttpClientFactory>(_ => new StubHttpClientFactory(handler));
        services.AddSingleton(batchSettings);
        services.AddSingleton(resolver);
        services.AddSingleton<IAuditAiCallWriter>(new RecordingAudit(state));
        services.AddSingleton(Substitute.For<IAiProviderQuotaTracker>());
        services.AddSingleton(Substitute.For<IAiProviderHealthTracker>());
        services.AddSingleton(Substitute.For<IAiProviderRpmGate>());
        services.AddSingleton(Substitute.For<IAlertService>());
        await using ServiceProvider provider = services.BuildServiceProvider();
        ConcurrentDictionary<int, Exception> failures = new();
        ConcurrentQueue<bool> deferredAfterSlowContinuation = new();
        bool slowContinuationFinished = false;
        using CancellationTokenSource timeout = new(TestTimeout);

        await AiBatchPipeline.RunAsync(Enumerable.Range(0, 6).Select<int, Func<CancellationToken, Task>>(index => async ct =>
        {
            using IServiceScope scope = provider.CreateScope();
            WorkItem work = state.Work[index];
            using IDisposable diagnostic = ParseDiagnostics.Begin("synthetic_file", work.RunId, index + 1, state);
            try
            {
                AiCallOutcome outcome = await scope.ServiceProvider.GetRequiredService<IAiCallOrchestrator>()
                    .ExecuteAsync(new(Path.GetFileName(work.Path)), index + 1, ct);
                state.Outcomes[index] = outcome;
                if (index == 0 && outcome.Success)
                {
                    // 首项非 AI 续行超过兄弟项的 1 秒内部预算，已确定的 deferred 不能因此变为超时升级。
                    await Task.Delay(TimeSpan.FromMilliseconds(1200), ct);
                    slowContinuationFinished = true;
                }
            }
            catch (Exception ex)
            {
                failures[index] = ex;
                if (ex is AiProviderBatchDeferredException) deferredAfterSlowContinuation.Enqueue(slowContinuationFinished);
            }
        }).ToArray(), settings.ToOptions(), timeout.Token).WaitAsync(TestTimeout);

        slowContinuationFinished.Should().BeTrue();
        state.Outcomes.Keys.Should().BeEquivalentTo(new[] { 0, 1, 2, 3 });
        state.Outcomes.Values.Should().OnlyContain(outcome => outcome.Success && outcome.WinningProviderId == ProviderId);
        failures.Keys.Should().BeEquivalentTo(new[] { 4, 5 });
        foreach (Exception failure in failures.Values) failure.Should().BeOfType<AiProviderBatchDeferredException>();
        deferredAfterSlowContinuation.Should().Equal(true, true);
        PhysicalRequest[] requests = handler.Requests.ToArray();
        requests.Count(request => request.Model == secondary.Endpoint.Model).Should().Be(0,
            "预算 deferred 不应因等待串行续行超时而偷偷升级到备用提供商");
        requests.Count(request => request.Model == primary.Endpoint.Model).Should().Be(5,
            "只允许一个坏共享包加四次预算内单项回退");
        requests.Where(request => request.IsBatch).Should().ContainSingle().Which.Items.Should().HaveCount(6);
        requests.Where(request => !request.IsBatch).SelectMany(request => request.Items).Select(row => row.Index)
            .Should().Equal(0, 1, 2, 3);
    }

    private static AiProviderResolution Resolution(long providerId, string model, string baseUrl = "https://fixture.invalid") =>
        new(providerId, AiProviderType.OpenAiCompatible, "合成提供商", true,
            new(baseUrl, null, model, TimeoutSeconds: 30, IsFree: true));

    private static AiProviderBatchSettingsDto ProviderLimits(AiProviderResolution route, int batchSize) => new()
    {
        ProviderId = route.ProviderId,
        ConfigurationKey = AiBatchProviderPresets.ConfigurationKey(route.Type, route.Endpoint.BaseUrl, route.Endpoint.Model),
        BatchSize = batchSize, ContextTokenBudget = 1048576, MaxOutputTokens = 262144, MaxResponseBytes = 8388608,
    };

    private static AiBatchSettingsDto CustomSettings() => new()
    {
        ExternalBatchSize = ItemCount, LocalBatchSize = 1, MaxWaitMilliseconds = 1000,
        ContextTokenBudget = 1048576, MaxOutputTokens = 262144, MaxResponseBytes = 8388608,
    };

    private static void AssertLifecycle(Harness harness)
    {
        harness.State.Errors.Should().BeEmpty();
        harness.State.Started.Should().Equal(Enumerable.Range(0, ItemCount));
        harness.State.ScopeIds.Should().HaveCount(ItemCount).And.OnlyHaveUniqueItems();
        harness.State.DisposedScopeIds.Should().BeEquivalentTo(harness.State.ScopeIds);
        harness.State.Orchestrators.Distinct(ReferenceEqualityComparer.Instance).Should().HaveCount(ItemCount);
        harness.State.RunningStages.Should().Be(0);
        harness.State.PeakStages.Should().Be(1, "只有 AI 等待点允许挂起，前置和后置业务阶段必须逐项运行");
        harness.State.RunIds.Values.Should().HaveCount(ItemCount).And.OnlyHaveUniqueItems();
        harness.State.ResumedRunIds.Should().HaveCount(ItemCount);
        foreach ((int index, string? runId) in harness.State.ResumedRunIds)
            runId.Should().Be(harness.State.Work[index].RunId, "续行不能继承共享请求 owner 的 RunId");
        foreach (WorkItem work in harness.State.Work)
        {
            harness.State.Events.Should().Contain(value => value.Name == "ai.provider_started" && value.RunId == work.RunId);
            harness.State.Events.Where(value => value.RunId == work.RunId)
                .Should().OnlyContain(value => value.MediaItemId == work.Index + 1);
        }
    }

    private static void AssertSuccessfulItems(Harness harness, IEnumerable<int> expected)
    {
        int[] indices = expected.ToArray();
        harness.State.Outcomes.Where(pair => pair.Value.Success).Select(pair => pair.Key).Should().BeEquivalentTo(indices);
        foreach (int index in indices)
        {
            AiCallOutcome result = harness.State.Outcomes[index];
            result.ProvidersAttempted.Should().Be(1);
            result.WinningProviderId.Should().Be(harness.State.Work[index].Route.ProviderId);
            result.Result.Should().NotBeNull();
            result.Result!.Title.Should().Be(harness.State.Work[index].Title);
            result.Result.MediaType.Should().Be("movie");
        }
    }

    private static void AssertEnvelopeIdentity(Harness harness)
    {
        PhysicalRequest[] requests = harness.Handler.Requests.ToArray();
        requests.SelectMany(request => request.Items).Select(item => item.Index).Should().BeEquivalentTo(Enumerable.Range(0, ItemCount));
        requests.SelectMany(request => request.Items).Select(item => item.Id).Should().HaveCount(ItemCount).And.OnlyHaveUniqueItems();
        foreach (PhysicalRequest request in requests)
        {
            request.IsBatch.Should().BeTrue("合法结果不得退回单项发送");
            request.ReplyIds.Should().Equal(request.Items.Reverse().Select(item => item.Id));
            request.RequestId.Should().NotBeNullOrWhiteSpace();
            foreach (BatchRow row in request.Items)
            {
                row.Id.Should().NotBeNullOrWhiteSpace();
                ParseDiagnosticEvent scheduled = harness.State.Events.Single(value => value.Name == "ai.batch_scheduled"
                    && value.RunId == harness.State.Work[row.Index].RunId);
                scheduled.Data.GetProperty("itemId").GetString().Should().Be(row.Id);
            }
        }
    }

    private enum ReplyMode { Success, RateLimited, WaitForCancellation, MalformedBatch }
    private sealed record WorkItem(int Index, string Title, string Path, string RunId, AiProviderResolution Route);
    private sealed record BatchRow(string? Id, int Index);
    private sealed record PhysicalRequest(bool IsBatch, BatchRow[] Items, string?[] ReplyIds, long ProviderId,
        string Model, int MaxTokens, string? RequestId);

    private sealed class Harness : IAsyncDisposable
    {
        public State State { get; }
        public StubHttpHandler Handler { get; }
        public ServiceProvider Provider { get; }
        public TaskCancellationManager Cancellation { get; } = new();
        private readonly PendingFileQueue _queue = new();
        private readonly TaskProcessorWorker _worker;

        public Harness(AiBatchSettingsDto settings, Func<int, AiProviderResolution> route,
            ReplyMode mode = ReplyMode.Success, string? connectionString = null)
        {
            settings.Validate();
            State = new(route);
            Handler = new(State, mode);
            ServiceCollection services = new();
            services.AddLogging();
            services.AddApplication();
            if (connectionString is not null) services.AddInfrastructurePersistence(connectionString);
            services.AddInfrastructureExternal();
            // 替换唯一 HTTP 工厂，所有生产协议均只能到此内存 handler，不能解析 DNS 或真实外呼。
            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<IHttpClientFactory>(_ => new StubHttpClientFactory(Handler));
            IAiBatchSettingsService batchSettings = Substitute.For<IAiBatchSettingsService>();
            batchSettings.GetAsync(Arg.Any<CancellationToken>()).Returns(settings);
            services.AddSingleton(batchSettings);
            services.AddSingleton(State);
            services.AddScoped<ScopedRoute>();
            services.RemoveAll<IAiProviderResolver>();
            services.AddScoped<IAiProviderResolver>(provider => provider.GetRequiredService<ScopedRoute>());
            services.RemoveAll<IProcessFileService>();
            services.AddScoped<IProcessFileService, ScopedProcess>();
            services.AddSingleton<IAuditAiCallWriter>(new RecordingAudit(State));
            services.AddSingleton(Substitute.For<IAiProviderHealthTracker>());
            services.AddSingleton(Substitute.For<IAiProviderRpmGate>());
            services.AddSingleton(Substitute.For<IAlertService>());
            if (connectionString is null) services.AddSingleton(Substitute.For<IAiProviderQuotaTracker>());
            Provider = services.BuildServiceProvider();
            _worker = new(_queue, Cancellation, Provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<TaskProcessorWorker>.Instance, batchSettings: batchSettings);
        }

        public async Task RunAsync(Func<Task>? duringRequest = null)
        {
            foreach (WorkItem work in State.Work)
                await _queue.EnqueueAsync(new(work.Path, 0, PendingFileSource.Manual));
            await _worker.StartAsync(CancellationToken.None);
            try
            {
                if (duringRequest is not null) await duringRequest().WaitAsync(TestTimeout);
                await State.AllDisposed.Task.WaitAsync(TestTimeout);
            }
            finally
            {
                using CancellationTokenSource stop = new(TimeSpan.FromSeconds(5));
                await _worker.StopAsync(stop.Token).WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        public async ValueTask DisposeAsync()
        {
            _worker.Dispose();
            await Provider.DisposeAsync();
        }
    }

    private sealed class State(Func<int, AiProviderResolution> route) : IParseDiagnosticSink
    {
        public WorkItem[] Work { get; } = Enumerable.Range(0, ItemCount).Select(index =>
            new WorkItem(index, $"Synthetic{index:D3}", $"/synthetic/Synthetic{index:D3}.mkv", Guid.NewGuid().ToString("N"), route(index))).ToArray();
        public ParseDiagnosticOptions Options { get; } = new() { Level = ParseDiagnosticLevel.Standard };
        public ConcurrentQueue<ParseDiagnosticEvent> Events { get; } = new();
        public ConcurrentQueue<int> Started { get; } = new();
        public ConcurrentQueue<int> Continued { get; } = new();
        public ConcurrentQueue<int> Cancelled { get; } = new();
        public ConcurrentQueue<Exception> Errors { get; } = new();
        public ConcurrentQueue<Guid> ScopeIds { get; } = new();
        public ConcurrentQueue<Guid> DisposedScopeIds { get; } = new();
        public ConcurrentBag<IAiCallOrchestrator> Orchestrators { get; } = new();
        public ConcurrentDictionary<int, string> RunIds { get; } = new();
        public ConcurrentDictionary<int, string?> ResumedRunIds { get; } = new();
        public ConcurrentDictionary<int, AiCallOutcome> Outcomes { get; } = new();
        public ConcurrentQueue<AuditAiCallEntry> Audits { get; } = new();
        public TaskCompletionSource AllDisposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int RunningStages;
        public int PeakStages;
        public void Write(ParseDiagnosticEvent value) => Events.Enqueue(value);

        public async Task StageAsync()
        {
            int active = Interlocked.Increment(ref RunningStages);
            int previous;
            do { previous = Volatile.Read(ref PeakStages); }
            while (active > previous && Interlocked.CompareExchange(ref PeakStages, active, previous) != previous);
            try { await Task.Yield(); }
            finally { Interlocked.Decrement(ref RunningStages); }
        }
    }

    private sealed class ScopedRoute : IAiProviderResolver
    {
        public Guid ScopeId { get; } = Guid.NewGuid();
        public AiProviderResolution Resolution { get; set; } = null!;
        public Task<IReadOnlyList<AiProviderResolution>> ResolveOrderedAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AiProviderResolution>>([Resolution]);
    }

    private sealed class ScopedProcess(IAiCallOrchestrator orchestrator, ScopedRoute route, State state) : IProcessFileService, IDisposable
    {
        public async Task<ProcessFileOutcome> ProcessAsync(PendingFileItem item, CancellationToken ct)
        {
            WorkItem work = state.Work.Single(value => value.Path == item.FullPath);
            route.Resolution = work.Route;
            state.Started.Enqueue(work.Index);
            state.ScopeIds.Enqueue(route.ScopeId);
            state.Orchestrators.Add(orchestrator);
            using IDisposable diagnostic = ParseDiagnostics.Begin("synthetic_file", work.RunId, work.Index + 1, state);
            state.RunIds[work.Index] = ParseDiagnostics.CurrentRunId!;
            await state.StageAsync();
            try
            {
                AiCallOutcome result = await orchestrator.ExecuteAsync(new(Path.GetFileName(item.FullPath)), work.Index + 1, ct);
                await state.StageAsync();
                state.Outcomes[work.Index] = result;
                state.Continued.Enqueue(work.Index);
                return new(work.Index + 1, result.Success ? ProcessOutcome.Completed : ProcessOutcome.AwaitingReview);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                state.Cancelled.Enqueue(work.Index);
                throw;
            }
            catch (Exception ex)
            {
                state.Errors.Enqueue(ex);
                throw;
            }
            finally { state.ResumedRunIds[work.Index] = ParseDiagnostics.CurrentRunId; }
        }

        public void Dispose()
        {
            state.DisposedScopeIds.Enqueue(route.ScopeId);
            if (state.DisposedScopeIds.Count == ItemCount) state.AllDisposed.TrySetResult();
        }
    }

    private sealed class RecordingAudit(State state) : IAuditAiCallWriter
    {
        public Task WriteAsync(AuditAiCallEntry entry, CancellationToken ct = default)
        {
            state.Audits.Enqueue(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class StubHttpClientFactory(StubHttpHandler handler) : IHttpClientFactory, IDisposable
    {
        private readonly ConcurrentBag<HttpClient> _clients = [];
        public HttpClient CreateClient(string name)
        {
            HttpClient client = new(handler, disposeHandler: false);
            _clients.Add(client);
            return client;
        }
        public void Dispose()
        {
            foreach (HttpClient client in _clients) client.Dispose();
            handler.Dispose();
        }
    }

    private sealed class StubHttpHandler(State state, ReplyMode mode) : HttpMessageHandler
    {
        private static readonly Regex FileIdentity = new("Synthetic(?<index>[0-9]{3})", RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
        public ConcurrentQueue<PhysicalRequest> Requests { get; } = new();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action<PhysicalRequest>? BeforeReply { get; set; }
        public int CancelledRequests;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken ct)
        {
            using JsonDocument payload = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(ct));
            string user = payload.RootElement.GetProperty("messages").EnumerateArray()
                .Single(value => value.GetProperty("role").GetString() == "user").GetProperty("content").GetString()!;
            bool isBatch = user.TrimStart().StartsWith('{');
            BatchRow[] rows;
            if (isBatch)
            {
                using JsonDocument envelope = JsonDocument.Parse(user);
                rows = envelope.RootElement.GetProperty("items").EnumerateArray().Select(value =>
                    new BatchRow(value.GetProperty("id").GetString(), Index(value.GetProperty("input").GetString()!))).ToArray();
            }
            else rows = [new(null, Index(user))];
            string? runId = ParseDiagnostics.CurrentRunId;
            WorkItem owner = state.Work.Single(work => work.RunId == runId);
            PhysicalRequest request = new(isBatch, rows, rows.Reverse().Select(row => row.Id).ToArray(), owner.Route.ProviderId,
                payload.RootElement.GetProperty("model").GetString()!, payload.RootElement.GetProperty("max_tokens").GetInt32(),
                ParseDiagnostics.CurrentRequestId);
            Requests.Enqueue(request);
            Entered.TrySetResult();
            BeforeReply?.Invoke(request);
            if (mode == ReplyMode.WaitForCancellation)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(10), ct); }
                catch (OperationCanceledException) { Interlocked.Increment(ref CancelledRequests); throw; }
                throw new TimeoutException("测试等待取消超时");
            }
            if (mode == ReplyMode.RateLimited)
                return JsonResponse(HttpStatusCode.TooManyRequests, "{\"error\":{\"message\":\"合成限流\"}}");
            string content = isBatch && mode == ReplyMode.MalformedBatch ? "{}" : isBatch
                ? JsonSerializer.Serialize(new { items = rows.Reverse().Select(row => new { id = row.Id, result = MediaResult(row.Index) }) })
                : JsonSerializer.Serialize(MediaResult(rows[0].Index));
            return JsonResponse(HttpStatusCode.OK, JsonSerializer.Serialize(new
            {
                choices = new[] { new { message = new { role = "assistant", content } } },
                usage = new { prompt_tokens = 11, completion_tokens = 7 },
            }));
        }

        private static int Index(string input)
        {
            Match match = FileIdentity.Match(input);
            if (!match.Success) throw new InvalidOperationException("生产提示词未包含合成文件名");
            return int.Parse(match.Groups["index"].Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        // 每个结果遵守生产单项媒体 schema；仅外层增加批量 ID，不复用脚本专用协议。
        private object MediaResult(int index) => new { title = state.Work[index].Title, year = (int?)null, type = "movie",
            season = (int?)null, episode = (int?)null, episodeEnd = (int?)null, confidence = 0.98, aliases = Array.Empty<string>() };

        private static HttpResponseMessage JsonResponse(HttpStatusCode status, string body) => new(status)
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
