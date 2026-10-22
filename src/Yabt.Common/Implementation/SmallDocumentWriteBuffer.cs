namespace Yabt.Common.Implementation;

internal sealed class SmallDocumentWriteBuffer(long _maximumBytes, string _documentName) : MemoryStream
{
    public override void Write(byte[] buffer, int offset, int count)
    {
        EnsureFits(count);
        base.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        EnsureFits(buffer.Length);
        base.Write(buffer);
    }

    public override void WriteByte(byte value)
    {
        EnsureFits(1);
        base.WriteByte(value);
    }

    public override Task WriteAsync
    (
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken = default
    )
    {
        EnsureFits(count);
        return base.WriteAsync(buffer, offset, count, cancellationToken);
    }

    public override ValueTask WriteAsync
    (
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        EnsureFits(buffer.Length);
        return base.WriteAsync(buffer, cancellationToken);
    }

    public override void SetLength(long value)
    {
        if (value > _maximumBytes) { ThrowExceeded(); }
        base.SetLength(value);
    }

    private void EnsureFits(int count)
    {
        if (Position > _maximumBytes - count) { ThrowExceeded(); }
    }

    private void ThrowExceeded() => throw new InvalidDataException
    (
        $"Document '{_documentName}' exceeds its maximum size of {_maximumBytes} bytes."
    );
}
