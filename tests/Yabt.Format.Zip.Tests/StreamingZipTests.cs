using System.IO.Compression;
using System.Text;
using Yabt.Format.Zip.Implementation;

namespace Yabt.Format.Zip.Tests;

[TestClass]
public sealed class StreamingZipTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 10, 22, 12, 34, 56, TimeSpan.Zero);

    [TestMethod]
    [DataRow(CompressionLevel.NoCompression)]
    [DataRow(CompressionLevel.Fastest)]
    [DataRow(CompressionLevel.Optimal)]
    [DataRow(CompressionLevel.SmallestSize)]
    public async Task WriterProducesStandardZip64ReadableByFramework(CompressionLevel compression)
    {
        using var package = new MemoryStream();
        using var directory = new MemoryStream();
        await using (var writer = new StreamingZipWriter(package, directory))
        {
            await using (var content = writer.CreateEntry("folder/Ã¤æ—¥æœ¬.txt", compression, Timestamp))
            {
                await content.WriteAsync(Encoding.UTF8.GetBytes("Hello ZIP, Ã¤æ—¥æœ¬!"));
            }

            await using (writer.CreateEntry("empty", compression, Timestamp)) { }
            await writer.CompleteAsync();
        }

        package.Position = 0;
        using var archive = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);
        Assert.HasCount(2, archive.Entries);
        var entry = archive.GetEntry("folder/Ã¤æ—¥æœ¬.txt")!;
        Assert.AreEqual(Timestamp.DateTime, entry.LastWriteTime.DateTime);
        using var reader = new StreamReader(entry.Open());
        Assert.AreEqual("Hello ZIP, Ã¤æ—¥æœ¬!", await reader.ReadToEndAsync());
        Assert.AreEqual(0, archive.GetEntry("empty")!.Length);

        var records = IndexedZipReader.ReadEntries(package).ToArray();
        foreach (var record in records)
        {
            await using var content = IndexedZipReader.OpenEntry(package, record, leaveOpen: true);
            await content.CopyToAsync(Stream.Null);
        }
    }

    [TestMethod]
    [DataRow(false, CompressionLevel.NoCompression)]
    [DataRow(false, CompressionLevel.Optimal)]
    [DataRow(true, CompressionLevel.NoCompression)]
    [DataRow(true, CompressionLevel.Optimal)]
    public async Task ReaderAcceptsFrameworkEntriesAndDataDescriptors(bool nonSeekable, CompressionLevel compression)
    {
        using var package = new MemoryStream();
        using (var archive = new ZipArchive(
            nonSeekable ? new NonSeekingWriteStream(package) : package,
            ZipArchiveMode.Create,
            leaveOpen: true))
        {
            var entry = archive.CreateEntry("folder/Ã¤æ—¥æœ¬.txt", compression);
            entry.LastWriteTime = Timestamp;
            await using var content = entry.Open();
            await content.WriteAsync(Encoding.UTF8.GetBytes("Hello ZIP!"));
        }

        var record = IndexedZipReader.ReadEntries(package).Single();
        Assert.AreEqual("folder/Ã¤æ—¥æœ¬.txt", record.FullName);
        Assert.AreEqual(Timestamp, record.LastWriteTime);
        await using var restored = IndexedZipReader.OpenEntry(package, record, leaveOpen: true);
        using var reader = new StreamReader(restored);
        Assert.AreEqual("Hello ZIP!", await reader.ReadToEndAsync());
    }

    [TestMethod]
    public async Task ReaderRejectsCorruptedStoredPayload()
    {
        using var package = await CreatePackageAsync();
        var record = IndexedZipReader.ReadEntries(package).Single();
        package.Position = 30 + Encoding.UTF8.GetByteCount(record.FullName) + 20;
        package.WriteByte(0xff);
        await using var restored = IndexedZipReader.OpenEntry(package, record, leaveOpen: true);
        await Assert.ThrowsAsync<InvalidDataException>(() => restored.CopyToAsync(Stream.Null));
    }

    [TestMethod]
    public async Task ReaderRejectsLocalNameMismatch()
    {
        using var package = await CreatePackageAsync();
        var record = IndexedZipReader.ReadEntries(package).Single();
        package.Position = 30;
        package.WriteByte((byte)'X');
        Assert.Throws<InvalidDataException>(() => IndexedZipReader.OpenEntry(package, record, leaveOpen: true));
    }

    [TestMethod]
    public async Task ReaderRejectsMissingZip64SizeEvidence()
    {
        using var package = await CreatePackageAsync();
        var record = IndexedZipReader.ReadEntries(package).Single();
        package.Position = record.CentralDirectoryOffset + 46 + Encoding.UTF8.GetByteCount(record.FullName);
        package.WriteByte(2);
        Assert.Throws<InvalidDataException>(() => IndexedZipReader.ReadEntries(package).ToArray());
    }

    [TestMethod]
    public async Task ReaderRejectsTruncatedDirectoryAndEncryption()
    {
        using var package = await CreatePackageAsync();
        var record = IndexedZipReader.ReadEntries(package).Single();
        package.Position = record.CentralDirectoryOffset + 8;
        package.WriteByte(1);
        Assert.Throws<InvalidDataException>(() => IndexedZipReader.ReadEntries(package).ToArray());
        package.SetLength(package.Length - 1);
        Assert.Throws<InvalidDataException>(() => IndexedZipReader.ReadEntries(package).ToArray());
    }

    [TestMethod]
    public async Task IndexedEntryReadsNeverReadCentralDirectoryAgain()
    {
        using var package = await CreatePackageAsync();
        var record = IndexedZipReader.ReadEntries(package).Single();
        using var guarded = new PayloadOnlyReadStream(package, record.CentralDirectoryOffset);
        for (var index = 0; index < 3; index++)
        {
            await using var restored = IndexedZipReader.OpenEntry(guarded, record, leaveOpen: true);
            await restored.CopyToAsync(Stream.Null);
        }
    }

    [TestMethod]
    public async Task WriterAndReaderSupportMoreThan65535Entries()
    {
        using var package = new MemoryStream();
        using var directory = new MemoryStream();
        await using (var writer = new StreamingZipWriter(package, directory))
        {
            for (var index = 0; index < 65536; index++)
            {
                await using (writer.CreateEntry($"entry-{index}", CompressionLevel.NoCompression, Timestamp)) { }
            }

            await writer.CompleteAsync();
        }

        Assert.AreEqual(65536, IndexedZipReader.ReadEntries(package).Count());
        package.Position = 0;
        using var framework = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);
        Assert.HasCount(65536, framework.Entries);
    }

    [TestMethod]
    public async Task WriterAndReaderSupportEntryOffsetsBeyondFourGiB()
    {
        using var package = new SparseTestStream { Position = (long)uint.MaxValue + 100 };
        using var directory = new MemoryStream();
        await using (var writer = new StreamingZipWriter(package, directory))
        {
            await using (var entry = writer.CreateEntry("large-offset", CompressionLevel.Optimal, Timestamp))
            {
                await entry.WriteAsync("content"u8.ToArray());
            }

            await writer.CompleteAsync();
        }

        var record = IndexedZipReader.ReadEntries(package).Single();
        Assert.IsGreaterThan((long)uint.MaxValue, record.LocalHeaderOffset);
        await using var restored = IndexedZipReader.OpenEntry(package, record, leaveOpen: true);
        using var reader = new StreamReader(restored);
        Assert.AreEqual("content", await reader.ReadToEndAsync());
        package.Position = 0;
        using var framework = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);
        using var frameworkReader = new StreamReader(framework.GetEntry("large-offset")!.Open());
        Assert.AreEqual("content", await frameworkReader.ReadToEndAsync());
    }

    [TestMethod]
    public async Task WriterAndReaderStreamEntryLargerThanFourGiB()
    {
        const long payloadLength = (long)uint.MaxValue + 12_345;
        var buffer = new byte[256 * 1024];
        using var package = new MemoryStream();
        using var directory = new MemoryStream();
        await using (var writer = new StreamingZipWriter(package, directory))
        {
            await using (var entry = writer.CreateEntry("large-payload", CompressionLevel.Fastest, Timestamp))
            {
                long written = 0;
                while (written < payloadLength)
                {
                    var count = (int)Math.Min(buffer.Length, payloadLength - written);
                    await entry.WriteAsync(buffer.AsMemory(0, count));
                    written += count;
                }
            }
            await writer.CompleteAsync();
        }

        var record = IndexedZipReader.ReadEntries(package).Single();
        Assert.AreEqual(payloadLength, record.Length);
        await using (var restored = IndexedZipReader.OpenEntry(package, record, leaveOpen: true))
        {
            long length = 0;
            int count;
            while ((count = await restored.ReadAsync(buffer)) != 0)
            {
                Assert.IsTrue(buffer.AsSpan(0, count).IndexOfAnyExcept((byte)0) < 0);
                length += count;
            }
            Assert.AreEqual(payloadLength, length);
        }

        package.Position = 0;
        using var framework = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);
        Assert.AreEqual(payloadLength, framework.GetEntry("large-payload")!.Length);
    }

    [TestMethod]
    [DataRow(CompressionLevel.NoCompression)]
    [DataRow(CompressionLevel.Optimal)]
    public async Task DisposedEntryCannotReadEvenWhenUnderlyingPackageIsRetained(CompressionLevel compression)
    {
        using var package = new MemoryStream();
        using var directory = new MemoryStream();
        await using (var writer = new StreamingZipWriter(package, directory))
        {
            await using (var entry = writer.CreateEntry("file", compression, Timestamp))
            {
                await entry.WriteAsync("payload"u8.ToArray());
            }
            await writer.CompleteAsync();
        }

        var record = IndexedZipReader.ReadEntries(package).Single();
        var restored = IndexedZipReader.OpenEntry(package, record, leaveOpen: true);
        await restored.DisposeAsync();
        await restored.DisposeAsync();
        Assert.IsFalse(restored.CanRead);
        Assert.IsTrue(package.CanRead);
        Assert.Throws<ObjectDisposedException>(() => restored.ReadByte());
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await restored.ReadExactlyAsync(new byte[1]));
    }

    [TestMethod]
    public async Task ReaderChecksCancellationDuringDirectoryTraversal()
    {
        using var package = await CreatePackageAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            IndexedZipReader.ReadEntries(package, cancellation.Token).ToArray());
    }

    private static async Task<MemoryStream> CreatePackageAsync()
    {
        var package = new MemoryStream();
        using var directory = new MemoryStream();
        await using var writer = new StreamingZipWriter(package, directory);
        await using (var entry = writer.CreateEntry("file.txt", CompressionLevel.NoCompression, Timestamp))
        {
            await entry.WriteAsync("test-content"u8.ToArray());
        }

        await writer.CompleteAsync();
        return package;
    }

    private sealed class NonSeekingWriteStream(Stream destination) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => destination.Flush();
        public override void Write(byte[] buffer, int offset, int count) => destination.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => destination.Write(buffer);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class PayloadOnlyReadStream(Stream source, long end) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => source.Length;
        public override long Position { get => source.Position; set => source.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            Assert.IsLessThanOrEqualTo(end, source.Position + buffer.Length);
            return source.Read(buffer);
        }

        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => source.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class SparseTestStream : Stream
    {
        private readonly List<(long Offset, byte[] Bytes)> _writes = [];
        private long _length;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get; set; }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var count = (int)Math.Min(buffer.Length, Length - Position);
            var result = buffer[..count];
            result.Clear();
            foreach (var write in _writes)
            {
                var first = Math.Max(write.Offset, Position);
                var last = Math.Min(write.Offset + write.Bytes.Length, Position + count);
                if (first < last)
                {
                    write.Bytes.AsSpan((int)(first - write.Offset), (int)(last - first)).CopyTo(result[(int)(first - Position)..]);
                }
            }

            Position += count;
            return count;
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _writes.Add((Position, buffer.ToArray()));
            Position += buffer.Length;
            _length = Math.Max(_length, Position);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        public override void SetLength(long value) => _length = value;
    }
}
