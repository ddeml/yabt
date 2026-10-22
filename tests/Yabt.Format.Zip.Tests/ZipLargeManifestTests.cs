using System.Buffers.Binary;
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
public sealed class ZipLargeManifestTests
{
    [TestMethod]
    public async Task LargeManifestRoundTripsAndRepeatedConcurrentOpensDoNotRereadCentralDirectory()
    {
        const int fileCount = 20_000;
        var directory = Path.Combine(Path.GetTempPath(), $"yabt-large-manifest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var options = new TemporaryContentOptions
            {
                TemporaryDirectory = directory,
                MemoryThresholdBytes = 4096,
                TotalMemoryBudgetBytes = 0
            };
            using (TemporaryIndex.BeginOperation(options))
            using (var backingServices = CreateServices(options))
            {
                var factory = new ObservedContentFactory(backingServices.GetRequiredService<ITemporaryContentFactory>());
                using var services = CreateServices(options, factory);
                var handler = services.GetRequiredService<IArchiveFormatHandler>();
                var source = new GeneratedManyFilesStore(fileCount);
                await using var backup = handler.ProjectBackupAsync(new(source, SourceDisplayName: "ManyFiles"));
                var artifacts = new List<ArchiveProjectedObject>();
                await foreach (var artifact in backup)
                {
                    await using var content = await artifact.OpenContentAsync(default);
                    artifacts.Add(artifact with
                    {
                        ContentLength = content.Content.Length,
                        ContentHash = await ArchiveHash.ComputeAsync(content.Content)
                    });
                }

                Assert.AreEqual(fileCount, source.OpenCount);
                var manifest = artifacts.Single(artifact => artifact.Projection?.ArtifactRole == ArchiveProjectionArtifactRoles.Manifest);
                Assert.IsGreaterThan(16L * 1024 * 1024, manifest.ContentLength!.Value);
                Assert.AreEqual(0L, factory.ReservedMemoryBytes);

                factory.ObserveNewContent = true;
                await using var restore = await handler.ProjectRestoreAsync(new
                (
                    artifacts,
                    restoreAsRoot: true,
                    requireCompleteProjection: true
                ));
                Assert.AreEqual(fileCount, restore.Objects.Count);
                Assert.AreEqual(0, factory.ObservedPackages);
                var firstObjects = restore.Objects.Take(8).ToArray();
                var readers = await Task.WhenAll(firstObjects.Select(item => item.OpenContentAsync(default)));
                try
                {
                    foreach (var reader in readers) { Assert.AreEqual(-1, reader.Content.ReadByte()); }
                    Assert.AreEqual(1, factory.ObservedPackages);
                    var initialDirectoryBytes = factory.CentralDirectoryBytesRead;
                    Assert.IsGreaterThan(0L, initialDirectoryBytes);

                    // Replaying a projected object creates another independent entry reader.
                    // None of these readers may parse or load the central directory again.
                    foreach (var item in restore.Objects.Skip(fileCount - 8))
                    {
                        await using var content = await item.OpenContentAsync(default);
                        Assert.AreEqual(-1, content.Content.ReadByte());
                    }
                    Assert.AreEqual(initialDirectoryBytes, factory.CentralDirectoryBytesRead);
                    Assert.AreEqual(fileCount, source.OpenCount);
                    Assert.AreEqual(0L, factory.ReservedMemoryBytes);
                }
                finally
                {
                    foreach (var reader in readers) { await reader.DisposeAsync(); }
                }
            }
            Assert.IsEmpty(Directory.EnumerateFileSystemEntries(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ServiceProvider CreateServices
    (
        TemporaryContentOptions options,
        ITemporaryContentFactory? factory = null
    )
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IOptionsMonitor<TemporaryContentOptions>>(new FixedOptions<TemporaryContentOptions>(options));
        services.AddSingleton<IOptionsMonitor<ZipArchiveFormatOptions>>(new FixedOptions<ZipArchiveFormatOptions>(new()
        {
            CompressionLevel = CompressionLevel.NoCompression
        }));
        services.AddYabtZipFormatHandler();
        services.AddYabtMetadata();
        if (factory is not null) { services.AddSingleton(factory); }
        return services.BuildServiceProvider();
    }

    private sealed class FixedOptions<T>(T _value) : IOptionsMonitor<T> where T : class
    {
        public T CurrentValue => _value;
        public T Get(string? name) => _value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class GeneratedManyFilesStore(int _count) : IReadOnlyObjectStore
    {
        public int OpenCount { get; private set; }

        public Task EnsureReadyAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<ArchiveObjectContent> OpenReadAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenCount++;
            return Task.FromResult(new ArchiveObjectContent(new MemoryStream([])));
        }

        public async IAsyncEnumerable<ArchiveFolderItem> GetFolderItemsAsync
        (
            string? folderPrefix,
            bool recursive = false,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            Assert.IsTrue(string.IsNullOrEmpty(folderPrefix));
            await Task.CompletedTask;
            for (var index = 0; index < _count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = $"file-{index:D6}-{new string('x', 400)}.txt";
                yield return ArchiveFolderItem.CreateObject(name, new
                (
                    name,
                    ContentLength: 0,
                    LastModifiedUtc: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
                ));
            }
        }
    }

    private sealed class ObservedContentFactory(ITemporaryContentFactory _inner) : ITemporaryContentFactory
    {
        private long _centralDirectoryBytesRead;
        private int _observedPackages;

        public bool ObserveNewContent { get; set; }
        public long ReservedMemoryBytes => _inner.ReservedMemoryBytes;
        public long CentralDirectoryBytesRead => Interlocked.Read(ref _centralDirectoryBytesRead);
        public int ObservedPackages => _observedPackages;

        public TemporaryContent Create() => ObserveNewContent
            ? new ObservedContent(_inner.Create(), this)
            : _inner.Create();

        private sealed class ObservedContent(TemporaryContent _content, ObservedContentFactory _owner) : TemporaryContent
        {
            private long _directoryOffset;
            private long _directoryLength;

            public override bool IsSealed => _content.IsSealed;
            public override bool IsFileBacked => _content.IsFileBacked;
            public override bool CanRead => _content.CanRead;
            public override bool CanSeek => _content.CanSeek;
            public override bool CanWrite => _content.CanWrite;
            public override long Length => _content.Length;
            public override long Position { get => _content.Position; set => _content.Position = value; }
            public override void Flush() => _content.Flush();
            public override Task FlushAsync(CancellationToken cancellationToken) => _content.FlushAsync(cancellationToken);
            public override int Read(byte[] buffer, int offset, int count) => _content.Read(buffer, offset, count);
            public override long Seek(long offset, SeekOrigin origin) => _content.Seek(offset, origin);
            public override void SetLength(long value) => _content.SetLength(value);
            public override void Write(byte[] buffer, int offset, int count) => _content.Write(buffer, offset, count);
            public override void Write(ReadOnlySpan<byte> buffer) => _content.Write(buffer);
            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
                _content.WriteAsync(buffer, cancellationToken);

            public override async ValueTask SealAsync(CancellationToken cancellationToken = default)
            {
                await _content.SealAsync(cancellationToken);
                if (_content.Length < 22) { return; }
                using var reader = _content.OpenRead();
                reader.Position = reader.Length - 22;
                Span<byte> end = stackalloc byte[22];
                reader.ReadExactly(end);
                if (BinaryPrimitives.ReadUInt32LittleEndian(end) != 0x06054b50) { return; }
                _directoryLength = BinaryPrimitives.ReadUInt32LittleEndian(end[12..]);
                _directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(end[16..]);
                Interlocked.Increment(ref _owner._observedPackages);
            }

            public override Stream OpenRead() => _directoryLength == 0
                ? _content.OpenRead()
                : new ObservedReadStream(_content.OpenRead(), _directoryOffset, _directoryLength, _owner);

            protected override void Dispose(bool disposing)
            {
                if (disposing) { _content.Dispose(); }
                base.Dispose(disposing);
            }

            public override async ValueTask DisposeAsync()
            {
                await _content.DisposeAsync();
                GC.SuppressFinalize(this);
            }
        }

        private sealed class ObservedReadStream
        (
            Stream _inner,
            long _directoryOffset,
            long _directoryLength,
            ObservedContentFactory _owner
        ) : Stream
        {
            public override bool CanRead => _inner.CanRead;
            public override bool CanSeek => _inner.CanSeek;
            public override bool CanWrite => false;
            public override long Length => _inner.Length;
            public override long Position { get => _inner.Position; set => _inner.Position = value; }
            public override void Flush() => _inner.Flush();
            public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

            public override int Read(Span<byte> buffer)
            {
                var position = _inner.Position;
                var count = _inner.Read(buffer);
                Observe(position, count);
                return count;
            }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                var position = _inner.Position;
                var count = await _inner.ReadAsync(buffer, cancellationToken);
                Observe(position, count);
                return count;
            }

            private void Observe(long position, int count)
            {
                var overlap = Math.Max(0, Math.Min(position + count, _directoryOffset + _directoryLength) -
                    Math.Max(position, _directoryOffset));
                Interlocked.Add(ref _owner._centralDirectoryBytesRead, overlap);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) { _inner.Dispose(); }
                base.Dispose(disposing);
            }

            public override async ValueTask DisposeAsync()
            {
                await _inner.DisposeAsync();
                GC.SuppressFinalize(this);
            }
        }
    }
}
