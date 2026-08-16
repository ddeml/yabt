using System.IO.Hashing;
using Yabt.Core.Models;

namespace Yabt.Sync.Implementation;

internal sealed class ContentHashingReadStream(Stream _innerStream) : Stream
{
    private readonly XxHash128 _hash = new();
    private bool _hashCompleted;

    public long BytesRead { get; private set; }

    public bool EndOfStreamReached { get; private set; }

    public override bool CanRead => _innerStream.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public string CompleteHash()
    {
        ObjectDisposedException.ThrowIf(_hashCompleted, this);
        if (!EndOfStreamReached)
        {
            throw new InvalidOperationException(
                "The content hash cannot be completed before the source stream reaches its end.");
        }

        _hashCompleted = true;

        return ArchiveHash.Format(_hash.GetHashAndReset());
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var bytesRead = _innerStream.Read(buffer, offset, count);
        RecordRead(buffer.AsSpan(offset, bytesRead));
        return bytesRead;
    }

    public override int Read(Span<byte> buffer)
    {
        var bytesRead = _innerStream.Read(buffer);
        RecordRead(buffer[..bytesRead]);
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
            buffer.AsMemory(offset, count),
            cancellationToken);
        RecordRead(buffer.AsSpan(offset, bytesRead));
        return bytesRead;
    }

    public override async ValueTask<int> ReadAsync
    (
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        var bytesRead = await _innerStream.ReadAsync(buffer, cancellationToken);
        RecordRead(buffer.Span[..bytesRead]);
        return bytesRead;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hash.Reset();
        }

        base.Dispose(disposing);
    }

    private void RecordRead(ReadOnlySpan<byte> bytes)
    {
        if (_hashCompleted)
        {
            throw new InvalidOperationException("The content hash has already been completed.");
        }

        _hash.Append(bytes);
        BytesRead += bytes.Length;
        EndOfStreamReached = bytes.Length == 0;
    }
}
