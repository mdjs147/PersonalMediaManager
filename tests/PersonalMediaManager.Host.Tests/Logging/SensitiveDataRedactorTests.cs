using PersonalMediaManager.Host.Logging;

namespace PersonalMediaManager.Host.Tests.Logging;

/// <summary>SensitiveDataRedactor — 脱敏名单与词边界（裸 key 收编，审计修复项 3）</summary>
/// <remarks>
/// 经 Host 门面调用，同时覆盖「转发 Application.Common.Logging 实现」的接线正确性。
/// 裸 key 的边界取舍（\b 词边界 + 紧跟 =，宁可误脱不泄露）：
///   命中：?key= / &amp;key= / x-key=（连字符处词边界成立，误脱可接受）
///   不命中：monkey=（k 前是单词字符无边界）、keyword=（key 后不是 =）、
///           JSON "key":"..."（设置键名等结构字段场景，名单刻意不收——ApiKey 的 JSON 序列化名是 apiKey 已覆盖，
///           Gemini 密钥仅以 URL query 形态出现）
/// </remarks>
public sealed class SensitiveDataRedactorTests
{
    [Fact]
    public void BareKey_QueryParam_IsRedacted()
    {
        string s = SensitiveDataRedactor.Redact("POST https://g.example.com/v1beta/models/m:generateContent?key=AIza1234567");

        s.Should().NotContain("AIza1234567");
        s.Should().Contain("key=***(11)", "保留长度提示的占位替换");
    }

    [Fact]
    public void BareKey_AmpersandForm_IsRedacted()
    {
        SensitiveDataRedactor.Redact("GET /x?page=2&key=secret9")
            .Should().Contain("&key=***(7)").And.NotContain("secret9");
    }

    [Fact]
    public void KeywordParam_IsNotRedacted()
    {
        SensitiveDataRedactor.Redact("GET /search?keyword=变形金刚")
            .Should().Be("GET /search?keyword=变形金刚", "key 后必须紧跟 =，keyword 不受裸 key 项影响");
    }

    [Fact]
    public void MonkeyParam_IsNotRedacted()
    {
        SensitiveDataRedactor.Redact("GET /zoo?monkey=georgie")
            .Should().Be("GET /zoo?monkey=georgie", "monkey 的 k 前是单词字符，\\b 词边界不成立");
    }

    [Fact]
    public void HyphenPrefixedKey_IsRedacted_PreferOverRedaction()
    {
        // x-key= 在连字符处词边界成立 → 命中；按「宁可误脱也不泄露」原则接受
        SensitiveDataRedactor.Redact("GET /x?x-key=abc123")
            .Should().Contain("key=***(6)").And.NotContain("abc123");
    }

    [Fact]
    public void Json_BareKeyField_IsNotRedacted_SettingKeyNamesStayReadable()
    {
        SensitiveDataRedactor.Redact("更新设置 {\"key\":\"System_Port\",\"value\":\"8095\"}")
            .Should().Contain("\"key\":\"System_Port\"", "JSON 名单刻意不收裸 key：设置键名等结构字段须保持可读");
    }

    [Fact]
    public void Existing_List_Still_Redacted_Regression()
    {
        SensitiveDataRedactor.Redact("?apikey=sk-1&token=t2&api-key=k3")
            .Should().NotContain("sk-1").And.NotContain("t2").And.NotContain("k3");
        SensitiveDataRedactor.Redact("{\"apiKey\":\"sk-22\"}").Should().NotContain("sk-22");
        SensitiveDataRedactor.Redact("Authorization: Bearer abc.def.ghi")
            .Should().Contain("Bearer ***").And.NotContain("abc.def.ghi");
    }

    [Fact]
    public void MaskValue_KeepsLengthHint()
    {
        SensitiveDataRedactor.MaskValue("12345").Should().Be("***(5)");
        SensitiveDataRedactor.MaskValue(null).Should().Be("***");
    }
}
