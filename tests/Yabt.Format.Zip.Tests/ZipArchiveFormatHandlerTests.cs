using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.Metadata;
using Yabt.Tests;

namespace Yabt.Format.Zip.Tests;

[TestClass]
public sealed class ZipArchiveFormatHandlerTests
{
    [TestMethod]
    public void ServiceRegistrationRegistersZipFormatHandler()
    {
        using var serviceProvider = CreateServices().BuildServiceProvider();

        var handlers = serviceProvider.GetServices<IArchiveFormatHandler>().ToArray();

        Assert.AreEqual(1, handlers.Length);
        var handler = handlers[0];
        Assert.AreEqual(ZipArchiveFormatName.Value, handler.FormatName);
    }

    [TestMethod]
    public async Task ProjectBackupAsyncProjectsSourceFolderToPackageAndManifest()
    {
        using var serviceProvider = CreateServices().BuildServiceProvider();
        var handler = serviceProvider.GetRequiredService<IArchiveFormatHandler>();
        var sourceStore = new MemoryObjectStore(provideContentHash: true);

        await UploadTextAsync(sourceStore, "folder/file.txt", "source content");

        var projectedObjects = await CollectProjectedObjectsAsync(handler.ProjectBackupAsync(new
        (
            sourceStore,
            Policy: new FolderPolicy(ZipArchiveFormatName.Value),
            SourceDisplayName: "Photos"
        )));

        Assert.AreEqual(2, projectedObjects.Count);
        var projectedObject = GetPackageObject(projectedObjects);
        var manifestObject = GetManifestObject(projectedObjects);
        Assert.AreEqual(
            $"{projectedObject.RelativePath}.yabt-manifest.json",
            manifestObject.RelativePath);
        Assert.AreEqual(
            projectedObject.Projection?.ProjectionId,
            manifestObject.Projection?.ProjectionId);
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
        var embeddedManifest = archive.GetEntry(".yabt-package-manifest.json");

        Assert.IsNotNull(entry);
        Assert.IsNotNull(embeddedManifest);
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        Assert.AreEqual("source content", await reader.ReadToEndAsync());

        await using var embeddedContent = embeddedManifest.Open();
        using var embeddedBytes = new MemoryStream();
        await embeddedContent.CopyToAsync(embeddedBytes);
        CollectionAssert.AreEqual(
            await ReadContentBytesAsync(manifestObject),
            embeddedBytes.ToArray());

        var manifestSerializer = serviceProvider.GetRequiredService<IManifestSerializer>();
        embeddedBytes.Position = 0;
        var manifest = await manifestSerializer.ReadAsync(embeddedBytes);
        Assert.AreEqual(ArchiveManifest.ExpectedDocumentType, manifest.DocumentType);
        Assert.AreEqual(ZipArchiveFormatVersion.Value, manifest.FormatVersion);
        Assert.AreEqual(projectedObject.RelativePath, manifest.PackageName);
        Assert.AreEqual("source content".Length, manifest.TotalBytes);
        var manifestEntry = manifest.Entries.Single();
        Assert.AreEqual(ArchiveManifestEntryKinds.File, manifestEntry.Kind);
        Assert.AreEqual("folder/file.txt", manifestEntry.RelativePath);
        Assert.AreEqual(
            ArchiveHash.Compute(Encoding.UTF8.GetBytes("source content")),
            manifestEntry.ContentHash);
    }

    [TestMethod]
    public async Task ProjectBackupAsyncStoresNativeEmptyFolderAsMarker()
    {
        using var serviceProvider = CreateServices().BuildServiceProvider();
        var handler = serviceProvider.GetRequiredService<IArchiveFormatHandler>();
        var innerStore = new MemoryObjectStore(provideContentHash: true);
        await UploadTextAsync(innerStore, "folder/file.txt", "source content");
        var sourceStore = new EmptyFolderReadOnlyObjectStore(
            innerStore,
            "folder/empty");

        var projectedObjects = await CollectProjectedObjectsAsync(handler.ProjectBackupAsync(new
        (
            sourceStore,
            Policy: new FolderPolicy(ZipArchiveFormatName.Value),
            SourceDisplayName: "Photos"
        )));
        var projectedObject = GetPackageObject(projectedObjects);

        await using var content = await projectedObject.OpenContentAsync(default);
        using var archive = new ZipArchive(content.Content, ZipArchiveMode.Read);
        var markerEntry = archive.GetEntry("folder/empty/.yabt-empty");

        Assert.IsNotNull(markerEntry);
        Assert.AreEqual(0, markerEntry.Length);
        await using var markerContent = markerEntry.Open();
        Assert.AreEqual(-1, markerContent.ReadByte());
    }

    [TestMethod]
    public async Task PackageManifestPreservesNestedProjectionProvenance()
    {
        using var serviceProvider = CreateServices().BuildServiceProvider();
        var handler = serviceProvider.GetRequiredService<IArchiveFormatHandler>();
        var manifestSerializer = serviceProvider.GetRequiredService<IManifestSerializer>();
        var innerStore = new MemoryObjectStore(provideContentHash: true);
        await UploadTextAsync(innerStore, "source/child.package", "nested package bytes");
        var nestedProjectionId = ArchiveHash.Compute(
            Encoding.UTF8.GetBytes("nested projection"));
        var nestedProjection = new ArchiveProjectionProvenance
        (
            "outer/child",
            ZipArchiveFormatName.Value,
            ZipArchiveFormatVersion.Value,
            nestedProjectionId,
            ArchiveProjectionArtifactRoles.Package
        );
        var sourceStore = new ProjectionMetadataReadOnlyObjectStore
        (
            innerStore,
            "source/child.package",
            nestedProjection
        );
        var projectedObjects = await CollectProjectedObjectsAsync(handler.ProjectBackupAsync(new
        (
            sourceStore,
            SourcePrefix: "source",
            Policy: new FolderPolicy(ZipArchiveFormatName.Value),
            SourceDisplayName: "Outer",
            LogicalPath: "outer"
        )));
        var adjacentManifest = GetManifestObject(projectedObjects);
        await using var adjacentContent = await adjacentManifest.OpenContentAsync(default);
        var manifest = await manifestSerializer.ReadAsync(adjacentContent.Content);

        var nestedEntry = manifest.Entries.Single();
        Assert.AreEqual(ArchiveManifestEntryKinds.FormatArtifact, nestedEntry.Kind);
        Assert.AreEqual("child", nestedEntry.Projection?.LogicalPath);
        Assert.AreEqual(nestedProjectionId, nestedEntry.Projection?.ProjectionId);

        var restoreArtifact = await CreateRestorableArtifactAsync(
            GetPackageObject(projectedObjects));
        await using var restoreProjection = await handler.ProjectRestoreAsync(new(restoreArtifact));
        var restoredNestedArtifact = restoreProjection.Objects.Single();
        Assert.AreEqual("Outer/child", restoredNestedArtifact.Projection?.LogicalPath);
        Assert.AreEqual(
            ArchiveProjectionArtifactRoles.Package,
            restoredNestedArtifact.Projection?.ArtifactRole);
    }

    [TestMethod]
    public async Task ProjectBackupAsyncIncludesNativeEmptyFolderInPackageIdentity()
    {
        using var serviceProvider = CreateServices().BuildServiceProvider();
        var handler = serviceProvider.GetRequiredService<IArchiveFormatHandler>();
        var innerStore = new MemoryObjectStore(provideContentHash: true);
        await UploadTextAsync(innerStore, "folder/file.txt", "source content");
        var sourceStoreWithEmptyFolder = new EmptyFolderReadOnlyObjectStore(
            innerStore,
            "folder/empty");
        var requestWithoutEmptyFolder = new ArchiveProjectionRequest
        (
            innerStore,
            Policy: new FolderPolicy(ZipArchiveFormatName.Value),
            SourceDisplayName: "Photos"
        );
        var requestWithEmptyFolder = requestWithoutEmptyFolder with
        {
            SourceStore = sourceStoreWithEmptyFolder,
        };

        var projectionWithoutEmptyFolder = GetPackageObject(await CollectProjectedObjectsAsync(
            handler.ProjectBackupAsync(requestWithoutEmptyFolder)));
        var firstProjectionWithEmptyFolder = GetPackageObject(await CollectProjectedObjectsAsync(
            handler.ProjectBackupAsync(requestWithEmptyFolder)));
        var secondProjectionWithEmptyFolder = GetPackageObject(await CollectProjectedObjectsAsync(
            handler.ProjectBackupAsync(requestWithEmptyFolder)));

        Assert.AreNotEqual(
            projectionWithoutEmptyFolder.RelativePath,
            firstProjectionWithEmptyFolder.RelativePath);
        Assert.AreNotEqual(
            projectionWithoutEmptyFolder.ChangeFingerprint,
            firstProjectionWithEmptyFolder.ChangeFingerprint);
        Assert.AreEqual(
            firstProjectionWithEmptyFolder.RelativePath,
            secondProjectionWithEmptyFolder.RelativePath);
        CollectionAssert.AreEqual(
            await ReadContentBytesAsync(firstProjectionWithEmptyFolder),
            await ReadContentBytesAsync(secondProjectionWithEmptyFolder));
    }

    [TestMethod]
    public async Task ProjectBackupAsyncUsesStableFullHashNameForUnchangedSource()
    {
        using var serviceProvider = CreateServices().BuildServiceProvider();
        var handler = serviceProvider.GetRequiredService<IArchiveFormatHandler>();
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

        var firstProjection = GetPackageObject(await CollectProjectedObjectsAsync(
            handler.ProjectBackupAsync(request)));
        var secondProjection = GetPackageObject(await CollectProjectedObjectsAsync(
            handler.ProjectBackupAsync(request)));

        Assert.AreEqual(firstProjection.RelativePath, secondProjection.RelativePath);
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
    public async Task ProjectBackupAsyncChangesFullHashNameWhenSourceContentChanges()
    {
        using var serviceProvider = CreateServices().BuildServiceProvider();
        var handler = serviceProvider.GetRequiredService<IArchiveFormatHandler>();
        var timeProvider = new ZipSourceTimeProvider(
            new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));
        var firstSourceStore = new MemoryObjectStore(timeProvider, provideContentHash: true);
        var secondSourceStore = new MemoryObjectStore(timeProvider, provideContentHash: true);
        await UploadTextAsync(firstSourceStore, "folder/file.txt", "first content");
        await UploadTextAsync(secondSourceStore, "folder/file.txt", "second content");

        var firstProjectionObjects = await CollectProjectedObjectsAsync(handler.ProjectBackupAsync(new
        (
            firstSourceStore,
            Policy: new FolderPolicy(ZipArchiveFormatName.Value),
            SourceDisplayName: "Photos"
        )));
        var firstProjection = GetPackageObject(firstProjectionObjects);
        var secondProjectionObjects = await CollectProjectedObjectsAsync(handler.ProjectBackupAsync(new
        (
            secondSourceStore,
            Policy: new FolderPolicy(ZipArchiveFormatName.Value),
            SourceDisplayName: "Photos"
        )));
        var secondProjection = GetPackageObject(secondProjectionObjects);

        Assert.AreNotEqual(firstProjection.RelativePath, secondProjection.RelativePath);
    }

    [TestMethod]
    public async Task ProjectBackupAsyncIncludesLogicalPathAndPolicySnapshotInIdentity()
    {
        using var serviceProvider = CreateServices().BuildServiceProvider();
        var handler = serviceProvider.GetRequiredService<IArchiveFormatHandler>();
        var sourceStore = new MemoryObjectStore(provideContentHash: true);
        await UploadTextAsync(sourceStore, "folder/file.txt", "same content");
        var request = new ArchiveProjectionRequest
        (
            sourceStore,
            Policy: new FolderPolicy(ZipArchiveFormatName.Value),
            SourceDisplayName: "Photos",
            LogicalPath: "first/photos"
        );

        var first = GetPackageObject(await CollectProjectedObjectsAsync(
            handler.ProjectBackupAsync(request)));
        var moved = GetPackageObject(await CollectProjectedObjectsAsync(
            handler.ProjectBackupAsync(request with
            {
                LogicalPath = "second/photos",
            })));
        var changedPolicy = GetPackageObject(await CollectProjectedObjectsAsync(
            handler.ProjectBackupAsync(request with
            {
                Policy = new FolderPolicy
                (
                    ZipArchiveFormatName.Value,
                    IncludePatterns: ["*.txt"]
                ),
            })));

        Assert.AreNotEqual(first.RelativePath, moved.RelativePath);
        Assert.AreNotEqual(first.RelativePath, changedPolicy.RelativePath);
    }

    [TestMethod]
    public async Task ProjectBackupAsyncUsesMetadataFingerprintWithoutOpeningSourceContent()
    {
        using var serviceProvider = CreateServices().BuildServiceProvider();
        var handler = serviceProvider.GetRequiredService<IArchiveFormatHandler>();
        var innerStore = new MemoryObjectStore(provideContentHash: false);
        await UploadTextAsync(innerStore, "folder/file.txt", "source content");
        var sourceStore = new CountingReadOnlyObjectStore(innerStore);

        var projectedObjects = await CollectProjectedObjectsAsync(handler.ProjectBackupAsync(new
        (
            sourceStore,
            Policy: new FolderPolicy(ZipArchiveFormatName.Value),
            SourceDisplayName: "Photos"
        )));
        var projectedObject = GetPackageObject(projectedObjects);

        Assert.AreEqual(0, sourceStore.OpenReadCount);
        StringAssert.StartsWith(
            projectedObject.ChangeFingerprint,
            "xxh128:");

        _ = await ReadContentBytesAsync(projectedObject);

        Assert.AreEqual(1, sourceStore.OpenReadCount);
    }

    [TestMethod]
    public async Task ProjectBackupAsyncReadsSourceToFingerprintWhenMetadataAndContentHashAreIncomplete()
    {
        using var serviceProvider = CreateServices().BuildServiceProvider();
        var handler = serviceProvider.GetRequiredService<IArchiveFormatHandler>();
        var innerStore = new MemoryObjectStore(provideContentHash: false);
        await UploadTextAsync(innerStore, "folder/file.txt", "source content");
        var countingStore = new CountingReadOnlyObjectStore(innerStore);
        var sourceStore = new MissingLastModifiedObjectStore(countingStore);

        var projectedObjects = await CollectProjectedObjectsAsync(handler.ProjectBackupAsync(new
        (
            sourceStore,
            Policy: new FolderPolicy(ZipArchiveFormatName.Value),
            SourceDisplayName: "Photos"
        )));
        var projectedObject = GetPackageObject(projectedObjects);

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
    public async Task ProjectBackupAsyncReadsEverySourceOnceWhenOneFingerprintNeedsContent()
    {
        using var serviceProvider = CreateServices().BuildServiceProvider();
        var handler = serviceProvider.GetRequiredService<IArchiveFormatHandler>();
        var innerStore = new MemoryObjectStore(provideContentHash: false);
        await UploadTextAsync(innerStore, "folder/incomplete.txt", "incomplete metadata");
        await UploadTextAsync(innerStore, "folder/complete.txt", "complete metadata");
        var countingStore = new CountingReadOnlyObjectStore(innerStore);
        var sourceStore = new MissingLastModifiedObjectStore(
            countingStore,
            "folder/incomplete.txt");

        var projectedObjects = await CollectProjectedObjectsAsync(handler.ProjectBackupAsync(new
        (
            sourceStore,
            Policy: new FolderPolicy(ZipArchiveFormatName.Value),
            SourceDisplayName: "Photos"
        )));
        var projectedObject = GetPackageObject(projectedObjects);

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
    public async Task ProjectBackupAsyncUsesDeterministicFallbackWhenSourceTimestampIsMissing()
    {
        var firstTimeProvider = new ZipSourceTimeProvider(
            new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));
        var secondTimeProvider = new ZipSourceTimeProvider(
            new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero));
        using var firstServiceProvider = CreateServices(firstTimeProvider).BuildServiceProvider();
        using var secondServiceProvider = CreateServices(secondTimeProvider).BuildServiceProvider();
        var firstHandler = firstServiceProvider.GetRequiredService<IArchiveFormatHandler>();
        var secondHandler = secondServiceProvider.GetRequiredService<IArchiveFormatHandler>();
        var innerStore = new MemoryObjectStore(provideContentHash: true);
        await UploadTextAsync(innerStore, "folder/file.txt", "source content");
        var sourceStore = new MissingLastModifiedObjectStore(innerStore);
        var request = new ArchiveProjectionRequest
        (
            sourceStore,
            Policy: new FolderPolicy(ZipArchiveFormatName.Value),
            SourceDisplayName: "Photos"
        );

        var firstProjection = GetPackageObject(await CollectProjectedObjectsAsync(
            firstHandler.ProjectBackupAsync(request)));
        var secondProjection = GetPackageObject(await CollectProjectedObjectsAsync(
            secondHandler.ProjectBackupAsync(request)));

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
    public async Task ProjectBackupAsyncIncludesTimestampWhenLengthIsMissing()
    {
        using var serviceProvider = CreateServices().BuildServiceProvider();
        var handler = serviceProvider.GetRequiredService<IArchiveFormatHandler>();
        var firstStore = new MemoryObjectStore(
            new ZipSourceTimeProvider(new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero)),
            provideContentHash: true);
        var secondStore = new MemoryObjectStore(
            new ZipSourceTimeProvider(new DateTimeOffset(2026, 8, 16, 13, 0, 0, TimeSpan.Zero)),
            provideContentHash: true);
        await UploadTextAsync(firstStore, "folder/file.txt", "same content");
        await UploadTextAsync(secondStore, "folder/file.txt", "same content");

        var firstProjectionObjects = await CollectProjectedObjectsAsync(handler.ProjectBackupAsync(new
        (
            new MissingContentLengthObjectStore(firstStore),
            Policy: new FolderPolicy(ZipArchiveFormatName.Value),
            SourceDisplayName: "Photos"
        )));
        var firstProjection = GetPackageObject(firstProjectionObjects);
        var secondProjectionObjects = await CollectProjectedObjectsAsync(handler.ProjectBackupAsync(new
        (
            new MissingContentLengthObjectStore(secondStore),
            Policy: new FolderPolicy(ZipArchiveFormatName.Value),
            SourceDisplayName: "Photos"
        )));
        var secondProjection = GetPackageObject(secondProjectionObjects);

        Assert.AreNotEqual(firstProjection.RelativePath, secondProjection.RelativePath);
    }

    [TestMethod]
    public async Task ProjectBackupAsyncRejectsManifestLargerThanRestoreSafetyLimit()
    {
        var oversizedManifestSerializer = new OversizedManifestSerializer();
        var services = CreateServices();
        services.AddSingleton<IManifestSerializer>(oversizedManifestSerializer);
        using var serviceProvider = services.BuildServiceProvider();
        var handler = serviceProvider.GetRequiredService<IArchiveFormatHandler>();
        var sourceStore = new MemoryObjectStore(provideContentHash: true);
        await UploadTextAsync(sourceStore, "folder/file.txt", "source content");

        var projectedObjects = await CollectProjectedObjectsAsync(handler.ProjectBackupAsync(new
        (
            sourceStore,
            Policy: new FolderPolicy(ZipArchiveFormatName.Value),
            SourceDisplayName: "Photos"
        )));
        var packageObject = GetPackageObject(projectedObjects);
        var manifestObject = GetManifestObject(projectedObjects);

        var packageException = await Assert.ThrowsExactlyAsync<YabtFormatZipException>(async () =>
        {
            await using var content = await packageObject.OpenContentAsync(default);
        });
        var manifestException = await Assert.ThrowsExactlyAsync<YabtFormatZipException>(async () =>
        {
            await using var content = await manifestObject.OpenContentAsync(default);
        });

        StringAssert.Contains(packageException.Message, "exceeds");
        StringAssert.Contains(packageException.Message, "restore safety limit");
        Assert.AreEqual(packageException.Message, manifestException.Message);
        Assert.AreEqual(1, oversizedManifestSerializer.WriteCount);
        Assert.IsFalse(oversizedManifestSerializer.SerializationCompleted);
        Assert.IsTrue(
            oversizedManifestSerializer.AcceptedByteCount <= 16 * 1024 * 1024,
            $"The serializer wrote {oversizedManifestSerializer.AcceptedByteCount} bytes " +
                "before the package manifest size limit stopped it.");
    }

    [TestMethod]
    public async Task ProjectBackupAndRestorePreserveTimestampOutsideZipDosRange()
    {
        var testCases = new[]
        {
            (
                SourceTimestamp: new DateTimeOffset
                (
                    1979,
                    12,
                    31,
                    23,
                    59,
                    59,
                    123,
                    TimeSpan.Zero
                ),
                ZipTimestamp: new DateTime(1980, 1, 1, 0, 0, 0)
            ),
            (
                SourceTimestamp: new DateTimeOffset
                (
                    2200,
                    1,
                    2,
                    3,
                    4,
                    5,
                    678,
                    TimeSpan.Zero
                ),
                ZipTimestamp: new DateTime(2107, 12, 31, 23, 59, 58)
            ),
        };

        using var serviceProvider = CreateServices().BuildServiceProvider();
        var handler = serviceProvider.GetRequiredService<IArchiveFormatHandler>();
        foreach (var testCase in testCases)
        {
            var sourceStore = new MemoryObjectStore
            (
                new ZipSourceTimeProvider(testCase.SourceTimestamp),
                provideContentHash: true
            );
            await UploadTextAsync(sourceStore, "folder/file.txt", "source content");
            var backupArtifacts = await CollectProjectedObjectsAsync(
                handler.ProjectBackupAsync(new
                (
                    sourceStore,
                    Policy: new FolderPolicy(ZipArchiveFormatName.Value),
                    SourceDisplayName: "Photos"
                )));

            var packageBytes = await ReadContentBytesAsync(
                GetPackageObject(backupArtifacts));
            using (var package = new MemoryStream(packageBytes, writable: false))
            using (var archive = new ZipArchive(package, ZipArchiveMode.Read))
            {
                Assert.AreEqual(
                    testCase.ZipTimestamp,
                    archive.GetEntry("folder/file.txt")?.LastWriteTime.DateTime);
                Assert.AreEqual(
                    testCase.ZipTimestamp,
                    archive.GetEntry(ArchivePackageManifestFileNames.EmbeddedEntryName)?
                        .LastWriteTime.DateTime);
            }

            var restoreArtifacts = new List<ArchiveProjectedObject>();
            foreach (var backupArtifact in backupArtifacts)
            {
                restoreArtifacts.Add(await CreateRestorableArtifactAsync(backupArtifact));
            }

            await using var restoreProjection = await handler.ProjectRestoreAsync(new
            (
                restoreArtifacts,
                requireCompleteProjection: true
            ));
            var restoredObject = restoreProjection.Objects.Single();
            Assert.AreEqual(testCase.SourceTimestamp, restoredObject.LastModifiedUtc);
            CollectionAssert.AreEqual(
                Encoding.UTF8.GetBytes("source content"),
                await ReadContentBytesAsync(restoredObject));
        }
    }

    [TestMethod]
    public async Task ProjectRestoreAsyncEagerlyValidatesDirectoryOnlyGroupedPackage()
    {
        using var serviceProvider = CreateServices().BuildServiceProvider();
        var handler = serviceProvider.GetRequiredService<IArchiveFormatHandler>();
        var sourceStore = new EmptyFolderReadOnlyObjectStore
        (
            new MemoryObjectStore(provideContentHash: true),
            "empty"
        );
        var backupArtifacts = await CollectProjectedObjectsAsync(
            handler.ProjectBackupAsync(new
            (
                sourceStore,
                Policy: new FolderPolicy(ZipArchiveFormatName.Value),
                SourceDisplayName: "Photos"
            )));
        var packageArtifact = await CreateRestorableArtifactAsync(
            GetPackageObject(backupArtifacts));
        var manifestArtifact = await CreateRestorableArtifactAsync(
            GetManifestObject(backupArtifacts));
        var corruptedPackage = ReplaceEmbeddedManifest(
            await ReadContentBytesAsync(packageArtifact),
            Encoding.UTF8.GetBytes("{}"));
        packageArtifact = packageArtifact with
        {
            OpenContentAsync = cancellationToken =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(new ArchiveObjectContent
                (
                    new MemoryStream(corruptedPackage, writable: false)
                ));
            },
            ContentLength = corruptedPackage.LongLength,
            ContentHash = ArchiveHash.Compute(corruptedPackage),
        };

        var exception = await Assert.ThrowsExactlyAsync<YabtFormatZipException>(async () =>
        {
            await using var projection = await handler.ProjectRestoreAsync(new
            (
                new[] { packageArtifact, manifestArtifact },
                requireCompleteProjection: true
            ));
        });

        StringAssert.Contains(exception.Message, "embedded and adjacent manifests differ");
    }

    [TestMethod]
    public async Task ProjectRestoreAsyncRoundTripsNestedAndRootPackageWithStableLifetime()
    {
        var expectedLastModifiedUtc = new DateTimeOffset
        (
            2026,
            8,
            24,
            12,
            30,
            7,
            456,
            TimeSpan.Zero
        );
        using var serviceProvider = CreateServices().BuildServiceProvider();
        var handler = serviceProvider.GetRequiredService<IArchiveFormatHandler>();
        var innerStore = new MemoryObjectStore
        (
            new ZipSourceTimeProvider(expectedLastModifiedUtc),
            provideContentHash: true
        );
        await UploadTextAsync(innerStore, "folder/file.txt", "source content");
        var sourceStore = new EmptyFolderReadOnlyObjectStore(
            innerStore,
            "folder/empty");
        var backupArtifacts = await CollectProjectedObjectsAsync(handler.ProjectBackupAsync(new
        (
            sourceStore,
            Policy: new FolderPolicy(ZipArchiveFormatName.Value),
            SourceDisplayName: "Photos"
        )));
        var backupArtifact = GetPackageObject(backupArtifacts);
        var nestedArtifact = await CreateRestorableArtifactAsync
        (
            backupArtifact with
            {
                RelativePath = $"albums/{backupArtifact.RelativePath}",
            }
        );

        Assert.IsTrue(handler.CanRestoreArtifact(nestedArtifact));
        await using (var nestedProjection = await handler.ProjectRestoreAsync(new
        (
            nestedArtifact
        )))
        {
            var restoredObject = nestedProjection.Objects.Single();
            Assert.AreEqual(
                "albums/Photos/folder/file.txt",
                restoredObject.RelativePath);
            Assert.AreEqual(
                expectedLastModifiedUtc,
                restoredObject.LastModifiedUtc);
            CollectionAssert.Contains(
                nestedProjection.Directories.ToArray(),
                "albums/Photos/folder/empty");

            CollectionAssert.AreEqual(
                Encoding.UTF8.GetBytes("source content"),
                await ReadContentBytesAsync(restoredObject));
            CollectionAssert.AreEqual(
                Encoding.UTF8.GetBytes("source content"),
                await ReadContentBytesAsync(restoredObject));
        }

        await using var rootProjection = await handler.ProjectRestoreAsync(new
        (
            nestedArtifact,
            restoreAsRoot: true
        ));
        Assert.AreEqual(
            "folder/file.txt",
            rootProjection.Objects.Single().RelativePath);
        CollectionAssert.Contains(
            rootProjection.Directories.ToArray(),
            "folder/empty");
    }

    [TestMethod]
    public async Task ProjectRestoreAsyncRejectsUnsafeZipEntryPath()
    {
        using var serviceProvider = CreateServices().BuildServiceProvider();
        var handler = serviceProvider.GetRequiredService<IArchiveFormatHandler>();
        var artifact = CreateZipRestoreArtifact("../escape.txt", "unsafe content");

        var exception = await Assert.ThrowsExactlyAsync<YabtFormatZipException>(
            () => handler.ProjectRestoreAsync(new(artifact)));

        StringAssert.Contains(exception.Message, "unsafe");
    }

    [TestMethod]
    public void CanRestoreArtifactRejectsMismatchedFileNameAndFingerprint()
    {
        using var serviceProvider = CreateServices().BuildServiceProvider();
        var handler = serviceProvider.GetRequiredService<IArchiveFormatHandler>();
        var artifact = CreateZipRestoreArtifact("file.txt", "content");
        var mismatchedArtifact = artifact with
        {
            ChangeFingerprint = ArchiveHash.Compute(
                Encoding.UTF8.GetBytes("different logical representation")),
        };

        Assert.IsFalse(handler.CanRestoreArtifact(mismatchedArtifact));
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

        services.AddYabtZipFormatHandler();
        services.AddYabtMetadata();

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

    private static ArchiveProjectedObject GetPackageObject
    (
        IEnumerable<ArchiveProjectedObject> projectedObjects
    ) => projectedObjects.Single(projectedObject =>
        string.Equals(
            projectedObject.Projection?.ArtifactRole,
            ArchiveProjectionArtifactRoles.Package,
            StringComparison.Ordinal));

    private static ArchiveProjectedObject GetManifestObject
    (
        IEnumerable<ArchiveProjectedObject> projectedObjects
    ) => projectedObjects.Single(projectedObject =>
        string.Equals(
            projectedObject.Projection?.ArtifactRole,
            ArchiveProjectionArtifactRoles.Manifest,
            StringComparison.Ordinal));

    private static async Task<ArchiveProjectedObject> CreateRestorableArtifactAsync
    (
        ArchiveProjectedObject backupArtifact
    )
    {
        var content = await ReadContentBytesAsync(backupArtifact);
        return backupArtifact with
        {
            OpenContentAsync = cancellationToken =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(new ArchiveObjectContent
                (
                    new MemoryStream(content, writable: false)
                ));
            },
            ContentLength = content.LongLength,
            ContentHash = ArchiveHash.Compute(content),
        };
    }

    private static ArchiveProjectedObject CreateZipRestoreArtifact
    (
        string entryPath,
        string entryText
    )
    {
        byte[] content;
        using (var package = new MemoryStream())
        {
            using (var archive = new ZipArchive
            (
                package,
                ZipArchiveMode.Create,
                leaveOpen: true
            ))
            {
                var entry = archive.CreateEntry(entryPath);
                using var writer = new StreamWriter
                (
                    entry.Open(),
                    Encoding.UTF8,
                    leaveOpen: false
                );
                writer.Write(entryText);
            }

            content = package.ToArray();
        }

        var contentHash = ArchiveHash.Compute(content);
        var packageName =
            $"Unsafe.{ArchiveHash.FormatFileNameToken(contentHash)}.zip";
        return new
        (
            packageName,
            cancellationToken =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(new ArchiveObjectContent
                (
                    new MemoryStream(content, writable: false)
                ));
            },
            content.LongLength,
            ContentHash: contentHash,
            ChangeFingerprint: contentHash
        );
    }

    private static byte[] ReplaceEmbeddedManifest
    (
        byte[] packageBytes,
        byte[] replacementManifestBytes
    )
    {
        using var package = new MemoryStream();
        package.Write(packageBytes);
        package.Position = 0;
        using (var archive = new ZipArchive(package, ZipArchiveMode.Update, leaveOpen: true))
        {
            var embeddedManifest = archive.GetEntry(
                ArchivePackageManifestFileNames.EmbeddedEntryName);
            Assert.IsNotNull(embeddedManifest);
            embeddedManifest.Delete();
            var replacement = archive.CreateEntry(
                ArchivePackageManifestFileNames.EmbeddedEntryName);
            using var replacementContent = replacement.Open();
            replacementContent.Write(replacementManifestBytes);
        }

        return package.ToArray();
    }

    private sealed class EmptyFolderReadOnlyObjectStore
    (
        IReadOnlyObjectStore _inner,
        string _emptyFolderPath
    ) : IReadOnlyObjectStore
    {
        private readonly string _normalizedEmptyFolderPath =
            ArchiveLayout.NormalizeObjectKey(_emptyFolderPath);

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
            var emptyFolderWasListed = false;
            var folderItems = _inner.GetFolderItemsAsync(
                folderPrefix,
                recursive,
                cancellationToken);
            await foreach (var folderItem in folderItems)
            {
                if (folderItem.IsFolder &&
                    string.Equals(
                        ArchiveLayout.NormalizeObjectKey(folderItem.Key),
                        _normalizedEmptyFolderPath,
                        StringComparison.Ordinal))
                {
                    emptyFolderWasListed = true;
                }

                yield return folderItem;
            }

            if (recursive || emptyFolderWasListed) { yield break; }

            var separator = _normalizedEmptyFolderPath.LastIndexOf('/');
            var emptyFolderParent = separator < 0 ?
                string.Empty :
                _normalizedEmptyFolderPath[..separator];
            if (!string.Equals(
                    ArchiveLayout.NormalizeObjectKey(folderPrefix),
                    emptyFolderParent,
                    StringComparison.Ordinal))
            {
                yield break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var emptyFolderName = separator < 0 ?
                _normalizedEmptyFolderPath :
                _normalizedEmptyFolderPath[(separator + 1)..];
            yield return ArchiveFolderItem.CreateFolder(
                emptyFolderName,
                _normalizedEmptyFolderPath);
        }
    }

    private sealed class ProjectionMetadataReadOnlyObjectStore
    (
        IReadOnlyObjectStore _inner,
        string _objectKey,
        ArchiveProjectionProvenance _projection
    ) : IReadOnlyObjectStore
    {
        private readonly string _normalizedObjectKey = ArchiveLayout.NormalizeObjectKey(_objectKey);

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
            var items = _inner.GetFolderItemsAsync(
                folderPrefix,
                recursive,
                cancellationToken);
            await foreach (var item in items)
            {
                if (item.Object is not null &&
                    string.Equals(
                        ArchiveLayout.NormalizeObjectKey(item.Object.Key),
                        _normalizedObjectKey,
                        StringComparison.Ordinal))
                {
                    yield return item with
                    {
                        Object = item.Object with
                        {
                            Projection = _projection,
                        },
                    };
                    continue;
                }

                yield return item;
            }
        }
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

    private sealed class OversizedManifestSerializer : IManifestSerializer
    {
        private const int OversizedManifestLength = (16 * 1024 * 1024) + 1;

        public int WriteCount { get; private set; }

        public long AcceptedByteCount { get; private set; }

        public bool SerializationCompleted { get; private set; }

        public ArchiveManifest Create
        (
            string sourcePath,
            DateTimeOffset createdAtUtc,
            string format,
            int formatVersion,
            string projectionId,
            string packageName,
            FolderPolicy policy,
            IEnumerable<ArchiveManifestEntry> entries
        )
        {
            var entrySnapshot = entries.ToArray();
            return new
            (
                ArchiveManifest.ExpectedDocumentType,
                ArchiveManifest.ExpectedSchemaVersion,
                sourcePath,
                createdAtUtc,
                format,
                formatVersion,
                projectionId,
                packageName,
                policy,
                entrySnapshot,
                entrySnapshot.Sum(entry => entry.Length),
                ArchiveHash.Compute([])
            );
        }

        public async Task WriteAsync
        (
            ArchiveManifest manifest,
            Stream destination,
            CancellationToken cancellationToken = default
        )
        {
            WriteCount++;
            var buffer = new byte[64 * 1024];
            var remaining = OversizedManifestLength;
            while (remaining > 0)
            {
                var writeLength = Math.Min(buffer.Length, remaining);
                await destination.WriteAsync(
                    buffer.AsMemory(0, writeLength),
                    cancellationToken);
                AcceptedByteCount += writeLength;
                remaining -= writeLength;
            }

            SerializationCompleted = true;
        }

        public Task<ArchiveManifest> ReadAsync
        (
            Stream source,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();
    }
}
