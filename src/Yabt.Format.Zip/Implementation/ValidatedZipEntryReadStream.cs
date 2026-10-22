using System.IO.Hashing;

namespace Yabt.Format.Zip.Implementation;

internal sealed class ValidatedZipEntryReadStream(Stream _content, ZipEntryRecord _entry) : Stream
{
    private readonly Crc32 _crc = new();
    private long _position;
    private bool _disposed;

    public override bool CanRead => !_disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _entry.Length;
    public override long Position { get => _position; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var count = _content.Read(buffer);
        if (!buffer.IsEmpty) { ValidateRead(buffer[..count]); }
        return count;
    }

    public override async ValueTask<int> ReadAsync
    (
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var count = await _content.ReadAsync(buffer, cancellationToken);
        if (!buffer.IsEmpty) { ValidateRead(buffer.Span[..count]); }
        return count;
    }

    public override Task<int> ReadAsync
    (
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    ) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private void ValidateRead(ReadOnlySpan<byte> data)
    {
        _position = checked(_position + data.Length);
        _crc.Append(data);
        if (_position > _entry.Length || data.IsEmpty &&
                (_position != _entry.Length || _crc.GetCurrentHashAsUInt32() != _entry.Crc32))
        {
            throw new InvalidDataException($"ZIP entry '{_entry.FullName}' content length or CRC32 is invalid.");
        }
    }

    public override void Flush() { }
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _content.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed) { return; }
        _disposed = true;
        await _content.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
