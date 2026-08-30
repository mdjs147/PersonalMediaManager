using PersonalMediaManager.Application.Contracts;
using PersonalMediaManager.Infrastructure.Platform.FileSystem;

namespace PersonalMediaManager.Infrastructure.Persistence.Tests.Audio;

/// <summary>FfprobeAudioProbe.ParseAudioStreams — ffprobe JSON 解析 + 不兼容标记</summary>
public sealed class FfprobeAudioProbeParseTests
{
    private static readonly IReadOnlySet<string> Av3a = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "av3a" };

    [Fact]
    public void Parses_Multiple_Audio_Streams_And_Flags_Incompatible()
    {
        const string json = """
        {
          "streams": [
            { "index": 1, "codec_name": "av3a", "channels": 6, "tags": { "language": "chi", "title": "菁彩声" } },
            { "index": 2, "codec_name": "aac", "channels": 2, "tags": { "language": "eng" } }
          ]
        }
        """;

        IReadOnlyList<AudioStreamInfo> streams = FfprobeAudioProbe.ParseAudioStreams(json, Av3a);

        streams.Should().HaveCount(2);
        streams[0].Index.Should().Be(1);
        streams[0].Codec.Should().Be("av3a");
        streams[0].Language.Should().Be("chi");
        streams[0].Channels.Should().Be(6);
        streams[0].Title.Should().Be("菁彩声");
        streams[0].IsIncompatible.Should().BeTrue();
        streams[1].Codec.Should().Be("aac");
        streams[1].IsIncompatible.Should().BeFalse();
    }

    [Fact]
    public void Empty_Streams_Array_Yields_Empty()
    {
        FfprobeAudioProbe.ParseAudioStreams("""{ "streams": [] }""", Av3a).Should().BeEmpty();
    }

    [Fact]
    public void Missing_Streams_Property_Yields_Empty()
    {
        FfprobeAudioProbe.ParseAudioStreams("""{ "format": {} }""", Av3a).Should().BeEmpty();
    }

    [Fact]
    public void Blank_Json_Yields_Empty()
    {
        FfprobeAudioProbe.ParseAudioStreams("", Av3a).Should().BeEmpty();
    }

    [Fact]
    public void Missing_Optional_Fields_Degrade_Gracefully()
    {
        // 无 tags / channels：language/title/channels 为 null，仍解析出 codec
        const string json = """{ "streams": [ { "index": 3, "codec_name": "dts" } ] }""";

        AudioStreamInfo s = FfprobeAudioProbe.ParseAudioStreams(json, Av3a).Single();

        s.Index.Should().Be(3);
        s.Codec.Should().Be("dts");
        s.Language.Should().BeNull();
        s.Channels.Should().BeNull();
        s.Title.Should().BeNull();
        s.IsIncompatible.Should().BeFalse(); // dts 不在 av3a 清单
    }

    [Fact]
    public void Codec_Match_Is_Case_Insensitive()
    {
        // ffprobe 一般输出小写，但清单用 OrdinalIgnoreCase 兜大小写漂移
        const string json = """{ "streams": [ { "index": 1, "codec_name": "AV3A" } ] }""";

        FfprobeAudioProbe.ParseAudioStreams(json, Av3a).Single().IsIncompatible.Should().BeTrue();
    }

    [Fact]
    public void Multiple_Incompatible_Codecs_All_Flagged()
    {
        IReadOnlySet<string> list = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "av3a", "truehd" };
        const string json = """
        {
          "streams": [
            { "index": 1, "codec_name": "av3a" },
            { "index": 2, "codec_name": "truehd" },
            { "index": 3, "codec_name": "ac3" }
          ]
        }
        """;

        IReadOnlyList<AudioStreamInfo> streams = FfprobeAudioProbe.ParseAudioStreams(json, list);

        streams.Where(s => s.IsIncompatible).Select(s => s.Index).Should().BeEquivalentTo(new[] { 1, 2 });
    }
}
