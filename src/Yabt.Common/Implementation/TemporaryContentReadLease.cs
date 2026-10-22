namespace Yabt.Common.Implementation;

internal sealed class TemporaryContentReadLease
(
    Stream _reader,
    SpillableTemporaryContent owner
) : Stream
{
    private SpillableTemporaryContent? _owner = owner;

    public override bool CanRead => _reader.CanRead;

    public override bool CanSeek => _reader.CanSeek;

    public override bool CanWrite => false;

    public override long Length => _reader.Length;

    public override long Position
    {
        get => _reader.Position;
        set => _reader.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count) => _reader.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) => _reader.Read(buffer);

    public override Task<int> ReadAsync
    (
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken = default
    ) => _reader.ReadAsync(buffer, offset, count, cancellationToken);

    public override ValueTask<int> ReadAsync
    (
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    ) => _reader.ReadAsync(buffer, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => _reader.Seek(offset, origin);

    public override void Flush() => _reader.Flush();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is not null)
            {
                try { _reader.Dispose(); }
                finally { owner.ReleaseReader(); }
            }
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        var owner = Interlocked.Exchange(ref _owner, null);
        if (owner is not null)
        {
            try { await _reader.DisposeAsync(); }
            finally { await owner.ReleaseReaderAsync(); }
        }
        GC.SuppressFinalize(this);
    }
}
