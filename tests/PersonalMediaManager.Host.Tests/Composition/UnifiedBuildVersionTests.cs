using System.Reflection;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Infrastructure.Persistence;

namespace PersonalMediaManager.Host.Tests.Composition;

public sealed class UnifiedBuildVersionTests
{
    [Fact]
    public void AssembliesAndCompatibilityMetadataShareOneProductVersion()
    {
        Assembly[] assemblies = [typeof(IVersionInfoProvider).Assembly, typeof(PmmDbContext).Assembly,
            typeof(UnifiedBuildVersionTests).Assembly];
        string? expected = null;
        foreach (Assembly assembly in assemblies)
        {
            Dictionary<string, string?> metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .ToDictionary(attribute => attribute.Key, attribute => attribute.Value);
            string product = metadata["ProductVersion"]!;
            product.Should().MatchRegex(@"^\d+\.\d+\.\d+$");
            expected ??= product;
            product.Should().Be(expected);
            metadata["FrontendVersion"].Should().Be(product, "兼容字段只能由主版本派生");
            metadata.Should().NotContainKey("DbVersion", "数据库迁移标识不再作为发布版本注入");
            assembly.GetName().Version!.ToString().Should().Be(product + ".0");
            assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()!.Version.Should().Be(product + ".0");
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion
                .Should().StartWith(product + "+", "提交标识是构建诊断，不能引入另一套产品版本");
        }
    }
}
