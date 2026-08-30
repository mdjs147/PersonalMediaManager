using System.Text.Json;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace PersonalMediaManager.Infrastructure.Persistence.Conversions;

/// <summary>JSON 列 ValueConverter（可空版本：POCO/List/Dict ↔ TEXT 列）</summary>
/// <remarks>
/// 与 JsonValueComparer&lt;T&gt; 配套。SQLite 无原生 JSON 列；统一以 TEXT 列存储 JSON 字符串。
/// </remarks>
public sealed class JsonValueConverter<T> : ValueConverter<T?, string?>
    where T : class
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    public JsonValueConverter()
        : base(
            v => v == null ? null : JsonSerializer.Serialize(v, JsonOpts),
            v => string.IsNullOrEmpty(v) ? null : JsonSerializer.Deserialize<T>(v, JsonOpts))
    {
    }
}

/// <summary>JSON 列 ValueConverter — 非空版本（用于 WebhookSubscription.Events 等非空集合属性）</summary>
/// <remarks>
/// EF Property&lt;List&lt;string&gt;&gt; 与 ValueConverter&lt;List&lt;string&gt;?, string?&gt; 因 nullable 注解不一致触发 CS8620；
/// 本类专门服务非空属性，让编译器精确匹配类型。
/// </remarks>
public sealed class JsonValueConverterNonNull<T> : ValueConverter<T, string>
    where T : class
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    public JsonValueConverterNonNull()
        : base(
            v => JsonSerializer.Serialize(v, JsonOpts),
            v => JsonSerializer.Deserialize<T>(v, JsonOpts)!)
    {
    }
}
