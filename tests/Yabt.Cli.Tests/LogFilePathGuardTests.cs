using System.Text.Json;
using Yabt.Cli.Implementation;
using Yabt.Core.Models;
using Yabt.Metadata;

namespace Yabt.Cli.Tests;

[TestClass]
public sealed class LogFilePathGuardTests
{
    [TestMethod]
    public async Task EnsureOutsideOperationRootsRejectsLogInsideCommandRoot()
    {
        var commandRoot = GetTestRoot("source");
        var guard = CreateGuard(commandRoot, GetTestRoot("archive"));

        var exception = await Assert.ThrowsExactlyAsync<YabtCliException>(() =>
            guard.EnsureOutsideOperationRootsAsync(
                Path.Combine(commandRoot, "logs", "run.log"),
                YabtCliCommandNames.Backup,
                commandRoot,
                targetStoreId: null,
                destinationRoot: null,
                CancellationToken.None));

        StringAssert.Contains(exception.Message, "command root");
    }

    [TestMethod]
    public async Task EnsureOutsideOperationRootsRejectsLogInsideFilesystemArchive()
    {
        var commandRoot = GetTestRoot("source");
        var archiveRoot = GetTestRoot("archive");
        var guard = CreateGuard(commandRoot, archiveRoot);

        var exception = await Assert.ThrowsExactlyAsync<YabtCliException>(() =>
            guard.EnsureOutsideOperationRootsAsync(
                Path.Combine(archiveRoot, "run.log"),
                YabtCliCommandNames.Verify,
                commandRoot,
                targetStoreId: null,
                destinationRoot: null,
                CancellationToken.None));

        StringAssert.Contains(exception.Message, "filesystem archive store");
    }

    [TestMethod]
    public async Task EnsureOutsideOperationRootsRejectsLogInsideRestoreDestination()
    {
        var commandRoot = GetTestRoot("descriptor");
        var destinationRoot = GetTestRoot("restore");
        var guard = CreateGuard(commandRoot, GetTestRoot("archive"));

        var exception = await Assert.ThrowsExactlyAsync<YabtCliException>(() =>
            guard.EnsureOutsideOperationRootsAsync(
                Path.Combine(destinationRoot, "run.log"),
                YabtCliCommandNames.Restore,
                commandRoot,
                targetStoreId: null,
                destinationRoot,
                CancellationToken.None));

        StringAssert.Contains(exception.Message, "restore destination");
    }

    [TestMethod]
    public async Task EnsureOutsideOperationRootsRejectsLogThatWouldBlockAbsentRestoreDestination()
    {
        var commandRoot = GetTestRoot("descriptor");
        var logPath = GetTestRoot("restore-parent");
        var destinationRoot = Path.Combine(logPath, "output");
        var guard = CreateGuard(commandRoot, GetTestRoot("archive"));

        var exception = await Assert.ThrowsExactlyAsync<YabtCliException>(() =>
            guard.EnsureOutsideOperationRootsAsync(
                logPath,
                YabtCliCommandNames.Restore,
                commandRoot,
                targetStoreId: null,
                destinationRoot,
                CancellationToken.None));

        StringAssert.Contains(exception.Message, "restore destination");
    }

    [TestMethod]
    public async Task EnsureOutsideOperationRootsAllowsExternalLog()
    {
        var commandRoot = GetTestRoot("source");
        var archiveRoot = GetTestRoot("archive");
        var guard = CreateGuard(commandRoot, archiveRoot);

        await guard.EnsureOutsideOperationRootsAsync(
            Path.Combine(GetTestRoot("logs"), "run.log"),
            YabtCliCommandNames.Backup,
            commandRoot,
            targetStoreId: null,
            destinationRoot: null,
            CancellationToken.None);
    }

    [TestMethod]
    public async Task EnsureOutsideOperationRootsWrapsInvalidPathsForCliReporting()
    {
        var guard = CreateGuard(GetTestRoot("source"), GetTestRoot("archive"));

        var exception = await Assert.ThrowsExactlyAsync<YabtCliException>(() =>
            guard.EnsureOutsideOperationRootsAsync(
                Path.Combine(GetTestRoot("logs"), "run.log"),
                YabtCliCommandNames.Scan,
                " ",
                targetStoreId: null,
                destinationRoot: null,
                CancellationToken.None));

        StringAssert.Contains(exception.Message, "Could not safely validate");
        Assert.IsInstanceOfType<ArgumentException>(exception.InnerException);
    }

    private static LogFilePathGuard CreateGuard(string descriptorRoot, string archiveRoot)
    {
        var store = new BackupRootStore("archive", "fileSystem")
        {
            ProviderProperties = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["rootPath"] = JsonSerializer.SerializeToElement(archiveRoot),
            },
        };
        var descriptor = new BackupRootDescriptor
        (
            BackupRootDescriptor.ExpectedDocumentType,
            BackupRootDescriptor.ExpectedSchemaVersion,
            "archive-id",
            DateTimeOffset.UnixEpoch,
            ArchiveLayout.Default,
            [store]
        );
        return new(new StubBackupRootLocator(new(descriptorRoot, descriptor)));
    }

    private static string GetTestRoot(string name) => Path.GetFullPath(Path.Combine(
        Path.GetTempPath(),
        "yabt-cli-path-guard-tests",
        name));

    private sealed class StubBackupRootLocator(BackupRootLocation _location) : IBackupRootLocator
    {
        public Task<BackupRootLocation> LocateRootAsync
        (
            string startPath,
            CancellationToken cancellationToken = default
        )
        {
            _ = startPath;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_location);
        }
    }
}
