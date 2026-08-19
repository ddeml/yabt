using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.FileSystem;
using Yabt.Format.Mirror;
using Yabt.Format.Zip;
using Yabt.Metadata;
using Yabt.Tests;

namespace Yabt.Sync.Tests;

[TestClass]
public sealed class ArchiveSynchronizerTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    [TestMethod]
    public async Task SyncAsyncCopiesNewProjectedObjectToTarget()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var targetRoot = Path.Combine(workspace, "target");
            await InitializeSourceRootAsync(sourceRoot, targetRoot);
            await WriteTextFileAsync(
                Path.Combine(sourceRoot, "folder", "file.txt"),
                "source content");

            using var serviceProvider = CreateServices().BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();

            var result = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));

            Assert.IsTrue(result.Completed);
            Assert.AreEqual(1, result.NewCount);
            AssertTextFile(Path.Combine(targetRoot, "folder", "file.txt"), "source content");
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task SyncAsyncPlacesNestedZipPackageInParentTargetFolder()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var targetRoot = Path.Combine(workspace, "target");
            var photosRoot = Path.Combine(sourceRoot, "albums", "photos");
            await InitializeSourceRootAsync(sourceRoot, targetRoot);
            await WriteTextFileAsync(
                Path.Combine(sourceRoot, "readme.txt"),
                "root content");
            await WritePolicyAsync(
                photosRoot,
                ZipArchiveFormatName.Value);
            await WriteTextFileAsync(
                Path.Combine(photosRoot, "image.txt"),
                "image content");

            using var serviceProvider = CreateServices().BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();

            var result = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));

            Assert.IsTrue(result.Completed);
            Assert.AreEqual(2, result.NewCount);
            AssertTextFile(Path.Combine(targetRoot, "readme.txt"), "root content");
            Assert.IsFalse(Directory.Exists(Path.Combine(targetRoot, "albums", "photos")));

            var zipFiles = Directory.GetFiles(
                Path.Combine(targetRoot, "albums"),
                "photos.*.zip");
            Assert.AreEqual(1, zipFiles.Length);

            await using var package = File.OpenRead(zipFiles[0]);
            using var archive = new ZipArchive(package, ZipArchiveMode.Read);
            var entry = archive.GetEntry("image.txt");

            Assert.IsNotNull(entry);
            using var reader = new StreamReader(entry.Open());
            Assert.AreEqual("image content", await reader.ReadToEndAsync());
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task SyncAsyncKeepsUnchangedZipWhenSynchronizationTimeChanges()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var targetRoot = Path.Combine(workspace, "target");
            var photosRoot = Path.Combine(sourceRoot, "albums", "photos");
            await InitializeSourceRootAsync(sourceRoot, targetRoot);
            await WritePolicyAsync(photosRoot, ZipArchiveFormatName.Value);
            await WriteTextFileAsync(
                Path.Combine(photosRoot, "image.txt"),
                "image content");

            var firstTimeProvider = new FixedTimeProvider(
                new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));
            using (var firstServiceProvider = CreateServices(firstTimeProvider).BuildServiceProvider())
            {
                var synchronizer = firstServiceProvider.GetRequiredService<IArchiveSynchronizer>();
                var firstResult = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));
                Assert.IsTrue(firstResult.Completed);
                Assert.AreEqual(1, firstResult.NewCount);
            }

            var firstPackagePath = Directory.GetFiles(
                Path.Combine(targetRoot, "albums"),
                "photos.*.zip").Single();

            var secondTimeProvider = new FixedTimeProvider(
                new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero));
            using (var secondServiceProvider = CreateServices(secondTimeProvider).BuildServiceProvider())
            {
                var synchronizer = secondServiceProvider.GetRequiredService<IArchiveSynchronizer>();
                var secondResult = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));

                Assert.IsTrue(secondResult.Completed);
                Assert.AreEqual(0, secondResult.NewCount);
                Assert.AreEqual(0, secondResult.ChangedCount);
                Assert.AreEqual(0, secondResult.ExtraCount);
                Assert.AreEqual(1, secondResult.UnchangedCount);
            }

            var secondPackagePath = Directory.GetFiles(
                Path.Combine(targetRoot, "albums"),
                "photos.*.zip").Single();
            Assert.AreEqual(firstPackagePath, secondPackagePath);
            Assert.IsFalse(Directory.Exists(Path.Combine(targetRoot, ".yabt-hist")));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task SyncAsyncCreatesNewFullHashZipNameWhenSourceChanges()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var targetRoot = Path.Combine(workspace, "target");
            var photosRoot = Path.Combine(sourceRoot, "albums", "photos");
            await InitializeSourceRootAsync(sourceRoot, targetRoot);
            await WritePolicyAsync(photosRoot, ZipArchiveFormatName.Value);
            await WriteTextFileAsync(
                Path.Combine(photosRoot, "image.txt"),
                "first image content");

            var timeProvider = new FixedTimeProvider(
                new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));
            using var serviceProvider = CreateServices(timeProvider).BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
            var firstResult = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));
            Assert.IsTrue(firstResult.Completed);
            var firstPackagePath = Directory.GetFiles(
                Path.Combine(targetRoot, "albums"),
                "photos.*.zip").Single();

            await WriteTextFileAsync(
                Path.Combine(photosRoot, "image.txt"),
                "second image content");

            var secondResult = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));

            Assert.IsTrue(secondResult.Completed);
            Assert.AreEqual(1, secondResult.NewCount);
            Assert.AreEqual(0, secondResult.ChangedCount);
            Assert.AreEqual(1, secondResult.ExtraCount);
            var secondPackagePath = Directory.GetFiles(
                Path.Combine(targetRoot, "albums"),
                "photos.*.zip").Single();
            Assert.AreNotEqual(firstPackagePath, secondPackagePath);
            Assert.AreEqual(
                Path.GetFileName(firstPackagePath),
                Path.GetFileName(Directory.GetFiles(
                    Path.Combine(targetRoot, ".yabt-hist"),
                    "photos.*.zip",
                    SearchOption.AllDirectories).Single()));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task SyncAsyncPreservesNestedMirrorFolder()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var targetRoot = Path.Combine(workspace, "target");
            var photosRoot = Path.Combine(sourceRoot, "albums", "photos");
            await InitializeSourceRootAsync(sourceRoot, targetRoot);
            await WritePolicyAsync(
                photosRoot,
                MirrorArchiveFormatName.Value);
            await WriteTextFileAsync(
                Path.Combine(photosRoot, "image.txt"),
                "image content");

            using var serviceProvider = CreateServices().BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();

            var result = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));

            Assert.IsTrue(result.Completed);
            AssertTextFile(
                Path.Combine(targetRoot, "albums", "photos", "image.txt"),
                "image content");
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task SyncAsyncUsesZipPolicyNestedWithinMirrorPolicy()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var targetRoot = Path.Combine(workspace, "target");
            var albumsRoot = Path.Combine(sourceRoot, "albums");
            var photosRoot = Path.Combine(albumsRoot, "photos");
            await InitializeSourceRootAsync(sourceRoot, targetRoot);
            await WritePolicyAsync(
                albumsRoot,
                MirrorArchiveFormatName.Value);
            await WritePolicyAsync(
                photosRoot,
                ZipArchiveFormatName.Value);
            await WriteTextFileAsync(
                Path.Combine(photosRoot, "image.txt"),
                "image content");

            using var serviceProvider = CreateServices().BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();

            var result = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));

            Assert.IsTrue(result.Completed);
            Assert.IsFalse(Directory.Exists(Path.Combine(targetRoot, "albums", "photos")));
            var zipFiles = Directory.GetFiles(
                Path.Combine(targetRoot, "albums"),
                "photos.*.zip");
            Assert.AreEqual(1, zipFiles.Length);
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task SyncAsyncMovesMirroredFolderToHistoryWhenPolicyChangesToZip()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var targetRoot = Path.Combine(workspace, "target");
            var photosRoot = Path.Combine(sourceRoot, "albums", "photos");
            await InitializeSourceRootAsync(sourceRoot, targetRoot);
            await WriteTextFileAsync(
                Path.Combine(photosRoot, "image.txt"),
                "image content");

            var timeProvider = new FixedTimeProvider(
                new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));
            using var serviceProvider = CreateServices(timeProvider).BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
            var mirrorResult = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));

            Assert.IsTrue(mirrorResult.Completed);
            AssertTextFile(
                Path.Combine(targetRoot, "albums", "photos", "image.txt"),
                "image content");
            await WriteTextFileAsync(
                Path.Combine(targetRoot, "albums", "photos", "orphan.txt"),
                "unexpected target content");
            Directory.CreateDirectory(Path.Combine(
                targetRoot,
                "albums",
                "photos",
                "native-empty"));

            await WritePolicyAsync(photosRoot, ZipArchiveFormatName.Value);

            var zipResult = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));

            Assert.IsTrue(zipResult.Completed);
            Assert.AreEqual(1, zipResult.NewCount);
            Assert.AreEqual(2, zipResult.ExtraCount);
            Assert.IsFalse(Directory.Exists(Path.Combine(targetRoot, "albums", "photos")));
            Assert.AreEqual(
                1,
                Directory.GetFiles(Path.Combine(targetRoot, "albums"), "photos.*.zip").Length);
            var historicalImages = Directory.GetFiles(
                Path.Combine(targetRoot, ".yabt-hist"),
                "image.txt",
                SearchOption.AllDirectories);
            Assert.AreEqual(1, historicalImages.Length);
            AssertTextFile(historicalImages[0], "image content");
            var historicalPhotosRoot = Path.GetDirectoryName(historicalImages[0]);
            Assert.IsNotNull(historicalPhotosRoot);
            AssertTextFile(
                Path.Combine(historicalPhotosRoot, "orphan.txt"),
                "unexpected target content");
            Assert.IsTrue(Directory.Exists(Path.Combine(
                historicalPhotosRoot,
                "native-empty")));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task SyncAsyncPreservesEmptyFolderMarkerWhenMirrorChangesToZip()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var targetRoot = Path.Combine(workspace, "target");
            var photosRoot = Path.Combine(sourceRoot, "albums", "photos");
            await InitializeSourceRootAsync(sourceRoot, targetRoot);
            Directory.CreateDirectory(photosRoot);

            var timeProvider = new FixedTimeProvider(
                new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));
            using var serviceProvider = CreateServices(timeProvider).BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
            var mirrorResult = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));

            Assert.IsTrue(mirrorResult.Completed);
            Assert.IsTrue(File.Exists(Path.Combine(
                targetRoot,
                "albums",
                "photos",
                ArchiveFolderMarkerFileNames.EmptyFolder)));

            var unchangedResult = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));

            Assert.IsTrue(unchangedResult.Completed);
            Assert.AreEqual(1, unchangedResult.UnchangedCount);

            await WritePolicyAsync(photosRoot, ZipArchiveFormatName.Value);

            var zipResult = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));

            Assert.IsTrue(zipResult.Completed);
            Assert.AreEqual(1, zipResult.NewCount);
            Assert.AreEqual(1, zipResult.ExtraCount);
            Assert.IsFalse(Directory.Exists(Path.Combine(targetRoot, "albums", "photos")));
            var historicalMarkers = Directory.GetFiles(
                Path.Combine(targetRoot, ".yabt-hist"),
                ArchiveFolderMarkerFileNames.EmptyFolder,
                SearchOption.AllDirectories);
            Assert.AreEqual(1, historicalMarkers.Length);
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task SyncAsyncRemovesEmptyFolderMarkerWhenMirrorFolderBecomesNonempty()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var targetRoot = Path.Combine(workspace, "target");
            var photosRoot = Path.Combine(sourceRoot, "albums", "photos");
            await InitializeSourceRootAsync(sourceRoot, targetRoot);
            Directory.CreateDirectory(photosRoot);

            var timeProvider = new FixedTimeProvider(
                new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));
            using var serviceProvider = CreateServices(timeProvider).BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
            var emptyResult = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));
            Assert.IsTrue(emptyResult.Completed);

            await WriteTextFileAsync(
                Path.Combine(photosRoot, "image.txt"),
                "image content");

            var nonemptyResult = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));

            Assert.IsTrue(nonemptyResult.Completed);
            Assert.AreEqual(1, nonemptyResult.NewCount);
            Assert.AreEqual(1, nonemptyResult.ExtraCount);
            AssertTextFile(
                Path.Combine(targetRoot, "albums", "photos", "image.txt"),
                "image content");
            Assert.IsFalse(File.Exists(Path.Combine(
                targetRoot,
                "albums",
                "photos",
                ArchiveFolderMarkerFileNames.EmptyFolder)));
            Assert.AreEqual(
                1,
                Directory.GetFiles(
                    Path.Combine(targetRoot, ".yabt-hist"),
                    ArchiveFolderMarkerFileNames.EmptyFolder,
                    SearchOption.AllDirectories).Length);
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task SyncAsyncMovesSelectedRootMarkerToHistoryWhenRootChangesToZip()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var targetRoot = Path.Combine(workspace, "target");
            await InitializeSourceRootAsync(sourceRoot, targetRoot);

            var timeProvider = new FixedTimeProvider(
                new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));
            using var serviceProvider = CreateServices(timeProvider).BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
            var mirrorResult = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));

            Assert.IsTrue(mirrorResult.Completed);
            Assert.IsTrue(File.Exists(Path.Combine(
                targetRoot,
                ArchiveFolderMarkerFileNames.EmptyFolder)));

            await WritePolicyAsync(sourceRoot, ZipArchiveFormatName.Value);

            var zipResult = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));

            Assert.IsTrue(zipResult.Completed);
            Assert.AreEqual(1, zipResult.NewCount);
            Assert.AreEqual(1, zipResult.ExtraCount);
            Assert.IsFalse(File.Exists(Path.Combine(
                targetRoot,
                ArchiveFolderMarkerFileNames.EmptyFolder)));
            Assert.AreEqual(1, Directory.GetFiles(targetRoot, "source.*.zip").Length);
            Assert.AreEqual(
                1,
                Directory.GetFiles(
                    Path.Combine(targetRoot, ".yabt-hist"),
                    ArchiveFolderMarkerFileNames.EmptyFolder,
                    SearchOption.AllDirectories).Length);
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task VerifyAsyncReportsStaleEmptyFolder()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var targetRoot = Path.Combine(workspace, "target");
            var photosRoot = Path.Combine(sourceRoot, "albums", "photos");
            await InitializeSourceRootAsync(sourceRoot, targetRoot);
            await WritePolicyAsync(photosRoot, ZipArchiveFormatName.Value);
            await WriteTextFileAsync(
                Path.Combine(photosRoot, "image.txt"),
                "image content");

            var timeProvider = new FixedTimeProvider(
                new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));
            using var serviceProvider = CreateServices(timeProvider).BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
            var syncResult = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));
            Assert.IsTrue(syncResult.Completed);
            Directory.CreateDirectory(Path.Combine(targetRoot, "albums", "photos"));

            var verifyResult = await synchronizer.VerifyAsync(new SyncRunRequest(sourceRoot));

            Assert.IsFalse(verifyResult.Completed);
            Assert.AreEqual(0, verifyResult.NewCount);
            Assert.AreEqual(0, verifyResult.ChangedCount);
            Assert.AreEqual(1, verifyResult.ExtraCount);
            Assert.AreEqual(1, verifyResult.UnchangedCount);
            Assert.IsTrue(Directory.Exists(Path.Combine(targetRoot, "albums", "photos")));
            Assert.IsFalse(Directory.Exists(Path.Combine(targetRoot, ".yabt-hist")));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task SyncAsyncPreservesDesiredAncestorsWhenZipChangesToMirror()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var targetRoot = Path.Combine(workspace, "target");
            var photosRoot = Path.Combine(sourceRoot, "albums", "photos");
            await InitializeSourceRootAsync(sourceRoot, targetRoot);
            await WritePolicyAsync(photosRoot, ZipArchiveFormatName.Value);
            await WriteTextFileAsync(
                Path.Combine(photosRoot, "image.txt"),
                "image content");

            var timeProvider = new FixedTimeProvider(
                new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));
            using var serviceProvider = CreateServices(timeProvider).BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
            var zipResult = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));
            Assert.IsTrue(zipResult.Completed);

            await WritePolicyAsync(photosRoot, MirrorArchiveFormatName.Value);

            var mirrorResult = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));

            Assert.IsTrue(mirrorResult.Completed);
            Assert.AreEqual(2, mirrorResult.NewCount);
            Assert.AreEqual(1, mirrorResult.ExtraCount);
            AssertTextFile(
                Path.Combine(targetRoot, "albums", "photos", "image.txt"),
                "image content");
            Assert.AreEqual(
                0,
                Directory.GetFiles(Path.Combine(targetRoot, "albums"), "photos.*.zip").Length);
            Assert.AreEqual(
                1,
                Directory.GetFiles(
                    Path.Combine(targetRoot, ".yabt-hist"),
                    "photos.*.zip",
                    SearchOption.AllDirectories).Length);

            var verifyResult = await synchronizer.VerifyAsync(new SyncRunRequest(sourceRoot));

            Assert.IsTrue(verifyResult.Completed);
            Assert.AreEqual(2, verifyResult.UnchangedCount);
            Assert.AreEqual(0, verifyResult.ExtraCount);
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task SyncAsyncAllocatesNewHistoryFolderForRepeatedSameTimeTransition()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var targetRoot = Path.Combine(workspace, "target");
            var photosRoot = Path.Combine(sourceRoot, "albums", "photos");
            await InitializeSourceRootAsync(sourceRoot, targetRoot);
            await WriteTextFileAsync(
                Path.Combine(photosRoot, "image.txt"),
                "image content");

            var timeProvider = new FixedTimeProvider(
                new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));
            using var serviceProvider = CreateServices(timeProvider).BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();

            await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));
            await WritePolicyAsync(photosRoot, ZipArchiveFormatName.Value);
            await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));
            await WritePolicyAsync(photosRoot, MirrorArchiveFormatName.Value);
            await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));
            await WritePolicyAsync(photosRoot, ZipArchiveFormatName.Value);

            var result = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));

            Assert.IsTrue(result.Completed);
            Assert.IsFalse(Directory.Exists(Path.Combine(targetRoot, "albums", "photos")));
            var historicalPhotoFolders = Directory.GetDirectories(
                Path.Combine(targetRoot, ".yabt-hist"),
                "photos",
                SearchOption.AllDirectories);
            Assert.AreEqual(2, historicalPhotoFolders.Length);
            var historicalRootNames = historicalPhotoFolders
                .Select(path => Directory.GetParent(path)?.Parent?.Name)
                .ToArray();
            CollectionAssert.AreEquivalent(
                new[]
                {
                    "20260724T120000Z",
                    "20260724T120000Z-1",
                },
                historicalRootNames);
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task SyncAsyncTreatsDeduplicationReferenceAsOccupiedHistoryPath()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var targetRoot = Path.Combine(workspace, "target");
            await InitializeSourceRootAsync(sourceRoot, targetRoot);
            await WriteTextFileAsync(
                Path.Combine(sourceRoot, "docs", "file.bin"),
                "new live content");
            await WriteTextFileAsync(
                Path.Combine(targetRoot, "docs", "file.bin"),
                "old live content");

            const string timestampSegment = "20260819T120000Z";
            const string logicalHistoryPath = $"{timestampSegment}/docs/file.bin";
            var backingContent = Encoding.UTF8.GetBytes("canonical historical content");
            var backingPath = Path.Combine(
                targetRoot,
                ".yabt-hist",
                "20260818T120000Z",
                "docs",
                "file.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(backingPath)!);
            await File.WriteAllBytesAsync(backingPath, backingContent);

            var timeProvider = new FixedTimeProvider(
                new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero));
            using var serviceProvider = CreateServices(timeProvider).BuildServiceProvider();
            var referenceSerializer = serviceProvider.GetRequiredService<IHistoryReferenceSerializer>();
            var referenceEntry = new ArchiveHistoryManifestEntry
            (
                logicalHistoryPath,
                ArchiveHistoryFileNames.CreateReferencePath(logicalHistoryPath),
                ArchiveHistoryEntryRepresentation.Reference,
                backingContent.LongLength,
                ArchiveHash.Compute(backingContent)
            );
            var reference = referenceSerializer.Create(referenceEntry);
            var referencePath = Path.Combine(
                targetRoot,
                ".yabt-hist",
                referenceEntry.StoredRelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(referencePath)!);
            await using (var referenceStream = File.Create(referencePath))
            {
                await referenceSerializer.WriteAsync(reference, referenceStream);
            }

            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
            var result = await synchronizer.SyncAsync(
                new SyncRunRequest(sourceRoot, ByteForByte: true));

            Assert.IsTrue(result.Completed);
            AssertTextFile(Path.Combine(targetRoot, "docs", "file.bin"), "new live content");
            AssertTextFile(
                Path.Combine(
                    targetRoot,
                    ".yabt-hist",
                    $"{timestampSegment}-1",
                    "docs",
                    "file.bin"),
                "old live content");
            Assert.IsFalse(File.Exists(Path.Combine(
                targetRoot,
                ".yabt-hist",
                timestampSegment,
                "docs",
                "file.bin")));
            Assert.IsTrue(File.Exists(referencePath));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task SyncAsyncRejectsZipPackageNameCollisionWithSourceObject()
    {
        await AssertZipPackageNameCollisionRejectedAsync(
            createSourceFolderCollision: false);
    }

    [TestMethod]
    public async Task SyncAsyncRejectsZipPackageNameCollisionWithSourceFolder()
    {
        await AssertZipPackageNameCollisionRejectedAsync(
            createSourceFolderCollision: true);
    }

    [TestMethod]
    public async Task SyncAsyncUploadsProjectedObjectsBeforeSourceEnumerationCompletes()
    {
        var sourceRoot = Path.Combine(
            Path.GetTempPath(),
            $"yabt-streaming-sync-test-{Guid.NewGuid():N}");
        var sourceStore = new FailingAfterFirstListedObjectStore();
        var targetStore = new MemoryObjectStore();
        var descriptor = CreateRootDescriptor(
            [new BackupRootStore("target", FixedBackupRootStoreResolver.StoreKindValue)]);

        using var serviceProvider = CreateStreamingServices(
            sourceRoot,
            descriptor,
            sourceStore,
            targetStore).BuildServiceProvider();
        var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();

        var failedAfterFirstObject = false;
        try
        {
            await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));
        }
        catch (InvalidOperationException)
        {
            failedAfterFirstObject = true;
        }

        Assert.IsTrue(failedAfterFirstObject);
        Assert.IsTrue(targetStore.TryGetObject("first.txt", out var uploadedObject));
        Assert.AreEqual(
            FailingAfterFirstListedObjectStore.FirstContent,
            Encoding.UTF8.GetString(uploadedObject.Content.Span));
    }

    [TestMethod]
    public async Task SyncAsyncHistorizesExactObjectAndSameNamedFolderSeparately()
    {
        var sourceRoot = Path.Combine(
            Path.GetTempPath(),
            $"yabt-object-folder-collision-test-{Guid.NewGuid():N}");
        var timeProvider = new FixedTimeProvider(
            new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));
        var sourceStore = new MemoryObjectStore(timeProvider);
        var targetStore = new MemoryObjectStore(timeProvider);
        await UploadTextObjectAsync(sourceStore, "a/current.txt", "current content");
        await UploadTextObjectAsync(targetStore, "a/b", "object content");
        await UploadTextObjectAsync(targetStore, "a/b/file.txt", "folder content");
        var descriptor = CreateRootDescriptor(
            [new BackupRootStore("target", FixedBackupRootStoreResolver.StoreKindValue)]);

        using var serviceProvider = CreateStreamingServices(
            sourceRoot,
            descriptor,
            sourceStore,
            targetStore,
            timeProvider).BuildServiceProvider();
        var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();

        var result = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));

        Assert.IsTrue(result.Completed);
        Assert.AreEqual(1, result.NewCount);
        Assert.AreEqual(2, result.ExtraCount);
        Assert.IsTrue(targetStore.TryGetObject("a/current.txt", out _));
        Assert.IsFalse(targetStore.TryGetObject("a/b", out _));
        Assert.IsFalse(targetStore.TryGetObject("a/b/file.txt", out _));
        Assert.IsTrue(targetStore.TryGetObject(
            ".yabt-hist/20260724T120000Z/a/b",
            out var historicalObject));
        Assert.AreEqual(
            "object content",
            Encoding.UTF8.GetString(historicalObject.Content.Span));
        Assert.IsTrue(targetStore.TryGetObject(
            ".yabt-hist/20260724T120000Z-1/a/b/file.txt",
            out var historicalFolderObject));
        Assert.AreEqual(
            "folder content",
            Encoding.UTF8.GetString(historicalFolderObject.Content.Span));
    }

    [TestMethod]
    public async Task SyncAsyncDoesNotCompareManifestHashWithTargetContentHash()
    {
        var sourceRoot = Path.Combine(
            Path.GetTempPath(),
            $"yabt-zip-content-hash-test-{Guid.NewGuid():N}");
        var timeProvider = new FixedTimeProvider(
            new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));
        var sourceStore = new MemoryObjectStore(timeProvider, provideContentHash: true);
        var targetStore = new MemoryObjectStore(timeProvider, provideContentHash: true);
        await UploadTextObjectAsync(sourceStore, "file.txt", "source content");
        var descriptor = CreateRootDescriptor(
            [new BackupRootStore("target", FixedBackupRootStoreResolver.StoreKindValue)]);

        using var serviceProvider = CreateStreamingServices(
            sourceRoot,
            descriptor,
            sourceStore,
            targetStore,
            timeProvider,
            new FolderPolicy(ZipArchiveFormatName.Value)).BuildServiceProvider();
        var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
        var firstResult = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));

        var secondResult = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));

        Assert.IsTrue(firstResult.Completed);
        Assert.AreEqual(1, firstResult.NewCount);
        Assert.IsTrue(secondResult.Completed);
        Assert.AreEqual(0, secondResult.NewCount);
        Assert.AreEqual(0, secondResult.ChangedCount);
        Assert.AreEqual(0, secondResult.ExtraCount);
        Assert.AreEqual(1, secondResult.UnchangedCount);
    }

    [TestMethod]
    public async Task SyncAndVerifyUseZipChangeManifestWithoutRebuildingUnchangedPackage()
    {
        var sourceRoot = Path.Combine(
            Path.GetTempPath(),
            $"yabt-fast-change-test-{Guid.NewGuid():N}");
        var timeProvider = new FixedTimeProvider(
            new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero));
        var sourceStore = new MemoryObjectStore(timeProvider);
        var targetStore = new MemoryObjectStore(timeProvider);
        await UploadTextObjectAsync(sourceStore, "file.txt", "source content");
        var guardedSourceStore = new DataReadGuardObjectStore(sourceStore);
        var guardedTargetStore = new DataReadGuardObjectStore(targetStore);
        var descriptor = CreateRootDescriptor(
            [new BackupRootStore("target", FixedBackupRootStoreResolver.StoreKindValue)]);

        using var serviceProvider = CreateStreamingServices(
            sourceRoot,
            descriptor,
            guardedSourceStore,
            guardedTargetStore,
            timeProvider,
            new FolderPolicy(ZipArchiveFormatName.Value)).BuildServiceProvider();
        var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();

        var firstResult = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));

        Assert.IsTrue(firstResult.Completed);
        Assert.IsTrue(targetStore.TryGetObject(
            ArchiveChangeManifest.BrotliFileName,
            out var manifestObject));
        Assert.IsFalse(targetStore.TryGetObject(
            ArchiveChangeManifest.UncompressedFileName,
            out _));
        Assert.AreEqual("application/octet-stream", manifestObject.ContentType);
        var manifestSerializer = serviceProvider.GetRequiredService<IChangeManifestSerializer>();
        await using var manifestContent = new MemoryStream(manifestObject.Content.ToArray(), writable: false);
        var manifest = await ReadChangeManifestAsync(
            manifestSerializer,
            manifestContent,
            ArchiveChangeManifestCompression.Brotli);
        var manifestEntry = manifest.Entries.Single();
        Assert.IsTrue(manifestEntry.RelativePath.EndsWith(".zip", StringComparison.Ordinal));
        Assert.IsTrue(manifestEntry.ChangeFingerprint.StartsWith(
            "xxh128:",
            StringComparison.Ordinal));
        Assert.IsTrue(manifestEntry.ArtifactLength > 0);
        Assert.IsTrue(manifestEntry.ContentHash?.StartsWith("xxh128:", StringComparison.Ordinal));

        guardedSourceStore.RejectDataReads = true;
        guardedTargetStore.RejectDataReads = true;

        var secondSyncResult = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));
        var verifyResult = await synchronizer.VerifyAsync(new SyncRunRequest(sourceRoot));

        Assert.IsTrue(secondSyncResult.Completed);
        Assert.AreEqual(1, secondSyncResult.UnchangedCount);
        Assert.IsTrue(verifyResult.Completed);
        Assert.AreEqual(1, verifyResult.UnchangedCount);

        await Assert.ThrowsExactlyAsync<YabtSyncException>(() => synchronizer.VerifyAsync(
            new SyncRunRequest(sourceRoot, ByteForByte: true)));

        guardedTargetStore.HideContentLengths = true;
        await Assert.ThrowsExactlyAsync<YabtSyncException>(() => synchronizer.VerifyAsync(
            new SyncRunRequest(sourceRoot)));

        guardedTargetStore.HideContentLengths = false;
        await targetStore.MoveAsync(
            manifestEntry.RelativePath,
            $"{ArchiveInternalFolderNames.TemporaryUploads}/original-package.zip");
        await UploadTextObjectAsync(targetStore, manifestEntry.RelativePath, "x");
        Assert.IsTrue(targetStore.TryGetObject(manifestEntry.RelativePath, out var truncatedPackage));
        Assert.AreEqual(1, truncatedPackage.Content.Length);
        Assert.IsTrue(targetStore.TryGetObject(
            ArchiveChangeManifest.BrotliFileName,
            out var currentManifestObject));
        await using var currentManifestContent = new MemoryStream(
            currentManifestObject.Content.ToArray(),
            writable: false);
        var currentManifest = await ReadChangeManifestAsync(
            manifestSerializer,
            currentManifestContent,
            ArchiveChangeManifestCompression.Brotli);
        Assert.IsTrue(currentManifest.Entries.Single().ArtifactLength > 1);

        var truncatedResult = await synchronizer.VerifyAsync(new SyncRunRequest(sourceRoot));

        Assert.IsFalse(truncatedResult.Completed);
        Assert.AreEqual(1, truncatedResult.ChangedCount);
    }

    [TestMethod]
    public async Task SyncAsyncWritesUncompressedChangeManifestWhenConfigured()
    {
        var sourceRoot = Path.Combine(
            Path.GetTempPath(),
            $"yabt-uncompressed-manifest-test-{Guid.NewGuid():N}");
        var sourceStore = new MemoryObjectStore();
        var targetStore = new MemoryObjectStore();
        await UploadTextObjectAsync(sourceStore, "file.txt", "source content");
        var descriptor = CreateRootDescriptor(
            [new BackupRootStore("target", FixedBackupRootStoreResolver.StoreKindValue)],
            changeManifestCompression: ArchiveChangeManifestCompression.None);

        using var serviceProvider = CreateStreamingServices(
            sourceRoot,
            descriptor,
            sourceStore,
            targetStore).BuildServiceProvider();
        var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();

        var result = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));

        Assert.IsTrue(result.Completed);
        Assert.IsTrue(targetStore.TryGetObject(
            ArchiveChangeManifest.UncompressedFileName,
            out var manifestObject));
        Assert.IsFalse(targetStore.TryGetObject(
            ArchiveChangeManifest.BrotliFileName,
            out _));
        Assert.AreEqual("application/json", manifestObject.ContentType);

        var serializer = serviceProvider.GetRequiredService<IChangeManifestSerializer>();
        await using var content = new MemoryStream(manifestObject.Content.ToArray(), writable: false);
        var manifest = await ReadChangeManifestAsync(
            serializer,
            content,
            ArchiveChangeManifestCompression.None);
        Assert.AreEqual(1, manifest.Entries.Count());
    }

    [TestMethod]
    [DataRow(ArchiveChangeManifestCompression.None, ArchiveChangeManifestCompression.Brotli)]
    [DataRow(ArchiveChangeManifestCompression.Brotli, ArchiveChangeManifestCompression.None)]
    public async Task SyncAsyncReadsEitherChangeManifestAndConvertsToConfiguredRepresentation
    (
        string initialCompression,
        string nextCompression
    )
    {
        var sourceRoot = Path.Combine(
            Path.GetTempPath(),
            $"yabt-manifest-conversion-test-{Guid.NewGuid():N}");
        var timeProvider = new FixedTimeProvider(
            new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero));
        var sourceStore = new MemoryObjectStore(timeProvider);
        var targetStore = new MemoryObjectStore(timeProvider);
        await UploadTextObjectAsync(sourceStore, "file.txt", "source content");
        var storeConfiguration = new BackupRootStore(
            "target",
            FixedBackupRootStoreResolver.StoreKindValue);
        var initialDescriptor = CreateRootDescriptor(
            [storeConfiguration],
            changeManifestCompression: initialCompression);

        using (var initialServiceProvider = CreateStreamingServices(
            sourceRoot,
            initialDescriptor,
            sourceStore,
            targetStore,
            timeProvider).BuildServiceProvider())
        {
            var initialSynchronizer = initialServiceProvider.GetRequiredService<IArchiveSynchronizer>();
            var initialResult = await initialSynchronizer.SyncAsync(new SyncRunRequest(sourceRoot));
            Assert.IsTrue(initialResult.Completed);
        }

        var initialFileName = GetChangeManifestFileName(initialCompression);
        var nextFileName = GetChangeManifestFileName(nextCompression);
        Assert.IsTrue(targetStore.TryGetObject(initialFileName, out _));
        Assert.IsFalse(targetStore.TryGetObject(nextFileName, out _));
        await UploadTextObjectAsync(
            targetStore,
            $".yabt-hist/{ArchiveHistoryFileNames.Manifest}",
            "existing history catalog");

        var nextDescriptor = CreateRootDescriptor(
            [storeConfiguration],
            changeManifestCompression: nextCompression);
        var guardedSourceStore = new DataReadGuardObjectStore(sourceStore)
        {
            RejectDataReads = true,
        };
        var guardedTargetStore = new DataReadGuardObjectStore(targetStore)
        {
            RejectDataReads = true,
        };
        using var nextServiceProvider = CreateStreamingServices(
            sourceRoot,
            nextDescriptor,
            guardedSourceStore,
            guardedTargetStore,
            timeProvider).BuildServiceProvider();
        var nextSynchronizer = nextServiceProvider.GetRequiredService<IArchiveSynchronizer>();

        var conversionResult = await nextSynchronizer.SyncAsync(new SyncRunRequest(sourceRoot));

        Assert.IsTrue(conversionResult.Completed);
        Assert.AreEqual(1, conversionResult.UnchangedCount);
        Assert.IsFalse(targetStore.TryGetObject(initialFileName, out _));
        Assert.IsTrue(targetStore.TryGetObject(nextFileName, out var convertedManifestObject));
        Assert.IsFalse(targetStore.TryGetObject(
            $".yabt-hist/20260816T120000Z/{initialFileName}",
            out _));
        Assert.IsTrue(targetStore.TryGetObject(
            $".yabt-hist/{ArchiveHistoryFileNames.Manifest}",
            out var historyManifestObject));
        Assert.AreEqual(
            "existing history catalog",
            Encoding.UTF8.GetString(historyManifestObject.Content.Span));
        Assert.IsFalse(targetStore.TryGetObject(
            $".yabt-hist/{ArchiveHistoryManifest.InvalidationMarkerFileName}",
            out _));

        var serializer = nextServiceProvider.GetRequiredService<IChangeManifestSerializer>();
        await using var convertedContent = new MemoryStream(
            convertedManifestObject.Content.ToArray(),
            writable: false);
        var convertedManifest = await ReadChangeManifestAsync(
            serializer,
            convertedContent,
            nextCompression);
        Assert.AreEqual(1, convertedManifest.Entries.Count());
    }

    [TestMethod]
    public async Task SyncAsyncAcceptsMatchingDualChangeManifestsAndConvergesToConfiguredRepresentation()
    {
        var sourceRoot = Path.Combine(
            Path.GetTempPath(),
            $"yabt-matching-dual-manifest-test-{Guid.NewGuid():N}");
        var timeProvider = new FixedTimeProvider(
            new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero));
        var sourceStore = new MemoryObjectStore(timeProvider);
        var targetStore = new MemoryObjectStore(timeProvider);
        await UploadTextObjectAsync(sourceStore, "file.txt", "source content");
        var descriptor = CreateRootDescriptor(
            [new BackupRootStore("target", FixedBackupRootStoreResolver.StoreKindValue)]);

        using var serviceProvider = CreateStreamingServices(
            sourceRoot,
            descriptor,
            sourceStore,
            targetStore,
            timeProvider).BuildServiceProvider();
        var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
        var serializer = serviceProvider.GetRequiredService<IChangeManifestSerializer>();

        var initialResult = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));
        Assert.IsTrue(initialResult.Completed);
        Assert.IsTrue(targetStore.TryGetObject(
            ArchiveChangeManifest.BrotliFileName,
            out var compressedManifestObject));
        await using var compressedContent = new MemoryStream(
            compressedManifestObject.Content.ToArray(),
            writable: false);
        var manifest = await ReadChangeManifestAsync(
            serializer,
            compressedContent,
            ArchiveChangeManifestCompression.Brotli);
        await UploadChangeManifestAsync(
            targetStore,
            serializer,
            manifest,
            ArchiveChangeManifestCompression.None);

        var verifyResult = await synchronizer.VerifyAsync(new SyncRunRequest(sourceRoot));
        var convergenceResult = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));

        Assert.IsTrue(verifyResult.Completed);
        Assert.AreEqual(1, verifyResult.UnchangedCount);
        Assert.IsTrue(convergenceResult.Completed);
        Assert.AreEqual(1, convergenceResult.UnchangedCount);
        Assert.IsTrue(targetStore.TryGetObject(ArchiveChangeManifest.BrotliFileName, out _));
        Assert.IsFalse(targetStore.TryGetObject(ArchiveChangeManifest.UncompressedFileName, out _));
        Assert.IsFalse(targetStore.TryGetObject(
            $".yabt-hist/20260816T120000Z/{ArchiveChangeManifest.BrotliFileName}",
            out _));
        Assert.IsFalse(targetStore.TryGetObject(
            $".yabt-hist/20260816T120000Z/{ArchiveChangeManifest.UncompressedFileName}",
            out _));
    }

    [TestMethod]
    public async Task SyncAsyncRejectsConflictingDualChangeManifestsAndRebuildsBoth()
    {
        var sourceRoot = Path.Combine(
            Path.GetTempPath(),
            $"yabt-conflicting-dual-manifest-test-{Guid.NewGuid():N}");
        var timeProvider = new FixedTimeProvider(
            new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero));
        var sourceStore = new MemoryObjectStore(timeProvider);
        var targetStore = new MemoryObjectStore(timeProvider);
        await UploadTextObjectAsync(sourceStore, "file.txt", "source content");
        var descriptor = CreateRootDescriptor(
            [new BackupRootStore("target", FixedBackupRootStoreResolver.StoreKindValue)]);

        using var serviceProvider = CreateStreamingServices(
            sourceRoot,
            descriptor,
            sourceStore,
            targetStore,
            timeProvider).BuildServiceProvider();
        var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
        var serializer = serviceProvider.GetRequiredService<IChangeManifestSerializer>();

        var initialResult = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));
        Assert.IsTrue(initialResult.Completed);
        var conflictingManifest = serializer.Create([]);
        await UploadChangeManifestAsync(
            targetStore,
            serializer,
            conflictingManifest,
            ArchiveChangeManifestCompression.None);

        await Assert.ThrowsExactlyAsync<YabtSyncException>(() => synchronizer.VerifyAsync(
            new SyncRunRequest(sourceRoot)));
        var byteForByteResult = await synchronizer.VerifyAsync(
            new SyncRunRequest(sourceRoot, ByteForByte: true));
        var recoveryResult = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));

        Assert.IsTrue(byteForByteResult.Completed);
        Assert.AreEqual(1, byteForByteResult.UnchangedCount);
        Assert.IsTrue(recoveryResult.Completed);
        Assert.AreEqual(1, recoveryResult.UnchangedCount);
        Assert.IsTrue(targetStore.TryGetObject(ArchiveChangeManifest.BrotliFileName, out _));
        Assert.IsFalse(targetStore.TryGetObject(ArchiveChangeManifest.UncompressedFileName, out _));
        Assert.IsFalse(targetStore.TryGetObject(
            $".yabt-hist/20260816T120000Z/{ArchiveChangeManifest.BrotliFileName}",
            out _));
        Assert.IsFalse(targetStore.TryGetObject(
            $".yabt-hist/20260816T120000Z/{ArchiveChangeManifest.UncompressedFileName}",
            out _));
    }

    [TestMethod]
    public async Task InterruptedDualManifestQuarantineLeavesEvidenceUntrusted()
    {
        var sourceRoot = Path.Combine(
            Path.GetTempPath(),
            $"yabt-interrupted-manifest-quarantine-test-{Guid.NewGuid():N}");
        var timeProvider = new FixedTimeProvider(
            new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero));
        var sourceStore = new MemoryObjectStore(timeProvider);
        var targetStore = new MemoryObjectStore(timeProvider);
        var guardedTargetStore = new DataReadGuardObjectStore(targetStore);
        await UploadTextObjectAsync(sourceStore, "file.txt", "source content");
        var descriptor = CreateRootDescriptor(
            [new BackupRootStore("target", FixedBackupRootStoreResolver.StoreKindValue)]);

        using var serviceProvider = CreateStreamingServices(
            sourceRoot,
            descriptor,
            sourceStore,
            guardedTargetStore,
            timeProvider).BuildServiceProvider();
        var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
        var serializer = serviceProvider.GetRequiredService<IChangeManifestSerializer>();

        var initialResult = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));
        Assert.IsTrue(initialResult.Completed);
        await UploadChangeManifestAsync(
            targetStore,
            serializer,
            serializer.Create([]),
            ArchiveChangeManifestCompression.None);
        guardedTargetStore.RejectConditionalDeleteKey =
            ArchiveChangeManifest.UncompressedFileName;

        await Assert.ThrowsExactlyAsync<YabtSyncException>(() => synchronizer.SyncAsync(
            new SyncRunRequest(sourceRoot)));

        Assert.IsTrue(targetStore.TryGetObject(
            ArchiveChangeManifest.InvalidationMarkerFileName,
            out _));
        Assert.IsFalse(targetStore.TryGetObject(ArchiveChangeManifest.BrotliFileName, out _));
        Assert.IsTrue(targetStore.TryGetObject(ArchiveChangeManifest.UncompressedFileName, out _));
        await Assert.ThrowsExactlyAsync<YabtSyncException>(() => synchronizer.VerifyAsync(
            new SyncRunRequest(sourceRoot)));
        var byteForByteResult = await synchronizer.VerifyAsync(
            new SyncRunRequest(sourceRoot, ByteForByte: true));
        Assert.IsTrue(byteForByteResult.Completed);

        guardedTargetStore.RejectConditionalDeleteKey = null;
        var recoveryResult = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));

        Assert.IsTrue(recoveryResult.Completed);
        Assert.AreEqual(1, recoveryResult.UnchangedCount);
        Assert.IsTrue(targetStore.TryGetObject(ArchiveChangeManifest.BrotliFileName, out _));
        Assert.IsFalse(targetStore.TryGetObject(ArchiveChangeManifest.UncompressedFileName, out _));
        Assert.IsFalse(targetStore.TryGetObject(
            ArchiveChangeManifest.InvalidationMarkerFileName,
            out _));
    }

    [TestMethod]
    public async Task InterruptedHistoryMetadataCleanupLeavesMarkerUntilRecovery()
    {
        var sourceRoot = Path.Combine(
            Path.GetTempPath(),
            $"yabt-interrupted-history-cleanup-test-{Guid.NewGuid():N}");
        var timeProvider = new FixedTimeProvider(
            new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero));
        var sourceStore = new MemoryObjectStore(timeProvider);
        var targetStore = new MemoryObjectStore(timeProvider);
        var guardedTargetStore = new DataReadGuardObjectStore(targetStore);
        const string initialContent = "initial content";
        await UploadTextObjectAsync(sourceStore, "file.txt", initialContent);
        var descriptor = CreateRootDescriptor(
            [new BackupRootStore("target", FixedBackupRootStoreResolver.StoreKindValue)]);

        using var serviceProvider = CreateStreamingServices(
            sourceRoot,
            descriptor,
            sourceStore,
            guardedTargetStore,
            timeProvider).BuildServiceProvider();
        var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();

        var initialResult = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));
        Assert.IsTrue(initialResult.Completed);
        var historyManifestKey =
            $".yabt-hist/{ArchiveHistoryFileNames.Manifest}";
        var historyInvalidationMarkerKey =
            $".yabt-hist/{ArchiveHistoryManifest.InvalidationMarkerFileName}";
        await UploadTextObjectAsync(
            targetStore,
            historyManifestKey,
            "stale history catalog");

        await using (var replacementContent = new MemoryStream(
            Encoding.UTF8.GetBytes("replacement content"),
            writable: false))
        {
            var replaced = await sourceStore.TryReplaceIfContentHashMatchesAsync(
                "file.txt",
                ArchiveHash.Compute(Encoding.UTF8.GetBytes(initialContent)),
                replacementContent,
                "text/plain",
                new Dictionary<string, string>(StringComparer.Ordinal));
            Assert.IsTrue(replaced);
        }

        guardedTargetStore.RejectConditionalDeleteKey = historyInvalidationMarkerKey;

        await Assert.ThrowsExactlyAsync<YabtSyncException>(() => synchronizer.SyncAsync(
            new SyncRunRequest(sourceRoot, ByteForByte: true)));

        Assert.IsFalse(targetStore.TryGetObject(historyManifestKey, out _));
        Assert.IsTrue(targetStore.TryGetObject(historyInvalidationMarkerKey, out _));

        guardedTargetStore.RejectConditionalDeleteKey = null;
        var deduplicationResult = await serviceProvider
            .GetRequiredService<IHistoryDeduplicator>()
            .DeduplicateAsync(new HistoryDeduplicationRequest(sourceRoot));

        Assert.IsTrue(deduplicationResult.Completed);
        Assert.IsTrue(targetStore.TryGetObject(historyManifestKey, out _));
        Assert.IsFalse(targetStore.TryGetObject(historyInvalidationMarkerKey, out _));
    }

    [TestMethod]
    public async Task ByteForByteVerifyIgnoresIncorrectManifestArtifactLength()
    {
        var sourceRoot = Path.Combine(
            Path.GetTempPath(),
            $"yabt-byte-manifest-length-test-{Guid.NewGuid():N}");
        var timeProvider = new FixedTimeProvider(
            new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero));
        var sourceStore = new MemoryObjectStore(timeProvider);
        var targetStore = new MemoryObjectStore(timeProvider);
        await UploadTextObjectAsync(sourceStore, "file.txt", "source content");
        var descriptor = CreateRootDescriptor(
            [new BackupRootStore("target", FixedBackupRootStoreResolver.StoreKindValue)]);

        using var serviceProvider = CreateStreamingServices(
            sourceRoot,
            descriptor,
            sourceStore,
            targetStore,
            timeProvider,
            new FolderPolicy(ZipArchiveFormatName.Value)).BuildServiceProvider();
        var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
        var serializer = serviceProvider.GetRequiredService<IChangeManifestSerializer>();

        var syncResult = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));
        Assert.IsTrue(syncResult.Completed);
        Assert.IsTrue(targetStore.TryGetObject(
            ArchiveChangeManifest.BrotliFileName,
            out var manifestObject));
        await using var manifestContent = new MemoryStream(
            manifestObject.Content.ToArray(),
            writable: false);
        var manifest = await ReadChangeManifestAsync(
            serializer,
            manifestContent,
            ArchiveChangeManifestCompression.Brotli);
        var incorrectManifest = serializer.Create(manifest.Entries.Select(entry => entry with
        {
            ArtifactLength = entry.ArtifactLength + 1,
        }));

        await targetStore.MoveAsync(
            ArchiveChangeManifest.BrotliFileName,
            $"{ArchiveInternalFolderNames.TemporaryUploads}/original-change-manifest.json.br");
        await UploadChangeManifestAsync(
            targetStore,
            serializer,
            incorrectManifest,
            ArchiveChangeManifestCompression.Brotli);

        var verifyResult = await synchronizer.VerifyAsync(
            new SyncRunRequest(sourceRoot, ByteForByte: true));

        Assert.IsTrue(verifyResult.Completed);
        Assert.AreEqual(1, verifyResult.UnchangedCount);
    }

    [TestMethod]
    public async Task SyncAsyncRejectsTargetUploadThatDoesNotConsumeCompleteZip()
    {
        var sourceRoot = Path.Combine(
            Path.GetTempPath(),
            $"yabt-partial-upload-test-{Guid.NewGuid():N}");
        var timeProvider = new FixedTimeProvider(
            new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero));
        var sourceStore = new MemoryObjectStore(timeProvider);
        var targetStore = new MemoryObjectStore(timeProvider);
        await UploadTextObjectAsync(sourceStore, "file.txt", "source content");
        var partialTargetStore = new DataReadGuardObjectStore(targetStore)
        {
            StopUploadsAfterEmptyRead = true,
        };
        var descriptor = CreateRootDescriptor(
            [new BackupRootStore("target", FixedBackupRootStoreResolver.StoreKindValue)]);

        using var serviceProvider = CreateStreamingServices(
            sourceRoot,
            descriptor,
            sourceStore,
            partialTargetStore,
            timeProvider,
            new FolderPolicy(ZipArchiveFormatName.Value)).BuildServiceProvider();
        var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();

        var exception = await Assert.ThrowsExactlyAsync<YabtSyncException>(() => synchronizer.SyncAsync(
            new SyncRunRequest(sourceRoot)));

        StringAssert.Contains(exception.ToString(), "before consuming all projected content");
        Assert.IsFalse(targetStore.TryGetObject(ArchiveChangeManifest.BrotliFileName, out _));
        Assert.IsFalse(targetStore.TryGetObject(ArchiveChangeManifest.UncompressedFileName, out _));
        var partialPackage = targetStore.Snapshot().Single();
        Assert.IsTrue(partialPackage.Key.EndsWith(".zip", StringComparison.Ordinal));
        Assert.AreEqual(0, partialPackage.Content.Length);
    }

    [TestMethod]
    public async Task SyncAsyncRejectsProjectedLengthThatDoesNotMatchReadContent()
    {
        var sourceRoot = Path.Combine(
            Path.GetTempPath(),
            $"yabt-source-length-test-{Guid.NewGuid():N}");
        var timeProvider = new FixedTimeProvider(
            new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero));
        var sourceStore = new MemoryObjectStore(timeProvider);
        var targetStore = new MemoryObjectStore(timeProvider);
        await UploadTextObjectAsync(sourceStore, "file.txt", "source content");
        var inaccurateSourceStore = new DataReadGuardObjectStore(sourceStore)
        {
            ContentLengthAdjustment = 1,
        };
        var descriptor = CreateRootDescriptor(
            [new BackupRootStore("target", FixedBackupRootStoreResolver.StoreKindValue)]);

        using var serviceProvider = CreateStreamingServices(
            sourceRoot,
            descriptor,
            inaccurateSourceStore,
            targetStore,
            timeProvider).BuildServiceProvider();
        var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();

        var exception = await Assert.ThrowsExactlyAsync<YabtSyncException>(
            () => synchronizer.SyncAsync(new SyncRunRequest(sourceRoot)));

        StringAssert.Contains(exception.ToString(), "reported length");
        Assert.IsFalse(targetStore.TryGetObject(ArchiveChangeManifest.BrotliFileName, out _));
        Assert.IsFalse(targetStore.TryGetObject(ArchiveChangeManifest.UncompressedFileName, out _));
    }

    [TestMethod]
    public async Task SyncAsyncStoresChangeManifestOutsideExplicitLivePrefix()
    {
        var sourceRoot = Path.Combine(
            Path.GetTempPath(),
            $"yabt-explicit-live-test-{Guid.NewGuid():N}");
        var timeProvider = new FixedTimeProvider(
            new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero));
        var sourceStore = new MemoryObjectStore(timeProvider);
        var targetStore = new MemoryObjectStore(timeProvider);
        await UploadTextObjectAsync(sourceStore, "live/file.txt", "source content");
        var descriptor = new BackupRootDescriptor
        (
            BackupRootDescriptor.ExpectedDocumentType,
            1,
            "source-archive",
            new DateTimeOffset(2026, 5, 31, 0, 0, 0, TimeSpan.Zero),
            new ArchiveLayout("live", "hist"),
            [new BackupRootStore("target", FixedBackupRootStoreResolver.StoreKindValue)],
            "source"
        );

        using var serviceProvider = CreateStreamingServices(
            sourceRoot,
            descriptor,
            sourceStore,
            targetStore,
            timeProvider).BuildServiceProvider();
        var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();

        var syncResult = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));
        var verifyResult = await synchronizer.VerifyAsync(new SyncRunRequest(sourceRoot));

        Assert.IsTrue(syncResult.Completed);
        Assert.IsTrue(verifyResult.Completed);
        Assert.IsTrue(targetStore.TryGetObject("live/file.txt", out _));
        Assert.IsTrue(targetStore.TryGetObject(
            ArchiveChangeManifest.BrotliFileName,
            out var manifestObject));
        Assert.IsFalse(targetStore.TryGetObject(
            $"live/{ArchiveChangeManifest.BrotliFileName}",
            out _));
        Assert.IsFalse(targetStore.TryGetObject(
            $"live/{ArchiveChangeManifest.UncompressedFileName}",
            out _));

        var serializer = serviceProvider.GetRequiredService<IChangeManifestSerializer>();
        await using var content = new MemoryStream(manifestObject.Content.ToArray(), writable: false);
        var manifest = await ReadChangeManifestAsync(
            serializer,
            content,
            ArchiveChangeManifestCompression.Brotli);
        var manifestEntry = manifest.Entries.Single();
        Assert.AreEqual("file.txt", manifestEntry.RelativePath);
        Assert.IsNull(manifestEntry.ArtifactLength);
    }

    [TestMethod]
    public async Task SyncAsyncRejectsLayoutsOverlappingTemporaryUploadPrefix()
    {
        var layouts = new[]
        {
            new ArchiveLayout(ArchiveInternalFolderNames.TemporaryUploads, "hist"),
            new ArchiveLayout("live", $"{ArchiveInternalFolderNames.TemporaryUploads}/history"),
            new ArchiveLayout($"{ArchiveInternalFolderNames.TemporaryUploads}/live", "hist"),
            new ArchiveLayout("live", ".YABT-TMP/history"),
        };

        foreach (var layout in layouts)
        {
            var sourceRoot = Path.Combine(
                Path.GetTempPath(),
                $"yabt-reserved-layout-test-{Guid.NewGuid():N}");
            var sourceStore = new MemoryObjectStore();
            var targetStore = new MemoryObjectStore();
            var descriptor = new BackupRootDescriptor
            (
                BackupRootDescriptor.ExpectedDocumentType,
                1,
                "source-archive",
                new DateTimeOffset(2026, 5, 31, 0, 0, 0, TimeSpan.Zero),
                layout,
                [new BackupRootStore("target", FixedBackupRootStoreResolver.StoreKindValue)],
                "source"
            );
            using var serviceProvider = CreateStreamingServices(
                sourceRoot,
                descriptor,
                sourceStore,
                targetStore).BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();

            var exception = await Assert.ThrowsExactlyAsync<YabtSyncException>(
                () => synchronizer.SyncAsync(new SyncRunRequest(sourceRoot)));

            StringAssert.Contains(exception.Message, ArchiveInternalFolderNames.TemporaryUploads);
        }
    }

    [TestMethod]
    public async Task SyncAndVerifyIgnoreFilesystemTemporaryUploadWorkspace()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var targetRoot = Path.Combine(workspace, "target");
            var orphanPath = Path.Combine
            (
                targetRoot,
                ".YABT-TMP",
                "interrupted-upload.tmp"
            );
            await InitializeSourceRootAsync(sourceRoot, targetRoot);
            await WriteTextFileAsync(Path.Combine(sourceRoot, "file.txt"), "source content");
            await WriteTextFileAsync(orphanPath, "partial content");

            using var serviceProvider = CreateServices().BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();

            var syncResult = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));
            var verifyResult = await synchronizer.VerifyAsync(new SyncRunRequest(sourceRoot));

            Assert.IsTrue(syncResult.Completed);
            Assert.AreEqual(1, syncResult.NewCount);
            Assert.AreEqual(0, syncResult.ExtraCount);
            Assert.IsTrue(verifyResult.Completed);
            Assert.AreEqual(1, verifyResult.UnchangedCount);
            Assert.AreEqual(0, verifyResult.ExtraCount);
            Assert.IsTrue(File.Exists(orphanPath));

            var manifestSerializer = serviceProvider.GetRequiredService<IChangeManifestSerializer>();
            await using var manifestContent = File.OpenRead(Path.Combine(
                targetRoot,
                ArchiveChangeManifest.BrotliFileName));
            var manifest = await ReadChangeManifestAsync(
                manifestSerializer,
                manifestContent,
                ArchiveChangeManifestCompression.Brotli);
            var manifestEntry = manifest.Entries.Single();
            Assert.AreEqual("file.txt", manifestEntry.RelativePath);
            Assert.IsNull(manifestEntry.ArtifactLength);
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(81_957)]
    public async Task SyncAsyncReadsChangedMirrorSourceOnlyOnce(int mismatchIndex)
    {
        const int contentLength = (81_920 * 2) + 257;

        var sourceRoot = Path.Combine(
            Path.GetTempPath(),
            $"yabt-single-read-mirror-test-{Guid.NewGuid():N}");
        var timeProvider = new FixedTimeProvider(
            new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero));
        var sourceBytes = Enumerable.Range(0, contentLength)
            .Select(index => (byte)(index % 251))
            .ToArray();
        var targetBytes = sourceBytes.ToArray();
        targetBytes[mismatchIndex] ^= 0xff;

        var innerSourceStore = new MemoryObjectStore(timeProvider);
        var sourceStore = new DataReadGuardObjectStore(innerSourceStore)
        {
            ReturnNonSeekableDataStreams = true,
        };
        var targetStore = new MemoryObjectStore(timeProvider);
        await UploadObjectAsync(innerSourceStore, "large.bin", sourceBytes);
        await UploadObjectAsync(targetStore, "large.bin", targetBytes);
        var descriptor = CreateRootDescriptor(
            [new BackupRootStore("target", FixedBackupRootStoreResolver.StoreKindValue)]);

        using var serviceProvider = CreateStreamingServices(
            sourceRoot,
            descriptor,
            sourceStore,
            targetStore,
            timeProvider).BuildServiceProvider();
        var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();

        var result = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));

        Assert.IsTrue(result.Completed);
        Assert.AreEqual(1, result.ChangedCount);
        Assert.AreEqual(1, sourceStore.GetOpenReadCount("large.bin"));
        Assert.AreEqual(contentLength, sourceStore.GetBytesRead("large.bin"));
        Assert.IsTrue(targetStore.TryGetObject("large.bin", out var targetObject));
        CollectionAssert.AreEqual(sourceBytes, targetObject.Content.ToArray());
    }

    [TestMethod]
    public async Task SyncAsyncRejectsComparedSourceLengthMismatchBeforeTargetMutation()
    {
        var sourceRoot = Path.Combine(
            Path.GetTempPath(),
            $"yabt-compared-length-test-{Guid.NewGuid():N}");
        var timeProvider = new FixedTimeProvider(
            new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero));
        var sourceBytes = "actual source bytes"u8.ToArray();
        var targetBytes = Enumerable.Repeat((byte)0x5a, sourceBytes.Length + 1).ToArray();
        var innerSourceStore = new MemoryObjectStore(timeProvider);
        var sourceStore = new DataReadGuardObjectStore(innerSourceStore)
        {
            ContentLengthAdjustment = 1,
            ReturnNonSeekableDataStreams = true,
        };
        var targetStore = new MemoryObjectStore(timeProvider);
        await UploadObjectAsync(innerSourceStore, "file.bin", sourceBytes);
        await UploadObjectAsync(targetStore, "file.bin", targetBytes);
        var descriptor = CreateRootDescriptor(
            [new BackupRootStore("target", FixedBackupRootStoreResolver.StoreKindValue)]);

        using var serviceProvider = CreateStreamingServices(
            sourceRoot,
            descriptor,
            sourceStore,
            targetStore,
            timeProvider).BuildServiceProvider();
        var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();

        var exception = await Assert.ThrowsExactlyAsync<YabtSyncException>(
            () => synchronizer.SyncAsync(new SyncRunRequest(sourceRoot)));

        StringAssert.Contains(exception.ToString(), "reported length");
        Assert.AreEqual(1, sourceStore.GetOpenReadCount("file.bin"));
        Assert.AreEqual(sourceBytes.Length, sourceStore.GetBytesRead("file.bin"));
        var targetObjects = targetStore.Snapshot();
        Assert.AreEqual(1, targetObjects.Count);
        Assert.AreEqual("file.bin", targetObjects[0].Key);
        CollectionAssert.AreEqual(targetBytes, targetObjects[0].Content.ToArray());
        Assert.IsFalse(targetStore.TryGetObject(ArchiveChangeManifest.BrotliFileName, out _));
        Assert.IsFalse(targetStore.TryGetObject(
            ArchiveChangeManifest.InvalidationMarkerFileName,
            out _));
    }

    [TestMethod]
    public async Task SyncAsyncUploadsComparedSnapshotWhenBackingSourceChangesAfterComparison()
    {
        const string contentType = "application/x-yabt-captured";

        var sourceRoot = Path.Combine(
            Path.GetTempPath(),
            $"yabt-compared-snapshot-test-{Guid.NewGuid():N}");
        var timeProvider = new FixedTimeProvider(
            new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero));
        var sourceBytes = Enumerable.Range(0, 100_333)
            .Select(index => (byte)(index % 241))
            .ToArray();
        var replacementBytes = sourceBytes
            .Select(value => (byte)(value ^ 0xff))
            .ToArray();
        var oldTargetBytes = sourceBytes.ToArray();
        oldTargetBytes[0] ^= 0xff;
        var sourceMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["origin"] = "captured",
        };
        var replacementMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["origin"] = "replacement",
        };

        var innerSourceStore = new MemoryObjectStore(timeProvider);
        var sourceStore = new DataReadGuardObjectStore(innerSourceStore)
        {
            ReturnNonSeekableDataStreams = true,
        };
        var innerTargetStore = new MemoryObjectStore(timeProvider);
        var sourceMutationCount = 0;
        var targetStore = new DataReadGuardObjectStore(innerTargetStore)
        {
            AfterMoveAsync = async (source, _, cancellationToken) =>
            {
                if (!string.Equals(source, "snapshot.bin", StringComparison.Ordinal))
                {
                    return;
                }

                sourceMutationCount++;
                innerSourceStore.Clear();
                await UploadObjectAsync(
                    innerSourceStore,
                    "snapshot.bin",
                    replacementBytes,
                    "application/x-yabt-replacement",
                    replacementMetadata,
                    cancellationToken);
            },
        };
        await UploadObjectAsync(
            innerSourceStore,
            "snapshot.bin",
            sourceBytes,
            contentType,
            sourceMetadata);
        await UploadObjectAsync(innerTargetStore, "snapshot.bin", oldTargetBytes);
        var descriptor = CreateRootDescriptor(
            [new BackupRootStore("target", FixedBackupRootStoreResolver.StoreKindValue)]);

        using var serviceProvider = CreateStreamingServices(
            sourceRoot,
            descriptor,
            sourceStore,
            targetStore,
            timeProvider).BuildServiceProvider();
        var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();

        var result = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));

        Assert.IsTrue(result.Completed);
        Assert.AreEqual(1, result.ChangedCount);
        Assert.AreEqual(1, sourceMutationCount);
        Assert.AreEqual(1, sourceStore.GetOpenReadCount("snapshot.bin"));
        Assert.AreEqual(sourceBytes.Length, sourceStore.GetBytesRead("snapshot.bin"));
        Assert.IsTrue(innerTargetStore.TryGetObject("snapshot.bin", out var capturedTarget));
        CollectionAssert.AreEqual(sourceBytes, capturedTarget.Content.ToArray());
        Assert.AreEqual(contentType, capturedTarget.ContentType);
        Assert.AreEqual("captured", capturedTarget.Metadata["origin"]);
        Assert.IsTrue(innerSourceStore.TryGetObject("snapshot.bin", out var changedSource));
        CollectionAssert.AreEqual(replacementBytes, changedSource.Content.ToArray());
    }

    [TestMethod]
    public async Task ByteForByteSyncReadsEachZipSourceOnlyOnceWhenTargetPackageChanged()
    {
        var sourceRoot = Path.Combine(
            Path.GetTempPath(),
            $"yabt-single-read-zip-test-{Guid.NewGuid():N}");
        var timeProvider = new FixedTimeProvider(
            new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero));
        var firstSourceBytes = Enumerable.Range(0, 90_000)
            .Select(index => (byte)(index % 239))
            .ToArray();
        var secondSourceBytes = Enumerable.Range(0, 12_345)
            .Select(index => (byte)(index % 227))
            .ToArray();
        var innerSourceStore = new MemoryObjectStore(timeProvider);
        var sourceStore = new DataReadGuardObjectStore(innerSourceStore)
        {
            ReturnNonSeekableDataStreams = true,
        };
        var targetStore = new MemoryObjectStore(timeProvider);
        await UploadObjectAsync(innerSourceStore, "first.bin", firstSourceBytes);
        await UploadObjectAsync(innerSourceStore, "folder/second.bin", secondSourceBytes);
        var descriptor = CreateRootDescriptor(
            [new BackupRootStore("target", FixedBackupRootStoreResolver.StoreKindValue)]);

        using var serviceProvider = CreateStreamingServices(
            sourceRoot,
            descriptor,
            sourceStore,
            targetStore,
            timeProvider,
            new FolderPolicy(ZipArchiveFormatName.Value)).BuildServiceProvider();
        var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
        var initialResult = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));
        Assert.IsTrue(initialResult.Completed);

        var originalPackage = targetStore.Snapshot().Single(archiveObject =>
            archiveObject.Key.EndsWith(".zip", StringComparison.Ordinal));
        var changedPackageBytes = originalPackage.Content.ToArray();
        changedPackageBytes[0] ^= 0xff;
        await targetStore.MoveAsync(
            originalPackage.Key,
            $"{ArchiveInternalFolderNames.TemporaryUploads}/original-package.zip");
        await UploadObjectAsync(
            targetStore,
            originalPackage.Key,
            changedPackageBytes,
            originalPackage.ContentType,
            originalPackage.Metadata);
        sourceStore.ResetCounts();

        var result = await synchronizer.SyncAsync(
            new SyncRunRequest(sourceRoot, ByteForByte: true));

        Assert.IsTrue(result.Completed);
        Assert.AreEqual(1, result.ChangedCount);
        Assert.AreEqual(1, sourceStore.GetOpenReadCount("first.bin"));
        Assert.AreEqual(firstSourceBytes.Length, sourceStore.GetBytesRead("first.bin"));
        Assert.AreEqual(1, sourceStore.GetOpenReadCount("folder/second.bin"));
        Assert.AreEqual(secondSourceBytes.Length, sourceStore.GetBytesRead("folder/second.bin"));
        Assert.IsTrue(targetStore.TryGetObject(originalPackage.Key, out var repairedPackage));
        CollectionAssert.AreEqual(
            originalPackage.Content.ToArray(),
            repairedPackage.Content.ToArray());
    }

    [TestMethod]
    public async Task ByteForByteComparisonDetectsContentChangeWithSameFingerprint()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var targetRoot = Path.Combine(workspace, "target");
            var sourceFile = Path.Combine(sourceRoot, "file.txt");
            await InitializeSourceRootAsync(sourceRoot, targetRoot);
            await WriteTextFileAsync(sourceFile, "first-value");
            var originalLastWriteTimeUtc = File.GetLastWriteTimeUtc(sourceFile);

            using var serviceProvider = CreateServices().BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
            var firstResult = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));
            Assert.IsTrue(firstResult.Completed);

            await WriteTextFileAsync(sourceFile, "other-value");
            File.SetLastWriteTimeUtc(sourceFile, originalLastWriteTimeUtc);

            var fastVerifyResult = await synchronizer.VerifyAsync(new SyncRunRequest(sourceRoot));
            var fullVerifyResult = await synchronizer.VerifyAsync(
                new SyncRunRequest(sourceRoot, ByteForByte: true));

            Assert.IsTrue(fastVerifyResult.Completed);
            Assert.AreEqual(1, fastVerifyResult.UnchangedCount);
            Assert.IsFalse(fullVerifyResult.Completed);
            Assert.AreEqual(1, fullVerifyResult.ChangedCount);

            var fullSyncResult = await synchronizer.SyncAsync(
                new SyncRunRequest(sourceRoot, ByteForByte: true));

            Assert.IsTrue(fullSyncResult.Completed);
            Assert.AreEqual(1, fullSyncResult.ChangedCount);
            AssertTextFile(Path.Combine(targetRoot, "file.txt"), "other-value");

            var stableVerifyResult = await synchronizer.VerifyAsync(new SyncRunRequest(sourceRoot));
            Assert.IsTrue(stableVerifyResult.Completed);
            Assert.AreEqual(1, stableVerifyResult.UnchangedCount);

            await WriteTextFileAsync(Path.Combine(targetRoot, "file.txt"), "wrong-value");

            var fastCorruptionResult = await synchronizer.VerifyAsync(new SyncRunRequest(sourceRoot));
            var fullCorruptionResult = await synchronizer.VerifyAsync(
                new SyncRunRequest(sourceRoot, ByteForByte: true));

            Assert.IsTrue(fastCorruptionResult.Completed);
            Assert.AreEqual(1, fastCorruptionResult.UnchangedCount);
            Assert.IsFalse(fullCorruptionResult.Completed);
            Assert.AreEqual(1, fullCorruptionResult.ChangedCount);
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task SyncAsyncRebuildsInvalidChangeManifestAfterFullComparison()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var targetRoot = Path.Combine(workspace, "target");
            var manifestPath = Path.Combine(targetRoot, ArchiveChangeManifest.BrotliFileName);
            await InitializeSourceRootAsync(sourceRoot, targetRoot);
            await WriteTextFileAsync(Path.Combine(sourceRoot, "file.txt"), "source content");

            using var serviceProvider = CreateServices().BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
            var firstResult = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));
            Assert.IsTrue(firstResult.Completed);

            await File.WriteAllTextAsync(manifestPath, "{ invalid manifest");

            await Assert.ThrowsExactlyAsync<YabtSyncException>(() => synchronizer.VerifyAsync(
                new SyncRunRequest(sourceRoot)));

            var fullVerifyResult = await synchronizer.VerifyAsync(
                new SyncRunRequest(sourceRoot, ByteForByte: true));
            Assert.IsTrue(fullVerifyResult.Completed);
            Assert.AreEqual(1, fullVerifyResult.UnchangedCount);

            var recoveryResult = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));

            Assert.IsTrue(recoveryResult.Completed);
            Assert.AreEqual(0, recoveryResult.NewCount);
            Assert.AreEqual(0, recoveryResult.ChangedCount);
            Assert.AreEqual(1, recoveryResult.UnchangedCount);

            var serializer = serviceProvider.GetRequiredService<IChangeManifestSerializer>();
            await using (var manifestContent = File.OpenRead(manifestPath))
            {
                var rebuiltManifest = await ReadChangeManifestAsync(
                    serializer,
                    manifestContent,
                    ArchiveChangeManifestCompression.Brotli);
                Assert.AreEqual(1, rebuiltManifest.Entries.Count());
            }

            var historyRoot = Path.Combine(targetRoot, ".yabt-hist");
            Assert.IsFalse(
                Directory.Exists(historyRoot) &&
                Directory.GetFiles(
                    historyRoot,
                    ArchiveChangeManifest.BrotliFileName,
                    SearchOption.AllDirectories).Any());
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task SyncAsyncMovesChangedTargetObjectToHistory()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var targetRoot = Path.Combine(workspace, "target");
            await InitializeSourceRootAsync(sourceRoot, targetRoot);
            await WriteTextFileAsync(
                Path.Combine(sourceRoot, "folder", "file.txt"),
                "new content");
            await WriteTextFileAsync(
                Path.Combine(targetRoot, "folder", "file.txt"),
                "old content");

            using var serviceProvider = CreateServices().BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();

            var result = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));

            Assert.IsTrue(result.Completed);
            Assert.AreEqual(1, result.ChangedCount);
            AssertTextFile(Path.Combine(targetRoot, "folder", "file.txt"), "new content");

            var historicalFiles = Directory.GetFiles(
                Path.Combine(targetRoot, ".yabt-hist"),
                "*",
                SearchOption.AllDirectories);
            Assert.AreEqual(1, historicalFiles.Length);
            AssertTextFile(historicalFiles[0], "old content");
            Assert.IsTrue(
                historicalFiles[0].EndsWith(
                    Path.Combine("folder", "file.txt"),
                    StringComparison.Ordinal),
                $"Historical path was '{historicalFiles[0]}'.");
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task SyncAsyncDoesNotCopySourceHistoryWhenLivePrefixIsEmpty()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var targetRoot = Path.Combine(workspace, "target");
            await InitializeSourceRootAsync(sourceRoot, targetRoot);
            await WriteTextFileAsync(
                Path.Combine(sourceRoot, "folder", "file.txt"),
                "source content");
            await WriteTextFileAsync(
                Path.Combine(sourceRoot, ".yabt-hist", "old.txt"),
                "historical content");

            using var serviceProvider = CreateServices().BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();

            var result = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));

            Assert.IsTrue(result.Completed);
            Assert.AreEqual(1, result.NewCount);
            AssertTextFile(Path.Combine(targetRoot, "folder", "file.txt"), "source content");
            Assert.IsFalse(File.Exists(Path.Combine(targetRoot, ".yabt-hist", "old.txt")));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task SyncAsyncDiscoversRootDescriptorInBaseFolder()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var sourceChildRoot = Path.Combine(sourceRoot, "folder");
            var targetRoot = Path.Combine(workspace, "target");
            await InitializeSourceRootAsync(sourceRoot, targetRoot);
            await WriteTextFileAsync(
                Path.Combine(sourceChildRoot, "file.txt"),
                "source content");

            using var serviceProvider = CreateServices().BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();

            var result = await synchronizer.SyncAsync(new SyncRunRequest(sourceChildRoot));

            Assert.IsTrue(result.Completed);
            Assert.AreEqual(1, result.NewCount);
            AssertTextFile(Path.Combine(targetRoot, "file.txt"), "source content");
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task SyncAsyncUsesRequestedTargetStoreId()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var firstTargetRoot = Path.Combine(workspace, "target-first");
            var secondTargetRoot = Path.Combine(workspace, "target-second");
            await InitializeSourceRootAsync(
                sourceRoot,
                [
                    CreateFileSystemStore("first", firstTargetRoot),
                    CreateFileSystemStore("second", secondTargetRoot),
                ]);
            await WriteTextFileAsync(
                Path.Combine(sourceRoot, "folder", "file.txt"),
                "source content");

            using var serviceProvider = CreateServices().BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();

            var result = await synchronizer.SyncAsync(new SyncRunRequest(
                sourceRoot,
                TargetStoreId: "SECOND"));

            Assert.IsTrue(result.Completed);
            AssertTextFile(Path.Combine(secondTargetRoot, "folder", "file.txt"), "source content");
            Assert.IsFalse(File.Exists(Path.Combine(firstTargetRoot, "folder", "file.txt")));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task SyncAsyncUsesDescriptorDefaultStoreId()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var firstTargetRoot = Path.Combine(workspace, "target-first");
            var secondTargetRoot = Path.Combine(workspace, "target-second");
            await InitializeSourceRootAsync(
                sourceRoot,
                [
                    CreateFileSystemStore("first", firstTargetRoot),
                    CreateFileSystemStore("second", secondTargetRoot),
                ],
                defaultStoreId: "second");
            await WriteTextFileAsync(
                Path.Combine(sourceRoot, "folder", "file.txt"),
                "source content");

            using var serviceProvider = CreateServices().BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();

            var result = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));

            Assert.IsTrue(result.Completed);
            AssertTextFile(Path.Combine(secondTargetRoot, "folder", "file.txt"), "source content");
            Assert.IsFalse(File.Exists(Path.Combine(firstTargetRoot, "folder", "file.txt")));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task SyncAsyncUsesFirstTargetStoreWhenNoStoreIdIsSelected()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var firstTargetRoot = Path.Combine(workspace, "target-first");
            var secondTargetRoot = Path.Combine(workspace, "target-second");
            await InitializeSourceRootAsync(
                sourceRoot,
                [
                    CreateFileSystemStore("first", firstTargetRoot),
                    CreateFileSystemStore("second", secondTargetRoot),
                ]);
            await WriteTextFileAsync(
                Path.Combine(sourceRoot, "folder", "file.txt"),
                "source content");

            using var serviceProvider = CreateServices().BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();

            var result = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));

            Assert.IsTrue(result.Completed);
            AssertTextFile(Path.Combine(firstTargetRoot, "folder", "file.txt"), "source content");
            Assert.IsFalse(File.Exists(Path.Combine(secondTargetRoot, "folder", "file.txt")));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task VerifyAsyncReportsDifferencesWithoutMutatingTarget()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var targetRoot = Path.Combine(workspace, "target");
            await InitializeSourceRootAsync(sourceRoot, targetRoot);
            await WriteTextFileAsync(
                Path.Combine(sourceRoot, "folder", "file.txt"),
                "new content");
            await WriteTextFileAsync(
                Path.Combine(targetRoot, "folder", "file.txt"),
                "old content");

            using var serviceProvider = CreateServices().BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();

            var result = await synchronizer.VerifyAsync(new SyncRunRequest(sourceRoot));

            Assert.IsFalse(result.Completed);
            Assert.AreEqual(1, result.ChangedCount);
            AssertTextFile(Path.Combine(targetRoot, "folder", "file.txt"), "old content");
            Assert.IsFalse(Directory.Exists(Path.Combine(targetRoot, ".yabt-hist")));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    private static async Task AssertZipPackageNameCollisionRejectedAsync
    (
        bool createSourceFolderCollision
    )
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var targetRoot = Path.Combine(workspace, "target");
            var albumsRoot = Path.Combine(sourceRoot, "albums");
            var photosRoot = Path.Combine(albumsRoot, "photos");
            await InitializeSourceRootAsync(sourceRoot, targetRoot);
            await WritePolicyAsync(
                photosRoot,
                ZipArchiveFormatName.Value);
            await WriteTextFileAsync(
                Path.Combine(photosRoot, "image.txt"),
                "image content");

            var timeProvider = new FixedTimeProvider(
                new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));
            using var serviceProvider = CreateServices(timeProvider).BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();

            var firstResult = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));

            Assert.IsTrue(firstResult.Completed);
            var packagePath = Directory.GetFiles(
                Path.Combine(targetRoot, "albums"),
                "photos.*.zip").Single();
            var collisionPath = Path.Combine(
                albumsRoot,
                Path.GetFileName(packagePath));
            if (createSourceFolderCollision)
            {
                await WriteTextFileAsync(
                    Path.Combine(collisionPath, "file.txt"),
                    "ordinary sibling content");
            }
            else
            {
                await WriteTextFileAsync(
                    collisionPath,
                    "ordinary sibling content");
            }

            var exception = await Assert.ThrowsExactlyAsync<YabtSyncException>(
                () => synchronizer.SyncAsync(new SyncRunRequest(sourceRoot)));

            StringAssert.Contains(exception.Message, "conflicts with a source item");
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    private static ServiceCollection CreateServices(TimeProvider? timeProvider = default)
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        if (timeProvider is not null)
        {
            services.AddSingleton(timeProvider);
        }

        services.AddYabtFileSystemObjectStore();
        services.AddYabtMirrorFormatProjector();
        services.AddYabtZipFormatProjector();
        services.AddYabtMetadata();
        services.AddYabtSync();

        return services;
    }

    private static ServiceCollection CreateStreamingServices
    (
        string sourceRoot,
        BackupRootDescriptor descriptor,
        IObjectStore sourceStore,
        IObjectStore targetStore,
        TimeProvider? timeProvider = default,
        FolderPolicy? folderPolicy = default
    )
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        if (timeProvider is not null)
        {
            services.AddSingleton(timeProvider);
        }

        services.AddYabtMetadata();
        services.AddSingleton<IBackupRootLocator>(new FixedBackupRootLocator(
            sourceRoot,
            descriptor));
        services.AddSingleton<IFolderPolicyReader>(new FixedFolderPolicyReader(
            folderPolicy ?? FolderPolicy.Default));
        services.AddSingleton<IBackupRootStoreResolver>(new FixedBackupRootStoreResolver(targetStore));
        services.AddSingleton<ISourceRootObjectStoreResolver>(new FixedSourceRootObjectStoreResolver(sourceStore));
        services.AddYabtMirrorFormatProjector();
        services.AddYabtZipFormatProjector();
        services.AddYabtSync();

        return services;
    }

    private static string CreateWorkspacePath()
    {
        return Path.Combine(
            Path.GetTempPath(),
            $"yabt-sync-tests-{Guid.NewGuid():N}");
    }

    private static Task InitializeSourceRootAsync
    (
        string sourceRoot,
        string targetRoot
    ) => InitializeSourceRootAsync(
        sourceRoot,
        [CreateFileSystemStore("target", targetRoot)]);

    private static async Task InitializeSourceRootAsync
    (
        string sourceRoot,
        IEnumerable<BackupRootStore> stores,
        string? defaultStoreId = default
    )
    {
        Directory.CreateDirectory(sourceRoot);

        var descriptor = CreateRootDescriptor(stores, defaultStoreId);
        await using var stream = File.Create(Path.Combine(sourceRoot, BackupRootFileNames.Primary));
        await JsonSerializer.SerializeAsync(
            stream,
            descriptor,
            JsonOptions);
    }

    private static async Task WritePolicyAsync
    (
        string folderPath,
        string format
    )
    {
        Directory.CreateDirectory(folderPath);

        await using var stream = File.Create(Path.Combine(folderPath, FolderPolicyFileNames.Primary));
        await JsonSerializer.SerializeAsync(
            stream,
            new FolderPolicy(format),
            JsonOptions);
    }

    private static BackupRootDescriptor CreateRootDescriptor
    (
        IEnumerable<BackupRootStore> stores,
        string? defaultStoreId = default,
        string? changeManifestCompression = default
    )
    {
        return new
        (
            BackupRootDescriptor.ExpectedDocumentType,
            1,
            "source-archive",
            new DateTimeOffset(2026, 5, 31, 0, 0, 0, TimeSpan.Zero),
            ArchiveLayout.Default,
            stores,
            "source",
            DefaultStoreId: defaultStoreId,
            ChangeManifestCompression: changeManifestCompression
        );
    }

    private static string GetChangeManifestFileName(string compression) => compression switch
    {
        ArchiveChangeManifestCompression.Brotli => ArchiveChangeManifest.BrotliFileName,
        ArchiveChangeManifestCompression.None => ArchiveChangeManifest.UncompressedFileName,
        _ => throw new InvalidOperationException(
            $"Unsupported test change manifest compression '{compression}'."),
    };

    private static async Task<ArchiveChangeManifest> ReadChangeManifestAsync
    (
        IChangeManifestSerializer serializer,
        Stream content,
        string compression
    )
    {
        if (string.Equals(
                compression,
                ArchiveChangeManifestCompression.None,
                StringComparison.Ordinal))
        {
            return await serializer.ReadAsync(content);
        }

        if (!string.Equals(
                compression,
                ArchiveChangeManifestCompression.Brotli,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Unsupported test change manifest compression '{compression}'.");
        }

        await using var decompressedContent = new BrotliStream(
            content,
            CompressionMode.Decompress,
            leaveOpen: true);
        return await serializer.ReadAsync(decompressedContent);
    }

    private static async Task UploadChangeManifestAsync
    (
        MemoryObjectStore targetStore,
        IChangeManifestSerializer serializer,
        ArchiveChangeManifest manifest,
        string compression
    )
    {
        await using var content = new MemoryStream();
        if (string.Equals(
                compression,
                ArchiveChangeManifestCompression.Brotli,
                StringComparison.Ordinal))
        {
            await using (var compressedContent = new BrotliStream(
                content,
                CompressionLevel.Optimal,
                leaveOpen: true))
            {
                await serializer.WriteAsync(manifest, compressedContent);
            }
        }
        else if (string.Equals(
                     compression,
                     ArchiveChangeManifestCompression.None,
                     StringComparison.Ordinal))
        {
            await serializer.WriteAsync(manifest, content);
        }
        else
        {
            throw new InvalidOperationException(
                $"Unsupported test change manifest compression '{compression}'.");
        }

        content.Position = 0;
        await targetStore.UploadAsync(
            GetChangeManifestFileName(compression),
            content,
            string.Equals(
                compression,
                ArchiveChangeManifestCompression.Brotli,
                StringComparison.Ordinal) ?
                    "application/octet-stream" :
                    "application/json",
            new Dictionary<string, string>(StringComparer.Ordinal));
    }

    private static BackupRootStore CreateFileSystemStore
    (
        string id,
        string rootPath
    )
    {
        Directory.CreateDirectory(rootPath);

        return new BackupRootStore(id, FileSystemObjectStoreKind.Value)
        {
            ProviderProperties = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["rootPath"] = JsonSerializer.SerializeToElement(rootPath, JsonOptions),
            },
        };
    }

    private static async Task WriteTextFileAsync
    (
        string path,
        string content
    )
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException());
        await File.WriteAllTextAsync(path, content);
    }

    private static async Task UploadTextObjectAsync
    (
        MemoryObjectStore store,
        string key,
        string content
    )
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content), writable: false);
        await store.UploadAsync(
            key,
            stream,
            "text/plain",
            new Dictionary<string, string>(StringComparer.Ordinal));
    }

    private static async Task UploadObjectAsync
    (
        MemoryObjectStore store,
        string key,
        ReadOnlyMemory<byte> content,
        string contentType = "application/octet-stream",
        IReadOnlyDictionary<string, string>? metadata = default,
        CancellationToken cancellationToken = default
    )
    {
        await using var stream = new MemoryStream(content.ToArray(), writable: false);
        await store.UploadAsync(
            key,
            stream,
            contentType,
            metadata ?? new Dictionary<string, string>(StringComparer.Ordinal),
            cancellationToken);
    }

    private static void AssertTextFile
    (
        string path,
        string expectedContent
    )
    {
        Assert.AreEqual(expectedContent, File.ReadAllText(path));
    }

    private static void DeleteWorkspace(string workspace)
    {
        if (Directory.Exists(workspace))
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    private sealed class DataReadGuardObjectStore(IObjectStore _innerStore) :
        IArchiveMutableObjectStore
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<string, int> _openReadCounts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _bytesRead = new(StringComparer.Ordinal);

        public bool RejectDataReads { get; set; }

        public bool HideContentLengths { get; set; }

        public bool ReturnNonSeekableDataStreams { get; set; }

        public long ContentLengthAdjustment { get; set; }

        public bool StopUploadsAfterEmptyRead { get; set; }

        public string? RejectMoveSource { get; set; }

        public string? RejectConditionalDeleteKey { get; set; }

        public Func<string, string, CancellationToken, Task>? AfterMoveAsync { get; set; }

        public Task EnsureReadyAsync(CancellationToken cancellationToken = default) =>
            _innerStore.EnsureReadyAsync(cancellationToken);

        public async Task UploadAsync
        (
            string key,
            Stream content,
            string contentType,
            IReadOnlyDictionary<string, string> metadata,
            CancellationToken cancellationToken = default
        )
        {
            if (!StopUploadsAfterEmptyRead)
            {
                await _innerStore.UploadAsync(
                    key,
                    content,
                    contentType,
                    metadata,
                    cancellationToken);
                return;
            }

            _ = await content.ReadAsync(Memory<byte>.Empty, cancellationToken);
            await using var partialContent = new MemoryStream([], writable: false);
            await _innerStore.UploadAsync(
                key,
                partialContent,
                contentType,
                metadata,
                cancellationToken);
        }

        public async Task<ArchiveObjectContent> OpenReadAsync
        (
            string key,
            CancellationToken cancellationToken = default
        )
        {
            var normalizedKey = ArchiveLayout.NormalizeObjectKey(key);
            if (RejectDataReads &&
                !string.Equals(
                    normalizedKey,
                    ArchiveChangeManifest.BrotliFileName,
                    StringComparison.Ordinal) &&
                !string.Equals(
                    normalizedKey,
                    ArchiveChangeManifest.UncompressedFileName,
                    StringComparison.Ordinal) &&
                !string.Equals(
                    normalizedKey,
                    ArchiveChangeManifest.InvalidationMarkerFileName,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Data object '{normalizedKey}' must not be opened.");
            }

            lock (_gate)
            {
                _openReadCounts.TryGetValue(normalizedKey, out var currentCount);
                _openReadCounts[normalizedKey] = currentCount + 1;
            }

            var content = await _innerStore.OpenReadAsync(
                normalizedKey,
                cancellationToken);
            if (!ReturnNonSeekableDataStreams)
            {
                return content;
            }

            return new
            (
                new CountingNonSeekableReadStream(
                    content.Content,
                    bytesRead => AddBytesRead(normalizedKey, bytesRead)),
                content.ContentType,
                content.Metadata
            );
        }

        public Task<bool> ExistsAsync
        (
            string key,
            CancellationToken cancellationToken = default
        ) => _innerStore.ExistsAsync(key, cancellationToken);

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
            if (_innerStore is not IArchiveMutableObjectStore mutableStore)
            {
                throw new InvalidOperationException(
                    "The wrapped test store does not support guarded archive mutations.");
            }

            return mutableStore.TryReplaceIfContentHashMatchesAsync(
                key,
                expectedContentHash,
                replacementContent,
                contentType,
                metadata,
                cancellationToken);
        }

        public Task<bool> TryDeleteIfContentHashMatchesAsync
        (
            string key,
            string expectedContentHash,
            CancellationToken cancellationToken = default
        )
        {
            if (string.Equals(key, RejectConditionalDeleteKey, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Conditional deletion of '{key}' was rejected by the test store.");
            }

            if (_innerStore is not IArchiveMutableObjectStore mutableStore)
            {
                throw new InvalidOperationException(
                    "The wrapped test store does not support guarded archive mutations.");
            }

            return mutableStore.TryDeleteIfContentHashMatchesAsync(
                key,
                expectedContentHash,
                cancellationToken);
        }

        public async IAsyncEnumerable<ArchiveFolderItem> GetFolderItemsAsync
        (
            string? folderPrefix,
            bool recursive = false,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            var items = _innerStore.GetFolderItemsAsync(
                folderPrefix,
                recursive,
                cancellationToken);
            await foreach (var item in items)
            {
                if (item.Object is not null &&
                    (HideContentLengths || ContentLengthAdjustment != 0))
                {
                    long? contentLength = HideContentLengths || !item.Object.ContentLength.HasValue ?
                        null :
                        checked(item.Object.ContentLength.Value + ContentLengthAdjustment);
                    yield return item with
                    {
                        Object = item.Object with
                        {
                            ContentLength = contentLength,
                        },
                    };
                    continue;
                }

                yield return item;
            }
        }

        public async Task MoveAsync
        (
            string source,
            string destination,
            CancellationToken cancellationToken = default
        )
        {
            if (string.Equals(source, RejectMoveSource, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Move from '{source}' was rejected by the test store.");
            }

            await _innerStore.MoveAsync(source, destination, cancellationToken);
            if (AfterMoveAsync is not null)
            {
                await AfterMoveAsync(source, destination, cancellationToken);
            }
        }

        public Task MoveFolderAsync
        (
            string sourcePrefix,
            string destinationPrefix,
            CancellationToken cancellationToken = default
        ) => _innerStore.MoveFolderAsync(
            sourcePrefix,
            destinationPrefix,
            cancellationToken);

        public Task<IArchiveMutationLock> AcquireArchiveMutationLockAsync
        (
            CancellationToken cancellationToken = default
        )
        {
            if (_innerStore is not IArchiveMutationLockProvider lockProvider)
            {
                throw new InvalidOperationException(
                    "The wrapped test store does not provide archive mutation locking.");
            }

            return lockProvider.AcquireArchiveMutationLockAsync(cancellationToken);
        }

        public int GetOpenReadCount(string key)
        {
            var normalizedKey = ArchiveLayout.NormalizeObjectKey(key);
            lock (_gate)
            {
                return _openReadCounts.GetValueOrDefault(normalizedKey);
            }
        }

        public long GetBytesRead(string key)
        {
            var normalizedKey = ArchiveLayout.NormalizeObjectKey(key);
            lock (_gate)
            {
                return _bytesRead.GetValueOrDefault(normalizedKey);
            }
        }

        public void ResetCounts()
        {
            lock (_gate)
            {
                _openReadCounts.Clear();
                _bytesRead.Clear();
            }
        }

        private void AddBytesRead(string normalizedKey, int bytesRead)
        {
            lock (_gate)
            {
                _bytesRead.TryGetValue(normalizedKey, out var currentCount);
                _bytesRead[normalizedKey] = currentCount + bytesRead;
            }
        }
    }

    private sealed class FailingAfterFirstListedObjectStore : IObjectStore
    {
        public const string FirstContent = "first content";

        private static readonly byte[] FirstContentBytes = Encoding.UTF8.GetBytes(FirstContent);

        public Task EnsureReadyAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task UploadAsync
        (
            string key,
            Stream content,
            string contentType,
            IReadOnlyDictionary<string, string> metadata,
            CancellationToken cancellationToken = default
        )
        {
            throw new NotSupportedException();
        }

        public Task<ArchiveObjectContent> OpenReadAsync
        (
            string key,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.AreEqual("first.txt", ArchiveLayout.NormalizeObjectKey(key));

            return Task.FromResult(new ArchiveObjectContent(
                new MemoryStream(FirstContentBytes, writable: false),
                "text/plain"));
        }

        public Task<bool> ExistsAsync
        (
            string key,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(string.Equals(
                ArchiveLayout.NormalizeObjectKey(key),
                "first.txt",
                StringComparison.Ordinal));
        }

        public async IAsyncEnumerable<ArchiveFolderItem> GetFolderItemsAsync
        (
            string? folderPrefix,
            bool recursive = false,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            _ = folderPrefix;
            _ = recursive;
            cancellationToken.ThrowIfCancellationRequested();

            await Task.Yield();

            yield return ArchiveFolderItem.CreateObject(
                "first.txt",
                new
                (
                    "first.txt",
                    FirstContentBytes.Length
                ));

            throw new InvalidOperationException("Source enumeration failed after the first object.");
        }

        public Task MoveAsync
        (
            string source,
            string destination,
            CancellationToken cancellationToken = default
        )
        {
            throw new NotSupportedException();
        }

        public Task MoveFolderAsync
        (
            string sourcePrefix,
            string destinationPrefix,
            CancellationToken cancellationToken = default
        )
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FixedBackupRootLocator
    (
        string _rootPath,
        BackupRootDescriptor _descriptor
    ) : IBackupRootLocator
    {
        public Task<BackupRootLocation> LocateRootAsync
        (
            string startPath,
            CancellationToken cancellationToken = default
        )
        {
            _ = startPath;
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(new BackupRootLocation(
                _rootPath,
                _descriptor));
        }
    }

    private sealed class FixedFolderPolicyReader(FolderPolicy _policy) : IFolderPolicyReader
    {
        public Task<FolderPolicy> ReadPolicyAsync
        (
            string folderPath,
            CancellationToken cancellationToken = default
        )
        {
            _ = folderPath;
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(_policy);
        }
    }

    private sealed class FixedBackupRootStoreResolver(IObjectStore _targetStore) : IBackupRootStoreResolver
    {
        public const string StoreKindValue = "memory";

        public string StoreKind => StoreKindValue;

        public IObjectStore ResolveStore
        (
            BackupRootStore store,
            string descriptorRootPath
        )
        {
            _ = store;
            _ = descriptorRootPath;

            return _targetStore;
        }
    }

    private sealed class FixedSourceRootObjectStoreResolver(IObjectStore _sourceStore) : ISourceRootObjectStoreResolver
    {
        public IObjectStore ResolveSourceRoot(string rootPath)
        {
            _ = rootPath;

            return _sourceStore;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset _utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
