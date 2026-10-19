using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yabt.Core.Models;
using Yabt.Metadata;

namespace Yabt.Sync.Tests;

[TestClass]
public sealed class BackupRepresentationTransitionTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BackupAsyncReplacesFileWithDirectoryAndPreservesOldFile(bool dryRun)
    {
        using var fixture = new TransitionFixture();
        await fixture.ConfigureAsync();
        await fixture.WriteSourceAsync("reports", "old file content");
        await fixture.BackupSuccessfullyAsync();
        File.Delete(fixture.SourcePath("reports"));
        await fixture.WriteSourceAsync("reports/2026/annual.txt", "new report content");
        Directory.CreateDirectory(fixture.SourcePath("reports/empty"));
        var before = fixture.SnapshotArchive();

        var result = await fixture.BackupAsync(dryRun);

        Assert.IsTrue(result.Completed, result.Message);
        if (dryRun)
        {
            CollectionAssert.AreEqual(before, fixture.SnapshotArchive());
            return;
        }

        Assert.AreEqual("new report content", await fixture.ReadArchiveAsync("reports/2026/annual.txt"));
        Assert.IsTrue(File.Exists(fixture.ArchivePath("reports/empty/.yabt-empty")));
        Assert.AreEqual("old file content", await File.ReadAllTextAsync(fixture.HistoryPath("reports")));
        await fixture.AssertSuccessfulRerunWithoutHistoryChangesAsync();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BackupAsyncReplacesDirectoryWithFileAndPreservesCompleteFolder(bool dryRun)
    {
        using var fixture = new TransitionFixture();
        await fixture.ConfigureAsync();
        await fixture.WriteSourceAsync("reports/old.txt", "old folder content");
        Directory.CreateDirectory(fixture.SourcePath("reports/empty"));
        await fixture.BackupSuccessfullyAsync();
        Directory.CreateDirectory(fixture.ArchivePath("reports/native-empty/deeper"));
        fixture.DeleteSourceDirectory("reports");
        await fixture.WriteSourceAsync("reports", "new file content");
        var before = fixture.SnapshotArchive();

        var result = await fixture.BackupAsync(dryRun);

        Assert.IsTrue(result.Completed, result.Message);
        if (dryRun)
        {
            CollectionAssert.AreEqual(before, fixture.SnapshotArchive());
            return;
        }

        Assert.AreEqual("new file content", await fixture.ReadArchiveAsync("reports"));
        Assert.AreEqual
        (
            "old folder content",
            await File.ReadAllTextAsync(fixture.HistoryPath("reports/old.txt"))
        );
        Assert.IsTrue(File.Exists(fixture.HistoryPath("reports/empty/.yabt-empty")));
        Assert.IsTrue(Directory.Exists(fixture.HistoryPath("reports/native-empty/deeper")));
        await fixture.AssertSuccessfulRerunWithoutHistoryChangesAsync();
    }

    [TestMethod]
    public async Task BackupAsyncReplacesNativeEmptyDirectoryWithFile()
    {
        using var fixture = new TransitionFixture();
        await fixture.ConfigureAsync();
        Directory.CreateDirectory(fixture.SourcePath("reports"));
        await fixture.BackupSuccessfullyAsync();
        File.Delete(fixture.ArchivePath("reports/.yabt-empty"));
        fixture.DeleteSourceDirectory("reports");
        await fixture.WriteSourceAsync("reports", "new file content");

        var result = await fixture.BackupAsync();

        Assert.IsTrue(result.Completed, result.Message);
        Assert.AreEqual("new file content", await fixture.ReadArchiveAsync("reports"));
        Assert.IsTrue(Directory.Exists(fixture.HistoryPath("reports")));
        Assert.AreEqual(0, Directory.GetFileSystemEntries(fixture.HistoryPath("reports")).Length);
        await fixture.AssertSuccessfulRerunWithoutHistoryChangesAsync();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BackupAsyncReconcilesRepresentationsInsideExplicitLivePrefix(bool replacesDirectory)
    {
        const string livePrefix = "nested/live";
        using var fixture = new TransitionFixture();
        await fixture.ConfigureAsync(livePrefix);
        var previousRelativePath = replacesDirectory ? "reports/old.txt" : "reports";
        var replacementRelativePath = replacesDirectory ? "reports" : "reports/new.txt";
        var previousPath = $"{livePrefix}/{previousRelativePath}";
        var replacementPath = $"{livePrefix}/{replacementRelativePath}";
        await fixture.WriteSourceAsync(previousPath, "old archived content");
        await fixture.BackupSuccessfullyAsync();
        await File.WriteAllTextAsync(fixture.ArchivePath("outside-live.txt"), "outside live sentinel");
        if (replacesDirectory) { fixture.DeleteSourceDirectory($"{livePrefix}/reports"); }
        else { File.Delete(fixture.SourcePath(previousPath)); }
        await fixture.WriteSourceAsync(replacementPath, "replacement content");

        var result = await fixture.BackupAsync();

        Assert.IsTrue(result.Completed, result.Message);
        Assert.AreEqual("replacement content", await fixture.ReadArchiveAsync(replacementPath));
        Assert.AreEqual
        (
            "old archived content",
            await File.ReadAllTextAsync(fixture.HistoryPath(previousRelativePath))
        );
        Assert.AreEqual("outside live sentinel", await fixture.ReadArchiveAsync("outside-live.txt"));
        Assert.IsTrue(File.Exists(fixture.ArchivePath(BackupRootFileNames.Primary)));
        Assert.IsTrue(File.Exists(fixture.ArchivePath(ArchiveChangeManifest.BrotliFileName)));
        Assert.IsFalse(File.Exists(fixture.ArchivePath(ArchiveChangeManifest.InvalidationMarkerFileName)));
        await fixture.AssertSuccessfulRerunWithoutHistoryChangesAsync();
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task BackupAsyncAppliesCaseOnlyFileRename
    (
        bool changesContent,
        bool dryRun
    )
    {
        using var fixture = new TransitionFixture();
        await fixture.ConfigureAsync();
        fixture.RequireCaseInsensitiveArchive();
        await fixture.WriteSourceAsync("report.txt", "old file content");
        await fixture.BackupSuccessfullyAsync();
        File.Move(fixture.SourcePath("report.txt"), fixture.SourcePath("temporary.txt"));
        File.Move(fixture.SourcePath("temporary.txt"), fixture.SourcePath("Report.txt"));
        var desiredContent = changesContent ? "new file content with different length" : "old file content";
        if (changesContent) { await fixture.WriteSourceAsync("Report.txt", desiredContent); }
        var before = fixture.SnapshotArchive();

        var result = await fixture.BackupAsync(dryRun);

        Assert.IsTrue(result.Completed, result.Message);
        if (dryRun)
        {
            CollectionAssert.AreEqual(before, fixture.SnapshotArchive());
            return;
        }

        Assert.AreEqual(desiredContent, await fixture.ReadArchiveAsync("Report.txt"));
        fixture.AssertExactArchiveChildName("", "Report.txt");
        var historicalFile = fixture.HistoryPath("report.txt");
        Assert.AreEqual("old file content", await File.ReadAllTextAsync(historicalFile));
        TransitionFixture.AssertExactChildName(Path.GetDirectoryName(historicalFile)!, "report.txt");
        await fixture.AssertSuccessfulRerunWithoutHistoryChangesAsync();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BackupAsyncAppliesCaseOnlyDirectoryRenameAndPreservesCompleteFolder(bool dryRun)
    {
        using var fixture = new TransitionFixture();
        await fixture.ConfigureAsync();
        fixture.RequireCaseInsensitiveArchive();
        await fixture.WriteSourceAsync("reports/annual.txt", "unchanged folder content");
        Directory.CreateDirectory(fixture.SourcePath("reports/empty"));
        await fixture.BackupSuccessfullyAsync();
        Directory.CreateDirectory(fixture.ArchivePath("reports/native-empty/deeper"));
        Directory.Move(fixture.SourcePath("reports"), fixture.SourcePath("temporary"));
        Directory.Move(fixture.SourcePath("temporary"), fixture.SourcePath("Reports"));
        var before = fixture.SnapshotArchive();

        var result = await fixture.BackupAsync(dryRun);

        Assert.IsTrue(result.Completed, result.Message);
        if (dryRun)
        {
            CollectionAssert.AreEqual(before, fixture.SnapshotArchive());
            return;
        }

        Assert.AreEqual("unchanged folder content", await fixture.ReadArchiveAsync("Reports/annual.txt"));
        fixture.AssertExactArchiveChildName("", "Reports");
        Assert.IsTrue(File.Exists(fixture.ArchivePath("Reports/empty/.yabt-empty")));
        Assert.AreEqual
        (
            "unchanged folder content",
            await File.ReadAllTextAsync(fixture.HistoryPath("reports/annual.txt"))
        );
        Assert.IsTrue(File.Exists(fixture.HistoryPath("reports/empty/.yabt-empty")));
        Assert.IsTrue(Directory.Exists(fixture.HistoryPath("reports/native-empty/deeper")));
        var historicalFolder = fixture.HistoryPath("reports");
        TransitionFixture.AssertExactChildName(Path.GetDirectoryName(historicalFolder)!, "reports");
        await fixture.AssertSuccessfulRerunWithoutHistoryChangesAsync();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BackupAsyncRecoversAfterReplacementPayloadCannotBeRead(bool replacesDirectory)
    {
        using var fixture = new TransitionFixture();
        await fixture.ConfigureAsync();
        var previousPath = replacesDirectory ? "reports/old.txt" : "reports";
        var replacementPath = replacesDirectory ? "reports" : "reports/new.txt";
        await fixture.WriteSourceAsync(previousPath, "old archived content");
        await fixture.WriteSourceAsync("obsolete.txt", "unrelated archived content");
        await fixture.BackupSuccessfullyAsync();
        if (replacesDirectory) { fixture.DeleteSourceDirectory("reports"); }
        else { File.Delete(fixture.SourcePath("reports")); }
        File.Delete(fixture.SourcePath("obsolete.txt"));
        await fixture.WriteSourceAsync(replacementPath, "replacement content");

        await using (var heldPayload = new FileStream
        (
            fixture.SourcePath(replacementPath),
            FileMode.Open,
            FileAccess.Read,
            FileShare.None
        ))
        {
            var failed = await fixture.BackupAsync();

            Assert.IsFalse(failed.Completed);
            Assert.IsTrue(failed.FailedCount > 0, failed.Message);
            Assert.AreEqual
            (
                "old archived content",
                await File.ReadAllTextAsync(fixture.HistoryPath(previousPath))
            );
            Assert.AreEqual("unrelated archived content", await fixture.ReadArchiveAsync("obsolete.txt"));
            Assert.IsTrue(File.Exists(fixture.ArchivePath(ArchiveChangeManifest.InvalidationMarkerFileName)));
            Assert.IsFalse(File.Exists(fixture.ArchivePath(ArchiveChangeManifest.BrotliFileName)));
            Assert.IsFalse(File.Exists(fixture.ArchivePath(ArchiveChangeManifest.UncompressedFileName)));
        }

        await fixture.BackupSuccessfullyAsync();

        Assert.AreEqual("replacement content", await fixture.ReadArchiveAsync(replacementPath));
        Assert.IsFalse(File.Exists(fixture.ArchivePath("obsolete.txt")));
        Assert.AreEqual
        (
            "old archived content",
            await File.ReadAllTextAsync(fixture.HistoryPath(previousPath))
        );
        Assert.IsFalse(File.Exists(fixture.ArchivePath(ArchiveChangeManifest.InvalidationMarkerFileName)));
        Assert.IsTrue(File.Exists(fixture.ArchivePath(ArchiveChangeManifest.BrotliFileName)));
        await fixture.AssertSuccessfulRerunWithoutHistoryChangesAsync();
    }

    private sealed class TransitionFixture : IDisposable
    {
        private const string FixturePrefix = "yabt-transition-tests-";
        private readonly ServiceProvider _services;
        private readonly string _rootPath;
        private readonly string _sourceRoot;
        private readonly string _archiveRoot;

        public TransitionFixture()
        {
            _rootPath = Path.GetFullPath
            (
                Path.Combine(Path.GetTempPath(), $"{FixturePrefix}{Guid.NewGuid():N}")
            );
            _sourceRoot = Path.Combine(_rootPath, "source");
            _archiveRoot = Path.Combine(_rootPath, "archive");
            var services = new ServiceCollection();
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            services.AddYabtFileSystemObjectStore();
            services.AddYabtMetadata();
            services.AddYabtSync();
            services.AddYabtMirrorFormatHandler();
            services.AddYabtZipFormatHandler();
            _services = services.BuildServiceProvider();
        }

        public string SourcePath(string relativePath) => Path.Combine(_sourceRoot, relativePath);

        public string ArchivePath(string relativePath) => Path.Combine(_archiveRoot, relativePath);

        public async Task ConfigureAsync(string livePrefix = "")
        {
            Directory.CreateDirectory(_sourceRoot);
            Directory.CreateDirectory(_archiveRoot);
            var descriptor = new
            {
                documentType = BackupRootDescriptor.ExpectedDocumentType,
                schemaVersion = BackupRootDescriptor.ExpectedSchemaVersion,
                archiveId = Guid.NewGuid().ToString("N"),
                createdAtUtc = new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero),
                defaultStoreId = "archive",
                layout = new { livePrefix, histPrefix = ".yabt-hist" },
                stores = new[] { new { id = "archive", kind = "fileSystem", rootPath = _archiveRoot } },
            };
            await File.WriteAllTextAsync
            (
                SourcePath(BackupRootFileNames.Primary),
                JsonSerializer.Serialize(descriptor)
            );
        }

        public async Task WriteSourceAsync(string relativePath, string content)
        {
            var path = SourcePath(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, content);
        }

        public Task<string> ReadArchiveAsync(string relativePath) => File.ReadAllTextAsync(ArchivePath(relativePath));

        public Task<SyncRunResult> BackupAsync(bool dryRun = false) =>
            _services.GetRequiredService<IArchiveSynchronizer>().BackupAsync(new(_sourceRoot, DryRun: dryRun));

        public async Task BackupSuccessfullyAsync()
        {
            var result = await BackupAsync();
            Assert.IsTrue(result.Completed, result.Message);
        }

        public async Task AssertSuccessfulRerunWithoutHistoryChangesAsync()
        {
            var before = SnapshotHistory();

            var result = await BackupAsync();

            Assert.IsTrue(result.Completed, result.Message);
            Assert.AreEqual(0, result.NewCount);
            Assert.AreEqual(0, result.ChangedCount);
            Assert.AreEqual(0, result.ExtraCount);
            CollectionAssert.AreEqual(before, SnapshotHistory());
        }

        public string HistoryPath(string relativePath)
        {
            var historyRoot = ArchivePath(".yabt-hist");
            Assert.IsTrue(Directory.Exists(historyRoot), "The previous representation must remain in history.");
            var matchingPaths = Directory.GetDirectories(historyRoot).
                Select(path => Path.Combine(path, relativePath)).
                Where(path => File.Exists(path) || Directory.Exists(path)).ToArray();
            Assert.AreEqual(1, matchingPaths.Length, $"Expected one historical occurrence of '{relativePath}'.");
            return matchingPaths[0];
        }

        public string[] SnapshotArchive() => SnapshotTree(_archiveRoot);

        public void AssertExactArchiveChildName(string relativeParent, string expectedName) =>
            AssertExactChildName(ArchivePath(relativeParent), expectedName);

        public static void AssertExactChildName(string parentPath, string expectedName)
        {
            var actualNames = Directory.GetFileSystemEntries(parentPath).
                Select(Path.GetFileName).
                Where(name => string.Equals(name, expectedName, StringComparison.OrdinalIgnoreCase)).ToArray();
            CollectionAssert.AreEqual(new[] { expectedName }, actualNames);
        }

        public void RequireCaseInsensitiveArchive()
        {
            var probe = ArchivePath("case-probe");
            File.WriteAllText(probe, "case probe");
            var ignoresCase = File.Exists(ArchivePath("CASE-PROBE"));
            File.Delete(probe);
            if (!ignoresCase) { Assert.Inconclusive("This regression requires a case-insensitive target directory."); }
        }

        public void DeleteSourceDirectory(string relativePath)
        {
            var fullPath = Path.GetFullPath(SourcePath(relativePath));
            Assert.IsTrue(fullPath.StartsWith(_sourceRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal));
            Directory.Delete(fullPath, recursive: true);
        }

        public void Dispose()
        {
            _services.Dispose();
            var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            Assert.AreEqual(temporaryRoot, Path.GetDirectoryName(_rootPath));
            Assert.IsTrue(Path.GetFileName(_rootPath).StartsWith(FixturePrefix, StringComparison.Ordinal));
            if (Directory.Exists(_rootPath)) { Directory.Delete(_rootPath, recursive: true); }
        }

        private string[] SnapshotHistory() => SnapshotTree(ArchivePath(".yabt-hist"));

        private static string[] SnapshotTree(string rootPath)
        {
            if (!Directory.Exists(rootPath)) { return []; }
            return Directory.GetFileSystemEntries(rootPath, "*", SearchOption.AllDirectories).
                Order(StringComparer.Ordinal).
                Select
                (
                    path => Directory.Exists(path) ?
                        $"D|{Path.GetRelativePath(rootPath, path)}" :
                        $"F|{Path.GetRelativePath(rootPath, path)}|{File.GetLastWriteTimeUtc(path).Ticks}|" +
                            Convert.ToBase64String(File.ReadAllBytes(path))
                ).ToArray();
        }
    }
}
