using PersonalMediaManager.Domain.Common;

namespace PersonalMediaManager.Domain.Tests.Common;

/// <summary>验证 Entity 按 Id 相等性的不变式，同时确认测试管线工作</summary>
public sealed class EntityEqualityTests
{
    private sealed class StubEntity : Entity
    {
        public StubEntity(long id) => Id = id;
    }

    private sealed class OtherStubEntity : Entity
    {
        public OtherStubEntity(long id) => Id = id;
    }

    [Fact]
    public void Equals_BySameId_ReturnsTrue()
    {
        var a = new StubEntity(42);
        var b = new StubEntity(42);

        a.Equals(b).Should().BeTrue();
        (a == b).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void Equals_ByDifferentId_ReturnsFalse()
    {
        new StubEntity(1).Equals(new StubEntity(2)).Should().BeFalse();
    }

    [Fact]
    public void Equals_WhenEitherIdIsZero_ReturnsFalse()
    {
        // 未持久化（Id=0）的实体不与任何实例相等，避免乐观并发误判
        new StubEntity(0).Equals(new StubEntity(0)).Should().BeFalse();
        new StubEntity(0).Equals(new StubEntity(1)).Should().BeFalse();
    }

    [Fact]
    public void Equals_AcrossDifferentEntityTypes_ReturnsFalse()
    {
        // 防止不同实体类型同 Id 互相 equal
        new StubEntity(7).Equals(new OtherStubEntity(7)).Should().BeFalse();
    }
}
