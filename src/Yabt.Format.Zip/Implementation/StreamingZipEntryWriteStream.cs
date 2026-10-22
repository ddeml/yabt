using System.IO.Hashing;

namespace Yabt.Format.Zip.Implementation;

internal sealed class StreamingZipEntryWriteStream
(
    Stream _compressed,
    bool leaveOpen,
    Action<long, uint> _complete
) : Stream
{
    private readonly Crc32 _crc = new();
    private long _length;
    private bool _disposed;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => !_disposed;
    public override long Length => _length;
    public override long Position { get => _length; set => throw new NotSupportedException(); }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _compressed.Write(buffer);
        _crc.Append(buffer);
        _length = checked(_length + buffer.Length);
    }

    public override async ValueTask WriteAsync
    (
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _compressed.WriteAsync(buffer, cancellationToken);
        _crc.Append(buffer.Span);
        _length = checked(_length + buffer.Length);
    }

    public override Task WriteAsync
    (
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    ) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() => _compressed.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _compressed.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            if (!leaveOpen) { _compressed.Dispose(); }
            _complete(_length, _crc.GetCurrentHashAsUInt32());
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed) { return; }
        _disposed = true;
        if (!leaveOpen) { await _compressed.DisposeAsync(); }
        _complete(_length, _crc.GetCurrentHashAsUInt32());
        GC.SuppressFinalize(this);
    }
}
