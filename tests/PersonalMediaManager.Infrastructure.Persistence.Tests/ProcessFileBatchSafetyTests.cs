using Microsoft.EntityFrameworkCore;
using NSubstitute;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Application.Services.Archive;
using PersonalMediaManager.Application.Services.Classify;
using PersonalMediaManager.Application.Services.Parse;
namespace PersonalMediaManager.Infrastructure.Persistence.Tests;

public sealed partial class ProcessFileServiceTests
{
    [Theory]
    [InlineData(true, 1, ProcessOutcome.Skipped)]
    [InlineData(false, 2, ProcessOutcome.Completed)]
    public async Task BatchedPreparationRechecksLiveCopyBeforeArchiving(bool targetExists, int calls, ProcessOutcome second)
    {
        ConfigureRule(0.2, false, title: "Example", year: null);
        ConfigureClassify(ClassifyDecision.Matched, 3);
        ConfigureArchive(ArchiveOutcome.Completed, "/Movies/Example.mkv");
        _fileHasher.TryComputeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("same-fixture-hash");
        _fileProbe.FileExists("/Movies/Example.mkv").Returns(targetExists);
        _tmdb.SearchAsync(Arg.Any<TmdbSearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TmdbSearchResult([NewCandidate(1001, "movie", "Example", 2024)], null));
        Task<IReadOnlyDictionary<string, AiCallOutcome>> Batch(IReadOnlyList<AiBatchEntry<AiParseRequest>> entries, CancellationToken _)
        {
            using PmmDbContext check = _dbFactory.CreateDbContext();
            check.Database.CurrentTransaction.Should().BeNull();
            check.Database.ExecuteSqlRaw("UPDATE System_Setting SET Value = Value WHERE 1 = 0");
            return Task.FromResult<IReadOnlyDictionary<string, AiCallOutcome>>(entries.ToDictionary(e => e.Id,
                _ => new AiCallOutcome(true, new("Example", null, "movie", null, null, null, 0.9), 1, 1, null)));
        }
        _aiOrchestrator.ExecuteAsync(Arg.Any<AiParseRequest>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(info => AiBatchPipeline.Schedule("fixture", info.Arg<AiParseRequest>(), Batch, info.Arg<CancellationToken>())!);
        ProcessFileOutcome?[] outcomes = new ProcessFileOutcome?[2];
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        await AiBatchPipeline.RunAsync(Enumerable.Range(0, 2).Select<int, Func<CancellationToken, Task>>(i => async ct =>
        { outcomes[i] = await NewSut().ProcessAsync(new(Path.Combine(root, $"Example-{i}.mkv"), 0, PendingFileSource.Manual), ct); }).ToArray(),
            new() { MaxItems = 2 });
        outcomes[0]!.Outcome.Should().Be(ProcessOutcome.Completed);
        outcomes[1]!.Outcome.Should().Be(second);
        await _archive.Received(calls).ArchiveAsync(Arg.Any<PersonalMediaManager.Domain.Aggregates.MediaItems.MediaItem>(), Arg.Any<CancellationToken>());
    }
}
