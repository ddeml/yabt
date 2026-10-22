namespace Yabt.Format.Zip.Implementation;

internal sealed class ZipEntryRangeStream(Stream _source, long _length, bool _leaveOpen) : Stream
{
    private long _position;
    private bool _disposed;

    public override bool CanRead => !_disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position { get => _position; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var remaining = (int)Math.Min(buffer.Length, _length - _position);
        if (remaining == 0) { return 0; }
        var count = _source.Read(buffer[..remaining]);
        if (count == 0) { throw new InvalidDataException("ZIP compressed entry content is truncated."); }
        _position += count;
        return count;
    }

    public override async ValueTask<int> ReadAsync
    (
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var remaining = (int)Math.Min(buffer.Length, _length - _position);
        if (remaining == 0) { return 0; }
        var count = await _source.ReadAsync(buffer[..remaining], cancellationToken);
        if (count == 0) { throw new InvalidDataException("ZIP compressed entry content is truncated."); }
        _position += count;
        return count;
    }

    public override Task<int> ReadAsync
    (
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    ) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() { }
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            if (!_leaveOpen) { _source.Dispose(); }
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed) { return; }
        _disposed = true;
        if (!_leaveOpen) { await _source.DisposeAsync(); }
        GC.SuppressFinalize(this);
    }
}
