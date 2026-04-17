namespace LlmTrans.Core.Proxy;

/// Passthrough stream that tees every byte read or written into a side buffer,
/// capped so a misconfigured debug session can't OOM the host. Used by the debug
/// path in the OpenAI adapter to capture the streaming upstream bytes and the
/// translated bytes emitted to the client without burning extra memory in
/// production (the taps exist only when debug mode is on).
public sealed class TappingStream : Stream
{
    private readonly Stream _inner;
    private readonly MemoryStream _tap;
    private readonly int _cap;
    private int _captured;

    public TappingStream(Stream inner, MemoryStream tap, int cap)
    {
        _inner = inner;
        _tap = tap;
        _cap = cap;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }
    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = _inner.Read(buffer, offset, count);
        TapBytes(buffer.AsSpan(offset, n));
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var n = await _inner.ReadAsync(buffer, ct);
        TapBytes(buffer.Span[..n]);
        return n;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        TapBytes(buffer.AsSpan(offset, count));
        _inner.Write(buffer, offset, count);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        TapBytes(buffer.Span);
        await _inner.WriteAsync(buffer, ct);
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    private void TapBytes(ReadOnlySpan<byte> span)
    {
        if (_captured >= _cap || span.IsEmpty) return;
        var take = Math.Min(span.Length, _cap - _captured);
        _tap.Write(span[..take]);
        _captured += take;
    }
}
