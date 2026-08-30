using Microsoft.EntityFrameworkCore;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Common.Archiving;
using PersonalMediaManager.Application.Dtos.Settings;
using PersonalMediaManager.Application.Services.Settings;
using PersonalMediaManager.Domain.Entities;
using PersonalMediaManager.Infrastructure.Persistence.Services.Archive;

namespace PersonalMediaManager.Infrastructure.Persistence.Services.Settings;

/// <summary>归档命名模板设置服务实现（读 / 写 4 个 Archive_ key + 草稿预览）</summary>
/// <remarks>
/// 读：复用 ArchiveNamingTemplateReader（缺失 / 非法逐项回退默认，与实际归档读模板同口径）。
/// 写：三模板先经 NamingTemplateRenderer.ValidateTemplate 校验（非法抛 BusinessException 1000），再 upsert 4 个 key。
/// 预览：用固定示例 + 与实际归档相同的 ArchiveFolderResolver.BuildRelativeTargetPath（targetRootForReuse=null 不扫盘），
///       保证渲染出的相对路径形态（含 {tmdb-NNN} / Season NN / SxxEyy 锁定结构）== 真实归档落点。
/// </remarks>
internal sealed class ArchiveNamingService : IArchiveNamingService
{
    private readonly IDbContextFactory<PmmDbContext> _dbFactory;

    public ArchiveNamingService(IDbContextFactory<PmmDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<ArchiveNamingResponse> GetAsync(CancellationToken ct = default)
    {
        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);
        NamingTemplateOptions opts = await ArchiveNamingTemplateReader.ReadAsync(db, logger: null, ct);
        return new ArchiveNamingResponse(
            opts.MovieNameTemplate, opts.TvShowFolderTemplate, opts.TvEpisodeNameTemplate, opts.SeasonFolderIncludeTitle);
    }

    public async Task UpdateAsync(UpdateArchiveNamingRequest req, CancellationToken ct = default)
    {
        // 三模板逐个语义校验（拒绝 {tmdb 字面量 / 路径分隔符 / 未知占位符；单集必含 {episode}）；任一非法即整体拒绝
        ValidateOrThrow(req.MovieNameTemplate, NamingTemplateRenderer.TemplateKind.Movie, "电影命名模板");
        ValidateOrThrow(req.TvShowFolderTemplate, NamingTemplateRenderer.TemplateKind.TvShowFolder, "剧集根目录模板");
        ValidateOrThrow(req.TvEpisodeNameTemplate, NamingTemplateRenderer.TemplateKind.TvEpisode, "单集命名模板");

        await using PmmDbContext db = await _dbFactory.CreateDbContextAsync(ct);
        await UpsertAsync(db, ArchiveNamingTemplateReader.MovieNameTemplateKey, req.MovieNameTemplate.Trim(), ct);
        await UpsertAsync(db, ArchiveNamingTemplateReader.TvShowFolderTemplateKey, req.TvShowFolderTemplate.Trim(), ct);
        await UpsertAsync(db, ArchiveNamingTemplateReader.TvEpisodeNameTemplateKey, req.TvEpisodeNameTemplate.Trim(), ct);
        await UpsertAsync(db, ArchiveNamingTemplateReader.SeasonFolderIncludeTitleKey, req.SeasonFolderIncludeTitle ? "true" : "false", ct);
        await db.SaveChangesAsync(ct);
    }

    public ArchiveNamingPreviewResponse Preview(ArchiveNamingPreviewRequest req)
    {
        // 各模板独立校验，错误清单分别返回（前端按模板内联展示）
        NamingTemplateRenderer.ValidationOutcome movie = NamingTemplateRenderer.ValidateTemplate(req.MovieNameTemplate, NamingTemplateRenderer.TemplateKind.Movie);
        NamingTemplateRenderer.ValidationOutcome tvFolder = NamingTemplateRenderer.ValidateTemplate(req.TvShowFolderTemplate, NamingTemplateRenderer.TemplateKind.TvShowFolder);
        NamingTemplateRenderer.ValidationOutcome tvEp = NamingTemplateRenderer.ValidateTemplate(req.TvEpisodeNameTemplate, NamingTemplateRenderer.TemplateKind.TvEpisode);

        // 合法模板才参与渲染；非法模板的样例直接给出错误占位（不抛，便于实时预览）
        NamingTemplateOptions opts = new(
            movie.IsValid ? req.MovieNameTemplate.Trim() : NamingTemplateOptions.DefaultMovieNameTemplate,
            tvFolder.IsValid ? req.TvShowFolderTemplate.Trim() : NamingTemplateOptions.DefaultTvShowFolderTemplate,
            tvEp.IsValid ? req.TvEpisodeNameTemplate.Trim() : NamingTemplateOptions.DefaultTvEpisodeNameTemplate,
            req.SeasonFolderIncludeTitle);

        List<ArchiveNamingPreviewSample> samples = new()
        {
            // 电影：盗梦空间 2010 tmdb27205，导演剪辑版 edition
            RenderSample("电影（多版本）", movie.IsValid,
                () => ArchiveFolderResolver.BuildRelativeTargetPath(
                    "盗梦空间", 2010, 27205, isTv: false, season: 0, episode: 0, episodeEnd: null, extension: "mkv",
                    targetRootForReuse: null, logger: null, seasonYear: null, seasonTitle: null,
                    template: opts, originalTitle: "Inception", edition: "导演剪辑版")),

            // 剧集单集：鬼灭之刃 2019 tmdb85937，S03E01，季首播年 2023，季标题「锻刀村篇」
            RenderSample("剧集（单集）", tvFolder.IsValid && tvEp.IsValid,
                () => ArchiveFolderResolver.BuildRelativeTargetPath(
                    "鬼灭之刃", 2019, 85937, isTv: true, season: 3, episode: 1, episodeEnd: null, extension: "mkv",
                    targetRootForReuse: null, logger: null, seasonYear: 2023, seasonTitle: "锻刀村篇",
                    template: opts, originalTitle: "Kimetsu no Yaiba")),

            // 剧集双集合并：S01E08-E09
            RenderSample("剧集（双集合并）", tvFolder.IsValid && tvEp.IsValid,
                () => ArchiveFolderResolver.BuildRelativeTargetPath(
                    "某番剧", 2020, 12345, isTv: true, season: 1, episode: 8, episodeEnd: 9, extension: "mkv",
                    targetRootForReuse: null, logger: null, seasonYear: null, seasonTitle: null,
                    template: opts, originalTitle: null)),
        };

        return new ArchiveNamingPreviewResponse(movie.Errors, tvFolder.Errors, tvEp.Errors, samples);
    }

    /// <summary>渲染单条样例：相关模板非法则给错误占位；渲染抛 ArgumentException（如清洗后空）则捕获为 error</summary>
    private static ArchiveNamingPreviewSample RenderSample(string label, bool relatedTemplatesValid, Func<string> render)
    {
        if (!relatedTemplatesValid)
            return new ArchiveNamingPreviewSample(label, null, "模板校验未通过，修正后可预览");
        try
        {
            return new ArchiveNamingPreviewSample(label, render(), null);
        }
        catch (ArgumentException ex)
        {
            return new ArchiveNamingPreviewSample(label, null, $"渲染失败：{ex.Message}");
        }
    }

    private static void ValidateOrThrow(string template, NamingTemplateRenderer.TemplateKind kind, string label)
    {
        NamingTemplateRenderer.ValidationOutcome outcome = NamingTemplateRenderer.ValidateTemplate(template, kind);
        if (!outcome.IsValid)
            throw new BusinessException($"{label}非法：{string.Join("；", outcome.Errors)}");
    }

    private static async Task UpsertAsync(PmmDbContext db, string key, string value, CancellationToken ct)
    {
        SystemSetting? existing = await db.SystemSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (existing is not null)
            existing.Value = value;
        else
            db.SystemSettings.Add(new SystemSetting { Key = key, Value = value, Category = "Archive" });
    }
}
