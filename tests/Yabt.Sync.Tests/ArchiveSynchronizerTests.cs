using System.Collections.Concurrent;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yabt.Common;
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
    public async Task BackupAsyncCopiesNewProjectedObjectToTarget()
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

            var result = await synchronizer.BackupAsync(new SyncRunRequest(sourceRoot));

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
    public async Task BackupAsyncLogsUserDataAtInformationAndControlMetadataAtDebug()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var targetRoot = Path.Combine(workspace, "target");
            var destinationRoot = Path.Combine(workspace, "restored");
            await InitializeSourceRootAsync(sourceRoot, targetRoot);
            await WriteTextFileAsync(
                Path.Combine(sourceRoot, "folder", "file.txt"),
                "source content");
            await WriteTextFileAsync(
                Path.Combine(sourceRoot, "removed.txt"),
                "kept in the archive during the dry run");
            await WriteTextFileAsync(
                Path.Combine(sourceRoot, ".yabt-notes.txt"),
                "ordinary user data despite its name");
            var packagedRoot = Path.Combine(sourceRoot, "packaged");
            await WritePolicyAsync(packagedRoot, ZipArchiveFormatName.Value);
            await WriteTextFileAsync(
                Path.Combine(packagedRoot, "inside.txt"),
                "packaged user data");
            Directory.CreateDirectory(Path.Combine(sourceRoot, "empty"));

            var logSink = new CapturingLogSink();
            using var serviceProvider = CreateServices(loggerSink: logSink)
                .BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();

            await synchronizer.BackupAsync(new SyncRunRequest(sourceRoot));

            var firstRunEntries = logSink.Entries.ToArray();
            Assert.IsTrue(firstRunEntries.Any(entry =>
                entry.Level == LogLevel.Information &&
                entry.EventId.Id == YabtEventIds.BackupObjectAdded &&
                entry.Message.Contains("folder/file.txt", StringComparison.Ordinal)));
            Assert.IsTrue(firstRunEntries.Any(entry =>
                entry.Level == LogLevel.Information &&
                entry.EventId.Id == YabtEventIds.BackupObjectAdded &&
                entry.Message.Contains(".yabt-notes.txt", StringComparison.Ordinal)));
            Assert.IsTrue(firstRunEntries.Any(entry =>
                entry.Level == LogLevel.Debug &&
                entry.EventId.Id == YabtEventIds.ControlMetadataOperation &&
                entry.Message.Contains(BackupRootFileNames.Primary, StringComparison.Ordinal)));
            Assert.IsTrue(firstRunEntries.Any(entry =>
                entry.Level == LogLevel.Information &&
                entry.EventId.Id == YabtEventIds.BackupEmptyDirectoryCreated &&
                entry.Message.Contains("empty", StringComparison.Ordinal)));
            Assert.IsTrue(firstRunEntries.Any(entry =>
                entry.Level == LogLevel.Debug &&
                entry.EventId.Id == YabtEventIds.ControlMetadataOperation &&
                entry.Message.Contains(
                    ArchiveFolderMarkerFileNames.EmptyFolder,
                    StringComparison.Ordinal)));
            Assert.IsTrue(firstRunEntries.Any(entry =>
                entry.Level == LogLevel.Debug &&
                entry.EventId.Id == YabtEventIds.ControlMetadataOperation &&
                entry.Message.Contains(
                    ArchivePackageManifestFileNames.AdjacentSuffix,
                    StringComparison.Ordinal)));
            Assert.IsFalse(firstRunEntries.Any(entry =>
                entry.Level == LogLevel.Information &&
                (entry.Message.Contains(
                        BackupRootFileNames.Primary,
                        StringComparison.OrdinalIgnoreCase) ||
                    entry.Message.Contains(
                        ArchiveFolderMarkerFileNames.EmptyFolder,
                        StringComparison.OrdinalIgnoreCase) ||
                    entry.Message.Contains(
                        ArchivePackageManifestFileNames.AdjacentSuffix,
                        StringComparison.OrdinalIgnoreCase))));

            logSink.Clear();
            await synchronizer.RestoreAsync(new SyncRunRequest
            (
                sourceRoot,
                DestinationRoot: destinationRoot
            ));

            Assert.IsTrue(logSink.Entries.Any(entry =>
                entry.Level == LogLevel.Debug &&
                entry.EventId.Id == YabtEventIds.ZipTemporaryPlumbingOperation &&
                entry.Message.Contains("restore staging file", StringComparison.Ordinal)));
            Assert.IsTrue(logSink.Entries.Any(entry =>
                entry.Level == LogLevel.Debug &&
                entry.EventId.Id == YabtEventIds.ControlMetadataOperation &&
                entry.Message.Contains("restore staging", StringComparison.Ordinal)));

            logSink.Clear();
            await synchronizer.BackupAsync(new SyncRunRequest(sourceRoot));

            Assert.IsTrue(logSink.Entries.Any(entry =>
                entry.Level == LogLevel.Debug &&
                entry.EventId.Id == YabtEventIds.ArchiveObjectUnchanged &&
                entry.Message.Contains("folder/file.txt", StringComparison.Ordinal)));
            Assert.IsTrue(logSink.Entries.Any(entry =>
                entry.Level == LogLevel.Debug &&
                entry.EventId.Id == YabtEventIds.EmptyDirectoryUnchanged &&
                entry.Message.Contains("empty", StringComparison.Ordinal)));
            Assert.IsTrue(logSink.Entries.Any(entry =>
                entry.Level == LogLevel.Debug &&
                entry.EventId.Id == YabtEventIds.ControlMetadataOperation &&
                entry.Message.Contains("Determined unchanged", StringComparison.Ordinal) &&
                entry.Message.Contains(BackupRootFileNames.Primary, StringComparison.Ordinal)));
            Assert.IsTrue(logSink.Entries.Any(entry =>
                entry.Level == LogLevel.Debug &&
                entry.EventId.Id == YabtEventIds.ControlMetadataOperation &&
                entry.Message.Contains("Determined unchanged", StringComparison.Ordinal) &&
                entry.Message.Contains(
                    ArchiveChangeManifest.BrotliFileName,
                    StringComparison.Ordinal)));

            await WriteTextFileAsync(
                Path.Combine(sourceRoot, "folder", "file.txt"),
                "changed source content");
            await WriteTextFileAsync(
                Path.Combine(sourceRoot, "new.txt"),
                "new content");
            File.Delete(Path.Combine(sourceRoot, "removed.txt"));
            Directory.Delete(packagedRoot, recursive: true);
            logSink.Clear();

            await synchronizer.BackupAsync(new SyncRunRequest(sourceRoot, DryRun: true));

            Assert.IsTrue(logSink.Entries.Any(entry =>
                entry.Level == LogLevel.Information &&
                entry.EventId.Id == YabtEventIds.BackupWouldAddObject &&
                entry.Message.StartsWith("Would add", StringComparison.Ordinal)));
            Assert.IsTrue(logSink.Entries.Any(entry =>
                entry.Level == LogLevel.Information &&
                entry.EventId.Id == YabtEventIds.BackupWouldChangeObject &&
                entry.Message.StartsWith("Would change", StringComparison.Ordinal)));
            Assert.IsTrue(logSink.Entries.Any(entry =>
                entry.Level == LogLevel.Information &&
                entry.EventId.Id == YabtEventIds.BackupWouldHistorizeObject &&
                entry.Message.StartsWith("Would move", StringComparison.Ordinal)));
            Assert.IsTrue(logSink.Entries.Any(entry =>
                entry.Level == LogLevel.Debug &&
                entry.EventId.Id == YabtEventIds.ControlMetadataOperation &&
                entry.Message.Contains("Would move", StringComparison.Ordinal) &&
                entry.Message.Contains(
                    ArchivePackageManifestFileNames.AdjacentSuffix,
                    StringComparison.Ordinal)));
            Assert.IsFalse(logSink.Entries.Any(entry =>
                entry.Level == LogLevel.Information &&
                entry.Message.Contains(
                    ArchivePackageManifestFileNames.AdjacentSuffix,
                    StringComparison.OrdinalIgnoreCase)));
            AssertTextFile(
                Path.Combine(targetRoot, "folder", "file.txt"),
                "source content");
            AssertTextFile(
                Path.Combine(targetRoot, "removed.txt"),
                "kept in the archive during the dry run");
            Assert.IsFalse(File.Exists(Path.Combine(targetRoot, "new.txt")));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task BackupAndRestoreLogNestedReservedNameAndMetadataNamedDirectoryAsUserData()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var archiveRoot = Path.Combine(workspace, "archive");
            var destinationRoot = Path.Combine(workspace, "restored");
            await InitializeSourceRootAsync(sourceRoot, archiveRoot);
            await WriteTextFileAsync(
                Path.Combine(sourceRoot, "nested", BackupRootFileNames.Primary),
                "ordinary nested user data");
            var metadataLikeUserFileNames = new[]
            {
                "report.yabt-manifest.json",
                "report.yabt-ref.json",
                ArchiveHistoryFileNames.Manifest,
                ArchivePackageManifestFileNames.EmbeddedEntryName,
            };
            foreach (var fileName in metadataLikeUserFileNames)
            {
                await WriteTextFileAsync(
                    Path.Combine(sourceRoot, "nested", fileName),
                    "ordinary user data despite its metadata-like name");
            }
            Directory.CreateDirectory(Path.Combine(
                sourceRoot,
                "nested",
                FolderPolicyFileNames.Primary));

            var logSink = new CapturingLogSink();
            using var serviceProvider = CreateServices(loggerSink: logSink)
                .BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();

            await synchronizer.BackupAsync(new SyncRunRequest(sourceRoot));

            Assert.IsTrue(logSink.Entries.Any(entry =>
                entry.Level == LogLevel.Information &&
                entry.EventId.Id == YabtEventIds.BackupObjectAdded &&
                entry.Message.Contains(
                    $"nested/{BackupRootFileNames.Primary}",
                    StringComparison.Ordinal)));
            foreach (var fileName in metadataLikeUserFileNames)
            {
                Assert.IsTrue(logSink.Entries.Any(entry =>
                    entry.Level == LogLevel.Information &&
                    entry.EventId.Id == YabtEventIds.BackupObjectAdded &&
                    entry.Message.Contains($"nested/{fileName}", StringComparison.Ordinal)));
            }

            logSink.Clear();
            await synchronizer.RestoreAsync(new SyncRunRequest
            (
                sourceRoot,
                DestinationRoot: destinationRoot
            ));

            Assert.IsTrue(logSink.Entries.Any(entry =>
                entry.Level == LogLevel.Information &&
                entry.EventId.Id == YabtEventIds.RestoreItemWritten &&
                entry.Message.Contains(
                    $"nested/{BackupRootFileNames.Primary}",
                    StringComparison.Ordinal)));
            Assert.IsTrue(logSink.Entries.Any(entry =>
                entry.Level == LogLevel.Information &&
                entry.EventId.Id == YabtEventIds.RestoreItemWritten &&
                entry.Message.Contains(
                    $"nested/{FolderPolicyFileNames.Primary}",
                    StringComparison.Ordinal)));
            foreach (var fileName in metadataLikeUserFileNames)
            {
                Assert.IsTrue(logSink.Entries.Any(entry =>
                    entry.Level == LogLevel.Information &&
                    entry.EventId.Id == YabtEventIds.RestoreItemWritten &&
                    entry.Message.Contains($"nested/{fileName}", StringComparison.Ordinal)));
            }

            var changedFileName = metadataLikeUserFileNames[0];
            var deletedFileName = metadataLikeUserFileNames[1];
            await WriteTextFileAsync(
                Path.Combine(sourceRoot, "nested", changedFileName),
                "changed ordinary user data");
            File.Delete(Path.Combine(sourceRoot, "nested", deletedFileName));
            logSink.Clear();

            await synchronizer.BackupAsync(new SyncRunRequest(sourceRoot));

            Assert.IsTrue(logSink.Entries.Any(entry =>
                entry.Level == LogLevel.Information &&
                entry.EventId.Id == YabtEventIds.BackupObjectChanged &&
                entry.Message.Contains($"nested/{changedFileName}", StringComparison.Ordinal)));
            foreach (var fileName in new[] { changedFileName, deletedFileName })
            {
                Assert.IsTrue(logSink.Entries.Any(entry =>
                    entry.Level == LogLevel.Information &&
                    entry.EventId.Id == YabtEventIds.BackupObjectHistorized &&
                    entry.Message.Contains($"nested/{fileName}", StringComparison.Ordinal)));
            }
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task BackupAsyncRepairsCorruptedEmptyFolderMarkerAtDebugOnly()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var archiveRoot = Path.Combine(workspace, "archive");
            await InitializeSourceRootAsync(sourceRoot, archiveRoot);
            Directory.CreateDirectory(Path.Combine(sourceRoot, "empty"));

            var logSink = new CapturingLogSink();
            using var serviceProvider = CreateServices(loggerSink: logSink)
                .BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
            await synchronizer.BackupAsync(new SyncRunRequest(sourceRoot));

            var markerPath = Path.Combine(
                archiveRoot,
                "empty",
                ArchiveFolderMarkerFileNames.EmptyFolder);
            await File.WriteAllBytesAsync(markerPath, [1]);
            logSink.Clear();

            await synchronizer.BackupAsync(new SyncRunRequest
            (
                sourceRoot,
                ByteForByte: true
            ));

            Assert.IsFalse(logSink.Entries.Any(entry =>
                entry.Level == LogLevel.Information &&
                entry.EventId.Id is
                    YabtEventIds.BackupEmptyDirectoryCreated or
                    YabtEventIds.BackupEmptyDirectoryChanged or
                    YabtEventIds.BackupEmptyDirectoryRemoved));
            Assert.IsTrue(logSink.Entries.Any(entry =>
                entry.Level == LogLevel.Debug &&
                entry.EventId.Id == YabtEventIds.ControlMetadataOperation &&
                entry.Message.Contains(
                    ArchiveFolderMarkerFileNames.EmptyFolder,
                    StringComparison.Ordinal) &&
                entry.Message.Contains("Replaced", StringComparison.Ordinal)));
            Assert.IsTrue(logSink.Entries.Any(entry =>
                entry.Level == LogLevel.Debug &&
                entry.EventId.Id == YabtEventIds.EmptyDirectoryUnchanged &&
                entry.Message.Contains("empty", StringComparison.Ordinal)));
            Assert.AreEqual(0, new FileInfo(markerPath).Length);
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task RestoreAsyncLogsUnchangedDescriptorAndInvalidLogicalStateRecoveryAtDebug()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var archiveRoot = Path.Combine(workspace, "archive");
            var destinationRoot = Path.Combine(workspace, "restored");
            await InitializeSourceRootAsync(sourceRoot, archiveRoot);
            await WriteTextFileAsync(Path.Combine(sourceRoot, "file.txt"), "content");

            var logSink = new CapturingLogSink();
            using var serviceProvider = CreateServices(loggerSink: logSink)
                .BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
            await synchronizer.BackupAsync(new SyncRunRequest(sourceRoot));
            await synchronizer.RestoreAsync(new SyncRunRequest
            (
                sourceRoot,
                DestinationRoot: destinationRoot
            ));

            logSink.Clear();
            await synchronizer.RestoreAsync(new SyncRunRequest
            (
                sourceRoot,
                DestinationRoot: destinationRoot
            ));

            Assert.IsTrue(logSink.Entries.Any(entry =>
                entry.Level == LogLevel.Debug &&
                entry.EventId.Id == YabtEventIds.ControlMetadataOperation &&
                entry.Message.Contains("Determined unchanged", StringComparison.Ordinal) &&
                entry.Message.Contains(BackupRootFileNames.Primary, StringComparison.Ordinal)));

            await File.WriteAllTextAsync(
                Path.Combine(destinationRoot, ArchiveLogicalStateManifest.FileName),
                "not valid JSON",
                Encoding.UTF8);
            logSink.Clear();

            await synchronizer.RestoreAsync(new SyncRunRequest
            (
                sourceRoot,
                DestinationRoot: destinationRoot
            ));

            Assert.IsTrue(logSink.Entries.Any(entry =>
                entry.Level == LogLevel.Debug &&
                entry.EventId.Id == YabtEventIds.ControlMetadataOperation &&
                entry.Message.Contains(
                    "Ignored invalid or unreadable",
                    StringComparison.Ordinal) &&
                entry.Message.Contains(
                    ArchiveLogicalStateManifest.FileName,
                    StringComparison.Ordinal)));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task RestoreAsyncLogsMissingEmptyDestinationRootWithoutChangingSummaryCounts()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var archiveRoot = Path.Combine(workspace, "archive");
            var destinationRoot = Path.Combine(workspace, "restored");
            var dryRunDestinationRoot = Path.Combine(workspace, "dry-run-restored");
            await InitializeSourceRootAsync(sourceRoot, archiveRoot);

            var logSink = new CapturingLogSink();
            using var serviceProvider = CreateServices(loggerSink: logSink)
                .BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
            await synchronizer.BackupAsync(new SyncRunRequest(sourceRoot));

            logSink.Clear();
            var result = await synchronizer.RestoreAsync(new SyncRunRequest
            (
                sourceRoot,
                DestinationRoot: destinationRoot
            ));

            Assert.IsTrue(result.Completed);
            Assert.AreEqual(0, result.NewCount);
            Assert.AreEqual(0, result.ChangedCount);
            Assert.AreEqual(0, result.ExtraCount);
            Assert.AreEqual(0, result.UnchangedCount);
            Assert.IsTrue(Directory.Exists(destinationRoot));
            Assert.IsTrue(logSink.Entries.Any(entry =>
                entry.Level == LogLevel.Information &&
                entry.EventId.Id == YabtEventIds.RestoreItemWritten &&
                entry.Message.Contains("new directory .", StringComparison.Ordinal)));

            logSink.Clear();
            var dryRunResult = await synchronizer.RestoreAsync(new SyncRunRequest
            (
                sourceRoot,
                DestinationRoot: dryRunDestinationRoot,
                DryRun: true
            ));

            Assert.IsTrue(dryRunResult.Completed);
            Assert.AreEqual(0, dryRunResult.NewCount);
            Assert.AreEqual(0, dryRunResult.ChangedCount);
            Assert.AreEqual(0, dryRunResult.ExtraCount);
            Assert.AreEqual(0, dryRunResult.UnchangedCount);
            Assert.IsFalse(Directory.Exists(dryRunDestinationRoot));
            Assert.IsTrue(logSink.Entries.Any(entry =>
                entry.Level == LogLevel.Information &&
                entry.EventId.Id == YabtEventIds.RestoreWouldWriteItem &&
                entry.Message.Contains("new directory .", StringComparison.Ordinal)));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task BackupAsyncCarriesExactRootDescriptorAndRecordsV3Evidence()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var archiveRoot = Path.Combine(workspace, "archive");
            var descriptor = CreateRootDescriptor(
                [CreateFileSystemStore("target", archiveRoot)]);
            var descriptorBytes = CreateExactRootDescriptorBytes(descriptor);
            await WriteRootDescriptorBytesAsync(sourceRoot, descriptorBytes);
            await WriteTextFileAsync(Path.Combine(sourceRoot, "file.txt"), "content");

            using var serviceProvider = CreateServices().BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();

            var result = await synchronizer.BackupAsync(new SyncRunRequest(sourceRoot));

            Assert.IsTrue(result.Completed);
            CollectionAssert.AreEqual(
                descriptorBytes,
                await File.ReadAllBytesAsync(Path.Combine(
                    archiveRoot,
                    BackupRootFileNames.Primary)));

            var manifestSerializer = serviceProvider.GetRequiredService<IChangeManifestSerializer>();
            await using var manifestContent = File.OpenRead(Path.Combine(
                archiveRoot,
                ArchiveChangeManifest.BrotliFileName));
            var manifest = await ReadChangeManifestAsync(
                manifestSerializer,
                manifestContent,
                ArchiveChangeManifestCompression.Brotli);
            Assert.AreEqual(ArchiveChangeManifest.ExpectedSchemaVersion, manifest.SchemaVersion);
            Assert.AreEqual(descriptorBytes.LongLength, manifest.RootDescriptorContentLength);
            Assert.AreEqual(
                ArchiveHash.Compute(descriptorBytes),
                manifest.RootDescriptorContentHash);
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task VerifyAndBackupDryRunReportMissingArchivedRootDescriptorWithoutMutating()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var archiveRoot = Path.Combine(workspace, "archive");
            var descriptor = CreateRootDescriptor(
                [CreateFileSystemStore("target", archiveRoot)]);
            var descriptorBytes = CreateExactRootDescriptorBytes(descriptor);
            await WriteRootDescriptorBytesAsync(sourceRoot, descriptorBytes);
            await WriteTextFileAsync(Path.Combine(sourceRoot, "file.txt"), "content");

            using var serviceProvider = CreateServices().BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
            await synchronizer.BackupAsync(new SyncRunRequest(sourceRoot));

            var archivedDescriptorPath = Path.Combine(
                archiveRoot,
                BackupRootFileNames.Primary);
            var changeManifestPath = Path.Combine(
                archiveRoot,
                ArchiveChangeManifest.BrotliFileName);
            File.Delete(archivedDescriptorPath);
            var changeManifestBytes = await File.ReadAllBytesAsync(changeManifestPath);

            var verifyResult = await synchronizer.VerifyAsync(new SyncRunRequest(sourceRoot));

            Assert.IsFalse(verifyResult.Completed);
            StringAssert.Contains(verifyResult.Message, "root descriptor is missing");
            Assert.IsFalse(File.Exists(archivedDescriptorPath));
            CollectionAssert.AreEqual(
                changeManifestBytes,
                await File.ReadAllBytesAsync(changeManifestPath));

            var dryRunResult = await synchronizer.BackupAsync(new SyncRunRequest
            (
                sourceRoot,
                DryRun: true
            ));

            Assert.IsTrue(dryRunResult.Completed);
            StringAssert.Contains(dryRunResult.Message, "root descriptor is missing");
            StringAssert.Contains(
                dryRunResult.Message,
                "a mutating backup would install the exact source bytes");
            Assert.IsFalse(File.Exists(archivedDescriptorPath));
            CollectionAssert.AreEqual(
                changeManifestBytes,
                await File.ReadAllBytesAsync(changeManifestPath));
            Assert.IsFalse(Directory.Exists(Path.Combine(archiveRoot, ".yabt-hist")));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task VerifyAndBackupDryRunReportDifferentArchivedRootDescriptorWithoutMutating()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var archiveRoot = Path.Combine(workspace, "archive");
            var descriptor = CreateRootDescriptor(
                [CreateFileSystemStore("target", archiveRoot)]);
            var initialDescriptorBytes = CreateExactRootDescriptorBytes(descriptor);
            await WriteRootDescriptorBytesAsync(sourceRoot, initialDescriptorBytes);
            await WriteTextFileAsync(Path.Combine(sourceRoot, "file.txt"), "content");

            var logSink = new CapturingLogSink();
            using var serviceProvider = CreateServices(loggerSink: logSink)
                .BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
            await synchronizer.BackupAsync(new SyncRunRequest(sourceRoot));

            var archivedDescriptorPath = Path.Combine(
                archiveRoot,
                BackupRootFileNames.Primary);
            var changeManifestPath = Path.Combine(
                archiveRoot,
                ArchiveChangeManifest.BrotliFileName);
            var archivedDescriptorBytes = await File.ReadAllBytesAsync(archivedDescriptorPath);
            var changeManifestBytes = await File.ReadAllBytesAsync(changeManifestPath);
            var reconfiguredSourceDescriptorBytes = JsonSerializer.SerializeToUtf8Bytes(
                descriptor,
                JsonOptions);
            Assert.IsFalse(initialDescriptorBytes.SequenceEqual(reconfiguredSourceDescriptorBytes));
            await WriteRootDescriptorBytesAsync(
                sourceRoot,
                reconfiguredSourceDescriptorBytes);

            logSink.Clear();
            var verifyResult = await synchronizer.VerifyAsync(new SyncRunRequest(sourceRoot));

            Assert.IsFalse(verifyResult.Completed);
            StringAssert.Contains(
                verifyResult.Message,
                "root descriptor differs from the source descriptor");
            CollectionAssert.AreEqual(
                archivedDescriptorBytes,
                await File.ReadAllBytesAsync(archivedDescriptorPath));
            CollectionAssert.AreEqual(
                changeManifestBytes,
                await File.ReadAllBytesAsync(changeManifestPath));

            logSink.Clear();
            var dryRunResult = await synchronizer.BackupAsync(new SyncRunRequest
            (
                sourceRoot,
                DryRun: true
            ));

            Assert.IsTrue(dryRunResult.Completed);
            StringAssert.Contains(
                dryRunResult.Message,
                "root descriptor differs from the source descriptor");
            StringAssert.Contains(
                dryRunResult.Message,
                "a mutating backup would install the exact source bytes");
            CollectionAssert.AreEqual(
                archivedDescriptorBytes,
                await File.ReadAllBytesAsync(archivedDescriptorPath));
            CollectionAssert.AreEqual(
                changeManifestBytes,
                await File.ReadAllBytesAsync(changeManifestPath));
            Assert.IsFalse(Directory.Exists(Path.Combine(archiveRoot, ".yabt-hist")));
            Assert.IsTrue(logSink.Entries.Any(entry =>
                entry.Level == LogLevel.Debug &&
                entry.EventId.Id == YabtEventIds.ControlMetadataOperation &&
                entry.Message.Contains(
                    "Would move to history (replacement)",
                    StringComparison.Ordinal) &&
                entry.Message.Contains(BackupRootFileNames.Primary, StringComparison.Ordinal)));
            Assert.IsTrue(logSink.Entries.Any(entry =>
                entry.Level == LogLevel.Debug &&
                entry.EventId.Id == YabtEventIds.ControlMetadataOperation &&
                entry.Message.Contains("Would write replacement", StringComparison.Ordinal) &&
                entry.Message.Contains(BackupRootFileNames.Primary, StringComparison.Ordinal)));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task RestoreAsyncCarriesExactRootDescriptorWithoutRewritingConfiguredPaths()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var archiveRoot = Path.Combine(workspace, "archive");
            var destinationRoot = Path.Combine(workspace, "restored");
            var descriptor = CreateRootDescriptor(
                [CreateFileSystemStore("target", archiveRoot)]);
            var descriptorBytes = CreateExactRootDescriptorBytes(descriptor);
            await WriteRootDescriptorBytesAsync(sourceRoot, descriptorBytes);
            await WriteTextFileAsync(Path.Combine(sourceRoot, "file.txt"), "content");

            using var serviceProvider = CreateServices().BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
            await synchronizer.BackupAsync(new SyncRunRequest(sourceRoot));

            var result = await synchronizer.RestoreAsync(new SyncRunRequest
            (
                sourceRoot,
                DestinationRoot: destinationRoot
            ));

            Assert.IsTrue(result.Completed);
            var restoredDescriptorBytes = await File.ReadAllBytesAsync(Path.Combine(
                destinationRoot,
                BackupRootFileNames.Primary));
            CollectionAssert.AreEqual(descriptorBytes, restoredDescriptorBytes);

            var rootSerializer = serviceProvider.GetRequiredService<IBackupRootSerializer>();
            await using var restoredDescriptorContent = new MemoryStream(
                restoredDescriptorBytes,
                writable: false);
            var restoredDocument = await rootSerializer.ReadDocumentAsync(
                restoredDescriptorContent);
            var restoredStore = restoredDocument.Descriptor.Stores.Single();
            Assert.IsNotNull(restoredStore.ProviderProperties);
            Assert.AreEqual(
                archiveRoot,
                restoredStore.ProviderProperties["rootPath"].GetString());
            Assert.AreNotEqual(
                destinationRoot,
                restoredStore.ProviderProperties["rootPath"].GetString());
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task RestoreAsyncRequiresOptInBeforeReplacingSameArchiveDescriptor()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var archiveRoot = Path.Combine(workspace, "archive");
            var destinationRoot = Path.Combine(workspace, "restored");
            var destinationArchiveRoot = Path.Combine(workspace, "destination-archive");
            var archivedDescriptor = CreateRootDescriptor(
                [CreateFileSystemStore("target", archiveRoot)]);
            var archivedDescriptorBytes = CreateExactRootDescriptorBytes(archivedDescriptor);
            await WriteRootDescriptorBytesAsync(sourceRoot, archivedDescriptorBytes);
            await InitializeSourceRootAsync(
                destinationRoot,
                [CreateFileSystemStore("destination", destinationArchiveRoot)]);
            var existingDescriptorPath = Path.Combine(
                destinationRoot,
                BackupRootFileNames.Primary);
            var existingDescriptorBytes = await File.ReadAllBytesAsync(existingDescriptorPath);
            await WriteTextFileAsync(Path.Combine(sourceRoot, "file.txt"), "content");

            var timeProvider = new FixedTimeProvider(
                new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero));
            using var serviceProvider = CreateServices(timeProvider).BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
            await synchronizer.BackupAsync(new SyncRunRequest(sourceRoot));

            var exception = await Assert.ThrowsExactlyAsync<YabtSyncException>(
                () => synchronizer.RestoreAsync(new SyncRunRequest
                (
                    sourceRoot,
                    DestinationRoot: destinationRoot
                )));

            StringAssert.Contains(exception.Message, "--replace-root-descriptor");
            CollectionAssert.AreEqual(
                existingDescriptorBytes,
                await File.ReadAllBytesAsync(existingDescriptorPath));
            Assert.IsFalse(File.Exists(Path.Combine(destinationRoot, "file.txt")));

            var result = await synchronizer.RestoreAsync(new SyncRunRequest
            (
                sourceRoot,
                DestinationRoot: destinationRoot,
                ReplaceRootDescriptor: true
            ));

            Assert.IsTrue(result.Completed);
            CollectionAssert.AreEqual(
                archivedDescriptorBytes,
                await File.ReadAllBytesAsync(existingDescriptorPath));
            CollectionAssert.AreEqual(
                existingDescriptorBytes,
                await File.ReadAllBytesAsync(Path.Combine(
                    destinationRoot,
                    ".yabt-hist",
                    "20260824T120000Z",
                    BackupRootFileNames.Primary)));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task BackupAndRestoreAsyncUseCustomBidirectionalFormatHandler()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var archiveRoot = Path.Combine(workspace, "archive");
            var destinationRoot = Path.Combine(workspace, "restored");
            var sourceBytes = Encoding.UTF8.GetBytes("custom format round-trip content");
            await InitializeSourceRootAsync(sourceRoot, archiveRoot);
            await WritePolicyAsync(
                sourceRoot,
                TestBidirectionalArchiveFormatHandler.FormatNameValue);
            await File.WriteAllBytesAsync(
                Path.Combine(sourceRoot, TestBidirectionalArchiveFormatHandler.SourceFileName),
                sourceBytes);

            var formatHandler = new TestBidirectionalArchiveFormatHandler();
            using var serviceProvider = CreateCustomFormatServices(formatHandler)
                .BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();

            var backupResult = await synchronizer.BackupAsync(new SyncRunRequest(sourceRoot));

            Assert.IsTrue(backupResult.Completed);
            Assert.AreEqual(1, backupResult.NewCount);
            Assert.AreEqual(1, formatHandler.BackupProjectionCount);
            Assert.AreEqual(0, formatHandler.RestoreProjectionCount);
            var artifactPath = Path.Combine(
                archiveRoot,
                TestBidirectionalArchiveFormatHandler.ArtifactFileName);
            Assert.IsTrue(File.Exists(artifactPath));
            Assert.IsFalse(TestBidirectionalArchiveFormatHandler.ArtifactFileName.EndsWith(
                ".zip",
                StringComparison.OrdinalIgnoreCase));
            var artifactBytes = await File.ReadAllBytesAsync(artifactPath);
            Assert.IsFalse(sourceBytes.SequenceEqual(artifactBytes));

            var restoreResult = await synchronizer.RestoreAsync(new SyncRunRequest
            (
                sourceRoot,
                DestinationRoot: destinationRoot
            ));

            Assert.IsTrue(restoreResult.Completed);
            Assert.AreEqual(1, restoreResult.NewCount);
            Assert.AreEqual(1, formatHandler.BackupProjectionCount);
            Assert.AreEqual(1, formatHandler.RestoreProjectionCount);
            CollectionAssert.AreEqual(
                sourceBytes,
                await File.ReadAllBytesAsync(Path.Combine(
                    destinationRoot,
                    TestBidirectionalArchiveFormatHandler.SourceFileName)));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task BackupAsyncRejectsEmptyHistoryPrefix()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var targetRoot = Path.Combine(workspace, "target");
            await InitializeSourceRootAsync(
                sourceRoot,
                [CreateFileSystemStore("target", targetRoot)],
                layout: new ArchiveLayout(HistPrefix: " "));
            await WriteTextFileAsync(Path.Combine(sourceRoot, "file.txt"), "content");

            using var serviceProvider = CreateServices().BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();

            var exception = await Assert.ThrowsExactlyAsync<YabtMetadataException>(
                () => synchronizer.BackupAsync(new SyncRunRequest(sourceRoot)));

            StringAssert.Contains(exception.Message, "history prefix");
            Assert.IsFalse(File.Exists(Path.Combine(targetRoot, "file.txt")));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task BackupAsyncRejectsSecretPropertyOnUnselectedAzureStoreBeforeTargetMutation()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var targetRoot = Path.Combine(workspace, "target");
            var unselectedAzureStore = new BackupRootStore("unused-azure", "azureBlob")
            {
                ProviderProperties = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["connectionString"] = JsonSerializer.SerializeToElement(
                        "DefaultEndpointsProtocol=https;AccountName=fake;AccountKey=fake"),
                },
            };
            await InitializeSourceRootAsync
            (
                sourceRoot,
                [CreateFileSystemStore("target", targetRoot), unselectedAzureStore],
                defaultStoreId: "target"
            );
            await WriteTextFileAsync(Path.Combine(sourceRoot, "file.txt"), "content");

            using var serviceProvider = CreateServices().BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();

            var exception = await Assert.ThrowsExactlyAsync<YabtMetadataException>(
                () => synchronizer.BackupAsync(new SyncRunRequest(sourceRoot)));

            StringAssert.Contains(exception.Message, "unused-azure");
            StringAssert.Contains(
                exception.Message,
                "may contain only id, kind, and configSectionPath");
            Assert.AreEqual(0, Directory.GetFiles(
                targetRoot,
                "*",
                SearchOption.AllDirectories).Length);
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task RestoreAsyncRestoresMirrorFilesToEmptyDestination()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var archiveRoot = Path.Combine(workspace, "archive");
            var destinationRoot = Path.Combine(workspace, "restored");
            await InitializeSourceRootAsync(sourceRoot, archiveRoot);
            await WriteTextFileAsync(
                Path.Combine(sourceRoot, "folder", "file.txt"),
                "source content");

            using var serviceProvider = CreateServices().BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
            await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));

            var result = await synchronizer.RestoreAsync(new SyncRunRequest
            (
                sourceRoot,
                DestinationRoot: destinationRoot
            ));

            Assert.IsTrue(result.Completed);
            Assert.AreEqual(1, result.NewCount);
            AssertTextFile(
                Path.Combine(destinationRoot, "folder", "file.txt"),
                "source content");
            Assert.IsTrue(Directory.Exists(Path.Combine(destinationRoot, ".yabt-tmp")));
            Assert.IsTrue(File.Exists(Path.Combine(destinationRoot, BackupRootFileNames.Primary)));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task RestoreAsyncUsesLogicalStateEvidenceUnlessByteForByteIsRequested()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var archiveRoot = Path.Combine(workspace, "archive");
            var destinationRoot = Path.Combine(workspace, "restored");
            await InitializeSourceRootAsync(sourceRoot, archiveRoot);
            await WriteTextFileAsync(Path.Combine(sourceRoot, "file.txt"), "content");

            using var serviceProvider = CreateServices().BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
            await synchronizer.BackupAsync(new SyncRunRequest(sourceRoot));
            await synchronizer.RestoreAsync(new SyncRunRequest
            (
                sourceRoot,
                DestinationRoot: destinationRoot
            ));

            var logicalStateManifestPath = Path.Combine(
                destinationRoot,
                ArchiveLogicalStateManifest.FileName);
            Assert.IsTrue(File.Exists(logicalStateManifestPath));
            var logicalStateSerializer =
                serviceProvider.GetRequiredService<ILogicalStateManifestSerializer>();
            await using (var logicalStateContent = File.OpenRead(logicalStateManifestPath))
            {
                var logicalStateManifest = await logicalStateSerializer.ReadAsync(
                    logicalStateContent);
                Assert.AreEqual(1, logicalStateManifest.Entries.Count());
                Assert.AreEqual(
                    "file.txt",
                    logicalStateManifest.Entries.Single().LogicalRelativePath);
            }

            var destinationFilePath = Path.Combine(destinationRoot, "file.txt");
            await using var exclusiveDestinationContent = new FileStream
            (
                destinationFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous
            );

            var normalResult = await synchronizer.RestoreAsync(new SyncRunRequest
            (
                sourceRoot,
                DestinationRoot: destinationRoot
            ));

            Assert.IsTrue(normalResult.Completed);
            Assert.AreEqual(0, normalResult.NewCount);
            Assert.AreEqual(0, normalResult.ChangedCount);
            Assert.AreEqual(1, normalResult.UnchangedCount);

            var byteForByteException = await Assert.ThrowsAsync<YabtFileSystemException>(
                () => synchronizer.RestoreAsync(new SyncRunRequest
                (
                    sourceRoot,
                    ByteForByte: true,
                    DestinationRoot: destinationRoot
                )));
            StringAssert.Contains(byteForByteException.Message, "file.txt");
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task RestoreAsyncRejectsCaseDistinctLogicalStateEntriesBeforeMutationOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive(
                "This logical-state collision is specific to case-insensitive Windows paths.");
        }

        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var archiveRoot = Path.Combine(workspace, "archive");
            var destinationRoot = Path.Combine(workspace, "restored");
            var destinationFilePath = Path.Combine(destinationRoot, "keep.txt");
            await InitializeSourceRootAsync(sourceRoot, archiveRoot);
            await WriteTextFileAsync(Path.Combine(sourceRoot, "file.txt"), "archive content");
            await WriteTextFileAsync(destinationFilePath, "destination content");

            using var serviceProvider = CreateServices().BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
            var logicalStateSerializer =
                serviceProvider.GetRequiredService<ILogicalStateManifestSerializer>();
            await synchronizer.BackupAsync(new SyncRunRequest(sourceRoot));

            var evidenceBytes = Encoding.UTF8.GetBytes("logical evidence");
            var contentHash = ArchiveHash.Compute(evidenceBytes);
            var statFingerprint = ArchiveChangeFingerprint.Create
            (
                evidenceBytes.LongLength,
                new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero)
            );
            var logicalStateManifest = logicalStateSerializer.Create
            (
                [
                    new("A.txt", statFingerprint, contentHash),
                    new("a.txt", statFingerprint, contentHash),
                ]
            );
            var logicalStateManifestPath = Path.Combine(
                destinationRoot,
                ArchiveLogicalStateManifest.FileName);
            await using (var content = File.Create(logicalStateManifestPath))
            {
                await logicalStateSerializer.WriteAsync(logicalStateManifest, content);
            }

            var originalLogicalStateBytes = await File.ReadAllBytesAsync(
                logicalStateManifestPath);
            var exception = await Assert.ThrowsExactlyAsync<YabtSyncException>(
                () => synchronizer.RestoreAsync(new SyncRunRequest
                (
                    sourceRoot,
                    DestinationRoot: destinationRoot
                )));

            StringAssert.Contains(exception.Message, "Logical-state manifest");
            StringAssert.Contains(exception.Message, "cannot be represented distinctly");
            AssertTextFile(destinationFilePath, "destination content");
            CollectionAssert.AreEqual(
                originalLogicalStateBytes,
                await File.ReadAllBytesAsync(logicalStateManifestPath));
            Assert.IsFalse(File.Exists(Path.Combine(
                destinationRoot,
                ArchiveLogicalStateManifest.InvalidationMarkerFileName)));
            Assert.IsFalse(File.Exists(Path.Combine(
                destinationRoot,
                BackupRootFileNames.Primary)));
            Assert.IsFalse(Directory.Exists(Path.Combine(destinationRoot, ".yabt-hist")));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task RestoreAsyncRecreatesEmptyMirrorFolderWithoutMarkerFile()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var archiveRoot = Path.Combine(workspace, "archive");
            var destinationRoot = Path.Combine(workspace, "restored");
            await InitializeSourceRootAsync(sourceRoot, archiveRoot);
            Directory.CreateDirectory(Path.Combine(sourceRoot, "empty"));

            using var serviceProvider = CreateServices().BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
            await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));

            var result = await synchronizer.RestoreAsync(new SyncRunRequest
            (
                sourceRoot,
                DestinationRoot: destinationRoot
            ));

            Assert.IsTrue(result.Completed);
            Assert.AreEqual(1, result.NewCount);
            Assert.IsTrue(Directory.Exists(Path.Combine(destinationRoot, "empty")));
            Assert.IsFalse(File.Exists(Path.Combine(
                destinationRoot,
                "empty",
                ArchiveFolderMarkerFileNames.EmptyFolder)));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task RestoreAsyncExtractsNestedZipPackage()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var archiveRoot = Path.Combine(workspace, "archive");
            var destinationRoot = Path.Combine(workspace, "restored");
            var photosRoot = Path.Combine(sourceRoot, "albums", "photos");
            await InitializeSourceRootAsync(sourceRoot, archiveRoot);
            await WritePolicyAsync(photosRoot, ZipArchiveFormatName.Value);
            await WriteTextFileAsync(
                Path.Combine(photosRoot, "image.txt"),
                "image content");
            Directory.CreateDirectory(Path.Combine(photosRoot, "empty"));

            using var serviceProvider = CreateServices().BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
            await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));

            var result = await synchronizer.RestoreAsync(new SyncRunRequest
            (
                sourceRoot,
                DestinationRoot: destinationRoot
            ));

            Assert.IsTrue(result.Completed);
            AssertTextFile(
                Path.Combine(destinationRoot, "albums", "photos", "image.txt"),
                "image content");
            Assert.IsTrue(Directory.Exists(Path.Combine(
                destinationRoot,
                "albums",
                "photos",
                "empty")));
            Assert.AreEqual(0, Directory.GetFiles(
                destinationRoot,
                "*.zip",
                SearchOption.AllDirectories).Length);

            var secondResult = await synchronizer.RestoreAsync(new SyncRunRequest
            (
                sourceRoot,
                DestinationRoot: destinationRoot
            ));
            Assert.AreEqual(0, secondResult.NewCount);
            Assert.AreEqual(0, secondResult.ChangedCount);
            Assert.AreEqual(0, secondResult.ExtraCount);
            Assert.IsTrue(secondResult.UnchangedCount >= 1);
            Assert.IsFalse(Directory.Exists(Path.Combine(destinationRoot, ".yabt-hist")));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task RestoreAsyncRecursivelyExtractsZipFolderNestedInsidePackagedRoot()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var archiveRoot = Path.Combine(workspace, "archive");
            var destinationRoot = Path.Combine(workspace, "restored");
            var nestedZipRoot = Path.Combine(sourceRoot, "albums", "photos");
            await InitializeSourceRootAsync(sourceRoot, archiveRoot);
            await WritePolicyAsync(sourceRoot, ZipArchiveFormatName.Value);
            await WritePolicyAsync(nestedZipRoot, ZipArchiveFormatName.Value);
            await WriteTextFileAsync(
                Path.Combine(sourceRoot, "root.txt"),
                "root content");
            await WriteTextFileAsync(
                Path.Combine(nestedZipRoot, "image.txt"),
                "nested image content");
            Directory.CreateDirectory(Path.Combine(nestedZipRoot, "empty"));

            using var serviceProvider = CreateServices().BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
            await synchronizer.BackupAsync(new SyncRunRequest(sourceRoot));

            var result = await synchronizer.RestoreAsync(new SyncRunRequest
            (
                sourceRoot,
                DestinationRoot: destinationRoot
            ));

            Assert.IsTrue(result.Completed);
            AssertTextFile(Path.Combine(destinationRoot, "root.txt"), "root content");
            AssertTextFile(
                Path.Combine(destinationRoot, "albums", "photos", "image.txt"),
                "nested image content");
            Assert.IsTrue(Directory.Exists(Path.Combine(
                destinationRoot,
                "albums",
                "photos",
                "empty")));
            AssertPolicyFormat(
                Path.Combine(destinationRoot, FolderPolicyFileNames.Primary),
                ZipArchiveFormatName.Value);
            AssertPolicyFormat(
                Path.Combine(
                    destinationRoot,
                    "albums",
                    "photos",
                    FolderPolicyFileNames.Primary),
                ZipArchiveFormatName.Value);
            Assert.AreEqual(0, Directory.GetFiles(
                destinationRoot,
                "*.zip",
                SearchOption.AllDirectories).Length);
            Assert.AreEqual(0, Directory.GetFiles(
                destinationRoot,
                "*.zip.yabt-manifest.json",
                SearchOption.AllDirectories).Length);
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task RestoreAsyncKeepsHashLookingZipWithoutProjectionProvenanceAsOrdinaryFile()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var archiveRoot = Path.Combine(workspace, "archive");
            var destinationRoot = Path.Combine(workspace, "restored");
            var ordinaryContent = Encoding.UTF8.GetBytes("not a package");
            var ordinaryFileName =
                $"ordinary.{ArchiveHash.FormatFileNameToken(ArchiveHash.Compute(ordinaryContent))}.zip";
            await InitializeSourceRootAsync(sourceRoot, archiveRoot);
            await File.WriteAllBytesAsync(
                Path.Combine(sourceRoot, ordinaryFileName),
                ordinaryContent);

            using var serviceProvider = CreateServices().BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
            await synchronizer.BackupAsync(new SyncRunRequest(sourceRoot));

            var result = await synchronizer.RestoreAsync(new SyncRunRequest
            (
                sourceRoot,
                DestinationRoot: destinationRoot
            ));

            Assert.IsTrue(result.Completed);
            CollectionAssert.AreEqual(
                ordinaryContent,
                await File.ReadAllBytesAsync(Path.Combine(
                    destinationRoot,
                    ordinaryFileName)));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RestoreAsyncRejectsInvalidAdjacentPackageManifestBeforeDestinationMutation
    (
        bool deleteManifest
    )
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var archiveRoot = Path.Combine(workspace, "archive");
            var destinationRoot = Path.Combine(workspace, "restored");
            var destinationFilePath = Path.Combine(destinationRoot, "file.txt");
            await InitializeSourceRootAsync(sourceRoot, archiveRoot);
            await WritePolicyAsync(sourceRoot, ZipArchiveFormatName.Value);
            await WriteTextFileAsync(Path.Combine(sourceRoot, "file.txt"), "archive content");
            await WriteTextFileAsync(destinationFilePath, "destination content");

            using var serviceProvider = CreateServices().BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
            await synchronizer.BackupAsync(new SyncRunRequest(sourceRoot));
            var manifestPath = Directory.GetFiles(
                archiveRoot,
                "*.zip.yabt-manifest.json",
                SearchOption.AllDirectories).Single();
            if (deleteManifest)
            {
                File.Delete(manifestPath);
            }
            else
            {
                await File.AppendAllTextAsync(manifestPath, "tampered");
            }

            var exception = await Assert.ThrowsExactlyAsync<YabtSyncException>(
                () => synchronizer.RestoreAsync(new SyncRunRequest
                (
                    sourceRoot,
                    DestinationRoot: destinationRoot
                )));

            StringAssert.Contains(exception.Message, "manifest");
            AssertTextFile(destinationFilePath, "destination content");
            Assert.IsFalse(File.Exists(Path.Combine(
                destinationRoot,
                BackupRootFileNames.Primary)));
            Assert.IsFalse(Directory.Exists(Path.Combine(destinationRoot, ".yabt-hist")));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task RestoreAsyncRejectsEmptyPackagedRootProjectionBeforeDestinationMutation()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var archiveRoot = Path.Combine(workspace, "archive");
            var destinationRoot = Path.Combine(workspace, "restored");
            var destinationFilePath = Path.Combine(destinationRoot, "file.txt");
            var descriptor = CreateRootDescriptor
            (
                [CreateFileSystemStore("target", archiveRoot)],
                changeManifestCompression: ArchiveChangeManifestCompression.None
            );
            var descriptorBytes = CreateExactRootDescriptorBytes(descriptor);
            await WriteRootDescriptorBytesAsync(sourceRoot, descriptorBytes);
            await WritePolicyAsync(sourceRoot, ZipArchiveFormatName.Value);
            await WriteTextFileAsync(Path.Combine(sourceRoot, "file.txt"), "archive content");
            await WriteTextFileAsync(destinationFilePath, "destination content");

            using var serviceProvider = CreateServices().BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
            var manifestSerializer = serviceProvider.GetRequiredService<IChangeManifestSerializer>();
            await synchronizer.BackupAsync(new SyncRunRequest(sourceRoot));

            var changeManifestPath = Path.Combine(
                archiveRoot,
                ArchiveChangeManifest.UncompressedFileName);
            ArchiveChangeManifest validManifest;
            await using (var manifestContent = File.OpenRead(changeManifestPath))
            {
                validManifest = await manifestSerializer.ReadAsync(manifestContent);
            }
            foreach (var entry in validManifest.Entries)
            {
                File.Delete(Path.Combine(
                    archiveRoot,
                    entry.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            }

            var emptyPackagedRootManifest = manifestSerializer.Create
            (
                [],
                ZipArchiveFormatName.Value,
                validManifest.RootDescriptorContentHash,
                validManifest.RootDescriptorContentLength
            );
            await using (var replacementContent = File.Create(changeManifestPath))
            {
                await manifestSerializer.WriteAsync(
                    emptyPackagedRootManifest,
                    replacementContent);
            }

            var exception = await Assert.ThrowsExactlyAsync<YabtSyncException>(
                () => synchronizer.RestoreAsync(new SyncRunRequest
                (
                    sourceRoot,
                    DestinationRoot: destinationRoot
                )));

            StringAssert.Contains(exception.Message, "packaged-root projection");
            AssertTextFile(destinationFilePath, "destination content");
            Assert.IsFalse(File.Exists(Path.Combine(
                destinationRoot,
                BackupRootFileNames.Primary)));
            Assert.IsFalse(Directory.Exists(Path.Combine(destinationRoot, ".yabt-hist")));
            Assert.IsFalse(File.Exists(Path.Combine(
                destinationRoot,
                ArchiveLogicalStateManifest.FileName)));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task RestoreAsyncDoesNotOpenTopLevelZipPackageDuringSecondNoOpRestore()
    {
        var sourceRoot = Path.Combine(
            Path.GetTempPath(),
            $"yabt-no-op-zip-source-{Guid.NewGuid():N}");
        var destinationRoot = Path.Combine(
            Path.GetTempPath(),
            $"yabt-no-op-zip-destination-{Guid.NewGuid():N}");
        try
        {
            var sourceStore = new MemoryObjectStore(provideContentHash: true);
            var archiveStore = new MemoryObjectStore(provideContentHash: true);
            var guardedArchiveStore = new DataReadGuardObjectStore(archiveStore);
            await UploadTextObjectAsync(sourceStore, "file.txt", "content");
            var descriptor = CreateRootDescriptor(
                [new BackupRootStore("target", FixedBackupRootStoreResolver.StoreKindValue)]);

            using var serviceProvider = CreateStreamingServices(
                sourceRoot,
                descriptor,
                sourceStore,
                guardedArchiveStore,
                folderPolicy: new FolderPolicy(ZipArchiveFormatName.Value))
                .BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
            await synchronizer.BackupAsync(new SyncRunRequest(sourceRoot));
            var packageKey = archiveStore.Snapshot()
                .Single(archiveObject => archiveObject.Key.EndsWith(
                    ".zip",
                    StringComparison.Ordinal))
                .Key;

            await synchronizer.RestoreAsync(new SyncRunRequest
            (
                sourceRoot,
                DestinationRoot: destinationRoot
            ));
            guardedArchiveStore.ResetCounts();

            var secondResult = await synchronizer.RestoreAsync(new SyncRunRequest
            (
                sourceRoot,
                DestinationRoot: destinationRoot
            ));

            Assert.IsTrue(secondResult.Completed);
            Assert.AreEqual(0, secondResult.NewCount);
            Assert.AreEqual(0, secondResult.ChangedCount);
            Assert.AreEqual(1, secondResult.UnchangedCount);
            Assert.AreEqual(0, guardedArchiveStore.GetOpenReadCount(packageKey));
        }
        finally
        {
            DeleteWorkspace(sourceRoot);
            DeleteWorkspace(destinationRoot);
        }
    }

    [TestMethod]
    public async Task RestoreAsyncRejectsDirectoryNamesThatDifferOnlyByCaseOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("This collision is specific to case-insensitive Windows paths.");
        }

        var sourceRoot = Path.Combine(
            Path.GetTempPath(),
            $"yabt-case-collision-source-{Guid.NewGuid():N}");
        var destinationRoot = Path.Combine(
            Path.GetTempPath(),
            $"yabt-case-collision-destination-{Guid.NewGuid():N}");
        try
        {
            var sourceStore = new MemoryObjectStore();
            var archiveStore = new MemoryObjectStore();
            await UploadTextObjectAsync(sourceStore, "Foo/a.txt", "first");
            await UploadTextObjectAsync(sourceStore, "foo/b.txt", "second");
            var descriptor = CreateRootDescriptor(
                [new BackupRootStore("target", FixedBackupRootStoreResolver.StoreKindValue)]);

            using var serviceProvider = CreateStreamingServices(
                sourceRoot,
                descriptor,
                sourceStore,
                archiveStore).BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
            await synchronizer.BackupAsync(new SyncRunRequest(sourceRoot));

            var exception = await Assert.ThrowsExactlyAsync<YabtSyncException>(
                () => synchronizer.RestoreAsync(new SyncRunRequest
                (
                    sourceRoot,
                    DestinationRoot: destinationRoot
                )));

            StringAssert.Contains(exception.Message, "differ only by case");
            Assert.IsFalse(Directory.Exists(destinationRoot));
        }
        finally
        {
            DeleteWorkspace(sourceRoot);
            DeleteWorkspace(destinationRoot);
        }
    }

    [TestMethod]
    public async Task RestoreAsyncExtractsPackagedRootDirectlyIntoDestination()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var archiveRoot = Path.Combine(workspace, "archive");
            var destinationRoot = Path.Combine(workspace, "restored");
            var sourceFilePath = Path.Combine(sourceRoot, "file.txt");
            var expectedLastModifiedUtc = new DateTime(
                2020,
                1,
                2,
                3,
                4,
                6,
                DateTimeKind.Utc);
            await InitializeSourceRootAsync(sourceRoot, archiveRoot);
            await WritePolicyAsync(sourceRoot, ZipArchiveFormatName.Value);
            await WriteTextFileAsync(sourceFilePath, "content");
            File.SetLastWriteTimeUtc(sourceFilePath, expectedLastModifiedUtc);

            using var serviceProvider = CreateServices().BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
            await synchronizer.BackupAsync(new SyncRunRequest(sourceRoot));
            File.Delete(Path.Combine(sourceRoot, FolderPolicyFileNames.Primary));

            var result = await synchronizer.RestoreAsync(new SyncRunRequest
            (
                sourceRoot,
                DestinationRoot: destinationRoot
            ));

            Assert.IsTrue(result.Completed);
            AssertTextFile(Path.Combine(destinationRoot, "file.txt"), "content");
            Assert.AreEqual(
                expectedLastModifiedUtc,
                File.GetLastWriteTimeUtc(Path.Combine(destinationRoot, "file.txt")));
            Assert.IsFalse(Directory.Exists(Path.Combine(destinationRoot, "source")));
            Assert.IsTrue(File.Exists(Path.Combine(
                destinationRoot,
                FolderPolicyFileNames.Primary)));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task RestoreAsyncUsesExactPackagedFileModificationTimeFromManifest()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var archiveRoot = Path.Combine(workspace, "archive");
            var destinationRoot = Path.Combine(workspace, "restored");
            var sourceFilePath = Path.Combine(sourceRoot, "file.txt");
            var sourceLastModifiedUtc = new DateTime(
                2020,
                1,
                2,
                3,
                4,
                5,
                123,
                DateTimeKind.Utc);
            await InitializeSourceRootAsync(sourceRoot, archiveRoot);
            await WritePolicyAsync(sourceRoot, ZipArchiveFormatName.Value);
            await WriteTextFileAsync(sourceFilePath, "content");
            File.SetLastWriteTimeUtc(sourceFilePath, sourceLastModifiedUtc);

            using var serviceProvider = CreateServices().BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
            await synchronizer.BackupAsync(new SyncRunRequest(sourceRoot));
            await synchronizer.RestoreAsync(new SyncRunRequest
            (
                sourceRoot,
                DestinationRoot: destinationRoot
            ));

            Assert.AreEqual(
                sourceLastModifiedUtc,
                File.GetLastWriteTimeUtc(Path.Combine(destinationRoot, "file.txt")));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task RestoreAsyncUsesOriginalMirrorFileModificationTime()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var archiveRoot = Path.Combine(workspace, "archive");
            var destinationRoot = Path.Combine(workspace, "restored");
            var sourceFilePath = Path.Combine(sourceRoot, "file.txt");
            var expectedLastModifiedUtc = new DateTime(
                2020,
                2,
                3,
                4,
                5,
                6,
                DateTimeKind.Utc);
            await InitializeSourceRootAsync(sourceRoot, archiveRoot);
            await WriteTextFileAsync(sourceFilePath, "content");
            File.SetLastWriteTimeUtc(sourceFilePath, expectedLastModifiedUtc);

            using var serviceProvider = CreateServices().BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
            await synchronizer.BackupAsync(new SyncRunRequest(sourceRoot));

            await synchronizer.RestoreAsync(new SyncRunRequest
            (
                sourceRoot,
                DestinationRoot: destinationRoot
            ));

            Assert.AreEqual(
                expectedLastModifiedUtc,
                File.GetLastWriteTimeUtc(Path.Combine(destinationRoot, "file.txt")));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task RestoreAsyncDryRunDoesNotCreateDestination()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var archiveRoot = Path.Combine(workspace, "archive");
            var destinationRoot = Path.Combine(workspace, "restored");
            await InitializeSourceRootAsync(sourceRoot, archiveRoot);
            await WriteTextFileAsync(Path.Combine(sourceRoot, "file.txt"), "content");

            using var serviceProvider = CreateServices().BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
            await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));

            var result = await synchronizer.RestoreAsync(new SyncRunRequest
            (
                sourceRoot,
                DryRun: true,
                DestinationRoot: destinationRoot
            ));

            Assert.IsTrue(result.Completed);
            Assert.AreEqual(1, result.NewCount);
            Assert.IsFalse(Directory.Exists(destinationRoot));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task RestoreAsyncHistorizesChangedAndExtraDestinationItems()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var archiveRoot = Path.Combine(workspace, "archive");
            var destinationRoot = Path.Combine(workspace, "restored");
            await InitializeSourceRootAsync(sourceRoot, archiveRoot);
            await WriteTextFileAsync(Path.Combine(sourceRoot, "changed.txt"), "archive content");
            await WriteTextFileAsync(Path.Combine(sourceRoot, "same.txt"), "same content");
            await WriteTextFileAsync(
                Path.Combine(destinationRoot, "changed.txt"),
                "destination content");
            await WriteTextFileAsync(
                Path.Combine(destinationRoot, "same.txt"),
                "same content");
            await WriteTextFileAsync(
                Path.Combine(destinationRoot, "extra.txt"),
                "extra content");
            await WriteTextFileAsync(
                Path.Combine(destinationRoot, "extra-folder", "file.txt"),
                "extra folder content");
            Directory.CreateDirectory(Path.Combine(
                destinationRoot,
                "extra-folder",
                "empty"));

            var timeProvider = new FixedTimeProvider(
                new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero));
            using var serviceProvider = CreateServices(timeProvider).BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
            await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));

            var result = await synchronizer.RestoreAsync(new SyncRunRequest
            (
                sourceRoot,
                DestinationRoot: destinationRoot
            ));

            Assert.IsTrue(result.Completed);
            Assert.AreEqual(0, result.NewCount);
            Assert.AreEqual(1, result.ChangedCount);
            Assert.AreEqual(2, result.ExtraCount);
            Assert.AreEqual(1, result.UnchangedCount);
            AssertTextFile(Path.Combine(destinationRoot, "changed.txt"), "archive content");
            AssertTextFile(Path.Combine(destinationRoot, "same.txt"), "same content");
            Assert.IsFalse(File.Exists(Path.Combine(destinationRoot, "extra.txt")));
            Assert.IsFalse(Directory.Exists(Path.Combine(destinationRoot, "extra-folder")));

            var historyRoots = Directory.GetDirectories(
                Path.Combine(destinationRoot, ".yabt-hist"));
            Assert.AreEqual(1, historyRoots.Length);
            AssertTextFile(
                Path.Combine(historyRoots[0], "changed.txt"),
                "destination content");
            AssertTextFile(
                Path.Combine(historyRoots[0], "extra.txt"),
                "extra content");
            AssertTextFile(
                Path.Combine(historyRoots[0], "extra-folder", "file.txt"),
                "extra folder content");
            Assert.IsTrue(Directory.Exists(Path.Combine(
                historyRoots[0],
                "extra-folder",
                "empty")));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task RestoreAsyncHistorizesFileAndFolderShapeConflicts()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var archiveRoot = Path.Combine(workspace, "archive");
            var destinationRoot = Path.Combine(workspace, "restored");
            await InitializeSourceRootAsync(sourceRoot, archiveRoot);
            await WriteTextFileAsync(Path.Combine(sourceRoot, "as-file"), "restored file");
            await WriteTextFileAsync(
                Path.Combine(sourceRoot, "as-folder", "child.txt"),
                "restored child");
            await WriteTextFileAsync(
                Path.Combine(destinationRoot, "as-file", "old.txt"),
                "old folder child");
            await WriteTextFileAsync(
                Path.Combine(destinationRoot, "as-folder"),
                "old file");

            using var serviceProvider = CreateServices().BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
            await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));

            var result = await synchronizer.RestoreAsync(new SyncRunRequest
            (
                sourceRoot,
                DestinationRoot: destinationRoot
            ));

            Assert.IsTrue(result.Completed);
            Assert.AreEqual(2, result.ChangedCount);
            AssertTextFile(Path.Combine(destinationRoot, "as-file"), "restored file");
            AssertTextFile(
                Path.Combine(destinationRoot, "as-folder", "child.txt"),
                "restored child");

            var historyRoot = Directory.GetDirectories(
                Path.Combine(destinationRoot, ".yabt-hist")).Single();
            AssertTextFile(
                Path.Combine(historyRoot, "as-file", "old.txt"),
                "old folder child");
            AssertTextFile(Path.Combine(historyRoot, "as-folder"), "old file");
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task RestoreAsyncSequencesHistoryWhenPriorFileBlocksHistoricalAncestor()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var archiveRoot = Path.Combine(workspace, "archive");
            var destinationRoot = Path.Combine(workspace, "restored");
            await InitializeSourceRootAsync(sourceRoot, archiveRoot);
            await WriteTextFileAsync(
                Path.Combine(sourceRoot, "folder", "keep.txt"),
                "archived");
            await WriteTextFileAsync(
                Path.Combine(destinationRoot, "folder"),
                "old file");

            var timeProvider = new FixedTimeProvider(
                new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero));
            using var serviceProvider = CreateServices(timeProvider).BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
            await synchronizer.BackupAsync(new SyncRunRequest(sourceRoot));
            await synchronizer.RestoreAsync(new SyncRunRequest
            (
                sourceRoot,
                DestinationRoot: destinationRoot
            ));
            await WriteTextFileAsync(
                Path.Combine(destinationRoot, "folder", "extra.txt"),
                "extra");

            var secondResult = await synchronizer.RestoreAsync(new SyncRunRequest
            (
                sourceRoot,
                DestinationRoot: destinationRoot
            ));

            Assert.IsTrue(secondResult.Completed);
            Assert.AreEqual(1, secondResult.ExtraCount);
            var historyRoot = Path.Combine(destinationRoot, ".yabt-hist");
            var historyVersions = Directory.GetDirectories(historyRoot);
            Assert.AreEqual(2, historyVersions.Length);
            var historicalFile = Directory.GetFiles(
                historyRoot,
                "folder",
                SearchOption.AllDirectories).Single();
            var historicalExtra = Directory.GetFiles(
                historyRoot,
                "extra.txt",
                SearchOption.AllDirectories).Single();
            AssertTextFile(historicalFile, "old file");
            AssertTextFile(historicalExtra, "extra");
            Assert.AreNotEqual(
                Path.GetDirectoryName(historicalFile),
                Path.GetDirectoryName(historicalExtra));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task RestoreAsyncSequencesHistoryForCaseInsensitiveTimestampCollision()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var archiveRoot = Path.Combine(workspace, "archive");
            var destinationRoot = Path.Combine(workspace, "restored");
            const string timestampSegment = "20260821T120000Z";
            const string existingTimestampSegment = "20260821t120000z";
            await InitializeSourceRootAsync(sourceRoot, archiveRoot);
            await WriteTextFileAsync(Path.Combine(sourceRoot, "keep.txt"), "archived");
            await WriteTextFileAsync(
                Path.Combine(destinationRoot, "obsolete.txt"),
                "new historical occurrence");
            await WriteTextFileAsync(
                Path.Combine(
                    destinationRoot,
                    ".yabt-hist",
                    existingTimestampSegment,
                    "obsolete.txt"),
                "existing historical occurrence");

            var timeProvider = new FixedTimeProvider(
                new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero));
            using var serviceProvider = CreateServices(timeProvider).BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
            await synchronizer.BackupAsync(new SyncRunRequest(sourceRoot));

            var result = await synchronizer.RestoreAsync(new SyncRunRequest
            (
                sourceRoot,
                DestinationRoot: destinationRoot
            ));

            Assert.IsTrue(result.Completed);
            Assert.AreEqual(1, result.ExtraCount);
            AssertTextFile(
                Path.Combine(
                    destinationRoot,
                    ".yabt-hist",
                    existingTimestampSegment,
                    "obsolete.txt"),
                "existing historical occurrence");
            AssertTextFile(
                Path.Combine(
                    destinationRoot,
                    ".yabt-hist",
                    $"{timestampSegment}-1",
                    "obsolete.txt"),
                "new historical occurrence");
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task RestoreAsyncDoesNotMergeOccurrenceIntoPriorHistoricalFolder()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var archiveRoot = Path.Combine(workspace, "archive");
            var destinationRoot = Path.Combine(workspace, "restored");
            const string timestampSegment = "20260821T120000Z";
            await InitializeSourceRootAsync(sourceRoot, archiveRoot);
            await WriteTextFileAsync(
                Path.Combine(sourceRoot, "folder", "keep.txt"),
                "same content");
            await WriteTextFileAsync(
                Path.Combine(destinationRoot, "folder", "keep.txt"),
                "same content");
            await WriteTextFileAsync(
                Path.Combine(destinationRoot, "folder", "extra.txt"),
                "new historical occurrence");
            await WriteTextFileAsync(
                Path.Combine(
                    destinationRoot,
                    ".yabt-hist",
                    timestampSegment,
                    "folder",
                    "original.txt"),
                "existing historical occurrence");

            var timeProvider = new FixedTimeProvider(
                new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero));
            using var serviceProvider = CreateServices(timeProvider).BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
            await synchronizer.BackupAsync(new SyncRunRequest(sourceRoot));

            var result = await synchronizer.RestoreAsync(new SyncRunRequest
            (
                sourceRoot,
                DestinationRoot: destinationRoot
            ));

            Assert.IsTrue(result.Completed);
            Assert.AreEqual(1, result.ExtraCount);
            Assert.IsFalse(File.Exists(Path.Combine(
                destinationRoot,
                ".yabt-hist",
                timestampSegment,
                "folder",
                "extra.txt")));
            AssertTextFile(
                Path.Combine(
                    destinationRoot,
                    ".yabt-hist",
                    timestampSegment,
                    "folder",
                    "original.txt"),
                "existing historical occurrence");
            AssertTextFile(
                Path.Combine(
                    destinationRoot,
                    ".yabt-hist",
                    $"{timestampSegment}-1",
                    "folder",
                    "extra.txt"),
                "new historical occurrence");
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task RestoreAsyncDryRunPlansHistorizationWithoutChangingDestination()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var archiveRoot = Path.Combine(workspace, "archive");
            var destinationRoot = Path.Combine(workspace, "restored");
            await InitializeSourceRootAsync(sourceRoot, archiveRoot);
            await WriteTextFileAsync(Path.Combine(sourceRoot, "file.txt"), "archive content");
            await WriteTextFileAsync(
                Path.Combine(destinationRoot, "file.txt"),
                "destination content");
            await WriteTextFileAsync(Path.Combine(destinationRoot, "extra.txt"), "extra");

            using var serviceProvider = CreateServices().BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
            await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));

            var result = await synchronizer.RestoreAsync(new SyncRunRequest
            (
                sourceRoot,
                DryRun: true,
                DestinationRoot: destinationRoot
            ));

            Assert.IsTrue(result.Completed);
            Assert.AreEqual(1, result.ChangedCount);
            Assert.AreEqual(1, result.ExtraCount);
            AssertTextFile(
                Path.Combine(destinationRoot, "file.txt"),
                "destination content");
            AssertTextFile(Path.Combine(destinationRoot, "extra.txt"), "extra");
            Assert.IsFalse(Directory.Exists(Path.Combine(destinationRoot, ".yabt-hist")));
            Assert.IsFalse(Directory.Exists(Path.Combine(destinationRoot, ".yabt-tmp")));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task RestoreAsyncCanReconcileConfiguredSourceRootAndPreservesDescriptor()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var archiveRoot = Path.Combine(workspace, "archive");
            await InitializeSourceRootAsync(sourceRoot, archiveRoot);
            await WriteTextFileAsync(Path.Combine(sourceRoot, "file.txt"), "archived content");

            using var serviceProvider = CreateServices().BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
            await synchronizer.BackupAsync(new SyncRunRequest(sourceRoot));
            await WriteTextFileAsync(Path.Combine(sourceRoot, "file.txt"), "newer content");
            await WriteTextFileAsync(Path.Combine(sourceRoot, "extra.txt"), "extra content");

            var result = await synchronizer.RestoreAsync(new SyncRunRequest
            (
                sourceRoot,
                DestinationRoot: sourceRoot
            ));

            Assert.IsTrue(result.Completed);
            Assert.AreEqual(1, result.ChangedCount);
            Assert.AreEqual(1, result.ExtraCount);
            AssertTextFile(Path.Combine(sourceRoot, "file.txt"), "archived content");
            Assert.IsTrue(File.Exists(Path.Combine(sourceRoot, BackupRootFileNames.Primary)));

            var historyRoot = Directory.GetDirectories(
                Path.Combine(sourceRoot, ".yabt-hist")).Single();
            AssertTextFile(Path.Combine(historyRoot, "file.txt"), "newer content");
            AssertTextFile(Path.Combine(historyRoot, "extra.txt"), "extra content");
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task RestoreAsyncRejectsDestinationNestedUnderConfiguredSourceRoot()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var archiveRoot = Path.Combine(workspace, "archive");
            var destinationRoot = Path.Combine(sourceRoot, "restore");
            await InitializeSourceRootAsync(sourceRoot, archiveRoot);
            await WriteTextFileAsync(Path.Combine(sourceRoot, "file.txt"), "content");

            using var serviceProvider = CreateServices().BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
            await synchronizer.BackupAsync(new SyncRunRequest(sourceRoot));

            var exception = await Assert.ThrowsExactlyAsync<YabtSyncException>(
                () => synchronizer.RestoreAsync(new SyncRunRequest
                (
                    sourceRoot,
                    DestinationRoot: destinationRoot
                )));

            StringAssert.Contains(exception.Message, "ancestors or descendants");
            StringAssert.Contains(exception.Message, "separate sibling folder");
            Assert.IsFalse(Directory.Exists(destinationRoot));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task RestoreAsyncRefusesSelectedFilesystemArchiveAsDestination()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var archiveRoot = Path.Combine(workspace, "archive");
            await InitializeSourceRootAsync(sourceRoot, archiveRoot);
            await WriteTextFileAsync(Path.Combine(sourceRoot, "file.txt"), "content");

            using var serviceProvider = CreateServices().BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
            await synchronizer.BackupAsync(new SyncRunRequest(sourceRoot));

            var exception = await Assert.ThrowsExactlyAsync<YabtSyncException>(
                () => synchronizer.RestoreAsync(new SyncRunRequest
                (
                    sourceRoot,
                    DestinationRoot: archiveRoot
                )));

            StringAssert.Contains(exception.Message, "selected filesystem archive");
            AssertTextFile(Path.Combine(archiveRoot, "file.txt"), "content");
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task RestoreAsyncRejectsExistingDestinationWithDifferentLayout()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var archiveRoot = Path.Combine(workspace, "archive");
            var destinationRoot = Path.Combine(workspace, "restored");
            var destinationArchiveRoot = Path.Combine(workspace, "destination-archive");
            var destinationLayout = new ArchiveLayout(
                LivePrefix: "live",
                HistPrefix: "history");
            await InitializeSourceRootAsync(sourceRoot, archiveRoot);
            await InitializeSourceRootAsync(
                destinationRoot,
                [CreateFileSystemStore("destination", destinationArchiveRoot)],
                layout: destinationLayout);
            await WriteTextFileAsync(Path.Combine(sourceRoot, "file.txt"), "archived");
            await WriteTextFileAsync(
                Path.Combine(destinationRoot, "live", "file.txt"),
                "changed");
            await WriteTextFileAsync(
                Path.Combine(destinationRoot, "history", "older.txt"),
                "older history");

            var timeProvider = new FixedTimeProvider(
                new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero));
            using var serviceProvider = CreateServices(timeProvider).BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
            await synchronizer.BackupAsync(new SyncRunRequest(sourceRoot));

            var exception = await Assert.ThrowsExactlyAsync<YabtSyncException>(
                () => synchronizer.RestoreAsync(new SyncRunRequest
                (
                    sourceRoot,
                    DestinationRoot: destinationRoot
                )));

            StringAssert.Contains(exception.Message, "different archive id or layout");
            AssertTextFile(
                Path.Combine(destinationRoot, "live", "file.txt"),
                "changed");
            Assert.IsFalse(File.Exists(Path.Combine(destinationRoot, "file.txt")));
            Assert.IsTrue(File.Exists(Path.Combine(
                destinationRoot,
                BackupRootFileNames.Primary)));
            AssertTextFile(
                Path.Combine(destinationRoot, "history", "older.txt"),
                "older history");
            Assert.AreEqual(
                0,
                Directory.GetDirectories(Path.Combine(destinationRoot, "history")).Length);
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task RestoreAsyncRemovesStaleDestinationHistoryCatalogAfterHistorization()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var archiveRoot = Path.Combine(workspace, "archive");
            var destinationRoot = Path.Combine(workspace, "restored");
            await InitializeSourceRootAsync(sourceRoot, archiveRoot);
            await WriteTextFileAsync(Path.Combine(sourceRoot, "file.txt"), "archived");
            await WriteTextFileAsync(Path.Combine(destinationRoot, "file.txt"), "changed");
            var historyRoot = Path.Combine(destinationRoot, ".yabt-hist");
            var historyManifestPath = Path.Combine(
                historyRoot,
                ArchiveHistoryFileNames.Manifest);
            await WriteTextFileAsync(historyManifestPath, "stale catalog");

            using var serviceProvider = CreateServices().BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
            await synchronizer.BackupAsync(new SyncRunRequest(sourceRoot));

            var result = await synchronizer.RestoreAsync(new SyncRunRequest
            (
                sourceRoot,
                DestinationRoot: destinationRoot
            ));

            Assert.IsTrue(result.Completed);
            Assert.IsFalse(File.Exists(historyManifestPath));
            Assert.IsFalse(File.Exists(Path.Combine(
                historyRoot,
                ArchiveHistoryManifest.InvalidationMarkerFileName)));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task RestoreAsyncRecoversStaleDestinationHistoryCatalogWithoutNewHistoryMoves()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var archiveRoot = Path.Combine(workspace, "archive");
            var destinationRoot = Path.Combine(workspace, "restored");
            var historyRoot = Path.Combine(destinationRoot, ".yabt-hist");
            var historyManifestPath = Path.Combine(
                historyRoot,
                ArchiveHistoryFileNames.Manifest);
            var invalidationMarkerPath = Path.Combine(
                historyRoot,
                ArchiveHistoryManifest.InvalidationMarkerFileName);
            await InitializeSourceRootAsync(sourceRoot, archiveRoot);
            await WriteTextFileAsync(Path.Combine(sourceRoot, "file.txt"), "archived");
            await WriteTextFileAsync(historyManifestPath, "stale catalog");
            await WriteTextFileAsync(invalidationMarkerPath, "stale marker");
            await WriteTextFileAsync(
                Path.Combine(historyRoot, "older.txt"),
                "older history");

            using var serviceProvider = CreateServices().BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
            await synchronizer.BackupAsync(new SyncRunRequest(sourceRoot));

            var result = await synchronizer.RestoreAsync(new SyncRunRequest
            (
                sourceRoot,
                DestinationRoot: destinationRoot
            ));

            Assert.IsTrue(result.Completed);
            Assert.AreEqual(0, result.ChangedCount);
            Assert.AreEqual(0, result.ExtraCount);
            Assert.IsFalse(File.Exists(historyManifestPath));
            Assert.IsFalse(File.Exists(invalidationMarkerPath));
            AssertTextFile(Path.Combine(historyRoot, "older.txt"), "older history");
            AssertTextFile(Path.Combine(destinationRoot, "file.txt"), "archived");
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task RestoreAsyncInvalidatesStaleDestinationLiveChangeManifests()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var archiveRoot = Path.Combine(workspace, "archive");
            var destinationRoot = Path.Combine(workspace, "restored");
            var uncompressedManifestPath = Path.Combine(
                destinationRoot,
                ArchiveChangeManifest.UncompressedFileName);
            var compressedManifestPath = Path.Combine(
                destinationRoot,
                ArchiveChangeManifest.BrotliFileName);
            var invalidationMarkerPath = Path.Combine(
                destinationRoot,
                ArchiveChangeManifest.InvalidationMarkerFileName);
            await InitializeSourceRootAsync(sourceRoot, archiveRoot);
            await WriteTextFileAsync(Path.Combine(sourceRoot, "file.txt"), "archived");
            await WriteTextFileAsync(
                Path.Combine(destinationRoot, "file.txt"),
                "changed");
            await WriteTextFileAsync(uncompressedManifestPath, "stale uncompressed");
            await WriteTextFileAsync(compressedManifestPath, "stale compressed");

            using var serviceProvider = CreateServices().BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
            await synchronizer.BackupAsync(new SyncRunRequest(sourceRoot));

            var result = await synchronizer.RestoreAsync(new SyncRunRequest
            (
                sourceRoot,
                DestinationRoot: destinationRoot
            ));

            Assert.IsTrue(result.Completed);
            AssertTextFile(Path.Combine(destinationRoot, "file.txt"), "archived");
            Assert.IsFalse(File.Exists(uncompressedManifestPath));
            Assert.IsFalse(File.Exists(compressedManifestPath));
            Assert.IsFalse(File.Exists(invalidationMarkerPath));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task RestoreAsyncAllowsHistoryNamedFolderInsideSeparateLivePrefix()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var archiveRoot = Path.Combine(workspace, "archive");
            var destinationRoot = Path.Combine(workspace, "restored");
            var layout = new ArchiveLayout(
                LivePrefix: "live",
                HistPrefix: ".yabt-hist");
            await InitializeSourceRootAsync(
                sourceRoot,
                [CreateFileSystemStore("target", archiveRoot)],
                layout: layout);
            await WriteTextFileAsync(
                Path.Combine(sourceRoot, "live", ".yabt-hist", "file.txt"),
                "ordinary live content");

            using var serviceProvider = CreateServices().BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
            await synchronizer.BackupAsync(new SyncRunRequest(sourceRoot));

            var result = await synchronizer.RestoreAsync(new SyncRunRequest
            (
                sourceRoot,
                DestinationRoot: destinationRoot
            ));

            Assert.IsTrue(result.Completed);
            AssertTextFile(
                Path.Combine(destinationRoot, "live", ".yabt-hist", "file.txt"),
                "ordinary live content");
            Assert.IsFalse(Directory.Exists(Path.Combine(destinationRoot, ".yabt-hist")));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task RestoreAsyncRejectsDestinationHistoryPrefixThatIsAFile()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var archiveRoot = Path.Combine(workspace, "archive");
            var destinationRoot = Path.Combine(workspace, "restored");
            await InitializeSourceRootAsync(sourceRoot, archiveRoot);
            await WriteTextFileAsync(Path.Combine(sourceRoot, "file.txt"), "content");
            await WriteTextFileAsync(
                Path.Combine(destinationRoot, ".yabt-hist"),
                "not a history folder");

            using var serviceProvider = CreateServices().BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
            await synchronizer.BackupAsync(new SyncRunRequest(sourceRoot));

            var exception = await Assert.ThrowsExactlyAsync<YabtSyncException>(
                () => synchronizer.RestoreAsync(new SyncRunRequest
                (
                    sourceRoot,
                    DestinationRoot: destinationRoot
                )));

            StringAssert.Contains(exception.Message, "must be a folder");
            AssertTextFile(
                Path.Combine(destinationRoot, ".yabt-hist"),
                "not a history folder");
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task RestoreAsyncRejectsDestinationLivePrefixThatIsAFile()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var archiveRoot = Path.Combine(workspace, "archive");
            var destinationRoot = Path.Combine(workspace, "restored");
            var destinationLayout = new ArchiveLayout(
                LivePrefix: "live",
                HistPrefix: "history");
            await InitializeSourceRootAsync(
                sourceRoot,
                [CreateFileSystemStore("target", archiveRoot)],
                layout: destinationLayout);
            await WriteTextFileAsync(Path.Combine(sourceRoot, "file.txt"), "content");
            await WriteTextFileAsync(
                Path.Combine(destinationRoot, "live"),
                "not a live folder");

            using var serviceProvider = CreateServices().BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
            await synchronizer.BackupAsync(new SyncRunRequest(sourceRoot));

            var exception = await Assert.ThrowsExactlyAsync<YabtSyncException>(
                () => synchronizer.RestoreAsync(new SyncRunRequest
                (
                    sourceRoot,
                    DestinationRoot: destinationRoot
                )));

            StringAssert.Contains(exception.Message, "must be a folder");
            AssertTextFile(
                Path.Combine(destinationRoot, "live"),
                "not a live folder");
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task RestoreAsyncRejectsFileAtDestinationLivePrefixAncestor()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var archiveRoot = Path.Combine(workspace, "archive");
            var destinationRoot = Path.Combine(workspace, "restored");
            var destinationLayout = new ArchiveLayout(
                LivePrefix: "branches/live",
                HistPrefix: "branches/history");
            await InitializeSourceRootAsync(
                sourceRoot,
                [CreateFileSystemStore("target", archiveRoot)],
                layout: destinationLayout);
            await WriteTextFileAsync(Path.Combine(sourceRoot, "file.txt"), "content");
            await WriteTextFileAsync(
                Path.Combine(destinationRoot, "branches"),
                "not a layout folder");

            using var serviceProvider = CreateServices().BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
            await synchronizer.BackupAsync(new SyncRunRequest(sourceRoot));

            var exception = await Assert.ThrowsExactlyAsync<YabtSyncException>(
                () => synchronizer.RestoreAsync(new SyncRunRequest
                (
                    sourceRoot,
                    DestinationRoot: destinationRoot
                )));

            StringAssert.Contains(exception.Message, "must be a folder");
            AssertTextFile(
                Path.Combine(destinationRoot, "branches"),
                "not a layout folder");
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task BackupAsyncRejectsLiveFileAtHistoryPrefixAncestor()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var archiveRoot = Path.Combine(workspace, "archive");
            var layout = new ArchiveLayout(HistPrefix: "meta/history");
            await InitializeSourceRootAsync(
                sourceRoot,
                [CreateFileSystemStore("target", archiveRoot)],
                layout: layout);
            await WriteTextFileAsync(Path.Combine(sourceRoot, "meta"), "content");

            using var serviceProvider = CreateServices().BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
            var exception = await Assert.ThrowsExactlyAsync<YabtSyncException>(
                () => synchronizer.BackupAsync(new SyncRunRequest(sourceRoot)));

            StringAssert.Contains(exception.Message, "conflicts with archive history");
            Assert.IsFalse(File.Exists(Path.Combine(archiveRoot, "meta")));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task RestoreAsyncRejectsArchiveObjectThatFailsManifestHash()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var archiveRoot = Path.Combine(workspace, "archive");
            var destinationRoot = Path.Combine(workspace, "restored");
            await InitializeSourceRootAsync(sourceRoot, archiveRoot);
            await WriteTextFileAsync(Path.Combine(sourceRoot, "file.txt"), "original");
            await WriteTextFileAsync(
                Path.Combine(destinationRoot, "file.txt"),
                "destination content");
            await WriteTextFileAsync(
                Path.Combine(destinationRoot, "extra.txt"),
                "extra content");
            var destinationManifestPath = Path.Combine(
                destinationRoot,
                ArchiveChangeManifest.UncompressedFileName);
            await WriteTextFileAsync(destinationManifestPath, "destination manifest");

            using var serviceProvider = CreateServices().BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
            await synchronizer.BackupAsync(new SyncRunRequest(sourceRoot));
            await File.WriteAllTextAsync(Path.Combine(archiveRoot, "file.txt"), "tampered");

            var exception = await Assert.ThrowsExactlyAsync<YabtSyncException>(
                () => synchronizer.RestoreAsync(new SyncRunRequest
                (
                    sourceRoot,
                    DestinationRoot: destinationRoot
                )));

            StringAssert.Contains(exception.Message, "failed its content hash check");
            AssertTextFile(
                Path.Combine(destinationRoot, "file.txt"),
                "destination content");
            AssertTextFile(
                Path.Combine(destinationRoot, "extra.txt"),
                "extra content");
            AssertTextFile(destinationManifestPath, "destination manifest");
            Assert.IsFalse(Directory.Exists(Path.Combine(destinationRoot, ".yabt-hist")));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task RestoreAsyncStagesEveryWriteBeforeMutatingDestination()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var archiveRoot = Path.Combine(workspace, "archive");
            var destinationRoot = Path.Combine(workspace, "restored");
            await InitializeSourceRootAsync(sourceRoot, archiveRoot);
            await WriteTextFileAsync(Path.Combine(sourceRoot, "a.txt"), "archive a");
            await WriteTextFileAsync(Path.Combine(sourceRoot, "z.txt"), "archive z");
            await WriteTextFileAsync(
                Path.Combine(destinationRoot, "a.txt"),
                "destination a");
            await WriteTextFileAsync(
                Path.Combine(destinationRoot, "z.txt"),
                "destination z");
            await WriteTextFileAsync(
                Path.Combine(destinationRoot, "extra.txt"),
                "extra content");
            var destinationManifestPath = Path.Combine(
                destinationRoot,
                ArchiveChangeManifest.UncompressedFileName);
            await WriteTextFileAsync(destinationManifestPath, "destination manifest");

            using var serviceProvider = CreateServices().BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
            await synchronizer.BackupAsync(new SyncRunRequest(sourceRoot));
            await File.WriteAllTextAsync(Path.Combine(archiveRoot, "z.txt"), "tampered z");

            var exception = await Assert.ThrowsExactlyAsync<YabtSyncException>(
                () => synchronizer.RestoreAsync(new SyncRunRequest
                (
                    sourceRoot,
                    DestinationRoot: destinationRoot
                )));

            StringAssert.Contains(exception.Message, "failed its content hash check");
            AssertTextFile(Path.Combine(destinationRoot, "a.txt"), "destination a");
            AssertTextFile(Path.Combine(destinationRoot, "z.txt"), "destination z");
            AssertTextFile(
                Path.Combine(destinationRoot, "extra.txt"),
                "extra content");
            AssertTextFile(destinationManifestPath, "destination manifest");
            Assert.IsFalse(Directory.Exists(Path.Combine(destinationRoot, ".yabt-hist")));
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
            Assert.AreEqual(3, result.NewCount);
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
                Assert.AreEqual(2, firstResult.NewCount);
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
                Assert.AreEqual(2, secondResult.UnchangedCount);
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
    public async Task BackupAsyncKeepsZipArtifactsInSameGenerationWhenManifestIsMissing()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var targetRoot = Path.Combine(workspace, "target");
            var sourceFilePath = Path.Combine(sourceRoot, "file.txt");
            var preservedLastWriteTimeUtc = new DateTime
            (
                2026,
                8,
                24,
                12,
                0,
                0,
                DateTimeKind.Utc
            );
            await InitializeSourceRootAsync(sourceRoot, targetRoot);
            await WritePolicyAsync(sourceRoot, ZipArchiveFormatName.Value);
            await WriteTextFileAsync(sourceFilePath, "first");
            File.SetLastWriteTimeUtc(sourceFilePath, preservedLastWriteTimeUtc);

            using var serviceProvider = CreateServices().BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
            var firstResult = await synchronizer.BackupAsync(new SyncRunRequest(sourceRoot));
            Assert.IsTrue(firstResult.Completed);

            var packagePath = Directory.GetFiles(targetRoot, "*.zip").Single();
            var manifestPath =
                $"{packagePath}{ArchivePackageManifestFileNames.AdjacentSuffix}";
            File.Delete(manifestPath);

            await WriteTextFileAsync(sourceFilePath, "other");
            File.SetLastWriteTimeUtc(sourceFilePath, preservedLastWriteTimeUtc);

            var secondResult = await synchronizer.BackupAsync(new SyncRunRequest(sourceRoot));
            Assert.IsTrue(secondResult.Completed);
            Assert.IsTrue(File.Exists(manifestPath));

            var adjacentManifestBytes = await File.ReadAllBytesAsync(manifestPath);
            await using (var packageContent = File.OpenRead(packagePath))
            using (var archive = new ZipArchive(packageContent, ZipArchiveMode.Read))
            {
                var embeddedManifest = archive.GetEntry(
                    ArchivePackageManifestFileNames.EmbeddedEntryName);
                Assert.IsNotNull(embeddedManifest);
                await using var embeddedManifestContent = embeddedManifest.Open();
                using var embeddedManifestBuffer = new MemoryStream();
                await embeddedManifestContent.CopyToAsync(embeddedManifestBuffer);
                CollectionAssert.AreEqual(
                    adjacentManifestBytes,
                    embeddedManifestBuffer.ToArray());

                var packagedFile = archive.GetEntry("file.txt");
                Assert.IsNotNull(packagedFile);
                using var reader = new StreamReader(packagedFile.Open(), Encoding.UTF8);
                Assert.AreEqual("other", await reader.ReadToEndAsync());
            }

            var destinationRoot = Path.Combine(workspace, "restore");
            var restoreResult = await synchronizer.RestoreAsync(new SyncRunRequest
            (
                sourceRoot,
                DestinationRoot: destinationRoot
            ));
            Assert.IsTrue(restoreResult.Completed);
            AssertTextFile(Path.Combine(destinationRoot, "file.txt"), "other");
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
            Assert.AreEqual(2, secondResult.NewCount);
            Assert.AreEqual(0, secondResult.ChangedCount);
            Assert.AreEqual(2, secondResult.ExtraCount);
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
            Assert.AreEqual(2, zipResult.NewCount);
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
            Assert.AreEqual(2, zipResult.NewCount);
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
            Assert.AreEqual(2, zipResult.NewCount);
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
            Assert.AreEqual(2, verifyResult.UnchangedCount);
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
            Assert.AreEqual(2, mirrorResult.ExtraCount);
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
                    "20260724T120000Z-2",
                },
                historicalRootNames);
            CollectionAssert.AreEquivalent
            (
                new[]
                {
                    "20260724T120000Z",
                    "20260724T120000Z-1",
                    "20260724T120000Z-2",
                },
                Directory.GetDirectories(Path.Combine(targetRoot, ".yabt-hist"))
                    .Select(Path.GetFileName)
                    .ToArray()
            );
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
        Assert.AreEqual(2, firstResult.NewCount);
        Assert.IsTrue(secondResult.Completed);
        Assert.AreEqual(0, secondResult.NewCount);
        Assert.AreEqual(0, secondResult.ChangedCount);
        Assert.AreEqual(0, secondResult.ExtraCount);
        Assert.AreEqual(2, secondResult.UnchangedCount);
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
        Assert.AreEqual(2, manifest.Entries.Count());
        var manifestEntry = manifest.Entries.Single(entry => entry.RelativePath.EndsWith(
            ".zip",
            StringComparison.Ordinal));
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
        Assert.AreEqual(2, secondSyncResult.UnchangedCount);
        Assert.IsTrue(verifyResult.Completed);
        Assert.AreEqual(2, verifyResult.UnchangedCount);

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
        Assert.IsTrue(currentManifest.Entries.Single(entry => string.Equals(
            entry.RelativePath,
            manifestEntry.RelativePath,
            StringComparison.Ordinal)).ArtifactLength > 1);

        var truncatedResult = await synchronizer.VerifyAsync(new SyncRunRequest(sourceRoot));

        Assert.IsFalse(truncatedResult.Completed);
        Assert.AreEqual(1, truncatedResult.ChangedCount);
        Assert.AreEqual(1, truncatedResult.UnchangedCount);
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
    public async Task BackupAsyncUpgradesLegacyChangeManifestWithoutDataChanges()
    {
        var sourceRoot = Path.Combine(
            Path.GetTempPath(),
            $"yabt-legacy-manifest-upgrade-test-{Guid.NewGuid():N}");
        var sourceStore = new MemoryObjectStore();
        var targetStore = new MemoryObjectStore();
        await UploadTextObjectAsync(sourceStore, "file.txt", "source content");
        var descriptor = CreateRootDescriptor(
            [new BackupRootStore("target", FixedBackupRootStoreResolver.StoreKindValue)]);

        using var serviceProvider = CreateStreamingServices(
            sourceRoot,
            descriptor,
            sourceStore,
            targetStore).BuildServiceProvider();
        var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
        var serializer = serviceProvider.GetRequiredService<IChangeManifestSerializer>();
        await synchronizer.BackupAsync(new SyncRunRequest(sourceRoot));
        Assert.IsTrue(targetStore.TryGetObject(
            ArchiveChangeManifest.BrotliFileName,
            out var currentManifestObject));
        await using var currentManifestContent = new MemoryStream(
            currentManifestObject.Content.ToArray(),
            writable: false);
        var currentManifest = await ReadChangeManifestAsync(
            serializer,
            currentManifestContent,
            ArchiveChangeManifestCompression.Brotli);
        var legacyManifest = CreateLegacyChangeManifest(currentManifest.Entries);
        await targetStore.MoveAsync(
            ArchiveChangeManifest.BrotliFileName,
            $"{ArchiveInternalFolderNames.TemporaryUploads}/schema-v2-manifest.json.br");
        await UploadLegacyChangeManifestAsync(
            targetStore,
            legacyManifest,
            ArchiveChangeManifestCompression.Brotli);

        var result = await synchronizer.BackupAsync(new SyncRunRequest(sourceRoot));

        Assert.IsTrue(result.Completed);
        Assert.AreEqual(1, result.UnchangedCount);
        Assert.IsTrue(targetStore.TryGetObject(
            ArchiveChangeManifest.BrotliFileName,
            out var upgradedManifestObject));
        await using var upgradedManifestContent = new MemoryStream(
            upgradedManifestObject.Content.ToArray(),
            writable: false);
        var upgradedManifest = await ReadChangeManifestAsync(
            serializer,
            upgradedManifestContent,
            ArchiveChangeManifestCompression.Brotli);
        Assert.AreEqual(
            ArchiveChangeManifest.ExpectedSchemaVersion,
            upgradedManifest.SchemaVersion);
        Assert.AreEqual(MirrorArchiveFormatName.Value, upgradedManifest.RootFormat);
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
        var conflictingManifest = serializer.Create([], MirrorArchiveFormatName.Value);
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
            serializer.Create([], MirrorArchiveFormatName.Value),
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
        var incorrectManifest = serializer.Create
        (
            manifest.Entries.Select(entry => entry with
            {
                ArtifactLength = entry.ArtifactLength + 1,
            }),
            ZipArchiveFormatName.Value
        );

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
        Assert.AreEqual(2, verifyResult.UnchangedCount);
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
    public async Task BackupAsyncRejectsLayoutsOverlappingReservedRootObjectPaths()
    {
        var reservedRootObjectPaths = new[]
        {
            BackupRootFileNames.Primary,
            ArchiveChangeManifest.BrotliFileName,
            ArchiveChangeManifest.UncompressedFileName,
            ArchiveChangeManifest.InvalidationMarkerFileName,
        };

        foreach (var reservedRootObjectPath in reservedRootObjectPaths)
        {
            var layouts = new[]
            {
                new ArchiveLayout(reservedRootObjectPath, "hist"),
                new ArchiveLayout("live", reservedRootObjectPath),
                new ArchiveLayout("live", $"{reservedRootObjectPath}/history"),
            };
            foreach (var layout in layouts)
            {
                var sourceRoot = Path.Combine(
                    Path.GetTempPath(),
                    $"yabt-reserved-root-layout-test-{Guid.NewGuid():N}");
                var sourceStore = new MemoryObjectStore();
                var targetStore = new MemoryObjectStore();
                var descriptor = new BackupRootDescriptor
                (
                    BackupRootDescriptor.ExpectedDocumentType,
                    BackupRootDescriptor.ExpectedSchemaVersion,
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
                    () => synchronizer.BackupAsync(new SyncRunRequest(sourceRoot)));

                StringAssert.Contains(exception.Message, reservedRootObjectPath);
            }
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

    private static ServiceCollection CreateServices
    (
        TimeProvider? timeProvider = default,
        CapturingLogSink? loggerSink = default
    )
    {
        var services = new ServiceCollection();
        if (loggerSink is null)
        {
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        }
        else
        {
            services.AddSingleton(loggerSink);
            services.AddSingleton(typeof(ILogger<>), typeof(CapturingLogger<>));
        }
        if (timeProvider is not null)
        {
            services.AddSingleton(timeProvider);
        }

        services.AddYabtFileSystemObjectStore();
        services.AddYabtMirrorFormatHandler();
        services.AddYabtZipFormatHandler();
        services.AddYabtMetadata();
        services.AddYabtSync();

        return services;
    }

    private sealed class CapturingLogSink
    {
        public ConcurrentQueue<CapturedLogEntry> Entries { get; } = new();

        public void Clear()
        {
            while (Entries.TryDequeue(out _))
            {
            }
        }
    }

    private sealed class CapturingLogger<T>(CapturingLogSink sink) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>
        (
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => sink.Entries.Enqueue(new(
            logLevel,
            eventId,
            formatter(state, exception)));
    }

    private sealed record CapturedLogEntry
    (
        LogLevel Level,
        EventId EventId,
        string Message
    );

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }

    private static ServiceCollection CreateCustomFormatServices
    (
        TestBidirectionalArchiveFormatHandler formatHandler
    )
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddYabtFileSystemObjectStore();
        services.AddSingleton<IArchiveFormatHandler>(formatHandler);
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
        services.AddYabtMirrorFormatHandler();
        services.AddYabtZipFormatHandler();
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
        string? defaultStoreId = default,
        ArchiveLayout? layout = default
    )
    {
        Directory.CreateDirectory(sourceRoot);

        var descriptor = CreateRootDescriptor(
            stores,
            defaultStoreId,
            layout: layout);
        await using var stream = File.Create(Path.Combine(sourceRoot, BackupRootFileNames.Primary));
        await JsonSerializer.SerializeAsync(
            stream,
            descriptor,
            JsonOptions);
    }

    private static byte[] CreateExactRootDescriptorBytes(BackupRootDescriptor descriptor)
    {
        var serializedDescriptor = JsonSerializer.SerializeToUtf8Bytes(
            descriptor,
            JsonOptions);
        var utf8WithBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        return
        [
            .. utf8WithBom.GetPreamble(),
            .. serializedDescriptor,
            .. Encoding.UTF8.GetBytes("\r\n\r\n"),
        ];
    }

    private static async Task WriteRootDescriptorBytesAsync
    (
        string rootPath,
        byte[] descriptorBytes
    )
    {
        Directory.CreateDirectory(rootPath);
        await File.WriteAllBytesAsync(
            Path.Combine(rootPath, BackupRootFileNames.Primary),
            descriptorBytes);
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
        string? changeManifestCompression = default,
        ArchiveLayout? layout = default
    )
    {
        return new
        (
            BackupRootDescriptor.ExpectedDocumentType,
            1,
            "source-archive",
            new DateTimeOffset(2026, 5, 31, 0, 0, 0, TimeSpan.Zero),
            layout ?? ArchiveLayout.Default,
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

    private static ArchiveChangeManifest CreateLegacyChangeManifest
    (
        IEnumerable<ArchiveChangeManifestEntry> entries
    )
    {
        var canonicalEntries = entries
            .OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)
            .ToArray();
        using var canonicalJson = new MemoryStream();
        using (var writer = new Utf8JsonWriter(canonicalJson))
        {
            writer.WriteStartObject();
            writer.WriteString("documentType", ArchiveChangeManifest.ExpectedDocumentType);
            writer.WriteNumber("schemaVersion", ArchiveChangeManifest.LegacySchemaVersion);
            writer.WriteStartArray("entries");
            foreach (var entry in canonicalEntries)
            {
                writer.WriteStartObject();
                writer.WriteString("relativePath", entry.RelativePath);
                writer.WriteString("changeFingerprint", entry.ChangeFingerprint);
                if (entry.ArtifactLength.HasValue)
                {
                    writer.WriteNumber("artifactLength", entry.ArtifactLength.Value);
                }

                if (entry.ContentHash is not null)
                {
                    writer.WriteString("contentHash", entry.ContentHash);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        var canonicalBytes = canonicalJson.GetBuffer().AsSpan(
            0,
            checked((int)canonicalJson.Length));
        return new
        (
            ArchiveChangeManifest.ExpectedDocumentType,
            ArchiveChangeManifest.LegacySchemaVersion,
            canonicalEntries,
            ArchiveHash.Compute(canonicalBytes)
        );
    }

    private static async Task UploadLegacyChangeManifestAsync
    (
        MemoryObjectStore targetStore,
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
            await using var compressedContent = new BrotliStream(
                content,
                CompressionLevel.Optimal,
                leaveOpen: true);
            await JsonSerializer.SerializeAsync(
                compressedContent,
                manifest,
                JsonOptions);
        }
        else if (string.Equals(
                     compression,
                     ArchiveChangeManifestCompression.None,
                     StringComparison.Ordinal))
        {
            await JsonSerializer.SerializeAsync(
                content,
                manifest,
                JsonOptions);
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

    private static void AssertPolicyFormat(string path, string expectedFormat)
    {
        var policy = JsonSerializer.Deserialize<FolderPolicy>(
            File.ReadAllText(path),
            JsonOptions);
        Assert.IsNotNull(policy);
        Assert.AreEqual(expectedFormat, policy.Format);
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

    private sealed class TestBidirectionalArchiveFormatHandler : IArchiveFormatHandler
    {
        public const string ArtifactFileName = "payload.yabt-test-bundle";
        public const string FormatNameValue = "test-bidirectional";
        public const string SourceFileName = "payload.bin";

        private int _backupProjectionCount;
        private int _restoreProjectionCount;

        public int BackupProjectionCount => Volatile.Read(ref _backupProjectionCount);

        public int RestoreProjectionCount => Volatile.Read(ref _restoreProjectionCount);

        public string FormatName => FormatNameValue;

        public bool ProjectsBesideSourceFolder => true;

        public bool CanRestoreArtifact(ArchiveProjectedObject artifact) =>
            string.Equals(
                ArchiveLayout.NormalizeObjectKey(artifact.RelativePath),
                ArtifactFileName,
                StringComparison.Ordinal);

        public async IAsyncEnumerable<ArchiveProjectedObject> ProjectBackupAsync
        (
            ArchiveProjectionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            ArgumentNullException.ThrowIfNull(request);
            Interlocked.Increment(ref _backupProjectionCount);

            var sourceKey = ArchiveLayout.CombinePrefixAndRelativePath(
                request.SourcePrefix,
                SourceFileName);
            await using var sourceContent = await request.SourceStore.OpenReadAsync(
                sourceKey,
                cancellationToken);
            var sourceBytes = await ReadAllBytesAsync(
                sourceContent.Content,
                cancellationToken);
            var artifactBytes = Transform(sourceBytes);
            var artifactHash = ArchiveHash.Compute(artifactBytes);
            var projectionId = $"test-bidirectional-v1:{artifactHash}";

            yield return new
            (
                ArtifactFileName,
                currentCancellationToken =>
                {
                    currentCancellationToken.ThrowIfCancellationRequested();
                    return Task.FromResult(new ArchiveObjectContent(
                        new MemoryStream(artifactBytes, writable: false),
                        "application/x-yabt-test-bundle"));
                },
                artifactBytes.Length,
                ContentHash: artifactHash,
                ChangeFingerprint: projectionId,
                Projection: new ArchiveProjectionProvenance
                (
                    ArchiveLayout.NormalizeObjectKey(request.LogicalPath),
                    FormatNameValue,
                    1,
                    projectionId,
                    "bundle"
                )
            );
        }

        public async Task<ArchiveRestoreProjection> ProjectRestoreAsync
        (
            ArchiveRestoreRequest request,
            CancellationToken cancellationToken = default
        )
        {
            ArgumentNullException.ThrowIfNull(request);
            Interlocked.Increment(ref _restoreProjectionCount);
            if (!request.RestoreAsRoot || !CanRestoreArtifact(request.Artifact))
            {
                throw new InvalidDataException(
                    $"Artifact '{request.Artifact.RelativePath}' is not a root test bundle.");
            }

            await using var artifactContent = await request.Artifact.OpenContentAsync(
                cancellationToken);
            var artifactBytes = await ReadAllBytesAsync(
                artifactContent.Content,
                cancellationToken);
            var actualArtifactHash = ArchiveHash.Compute(artifactBytes);
            if (!string.Equals(
                    request.Artifact.ContentHash,
                    actualArtifactHash,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Artifact '{request.Artifact.RelativePath}' failed its content hash check.");
            }

            var restoredBytes = Transform(artifactBytes);
            var restoredHash = ArchiveHash.Compute(restoredBytes);
            return new ArchiveRestoreProjection
            (
                [
                    new ArchiveProjectedObject
                    (
                        SourceFileName,
                        currentCancellationToken =>
                        {
                            currentCancellationToken.ThrowIfCancellationRequested();
                            return Task.FromResult(new ArchiveObjectContent(
                                new MemoryStream(restoredBytes, writable: false)));
                        },
                        restoredBytes.Length,
                        ContentHash: restoredHash,
                        ChangeFingerprint: $"test-restored-v1:{restoredHash}"
                    ),
                ]
            );
        }

        private static async Task<byte[]> ReadAllBytesAsync
        (
            Stream content,
            CancellationToken cancellationToken
        )
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            return buffer.ToArray();
        }

        private static byte[] Transform(ReadOnlySpan<byte> content)
        {
            var transformed = new byte[content.Length];
            for (var index = 0; index < content.Length; index++)
            {
                transformed[index] = (byte)(content[index] ^ 0xA5);
            }

            return transformed;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset _utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
