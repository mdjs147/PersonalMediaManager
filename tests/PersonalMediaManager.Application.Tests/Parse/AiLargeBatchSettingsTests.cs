using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Dtos.Ai;
using PersonalMediaManager.Domain.Enums;
namespace PersonalMediaManager.Application.Tests.Parse;

public sealed class AiLargeBatchSettingsTests
{
    [Theory]
    [InlineData("https://api.deepseek.com", "deepseek-flash", true)]
    [InlineData("https://api.deepseek.com/v1/", "deepseek-flash", true)]
    [InlineData("https://api.deepseek.com.evil.invalid", "deepseek-flash", false)]
    [InlineData("http://api.deepseek.com", "deepseek-flash", false)]
    [InlineData("https://api.deepseek.com?token=synthetic", "deepseek-flash", false)]
    [InlineData("https://api.deepseek.com", "deepseek-chat", false)]
    [InlineData("https://api.deepseek.com", "deepseek-v4-pro", false)]
    [InlineData("https://gateway.invalid", "deepseek-flash", false)]
    public void RecommendationsRequireExactOfficialEndpointAndVerifiedLiveModel(string endpoint, string model, bool supported)
    {
        AiBatchProviderInfoDto info = AiBatchProviderPresets.Describe(7, "任意显示名称", AiProviderType.OpenAiCompatible, endpoint, model);
        (info.RecommendedSettings is not null).Should().Be(supported);
        if (!supported) { info.AdvancedSettings.Should().BeNull(); return; }
        info.RecommendedSettings!.BatchSize.Should().Be(32);
        info.RecommendedSettings.DisableThinking.Should().BeTrue();
        info.RecommendedSettings.ContextTokenBudget.Should().Be(65536);
        info.RecommendedSettings.MaxOutputTokens.Should().Be(32768);
        info.AdvancedSettings!.BatchSize.Should().Be(128);
        info.AdvancedSettings.ContextTokenBudget.Should().Be(131072);
        info.AdvancedSettings.MaxOutputTokens.Should().Be(65536);
    }
    [Fact]
    public void PerProviderPresetDoesNotChangeOtherProvidersLocalOrNewModel()
    {
        AiBatchProviderInfoDto info = AiBatchProviderPresets.Describe(7, "fixture", AiProviderType.OpenAiCompatible, "https://api.deepseek.com", "deepseek-flash");
        AiBatchSettingsDto dto = new() { ProviderSettings = [info.AdvancedSettings!] };
        dto.Validate(); dto.ExternalBatchSize.Should().Be(1); dto.LocalBatchSize.Should().Be(1);
        dto.ToOptions(7, info.ConfigurationKey).ExternalMaxItems.Should().Be(128);
        dto.ToOptions(8, info.ConfigurationKey).ExternalMaxItems.Should().Be(1);
        dto.ToOptions(7, new string('0', 64)).ContextTokenBudget.Should().Be(8192);
        dto.ToOptions(7, new string('0', 64)).DisableThinking.Should().BeFalse();
        dto.ToOptions().MaxItems.Should().Be(128);
    }
    [Theory]
    [InlineData(129, 1, 8192, 2048, 65536)]
    [InlineData(128, 3, 8192, 2048, 65536)]
    [InlineData(128, 1, 1048577, 2048, 65536)]
    [InlineData(128, 1, 8192, 262145, 65536)]
    [InlineData(128, 1, 8192, 2048, 8388609)]
    public void HardResourceLimitsCannotBeExceeded(int external, int local, int context, int output, int bytes)
    {
        Action validate = () => new AiBatchSettingsDto { ExternalBatchSize = external, LocalBatchSize = local,
            ContextTokenBudget = context, MaxOutputTokens = output, MaxResponseBytes = bytes }.Validate();
        validate.Should().Throw<BusinessException>();
    }
}
