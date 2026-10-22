using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Yabt.Common;
using Yabt.Core.Models;

namespace Yabt.Metadata.Tests;

[TestClass]
public sealed class StreamedManifestTests
{
    private string _temporaryDirectory = null!;
    private static readonly DateTimeOffset Timestamp = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private static readonly string ContentHash = ArchiveHash.Compute("payload"u8);

    [TestInitialize]
    public void Initialize()
    {
        _temporaryDirectory = Path.Combine(Path.GetTempPath(), $"yabt-manifest-stream-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_temporaryDirectory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        Assert.IsEmpty(Directory.EnumerateFileSystemEntries(_temporaryDirectory));
        Directory.Delete(_temporaryDirectory);
    }

    [TestMethod]
    public async Task PackageManifestLargerThanSixteenMiBUsesReplayableDiskEntriesAndPreservesCanonicalBytes()
    {
        const int entryCount = 32768;
        using var provider = CreateServices();
        var serializer = provider.GetRequiredService<IManifestSerializer>();
        var buffers = provider.GetRequiredService<ITemporaryContentFactory>();
        using var operation = TemporaryIndex.BeginOperation(Options);
        using var original = serializer.Create
        (
            string.Empty,
            Timestamp,
            "zip",
            1,
            ContentHash,
            "large.zip",
            new("zip"),
            GeneratePackageEntries(entryCount)
        );
        await using var serialized = buffers.Create();
        await serializer.WriteAsync(original, serialized);
        await serialized.SealAsync();
        Assert.IsGreaterThan(16L * 1024 * 1024, serialized.Length);
        Assert.IsTrue(serialized.IsFileBacked);
        Assert.AreEqual(0L, buffers.ReservedMemoryBytes);

        await using var source = serialized.OpenRead();
        using var restored = await serializer.ReadAsync(source);
        Assert.AreEqual(original.ManifestHash, restored.ManifestHash);
        Assert.AreEqual(entryCount, restored.Entries.Count());
        Assert.AreEqual("00000000", restored.Entries.First().StoredPath[..8]);
        Assert.AreEqual($"{entryCount - 1:D8}", restored.Entries.Last().StoredPath[..8]);
        await using var rewritten = buffers.Create();
        await serializer.WriteAsync(restored, rewritten);
        await rewritten.SealAsync();
        await using var first = serialized.OpenRead();
        await using var second = rewritten.OpenRead();
        await AssertEqualStreamsAsync(first, second);
        Assert.HasCount(1, Directory.EnumerateFiles(_temporaryDirectory, "*.sqlite"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CreatedManifestOwnsEntriesIndependentlyOfItsSource(bool sharedOperation)
    {
        using var provider = CreateServices();
        var serializer = provider.GetRequiredService<IChangeManifestSerializer>();
        using var operation = sharedOperation ? TemporaryIndex.BeginOperation(Options) : null;
        ArchiveChangeManifest copy;
        string expectedHash;
        using (var original = serializer.Create(GenerateChangeEntries(3), "mirror"))
        {
            expectedHash = original.ManifestHash;
            copy = serializer.Create(original.Entries, "mirror");
            Assert.AreNotSame(original.Entries, copy.Entries);
        }

        using (copy)
        {
            Assert.AreEqual(3, copy.Entries.Count());
            Assert.AreEqual("00000000.txt", copy.Entries.First().RelativePath);
            Assert.AreEqual(expectedHash, copy.ManifestHash);
            using var serialized = new MemoryStream();
            await serializer.WriteAsync(copy, serialized);
            serialized.Position = 0;
            using var restored = await serializer.ReadAsync(serialized);
            Assert.AreEqual(expectedHash, restored.ManifestHash);
            Assert.AreEqual(3, restored.Entries.Count());
        }
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(3)]
    [DataRow(31)]
    public async Task ChunkedNonseekableChangeManifestPreservesLargeSingleEntriesAndUtf8Bom(int chunkSize)
    {
        using var provider = CreateServices();
        var serializer = provider.GetRequiredService<IChangeManifestSerializer>();
        var fingerprint = "custom:" + new string('x', 40 * 1024);
        using var original = serializer.Create
        (
            [new("ä資料.txt", fingerprint, ContentHash: ContentHash)],
            "mirror"
        );
        using var serialized = new MemoryStream();
        serialized.Write(new byte[] { 0xef, 0xbb, 0xbf });
        await serializer.WriteAsync(original, serialized);
        serialized.Position = 0;
        using var source = new ChunkedManifestInput(serialized, chunkSize);

        using var restored = await serializer.ReadAsync(source);

        Assert.AreEqual(original.ManifestHash, restored.ManifestHash);
        Assert.AreEqual("ä資料.txt", restored.Entries.Single().RelativePath);
        Assert.AreEqual(fingerprint, restored.Entries.Single().ChangeFingerprint);
    }

    [TestMethod]
    public async Task EntriesMayPrecedeHeaderPropertiesWithoutLosingStrictValidation()
    {
        using var provider = CreateServices();
        var serializer = provider.GetRequiredService<ILogicalStateManifestSerializer>();
        using var original = serializer.Create
        (
            [new("first.txt", ArchiveChangeFingerprint.Create(3, Timestamp), ContentHash)]
        );
        using var serialized = new MemoryStream();
        await serializer.WriteAsync(original, serialized);
        var json = Encoding.UTF8.GetString(serialized.ToArray());
        using var parsed = System.Text.Json.JsonDocument.Parse(json);
        var root = parsed.RootElement;
        var reordered = "{\"entries\":" + root.GetProperty("entries").GetRawText() +
            ",\"manifestHash\":" + root.GetProperty("manifestHash").GetRawText() +
            ",\"schemaVersion\":1,\"documentType\":\"yabt.logicalStateManifest\"}";
        using var source = new ChunkedManifestInput(new MemoryStream(Encoding.UTF8.GetBytes(reordered)), 3);

        using var restored = await serializer.ReadAsync(source);

        Assert.AreEqual(original.ManifestHash, restored.ManifestHash);
        Assert.AreEqual("first.txt", restored.Entries.Single().LogicalRelativePath);
    }

    [TestMethod]
    public async Task CanceledReadDisposesItsPartialEntryIndex()
    {
        using var provider = CreateServices();
        var serializer = provider.GetRequiredService<IChangeManifestSerializer>();
        using var serialized = new MemoryStream();
        using (var original = serializer.Create(GenerateChangeEntries(1000), "mirror"))
        {
            await serializer.WriteAsync(original, serialized);
        }
        Assert.IsEmpty(Directory.EnumerateFiles(_temporaryDirectory));
        serialized.Position = 0;
        using var cancellation = new CancellationTokenSource();
        using var source = new ChunkedManifestInput(serialized, 97, cancellation, cancelAfter: 4096);

        await Assert.ThrowsAsync<YabtMetadataException>
        (
            () => serializer.ReadAsync(source, cancellation.Token)
        );

        Assert.IsTrue(cancellation.IsCancellationRequested);
        Assert.IsEmpty(Directory.EnumerateFiles(_temporaryDirectory));
    }

    [TestMethod]
    [DataRow("{\"documentType\":\"yabt.historyManifest\",\"schemaVersion\":1,\"entries\":[")]
    [DataRow("{\"documentType\":\"yabt.historyManifest\",\"schemaVersion\":1,\"entries\":[],\"entries\":[]}")]
    [DataRow("{\"documentType\":\"yabt.historyManifest\",\"schemaVersion\":1,\"unknown\":[")]
    [DataRow("{\"documentType\":\"yabt.historyManifest\",\"schemaVersion\":1,\"entries\":null}")]
    public async Task MalformedOrUnknownManifestContentReleasesEntryIndex(string json)
    {
        using var provider = CreateServices();
        var serializer = provider.GetRequiredService<IHistoryManifestSerializer>();
        using var source = new ChunkedManifestInput(new MemoryStream(Encoding.UTF8.GetBytes(json)), 1);

        await Assert.ThrowsAsync<YabtMetadataException>(() => serializer.ReadAsync(source));

        Assert.IsEmpty(Directory.EnumerateFiles(_temporaryDirectory));
    }

    [TestMethod]
    public async Task FailedValidationOfCallerOwnedEntrySequenceReleasesItsSnapshot()
    {
        using var provider = CreateServices();
        var serializer = provider.GetRequiredService<IChangeManifestSerializer>();
        var invalid = new ArchiveChangeManifest
        (
            ArchiveChangeManifest.ExpectedDocumentType,
            ArchiveChangeManifest.ExpectedSchemaVersion,
            GenerateChangeEntries(3),
            ArchiveHash.Compute("incorrect"u8),
            "mirror"
        );
        using var destination = new MemoryStream();

        await Assert.ThrowsAsync<YabtMetadataException>(() => serializer.WriteAsync(invalid, destination));

        Assert.IsEmpty(Directory.EnumerateFiles(_temporaryDirectory));
    }

    [TestMethod]
    public void FailedPackageHeaderValidationReleasesCreatedCanonicalEntries()
    {
        using var provider = CreateServices();
        var serializer = provider.GetRequiredService<IManifestSerializer>();

        Assert.Throws<YabtMetadataException>(() => serializer.Create
        (
            string.Empty,
            Timestamp,
            "invalid format",
            1,
            ContentHash,
            "archive.zip",
            new("zip"),
            GeneratePackageEntries(2)
        ));

        Assert.IsEmpty(Directory.EnumerateFiles(_temporaryDirectory));
    }

    [TestMethod]
    [DataRow("{\"documentType\":\"expected\",\"entries\":[{},{}]}", true)]
    [DataRow("{\"documentType\":\"expected\",\"entries\":[", false)]
    [DataRow("{\"documentType\":\"expected\"} trailing", false)]
    [DataRow("{\"nested\":{\"documentType\":\"expected\"}}", false)]
    [DataRow("{\"documentType\":123}", false)]
    [DataRow("[\"expected\"]", false)]
    [DataRow("{\"documentType\":\"expected\",\"documentType\":\"other\"}", false)]
    [DataRow("{\"documentType\":\"expected\",\"n\":12e+4,\"s\":\"\\u0061\\n\"}", true)]
    [DataRow("{\"documentType\":\"expected\",\"n\":01}", false)]
    [DataRow("{\"documentType\":\"expected\",\"n\":1.}", false)]
    [DataRow("{\"documentType\":\"expected\",\"n\":1e+}", false)]
    [DataRow("{\"documentType\":\"expected\",\"s\":\"\\u00xx\"}", false)]
    [DataRow("{\"documentType\":\"expected\",\"n\":[1 2]}", false)]
    public async Task DocumentTypeProbeValidatesCompleteJsonWithoutMaterializingIt(string json, bool expected)
    {
        using var source = new ChunkedManifestInput(new MemoryStream(Encoding.UTF8.GetBytes(json)), 1);

        Assert.AreEqual(expected, await MetadataJsonDocumentType.HasAsync(source, "expected"));
    }

    [TestMethod]
    public async Task DocumentTypeProbePropagatesReadFailureAfterMatchingHeader()
    {
        using var source = new ThrowingTailManifestInput();

        await Assert.ThrowsAsync<IOException>(() => MetadataJsonDocumentType.HasAsync(source, "expected"));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task DocumentTypeProbeStreamsHugeSingleStringOrNumberTokens(bool stringToken)
    {
        using var source = new GeneratedJsonProbeInput(stringToken, 32L * 1024 * 1024);

        Assert.IsTrue(await MetadataJsonDocumentType.HasAsync(source, "expected"));
        Assert.IsLessThanOrEqualTo(4096, source.MaximumReadRequest);
    }

    private TemporaryContentOptions Options => new()
    {
        MemoryThresholdBytes = 1024,
        TotalMemoryBudgetBytes = 0,
        TemporaryDirectory = _temporaryDirectory
    };

    private ServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IOptionsMonitor<TemporaryContentOptions>>(new ManifestTemporaryOptionsMonitor(Options));
        services.AddYabtMetadata();
        return services.BuildServiceProvider();
    }

    private static IEnumerable<ArchiveManifestEntry> GeneratePackageEntries(int count)
    {
        var suffix = new string('x', 220) + ".txt";
        for (var index = count - 1; index >= 0; index--)
        {
            var path = $"{index:D8}-{suffix}";
            yield return new(ArchiveManifestEntryKinds.File, path, path, 1, Timestamp, ContentHash);
        }
    }

    private static IEnumerable<ArchiveChangeManifestEntry> GenerateChangeEntries(int count)
    {
        for (var index = 0; index < count; index++)
        {
            yield return new($"{index:D8}.txt", "custom:value", ContentHash: ContentHash);
        }
    }

    private static async Task AssertEqualStreamsAsync(Stream first, Stream second)
    {
        var firstBuffer = new byte[16 * 1024];
        var secondBuffer = new byte[16 * 1024];
        int count;
        while ((count = await first.ReadAsync(firstBuffer)) != 0)
        {
            await second.ReadExactlyAsync(secondBuffer.AsMemory(0, count));
            Assert.IsTrue(firstBuffer.AsSpan(0, count).SequenceEqual(secondBuffer.AsSpan(0, count)));
        }
        Assert.AreEqual(0, await second.ReadAsync(secondBuffer));
    }

    private sealed class ManifestTemporaryOptionsMonitor
    (
        TemporaryContentOptions _currentValue
    ) : IOptionsMonitor<TemporaryContentOptions>
    {
        public TemporaryContentOptions CurrentValue => _currentValue;

        public TemporaryContentOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<TemporaryContentOptions, string?> listener) => null;
    }

    private sealed class ChunkedManifestInput
    (
        Stream _source,
        int _chunkSize,
        CancellationTokenSource? cancellation = null,
        long cancelAfter = long.MaxValue
    ) : Stream
    {
        private long _read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            _source.Read(buffer, offset, Math.Min(count, _chunkSize));

        public override async ValueTask<int> ReadAsync
        (
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            if (_read >= cancelAfter) { cancellation?.Cancel(); }
            cancellationToken.ThrowIfCancellationRequested();
            var read = await _source.ReadAsync(buffer[..Math.Min(buffer.Length, _chunkSize)], cancellationToken);
            _read += read;
            return read;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _source.Dispose(); }
            base.Dispose(disposing);
        }
    }

    private sealed class ThrowingTailManifestInput : MemoryStream
    {
        public ThrowingTailManifestInput() : base("{\"documentType\":\"expected\"}"u8.ToArray())
        {
        }

        public override ValueTask<int> ReadAsync
        (
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        ) => Position == Length ?
            ValueTask.FromException<int>(new IOException("Injected read failure after document type.")) :
            base.ReadAsync(buffer, cancellationToken);
    }

    private sealed class GeneratedJsonProbeInput : Stream
    {
        private readonly byte[] _prefix;
        private readonly byte[] _suffix;
        private readonly long _payloadLength;
        private long _position;

        public GeneratedJsonProbeInput(bool stringToken, long payloadLength)
        {
            _prefix = Encoding.UTF8.GetBytes("{\"documentType\":\"expected\",\"payload\":" + (stringToken ? "\"" : string.Empty));
            _suffix = Encoding.UTF8.GetBytes(stringToken ? "\"}" : "}");
            _payloadLength = payloadLength;
        }

        public int MaximumReadRequest { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            MaximumReadRequest = Math.Max(MaximumReadRequest, buffer.Length);
            if (_position < _prefix.Length)
            {
                var count = Math.Min(buffer.Length, _prefix.Length - (int)_position);
                _prefix.AsSpan((int)_position, count).CopyTo(buffer);
                _position += count;
                return count;
            }
            var payloadEnd = _prefix.Length + _payloadLength;
            if (_position < payloadEnd)
            {
                var count = (int)Math.Min(buffer.Length, payloadEnd - _position);
                buffer[..count].Fill((byte)'1');
                _position += count;
                return count;
            }
            var suffixPosition = (int)(_position - payloadEnd);
            var suffixCount = Math.Min(buffer.Length, _suffix.Length - suffixPosition);
            _suffix.AsSpan(suffixPosition, suffixCount).CopyTo(buffer);
            _position += suffixCount;
            return suffixCount;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
