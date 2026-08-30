using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Infrastructure.External.Subtitles;

/// <summary>ISubtitleProviderRegistry 实现：按枚举键路由到具体字幕源策略</summary>
/// <remarks>
/// 照 AiProtocolParser 的字典路由门面：构造收全部 <see cref="ISubtitleProvider"/> 建字典，
/// 多个相同 Provider 的实现不允许（DI 每源一个），ToDictionary 自然报重复。
/// </remarks>
internal sealed class SubtitleProviderRegistry : ISubtitleProviderRegistry
{
    private readonly IReadOnlyDictionary<SubtitleProviderType, ISubtitleProvider> _providers;

    public SubtitleProviderRegistry(IEnumerable<ISubtitleProvider> providers)
    {
        _providers = providers.ToDictionary(p => p.Provider);
    }

    public ISubtitleProvider Resolve(SubtitleProviderType type)
    {
        if (!_providers.TryGetValue(type, out ISubtitleProvider? provider))
            throw new SubtitleProviderException($"未注册的字幕源：{type}");
        return provider;
    }
}
