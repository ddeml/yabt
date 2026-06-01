namespace Yabt.WebDav.Implementation;

internal sealed class WebDavNonDisposingStream(Stream _inner) : Stream
{
    public override bool CanRead => _inner.CanRead;

    public override bool CanSeek => _inner.CanSeek;

    public override bool CanWrite => _inner.CanWrite;

    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override void Flush()
    {
        _inner.Flush();
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        return _inner.FlushAsync(cancellationToken);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return _inner.Read(buffer, offset, count);
    }

    public override ValueTask<int> ReadAsync
    (
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        return _inner.ReadAsync(buffer, cancellationToken);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        return _inner.Seek(offset, origin);
    }

    public override void SetLength(long value)
    {
        _inner.SetLength(value);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        _inner.Write(buffer, offset, count);
    }

    public override ValueTask WriteAsync
    (
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        return _inner.WriteAsync(buffer, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
    }

    public override ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
