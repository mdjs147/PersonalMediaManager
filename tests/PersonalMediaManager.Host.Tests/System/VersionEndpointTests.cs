using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Dtos.System;
using PersonalMediaManager.Host.Middleware;

namespace PersonalMediaManager.Host.Tests.SystemModule;

/// <summary>匿名版本端点只返回产品信息，不访问迁移历史</summary>
public sealed class VersionEndpointTests
{
    [Fact(DisplayName = "初始化前匿名版本端点可读，保留兼容形状但不读取或公开迁移诊断")]
    public async Task AnonymousVersion_UsesStaticMetadataWithoutMigrationDetails()
    {
        SetupGuardMiddleware.ResetCacheForTest();
        using PmmHostFactory factory = new()
        {
            ConfigureTestServices = services =>
            {
                services.RemoveAll<IVersionInfoProvider>();
                services.AddSingleton<IVersionInfoProvider>(new StaticOnlyVersionInfoProvider());
            },
        };
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/api/system/version");
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();

        response.IsSuccessStatusCode.Should().BeTrue();
        body.GetProperty("code").GetInt32().Should().Be(ApiCode.Success);
        JsonElement data = body.GetProperty("data");
        data.GetProperty("product").GetString().Should().Be("0.4.0");
        data.GetProperty("backend").GetString().Should().Be("0.4.0");
        data.GetProperty("frontend").GetString().Should().Be("0.4.0");
        data.GetProperty("commit").GetString().Should().Be("1234abcd");
        JsonElement database = data.GetProperty("database");
        database.GetProperty("status").GetString().Should().Be("notChecked");
        database.GetProperty("target").GetString().Should().BeEmpty();
        database.GetProperty("applied").GetString().Should().Be("unknown");
        database.GetProperty("appliedMigrationId").ValueKind.Should().Be(JsonValueKind.Null);
        database.GetProperty("needsMigration").GetBoolean().Should().BeFalse();
        database.GetProperty("historyAvailable").GetBoolean().Should().BeFalse();
        database.GetProperty("pendingMigrationIds").GetArrayLength().Should().Be(0);
        database.GetProperty("unknownMigrationIds").GetArrayLength().Should().Be(0);
        data.TryGetProperty("dbVersionTarget", out _).Should().BeFalse();
        SetupGuardMiddleware.ResetCacheForTest();
    }

    private sealed class StaticOnlyVersionInfoProvider : IVersionInfoProvider
    {
        public StaticVersionInfo GetStatic() => new()
        {
            Product = "0.4.0",
            Backend = "9.9.9",
            Frontend = "8.8.8",
            Commit = "1234abcd",
            DbVersionTarget = "20260722141057_InternalMigration",
        };

        public Task<VersionInfoResponse> GetFullAsync(CancellationToken ct = default)
            => throw new InvalidOperationException("匿名版本端点不得读取完整迁移状态");
    }
}
