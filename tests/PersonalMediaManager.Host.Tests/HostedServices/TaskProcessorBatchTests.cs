using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PersonalMediaManager.Application.DependencyInjection;
using PersonalMediaManager.Application.Dtos.Ai;
using PersonalMediaManager.Application.Services.Audit;
using PersonalMediaManager.Application.Services.Webhook;
using PersonalMediaManager.Domain.Aggregates.AiProviders;
using PersonalMediaManager.Infrastructure.Persistence;
using PersonalMediaManager.Infrastructure.Persistence.DependencyInjection;
using NSubstitute;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Services.Parse;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Host.HostedServices;
using PersonalMediaManager.Infrastructure.External.DependencyInjection;
namespace PersonalMediaManager.Host.Tests.HostedServices;
public sealed class TaskProcessorBatchTests
{
    [Fact]
    public async Task RealWorkerAggregatesDifferentFilesAndDisposesScopesAfterSerialContinuation()
    {
        AiBatchOptions options = new() { MaxItems = 2, ExternalMaxItems = 2, MaxWaitMilliseconds = 100 };
        State state = new(); StubProtocol protocol = new();
        ServiceCollection services = new(); services.AddLogging(); services.AddInfrastructureExternal();
        services.RemoveAll<IAiProtocol>(); services.AddSingleton<IAiProtocol>(protocol);
        services.AddSingleton(options); services.AddSingleton(state); services.AddScoped<IProcessFileService, Process>();
        await using ServiceProvider provider = services.BuildServiceProvider();
        PendingFileQueue queue = new();
        await queue.EnqueueAsync(new("/synthetic/Alpha.mkv", 0, PendingFileSource.Manual));
        await queue.EnqueueAsync(new("/synthetic/Beta.mkv", 0, PendingFileSource.Manual));
        using TaskProcessorWorker worker = new(queue, new TaskCancellationManager(), provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<TaskProcessorWorker>.Instance, options);
        await worker.StartAsync(CancellationToken.None);
        await state.Done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(CancellationToken.None);
        protocol.Calls.Should().Be(1); state.Titles.Should().Equal("Alpha", "Beta");
        state.Peak.Should().Be(1); state.Disposed.Should().Be(2);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SettingsReadFailurePreservesDequeuedItemAndContinues(bool unrelatedCancellation)
    {
        List<string> processed = [];
        TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IProcessFileService process = NSubstitute.Substitute.For<IProcessFileService>();
        process.ProcessAsync(NSubstitute.Arg.Any<PendingFileItem>(), NSubstitute.Arg.Any<CancellationToken>())
            .Returns(call => { processed.Add(call.Arg<PendingFileItem>().FullPath); if (processed.Count == 2) done.TrySetResult();
                return Task.FromResult(new ProcessFileOutcome(1, ProcessOutcome.Completed)); });
        IAiBatchSettingsService settings = NSubstitute.Substitute.For<IAiBatchSettingsService>();
        int reads = 0;
        settings.GetAsync(NSubstitute.Arg.Any<CancellationToken>()).Returns(_ => ++reads == 1
            ? Task.FromException<PersonalMediaManager.Application.Dtos.Ai.AiBatchSettingsDto>(unrelatedCancellation
                ? new OperationCanceledException("模拟独立设置超时") : new IOException("模拟瞬时设置故障"))
            : Task.FromResult(new PersonalMediaManager.Application.Dtos.Ai.AiBatchSettingsDto()));
        ServiceCollection services = new(); services.AddScoped<IProcessFileService>(_ => process);
        await using ServiceProvider provider = services.BuildServiceProvider();
        PendingFileQueue queue = new();
        await queue.EnqueueAsync(new("/synthetic/first.mkv", 0, PendingFileSource.Manual));
        await queue.EnqueueAsync(new("/synthetic/second.mkv", 0, PendingFileSource.Manual));
        using TaskProcessorWorker worker = new(queue, new TaskCancellationManager(), provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<TaskProcessorWorker>.Instance, batchSettings: settings);
        await worker.StartAsync(CancellationToken.None);
        try { await done.Task.WaitAsync(TimeSpan.FromSeconds(2)); }
        finally { await worker.StopAsync(CancellationToken.None); }
        processed.Should().Equal("/synthetic/first.mkv", "/synthetic/second.mkv");
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealParserAndSqliteTrackerReserveQuotaBeforeSecondPhysicalChunk(bool cancelOwner)
    {
        string connectionString = $"Data Source=quota-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using SqliteConnection keeper = new(connectionString);
        await keeper.OpenAsync();
        using CancellationTokenSource owner = new();
        StubProtocol protocol = new() { BeforeRespond = cancelOwner ? () => owner.Cancel() : null };
        IAiProviderResolver resolver = Substitute.For<IAiProviderResolver>();
        IAiBatchSettingsService settings = Substitute.For<IAiBatchSettingsService>();
        settings.GetAsync(Arg.Any<CancellationToken>()).Returns(new AiBatchSettingsDto { ExternalBatchSize = 2, ContextTokenBudget = 32768 });
        ServiceCollection services = new(); services.AddLogging(); services.AddApplication();
        services.AddInfrastructurePersistence(connectionString); services.AddInfrastructureExternal();
        services.RemoveAll<IAiProtocol>(); services.AddSingleton<IAiProtocol>(protocol);
        services.AddSingleton(resolver); services.AddSingleton(settings);
        services.AddSingleton(Substitute.For<IAuditAiCallWriter>());
        services.AddSingleton(Substitute.For<IAiProviderHealthTracker>());
        services.AddSingleton(Substitute.For<IAlertService>());
        services.AddSingleton(Substitute.For<IAiProviderRpmGate>());
        await using ServiceProvider provider = services.BuildServiceProvider();
        IDbContextFactory<PmmDbContext> factory = provider.GetRequiredService<IDbContextFactory<PmmDbContext>>();
        long providerId;
        await using (PmmDbContext context = await factory.CreateDbContextAsync())
        {
            await context.Database.EnsureCreatedAsync();
            ParseAiProvider row = new() { Name = "合成单次额度", Type = AiProviderType.OpenAiCompatible,
                BaseUrl = "https://fixture.invalid", Model = "fixture", Enabled = true, QuotaCallLimit = 1 };
            context.ParseAiProviders.Add(row); await context.SaveChangesAsync(); providerId = row.Id;
        }
        resolver.ResolveOrderedAsync(Arg.Any<CancellationToken>()).Returns(new AiProviderResolution[]
            { new(providerId, AiProviderType.OpenAiCompatible, "合成单次额度", true, new("https://fixture.invalid", null, "fixture")) });
        int successful = 0;
        await AiBatchPipeline.RunAsync(Enumerable.Range(0, 4).Select<int, Func<CancellationToken, Task>>(i => async ct =>
        {
            using IServiceScope scope = provider.CreateScope();
            try
            {
                if ((await scope.ServiceProvider.GetRequiredService<IAiCallOrchestrator>().ExecuteAsync(
                    new(i % 2 == 0 ? "Alpha.mkv" : "Beta.mkv"), i, i == 0 ? owner.Token : ct)).Success) successful++;
            }
            catch (OperationCanceledException) when (i == 0 && owner.IsCancellationRequested) { }
        }).ToArray(), new() { MaxItems = 4 });
        protocol.Calls.Should().Be(1); successful.Should().Be(cancelOwner ? 1 : 2);
        await using PmmDbContext verify = await factory.CreateDbContextAsync();
        ParseAiProvider stored = await verify.ParseAiProviders.AsNoTracking().SingleAsync();
        stored.QuotaUsedCalls.Should().Be(1); stored.QuotaUsedTokens.Should().Be(18); stored.QuotaExceededAt.Should().NotBeNull();
    }

    private sealed class State
    {
        public int Running, Peak, Disposed;
        public List<string> Titles { get; } = [];
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task Stage() { Peak = Math.Max(Peak, Interlocked.Increment(ref Running)); await Task.Delay(5); Interlocked.Decrement(ref Running); }
    }
    private sealed class Process(IAiParser parser, State state) : IProcessFileService, IDisposable
    {
        public async Task<ProcessFileOutcome> ProcessAsync(PendingFileItem item, CancellationToken ct)
        {
            await state.Stage();
            AiParseOutcome result = await parser.ParseAsync(AiProviderType.OpenAiCompatible, new("https://fixture.invalid", null, "fixture"),
                new(Path.GetFileName(item.FullPath)), ct);
            await state.Stage(); state.Titles.Add(result.Result.Title);
            return new(state.Titles.Count, ProcessOutcome.Completed);
        }
        public void Dispose() { if (++state.Disposed == 2) state.Done.TrySetResult(); }
    }
    private sealed class StubProtocol : IAiProtocol
    {
        public AiProviderType Protocol => AiProviderType.OpenAiCompatible;
        public int Calls;
        public Action? BeforeRespond { get; init; }
        public Task<AiCompletion> CompleteAsync(AiProtocolRequest request, CancellationToken ct = default)
        {
            Calls++; BeforeRespond?.Invoke();
            using JsonDocument doc = JsonDocument.Parse(request.Messages[1].Content);
            return Task.FromResult(new AiCompletion(JsonSerializer.Serialize(new
            { items = doc.RootElement.GetProperty("items").EnumerateArray().Reverse().Select(row => new
                { id = row.GetProperty("id").GetString(), result = new
                    { title = row.GetProperty("input").GetString()!.Contains("Alpha", StringComparison.Ordinal) ? "Alpha" : "Beta", type = "movie", confidence = 0.9 } }) }), 11, 7));
        }
    }
}
