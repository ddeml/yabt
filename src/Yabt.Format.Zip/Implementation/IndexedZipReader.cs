using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Yabt.Format.Zip.Implementation;

/// <summary>
/// Enumerates central-directory records once without retaining them. Indexed
/// records can subsequently open independent streams without parsing the
/// directory again. Compression remains owned by the framework.
/// </summary>
internal static class IndexedZipReader
{
    private static readonly UTF8Encoding NameEncoding = new(false, true);

    public static IEnumerable<ZipEntryRecord> ReadEntries
    (
        Stream package,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(package);
        var directory = ReadDirectory(package);
        package.Position = directory.Offset;
        for (long index = 0; index < directory.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return ReadEntry(package, directory.Offset, directory.Length);
        }

        if (package.Position != directory.Offset + directory.Length)
        {
            throw new InvalidDataException("ZIP central-directory count or length is inconsistent.");
        }
    }

    public static Stream OpenEntry
    (
        Stream package,
        ZipEntryRecord entry,
        bool leaveOpen = false
    )
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(entry);
        try
        {
            var dataOffset = ValidateLocalHeader(package, entry);
            package.Position = dataOffset;
            var range = new ZipEntryRangeStream(package, entry.CompressedLength, leaveOpen);
            Stream content = entry.CompressionMethod switch
            {
                0 => range,
                8 => new DeflateStream(range, CompressionMode.Decompress),
                _ => throw new InvalidDataException(
                    $"ZIP entry '{entry.FullName}' uses unsupported compression method {entry.CompressionMethod}.")
            };
            return new ValidatedZipEntryReadStream(content, entry);
        }
        catch
        {
            if (!leaveOpen) { package.Dispose(); }
            throw;
        }
    }

    private static (long Offset, long Length, long Count) ReadDirectory(Stream package)
    {
        if (!package.CanRead || !package.CanSeek || package.Length < 22)
        {
            throw new InvalidDataException("ZIP package is not a readable seekable archive.");
        }

        var tail = new byte[(int)Math.Min(package.Length, ushort.MaxValue + 22L)];
        var tailOffset = package.Length - tail.Length;
        package.Position = tailOffset;
        package.ReadExactly(tail);
        for (var index = tail.Length - 22; index >= 0; index--)
        {
            var end = tail.AsSpan(index);
            if (BinaryPrimitives.ReadUInt32LittleEndian(end) != 0x06054b50 ||
                BinaryPrimitives.ReadUInt16LittleEndian(end[20..]) != end.Length - 22)
            {
                continue;
            }

            if (BinaryPrimitives.ReadUInt16LittleEndian(end[4..]) != 0 ||
                BinaryPrimitives.ReadUInt16LittleEndian(end[6..]) != 0 ||
                BinaryPrimitives.ReadUInt16LittleEndian(end[8..]) !=
                    BinaryPrimitives.ReadUInt16LittleEndian(end[10..]))
            {
                throw new InvalidDataException("Multipart ZIP archives are unsupported.");
            }

            long count = BinaryPrimitives.ReadUInt16LittleEndian(end[10..]);
            long length = BinaryPrimitives.ReadUInt32LittleEndian(end[12..]);
            long offset = BinaryPrimitives.ReadUInt32LittleEndian(end[16..]);
            var endOffset = tailOffset + index;
            if (count == ushort.MaxValue || length == uint.MaxValue || offset == uint.MaxValue)
            {
                return ReadZip64Directory(package, endOffset);
            }

            ValidateDirectoryBounds(offset, length, count, endOffset);
            return (offset, length, count);
        }

        throw new InvalidDataException("ZIP end-of-central-directory record was not found.");
    }

    private static (long Offset, long Length, long Count) ReadZip64Directory
    (
        Stream package,
        long endOffset
    )
    {
        if (endOffset < 20) { throw new InvalidDataException("ZIP64 locator is missing."); }
        package.Position = endOffset - 20;
        Span<byte> locator = stackalloc byte[20];
        package.ReadExactly(locator);
        if (BinaryPrimitives.ReadUInt32LittleEndian(locator) != 0x07064b50 ||
            BinaryPrimitives.ReadUInt32LittleEndian(locator[4..]) != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(locator[16..]) != 1)
        {
            throw new InvalidDataException("ZIP64 locator is invalid or refers to multiple disks.");
        }

        var zip64Offset = ReadLength(locator[8..]);
        if (zip64Offset > endOffset - 76)
        {
            throw new InvalidDataException("ZIP64 end record is outside the archive.");
        }

        package.Position = zip64Offset;
        Span<byte> end = stackalloc byte[56];
        package.ReadExactly(end);
        if (BinaryPrimitives.ReadUInt32LittleEndian(end) != 0x06064b50 ||
            ReadLength(end[4..]) < 44 ||
            ReadLength(end[4..]) > endOffset - 20 - zip64Offset - 12 ||
            BinaryPrimitives.ReadUInt32LittleEndian(end[16..]) != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(end[20..]) != 0 ||
            ReadLength(end[24..]) != ReadLength(end[32..]))
        {
            throw new InvalidDataException("ZIP64 end record is inconsistent.");
        }

        var count = ReadLength(end[32..]);
        var length = ReadLength(end[40..]);
        var offset = ReadLength(end[48..]);
        ValidateDirectoryBounds(offset, length, count, zip64Offset);
        return (offset, length, count);
    }

    private static void ValidateDirectoryBounds(long offset, long length, long count, long end)
    {
        if (offset > end || length > end - offset || count > length / 46)
        {
            throw new InvalidDataException("ZIP central directory lies outside the archive.");
        }
    }

    private static ZipEntryRecord ReadEntry(Stream package, long directoryOffset, long directoryLength)
    {
        var directoryEnd = directoryOffset + directoryLength;
        EnsureAvailable(package, 46, directoryEnd);
        Span<byte> header = stackalloc byte[46];
        package.ReadExactly(header);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x02014b50)
        {
            throw new InvalidDataException("ZIP central-directory entry signature is invalid.");
        }

        var flags = BinaryPrimitives.ReadUInt16LittleEndian(header[8..]);
        ValidateFlags(flags);
        var method = BinaryPrimitives.ReadUInt16LittleEndian(header[10..]);
        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header[28..]);
        var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header[30..]);
        var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(header[32..]);
        long disk = BinaryPrimitives.ReadUInt16LittleEndian(header[34..]);
        long compressedLength = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
        long length = BinaryPrimitives.ReadUInt32LittleEndian(header[24..]);
        long localOffset = BinaryPrimitives.ReadUInt32LittleEndian(header[42..]);
        EnsureAvailable(package, nameLength + extraLength + commentLength, directoryEnd);
        var name = new byte[nameLength];
        var extra = new byte[extraLength];
        package.ReadExactly(name);
        package.ReadExactly(extra);
        package.Position += commentLength;
        ReadZip64Extra(extra, ref length, ref compressedLength, ref localOffset, ref disk);
        if (disk != 0 || localOffset >= directoryOffset ||
            compressedLength > directoryOffset - localOffset)
        {
            throw new InvalidDataException("ZIP entry points outside its package payload.");
        }

        return new
        (
            NameEncoding.GetString(name),
            length,
            compressedLength,
            localOffset,
            directoryOffset,
            BinaryPrimitives.ReadUInt32LittleEndian(header[16..]),
            method,
            flags,
            BinaryPrimitives.ReadInt32LittleEndian(header[38..]),
            DecodeTimestamp(BinaryPrimitives.ReadUInt32LittleEndian(header[12..]))
        );
    }

    private static long ValidateLocalHeader(Stream package, ZipEntryRecord entry)
    {
        package.Position = entry.LocalHeaderOffset;
        EnsureAvailable(package, 30, entry.CentralDirectoryOffset);
        Span<byte> header = stackalloc byte[30];
        package.ReadExactly(header);
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(header[6..]);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x04034b50 ||
            flags != entry.Flags ||
            BinaryPrimitives.ReadUInt16LittleEndian(header[8..]) != entry.CompressionMethod)
        {
            throw new InvalidDataException($"ZIP entry '{entry.FullName}' local header differs from its directory record.");
        }

        ValidateFlags(flags);
        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header[26..]);
        var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header[28..]);
        EnsureAvailable(package, nameLength + extraLength, entry.CentralDirectoryOffset);
        var name = new byte[nameLength];
        var extra = new byte[extraLength];
        package.ReadExactly(name);
        package.ReadExactly(extra);
        if (!string.Equals(NameEncoding.GetString(name), entry.FullName, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"ZIP entry '{entry.FullName}' local name differs from its directory record.");
        }

        var dataOffset = package.Position;
        if (entry.CompressedLength > entry.CentralDirectoryOffset - dataOffset)
        {
            throw new InvalidDataException($"ZIP entry '{entry.FullName}' content extends beyond the payload region.");
        }

        long compressedLength = BinaryPrimitives.ReadUInt32LittleEndian(header[18..]);
        long length = BinaryPrimitives.ReadUInt32LittleEndian(header[22..]);
        long ignoredOffset = 0;
        long ignoredDisk = 0;
        if ((flags & 8) == 0)
        {
            ReadZip64Extra(extra, ref length, ref compressedLength, ref ignoredOffset, ref ignoredDisk);
            if (length != entry.Length || compressedLength != entry.CompressedLength ||
                BinaryPrimitives.ReadUInt32LittleEndian(header[14..]) != entry.Crc32)
            {
                throw new InvalidDataException($"ZIP entry '{entry.FullName}' local sizes or checksum differ from its directory record.");
            }
        }
        else
        {
            var zip64 = compressedLength == uint.MaxValue || length == uint.MaxValue ||
                entry.CompressedLength >= uint.MaxValue || entry.Length >= uint.MaxValue;
            ValidateDataDescriptor(package, entry, dataOffset + entry.CompressedLength, zip64);
        }

        return dataOffset;
    }

    private static void ValidateDataDescriptor
    (
        Stream package,
        ZipEntryRecord entry,
        long offset,
        bool zip64
    )
    {
        package.Position = offset;
        var minimumLength = zip64 ? 20 : 12;
        EnsureAvailable(package, minimumLength, entry.CentralDirectoryOffset);
        Span<byte> descriptor = stackalloc byte[24];
        package.ReadExactly(descriptor[..minimumLength]);
        if (DescriptorMatches(descriptor[..minimumLength], entry, zip64)) { return; }
        if (BinaryPrimitives.ReadUInt32LittleEndian(descriptor) == 0x08074b50)
        {
            EnsureAvailable(package, 4, entry.CentralDirectoryOffset);
            package.ReadExactly(descriptor.Slice(minimumLength, 4));
            if (DescriptorMatches(descriptor.Slice(4, minimumLength), entry, zip64)) { return; }
        }

        throw new InvalidDataException($"ZIP entry '{entry.FullName}' data descriptor is inconsistent.");
    }

    private static bool DescriptorMatches(ReadOnlySpan<byte> descriptor, ZipEntryRecord entry, bool zip64)
    {
        if (BinaryPrimitives.ReadUInt32LittleEndian(descriptor) != entry.Crc32) { return false; }
        return zip64
            ? BinaryPrimitives.ReadUInt64LittleEndian(descriptor[4..]) == (ulong)entry.CompressedLength &&
                BinaryPrimitives.ReadUInt64LittleEndian(descriptor[12..]) == (ulong)entry.Length
            : BinaryPrimitives.ReadUInt32LittleEndian(descriptor[4..]) == entry.CompressedLength &&
                BinaryPrimitives.ReadUInt32LittleEndian(descriptor[8..]) == entry.Length;
    }

    private static void ReadZip64Extra
    (
        ReadOnlySpan<byte> extra,
        ref long length,
        ref long compressedLength,
        ref long localOffset,
        ref long disk
    )
    {
        var found = false;
        while (!extra.IsEmpty)
        {
            if (extra.Length < 4) { throw new InvalidDataException("ZIP extra field is truncated."); }
            var id = BinaryPrimitives.ReadUInt16LittleEndian(extra);
            var size = BinaryPrimitives.ReadUInt16LittleEndian(extra[2..]);
            extra = extra[4..];
            if (size > extra.Length) { throw new InvalidDataException("ZIP extra field is truncated."); }
            if (id == 1)
            {
                if (found) { throw new InvalidDataException("ZIP entry has duplicate ZIP64 extra fields."); }
                found = true;
                var values = extra[..size];
                if (length == uint.MaxValue) { length = TakeLength(ref values); }
                if (compressedLength == uint.MaxValue) { compressedLength = TakeLength(ref values); }
                if (localOffset == uint.MaxValue) { localOffset = TakeLength(ref values); }
                if (disk == ushort.MaxValue)
                {
                    if (values.Length < 4) { throw new InvalidDataException("ZIP64 disk number is missing."); }
                    disk = BinaryPrimitives.ReadUInt32LittleEndian(values);
                }
            }

            extra = extra[size..];
        }

        if (!found && (length == uint.MaxValue || compressedLength == uint.MaxValue ||
                localOffset == uint.MaxValue || disk == ushort.MaxValue))
        {
            throw new InvalidDataException("Required ZIP64 extra field is missing.");
        }
    }

    private static long TakeLength(ref ReadOnlySpan<byte> values)
    {
        if (values.Length < 8) { throw new InvalidDataException("ZIP64 size or offset is missing."); }
        var value = ReadLength(values);
        values = values[8..];
        return value;
    }

    private static long ReadLength(ReadOnlySpan<byte> value)
    {
        var result = BinaryPrimitives.ReadUInt64LittleEndian(value);
        return result <= long.MaxValue ? (long)result :
            throw new InvalidDataException("ZIP size or offset exceeds the supported stream range.");
    }

    private static void ValidateFlags(ushort flags)
    {
        if ((flags & 0x2041) != 0)
        {
            throw new InvalidDataException("Encrypted or masked ZIP entry headers are unsupported.");
        }
    }

    private static void EnsureAvailable(Stream source, long count, long end)
    {
        if (source.Position < 0 || source.Position > end || count > end - source.Position)
        {
            throw new InvalidDataException("ZIP header or content is truncated.");
        }
    }

    private static DateTimeOffset DecodeTimestamp(uint timestamp)
    {
        var year = 1980 + (int)(timestamp >> 25);
        var month = (int)((timestamp >> 21) & 15);
        var day = (int)((timestamp >> 16) & 31);
        var hour = (int)((timestamp >> 11) & 31);
        var minute = (int)((timestamp >> 5) & 63);
        var second = (int)(timestamp & 31) * 2;
        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) ||
            hour > 23 || minute > 59 || second > 59)
        {
            // Match ZipArchive's handling of malformed DOS timestamps. Exact
            // restore timestamps come from the validated package manifest.
            return new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        }

        return new(year, month, day, hour, minute, second, TimeSpan.Zero);
    }
}
