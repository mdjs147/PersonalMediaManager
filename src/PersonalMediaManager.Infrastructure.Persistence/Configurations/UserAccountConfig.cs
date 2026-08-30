using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Infrastructure.Persistence.Configurations;

/// <summary>User_Account 表配置</summary>
internal sealed class UserAccountConfig : IEntityTypeConfiguration<UserAccount>
{
    public void Configure(EntityTypeBuilder<UserAccount> b)
    {
        b.ToTable("User_Account");
        b.HasKey(x => x.Id);

        b.Property(x => x.Username).HasMaxLength(64).IsRequired();
        b.Property(x => x.PasswordHash).HasMaxLength(100).IsRequired();
        b.Property(x => x.Role).HasMaxLength(16).HasConversion<string>().HasDefaultValue(UserRole.Viewer).HasSentinel(UserRole.Viewer).IsRequired();
        b.Property(x => x.LastLoginIp).HasMaxLength(45);

        b.HasIndex(x => x.Username).IsUnique().HasDatabaseName("UQ_User_Account_Username");
        b.HasIndex(x => x.Role).HasDatabaseName("IX_User_Account_Role");
    }
}
