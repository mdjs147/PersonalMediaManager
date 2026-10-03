using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Infrastructure.External.Ai;
namespace PersonalMediaManager.Infrastructure.External.Tests.Ai;
public sealed class AiResponseReaderTests
{
    [Fact]
    public async Task RejectsOversizedResponseBeforeMaterializingText()
    {
        using StringContent content = new(new string('x', 1025));
        Func<Task> read = () => AiResponseReader.ReadAsync(content, 1024, default);
        await read.Should().ThrowAsync<AiProviderLogicalException>();
    }
    [Fact]
    public async Task PreservesCancellation()
    {
        using StringContent content = new("{}");
        using CancellationTokenSource cancelled = new(); cancelled.Cancel();
        Func<Task> read = () => AiResponseReader.ReadAsync(content, 1024, cancelled.Token);
        await read.Should().ThrowAsync<OperationCanceledException>();
    }
}
