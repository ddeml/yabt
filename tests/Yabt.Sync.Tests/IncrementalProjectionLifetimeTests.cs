using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yabt.Core;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.Metadata;
using Yabt.Tests;

namespace Yabt.Sync.Tests;

[TestClass]
public sealed class IncrementalProjectionLifetimeTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BackupRetainsProjectionUntilArtifactsInDifferentFolderUnitsAreCommitted
    (
        bool explicitMirrorParent
    )
    {
        var source = new MemoryObjectStore();
        var target = new MemoryObjectStore();
        var parent = explicitMirrorParent ? "outer/" : string.Empty;
        var firstPath = parent + "pack/a.bin";
        var secondPath = parent + "pack/deep/b.bin";
        await WriteAsync(source, "selected/" + parent + "pack/.yabt-policy.json", "{\"format\":\"split-package\"}");
        if (explicitMirrorParent)
        {
            await WriteAsync(source, "selected/outer/.yabt-policy.json", "{\"format\":\"mirror\"}");
        }
        var handler = new SplitPackageHandler
        (
            beforeSecondRead: () => Assert.IsTrue
            (
                target.TryGetObject(firstPath, out _),
                "The first folder's artifact should be committed before preparing the descendant artifact."
            ),
            onDispose: () =>
            {
                Assert.IsTrue(target.TryGetObject(firstPath, out _));
                Assert.IsTrue
                (
                    target.TryGetObject(secondPath, out _),
                    "Releasing a projection after only its first folder would invalidate its remaining artifacts."
                );
            }
        );
        using var provider = CreateServices(source, target, handler);

        var result = await provider.GetRequiredService<IArchiveSynchronizer>().BackupAsync(new("source"));

        Assert.IsTrue(result.Completed, result.Message);
        Assert.AreEqual("first artifact", Encoding.UTF8.GetString(target.GetObject(firstPath).Content.Span));
        Assert.AreEqual("second artifact", Encoding.UTF8.GetString(target.GetObject(secondPath).Content.Span));
        Assert.AreEqual(2, handler.ReadCount);
        Assert.AreEqual(1, handler.DisposeCount);
        Assert.IsTrue(target.TryGetObject(ArchiveChangeManifest.BrotliFileName, out _));
    }

    [TestMethod]
    public async Task BackupRejectsForeignNestedArtifactsBeforeExposingTheirFolder()
    {
        var source = new MemoryObjectStore();
        var target = new MemoryObjectStore();
        await WriteAsync(source, "selected/pack/.yabt-policy.json", "{\"format\":\"split-package\"}");
        var handler = new SplitPackageHandler
        (
            beforeSecondRead: () => Assert.Fail("Invalid projection outputs must not be opened."),
            onDispose: () => { },
            outputFolder: "elsewhere"
        );
        using var provider = CreateServices(source, target, handler);

        var exception = await Assert.ThrowsExactlyAsync<YabtSyncException>
        (
            () => provider.GetRequiredService<IArchiveSynchronizer>().BackupAsync(new("source"))
        );

        StringAssert.Contains(exception.Message, "outside its registered folder scope");
        Assert.AreEqual(0, handler.ReadCount);
        Assert.AreEqual(1, handler.DisposeCount);
        Assert.IsEmpty(target.Snapshot(), "An invalid projection must not publish payloads or a successful manifest.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BackupRejectsProjectedFilePrefixCollisionsBeforeExposingTheirFolder(bool descendantFirst)
    {
        var source = new MemoryObjectStore();
        var target = new MemoryObjectStore();
        await WriteAsync(source, "selected/pack/.yabt-policy.json", "{\"format\":\"split-package\"}");
        var handler = new SplitPackageHandler
        (
            beforeSecondRead: () => Assert.Fail("Conflicting projection outputs must not be opened."),
            onDispose: () => { },
            filePrefixCollision: true,
            descendantFirst: descendantFirst
        );
        using var provider = CreateServices(source, target, handler);

        var exception = await Assert.ThrowsExactlyAsync<YabtCoreException>
        (
            () => provider.GetRequiredService<IArchiveSynchronizer>().BackupAsync(new("source"))
        );

        StringAssert.Contains(exception.Message, "identify the same destination path");
        Assert.AreEqual(0, handler.ReadCount);
        Assert.AreEqual(1, handler.DisposeCount);
        Assert.IsEmpty(target.Snapshot(), "Neither side of a file/prefix collision may be silently omitted.");
    }

    private static ServiceProvider CreateServices
    (
        MemoryObjectStore source,
        MemoryObjectStore target,
        IArchiveFormatHandler handler
    )
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddYabtMetadata();
        services.AddYabtMirrorFormatHandler();
        services.AddSingleton(handler);
        services.AddYabtSync();
        services.AddSingleton<IArchiveLocationResolver>(new LocationResolver(source));
        services.AddSingleton<IBackupRootStoreResolver>(new StoreResolver(target));
        services.AddSingleton<IRestoreDestinationFactory>(new ReplayFactory());
        return services.BuildServiceProvider();
    }

    private static async Task WriteAsync(MemoryObjectStore store, string path, string text)
    {
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes(text));
        await store.UploadAsync(path, content, "application/octet-stream", new Dictionary<string, string>());
    }

    private sealed class SplitPackageHandler
    (
        Action beforeSecondRead,
        Action onDispose,
        string outputFolder = "pack",
        bool filePrefixCollision = false,
        bool descendantFirst = false
    ) : IArchiveFormatHandler
    {
        public string FormatName => "split-package";

        public bool ProjectsBesideSourceFolder => true;

        public int ReadCount { get; private set; }

        public int DisposeCount { get; private set; }

        public bool CanRestoreArtifact(ArchiveProjectedObject artifact) => false;

        public Task<ArchiveRestoreProjection> ProjectRestoreAsync
        (
            ArchiveRestoreRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public ArchiveBackupProjection ProjectBackupAsync
        (
            ArchiveProjectionRequest request,
            CancellationToken cancellationToken = default
        ) => new(Objects(request, cancellationToken), new Owner(this, onDispose));

        private async IAsyncEnumerable<ArchiveProjectedObject> Objects
        (
            ArchiveProjectionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            var provenance = new ArchiveProjectionProvenance
            (
                request.LogicalPath!, FormatName, 1, "split-v1:1", "first"
            );
            var first = Create($"{outputFolder}/a.bin", "first artifact", provenance);
            var secondPath = filePrefixCollision ? $"{outputFolder}/a.bin/deep.bin" : $"{outputFolder}/deep/b.bin";
            var second = Create(secondPath, "second artifact", provenance with { ArtifactRole = "second" });
            yield return descendantFirst ? second : first;
            yield return descendantFirst ? first : second;
            await Task.CompletedTask;
        }

        private ArchiveProjectedObject Create
        (
            string path,
            string text,
            ArchiveProjectionProvenance provenance
        )
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            return new
            (
                path,
                cancellationToken =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Assert.AreEqual(0, DisposeCount, "The shared artifact owner must remain alive until every unit finishes.");
                    ReadCount++;
                    if (provenance.ArtifactRole == "second") { beforeSecondRead(); }
                    return Task.FromResult(new ArchiveObjectContent(new MemoryStream(bytes, writable: false)));
                },
                bytes.Length,
                DateTimeOffset.UnixEpoch,
                ArchiveHash.Compute(bytes),
                "split-v1:1",
                provenance
            );
        }

        private sealed class Owner(SplitPackageHandler _handler, Action _onDispose) : IAsyncDisposable
        {
            private bool _disposed;

            public ValueTask DisposeAsync()
            {
                if (_disposed) { return ValueTask.CompletedTask; }
                _disposed = true;
                _handler.DisposeCount++;
                _onDispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class LocationResolver(IObjectStore _source) : IArchiveLocationResolver
    {
        public Task<ArchiveSourceLocation> LocateSourceAsync
        (
            string input,
            bool requireRoot = false,
            CancellationToken cancellationToken = default
        )
        {
            var descriptor = new BackupRootDescriptor
            (
                BackupRootDescriptor.ExpectedDocumentType, BackupRootDescriptor.ExpectedSchemaVersion,
                "incremental-projection-lifetime", DateTimeOffset.UnixEpoch, ArchiveLayout.Default,
                [new("target", StoreResolver.Kind)]
            );
            return Task.FromResult(new ArchiveSourceLocation
            (
                new("opaque source root", descriptor), ObjectPath.Parse("selected"),
                ObjectPath.Parse("selected"), "source", _source
            ));
        }

        public Task<FolderPolicy> ReadPolicyAsync
        (
            ArchiveSourceLocation source,
            ObjectPath prefix,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(prefix.Name == "pack" ? new FolderPolicy("split-package") : FolderPolicy.Default);

        public void ValidateBackupLocations(ArchiveSourceLocation source, IObjectStore targetStore) { }

        public void ConfigureSourceTraversal(ArchiveSourceLocation source, IObjectStore targetStore) { }

        public void ValidateTemporaryStorageLocations
        (
            string temporaryDirectory,
            ArchiveSourceLocation? source,
            IObjectStore archiveStore,
            IRestoreDestination? destination = null
        ) { }

        public void ValidateRestoreLocations
        (
            ArchiveSourceLocation descriptorLocation,
            IRestoreDestination destination,
            IObjectStore archiveStore
        ) => throw new NotSupportedException();
    }

    private sealed class StoreResolver(IObjectStore _target) : IBackupRootStoreResolver
    {
        public const string Kind = "incremental-projection-lifetime";

        public string StoreKind => Kind;

        public IObjectStore ResolveStore(BackupRootStore store, string descriptorRootPath) => _target;
    }

    private sealed class ReplayFactory : IRestoreDestinationFactory
    {
        public IRestoreDestination Create(string rootPath, IObjectStore? store = null) =>
            throw new NotSupportedException();

        public Stream CreateComparisonReplayStream() => new MemoryStream();
    }
}
