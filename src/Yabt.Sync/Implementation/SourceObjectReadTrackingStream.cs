namespace Yabt.Sync.Implementation;

internal sealed class SourceObjectReadTrackingStream
(
    Stream _innerStream,
    string _sourcePath,
    string _objectKey
) : Stream
{
    public override bool CanRead => _innerStream.CanRead;

    public override bool CanSeek => _innerStream.CanSeek;

    public override bool CanTimeout => _innerStream.CanTimeout;

    public override bool CanWrite => _innerStream.CanWrite;

    public override long Length => _innerStream.Length;

    public override long Position
    {
        get => _innerStream.Position;
        set => _innerStream.Position = value;
    }

    public override int ReadTimeout
    {
        get => _innerStream.ReadTimeout;
        set => _innerStream.ReadTimeout = value;
    }

    public override int WriteTimeout
    {
        get => _innerStream.WriteTimeout;
        set => _innerStream.WriteTimeout = value;
    }

    public override void Flush() => _innerStream.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        _innerStream.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count)
    {
        try
        {
            return _innerStream.Read(buffer, offset, count);
        }
        catch (Exception exception) when (ShouldMarkReadFailure(exception))
        {
            throw MarkReadFailure(exception);
        }
    }

    public override int Read(Span<byte> buffer)
    {
        try
        {
            return _innerStream.Read(buffer);
        }
        catch (Exception exception) when (ShouldMarkReadFailure(exception))
        {
            throw MarkReadFailure(exception);
        }
    }

    public override int ReadByte()
    {
        try
        {
            return _innerStream.ReadByte();
        }
        catch (Exception exception) when (ShouldMarkReadFailure(exception))
        {
            throw MarkReadFailure(exception);
        }
    }

    public override async Task<int> ReadAsync
    (
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return await _innerStream.ReadAsync(
                buffer,
                offset,
                count,
                cancellationToken);
        }
        catch (Exception exception) when (ShouldMarkReadFailure(exception))
        {
            throw MarkReadFailure(exception);
        }
    }

    public override async ValueTask<int> ReadAsync
    (
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            return await _innerStream.ReadAsync(buffer, cancellationToken);
        }
        catch (Exception exception) when (ShouldMarkReadFailure(exception))
        {
            throw MarkReadFailure(exception);
        }
    }

    public override long Seek(long offset, SeekOrigin origin) =>
        _innerStream.Seek(offset, origin);

    public override void SetLength(long value) => _innerStream.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count) =>
        _innerStream.Write(buffer, offset, count);

    public override void Write(ReadOnlySpan<byte> buffer) => _innerStream.Write(buffer);

    public override Task WriteAsync
    (
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    ) => _innerStream.WriteAsync(buffer, offset, count, cancellationToken);

    public override ValueTask WriteAsync
    (
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default
    ) => _innerStream.WriteAsync(buffer, cancellationToken);

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

    private SourceObjectReadException MarkReadFailure(Exception exception)
    {
        return new SourceObjectReadException(
            _sourcePath,
            _objectKey,
            exception);
    }

    private static bool ShouldMarkReadFailure(Exception exception) =>
        exception is not OperationCanceledException &&
        SourceObjectReadException.FindAll(exception).Count == 0;
}
