using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.Tests;

namespace Yabt.Format.Zip.Tests;

[TestClass]
public sealed class ZipArchiveFormatProjectorTests
{
    [TestMethod]
    public void ServiceRegistrationRegistersZipFormatProjector()
    {
        using var serviceProvider = CreateServices().BuildServiceProvider();

        var projectors = serviceProvider.GetServices<IArchiveFormatProjector>().ToArray();

        Assert.AreEqual(1, projectors.Length);
        var projector = projectors[0];
        Assert.AreEqual(ZipArchiveFormatName.Value, projector.FormatName);
    }

    [TestMethod]
    public async Task ProjectAsyncProjectsSourceFolderToSingleZipPackage()
    {
        using var serviceProvider = CreateServices().BuildServiceProvider();
        var projector = serviceProvider.GetRequiredService<IArchiveFormatProjector>();
        var sourceStore = new MemoryObjectStore(provideContentHash: true);

        await UploadTextAsync(sourceStore, "folder/file.txt", "source content");

        var projectedObjects = await CollectProjectedObjectsAsync(projector.ProjectAsync(new
        (
            sourceStore,
            Policy: new FolderPolicy(ZipArchiveFormatName.Value),
            SourceDisplayName: "Photos"
        )));

        var projectedObject = projectedObjects.Single();
        StringAssert.Matches(
            projectedObject.RelativePath,
            new Regex
            (
                "^Photos\\.xxh128-[0-9a-v]{25}[048cgkos]\\.zip$",
                RegexOptions.CultureInvariant
            ));
        Assert.IsNull(projectedObject.ContentHash);
        StringAssert.Matches(
            projectedObject.ChangeFingerprint,
            new Regex
            (
                "^xxh128:[A-Za-z0-9_-]{21}[AQgw]$",
                RegexOptions.CultureInvariant
            ));
        StringAssert.Contains(
            projectedObject.RelativePath,
            ArchiveHash.FormatFileNameToken(projectedObject.ChangeFingerprint));

        await using var content = await projectedObject.OpenContentAsync(default);
        using var archive = new ZipArchive(content.Content, ZipArchiveMode.Read);
        var entry = archive.GetEntry("folder/file.txt");

        Assert.IsNotNull(entry);
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        Assert.AreEqual("source content", await reader.ReadToEndAsync());
    }

    [TestMethod]
    public async Task ProjectAsyncUsesStableFullHashNameForUnchangedSource()
    {
        using var serviceProvider = CreateServices().BuildServiceProvider();
        var projector = serviceProvider.GetRequiredService<IArchiveFormatProjector>();
        var timeProvider = new ZipSourceTimeProvider(
            new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));
        var sourceStore = new MemoryObjectStore(timeProvider, provideContentHash: true);
        await UploadTextAsync(sourceStore, "folder/file.txt", "source content");
        var request = new ArchiveProjectionRequest
        (
            sourceStore,
            Policy: new FolderPolicy(ZipArchiveFormatName.Value),
            SourceDisplayName: "Photos"
        );

        var firstProjection = (await CollectProjectedObjectsAsync(
            projector.ProjectAsync(request))).Single();
        var secondProjection = (await CollectProjectedObjectsAsync(
            projector.ProjectAsync(request))).Single();

        Assert.AreEqual(firstProjection.RelativePath, secondProjection.RelativePath);
        Assert.AreEqual(
            "Photos.xxh128-a5rquodjp7f84aj82brdp2skjk.zip",
            firstProjection.RelativePath);
        StringAssert.Matches(
            firstProjection.RelativePath,
            new Regex
            (
                "^Photos\\.xxh128-[0-9a-v]{25}[048cgkos]\\.zip$",
                RegexOptions.CultureInvariant
            ));
        CollectionAssert.AreEqual(
            await ReadContentBytesAsync(firstProjection),
            await ReadContentBytesAsync(secondProjection));
    }

    [TestMethod]
    public async Task ProjectAsyncChangesFullHashNameWhenSourceContentChanges()
    {
        using var serviceProvider = CreateServices().BuildServiceProvider();
        var projector = serviceProvider.GetRequiredService<IArchiveFormatProjector>();
        var timeProvider = new ZipSourceTimeProvider(
            new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));
        var firstSourceStore = new MemoryObjectStore(timeProvider, provideContentHash: true);
        var secondSourceStore = new MemoryObjectStore(timeProvider, provideContentHash: true);
        await UploadTextAsync(firstSourceStore, "folder/file.txt", "first content");
        await UploadTextAsync(secondSourceStore, "folder/file.txt", "second content");

        var firstProjection = (await CollectProjectedObjectsAsync(projector.ProjectAsync(new
        (
            firstSourceStore,
            Policy: new FolderPolicy(ZipArchiveFormatName.Value),
            SourceDisplayName: "Photos"
        )))).Single();
        var secondProjection = (await CollectProjectedObjectsAsync(projector.ProjectAsync(new
        (
            secondSourceStore,
            Policy: new FolderPolicy(ZipArchiveFormatName.Value),
            SourceDisplayName: "Photos"
        )))).Single();

        Assert.AreNotEqual(firstProjection.RelativePath, secondProjection.RelativePath);
    }

    [TestMethod]
    public async Task ProjectAsyncUsesMetadataFingerprintWithoutOpeningSourceContent()
    {
        using var serviceProvider = CreateServices().BuildServiceProvider();
        var projector = serviceProvider.GetRequiredService<IArchiveFormatProjector>();
        var innerStore = new MemoryObjectStore(provideContentHash: false);
        await UploadTextAsync(innerStore, "folder/file.txt", "source content");
        var sourceStore = new CountingReadOnlyObjectStore(innerStore);

        var projectedObject = (await CollectProjectedObjectsAsync(projector.ProjectAsync(new
        (
            sourceStore,
            Policy: new FolderPolicy(ZipArchiveFormatName.Value),
            SourceDisplayName: "Photos"
        )))).Single();

        Assert.AreEqual(0, sourceStore.OpenReadCount);
        StringAssert.StartsWith(
            projectedObject.ChangeFingerprint,
            "xxh128:");

        _ = await ReadContentBytesAsync(projectedObject);

        Assert.AreEqual(1, sourceStore.OpenReadCount);
    }

    [TestMethod]
    public async Task ProjectAsyncReadsSourceToFingerprintWhenMetadataAndContentHashAreIncomplete()
    {
        using var serviceProvider = CreateServices().BuildServiceProvider();
        var projector = serviceProvider.GetRequiredService<IArchiveFormatProjector>();
        var innerStore = new MemoryObjectStore(provideContentHash: false);
        await UploadTextAsync(innerStore, "folder/file.txt", "source content");
        var countingStore = new CountingReadOnlyObjectStore(innerStore);
        var sourceStore = new MissingLastModifiedObjectStore(countingStore);

        var projectedObject = (await CollectProjectedObjectsAsync(projector.ProjectAsync(new
        (
            sourceStore,
            Policy: new FolderPolicy(ZipArchiveFormatName.Value),
            SourceDisplayName: "Photos"
        )))).Single();

        Assert.AreEqual(1, countingStore.OpenReadCount);
        StringAssert.StartsWith(
            projectedObject.ChangeFingerprint,
            "xxh128:");

        var firstPackage = await ReadContentBytesAsync(projectedObject);
        var secondPackage = await ReadContentBytesAsync(projectedObject);

        Assert.AreEqual(1, countingStore.OpenReadCount);
        Assert.AreEqual(
            Encoding.UTF8.GetByteCount("source content"),
            countingStore.GetBytesRead("folder/file.txt"));
        Assert.AreEqual(firstPackage.LongLength, projectedObject.ContentLength);
        CollectionAssert.AreEqual(firstPackage, secondPackage);
    }

    [TestMethod]
    public async Task ProjectAsyncReadsEverySourceOnceWhenOneFingerprintNeedsContent()
    {
        using var serviceProvider = CreateServices().BuildServiceProvider();
        var projector = serviceProvider.GetRequiredService<IArchiveFormatProjector>();
        var innerStore = new MemoryObjectStore(provideContentHash: false);
        await UploadTextAsync(innerStore, "folder/incomplete.txt", "incomplete metadata");
        await UploadTextAsync(innerStore, "folder/complete.txt", "complete metadata");
        var countingStore = new CountingReadOnlyObjectStore(innerStore);
        var sourceStore = new MissingLastModifiedObjectStore(
            countingStore,
            "folder/incomplete.txt");

        var projectedObject = (await CollectProjectedObjectsAsync(projector.ProjectAsync(new
        (
            sourceStore,
            Policy: new FolderPolicy(ZipArchiveFormatName.Value),
            SourceDisplayName: "Photos"
        )))).Single();

        Assert.AreEqual(1, countingStore.GetOpenReadCount("folder/incomplete.txt"));
        Assert.AreEqual(1, countingStore.GetOpenReadCount("folder/complete.txt"));

        var firstPackage = await ReadContentBytesAsync(projectedObject);
        var secondPackage = await ReadContentBytesAsync(projectedObject);

        Assert.AreEqual(1, countingStore.GetOpenReadCount("folder/incomplete.txt"));
        Assert.AreEqual(1, countingStore.GetOpenReadCount("folder/complete.txt"));
        Assert.AreEqual(
            Encoding.UTF8.GetByteCount("incomplete metadata"),
            countingStore.GetBytesRead("folder/incomplete.txt"));
        Assert.AreEqual(
            Encoding.UTF8.GetByteCount("complete metadata"),
            countingStore.GetBytesRead("folder/complete.txt"));
        CollectionAssert.AreEqual(firstPackage, secondPackage);
    }

    [TestMethod]
    public async Task ProjectAsyncUsesDeterministicFallbackWhenSourceTimestampIsMissing()
    {
        var firstTimeProvider = new ZipSourceTimeProvider(
            new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));
        var secondTimeProvider = new ZipSourceTimeProvider(
            new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero));
        using var firstServiceProvider = CreateServices(firstTimeProvider).BuildServiceProvider();
        using var secondServiceProvider = CreateServices(secondTimeProvider).BuildServiceProvider();
        var firstProjector = firstServiceProvider.GetRequiredService<IArchiveFormatProjector>();
        var secondProjector = secondServiceProvider.GetRequiredService<IArchiveFormatProjector>();
        var innerStore = new MemoryObjectStore(provideContentHash: true);
        await UploadTextAsync(innerStore, "folder/file.txt", "source content");
        var sourceStore = new MissingLastModifiedObjectStore(innerStore);
        var request = new ArchiveProjectionRequest
        (
            sourceStore,
            Policy: new FolderPolicy(ZipArchiveFormatName.Value),
            SourceDisplayName: "Photos"
        );

        var firstProjection = (await CollectProjectedObjectsAsync(
            firstProjector.ProjectAsync(request))).Single();
        var secondProjection = (await CollectProjectedObjectsAsync(
            secondProjector.ProjectAsync(request))).Single();

        Assert.AreEqual(firstProjection.RelativePath, secondProjection.RelativePath);
        CollectionAssert.AreEqual(
            await ReadContentBytesAsync(firstProjection),
            await ReadContentBytesAsync(secondProjection));

        await using var content = await firstProjection.OpenContentAsync(default);
        using var archive = new ZipArchive(content.Content, ZipArchiveMode.Read);
        var entry = archive.GetEntry("folder/file.txt");
        Assert.IsNotNull(entry);
        Assert.AreEqual(
            new DateTime(1980, 1, 1),
            entry.LastWriteTime.DateTime);
    }

    [TestMethod]
    public async Task ProjectAsyncIncludesTimestampWhenLengthIsMissing()
    {
        using var serviceProvider = CreateServices().BuildServiceProvider();
        var projector = serviceProvider.GetRequiredService<IArchiveFormatProjector>();
        var firstStore = new MemoryObjectStore(
            new ZipSourceTimeProvider(new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero)),
            provideContentHash: true);
        var secondStore = new MemoryObjectStore(
            new ZipSourceTimeProvider(new DateTimeOffset(2026, 8, 16, 13, 0, 0, TimeSpan.Zero)),
            provideContentHash: true);
        await UploadTextAsync(firstStore, "folder/file.txt", "same content");
        await UploadTextAsync(secondStore, "folder/file.txt", "same content");

        var firstProjection = (await CollectProjectedObjectsAsync(projector.ProjectAsync(new
        (
            new MissingContentLengthObjectStore(firstStore),
            Policy: new FolderPolicy(ZipArchiveFormatName.Value),
            SourceDisplayName: "Photos"
        )))).Single();
        var secondProjection = (await CollectProjectedObjectsAsync(projector.ProjectAsync(new
        (
            new MissingContentLengthObjectStore(secondStore),
            Policy: new FolderPolicy(ZipArchiveFormatName.Value),
            SourceDisplayName: "Photos"
        )))).Single();

        Assert.AreNotEqual(firstProjection.RelativePath, secondProjection.RelativePath);
    }

    [TestMethod]
    public async Task MemoryObjectStoreGetFolderItemsAsyncProvidesContentHashWhenEnabled()
    {
        var sourceStore = new MemoryObjectStore(provideContentHash: true);

        await UploadTextAsync(sourceStore, "folder/file.txt", "source content");

        var sourceObjects = new List<ArchiveObjectInfo>();
        var sourceFolderItems = sourceStore.GetFolderItemsAsync("folder");
        await foreach (var sourceFolderItem in sourceFolderItems)
        {
            if (sourceFolderItem.Object is not null)
            {
                sourceObjects.Add(sourceFolderItem.Object);
            }
        }

        var contentHash = sourceObjects.Single().ContentHash ?? string.Empty;
        Assert.AreEqual("xxh128:I5kS3t_MtgEZAbZJSW82sw", contentHash);
        Assert.AreEqual(29, contentHash.Length);
    }

    private static ServiceCollection CreateServices(TimeProvider? timeProvider = default)
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        if (timeProvider is not null)
        {
            services.AddSingleton(timeProvider);
        }

        services.AddYabtZipFormatProjector();

        return services;
    }

    private static async Task UploadTextAsync
    (
        MemoryObjectStore store,
        string key,
        string content
    )
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        await store.UploadAsync(
            key,
            stream,
            "text/plain",
            new Dictionary<string, string>(StringComparer.Ordinal));
    }

    private static async Task<IReadOnlyList<ArchiveProjectedObject>> CollectProjectedObjectsAsync
    (
        IAsyncEnumerable<ArchiveProjectedObject> projectedObjects
    )
    {
        var result = new List<ArchiveProjectedObject>();
        await foreach (var projectedObject in projectedObjects)
        {
            result.Add(projectedObject);
        }

        return result;
    }

    private static async Task<byte[]> ReadContentBytesAsync(ArchiveProjectedObject projectedObject)
    {
        await using var content = await projectedObject.OpenContentAsync(default);
        using var memory = new MemoryStream();
        await content.Content.CopyToAsync(memory);
        return memory.ToArray();
    }

    private sealed class MissingLastModifiedObjectStore
    (
        IReadOnlyObjectStore _inner,
        string? _affectedKey = default
    ) : IReadOnlyObjectStore
    {
        public Task EnsureReadyAsync(CancellationToken cancellationToken = default) =>
            _inner.EnsureReadyAsync(cancellationToken);

        public Task<ArchiveObjectContent> OpenReadAsync
        (
            string key,
            CancellationToken cancellationToken = default
        ) => _inner.OpenReadAsync(key, cancellationToken);

        public Task<bool> ExistsAsync
        (
            string key,
            CancellationToken cancellationToken = default
        ) => _inner.ExistsAsync(key, cancellationToken);

        public async IAsyncEnumerable<ArchiveFolderItem> GetFolderItemsAsync
        (
            string? folderPrefix,
            bool recursive = false,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            var folderItems = _inner.GetFolderItemsAsync(
                folderPrefix,
                recursive,
                cancellationToken);
            await foreach (var folderItem in folderItems)
            {
                if (folderItem.Object is not null &&
                    _affectedKey is not null &&
                    !string.Equals(
                        ArchiveLayout.NormalizeObjectKey(folderItem.Object.Key),
                        ArchiveLayout.NormalizeObjectKey(_affectedKey),
                        StringComparison.Ordinal))
                {
                    yield return folderItem;
                    continue;
                }

                yield return folderItem with
                {
                    Object = folderItem.Object is null ? null : folderItem.Object with
                    {
                        LastModifiedUtc = null,
                    },
                };
            }
        }
    }

    private sealed class MissingContentLengthObjectStore(IReadOnlyObjectStore _inner) : IReadOnlyObjectStore
    {
        public Task EnsureReadyAsync(CancellationToken cancellationToken = default) =>
            _inner.EnsureReadyAsync(cancellationToken);

        public Task<ArchiveObjectContent> OpenReadAsync
        (
            string key,
            CancellationToken cancellationToken = default
        ) => _inner.OpenReadAsync(key, cancellationToken);

        public Task<bool> ExistsAsync
        (
            string key,
            CancellationToken cancellationToken = default
        ) => _inner.ExistsAsync(key, cancellationToken);

        public async IAsyncEnumerable<ArchiveFolderItem> GetFolderItemsAsync
        (
            string? folderPrefix,
            bool recursive = false,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            var folderItems = _inner.GetFolderItemsAsync(
                folderPrefix,
                recursive,
                cancellationToken);
            await foreach (var folderItem in folderItems)
            {
                yield return folderItem with
                {
                    Object = folderItem.Object is null ? null : folderItem.Object with
                    {
                        ContentLength = null,
                    },
                };
            }
        }
    }

    private sealed class CountingReadOnlyObjectStore(IReadOnlyObjectStore _inner) : IReadOnlyObjectStore
    {
        private readonly Dictionary<string, int> _openReadCounts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _bytesRead = new(StringComparer.Ordinal);

        public int OpenReadCount => _openReadCounts.Values.Sum();

        public int GetOpenReadCount(string key) =>
            _openReadCounts.GetValueOrDefault(ArchiveLayout.NormalizeObjectKey(key));

        public long GetBytesRead(string key) =>
            _bytesRead.GetValueOrDefault(ArchiveLayout.NormalizeObjectKey(key));

        public Task EnsureReadyAsync(CancellationToken cancellationToken = default) =>
            _inner.EnsureReadyAsync(cancellationToken);

        public async Task<ArchiveObjectContent> OpenReadAsync
        (
            string key,
            CancellationToken cancellationToken = default
        )
        {
            var normalizedKey = ArchiveLayout.NormalizeObjectKey(key);
            _openReadCounts[normalizedKey] = GetOpenReadCount(normalizedKey) + 1;
            var content = await _inner.OpenReadAsync(key, cancellationToken);
            return new
            (
                new CountingNonSeekableReadStream(
                    content.Content,
                    bytesRead => _bytesRead[normalizedKey] =
                        GetBytesRead(normalizedKey) + bytesRead),
                content.ContentType,
                content.Metadata
            );
        }

        public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) =>
            _inner.ExistsAsync(key, cancellationToken);

        public async IAsyncEnumerable<ArchiveFolderItem> GetFolderItemsAsync
        (
            string? folderPrefix,
            bool recursive = false,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            var folderItems = _inner.GetFolderItemsAsync(
                folderPrefix,
                recursive,
                cancellationToken);

            await foreach (var folderItem in folderItems)
            {
                yield return folderItem;
            }
        }
    }

    private sealed class ZipSourceTimeProvider(DateTimeOffset _utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
