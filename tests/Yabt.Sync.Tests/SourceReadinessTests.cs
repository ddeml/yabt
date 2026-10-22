using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.FileSystem;
using Yabt.Format.Mirror;
using Yabt.Format.Zip;
using Yabt.Metadata;

namespace Yabt.Sync.Tests;

[TestClass]
public sealed class SourceReadinessTests
{
    [TestMethod]
    [DataRow(MirrorArchiveFormatName.Value, "backup")]
    [DataRow(MirrorArchiveFormatName.Value, "dry-run")]
    [DataRow(MirrorArchiveFormatName.Value, "verify")]
    [DataRow(ZipArchiveFormatName.Value, "backup")]
    [DataRow(ZipArchiveFormatName.Value, "dry-run")]
    [DataRow(ZipArchiveFormatName.Value, "verify")]
    public async Task DisappearingSourceRootIsNotRecreatedOrReconciledAsEmpty
    (
        string format,
        string operation
    )
    {
        using var fixture = await SourceReadinessFixture.CreateAsync(format);
        await File.WriteAllTextAsync(Path.Combine(fixture.SourceRoot, "important.txt"), "preserved archive content");
        var initialBackup = await fixture.Synchronizer.BackupAsync(new(fixture.SourceRoot));
        Assert.IsTrue(initialBackup.Completed, initialBackup.Message);
        var archiveBefore = SnapshotArchive(fixture.ArchiveRoot);

        // The descriptor and selected policy have been read successfully. Disappear at the
        // boundary immediately before synchronization checks source readiness, without a race.
        fixture.LocationResolver.AfterPolicyRead = () => fixture.MoveAside(fixture.SourceRoot);

        await Assert.ThrowsAsync<YabtFileSystemException>
        (
            () => RunAsync(fixture, operation, fixture.SourceRoot)
        );

        Assert.IsTrue(fixture.LocationResolver.CallbackInvoked);
        Assert.IsFalse(Directory.Exists(fixture.SourceRoot), "Readiness must never recreate the missing source.");
        CollectionAssert.AreEqual(archiveBefore, SnapshotArchive(fixture.ArchiveRoot));
    }

    [TestMethod]
    [DataRow(MirrorArchiveFormatName.Value, "backup", false)]
    [DataRow(MirrorArchiveFormatName.Value, "dry-run", false)]
    [DataRow(MirrorArchiveFormatName.Value, "verify", false)]
    [DataRow(ZipArchiveFormatName.Value, "backup", false)]
    [DataRow(ZipArchiveFormatName.Value, "dry-run", false)]
    [DataRow(ZipArchiveFormatName.Value, "verify", false)]
    [DataRow(MirrorArchiveFormatName.Value, "backup", true)]
    [DataRow(MirrorArchiveFormatName.Value, "dry-run", true)]
    [DataRow(MirrorArchiveFormatName.Value, "verify", true)]
    [DataRow(ZipArchiveFormatName.Value, "backup", true)]
    [DataRow(ZipArchiveFormatName.Value, "dry-run", true)]
    [DataRow(ZipArchiveFormatName.Value, "verify", true)]
    public async Task MissingSelectedFolderOrLivePrefixIsNotReconciledAsEmpty
    (
        string format,
        string operation,
        bool explicitLivePrefix
    )
    {
        var layout = explicitLivePrefix ? new ArchiveLayout("live", "hist") : ArchiveLayout.Default;
        using var fixture = await SourceReadinessFixture.CreateAsync(format, layout);
        var payloadRoot = Path.Combine(fixture.SourceRoot, explicitLivePrefix ? "live" : "selected");
        var selectedRoot = explicitLivePrefix ? fixture.SourceRoot : payloadRoot;
        Directory.CreateDirectory(payloadRoot);
        if (!explicitLivePrefix)
        {
            await WritePolicyAsync(payloadRoot, format);
        }
        await File.WriteAllTextAsync(Path.Combine(payloadRoot, "important.txt"), "preserved selected content");
        var initialBackup = await fixture.Synchronizer.BackupAsync(new(selectedRoot));
        Assert.IsTrue(initialBackup.Completed, initialBackup.Message);
        var archiveBefore = SnapshotArchive(fixture.ArchiveRoot);
        fixture.MoveAside(payloadRoot);

        await Assert.ThrowsAsync<YabtFileSystemException>
        (
            () => RunAsync(fixture, operation, selectedRoot)
        );

        Assert.IsTrue(Directory.Exists(fixture.SourceRoot));
        Assert.IsFalse(Directory.Exists(payloadRoot));
        CollectionAssert.AreEqual(archiveBefore, SnapshotArchive(fixture.ArchiveRoot));
    }

    [TestMethod]
    [DataRow(MirrorArchiveFormatName.Value)]
    [DataRow(ZipArchiveFormatName.Value)]
    public async Task DisappearingListedChildFailsWithoutChangingArchivedContents(string format)
    {
        using var fixture = await SourceReadinessFixture.CreateAsync(format);
        var childRoot = Path.Combine(fixture.SourceRoot, "child");
        Directory.CreateDirectory(childRoot);
        await WritePolicyAsync(childRoot, format);
        await File.WriteAllTextAsync(Path.Combine(childRoot, "important.txt"), "preserved child content");
        var initialBackup = await fixture.Synchronizer.BackupAsync(new(fixture.SourceRoot));
        Assert.IsTrue(initialBackup.Completed, initialBackup.Message);
        var archiveBefore = SnapshotArchive(fixture.ArchiveRoot);

        // The source root has passed readiness and its listing already yielded this child.
        // Reading the child's policy is the last controlled boundary before its own listing.
        fixture.LocationResolver.AfterPolicyReadPrefix = ObjectPath.Parse("child");
        fixture.LocationResolver.AfterPolicyRead = () => fixture.MoveAside(childRoot);

        await Assert.ThrowsAsync<YabtFileSystemException>
        (
            () => fixture.Synchronizer.BackupAsync(new(fixture.SourceRoot))
        );

        Assert.IsTrue(fixture.LocationResolver.CallbackInvoked);
        Assert.IsTrue(Directory.Exists(fixture.SourceRoot));
        Assert.IsFalse(Directory.Exists(childRoot));
        CollectionAssert.AreEqual(archiveBefore, SnapshotArchive(fixture.ArchiveRoot));
    }

    [TestMethod]
    [DataRow(MirrorArchiveFormatName.Value)]
    [DataRow(ZipArchiveFormatName.Value)]
    public async Task ExistingEmptyLiveDirectoryCanBeBackedUpAndVerified(string format)
    {
        using var fixture = await SourceReadinessFixture.CreateAsync(format, new("live", "hist"));
        var liveRoot = Path.Combine(fixture.SourceRoot, "live");
        Directory.CreateDirectory(liveRoot);

        var backup = await fixture.Synchronizer.BackupAsync(new(fixture.SourceRoot));
        var verification = await fixture.Synchronizer.VerifyAsync(new(fixture.SourceRoot, ByteForByte: true));

        Assert.IsTrue(backup.Completed, backup.Message);
        Assert.IsTrue(verification.Completed, verification.Message);
        Assert.IsTrue(Directory.Exists(liveRoot));
        Assert.IsFalse(Directory.EnumerateFileSystemEntries(liveRoot).Any(), "Reading must leave the empty source empty.");
        Assert.IsTrue(File.Exists(Path.Combine(fixture.ArchiveRoot, BackupRootFileNames.Primary)));
        Assert.IsTrue(File.Exists(Path.Combine(fixture.ArchiveRoot, ArchiveChangeManifest.BrotliFileName)));
    }

    [TestMethod]
    [DataRow(MirrorArchiveFormatName.Value, "backup")]
    [DataRow(MirrorArchiveFormatName.Value, "dry-run")]
    [DataRow(MirrorArchiveFormatName.Value, "verify")]
    [DataRow(ZipArchiveFormatName.Value, "backup")]
    [DataRow(ZipArchiveFormatName.Value, "dry-run")]
    [DataRow(ZipArchiveFormatName.Value, "verify")]
    public async Task OnlyMutatingBackupInitializesMissingTarget(string format, string operation)
    {
        using var fixture = await SourceReadinessFixture.CreateAsync(format);
        await File.WriteAllTextAsync(Path.Combine(fixture.SourceRoot, "important.txt"), "new archive content");
        Assert.IsFalse(Directory.Exists(fixture.ArchiveRoot));

        var result = await RunAsync(fixture, operation, fixture.SourceRoot);

        Assert.AreEqual(operation != "verify", result.Completed, result.Message);
        Assert.IsTrue(result.NewCount > 0);
        Assert.AreEqual(operation == "backup", Directory.Exists(fixture.ArchiveRoot));
        if (operation == "backup")
        {
            Assert.IsTrue(File.Exists(Path.Combine(fixture.ArchiveRoot, BackupRootFileNames.Primary)));
            Assert.IsTrue(File.Exists(Path.Combine(fixture.ArchiveRoot, ArchiveChangeManifest.BrotliFileName)));
            var verification = await fixture.Synchronizer.VerifyAsync(new(fixture.SourceRoot, ByteForByte: true));
            Assert.IsTrue(verification.Completed, verification.Message);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DeduplicationRefusesMissingArchiveWithoutInitializingIt(bool dryRun)
    {
        using var fixture = await SourceReadinessFixture.CreateAsync(MirrorArchiveFormatName.Value);
        var deduplicator = fixture.Services.GetRequiredService<IHistoryDeduplicator>();

        var exception = await Assert.ThrowsAsync<YabtSyncException>
        (
            () => deduplicator.DeduplicateAsync(new(fixture.SourceRoot, DryRun: dryRun))
        );

        Assert.IsInstanceOfType<YabtFileSystemException>(exception.InnerException);
        Assert.IsFalse(Directory.Exists(fixture.ArchiveRoot));
    }

    private static Task<SyncRunResult> RunAsync
    (
        SourceReadinessFixture fixture,
        string operation,
        string selectedRoot
    ) => operation switch
    {
        "backup" => fixture.Synchronizer.BackupAsync(new(selectedRoot)),
        "dry-run" => fixture.Synchronizer.BackupAsync(new(selectedRoot, DryRun: true)),
        "verify" => fixture.Synchronizer.VerifyAsync(new(selectedRoot)),
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    private static string[] SnapshotArchive(string archiveRoot) => Directory
        .EnumerateFileSystemEntries(archiveRoot, "*", SearchOption.AllDirectories)
        .Where(path => !Path.GetRelativePath(archiveRoot, path).Split(Path.DirectorySeparatorChar)
            .Contains(ArchiveInternalFolderNames.TemporaryUploads, StringComparer.Ordinal))
        .Order(StringComparer.Ordinal)
        .Select
        (
            path => Directory.Exists(path) ?
                $"directory|{Path.GetRelativePath(archiveRoot, path)}" :
                string.Join
                (
                    "|",
                    Path.GetRelativePath(archiveRoot, path),
                    File.GetLastWriteTimeUtc(path).Ticks,
                    Convert.ToBase64String(File.ReadAllBytes(path))
                )
        )
        .ToArray();

    private static Task WritePolicyAsync(string rootPath, string format) => File.WriteAllTextAsync
    (
        Path.Combine(rootPath, FolderPolicyFileNames.Primary),
        JsonSerializer.Serialize(new { format })
    );

    private sealed class ReadinessLocationResolver(IArchiveLocationResolver _inner) : IArchiveLocationResolver
    {
        public Action? AfterPolicyRead { get; set; }

        public ObjectPath? AfterPolicyReadPrefix { get; set; }

        public bool CallbackInvoked { get; private set; }

        public Task<ArchiveSourceLocation> LocateSourceAsync
        (
            string input,
            bool requireRoot = false,
            CancellationToken cancellationToken = default
        ) => _inner.LocateSourceAsync(input, requireRoot, cancellationToken);

        public void ValidateTemporaryStorageLocations
        (
            string temporaryDirectory,
            ArchiveSourceLocation? source,
            IObjectStore archiveStore,
            IRestoreDestination? destination = null
        ) => _inner.ValidateTemporaryStorageLocations(temporaryDirectory, source, archiveStore, destination);

        public void ConfigureSourceTraversal(ArchiveSourceLocation source, IObjectStore targetStore) =>
            _inner.ConfigureSourceTraversal(source, targetStore);

        public void ValidateBackupLocations(ArchiveSourceLocation source, IObjectStore targetStore) =>
            _inner.ValidateBackupLocations(source, targetStore);

        public async Task<FolderPolicy> ReadPolicyAsync
        (
            ArchiveSourceLocation source,
            ObjectPath prefix,
            CancellationToken cancellationToken = default
        )
        {
            var policy = await _inner.ReadPolicyAsync(source, prefix, cancellationToken);
            var callback = AfterPolicyRead;
            if (callback is not null && (AfterPolicyReadPrefix is null || AfterPolicyReadPrefix == prefix))
            {
                AfterPolicyRead = null;
                CallbackInvoked = true;
                callback();
            }
            return policy;
        }

        public void ValidateRestoreLocations
        (
            ArchiveSourceLocation descriptorLocation,
            IRestoreDestination destination,
            IObjectStore archiveStore
        ) => _inner.ValidateRestoreLocations(descriptorLocation, destination, archiveStore);
    }

    private sealed class SourceReadinessFixture : IDisposable
    {
        private const string FixturePrefix = "yabt-source-readiness-";

        private SourceReadinessFixture()
        {
            RootPath = Path.Combine(Path.GetTempPath(), $"{FixturePrefix}{Guid.NewGuid():N}");
            SourceRoot = Path.Combine(RootPath, "source");
            ArchiveRoot = Path.Combine(RootPath, "archive");
            var services = new ServiceCollection();
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            services.AddYabtFileSystemObjectStore();
            services.AddYabtMirrorFormatHandler();
            services.AddYabtZipFormatHandler();
            services.AddYabtMetadata();
            services.AddYabtSync();
            var registration = services.Single(service => service.ServiceType == typeof(IArchiveLocationResolver));
            var implementationType = registration.ImplementationType!;
            services.Remove(registration);
            services.AddSingleton<IArchiveLocationResolver>
            (
                provider => new ReadinessLocationResolver
                (
                    (IArchiveLocationResolver)ActivatorUtilities.CreateInstance(provider, implementationType)
                )
            );
            Services = services.BuildServiceProvider();
        }

        public string RootPath { get; }

        public string SourceRoot { get; }

        public string ArchiveRoot { get; }

        public ServiceProvider Services { get; }

        public IArchiveSynchronizer Synchronizer => Services.GetRequiredService<IArchiveSynchronizer>();

        public ReadinessLocationResolver LocationResolver =>
            (ReadinessLocationResolver)Services.GetRequiredService<IArchiveLocationResolver>();

        public static async Task<SourceReadinessFixture> CreateAsync(string format, ArchiveLayout? layout = null)
        {
            var fixture = new SourceReadinessFixture();
            try
            {
                Directory.CreateDirectory(fixture.SourceRoot);
                var store = new BackupRootStore("archive", FileSystemObjectStoreKind.Value)
                {
                    ProviderProperties = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                    {
                        ["rootPath"] = JsonSerializer.SerializeToElement(fixture.ArchiveRoot),
                    },
                };
                var descriptor = new BackupRootDescriptor
                (
                    BackupRootDescriptor.ExpectedDocumentType,
                    BackupRootDescriptor.ExpectedSchemaVersion,
                    Guid.NewGuid().ToString("N"),
                    DateTimeOffset.UtcNow,
                    layout ?? ArchiveLayout.Default,
                    [store]
                );
                await using (var content = File.Create(Path.Combine(fixture.SourceRoot, BackupRootFileNames.Primary)))
                {
                    await fixture.Services.GetRequiredService<IBackupRootSerializer>().WriteAsync(descriptor, content);
                }
                await WritePolicyAsync(fixture.SourceRoot, format);
                return fixture;
            }
            catch
            {
                fixture.Dispose();
                throw;
            }
        }

        public void MoveAside(string directoryPath)
        {
            var sourcePath = Path.GetFullPath(directoryPath);
            var destinationPath = Path.GetFullPath(Path.Combine(RootPath, "unavailable-source"));
            Assert.IsTrue(sourcePath.StartsWith(RootPath + Path.DirectorySeparatorChar, StringComparison.Ordinal));
            Assert.AreEqual(RootPath, Path.GetDirectoryName(destinationPath));
            Directory.Move(sourcePath, destinationPath);
        }

        public void Dispose()
        {
            Services.Dispose();
            var fullFixturePath = Path.GetFullPath(RootPath);
            var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
            Assert.AreEqual(temporaryRoot.TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(fullFixturePath));
            Assert.IsTrue(Path.GetFileName(fullFixturePath).StartsWith(FixturePrefix, StringComparison.Ordinal));
            if (Directory.Exists(fullFixturePath))
            {
                Directory.Delete(fullFixturePath, recursive: true);
            }
        }
    }
}
