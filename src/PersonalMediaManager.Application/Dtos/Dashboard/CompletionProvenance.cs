using System.Text.Json;
using PersonalMediaManager.Application.Dtos.History;
using PersonalMediaManager.Domain.Enums;

namespace PersonalMediaManager.Application.Dtos.Dashboard;

/// <summary>最终完成路线与保留历史中可证实的干预</summary>
/// <remarks>类别互斥，标记可重叠；缺少证据不等于无人干预，完成不等于匹配正确。</remarks>
public sealed record CompletionProvenance(
    string Category, bool EverReviewed, bool EverConfirmed, bool ExplicitCorrection,
    bool ManualArchive, bool ForcedAnchor, bool FolderReuse, bool AutomaticRetry);

/// <summary>当前已完成记录的证据统计</summary>
public sealed record CompletionProvenanceStats(
    int Completed, int Confirmed, int ManualArchive, int AutomaticPipeline, int Unknown,
    int EverReviewed, int EverConfirmed, int ExplicitCorrection, int ForcedAnchor,
    int FolderReuse, int AutomaticRetry);

/// <summary>处理时间线的保守证据投影</summary>
public static class CompletionProvenanceProjection
{
    public static CompletionProvenance Project(MediaItemStatus status, ParseSource? source,
        IEnumerable<ProcessStepEntry> steps)
    {
        ProcessStepEntry[] ordered = steps.OrderBy(s => s.StartedAt).ThenBy(s => s.Id).ToArray();
        ProcessStepEntry? latest = ordered.LastOrDefault(s => s.Stage == MediaItemStatus.Completed);
        string category = "NotCompleted";
        bool reviewed = false, confirmed = false, corrected = false, manual = false;
        bool anchor = source == ParseSource.Manual, reuse = false, retry = false;
        foreach (ProcessStepEntry step in ordered)
        {
            reviewed |= step.Stage == MediaItemStatus.AwaitingReview;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(step.Detail ?? "{}");
                JsonElement d = doc.RootElement;
                if (d.ValueKind != JsonValueKind.Object) continue;
                bool confirm = True(d, "confirm");
                bool archive = True(d, "manual") && step.Stage == MediaItemStatus.Completed;
                confirmed |= confirm;
                manual |= archive;
                corrected |= True(d, "explicitCorrection");
                bool matchingStage = step.Stage is MediaItemStatus.Parsing or MediaItemStatus.TmdbMatching or MediaItemStatus.TmdbRematching;
                anchor |= True(d, "forcedAnchor") || (matchingStage && Text(d, "source") == "manual");
                reuse |= True(d, "folderReuse") || (matchingStage && Text(d, "source") == "reuse");
                retry |= True(d, "autoRetry");
                if (status == MediaItemStatus.Completed && ReferenceEquals(step, latest))
                {
                    // 冲突标记无法自证路线；仅最新完成步骤决定最终类别。
                    string? route = Text(d, "completionRoute");
                    bool conflict = confirm && archive
                        || (VersionOne(d) && route is not null
                            && ((confirm && route != "Confirmed") || (archive && route != "ManualArchive")));
                    category = conflict ? "Unknown" : archive ? "ManualArchive" : confirm ? "Confirmed"
                        : VersionOne(d) && route == "AutomaticPipeline" ? "AutomaticPipeline" : "Unknown";
                }
            }
            catch (JsonException) { }
        }
        if (status == MediaItemStatus.Completed && category == "NotCompleted") category = "Unknown";
        return new(category, reviewed, confirmed, corrected, manual, anchor, reuse, retry);
    }

    public static CompletionProvenanceStats Aggregate(IEnumerable<CompletionProvenance> evidence)
    {
        CompletionProvenance[] rows = evidence.Where(e => e.Category != "NotCompleted").ToArray();
        return new(rows.Length, rows.Count(e => e.Category == "Confirmed"),
            rows.Count(e => e.Category == "ManualArchive"), rows.Count(e => e.Category == "AutomaticPipeline"),
            rows.Count(e => e.Category == "Unknown"), rows.Count(e => e.EverReviewed),
            rows.Count(e => e.EverConfirmed), rows.Count(e => e.ExplicitCorrection),
            rows.Count(e => e.ForcedAnchor), rows.Count(e => e.FolderReuse), rows.Count(e => e.AutomaticRetry));
    }

    private static bool True(JsonElement d, string key) => d.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.True;
    private static string? Text(JsonElement d, string key) => d.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static bool VersionOne(JsonElement d) => d.TryGetProperty("provenanceVersion", out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) && n == 1;
}
