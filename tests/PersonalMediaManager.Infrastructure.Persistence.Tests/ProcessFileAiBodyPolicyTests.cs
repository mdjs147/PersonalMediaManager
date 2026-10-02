using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Common.Diagnostics;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.DependencyInjection;
using PersonalMediaManager.Application.Services.Audit;
using PersonalMediaManager.Application.Services.Parse;
using PersonalMediaManager.Domain.Aggregates.AiProviders;
using PersonalMediaManager.Domain.Aggregates.MediaItems;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Infrastructure.Persistence.Services.Audit;
using PersonalMediaManager.Infrastructure.Persistence.Services.Parse;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

public sealed partial class ProcessFileServiceTests
{
    [Theory]
    [InlineData(ParseDiagnosticLevel.Standard, false)]
    [InlineData(ParseDiagnosticLevel.Detailed, true)]
    public async Task RealAiFailureBodyObeysPolicyThroughPipelineTimelineAndAudit(ParseDiagnosticLevel level, bool keepsBody)
    {
        const string marker = "PrivacyResponseMarker42";
        const string secret = "provider-password-must-not-leak";
        const string endpointKey = "endpoint-credential-must-not-leak";
        ConfigureRule(confidence: 0.3, hasSpecialChars: false);
        BodyPolicyDiagnosticSink sink = new(level);
        long providerId;
        using (PmmDbContext db = _dbFactory.CreateDbContext())
        {
            ParseAiProvider stored = new()
            {
                Name = "测试提供商", Type = AiProviderType.OpenAiCompatible,
                BaseUrl = "https://example.invalid", Model = "test-model", IsPrimary = true,
            };
            db.ParseAiProviders.Add(stored);
            db.SaveChanges();
            providerId = stored.Id;
        }
        IAiProviderResolver resolver = Substitute.For<IAiProviderResolver>();
        resolver.ResolveOrderedAsync(Arg.Any<CancellationToken>()).Returns(new AiProviderResolution[]
        {
            new(providerId, AiProviderType.OpenAiCompatible, "测试提供商", true,
                new("https://example.invalid", endpointKey, "test-model")),
        });
        IAiParser parser = Substitute.For<IAiParser>();
        parser.Supports(AiProviderType.OpenAiCompatible).Returns(true);
        AiProviderLogicalException failure = new($"提供商失败：body={marker}; password={secret}; {endpointKey}", 400);
        failure.Data[AiCallDiagnostics.HttpStatusKey] = 400;
        failure.Data[AiCallDiagnostics.ResponseTextKey] = JsonSerializer.Serialize(new { error = marker, password = secret });
        parser.ParseAsync(Arg.Any<AiProviderType>(), Arg.Any<AiProviderEndpoint>(), Arg.Any<AiParseRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(failure);

        // 经公开 DI 入口取得真实内部编排器，并以真实审计写入器验证 SQLite 持久化路径。
        ServiceCollection services = new();
        services.AddApplication();
        services.AddLogging();
        services.AddSingleton(resolver);
        services.AddSingleton(parser);
        services.AddSingleton<IAuditAiCallWriter>(new AuditAiCallWriter(_dbFactory, new SystemClock()));
        services.AddSingleton(Substitute.For<IAiProviderHealthTracker>());
        services.AddSingleton(Substitute.For<IAiProviderQuotaTracker>());
        services.AddSingleton(Substitute.For<IAiProviderRpmGate>());
        using ServiceProvider container = services.BuildServiceProvider();
        using IServiceScope serviceScope = container.CreateScope();
        IAiCallOrchestrator orchestrator = serviceScope.ServiceProvider.GetRequiredService<IAiCallOrchestrator>();
        ProcessFileService sut = new(
            _dbFactory, _writeDetector, _ruleEngine, _forcedMatch, _tmdb, orchestrator, _folderCache,
            _classify, _archive, _fileHasher, _fileProbe, _audioProbe, new NullTaskNotifier(), _webhook,
            new SystemClock(), NullLogger<ProcessFileService>.Instance, diagnostics: sink);
        ProcessFileOutcome outcome = await sut.ProcessAsync(new(_tempFile, 1, PendingFileSource.Manual), CancellationToken.None);

        outcome.Outcome.Should().Be(ProcessOutcome.AwaitingReview);
        MediaItem media = ReadOne();
        media.Status.Should().Be(MediaItemStatus.AwaitingReview);
        await parser.Received(1).ParseAsync(Arg.Any<AiProviderType>(), Arg.Any<AiProviderEndpoint>(), Arg.Any<AiParseRequest>(), Arg.Any<CancellationToken>());
        using PmmDbContext read = _dbFactory.CreateDbContext();
        string[] timeline = read.ProcessSteps.AsNoTracking().Where(step => step.MediaItemId == media.Id)
            .Select(step => step.Detail ?? string.Empty).ToArray();
        timeline.Should().NotBeEmpty();
        AuditAiCall audit = read.AuditAiCalls.AsNoTracking().Single(row => row.MediaItemId == media.Id);
        audit.ErrorType.Should().Be("Http4xx");
        sink.Events.Should().Contain(e => e.Name == "ai.chain_completed");
        sink.Events.Should().Contain(e => e.Name == "pipeline.terminal");
        foreach (string content in new[]
        {
            JsonSerializer.Serialize(sink.Events), media.ErrorMessage ?? string.Empty,
            string.Join('\n', timeline), audit.ErrorDetail ?? string.Empty, audit.ResponseText ?? string.Empty,
        })
        {
            content.Should().NotContain(secret).And.NotContain(endpointKey);
            if (keepsBody) content.Should().Contain(marker);
            else content.Should().NotContain(marker);
        }
    }

    private sealed class BodyPolicyDiagnosticSink(ParseDiagnosticLevel level) : IParseDiagnosticSink
    {
        public ParseDiagnosticOptions Options { get; } = new() { Level = level };
        public List<ParseDiagnosticEvent> Events { get; } = [];
        public void Write(ParseDiagnosticEvent value) => Events.Add(value);
    }
}
