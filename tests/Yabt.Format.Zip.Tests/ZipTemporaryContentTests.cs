using System.IO.Compression;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Yabt.Common;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;

namespace Yabt.Format.Zip.Tests;

[TestClass]
public sealed class ZipTemporaryContentTests
{
    private string _temporaryDirectory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _temporaryDirectory = Path.Combine(Path.GetTempPath(), $"yabt-zip-spill-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_temporaryDirectory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        Assert.IsEmpty(Directory.EnumerateFileSystemEntries(_temporaryDirectory));
        Directory.Delete(_temporaryDirectory);
    }

    [TestMethod]
    [DataRow(true, 0L)]
    [DataRow(false, 0L)]
    [DataRow(true, 4096L)]
    [DataRow(false, 4096L)]
    public async Task LargeNonseekableSourceIsReadOnceAndArtifactsShareOneDiskBackedMaterialization
    (
        bool provideMetadata,
        long memoryBudget
    )
    {
        const long payloadLength = 32L * 1024 * 1024 + 137;
        using var services = CreateServices(threshold: 1024, memoryBudget);
        var handler = services.GetRequiredService<IArchiveFormatHandler>();
        var factory = services.GetRequiredService<ITemporaryContentFactory>();
        var source = new GeneratedZipSourceStore(payloadLength, provideMetadata);
        await using (var projection = handler.ProjectBackupAsync(new(source, SourceDisplayName: "Large")))
        {
            var artifacts = await CollectAsync(projection);
            Assert.AreEqual(provideMetadata ? 0 : 1, source.OpenCount);
            var package = GetArtifact(artifacts, ArchiveProjectionArtifactRoles.Package);
            var manifest = GetArtifact(artifacts, ArchiveProjectionArtifactRoles.Manifest);
            Assert.AreEqual(package.Projection!.ProjectionId, manifest.Projection!.ProjectionId);
            var opening = await Task.WhenAll
            (
                package.OpenContentAsync(default),
                manifest.OpenContentAsync(default),
                package.OpenContentAsync(default)
            );
            await using var packageContent = opening[0];
            await using var manifestContent = opening[1];
            await using var otherPackageContent = opening[2];

            Assert.AreEqual(1, source.OpenCount);
            Assert.AreEqual(1, source.DisposeCount);
            Assert.IsGreaterThan(payloadLength, packageContent.Content.Length);
            Assert.IsTrue(factory.ReservedMemoryBytes <= memoryBudget);
            Assert.IsGreaterThanOrEqualTo(1, Directory.EnumerateFiles(_temporaryDirectory).Count());
            Assert.AreEqual('P', packageContent.Content.ReadByte());
            Assert.AreEqual('P', otherPackageContent.Content.ReadByte());
            Assert.AreEqual(1L, otherPackageContent.Content.Position);
            packageContent.Content.Position = 0;

            using var archive = new ZipArchive(packageContent.Content, ZipArchiveMode.Read, leaveOpen: true);
            var payload = archive.GetEntry(GeneratedZipSourceStore.PayloadName);
            Assert.IsNotNull(payload);
            Assert.AreEqual(payloadLength, payload.Length);
            await using (var payloadContent = payload.Open())
            {
                await AssertGeneratedBytesAsync(payloadContent, payloadLength);
            }
            var embedded = archive.GetEntry(ArchivePackageManifestFileNames.EmbeddedEntryName);
            Assert.IsNotNull(embedded);
            await using var embeddedContent = embedded.Open();
            await AssertEqualStreamsAsync(manifestContent.Content, embeddedContent);
        }

        Assert.AreEqual(0L, factory.ReservedMemoryBytes);
        Assert.IsEmpty(Directory.EnumerateFiles(_temporaryDirectory));
    }

    [TestMethod]
    public async Task OpenArtifactReaderRetainsItsBackingAfterProjectionDisposal()
    {
        using var services = CreateServices(threshold: 1024, memoryBudget: 0);
        var handler = services.GetRequiredService<IArchiveFormatHandler>();
        var factory = services.GetRequiredService<ITemporaryContentFactory>();
        var source = new GeneratedZipSourceStore(2 * 1024 * 1024, provideMetadata: true);
        var projection = handler.ProjectBackupAsync(new(source, SourceDisplayName: "Leased"));
        var artifacts = await CollectAsync(projection);
        var package = GetArtifact(artifacts, ArchiveProjectionArtifactRoles.Package);
        await using (var packageContent = await package.OpenContentAsync(default))
        {
            await projection.DisposeAsync();

            Assert.HasCount(1, Directory.EnumerateFiles(_temporaryDirectory));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => package.OpenContentAsync(default));
            using var archive = new ZipArchive(packageContent.Content, ZipArchiveMode.Read, leaveOpen: true);
            var payload = archive.GetEntry(GeneratedZipSourceStore.PayloadName);
            Assert.IsNotNull(payload);
            await using var payloadContent = payload.Open();
            await AssertGeneratedBytesAsync(payloadContent, 2 * 1024 * 1024);
        }

        await projection.DisposeAsync();
        Assert.AreEqual(0L, factory.ReservedMemoryBytes);
        Assert.IsEmpty(Directory.EnumerateFiles(_temporaryDirectory));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task SourceFailureAfterSpillingReleasesPartialPackage(bool provideMetadata)
    {
        using var services = CreateServices(threshold: 1024, memoryBudget: 4096);
        var handler = services.GetRequiredService<IArchiveFormatHandler>();
        var factory = services.GetRequiredService<ITemporaryContentFactory>();
        var source = new GeneratedZipSourceStore
        (
            1024 * 1024,
            provideMetadata,
            failAfter: 128 * 1024
        );
        await using (var projection = handler.ProjectBackupAsync(new(source, SourceDisplayName: "Failed")))
        {
            var exception = await Assert.ThrowsAsync<AggregateException>(async () =>
            {
                var artifacts = await CollectAsync(projection);
                var package = GetArtifact(artifacts, ArchiveProjectionArtifactRoles.Package);
                await using var ignored = await package.OpenContentAsync(default);
            });

            Assert.IsTrue(exception.InnerExceptions.Any(failure => failure is IOException));
            Assert.AreEqual(1, source.OpenCount);
            Assert.AreEqual(1, source.DisposeCount);
        }

        Assert.AreEqual(0L, factory.ReservedMemoryBytes);
        Assert.IsEmpty(Directory.EnumerateFiles(_temporaryDirectory));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task CancellationAfterSpillingReleasesPartialPackage(bool provideMetadata)
    {
        using var services = CreateServices(threshold: 1024, memoryBudget: 4096);
        var handler = services.GetRequiredService<IArchiveFormatHandler>();
        var factory = services.GetRequiredService<ITemporaryContentFactory>();
        using var cancellation = new CancellationTokenSource();
        var source = new GeneratedZipSourceStore
        (
            1024 * 1024,
            provideMetadata,
            failAfter: 128 * 1024,
            cancellation: cancellation
        );
        await using (var projection = handler.ProjectBackupAsync
        (
            new(source, SourceDisplayName: "Canceled"),
            cancellation.Token
        ))
        {
            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            {
                var artifacts = await CollectAsync(projection);
                var package = GetArtifact(artifacts, ArchiveProjectionArtifactRoles.Package);
                await using var ignored = await package.OpenContentAsync(cancellation.Token);
            });

            Assert.AreEqual(1, source.OpenCount);
            Assert.AreEqual(1, source.DisposeCount);
        }

        Assert.AreEqual(0L, factory.ReservedMemoryBytes);
        Assert.IsEmpty(Directory.EnumerateFiles(_temporaryDirectory));
    }

    [TestMethod]
    public async Task AbandoningEagerProjectionAfterFirstArtifactReleasesBothArtifacts()
    {
        using var services = CreateServices(threshold: 1024, memoryBudget: 0);
        var handler = services.GetRequiredService<IArchiveFormatHandler>();
        var factory = services.GetRequiredService<ITemporaryContentFactory>();
        var source = new GeneratedZipSourceStore(1024 * 1024, provideMetadata: false);
        await using (var projection = handler.ProjectBackupAsync(new(source, SourceDisplayName: "Abandoned")))
        {
            await using var enumerator = projection.GetAsyncEnumerator();
            Assert.IsTrue(await enumerator.MoveNextAsync());
            Assert.AreEqual(1, source.OpenCount);
            Assert.HasCount(2, Directory.EnumerateFiles(_temporaryDirectory));
        }

        Assert.AreEqual(0L, factory.ReservedMemoryBytes);
        Assert.IsEmpty(Directory.EnumerateFiles(_temporaryDirectory));
    }

    private ServiceProvider CreateServices(int threshold, long memoryBudget)
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IOptionsMonitor<TemporaryContentOptions>>
        (
            new ZipTemporaryContentOptionsMonitor<TemporaryContentOptions>(new()
            {
                MemoryThresholdBytes = threshold,
                TotalMemoryBudgetBytes = memoryBudget,
                TemporaryDirectory = _temporaryDirectory
            })
        );
        services.AddSingleton<IOptionsMonitor<ZipArchiveFormatOptions>>
        (
            new ZipTemporaryContentOptionsMonitor<ZipArchiveFormatOptions>(new()
            {
                CompressionLevel = CompressionLevel.NoCompression,
                HashBufferSize = 16 * 1024
            })
        );
        services.AddYabtZipFormatHandler();
        services.AddYabtMetadata();
        return services.BuildServiceProvider();
    }

    private static async Task<List<ArchiveProjectedObject>> CollectAsync(ArchiveBackupProjection projection)
    {
        var artifacts = new List<ArchiveProjectedObject>();
        await foreach (var artifact in projection) { artifacts.Add(artifact); }
        return artifacts;
    }

    private static ArchiveProjectedObject GetArtifact(IEnumerable<ArchiveProjectedObject> artifacts, string role) =>
        artifacts.Single(artifact => artifact.Projection?.ArtifactRole == role);

    private static async Task AssertGeneratedBytesAsync(Stream source, long expectedLength)
    {
        var buffer = new byte[16 * 1024];
        long position = 0;
        int count;
        while ((count = await source.ReadAsync(buffer)) != 0)
        {
            for (var index = 0; index < count; index++)
            {
                if (buffer[index] != (byte)((position + index) % 251))
                {
                    Assert.Fail($"Payload differs at byte {position + index}.");
                }
            }
            position += count;
        }
        Assert.AreEqual(expectedLength, position);
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

    private sealed class ZipTemporaryContentOptionsMonitor<TOptions>
    (
        TOptions _currentValue
    ) : IOptionsMonitor<TOptions> where TOptions : class
    {
        public TOptions CurrentValue => _currentValue;

        public TOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<TOptions, string?> listener) => null;
    }

    private sealed class GeneratedZipSourceStore
    (
        long _length,
        bool provideMetadata,
        long? failAfter = null,
        CancellationTokenSource? cancellation = null
    ) : IReadOnlyObjectStore
    {
        internal const string PayloadName = "payload.bin";

        public int OpenCount { get; private set; }

        public int DisposeCount { get; private set; }

        public Task EnsureReadyAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<ArchiveObjectContent> OpenReadAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.AreEqual(PayloadName, key);
            OpenCount++;
            return Task.FromResult(new ArchiveObjectContent
            (
                new GeneratedZipPayloadStream(_length, () => DisposeCount++, failAfter, cancellation)
            ));
        }

        public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(key == PayloadName);

        public async IAsyncEnumerable<ArchiveFolderItem> GetFolderItemsAsync
        (
            string? folderPrefix,
            bool recursive = false,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.IsTrue(string.IsNullOrEmpty(folderPrefix));
            await Task.CompletedTask;
            yield return ArchiveFolderItem.CreateObject
            (
                PayloadName,
                new
                (
                    PayloadName,
                    ContentLength: provideMetadata ? _length : null,
                    LastModifiedUtc: provideMetadata ? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) : null
                )
            );
        }
    }

    private sealed class GeneratedZipPayloadStream
    (
        long _length,
        Action _onDispose,
        long? failAfter,
        CancellationTokenSource? cancellation
    ) : Stream
    {
        private long _position;
        private bool _disposed;

        public override bool CanRead => !_disposed;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (failAfter is not null && _position >= failAfter.Value)
            {
                if (cancellation is not null)
                {
                    cancellation.Cancel();
                    cancellation.Token.ThrowIfCancellationRequested();
                }
                throw new IOException("Injected generated-source read failure.");
            }
            var count = (int)Math.Min(buffer.Length, _length - _position);
            for (var index = 0; index < count; index++)
            {
                buffer[index] = (byte)((_position + index) % 251);
            }
            _position += count;
            return count;
        }

        public override ValueTask<int> ReadAsync
        (
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                _onDispose();
            }
            base.Dispose(disposing);
        }
    }
}
