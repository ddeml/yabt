using System.IO.Compression;

namespace Yabt.Format.Zip.Implementation;

internal sealed class ZipArchiveEntryReadStream
(
    Stream _entryContent,
    ZipArchive _archive,
    Stream _packageContent
) : Stream
{
    private int _disposed;

    public override bool CanRead => _entryContent.CanRead;

    public override bool CanSeek => _entryContent.CanSeek;

    public override bool CanWrite => false;

    public override long Length => _entryContent.Length;

    public override long Position
    {
        get => _entryContent.Position;
        set => _entryContent.Position = value;
    }

    public override void Flush()
    {
        _entryContent.Flush();
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        return _entryContent.FlushAsync(cancellationToken);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return _entryContent.Read(buffer, offset, count);
    }

    public override ValueTask<int> ReadAsync
    (
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        return _entryContent.ReadAsync(buffer, cancellationToken);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        return _entryContent.Seek(offset, origin);
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    protected override void Dispose(bool disposing)
    {
        try
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                try
                {
                    _entryContent.Dispose();
                }
                finally
                {
                    try
                    {
                        _archive.Dispose();
                    }
                    finally
                    {
                        _packageContent.Dispose();
                    }
                }
            }
        }
        finally
        {
            base.Dispose(disposing);
        }
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }

        try
        {
            try
            {
                await _entryContent.DisposeAsync();
            }
            finally
            {
                try
                {
                    _archive.Dispose();
                }
                finally
                {
                    await _packageContent.DisposeAsync();
                }
            }
        }
        finally
        {
            GC.SuppressFinalize(this);
        }
    }
}
