using System.Globalization;
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
public sealed class ProviderNamespaceTests
{
    [TestMethod]
    [DataRow("A", "a/child.txt", false, "")]
    [DataRow("A/first.txt", "a/second.txt", false, "")]
    [DataRow("A.txt", "a.txt", false, "")]
    [DataRow("caf\u00e9.txt", "cafe\u0301.txt", false, "")]
    [DataRow("resume/first.txt", "r\u00e9sum\u00e9/second.txt", true, "nested/live")]
    public async Task BackupRejectsProviderEquivalentPathsBeforeChangingLiveDataOrHistory
    (
        string firstPath,
        string secondPath,
        bool ignoreAccents,
        string livePrefix
    )
    {
        var source = new MemoryObjectStore();
        var target = new NamespaceStore(new FoldingNamespace(ignoreAccents));
        var layout = new ArchiveLayout(livePrefix, "history");
        await UploadAsync(source, "000-change.txt", "new bytes that must not replace the live object");
        await UploadAsync(source, firstPath, "first identity");
        await UploadAsync(source, secondPath, "second identity");
        await UploadAsync(target.Inner, layout.ToLiveObjectKey("000-change.txt"), "original live bytes");
        await UploadAsync(target.Inner, layout.ToLiveObjectKey(firstPath), "planned blocker must remain live");
        await UploadAsync(target.Inner, layout.ToLiveObjectKey("obsolete.txt"), "extra bytes that must not be historized");
        await UploadAsync(target.Inner, "history/previous/retained.txt", "prior history must remain exact");
        var before = Snapshot(target.Inner);
        using var services = CreateServices(source, target, layout).BuildServiceProvider();

        var exception = await Assert.ThrowsAsync<YabtCoreException>
        (
            () => services.GetRequiredService<IArchiveSynchronizer>().BackupAsync(new("provider-owned-source"))
        );

        StringAssert.Contains(exception.Message, "same destination path");
        Assert.AreEqual(0, target.Mutations.Count, string.Join(Environment.NewLine, target.Mutations));
        CollectionAssert.AreEqual(before, Snapshot(target.Inner));
        Assert.IsFalse(target.Inner.TryGetObject(ArchiveChangeManifest.BrotliFileName, out _));
        Assert.IsFalse(target.Inner.TryGetObject(ArchiveChangeManifest.UncompressedFileName, out _));
        Assert.IsFalse(target.Inner.TryGetObject(ArchiveChangeManifest.InvalidationMarkerFileName, out _));
    }

    [TestMethod]
    public async Task BackupUsesOrdinalProviderSemanticsEvenOnCaseInsensitiveHost()
    {
        var source = new MemoryObjectStore();
        var target = new NamespaceStore(ObjectStoreNamespace.Ordinal);
        await UploadAsync(source, "A/first.txt", "uppercase directory");
        await UploadAsync(source, "a/second.txt", "lowercase directory");
        await UploadAsync(source, "caf\u00e9.txt", "composed name");
        await UploadAsync(source, "cafe\u0301.txt", "decomposed name");
        using var services = CreateServices(source, target, ArchiveLayout.Default).BuildServiceProvider();
        var synchronizer = services.GetRequiredService<IArchiveSynchronizer>();

        var result = await synchronizer.BackupAsync(new("provider-owned-source"));
        var verification = await synchronizer.VerifyAsync(new("provider-owned-source", ByteForByte: true));

        Assert.IsTrue(result.Completed, result.Message);
        Assert.IsTrue(verification.Completed, verification.Message);
        foreach (var item in source.Snapshot())
        {
            Assert.IsTrue(target.Inner.TryGetObject(item.Key, out var archived), item.Key);
            CollectionAssert.AreEqual(item.Content.ToArray(), archived.Content.ToArray(), item.Key);
        }
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task BackupSeparatesAnExactObjectFromItsSameNamedPrefix
    (
        bool desiredDirectory,
        bool dryRun
    )
    {
        var source = new MemoryObjectStore();
        var target = new NamespaceStore(ObjectStoreNamespace.Ordinal);
        var desiredPath = desiredDirectory ? "item/keep.txt" : "item";
        var obsoletePath = desiredDirectory ? "item" : "item/keep.txt";
        var desiredContent = desiredDirectory ? "descendant payload" : "exact object";
        var obsoleteContent = desiredDirectory ? "exact object" : "descendant payload";
        await UploadAsync(source, desiredPath, desiredContent);
        await UploadAsync(target.Inner, "item", "exact object");
        await UploadAsync(target.Inner, "item/keep.txt", "descendant payload");
        var before = Snapshot(target.Inner);
        var unchangedTimestamp = target.Inner.GetObject(desiredPath).LastModifiedUtc;
        using var services = CreateServices(source, target, ArchiveLayout.Default).BuildServiceProvider();
        var synchronizer = services.GetRequiredService<IArchiveSynchronizer>();

        var result = await synchronizer.BackupAsync
        (
            new("provider-owned-source", DryRun: dryRun, ByteForByte: true)
        );

        Assert.IsTrue(result.Completed, result.Message);
        Assert.AreEqual(1, result.UnchangedCount);
        Assert.AreEqual(0, result.NewCount);
        Assert.AreEqual(0, result.ChangedCount);
        Assert.AreEqual(1, result.ExtraCount);
        if (dryRun)
        {
            Assert.AreEqual(0, target.Mutations.Count);
            CollectionAssert.AreEqual(before, Snapshot(target.Inner));
            return;
        }

        Assert.AreEqual(unchangedTimestamp, target.Inner.GetObject(desiredPath).LastModifiedUtc);
        Assert.AreEqual
        (
            desiredContent,
            Encoding.UTF8.GetString(target.Inner.GetObject(desiredPath).Content.Span)
        );
        Assert.IsFalse(target.Inner.TryGetObject(obsoletePath, out _));
        var historicalObjects = target.Inner.Snapshot()
            .Where(item => item.Key.StartsWith(".yabt-hist/", StringComparison.Ordinal))
            .ToArray();
        Assert.HasCount(1, historicalObjects);
        StringAssert.EndsWith(historicalObjects[0].Key, $"/{obsoletePath}");
        Assert.AreEqual(obsoleteContent, Encoding.UTF8.GetString(historicalObjects[0].Content.Span));
        var moves = target.Mutations.Where(mutation => mutation.StartsWith("move ", StringComparison.Ordinal)).ToArray();
        Assert.HasCount(1, moves);
        StringAssert.StartsWith(moves[0], desiredDirectory ? "move item to " : "move folder item to ");
        var verification = await synchronizer.VerifyAsync(new("provider-owned-source", ByteForByte: true));
        Assert.IsTrue(verification.Completed, verification.Message);
    }

    [TestMethod]
    [DataRow("Report.txt", "report.txt", false, "")]
    [DataRow("r\u00e9sum\u00e9.txt", "resume.txt", false, "")]
    [DataRow("R\u00e9sum\u00e9", "resume", true, "")]
    [DataRow("Report.txt", "report.txt", false, "nested/live")]
    [DataRow("R\u00e9sum\u00e9", "resume", true, "nested/live")]
    public async Task BackupHistorizesProviderEquivalentSpellingBeforeInstallingReplacement
    (
        string existingName,
        string desiredName,
        bool directory,
        string livePrefix
    )
    {
        var source = new MemoryObjectStore();
        var target = new NamespaceStore(new FoldingNamespace(ignoreAccents: true));
        var layout = new ArchiveLayout(livePrefix, ArchiveLayout.Default.HistPrefix);
        var existingPath = directory ? $"{existingName}/child.txt" : existingName;
        var desiredPath = directory ? $"{desiredName}/child.txt" : desiredName;
        var existingKey = layout.ToLiveObjectKey(existingPath);
        var desiredKey = layout.ToLiveObjectKey(desiredPath);
        await UploadAsync(source, desiredPath, "replacement payload");
        await UploadAsync(target.Inner, existingKey, "original payload");
        using var services = CreateServices(source, target, layout).BuildServiceProvider();
        var synchronizer = services.GetRequiredService<IArchiveSynchronizer>();

        var result = await synchronizer.BackupAsync(new("provider-owned-source"));

        Assert.IsTrue(result.Completed, result.Message);
        Assert.IsFalse(target.Inner.TryGetObject(existingKey, out _));
        Assert.AreEqual
        (
            "replacement payload",
            Encoding.UTF8.GetString(target.Inner.GetObject(desiredKey).Content.Span)
        );
        var historicalObjects = target.Inner.Snapshot()
            .Where(item => item.Key.StartsWith(".yabt-hist/", StringComparison.Ordinal))
            .ToArray();
        Assert.HasCount(1, historicalObjects);
        StringAssert.EndsWith(historicalObjects[0].Key, $"/{existingPath}");
        Assert.AreEqual("original payload", Encoding.UTF8.GetString(historicalObjects[0].Content.Span));
        var moveKey = layout.ToLiveObjectKey(existingName);
        var expectedMove = directory ? $"move folder {moveKey} to " : $"move {moveKey} to ";
        var moveIndex = target.Mutations.FindIndex(mutation => mutation.StartsWith(expectedMove, StringComparison.Ordinal));
        var uploadIndex = target.Mutations.IndexOf($"upload {desiredKey}");
        Assert.IsTrue(moveIndex >= 0 && uploadIndex > moveIndex, string.Join(Environment.NewLine, target.Mutations));
        var verification = await synchronizer.VerifyAsync(new("provider-owned-source", ByteForByte: true));
        Assert.IsTrue(verification.Completed, verification.Message);
    }

    [TestMethod]
    public async Task RestoreRejectsProviderEquivalentNamesBeforeMutation()
    {
        var source = new MemoryObjectStore();
        var archive = new NamespaceStore(ObjectStoreNamespace.Ordinal);
        var destination = new MemoryRestoreDestination(new FoldingNamespace(ignoreAccents: true));
        await UploadAsync(source, "r\u00e9sum\u00e9.txt", "accented name");
        await UploadAsync(source, "resume.txt", "unaccented name");
        await UploadAsync(destination.Contents.Inner, "obsolete.txt", "destination sentinel");
        using var services = CreateServices(source, archive, ArchiveLayout.Default, destination).BuildServiceProvider();
        var synchronizer = services.GetRequiredService<IArchiveSynchronizer>();
        var backup = await synchronizer.BackupAsync(new("provider-owned-source"));
        Assert.IsTrue(backup.Completed, backup.Message);
        var before = Snapshot(destination.Contents.Inner);

        var exception = await Assert.ThrowsAsync<YabtCoreException>
        (
            () => synchronizer.RestoreAsync
            (
                new("provider-owned-source", DestinationRoot: "opaque destination identity")
            )
        );

        StringAssert.Contains(exception.Message, "same destination path");
        Assert.AreEqual(0, destination.Contents.Mutations.Count);
        Assert.AreEqual(0, destination.MutationAttempts);
        CollectionAssert.AreEqual(before, Snapshot(destination.Contents.Inner));
    }

    [TestMethod]
    public async Task RestoreScopesProviderComparisonRulesToTheDestinationLivePrefix()
    {
        var source = new MemoryObjectStore();
        var archive = new NamespaceStore(ObjectStoreNamespace.Ordinal);
        var destination = new MemoryRestoreDestination(new LiveSensitiveNamespace());
        await UploadAsync(source, "A/first.txt", "uppercase directory");
        await UploadAsync(source, "a/second.txt", "lowercase directory");
        using var services = CreateServices(source, archive, ArchiveLayout.Default, destination).BuildServiceProvider();
        var descriptor = new BackupRootDescriptor
        (
            BackupRootDescriptor.ExpectedDocumentType,
            BackupRootDescriptor.ExpectedSchemaVersion,
            "provider-namespace-tests",
            DateTimeOffset.UnixEpoch,
            new("live", "history"),
            [new("target", FixedStoreResolver.Kind)]
        );
        await using var descriptorContent = new MemoryStream();
        await services.GetRequiredService<IBackupRootSerializer>().WriteAsync(descriptor, descriptorContent);
        descriptorContent.Position = 0;
        await destination.Contents.Inner.UploadAsync
        (
            BackupRootFileNames.Primary,
            descriptorContent,
            "application/json",
            new Dictionary<string, string>()
        );
        var synchronizer = services.GetRequiredService<IArchiveSynchronizer>();
        var backup = await synchronizer.BackupAsync(new("provider-owned-source"));
        Assert.IsTrue(backup.Completed, backup.Message);
        Assert.IsTrue(destination.Namespace.PathComparer.Equals("A", "a"));
        var before = Snapshot(destination.Contents.Inner);

        var restore = await synchronizer.RestoreAsync
        (
            new
            (
                "provider-owned-source",
                DryRun: true,
                ByteForByte: true,
                DestinationRoot: "opaque destination identity"
            )
        );

        Assert.IsTrue(restore.Completed, restore.Message);
        Assert.AreEqual(2, restore.NewCount);
        Assert.AreEqual(0, destination.Contents.Mutations.Count);
        Assert.AreEqual(0, destination.MutationAttempts);
        CollectionAssert.AreEqual(before, Snapshot(destination.Contents.Inner));
    }

    [TestMethod]
    public async Task RestoreRejectsDestinationLayoutChangedWhileAcquiringItsMutationLock()
    {
        var source = new MemoryObjectStore();
        var archive = new NamespaceStore(ObjectStoreNamespace.Ordinal);
        var destination = new MemoryRestoreDestination(new LiveSensitiveNamespace());
        await UploadAsync(source, "new.txt", "archive payload must not be restored under a changed prefix");
        await UploadAsync(destination.Contents.Inner, "live/retained.txt", "original live sentinel");
        await UploadAsync(destination.Contents.Inner, "other/retained.txt", "replacement live sentinel");
        using var services = CreateServices(source, archive, ArchiveLayout.Default, destination).BuildServiceProvider();
        var serializer = services.GetRequiredService<IBackupRootSerializer>();
        var descriptor = new BackupRootDescriptor
        (
            BackupRootDescriptor.ExpectedDocumentType,
            BackupRootDescriptor.ExpectedSchemaVersion,
            "provider-namespace-tests",
            DateTimeOffset.UnixEpoch,
            new("live", "history"),
            [new("target", FixedStoreResolver.Kind)]
        );
        await using var initialContent = new MemoryStream();
        await serializer.WriteAsync(descriptor, initialContent);
        initialContent.Position = 0;
        await destination.Contents.Inner.UploadAsync
        (
            BackupRootFileNames.Primary,
            initialContent,
            "application/json",
            new Dictionary<string, string>()
        );
        await using var changedContent = new MemoryStream();
        await serializer.WriteAsync(descriptor with { Layout = new("other", "history") }, changedContent);
        var changedDescriptorBytes = changedContent.ToArray();
        var descriptorChangeCount = 0;
        destination.Contents.OnLockAcquired = async () =>
        {
            var previous = destination.Contents.Inner.GetObject(BackupRootFileNames.Primary);
            await using var replacement = new MemoryStream(changedDescriptorBytes, writable: false);
            Assert.IsTrue
            (
                await destination.Contents.Inner.TryReplaceIfContentHashMatchesAsync
                (
                    BackupRootFileNames.Primary,
                    ArchiveHash.Compute(previous.Content.Span),
                    replacement,
                    "application/json",
                    new Dictionary<string, string>()
                )
            );
            descriptorChangeCount++;
        };
        var synchronizer = services.GetRequiredService<IArchiveSynchronizer>();
        var backup = await synchronizer.BackupAsync(new("provider-owned-source"));
        Assert.IsTrue(backup.Completed, backup.Message);

        var exception = await Assert.ThrowsAsync<YabtSyncException>
        (
            () => synchronizer.RestoreAsync
            (
                new("provider-owned-source", DestinationRoot: "opaque destination identity")
            )
        );

        StringAssert.Contains(exception.Message, "destination layout changed");
        Assert.AreEqual(1, descriptorChangeCount);
        Assert.AreEqual(0, destination.Contents.Mutations.Count);
        Assert.AreEqual(0, destination.MutationAttempts);
        CollectionAssert.AreEqual
        (
            new[] { BackupRootFileNames.Primary, "live/retained.txt", "other/retained.txt" },
            destination.Contents.Inner.Snapshot().Select(item => item.Key).ToArray()
        );
        CollectionAssert.AreEqual
        (
            changedDescriptorBytes,
            destination.Contents.Inner.GetObject(BackupRootFileNames.Primary).Content.ToArray()
        );
        Assert.AreEqual
        (
            "original live sentinel",
            Encoding.UTF8.GetString(destination.Contents.Inner.GetObject("live/retained.txt").Content.Span)
        );
        Assert.AreEqual
        (
            "replacement live sentinel",
            Encoding.UTF8.GetString(destination.Contents.Inner.GetObject("other/retained.txt").Content.Span)
        );
    }

    private static ServiceCollection CreateServices
    (
        IObjectStore source,
        IObjectStore target,
        ArchiveLayout layout,
        IRestoreDestination? destination = null
    )
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddYabtMetadata();
        services.AddYabtMirrorFormatHandler();
        services.AddYabtSync();
        services.AddSingleton<IArchiveLocationResolver>(new MemoryLocationResolver(source, layout));
        services.AddSingleton<IBackupRootStoreResolver>(new FixedStoreResolver(target));
        services.AddSingleton<IRestoreDestinationFactory>(new ReplayOnlyDestinationFactory(destination));
        return services;
    }

    private static async Task UploadAsync(MemoryObjectStore store, string key, string value)
    {
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes(value));
        await store.UploadAsync(key, content, "text/plain", new Dictionary<string, string>());
    }

    private static string[] Snapshot(MemoryObjectStore store) => store.Snapshot()
        .Select(item => $"{item.Key}|{Convert.ToBase64String(item.Content.Span)}|{item.LastModifiedUtc:O}")
        .ToArray();

    private sealed class FoldingNamespace(bool ignoreAccents) : ObjectStoreNamespace
    {
        public override StringComparer PathComparer { get; } = new FoldingComparer(ignoreAccents);

        public override void ValidatePath(ObjectPath path)
        {
        }
    }

    private sealed class FoldingComparer(bool _ignoreAccents) : StringComparer
    {
        public override int Compare(string? x, string? y) =>
            StringComparer.OrdinalIgnoreCase.Compare(Fold(x), Fold(y));

        public override bool Equals(string? x, string? y) =>
            StringComparer.OrdinalIgnoreCase.Equals(Fold(x), Fold(y));

        public override int GetHashCode(string obj) => StringComparer.OrdinalIgnoreCase.GetHashCode(Fold(obj)!);

        private string? Fold(string? value)
        {
            if (value is null) { return null; }
            var decomposed = value.Normalize(NormalizationForm.FormD);
            return _ignoreAccents ?
                string.Concat(decomposed.Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)) :
                decomposed;
        }
    }

    private sealed class LiveSensitiveNamespace : ObjectStoreNamespace
    {
        public override StringComparer PathComparer { get; } = new LiveSensitiveComparer();

        public override void ValidatePath(ObjectPath path)
        {
        }
    }

    private sealed class LiveSensitiveComparer : StringComparer
    {
        public override int Compare(string? x, string? y)
        {
            if (x is null || y is null) { return StringComparer.Ordinal.Compare(x, y); }
            var firstLive = x.StartsWith("live/", StringComparison.OrdinalIgnoreCase);
            var secondLive = y.StartsWith("live/", StringComparison.OrdinalIgnoreCase);
            return firstLive && secondLive ?
                StringComparer.Ordinal.Compare(x[5..], y[5..]) :
                StringComparer.OrdinalIgnoreCase.Compare(x, y);
        }

        public override bool Equals(string? x, string? y) => Compare(x, y) == 0;

        public override int GetHashCode(string obj) => obj.StartsWith("live/", StringComparison.OrdinalIgnoreCase) ?
            StringComparer.Ordinal.GetHashCode(obj[5..]) : StringComparer.OrdinalIgnoreCase.GetHashCode(obj);
    }

    private sealed class MemoryLocationResolver(IObjectStore _source, ArchiveLayout _layout) : IArchiveLocationResolver
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
                BackupRootDescriptor.ExpectedDocumentType,
                BackupRootDescriptor.ExpectedSchemaVersion,
                "provider-namespace-tests",
                DateTimeOffset.UnixEpoch,
                _layout,
                [new("target", FixedStoreResolver.Kind)]
            );
            return Task.FromResult
            (
                new ArchiveSourceLocation
                (
                    new("opaque provider root", descriptor),
                    ObjectPath.Root,
                    ObjectPath.Root,
                    "selected source",
                    _source
                )
            );
        }

        public void ValidateBackupLocations(ArchiveSourceLocation source, IObjectStore targetStore)
        {
        }

        public void ConfigureSourceTraversal(ArchiveSourceLocation source, IObjectStore targetStore)
        {
        }

        public Task<FolderPolicy> ReadPolicyAsync
        (
            ArchiveSourceLocation source,
            ObjectPath prefix,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(FolderPolicy.Default);

        public void ValidateRestoreLocations
        (
            ArchiveSourceLocation descriptorLocation,
            IRestoreDestination destination,
            IObjectStore archiveStore
        )
        {
        }
    }

    private sealed class FixedStoreResolver(IObjectStore _target) : IBackupRootStoreResolver
    {
        public const string Kind = "synthetic-namespace";

        public string StoreKind => Kind;

        public IObjectStore ResolveStore(BackupRootStore store, string descriptorRootPath) => _target;
    }

    private sealed class ReplayOnlyDestinationFactory(IRestoreDestination? _destination) : IRestoreDestinationFactory
    {
        public IRestoreDestination Create(string rootPath, IObjectStore? store = null) =>
            _destination ?? throw new NotSupportedException();

        public Stream CreateComparisonReplayStream() => new MemoryStream();
    }

    private sealed class MemoryRestoreDestination(IObjectStoreNamespace objectNamespace) : IRestoreDestination
    {
        public NamespaceStore Contents { get; } = new(objectNamespace);
        public int MutationAttempts { get; private set; }
        public string RootPath => "opaque destination identity";
        public IObjectStore Store => Contents;
        public IObjectStoreNamespace Namespace => Contents.Namespace;

        public Task EnsureSafeAsync
        (
            string? livePrefix,
            string? historyPrefix,
            CancellationToken cancellationToken = default
        ) => Task.CompletedTask;

        public void ValidateRelativePath(string relativePath) => Namespace.ValidatePath(ObjectPath.Parse(relativePath));

        public bool DirectoryExists(ObjectPath path) => path.IsRoot || Contents.Inner.Snapshot()
            .Any(item => ObjectPath.Parse(item.Key).Parent.IsUnder(path));

        public Task CreateDirectoryAsync(string? relativePath, CancellationToken cancellationToken = default)
        {
            MutationAttempts++;
            throw new InvalidOperationException("Preflight and dry-run tests must not create directories.");
        }

        public Task<IObjectStoreStagingArea> CreateStagingAreaAsync(CancellationToken cancellationToken = default)
        {
            MutationAttempts++;
            throw new InvalidOperationException("Preflight and dry-run tests must not create staging.");
        }

        public Task CommitStagedFileAsync
        (
            string relativePath,
            string stagingToken,
            DateTimeOffset? lastModifiedUtc,
            CancellationToken cancellationToken = default
        )
        {
            MutationAttempts++;
            throw new InvalidOperationException("Preflight and dry-run tests must not commit files.");
        }
    }

    private sealed class NamespaceStore(IObjectStoreNamespace _namespace) : IArchiveMutableObjectStore
    {
        public MemoryObjectStore Inner { get; } = new();
        public List<string> Mutations { get; } = [];
        public IObjectStoreNamespace Namespace => _namespace;
        public Func<Task>? OnLockAcquired { get; set; }

        public Task EnsureReadyAsync(CancellationToken cancellationToken = default) => Inner.EnsureReadyAsync(cancellationToken);

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Inner.InitializeAsync(cancellationToken);

        public Task<bool> IsIndependentMaterializationAsync
        (
            string key,
            CancellationToken cancellationToken = default
        ) => Inner.IsIndependentMaterializationAsync(key, cancellationToken);

        public Task<ArchiveObjectContent> OpenReadAsync(string key, CancellationToken cancellationToken = default) =>
            Inner.OpenReadAsync(key, cancellationToken);

        public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) =>
            Inner.ExistsAsync(key, cancellationToken);

        public IAsyncEnumerable<ArchiveFolderItem> GetFolderItemsAsync
        (
            string? folderPrefix,
            bool recursive = false,
            CancellationToken cancellationToken = default
        ) => Inner.GetFolderItemsAsync(folderPrefix, recursive, cancellationToken);

        public Task UploadAsync
        (
            string key,
            Stream content,
            string contentType,
            IReadOnlyDictionary<string, string> metadata,
            CancellationToken cancellationToken = default
        )
        {
            Mutations.Add($"upload {key}");
            return Inner.UploadAsync(key, content, contentType, metadata, cancellationToken);
        }

        public Task MoveAsync(string sourceKey, string destinationKey, CancellationToken cancellationToken = default)
        {
            Mutations.Add($"move {sourceKey} to {destinationKey}");
            return Inner.MoveAsync(sourceKey, destinationKey, cancellationToken);
        }

        public Task MoveFolderAsync(string sourcePrefix, string destinationPrefix, CancellationToken cancellationToken = default)
        {
            Mutations.Add($"move folder {sourcePrefix} to {destinationPrefix}");
            return Inner.MoveFolderAsync(sourcePrefix, destinationPrefix, cancellationToken);
        }

        public Task<bool> TryReplaceIfContentHashMatchesAsync
        (
            string key,
            string expectedContentHash,
            Stream replacementContent,
            string contentType,
            IReadOnlyDictionary<string, string> metadata,
            CancellationToken cancellationToken = default
        )
        {
            Mutations.Add($"replace {key}");
            return Inner.TryReplaceIfContentHashMatchesAsync
            (
                key,
                expectedContentHash,
                replacementContent,
                contentType,
                metadata,
                cancellationToken
            );
        }

        public Task<bool> TryDeleteIfContentHashMatchesAsync
        (
            string key,
            string expectedContentHash,
            CancellationToken cancellationToken = default
        )
        {
            Mutations.Add($"delete {key}");
            return Inner.TryDeleteIfContentHashMatchesAsync(key, expectedContentHash, cancellationToken);
        }

        public async Task<IArchiveMutationLock> AcquireArchiveMutationLockAsync(CancellationToken cancellationToken = default)
        {
            var mutationLock = await ((IArchiveMutationLockProvider)Inner).AcquireArchiveMutationLockAsync(cancellationToken);
            try
            {
                if (OnLockAcquired is not null) { await OnLockAcquired(); }
                return mutationLock;
            }
            catch (Exception)
            {
                await mutationLock.DisposeAsync();
                throw;
            }
        }
    }
}
