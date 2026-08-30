using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PersonalMediaManager.Domain.Entities;

namespace PersonalMediaManager.Infrastructure.Persistence.Configurations;

/// <summary>System_Setting 表配置（PK=Key 字符串，含种子数据）</summary>
internal sealed class SystemSettingConfig : IEntityTypeConfiguration<SystemSetting>
{
    public void Configure(EntityTypeBuilder<SystemSetting> b)
    {
        b.ToTable("System_Setting");
        b.HasKey(x => x.Key);

        b.Property(x => x.Key).HasMaxLength(100).IsRequired();
        b.Property(x => x.Value).HasMaxLength(4000);
        b.Property(x => x.Category).HasMaxLength(50).HasDefaultValue("General").IsRequired();
        b.Property(x => x.Description).HasMaxLength(500);
        b.Property(x => x.CreatedAt).IsRequired();
        b.Property(x => x.UpdatedAt).IsRequired();

        b.HasIndex(x => x.Category).HasDatabaseName("IX_System_Setting_Category");

        // 种子（数据库设计 §1.1 典型种子）；CreatedAt/UpdatedAt 在 Migration 内为固定时间，便于幂等
        // Description 用中文短句（前端「常规」页折叠项的字段标签直接取此字段；空则回退到 Key）
        var seedTime = new DateTimeOffset(2026, 5, 16, 0, 0, 0, TimeSpan.Zero);
        // 升级检查相关 5 个 key 在 0.2.0 引入，单独打一个时间戳便于 Migration 落 InsertData
        var updateSeedTime = new DateTimeOffset(2026, 5, 27, 0, 0, 0, TimeSpan.Zero);
        // 升级检查的 Enabled / CheckIntervalHours 从 appsettings 迁移到 db，单独打一个时间戳便于追踪
        var updateEnabledSeedTime = new DateTimeOffset(2026, 5, 27, 12, 0, 0, TimeSpan.Zero);
        // 代理设置（5 个 Proxy_* key）：总开关 + 地址 + bypass + TMDB / 升级检查 子开关；
        // AI 提供商的「是否走代理」落在 Parse_AiProvider.UseProxy 列（每条 Provider 独立可选）
        var proxySeedTime = new DateTimeOffset(2026, 5, 27, 20, 0, 0, TimeSpan.Zero);
        // Webhook 总开关（0.12.0 新增）：默认 false，关闭时归档不产生 Webhook_Delivery
        var webhookSeedTime = new DateTimeOffset(2026, 6, 3, 0, 0, 0, TimeSpan.Zero);
        // AI 调用日志留存（监控增强 0.5.0）：保留天数 + 单 provider 行数上限双闸，AiCallRetentionJob 每日清理
        var aiCallRetentionSeedTime = new DateTimeOffset(2026, 6, 4, 0, 0, 0, TimeSpan.Zero);
        // 自动备份（BackupJob 每日 04:00 VACUUM INTO 在线快照 + 按份数清理；与 BackupService.EnabledKey / RetainCountKey 必须 1:1 对齐）
        var backupSeedTime = new DateTimeOffset(2026, 6, 6, 0, 0, 0, TimeSpan.Zero);
        // 健康告警阈值（0.15.0）：归档盘剩余 warn/critical 百分比 + 告警抑制窗口；HealthCheckService 读阈值，AlertService 读抑制窗口
        var healthSeedTime = new DateTimeOffset(2026, 6, 6, 12, 0, 0, TimeSpan.Zero);
        // 空目录清理可忽略扩展名清单（0.3.0 新增）：配合既有 File.CleanEmptyDir 总开关，把「仅剩 .torrent 等残留」的目录也视为空一并回收；与 ArchiveService.CleanEmptyDirIgnoreExtsKey 对齐
        var cleanEmptyDirSeedTime = new DateTimeOffset(2026, 6, 13, 0, 0, 0, TimeSpan.Zero);
        // 音频不兼容轨处理（av3a Audio Vivid 等）：总开关 + ffmpeg 路径 + 不兼容 codec 清单 + 自动重混开关；与 ProcessFileService.Audio*Key 必须 1:1 对齐
        var audioSeedTime = new DateTimeOffset(2026, 6, 19, 0, 0, 0, TimeSpan.Zero);
        // 归档命名模板（0.6.0 新增）：3 个文件名主体模板 + 1 个季标题开关；默认值 == 改造前硬编码命名（升级零行为变化）；
        // 与 ArchiveNamingTemplateReader.*Key 必须 1:1 对齐，写入校验走 ArchiveNaming 专属页 + NamingTemplateRenderer.ValidateTemplate
        var archiveNamingSeedTime = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        b.HasData(
            new SystemSetting { Key = "Web.Port",                       Value = "7288",         Category = "General", Description = "Web 服务端口",                                CreatedAt = seedTime, UpdatedAt = seedTime },
            new SystemSetting { Key = "Parse.ConfidenceThreshold",      Value = "0.6",          Category = "Parse",   Description = "解析置信度阈值（低于则进入人工确认）",        CreatedAt = seedTime, UpdatedAt = seedTime },
            new SystemSetting { Key = "Parse.AiConfidenceThreshold",    Value = "0.7",          Category = "Parse",   Description = "AI 兜底解析置信度阈值",                       CreatedAt = seedTime, UpdatedAt = seedTime },
            // 原 Tmdb.CandidateThreshold KV 已移除：TMDB 候选阈值的权威值在 Tmdb_Setting.CandidateThreshold 列（TMDB 专属页编辑、带 [1,50] 校验），该 KV 无任何消费点且与专属页重复
            new SystemSetting { Key = "File.Operation",                 Value = "Move",         Category = "General", Description = "文件操作方式（Move / Copy / Link）",          CreatedAt = seedTime, UpdatedAt = seedTime },
            new SystemSetting { Key = "File.CleanEmptyDir",             Value = "false",        Category = "General", Description = "归档完成后清理源端空目录",                    CreatedAt = seedTime, UpdatedAt = seedTime },
            new SystemSetting { Key = "Scan.IntervalHours",             Value = "12",           Category = "General", Description = "全量扫描周期（小时）",                        CreatedAt = seedTime, UpdatedAt = seedTime },
            new SystemSetting { Key = "Stability.SecondsBeforeReady",   Value = "5",            Category = "General", Description = "文件稳定判定时长（秒，写入静默达此值后视为就绪）", CreatedAt = seedTime, UpdatedAt = seedTime },
            new SystemSetting { Key = "Log.Level",                      Value = "Information",  Category = "Log",     Description = "日志级别（Trace / Debug / Information / Warning / Error）", CreatedAt = seedTime, UpdatedAt = seedTime },
            // 原 Webhook.DeliveryRetainCount KV 已移除：投递记录保留特性从未实现（全仓无任何消费点，Webhook_Delivery 只增不减，仅靠手动清史 + 删订阅级联兜底），
            // 且 86c50ae 后 Webhook 分组已从常规设置页隐藏 → 该 KV UI 不可见亦无人读；如需自动保留应作为时间制维护 Job（参照 LogRetentionJob）重新设计，而非接此死键
            // Webhook 总开关（与 ArchiveService.WebhookEnabledKey / WebhookSubscriptionService.GlobalEnabledKey 必须 1:1 对齐）
            new SystemSetting { Key = "Webhook_Enabled",                Value = "false",        Category = "Webhook", Description = "Webhook 总开关（false=关闭时归档不产生投递记录）", CreatedAt = webhookSeedTime, UpdatedAt = webhookSeedTime },
            // 升级检查（与 IUpdateSettingService / UpdateSettingService 的 Key 常量必须 1:1 对齐）
            new SystemSetting { Key = "Update_GitHubOwner",             Value = "mdjs147",      Category = "Update",  Description = "GitHub 仓库所有者（升级检查数据源）",         CreatedAt = updateSeedTime, UpdatedAt = updateSeedTime },
            new SystemSetting { Key = "Update_GitHubRepo",              Value = "PersonalMediaManager", Category = "Update", Description = "GitHub 仓库名（升级检查数据源）",     CreatedAt = updateSeedTime, UpdatedAt = updateSeedTime },
            new SystemSetting { Key = "Update_GitHubPat",               Value = null,           Category = "Update",  Description = "GitHub PAT（加密存储；空 = 匿名 60 req/h，配置 = 5000 req/h）", CreatedAt = updateSeedTime, UpdatedAt = updateSeedTime },
            new SystemSetting { Key = "Update_LastCheckJson",           Value = null,           Category = "Update",  Description = "上次升级检查结果 JSON 快照（含 etag/error/latest）", CreatedAt = updateSeedTime, UpdatedAt = updateSeedTime },
            new SystemSetting { Key = "Update_SkippedVersion",          Value = null,           Category = "Update",  Description = "用户跳过的版本号（命中后不再气泡提示）",      CreatedAt = updateSeedTime, UpdatedAt = updateSeedTime },
            new SystemSetting { Key = "Update_Enabled",                 Value = "true",         Category = "Update",  Description = "自动检查启用开关（true / false）",            CreatedAt = updateEnabledSeedTime, UpdatedAt = updateEnabledSeedTime },
            new SystemSetting { Key = "Update_CheckIntervalHours",      Value = "24",           Category = "Update",  Description = "自动检查周期（小时，[1, 720]）",              CreatedAt = updateEnabledSeedTime, UpdatedAt = updateEnabledSeedTime },
            // 代理设置（与 IProxyResolver / ProxyResolver 的 Key 常量必须 1:1 对齐）；
            // Proxy.UseForAi 不在此处 — AI 走代理由每个 Parse_AiProvider.UseProxy 单独决定
            new SystemSetting { Key = "Proxy_Enabled",                  Value = "false",        Category = "Proxy",   Description = "代理总开关（true / false）",                  CreatedAt = proxySeedTime, UpdatedAt = proxySeedTime },
            new SystemSetting { Key = "Proxy_HttpUrl",                  Value = null,           Category = "Proxy",   Description = "HTTP 代理地址（如 http://127.0.0.1:7890）",   CreatedAt = proxySeedTime, UpdatedAt = proxySeedTime },
            new SystemSetting { Key = "Proxy_BypassList",               Value = null,           Category = "Proxy",   Description = "额外 bypass 规则，逗号或换行分隔（如 *.cn,corp.local）",  CreatedAt = proxySeedTime, UpdatedAt = proxySeedTime },
            new SystemSetting { Key = "Proxy_UseForTmdb",                Value = "true",         Category = "Proxy",   Description = "TMDB 客户端是否走代理（true / false）",       CreatedAt = proxySeedTime, UpdatedAt = proxySeedTime },
            new SystemSetting { Key = "Proxy_UseForUpdateCheck",         Value = "true",         Category = "Proxy",   Description = "GitHub 升级检查是否走代理（true / false）",   CreatedAt = proxySeedTime, UpdatedAt = proxySeedTime },
            // AI 调用日志留存双闸（与 AiCallRetentionService.RetentionDaysKey / MaxRowsPerProviderKey 必须 1:1 对齐）
            new SystemSetting { Key = "Audit_AiCallRetentionDays",       Value = "90",           Category = "Audit",   Description = "AI 调用日志保留天数（超期清理；0=不按天清理）",         CreatedAt = aiCallRetentionSeedTime, UpdatedAt = aiCallRetentionSeedTime },
            new SystemSetting { Key = "Audit_AiCallMaxRowsPerProvider",  Value = "50000",        Category = "Audit",   Description = "单 AI 提供商调用日志最大保留行数（超额删最旧；0=不限行数）", CreatedAt = aiCallRetentionSeedTime, UpdatedAt = aiCallRetentionSeedTime },
            // 自动备份双 key（与 BackupService.EnabledKey / RetainCountKey 必须 1:1 对齐；BackupJob 每日 04:00 触发）
            new SystemSetting { Key = "Backup_Enabled",                  Value = "true",         Category = "Backup",  Description = "自动备份开关（每日 04:00 在线快照数据库 + 密钥环到 backups 目录）", CreatedAt = backupSeedTime, UpdatedAt = backupSeedTime },
            new SystemSetting { Key = "Backup_RetainCount",              Value = "7",            Category = "Backup",  Description = "备份保留份数（超出删最旧；范围 [1, 365]）", CreatedAt = backupSeedTime, UpdatedAt = backupSeedTime },
            // 健康告警阈值（与 HealthCheckService.DiskWarnPercentKey/DiskCriticalPercentKey、AlertService.SuppressMinutesKey 必须 1:1 对齐）
            new SystemSetting { Key = "Archive_DiskWarnPercent",         Value = "10",           Category = "Archive", Description = "归档盘剩余空间低于此百分比 → 健康检查 warn + disk.low 通知（0=不检查）", CreatedAt = healthSeedTime, UpdatedAt = healthSeedTime },
            new SystemSetting { Key = "Archive_DiskCriticalPercent",     Value = "5",            Category = "Archive", Description = "归档盘剩余空间低于此百分比 → 健康检查 fail + disk.low 严重通知（0=不检查）", CreatedAt = healthSeedTime, UpdatedAt = healthSeedTime },
            new SystemSetting { Key = "System_AlertSuppressMinutes",     Value = "60",           Category = "Alert",   Description = "同类告警抑制窗口（分钟，窗口内同一告警只发一次，防风暴）", CreatedAt = healthSeedTime, UpdatedAt = healthSeedTime },
            // 空目录清理可忽略扩展名清单（与 File.CleanEmptyDir 总开关配套；留空 = 仅清真正的空目录）
            new SystemSetting { Key = "File.CleanEmptyDirIgnoreExts",     Value = ".torrent",     Category = "General", Description = "清理空目录时视为「可忽略残留」的扩展名清单（逗号分隔；目录仅剩这些文件 + 子目录递归为空也算空，一并删除；留空=仅清真空目录）", CreatedAt = cleanEmptyDirSeedTime, UpdatedAt = cleanEmptyDirSeedTime },
            // 音频不兼容轨处理（av3a Audio Vivid 等；4 个 key 与 ProcessFileService.Audio*Key 必须 1:1 对齐）
            new SystemSetting { Key = "Audio_IncompatibleCheckEnabled",  Value = "false",        Category = "Audio",   Description = "音频不兼容轨检查总开关（开则归档前用 ffprobe 探测 av3a 等不兼容音轨并打标）", CreatedAt = audioSeedTime, UpdatedAt = audioSeedTime },
            new SystemSetting { Key = "Audio_FfmpegPath",                Value = null,           Category = "Audio",   Description = "ffmpeg / ffprobe 所在目录或 ffmpeg 可执行文件路径（自备，留空=不探测不重混）", CreatedAt = audioSeedTime, UpdatedAt = audioSeedTime },
            new SystemSetting { Key = "Audio_IncompatibleCodecs",        Value = "av3a",         Category = "Audio",   Description = "视为不兼容的音频编解码器清单（逗号分隔，小写；默认 av3a 菁彩声；留空=不检查）", CreatedAt = audioSeedTime, UpdatedAt = audioSeedTime },
            new SystemSetting { Key = "Audio_AutoRemux",                 Value = "false",        Category = "Audio",   Description = "自动重混丢不兼容音轨（开且存在其它兼容音轨时归档用 ffmpeg 流复制去掉 av3a 轨；关=仅标记）", CreatedAt = audioSeedTime, UpdatedAt = audioSeedTime },
            // 归档命名模板（4 个 key 与 ArchiveNamingTemplateReader.*Key 必须 1:1 对齐；默认值 == NamingTemplateOptions.Default == 改造前硬编码命名）
            new SystemSetting { Key = "Archive_MovieNameTemplate",        Value = "{title} ({year})",            Category = "Archive", Description = "电影目录名 + 文件名主体模板（{tmdb-NNN} 后缀由系统自动追加）。占位符：{title} {originaltitle} {year} {tmdbid} {edition}", CreatedAt = archiveNamingSeedTime, UpdatedAt = archiveNamingSeedTime },
            new SystemSetting { Key = "Archive_TvShowFolderTemplate",     Value = "{title} ({year})",            Category = "Archive", Description = "剧集根目录主体模板（{tmdb-NNN} 后缀由系统自动追加）。占位符：{title} {originaltitle} {year} {tmdbid}", CreatedAt = archiveNamingSeedTime, UpdatedAt = archiveNamingSeedTime },
            new SystemSetting { Key = "Archive_TvEpisodeNameTemplate",    Value = "{title} ({year}) - {episode}", Category = "Archive", Description = "单集文件名主体模板（必含 {episode}=SxxEyy）。占位符：{title} {originaltitle} {year} {seasonyear} {tmdbid} {episode}", CreatedAt = archiveNamingSeedTime, UpdatedAt = archiveNamingSeedTime },
            new SystemSetting { Key = "Archive_SeasonFolderIncludeTitle", Value = "true",                        Category = "Archive", Description = "季目录是否追加季标题后缀（true=「Season 03 锻刀村篇」；false=纯「Season 03」）。季目录 Season NN 前缀本身锁定不可改", CreatedAt = archiveNamingSeedTime, UpdatedAt = archiveNamingSeedTime });
    }
}
