using System.Text;
using AdaptiveApi.Core.Streaming;

namespace AdaptiveApi.Core.Tests;

public sealed class SseParserTests
{
    [Fact]
    public async Task Parses_data_events_separated_by_blank_lines()
    {
        var payload = "data: a\n\ndata: b\n\ndata: [DONE]\n\n";
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(payload));

        var events = new List<SseEvent>();
        await foreach (var ev in SseParser.ReadAsync(ms, default))
            events.Add(ev);

        Assert.Equal(3, events.Count);
        Assert.Equal("a", events[0].Data);
        Assert.Equal("b", events[1].Data);
        Assert.Equal("[DONE]", events[2].Data);
    }

    [Fact]
    public async Task Joins_multi_line_data_with_newline()
    {
        var payload = "data: line1\ndata: line2\n\n";
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        var events = new List<SseEvent>();
        await foreach (var ev in SseParser.ReadAsync(ms, default))
            events.Add(ev);
        Assert.Single(events);
        Assert.Equal("line1\nline2", events[0].Data);
    }

    [Fact]
    public async Task Ignores_comment_lines()
    {
        var payload = ": heartbeat\ndata: keep\n\n";
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        var events = new List<SseEvent>();
        await foreach (var ev in SseParser.ReadAsync(ms, default))
            events.Add(ev);
        Assert.Single(events);
        Assert.Equal("keep", events[0].Data);
    }

    [Fact]
    public void Serialize_round_trips_minimal_event()
    {
        var bytes = SseParser.SerializeEvent(new SseEvent(null, "hello"));
        Assert.Equal("data: hello\n\n", Encoding.UTF8.GetString(bytes));
    }
}
