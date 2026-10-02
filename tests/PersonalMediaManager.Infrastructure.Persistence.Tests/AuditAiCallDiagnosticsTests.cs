using PersonalMediaManager.Application.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Common.Diagnostics;
using PersonalMediaManager.Application.Services.Audit;
using PersonalMediaManager.Domain.Aggregates.AiProviders;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Infrastructure.Persistence.Services.Audit;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

/// <summary>审计正文统一遵从诊断级别、脱敏与显式截断</summary>
public sealed class AuditAiCallDiagnosticsTests
{
    [Theory]
    [InlineData(ParseDiagnosticLevel.Off)]
    [InlineData(ParseDiagnosticLevel.Standard)]
    public void BodyDisabledExplainsPolicyWithoutLeakingText(ParseDiagnosticLevel level)
    {
        using IDisposable scope = ParseDiagnostics.Begin("audit", sink: new Sink(level));
        string? result = AuditAiCallWriter.FormatText("private prompt body");
        result.Should().Contain("正文未记录").And.Contain("SHA256=").And.NotContain("private prompt body");
        AuditAiCallWriter.FormatText(null).Should().BeNull();
    }

    [Fact]
    public void DetailedRedactsSecretsReasoningAndAbsolutePaths()
    {
        using IDisposable scope = ParseDiagnostics.Begin("audit", sink: new Sink(ParseDiagnosticLevel.Detailed));
        const string input = """{"authorization":"Bearer credential","reasoning_content":"private thoughts","path":"/home/person/private/media.mkv","content":"visible output"}""";
        string? result = AuditAiCallWriter.FormatText(input);
        result.Should().Contain("visible output").And.Contain("正文已脱敏")
            .And.NotContain("credential").And.NotContain("private thoughts").And.NotContain("/home/person");
    }

    [Fact]
    public void DetailedTruncationIsUtf8BoundedAndExplicit()
    {
        using IDisposable scope = ParseDiagnostics.Begin("audit", sink: new Sink(ParseDiagnosticLevel.Detailed));
        string? result = AuditAiCallWriter.FormatText("电影电影电影电影", maxUtf8Bytes: 10);
        result.Should().StartWith("电影电\n").And.Contain("正文已截断").And.Contain("已捕获=9字节").And.Contain("SHA256=");
    }

    [Fact]
    public async Task DatabaseRowContainsPolicyNoticeAndKeepsExistingAuditMetadata()
    {
        await using SqliteConnection connection = new("DataSource=:memory:");
        await connection.OpenAsync();
        Factory factory = new(connection);
        long providerId;
        await using (PmmDbContext db = factory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            ParseAiProvider provider = new() { Name = "测试", Type = AiProviderType.OpenAiCompatible, BaseUrl = "https://example.invalid", Model = "test-model" };
            db.ParseAiProviders.Add(provider);
            await db.SaveChangesAsync();
            providerId = provider.Id;
        }
        Sink sink = new(ParseDiagnosticLevel.Standard);
        using IDisposable scope = ParseDiagnostics.Begin("file", "test-run", sink: sink);
        AuditAiCallWriter writer = new(factory, new SystemClock());
        await writer.WriteAsync(new(providerId, null, true, 123, Model: "test-model", ChainId: "chain",
            RequestText: "private request", ResponseText: "private response"));
        await using PmmDbContext read = factory.CreateDbContext();
        AuditAiCall row = await read.AuditAiCalls.SingleAsync();
        row.RequestText.Should().Contain("正文未记录").And.NotContain("private request");
        row.ResponseText.Should().Contain("正文未记录").And.NotContain("private response");
        row.ChainId.Should().Be("chain"); row.Model.Should().Be("test-model"); row.LatencyMs.Should().Be(123);
        sink.Events.Should().Contain(e => e.Name == "ai.audit_written" && e.Data.GetProperty("auditId").GetInt64() == row.Id);
    }

    private sealed class Sink(ParseDiagnosticLevel level) : IParseDiagnosticSink
    {
        public ParseDiagnosticOptions Options { get; } = new() { Level = level };
        public List<ParseDiagnosticEvent> Events { get; } = [];
        public void Write(ParseDiagnosticEvent value) => Events.Add(value);
    }

    private sealed class Factory(SqliteConnection connection) : IDbContextFactory<PmmDbContext>
    {
        public PmmDbContext CreateDbContext() => new(new DbContextOptionsBuilder<PmmDbContext>().UseSqlite(connection).Options);
    }
}
