using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Yabt.Format.Zip.Implementation;

/// <summary>
/// Writes standard ZIP64 archives with only the active entry in memory. The
/// caller supplies seekable payload and central-directory staging streams.
/// </summary>
internal sealed class StreamingZipWriter : IDisposable, IAsyncDisposable
{
    private readonly Stream _destination;
    private readonly Stream _centralDirectory;
    private StreamingZipEntryWriteStream? _activeEntry;
    private long _entryCount;
    private bool _completed;
    private bool _disposed;

    public StreamingZipWriter(Stream destination, Stream centralDirectory)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(centralDirectory);
        if (!destination.CanWrite || !destination.CanSeek ||
            !centralDirectory.CanWrite || !centralDirectory.CanRead ||
            !centralDirectory.CanSeek)
        {
            throw new ArgumentException("ZIP staging streams must be seekable; the directory must also be readable.");
        }

        _destination = destination;
        _centralDirectory = centralDirectory;
    }

    public Stream CreateEntry
    (
        string fullName,
        CompressionLevel compressionLevel,
        DateTimeOffset lastWriteTime,
        int externalAttributes = 0
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed || _activeEntry is not null)
        {
            throw new InvalidOperationException("A ZIP entry must be closed before another entry or completion.");
        }

        ArgumentException.ThrowIfNullOrEmpty(fullName);
        var name = new UTF8Encoding(false, true).GetBytes(fullName);
        if (name.Length > ushort.MaxValue)
        {
            throw new ArgumentException("The UTF-8 ZIP entry name exceeds 65535 bytes.", nameof(fullName));
        }

        var timestamp = EncodeTimestamp(lastWriteTime);
        var method = compressionLevel == CompressionLevel.NoCompression ? (ushort)0 : (ushort)8;
        var headerOffset = _destination.Position;
        Span<byte> header = stackalloc byte[30];
        header.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x04034b50);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], 45);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], 0x0800);
        BinaryPrimitives.WriteUInt16LittleEndian(header[8..], method);
        BinaryPrimitives.WriteUInt32LittleEndian(header[10..], timestamp);
        BinaryPrimitives.WriteUInt32LittleEndian(header[18..], uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(header[22..], uint.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(header[26..], checked((ushort)name.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(header[28..], 20);
        _destination.Write(header);
        _destination.Write(name);
        Span<byte> extra = stackalloc byte[20];
        extra.Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(extra, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(extra[2..], 16);
        _destination.Write(extra);
        var dataOffset = _destination.Position;
        Stream compressed = method == 0
            ? _destination
            : new DeflateStream(_destination, compressionLevel, leaveOpen: true);
        _activeEntry = new
        (
            compressed,
            leaveOpen: method == 0,
            (length, crc) => FinishEntry(
                name, headerOffset, dataOffset, method, timestamp, externalAttributes, length, crc)
        );
        return _activeEntry;
    }

    public async Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed) { return; }
        if (_activeEntry is not null)
        {
            throw new InvalidOperationException("The active ZIP entry must be closed before completion.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var directoryOffset = _destination.Position;
        _centralDirectory.Position = 0;
        await _centralDirectory.CopyToAsync(_destination, cancellationToken);
        var directoryLength = _destination.Position - directoryOffset;
        WriteEndRecords(directoryOffset, directoryLength);
        await _destination.FlushAsync(cancellationToken);
        _completed = true;
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        _activeEntry?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) { return; }
        _disposed = true;
        if (_activeEntry is not null) { await _activeEntry.DisposeAsync(); }
    }

    private void FinishEntry
    (
        byte[] name,
        long headerOffset,
        long dataOffset,
        ushort method,
        uint timestamp,
        int externalAttributes,
        long length,
        uint crc
    )
    {
        var end = _destination.Position;
        var compressedLength = end - dataOffset;
        Span<byte> value = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(value, crc);
        _destination.Position = headerOffset + 14;
        _destination.Write(value[..4]);
        BinaryPrimitives.WriteInt64LittleEndian(value, length);
        BinaryPrimitives.WriteInt64LittleEndian(value[8..], compressedLength);
        _destination.Position = headerOffset + 30 + name.Length + 4;
        _destination.Write(value);
        _destination.Position = end;

        Span<byte> header = stackalloc byte[46];
        header.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x02014b50);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], 45);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], 45);
        BinaryPrimitives.WriteUInt16LittleEndian(header[8..], 0x0800);
        BinaryPrimitives.WriteUInt16LittleEndian(header[10..], method);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], timestamp);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], crc);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], uint.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(header[28..], checked((ushort)name.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(header[30..], 28);
        BinaryPrimitives.WriteInt32LittleEndian(header[38..], externalAttributes);
        BinaryPrimitives.WriteUInt32LittleEndian(header[42..], uint.MaxValue);
        _centralDirectory.Write(header);
        _centralDirectory.Write(name);
        Span<byte> extra = stackalloc byte[28];
        BinaryPrimitives.WriteUInt16LittleEndian(extra, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(extra[2..], 24);
        BinaryPrimitives.WriteInt64LittleEndian(extra[4..], length);
        BinaryPrimitives.WriteInt64LittleEndian(extra[12..], compressedLength);
        BinaryPrimitives.WriteInt64LittleEndian(extra[20..], headerOffset);
        _centralDirectory.Write(extra);
        _entryCount = checked(_entryCount + 1);
        _activeEntry = null;
    }

    private void WriteEndRecords(long directoryOffset, long directoryLength)
    {
        var zip64Offset = _destination.Position;
        Span<byte> end = stackalloc byte[98];
        end.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(end, 0x06064b50);
        BinaryPrimitives.WriteInt64LittleEndian(end[4..], 44);
        BinaryPrimitives.WriteUInt16LittleEndian(end[12..], 45);
        BinaryPrimitives.WriteUInt16LittleEndian(end[14..], 45);
        BinaryPrimitives.WriteInt64LittleEndian(end[24..], _entryCount);
        BinaryPrimitives.WriteInt64LittleEndian(end[32..], _entryCount);
        BinaryPrimitives.WriteInt64LittleEndian(end[40..], directoryLength);
        BinaryPrimitives.WriteInt64LittleEndian(end[48..], directoryOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(end[56..], 0x07064b50);
        BinaryPrimitives.WriteInt64LittleEndian(end[64..], zip64Offset);
        BinaryPrimitives.WriteUInt32LittleEndian(end[72..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(end[76..], 0x06054b50);
        BinaryPrimitives.WriteUInt16LittleEndian(end[84..], (ushort)Math.Min(_entryCount, ushort.MaxValue));
        BinaryPrimitives.WriteUInt16LittleEndian(end[86..], (ushort)Math.Min(_entryCount, ushort.MaxValue));
        BinaryPrimitives.WriteUInt32LittleEndian(end[88..], (uint)Math.Min(directoryLength, uint.MaxValue));
        BinaryPrimitives.WriteUInt32LittleEndian(end[92..], (uint)Math.Min(directoryOffset, uint.MaxValue));
        _destination.Write(end);
    }

    private static uint EncodeTimestamp(DateTimeOffset value)
    {
        if (value.Year is < 1980 or > 2107)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "ZIP timestamps must be in the DOS date range.");
        }

        return (uint)((value.Year - 1980) << 25 | value.Month << 21 | value.Day << 16 |
            value.Hour << 11 | value.Minute << 5 | value.Second / 2);
    }
}
