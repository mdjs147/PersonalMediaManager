using Microsoft.EntityFrameworkCore;
using PersonalMediaManager.Domain.Aggregates.MediaItems;
using PersonalMediaManager.Domain.Aggregates.WebhookSubscriptions;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Domain.Enums;
using PersonalMediaManager.Infrastructure.Persistence;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

/// <summary>验证 8 条 JSON 列（string/string?）补 ValueComparer 后 ChangeTracking 能正确识别字符串改写并落库</summary>
/// <remarks>
/// 对应审计 P0-r2.1 ~ P0-r2.8。本测试覆盖 3 个代表性列：
///   - MediaItem.ParsedInfo（聚合 private setter，走 ApplyTmdbMatch 行为方法）
///   - CategoryMatchRule.Conditions（贫血实体 public setter）
///   - WebhookDelivery.Payload（贫血实体 public setter）
/// 流程：Add → SaveChanges → 改 JSON 字段 → SaveChanges → 重新查询断言改后值已落库。
/// </remarks>
public sealed class JsonColumnTrackingTests : IClassFixture<PmmDbContextTestFixture>
{
    private readonly PmmDbContextTestFixture _fixture;

    public JsonColumnTrackingTests(PmmDbContextTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task MediaItem_ParsedInfoUpdate_IsPersisted()
    {
        // Arrange：用 CreateFixture 创建 Queued 状态 MediaItem，先写入原始 ParsedInfo
        const string sourcePath = "F:/test/json-tracking/movie.mkv";
        string originalJson = new ParsedInfo("旧标题", 2020, "movie", null, null, null, null).ToJson();

        long id;
        using (PmmDbContext writer = _fixture.CreateContext())
        {
            MediaItem item = MediaItem.CreateFixture(
                sourcePath: sourcePath,
                fileName: "movie.mkv",
                fileSize: 1024L,
                status: MediaItemStatus.Queued,
                parsedInfo: originalJson);
            writer.MediaItems.Add(item);
            await writer.SaveChangesAsync();
            id = item.Id;
        }

        // Act：重新查出后通过聚合行为方法改 ParsedInfo（同时写 TmdbId + MediaType + ParseSource）
        string updatedJson;
        using (PmmDbContext updater = _fixture.CreateContext())
        {
            MediaItem tracked = await updater.MediaItems.FirstAsync(x => x.Id == id);
            ParsedInfo updated = new("新标题", 2024, "movie", null, null, null, 99L);
            tracked.ApplyTmdbMatch(123, "movie", ParseSource.Ai, 0.92, updated);
            updatedJson = updated.ToJson();
            await updater.SaveChangesAsync();
        }

        // Assert：另起 DbContext 重新查询，ParsedInfo 必须是改后 JSON（而非原始 JSON）
        using PmmDbContext reader = _fixture.CreateContext();
        MediaItem read = await reader.MediaItems.AsNoTracking().FirstAsync(x => x.Id == id);
        read.ParsedInfo.Should().Be(updatedJson);
        read.ParsedInfo.Should().NotBe(originalJson);
        read.TmdbId.Should().Be(123);
    }

    [Fact]
    public async Task CategoryMatchRule_ConditionsUpdate_IsPersisted()
    {
        // Arrange：先建分类定义满足 FK；再建匹配规则写入原始 Conditions JSON
        const string originalConditions = """{"all":[{"eq":{"field":"type","value":"movie"}}]}""";
        const string updatedConditions = """{"any":[{"contains":{"field":"title","value":"测试"}},{"eq":{"field":"year","value":2024}}]}""";

        long ruleId;
        using (PmmDbContext writer = _fixture.CreateContext())
        {
            CategoryDefinition cat = new()
            {
                Name = "测试分类_JsonTracking",
                MediaType = MediaType.Movie,
                TargetRoot = "F:/test/json-tracking/movies",
                Priority = 100,
            };
            writer.CategoryDefinitions.Add(cat);
            await writer.SaveChangesAsync();

            CategoryMatchRule rule = new()
            {
                CategoryId = cat.Id,
                Name = "条件树测试规则",
                Conditions = originalConditions,
                Priority = 50,
                Enabled = true,
            };
            writer.CategoryMatchRules.Add(rule);
            await writer.SaveChangesAsync();
            ruleId = rule.Id;
        }

        // Act：重新查出后改 Conditions
        using (PmmDbContext updater = _fixture.CreateContext())
        {
            CategoryMatchRule tracked = await updater.CategoryMatchRules.FirstAsync(x => x.Id == ruleId);
            tracked.Conditions = updatedConditions;
            await updater.SaveChangesAsync();
        }

        // Assert：另起 DbContext 重新查询 Conditions 必须为改后 JSON
        using PmmDbContext reader = _fixture.CreateContext();
        CategoryMatchRule read = await reader.CategoryMatchRules.AsNoTracking().FirstAsync(x => x.Id == ruleId);
        read.Conditions.Should().Be(updatedConditions);
        read.Conditions.Should().NotBe(originalConditions);
    }

    [Fact]
    public async Task WebhookDelivery_PayloadUpdate_IsPersisted()
    {
        // Arrange：先建订阅满足 FK；再建出站记录写入原始 Payload JSON
        const string originalPayload = """{"event":"media.archived","requestId":"req-001","data":{"id":1}}""";
        const string updatedPayload = """{"event":"media.archived","requestId":"req-002","data":{"id":1,"retry":true}}""";

        long deliveryId;
        using (PmmDbContext writer = _fixture.CreateContext())
        {
            WebhookSubscription sub = new()
            {
                Name = "测试出站订阅",
                Url = "https://example.com/payload-test",
                Events = ["media.archived"],
            };
            writer.WebhookSubscriptions.Add(sub);
            await writer.SaveChangesAsync();

            WebhookDelivery delivery = new()
            {
                SubscriptionId = sub.Id,
                Event = "media.archived",
                Payload = originalPayload,
                Status = WebhookDeliveryStatus.Pending,
                RequestId = "req-001",
            };
            writer.WebhookDeliveries.Add(delivery);
            await writer.SaveChangesAsync();
            deliveryId = delivery.Id;
        }

        // Act：模拟重投场景——改写 Payload（带新 requestId）
        using (PmmDbContext updater = _fixture.CreateContext())
        {
            WebhookDelivery tracked = await updater.WebhookDeliveries.FirstAsync(x => x.Id == deliveryId);
            tracked.Payload = updatedPayload;
            tracked.RequestId = "req-002";
            tracked.Attempts = 1;
            await updater.SaveChangesAsync();
        }

        // Assert：另起 DbContext 重新查询 Payload 必须为改后 JSON
        using PmmDbContext reader = _fixture.CreateContext();
        WebhookDelivery read = await reader.WebhookDeliveries.AsNoTracking().FirstAsync(x => x.Id == deliveryId);
        read.Payload.Should().Be(updatedPayload);
        read.Payload.Should().NotBe(originalPayload);
        read.Attempts.Should().Be(1);
    }
}
