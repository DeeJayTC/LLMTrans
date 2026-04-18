using System.Runtime.CompilerServices;
using System.Text;

namespace AdaptiveApi.Core.Streaming;

public sealed record SseEvent(string? Event, string Data);

public static class SseParser
{
    public static async IAsyncEnumerable<SseEvent> ReadAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        string? eventName = null;
        var data = new StringBuilder();

        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;

            if (line.Length == 0)
            {
                if (data.Length > 0 || eventName is not null)
                {
                    yield return new SseEvent(eventName, data.ToString());
                    eventName = null;
                    data.Clear();
                }
                continue;
            }

            if (line.StartsWith(":", StringComparison.Ordinal)) continue;

            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? string.Empty
                : (colon + 1 < line.Length && line[colon + 1] == ' ' ? line[(colon + 2)..] : line[(colon + 1)..]);

            switch (field)
            {
                case "event": eventName = value; break;
                case "data":
                    if (data.Length > 0) data.Append('\n');
                    data.Append(value);
                    break;
                case "id":
                case "retry":
                    // ignored for our use case
                    break;
            }
        }

        if (data.Length > 0 || eventName is not null)
            yield return new SseEvent(eventName, data.ToString());
    }

    public static byte[] SerializeEvent(SseEvent ev)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(ev.Event))
            sb.Append("event: ").Append(ev.Event).Append('\n');
        sb.Append("data: ").Append(ev.Data).Append("\n\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }
}
