using AnimeBatch.Core.Services;
using Xunit;

namespace AnimeBatch.Tests;

public class ProbeServiceTests
{
    private const string SampleJson = """
        {
          "streams": [
            { "index": 0, "codec_type": "video", "codec_name": "h264" },
            { "index": 1, "codec_type": "audio", "channels": 2, "tags": { "language": "jpn" } },
            { "index": 2, "codec_type": "subtitle", "tags": { "language": "por" } },
            { "index": 3, "codec_type": "subtitle", "tags": { "language": "eng" } }
          ],
          "format": { "duration": "1433.620000" },
          "chapters": [
            { "id": 0, "start_time": "0.000000", "end_time": "36.000000", "tags": { "title": "Episode" } },
            { "id": 1, "start_time": "36.000000", "end_time": "111.000000", "tags": { "title": "Intro" } },
            { "id": 2, "start_time": "111.000000", "end_time": "1433.620000", "tags": { "title": "Episode" } }
          ]
        }
        """;

    [Fact]
    public void Parse_extrai_duracao_trilhas_e_capitulos()
    {
        var info = ProbeService.Parse(SampleJson, @"E:\x\ep.mkv");

        Assert.Equal(1433.62, info.DurationSeconds, 3);
        Assert.Single(info.AudioStreams);
        Assert.Equal("jpn", info.AudioStreams[0].Language);
        Assert.Equal(2, info.AudioStreams[0].Channels);
        Assert.Equal(2, info.SubtitleStreams.Count);
        Assert.Equal("por", info.SubtitleStreams[0].Language);
        Assert.Equal(3, info.Chapters.Count);
        Assert.Equal(36.0, info.Chapters[1].StartSeconds, 3);
        Assert.Equal("Intro", info.Chapters[1].Title);
    }

    [Fact]
    public void Parse_json_sem_capitulos_ou_trilhas_nao_quebra()
    {
        var info = ProbeService.Parse("""{ "format": { "duration": "10.0" } }""", "x.mkv");

        Assert.Equal(10.0, info.DurationSeconds, 3);
        Assert.Empty(info.AudioStreams);
        Assert.Empty(info.SubtitleStreams);
        Assert.Empty(info.Chapters);
    }
}
