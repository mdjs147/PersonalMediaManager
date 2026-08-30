using Microsoft.EntityFrameworkCore;
using PersonalMediaManager.Domain.Aggregates.WebhookSubscriptions;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Infrastructure.Persistence;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

/// <summary>验证 JSON 列 ValueConverter + ValueComparer：写入 List 后再读出，内容应严格一致</summary>
public sealed class JsonValueConverterRoundTripTests : IClassFixture<PmmDbContextTestFixture>
{
    private readonly PmmDbContextTestFixture _fixture;

    public JsonValueConverterRoundTripTests(PmmDbContextTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task WebhookSubscription_EventsListRoundTrip_IsExact()
    {
        long id;
        using (PmmDbContext writer = _fixture.CreateContext())
        {
            WebhookSubscription sub = new()
            {
                Name = "测试订阅",
                Url = "https://example.com/hook",
                Events = ["media.archived", "media.failed", "media.skipped"],
            };
            writer.WebhookSubscriptions.Add(sub);
            await writer.SaveChangesAsync();
            id = sub.Id;
        }

        using PmmDbContext reader = _fixture.CreateContext();
        WebhookSubscription read = await reader.WebhookSubscriptions.AsNoTracking().FirstAsync(x => x.Id == id);
        read.Events.Should().BeEquivalentTo(["media.archived", "media.failed", "media.skipped"], opt => opt.WithStrictOrdering());
    }

    [Fact]
    public async Task TmdbMetadataCache_OriginCountryNullableListRoundTrip()
    {
        using (PmmDbContext writer = _fixture.CreateContext())
        {
            TmdbMetadataCache cache = new()
            {
                TmdbId = 999,
                MediaType = "movie",
                Title = "测试电影",
                OriginCountry = ["CN", "HK", "TW"],
                CachedAt = DateTimeOffset.UtcNow,
            };
            writer.TmdbMetadataCaches.Add(cache);
            await writer.SaveChangesAsync();
        }

        using PmmDbContext reader = _fixture.CreateContext();
        TmdbMetadataCache read = await reader.TmdbMetadataCaches.AsNoTracking()
            .FirstAsync(x => x.TmdbId == 999 && x.MediaType == "movie");
        read.OriginCountry.Should().NotBeNull();
        read.OriginCountry!.Should().BeEquivalentTo(["CN", "HK", "TW"], opt => opt.WithStrictOrdering());
    }
}
