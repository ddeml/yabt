namespace Yabt.Tests;

public sealed class CountingNonSeekableReadStream
(
    Stream _innerStream,
    Action<int> _observeRead
) : Stream
{
    public override bool CanRead => _innerStream.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => _innerStream.Flush();

    public override int Read(byte[] buffer, int offset, int count)
    {
        var bytesRead = _innerStream.Read(buffer, offset, count);
        _observeRead(bytesRead);
        return bytesRead;
    }

    public override int Read(Span<byte> buffer)
    {
        var bytesRead = _innerStream.Read(buffer);
        _observeRead(bytesRead);
        return bytesRead;
    }

    public override async Task<int> ReadAsync
    (
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    )
    {
        var bytesRead = await _innerStream.ReadAsync(
            buffer,
            offset,
            count,
            cancellationToken);
        _observeRead(bytesRead);
        return bytesRead;
    }

    public override async ValueTask<int> ReadAsync
    (
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        var bytesRead = await _innerStream.ReadAsync(
            buffer,
            cancellationToken);
        _observeRead(bytesRead);
        return bytesRead;
    }

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _innerStream.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _innerStream.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
