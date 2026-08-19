using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yabt.Core.Models;
using Yabt.Metadata;

namespace Yabt.Sync.Tests;

[TestClass]
public sealed class HistoryDeduplicatorTests
{
    [TestMethod]
    public async Task DeduplicateAsyncReplacesNewerDuplicateWithSelfDescribingReference()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var archiveRoot = Path.Combine(workspace, "archive");
            var targetRoot = Path.Combine(workspace, "target");
            await InitializeArchiveRootAsync(archiveRoot, targetRoot);
            var content = CreateContent(6_000);
            var olderPath = Path.Combine(
                targetRoot,
                ".yabt-hist",
                "20260818T100000Z",
                "docs",
                "report.pdf");
            var newerPath = Path.Combine(
                targetRoot,
                ".yabt-hist",
                "20260819T100000Z",
                "docs",
                "report.pdf");
            await WriteBytesAsync(olderPath, content);
            await WriteBytesAsync(newerPath, content);

            using var services = CreateServices().BuildServiceProvider();
            var deduplicator = services.GetRequiredService<IHistoryDeduplicator>();
            var result = await deduplicator.DeduplicateAsync(
                new HistoryDeduplicationRequest(archiveRoot));

            Assert.IsTrue(result.Completed);
            Assert.AreEqual(2, result.ScannedObjectCount);
            Assert.AreEqual(1, result.DuplicateGroupCount);
            Assert.AreEqual(1, result.ReplacedObjectCount);
            Assert.IsTrue(result.BytesSaved > 0);
            Assert.IsTrue(File.Exists(olderPath));
            Assert.IsFalse(File.Exists(newerPath));

            var referencePath = $"{newerPath}{ArchiveHistoryFileNames.ReferenceSuffix}";
            Assert.IsTrue(File.Exists(referencePath));
            var referenceSerializer = services.GetRequiredService<IHistoryReferenceSerializer>();
            await using (var referenceStream = File.OpenRead(referencePath))
            {
                var reference = await referenceSerializer.ReadAsync(referenceStream);
                Assert.AreEqual(
                    "20260819T100000Z/docs/report.pdf",
                    reference.Entry.RelativePath);
                Assert.AreEqual(
                    "20260819T100000Z/docs/report.pdf.yabt-ref.json",
                    reference.Entry.StoredRelativePath);
                Assert.AreEqual(ArchiveHistoryEntryRepresentation.Reference, reference.Entry.Representation);
                Assert.AreEqual(content.LongLength, reference.Entry.ContentLength);
                Assert.IsTrue(ArchiveHash.IsValid(reference.Entry.ContentHash));
            }

            var manifestPath = Path.Combine(
                targetRoot,
                ".yabt-hist",
                ArchiveHistoryFileNames.Manifest);
            Assert.IsTrue(File.Exists(manifestPath));
            var manifestSerializer = services.GetRequiredService<IHistoryManifestSerializer>();
            await using var manifestStream = File.OpenRead(manifestPath);
            var manifest = await manifestSerializer.ReadAsync(manifestStream);
            var entries = manifest.Entries.ToArray();
            Assert.AreEqual(2, entries.Length);
            Assert.AreEqual(1, entries.Count(entry => string.Equals(
                entry.Representation,
                ArchiveHistoryEntryRepresentation.Materialized,
                StringComparison.Ordinal)));
            Assert.AreEqual(1, entries.Count(entry => string.Equals(
                entry.Representation,
                ArchiveHistoryEntryRepresentation.Reference,
                StringComparison.Ordinal)));
            Assert.IsFalse(File.Exists(Path.Combine(
                targetRoot,
                ".yabt-hist",
                ArchiveHistoryManifest.InvalidationMarkerFileName)));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task DeduplicateAsyncLeavesTinyDuplicateMaterialized()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var archiveRoot = Path.Combine(workspace, "archive");
            var targetRoot = Path.Combine(workspace, "target");
            await InitializeArchiveRootAsync(archiveRoot, targetRoot);
            var content = CreateContent(ArchiveHistoryDeduplication.DefaultTinyFileMaximumBytes);
            var firstPath = Path.Combine(targetRoot, ".yabt-hist", "one", "tiny.bin");
            var secondPath = Path.Combine(targetRoot, ".yabt-hist", "two", "tiny.bin");
            await WriteBytesAsync(firstPath, content);
            await WriteBytesAsync(secondPath, content);

            using var services = CreateServices().BuildServiceProvider();
            var result = await services.GetRequiredService<IHistoryDeduplicator>().DeduplicateAsync(
                new HistoryDeduplicationRequest(archiveRoot));

            Assert.IsTrue(result.Completed);
            Assert.AreEqual(0, result.ReplacedObjectCount);
            Assert.AreEqual(2, result.TinyObjectCount);
            Assert.IsTrue(File.Exists(firstPath));
            Assert.IsTrue(File.Exists(secondPath));
            Assert.AreEqual(0, Directory.GetFiles(
                Path.Combine(targetRoot, ".yabt-hist"),
                $"*{ArchiveHistoryFileNames.ReferenceSuffix}",
                SearchOption.AllDirectories).Length);
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task DeduplicateAsyncDryRunDoesNotWriteTarget()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var archiveRoot = Path.Combine(workspace, "archive");
            var targetRoot = Path.Combine(workspace, "target");
            await InitializeArchiveRootAsync(archiveRoot, targetRoot);
            var content = CreateContent(6_000);
            var firstPath = Path.Combine(targetRoot, ".yabt-hist", "one", "file.bin");
            var secondPath = Path.Combine(targetRoot, ".yabt-hist", "two", "file.bin");
            await WriteBytesAsync(firstPath, content);
            await WriteBytesAsync(secondPath, content);

            using var services = CreateServices().BuildServiceProvider();
            var result = await services.GetRequiredService<IHistoryDeduplicator>().DeduplicateAsync(
                new HistoryDeduplicationRequest(archiveRoot, DryRun: true));

            Assert.IsTrue(result.Completed);
            Assert.AreEqual(1, result.ReplacedObjectCount);
            Assert.IsTrue(File.Exists(firstPath));
            Assert.IsTrue(File.Exists(secondPath));
            Assert.IsFalse(File.Exists(Path.Combine(
                targetRoot,
                ".yabt-hist",
                ArchiveHistoryFileNames.Manifest)));
            Assert.IsFalse(Directory.Exists(Path.Combine(targetRoot, ".yabt-tmp")));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task DeduplicateAsyncRebuildsLostManifestFromActualAndReference()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var archiveRoot = Path.Combine(workspace, "archive");
            var targetRoot = Path.Combine(workspace, "target");
            await InitializeArchiveRootAsync(archiveRoot, targetRoot);
            var content = CreateContent(6_000);
            await WriteBytesAsync(
                Path.Combine(targetRoot, ".yabt-hist", "one", "file.bin"),
                content);
            await WriteBytesAsync(
                Path.Combine(targetRoot, ".yabt-hist", "two", "file.bin"),
                content);

            using var services = CreateServices().BuildServiceProvider();
            var deduplicator = services.GetRequiredService<IHistoryDeduplicator>();
            await deduplicator.DeduplicateAsync(new HistoryDeduplicationRequest(archiveRoot));

            var manifestPath = Path.Combine(
                targetRoot,
                ".yabt-hist",
                ArchiveHistoryFileNames.Manifest);
            File.Delete(manifestPath);

            var rebuilt = await deduplicator.DeduplicateAsync(
                new HistoryDeduplicationRequest(archiveRoot));

            Assert.IsTrue(rebuilt.Completed);
            Assert.AreEqual(1, rebuilt.ExistingReferenceCount);
            Assert.AreEqual(0, rebuilt.ReplacedObjectCount);
            Assert.IsTrue(File.Exists(manifestPath));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task DeduplicateAsyncRecoversWhenOriginalAndReferenceBothExist()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var archiveRoot = Path.Combine(workspace, "archive");
            var targetRoot = Path.Combine(workspace, "target");
            await InitializeArchiveRootAsync(archiveRoot, targetRoot);
            var content = CreateContent(6_000);
            var olderPath = Path.Combine(targetRoot, ".yabt-hist", "one", "file.bin");
            var newerPath = Path.Combine(targetRoot, ".yabt-hist", "two", "file.bin");
            await WriteBytesAsync(olderPath, content);
            await WriteBytesAsync(newerPath, content);

            using var services = CreateServices().BuildServiceProvider();
            var deduplicator = services.GetRequiredService<IHistoryDeduplicator>();
            await deduplicator.DeduplicateAsync(new HistoryDeduplicationRequest(archiveRoot));

            var firstReferencePath = $"{newerPath}{ArchiveHistoryFileNames.ReferenceSuffix}";
            var referenceSerializer = services.GetRequiredService<IHistoryReferenceSerializer>();
            ArchiveHistoryContentReference firstReference;
            await using (var referenceStream = File.OpenRead(firstReferencePath))
            {
                firstReference = await referenceSerializer.ReadAsync(referenceStream);
            }

            await WriteBytesAsync(newerPath, content);
            if (firstReference.Entry.LastModifiedUtc.HasValue)
            {
                File.SetLastWriteTimeUtc(
                    newerPath,
                    firstReference.Entry.LastModifiedUtc.Value.UtcDateTime);
            }

            var recovered = await deduplicator.DeduplicateAsync(
                new HistoryDeduplicationRequest(archiveRoot));

            Assert.IsTrue(recovered.Completed);
            Assert.AreEqual(1, recovered.ReplacedObjectCount);
            Assert.IsFalse(File.Exists(newerPath));
            Assert.IsFalse(File.Exists(firstReferencePath));
            Assert.IsTrue(File.Exists(
                $"{newerPath}.1{ArchiveHistoryFileNames.ReferenceSuffix}"));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task DeduplicateAsyncPreservesReferenceWhenBackingObjectIsMissing()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var archiveRoot = Path.Combine(workspace, "archive");
            var targetRoot = Path.Combine(workspace, "target");
            await InitializeArchiveRootAsync(archiveRoot, targetRoot);
            var content = CreateContent(6_000);
            var olderPath = Path.Combine(targetRoot, ".yabt-hist", "one", "file.bin");
            var newerPath = Path.Combine(targetRoot, ".yabt-hist", "two", "file.bin");
            await WriteBytesAsync(olderPath, content);
            await WriteBytesAsync(newerPath, content);

            using var services = CreateServices().BuildServiceProvider();
            var deduplicator = services.GetRequiredService<IHistoryDeduplicator>();
            await deduplicator.DeduplicateAsync(new HistoryDeduplicationRequest(archiveRoot));
            var referencePath = $"{newerPath}{ArchiveHistoryFileNames.ReferenceSuffix}";
            File.Delete(olderPath);

            await Assert.ThrowsExactlyAsync<YabtSyncException>(() =>
                deduplicator.DeduplicateAsync(new HistoryDeduplicationRequest(archiveRoot)));

            Assert.IsTrue(File.Exists(referencePath));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task DeduplicateAsyncAllocatesReferenceNameWithoutOverwritingExistingObject()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var archiveRoot = Path.Combine(workspace, "archive");
            var targetRoot = Path.Combine(workspace, "target");
            await InitializeArchiveRootAsync(archiveRoot, targetRoot);
            var content = CreateContent(6_000);
            var olderPath = Path.Combine(targetRoot, ".yabt-hist", "one", "file.bin");
            var newerPath = Path.Combine(targetRoot, ".yabt-hist", "two", "file.bin");
            var occupiedReferencePath = $"{newerPath}{ArchiveHistoryFileNames.ReferenceSuffix}";
            await WriteBytesAsync(olderPath, content);
            await WriteBytesAsync(newerPath, content);
            await WriteBytesAsync(occupiedReferencePath, Encoding.UTF8.GetBytes("ordinary history data"));

            using var services = CreateServices().BuildServiceProvider();
            var result = await services.GetRequiredService<IHistoryDeduplicator>().DeduplicateAsync(
                new HistoryDeduplicationRequest(archiveRoot));

            Assert.IsTrue(result.Completed);
            CollectionAssert.AreEqual(
                Encoding.UTF8.GetBytes("ordinary history data"),
                await File.ReadAllBytesAsync(occupiedReferencePath));
            Assert.IsTrue(File.Exists(
                $"{newerPath}.1{ArchiveHistoryFileNames.ReferenceSuffix}"));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task DeduplicateAsyncUsesConfiguredTinyFileMaximum()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var archiveRoot = Path.Combine(workspace, "archive");
            var targetRoot = Path.Combine(workspace, "target");
            await InitializeArchiveRootAsync(
                archiveRoot,
                targetRoot,
                historyDeduplicationTinyFileMaximumBytes: 8_000);
            var content = CreateContent(6_000);
            var firstPath = Path.Combine(targetRoot, ".yabt-hist", "one", "file.bin");
            var secondPath = Path.Combine(targetRoot, ".yabt-hist", "two", "file.bin");
            await WriteBytesAsync(firstPath, content);
            await WriteBytesAsync(secondPath, content);

            using var services = CreateServices().BuildServiceProvider();
            var result = await services.GetRequiredService<IHistoryDeduplicator>().DeduplicateAsync(
                new HistoryDeduplicationRequest(archiveRoot));

            Assert.AreEqual(0, result.ReplacedObjectCount);
            Assert.IsTrue(File.Exists(firstPath));
            Assert.IsTrue(File.Exists(secondPath));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task DeduplicateAsyncRebuildsCorruptManifestFromHistoryObjects()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var archiveRoot = Path.Combine(workspace, "archive");
            var targetRoot = Path.Combine(workspace, "target");
            await InitializeArchiveRootAsync(archiveRoot, targetRoot);
            await WriteBytesAsync(
                Path.Combine(targetRoot, ".yabt-hist", "one", "file.bin"),
                CreateContent(6_000));

            using var services = CreateServices().BuildServiceProvider();
            var deduplicator = services.GetRequiredService<IHistoryDeduplicator>();
            await deduplicator.DeduplicateAsync(new HistoryDeduplicationRequest(archiveRoot));
            var manifestPath = Path.Combine(
                targetRoot,
                ".yabt-hist",
                ArchiveHistoryFileNames.Manifest);
            await File.WriteAllTextAsync(manifestPath, "{broken", Encoding.UTF8);

            var rebuilt = await deduplicator.DeduplicateAsync(
                new HistoryDeduplicationRequest(archiveRoot));

            Assert.IsTrue(rebuilt.Completed);
            var manifestSerializer = services.GetRequiredService<IHistoryManifestSerializer>();
            await using var manifestStream = File.OpenRead(manifestPath);
            var manifest = await manifestSerializer.ReadAsync(manifestStream);
            Assert.AreEqual(1, manifest.Entries.Count());
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task DeduplicateAsyncPreservesConflictingOriginalAndReference()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var archiveRoot = Path.Combine(workspace, "archive");
            var targetRoot = Path.Combine(workspace, "target");
            await InitializeArchiveRootAsync(archiveRoot, targetRoot);
            var content = CreateContent(6_000);
            var olderPath = Path.Combine(targetRoot, ".yabt-hist", "one", "file.bin");
            var newerPath = Path.Combine(targetRoot, ".yabt-hist", "two", "file.bin");
            await WriteBytesAsync(olderPath, content);
            await WriteBytesAsync(newerPath, content);

            using var services = CreateServices().BuildServiceProvider();
            var deduplicator = services.GetRequiredService<IHistoryDeduplicator>();
            await deduplicator.DeduplicateAsync(new HistoryDeduplicationRequest(archiveRoot));
            var referencePath = $"{newerPath}{ArchiveHistoryFileNames.ReferenceSuffix}";
            var differentContent = content.ToArray();
            differentContent[0] ^= 0xff;
            await WriteBytesAsync(newerPath, differentContent);

            await Assert.ThrowsExactlyAsync<YabtSyncException>(() =>
                deduplicator.DeduplicateAsync(new HistoryDeduplicationRequest(archiveRoot)));

            CollectionAssert.AreEqual(differentContent, await File.ReadAllBytesAsync(newerPath));
            Assert.IsTrue(File.Exists(referencePath));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task DeduplicateAsyncKeepsObjectWhenCatalogGrowthRemovesNetSaving()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var archiveRoot = Path.Combine(workspace, "archive");
            var targetRoot = Path.Combine(workspace, "target");
            await InitializeArchiveRootAsync(
                archiveRoot,
                targetRoot,
                historyDeduplicationTinyFileMaximumBytes: 0);

            using var services = CreateServices().BuildServiceProvider();
            var referenceSerializer = services.GetRequiredService<IHistoryReferenceSerializer>();
            var relativePath = "two/file.bin";
            var storedReferencePath =
                $"{relativePath}{ArchiveHistoryFileNames.ReferenceSuffix}";
            var timestamp = new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
            long contentLength = 512;
            long referenceLength = 0;
            byte[] content = [];
            for (var iteration = 0; iteration < 5; iteration++)
            {
                content = CreateContent(contentLength);
                var referenceEntry = new ArchiveHistoryManifestEntry
                (
                    relativePath,
                    storedReferencePath,
                    ArchiveHistoryEntryRepresentation.Reference,
                    contentLength,
                    ArchiveHash.Compute(content),
                    timestamp,
                    "application/octet-stream"
                );
                var reference = referenceSerializer.Create(referenceEntry);
                using var serializedReference = new MemoryStream();
                await referenceSerializer.WriteAsync(reference, serializedReference);
                referenceLength = serializedReference.Length;
                var nextLength = referenceLength + 1;
                if (nextLength == contentLength)
                {
                    break;
                }

                contentLength = nextLength;
            }

            Assert.AreEqual(contentLength, content.LongLength);
            Assert.IsTrue(referenceLength < contentLength);
            var manifestGrowth =
                JsonSerializer.SerializeToUtf8Bytes(storedReferencePath).LongLength -
                JsonSerializer.SerializeToUtf8Bytes(relativePath).LongLength +
                JsonSerializer.SerializeToUtf8Bytes(
                    ArchiveHistoryEntryRepresentation.Reference).LongLength -
                JsonSerializer.SerializeToUtf8Bytes(
                    ArchiveHistoryEntryRepresentation.Materialized).LongLength;
            Assert.IsTrue(referenceLength + manifestGrowth >= contentLength);

            var firstPath = Path.Combine(targetRoot, ".yabt-hist", "one", "file.bin");
            var secondPath = Path.Combine(targetRoot, ".yabt-hist", "two", "file.bin");
            await WriteBytesAsync(firstPath, content);
            await WriteBytesAsync(secondPath, content);
            File.SetLastWriteTimeUtc(firstPath, timestamp.UtcDateTime);
            File.SetLastWriteTimeUtc(secondPath, timestamp.UtcDateTime);

            var result = await services.GetRequiredService<IHistoryDeduplicator>().DeduplicateAsync(
                new HistoryDeduplicationRequest(archiveRoot));

            Assert.AreEqual(0, result.ReplacedObjectCount);
            Assert.IsTrue(File.Exists(firstPath));
            Assert.IsTrue(File.Exists(secondPath));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task DeduplicateAsyncDoesNotModifyMatchingLiveObjects()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var archiveRoot = Path.Combine(workspace, "archive");
            var targetRoot = Path.Combine(workspace, "target");
            await InitializeArchiveRootAsync(archiveRoot, targetRoot);
            var content = CreateContent(6_000);
            var firstLivePath = Path.Combine(targetRoot, "one.bin");
            var secondLivePath = Path.Combine(targetRoot, "two.bin");
            await WriteBytesAsync(firstLivePath, content);
            await WriteBytesAsync(secondLivePath, content);

            using var services = CreateServices().BuildServiceProvider();
            var result = await services.GetRequiredService<IHistoryDeduplicator>().DeduplicateAsync(
                new HistoryDeduplicationRequest(archiveRoot));

            Assert.AreEqual(0, result.ScannedObjectCount);
            CollectionAssert.AreEqual(content, await File.ReadAllBytesAsync(firstLivePath));
            CollectionAssert.AreEqual(content, await File.ReadAllBytesAsync(secondLivePath));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    [DataRow("", ".yabt-tmp/history")]
    [DataRow("archive/live", "archive")]
    public async Task DeduplicateAsyncRejectsUnsafeHistoryLayout
    (
        string livePrefix,
        string histPrefix
    )
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var archiveRoot = Path.Combine(workspace, "archive");
            var targetRoot = Path.Combine(workspace, "target");
            await InitializeArchiveRootAsync(
                archiveRoot,
                targetRoot,
                livePrefix: livePrefix,
                histPrefix: histPrefix);

            using var services = CreateServices().BuildServiceProvider();
            var deduplicator = services.GetRequiredService<IHistoryDeduplicator>();

            await Assert.ThrowsExactlyAsync<YabtSyncException>(() =>
                deduplicator.DeduplicateAsync(new HistoryDeduplicationRequest(archiveRoot)));

            Assert.IsFalse(File.Exists(Path.Combine(
                targetRoot,
                ArchiveInternalObjectKeys.MutationLock.Replace('/', Path.DirectorySeparatorChar))));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task SyncAsyncRemovesStaleHistoryManifestAndMarkerWhenHistoryChanges()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var archiveRoot = Path.Combine(workspace, "archive");
            var targetRoot = Path.Combine(workspace, "target");
            await InitializeArchiveRootAsync(archiveRoot, targetRoot);
            var sourceFile = Path.Combine(archiveRoot, "file.txt");
            await File.WriteAllTextAsync(sourceFile, "first", Encoding.UTF8);

            using var services = CreateServices(includeMirrorProjector: true).BuildServiceProvider();
            var synchronizer = services.GetRequiredService<IArchiveSynchronizer>();
            await synchronizer.SyncAsync(new SyncRunRequest(archiveRoot));

            var deduplicator = services.GetRequiredService<IHistoryDeduplicator>();
            await deduplicator.DeduplicateAsync(new HistoryDeduplicationRequest(archiveRoot));
            var manifestPath = Path.Combine(
                targetRoot,
                ".yabt-hist",
                ArchiveHistoryFileNames.Manifest);
            Assert.IsTrue(File.Exists(manifestPath));

            await File.WriteAllTextAsync(sourceFile, "second", Encoding.UTF8);
            await synchronizer.SyncAsync(new SyncRunRequest(archiveRoot, ByteForByte: true));

            Assert.IsFalse(File.Exists(manifestPath));
            Assert.IsFalse(File.Exists(Path.Combine(
                targetRoot,
                ".yabt-hist",
                ArchiveHistoryManifest.InvalidationMarkerFileName)));

            await deduplicator.DeduplicateAsync(new HistoryDeduplicationRequest(archiveRoot));
            Assert.IsTrue(File.Exists(manifestPath));
            Assert.IsFalse(File.Exists(Path.Combine(
                targetRoot,
                ".yabt-hist",
                ArchiveHistoryManifest.InvalidationMarkerFileName)));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    private static ServiceCollection CreateServices(bool includeMirrorProjector = false)
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddYabtFileSystemObjectStore();
        services.AddYabtMetadata();
        services.AddYabtSync();
        if (includeMirrorProjector)
        {
            services.AddYabtMirrorFormatProjector();
        }

        return services;
    }

    private static async Task InitializeArchiveRootAsync
    (
        string archiveRoot,
        string targetRoot,
        long? historyDeduplicationTinyFileMaximumBytes = default,
        string livePrefix = "",
        string histPrefix = ".yabt-hist"
    )
    {
        Directory.CreateDirectory(archiveRoot);
        Directory.CreateDirectory(targetRoot);
        var descriptor = new
        {
            documentType = BackupRootDescriptor.ExpectedDocumentType,
            schemaVersion = BackupRootDescriptor.ExpectedSchemaVersion,
            rootRole = "source",
            archiveId = Guid.NewGuid().ToString(),
            defaultStoreId = "target",
            createdAtUtc = new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero),
            historyDeduplicationTinyFileMaximumBytes,
            layout = new
            {
                livePrefix,
                histPrefix,
            },
            stores = new[]
            {
                new
                {
                    id = "target",
                    kind = "fileSystem",
                    rootPath = targetRoot,
                },
            },
        };
        var json = JsonSerializer.Serialize(descriptor, new JsonSerializerOptions
        {
            WriteIndented = true,
        });
        await File.WriteAllTextAsync(
            Path.Combine(archiveRoot, BackupRootFileNames.Primary),
            json,
            Encoding.UTF8);
    }

    private static byte[] CreateContent(long length)
    {
        var content = new byte[checked((int)length)];
        for (var index = 0; index < content.Length; index++)
        {
            content[index] = (byte)(index % 251);
        }

        return content;
    }

    private static async Task WriteBytesAsync(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, content);
    }

    private static string CreateWorkspacePath() => Path.Combine(
        Path.GetTempPath(),
        $"yabt-history-dedup-tests-{Guid.NewGuid():N}");

    private static void DeleteWorkspace(string workspace)
    {
        if (Directory.Exists(workspace))
        {
            Directory.Delete(workspace, recursive: true);
        }
    }
}
