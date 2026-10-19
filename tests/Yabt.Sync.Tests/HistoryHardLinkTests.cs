using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yabt.Core.Models;
using Yabt.Metadata;

namespace Yabt.Sync.Tests;

[TestClass]
public sealed class HistoryHardLinkTests
{
    [TestMethod]
    [DataRow(true, false, false)]
    [DataRow(false, false, false)]
    [DataRow(true, true, false)]
    [DataRow(false, true, false)]
    [DataRow(true, false, true)]
    [DataRow(false, false, true)]
    public async Task DeduplicateAsyncPreservesIndependentCopyAndSharedOccurrence
    (
        bool linkedFirst,
        bool dryRun,
        bool sharesLiveFile
    )
    {
        using var fixture = new HardLinkHistoryFixture();
        await fixture.ConfigureAsync();
        var content = CreateContent();
        var externalPath = sharesLiveFile ?
            Path.Combine(fixture.ArchiveRoot, "live.bin") :
            Path.Combine(fixture.RootPath, "external.bin");
        await fixture.WriteBytesAsync(externalPath, content);
        var linkedPath = fixture.HistoryPath(linkedFirst ? "a.bin" : "b.bin");
        var privatePath = fixture.HistoryPath(linkedFirst ? "b.bin" : "a.bin");
        await fixture.CreateHardLinkAsync(linkedPath, externalPath);
        await fixture.WriteBytesAsync(privatePath, content);
        var historyBefore = fixture.SnapshotHistory();

        var result = await fixture.Deduplicator.DeduplicateAsync
        (
            new(fixture.SourceRoot, DryRun: dryRun)
        );

        Assert.IsTrue(result.Completed, result.Message);
        Assert.AreEqual(0, result.ReplacedObjectCount);
        Assert.AreEqual(0L, result.BytesSaved);
        CollectionAssert.AreEqual(content, await File.ReadAllBytesAsync(linkedPath));
        CollectionAssert.AreEqual(content, await File.ReadAllBytesAsync(privatePath));
        Assert.AreEqual(0, fixture.ReferencePaths().Length);
        if (dryRun)
        {
            CollectionAssert.AreEqual(historyBefore, fixture.SnapshotHistory());
            Assert.IsFalse(Directory.Exists(Path.Combine(fixture.ArchiveRoot, ".yabt-tmp")));
        }

        var changed = ChangeContent(content);
        await fixture.OverwriteInPlaceAsync(externalPath, changed);
        CollectionAssert.AreEqual(changed, await File.ReadAllBytesAsync(linkedPath));
        CollectionAssert.AreEqual
        (
            content,
            await File.ReadAllBytesAsync(privatePath),
            "Deduplication must retain the independent archive copy."
        );
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DeduplicateAsyncDoesNotClaimSavingsForSharedHistoricalNames(bool dryRun)
    {
        using var fixture = new HardLinkHistoryFixture();
        await fixture.ConfigureAsync();
        var content = CreateContent();
        var firstPath = fixture.HistoryPath("a.bin");
        var secondPath = fixture.HistoryPath("b.bin");
        await fixture.WriteBytesAsync(firstPath, content);
        await fixture.CreateHardLinkAsync(secondPath, firstPath);
        var historyBefore = fixture.SnapshotHistory();

        var result = await fixture.Deduplicator.DeduplicateAsync
        (
            new(fixture.SourceRoot, DryRun: dryRun)
        );

        Assert.IsTrue(result.Completed, result.Message);
        Assert.AreEqual(0, result.ReplacedObjectCount);
        Assert.AreEqual(0L, result.BytesSaved);
        CollectionAssert.AreEqual(content, await File.ReadAllBytesAsync(firstPath));
        CollectionAssert.AreEqual(content, await File.ReadAllBytesAsync(secondPath));
        Assert.AreEqual(0, fixture.ReferencePaths().Length);
        if (dryRun) { CollectionAssert.AreEqual(historyBefore, fixture.SnapshotHistory()); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DeduplicateAsyncRejectsReferenceWhoseOnlyBackingBecameShared(bool dryRun)
    {
        using var fixture = new HardLinkHistoryFixture();
        await fixture.ConfigureAsync();
        var content = CreateContent();
        var backingPath = fixture.HistoryPath("a.bin");
        var duplicatePath = fixture.HistoryPath("b.bin");
        await fixture.WriteBytesAsync(backingPath, content);
        await fixture.WriteBytesAsync(duplicatePath, content);
        var initial = await fixture.Deduplicator.DeduplicateAsync(new(fixture.SourceRoot));
        Assert.AreEqual(1, initial.ReplacedObjectCount);
        Assert.IsTrue(File.Exists(backingPath));
        Assert.AreEqual(1, fixture.ReferencePaths().Length);

        var externalPath = Path.Combine(fixture.RootPath, "external.bin");
        await fixture.CreateHardLinkAsync(externalPath, backingPath);
        var unrelatedContent = ChangeContent(content);
        await fixture.WriteBytesAsync(fixture.HistoryPath("c.bin"), unrelatedContent);
        await fixture.WriteBytesAsync(fixture.HistoryPath("d.bin"), unrelatedContent);
        var historyBefore = fixture.SnapshotHistory();

        var exception = await Assert.ThrowsExactlyAsync<YabtSyncException>
        (
            () => fixture.Deduplicator.DeduplicateAsync(new(fixture.SourceRoot, DryRun: dryRun))
        );

        StringAssert.Contains(exception.ToString().ToLowerInvariant(), "backing");
        CollectionAssert.AreEqual
        (
            historyBefore,
            fixture.SnapshotHistory(),
            "An unsafe existing reference must prevent reference, catalog, and payload mutations."
        );
        CollectionAssert.AreEqual(content, await File.ReadAllBytesAsync(externalPath));
    }

    [TestMethod]
    public async Task DeduplicateAsyncUsesIndependentBackingWhenSharedOccurrenceExists()
    {
        using var fixture = new HardLinkHistoryFixture();
        await fixture.ConfigureAsync();
        var content = CreateContent();
        var externalPath = Path.Combine(fixture.RootPath, "external.bin");
        var linkedPath = fixture.HistoryPath("a.bin");
        var privatePath = fixture.HistoryPath("b.bin");
        var duplicatePath = fixture.HistoryPath("c.bin");
        await fixture.WriteBytesAsync(externalPath, content);
        await fixture.CreateHardLinkAsync(linkedPath, externalPath);
        await fixture.WriteBytesAsync(privatePath, content);
        await fixture.WriteBytesAsync(duplicatePath, content);

        var first = await fixture.Deduplicator.DeduplicateAsync(new(fixture.SourceRoot));

        Assert.IsTrue(first.Completed, first.Message);
        Assert.AreEqual(1, first.ReplacedObjectCount);
        Assert.IsTrue(first.BytesSaved > 0);
        Assert.IsTrue(File.Exists(linkedPath));
        Assert.IsTrue(File.Exists(privatePath));
        Assert.IsFalse(File.Exists(duplicatePath));
        var referencePath = duplicatePath + ArchiveHistoryFileNames.ReferenceSuffix;
        var referenceBytes = await File.ReadAllBytesAsync(referencePath);

        await fixture.OverwriteInPlaceAsync(externalPath, ChangeContent(content));
        var second = await fixture.Deduplicator.DeduplicateAsync(new(fixture.SourceRoot));

        Assert.IsTrue(second.Completed, second.Message);
        Assert.AreEqual(0, second.ReplacedObjectCount);
        Assert.AreEqual(0L, second.BytesSaved);
        Assert.AreEqual(1, second.ExistingReferenceCount);
        CollectionAssert.AreEqual(content, await File.ReadAllBytesAsync(privatePath));
        CollectionAssert.AreEqual(referenceBytes, await File.ReadAllBytesAsync(referencePath));
    }

    [TestMethod]
    public async Task DeduplicateAsyncKeepsOrphanReferenceWhenItsMaterializationIsShared()
    {
        using var fixture = new HardLinkHistoryFixture();
        await fixture.ConfigureAsync();
        var content = CreateContent();
        var canonicalPath = fixture.HistoryPath("a.bin");
        var duplicatePath = fixture.HistoryPath("b.bin");
        await fixture.WriteBytesAsync(canonicalPath, content);
        await fixture.WriteBytesAsync(duplicatePath, content);
        var initial = await fixture.Deduplicator.DeduplicateAsync(new(fixture.SourceRoot));
        Assert.AreEqual(1, initial.ReplacedObjectCount);

        var referencePath = duplicatePath + ArchiveHistoryFileNames.ReferenceSuffix;
        ArchiveHistoryContentReference reference;
        await using (var stream = File.OpenRead(referencePath))
        {
            reference = await fixture.Services.GetRequiredService<IHistoryReferenceSerializer>().ReadAsync(stream);
        }
        var externalPath = Path.Combine(fixture.RootPath, "external.bin");
        await fixture.WriteBytesAsync(externalPath, content);
        File.SetLastWriteTimeUtc(externalPath, reference.Entry.LastModifiedUtc!.Value.UtcDateTime);
        await fixture.CreateHardLinkAsync(duplicatePath, externalPath);
        File.Delete(canonicalPath);
        var historyBefore = fixture.SnapshotHistory();

        var exception = await Assert.ThrowsExactlyAsync<YabtSyncException>
        (
            () => fixture.Deduplicator.DeduplicateAsync(new(fixture.SourceRoot))
        );

        StringAssert.Contains(exception.ToString().ToLowerInvariant(), "backing");
        CollectionAssert.AreEqual(historyBefore, fixture.SnapshotHistory());
        CollectionAssert.AreEqual(content, await File.ReadAllBytesAsync(externalPath));
    }

    [TestMethod]
    [DataRow("mirror")]
    [DataRow("zip")]
    public async Task BackupAndRestoreAsyncMaterializesIndependentCopiesOfSourceHardLinks(string format)
    {
        using var fixture = new HardLinkHistoryFixture();
        await fixture.ConfigureAsync(format);
        var content = CreateContent();
        var sourceFirst = Path.Combine(fixture.SourceRoot, "first.bin");
        var sourceSecond = Path.Combine(fixture.SourceRoot, "second.bin");
        await fixture.WriteBytesAsync(sourceFirst, content);
        await fixture.CreateHardLinkAsync(sourceSecond, sourceFirst);

        var backup = await fixture.Synchronizer.BackupAsync(new(fixture.SourceRoot));
        Assert.IsTrue(backup.Completed, backup.Message);
        var changed = ChangeContent(content);
        await fixture.OverwriteInPlaceAsync(sourceFirst, changed);
        CollectionAssert.AreEqual(changed, await File.ReadAllBytesAsync(sourceSecond));

        var restore = await fixture.Synchronizer.RestoreAsync
        (
            new(fixture.SourceRoot, DestinationRoot: fixture.RestoreRoot, ByteForByte: true)
        );

        Assert.IsTrue(restore.Completed, restore.Message);
        var restoredFirst = Path.Combine(fixture.RestoreRoot, "first.bin");
        var restoredSecond = Path.Combine(fixture.RestoreRoot, "second.bin");
        CollectionAssert.AreEqual(content, await File.ReadAllBytesAsync(restoredFirst));
        CollectionAssert.AreEqual(content, await File.ReadAllBytesAsync(restoredSecond));
        await fixture.OverwriteInPlaceAsync(restoredFirst, changed);
        CollectionAssert.AreEqual(content, await File.ReadAllBytesAsync(restoredSecond));

        if (format == "mirror")
        {
            var archivedFirst = Path.Combine(fixture.ArchiveRoot, "first.bin");
            var archivedSecond = Path.Combine(fixture.ArchiveRoot, "second.bin");
            CollectionAssert.AreEqual(content, await File.ReadAllBytesAsync(archivedFirst));
            await fixture.OverwriteInPlaceAsync(archivedFirst, changed);
            CollectionAssert.AreEqual(content, await File.ReadAllBytesAsync(archivedSecond));
        }
    }

    private static byte[] CreateContent() => Enumerable.Range(0, 20_000).
        Select(index => (byte)(index % 251)).ToArray();

    private static byte[] ChangeContent(byte[] content)
    {
        var changed = content.ToArray();
        changed[0] ^= 0xff;
        changed[^1] ^= 0xff;
        return changed;
    }

    private sealed class HardLinkHistoryFixture : IDisposable
    {
        private const string FixturePrefix = "yabt-history-hardlinks-";
        private readonly List<string> _hardLinks = [];

        public HardLinkHistoryFixture()
        {
            RootPath = Path.GetFullPath
            (
                Path.Combine(Path.GetTempPath(), $"{FixturePrefix}{Guid.NewGuid():N}")
            );
            Directory.CreateDirectory(RootPath);
            SourceRoot = Path.Combine(RootPath, "source");
            ArchiveRoot = Path.Combine(RootPath, "archive");
            RestoreRoot = Path.Combine(RootPath, "restored");
            var services = new ServiceCollection();
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            services.AddYabtFileSystemObjectStore();
            services.AddYabtMetadata();
            services.AddYabtSync();
            services.AddYabtMirrorFormatHandler();
            services.AddYabtZipFormatHandler();
            Services = services.BuildServiceProvider();
        }

        public string RootPath { get; }
        public string SourceRoot { get; }
        public string ArchiveRoot { get; }
        public string RestoreRoot { get; }
        public ServiceProvider Services { get; }

        public IHistoryDeduplicator Deduplicator => Services.GetRequiredService<IHistoryDeduplicator>();
        public IArchiveSynchronizer Synchronizer => Services.GetRequiredService<IArchiveSynchronizer>();

        public string HistoryPath(string name) => Path.Combine(ArchiveRoot, ".yabt-hist", "one", name);

        public async Task ConfigureAsync(string format = "mirror")
        {
            Directory.CreateDirectory(SourceRoot);
            Directory.CreateDirectory(ArchiveRoot);
            var descriptor = new
            {
                documentType = BackupRootDescriptor.ExpectedDocumentType,
                schemaVersion = BackupRootDescriptor.ExpectedSchemaVersion,
                archiveId = Guid.NewGuid().ToString("N"),
                createdAtUtc = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero),
                defaultStoreId = "archive",
                layout = new { livePrefix = "", histPrefix = ".yabt-hist" },
                stores = new[] { new { id = "archive", kind = "fileSystem", rootPath = ArchiveRoot } },
            };
            await File.WriteAllTextAsync
            (
                Path.Combine(SourceRoot, BackupRootFileNames.Primary),
                JsonSerializer.Serialize(descriptor)
            );
            await File.WriteAllTextAsync
            (
                Path.Combine(SourceRoot, FolderPolicyFileNames.Primary),
                JsonSerializer.Serialize(new { format })
            );
        }

        public async Task WriteBytesAsync(string path, byte[] content)
        {
            AssertInsideFixture(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, content);
        }

        public async Task OverwriteInPlaceAsync(string path, byte[] content)
        {
            AssertInsideFixture(path);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
            await stream.WriteAsync(content);
            stream.SetLength(content.LongLength);
        }

        public async Task CreateHardLinkAsync(string linkPath, string existingPath)
        {
            AssertInsideFixture(linkPath);
            AssertInsideFixture(existingPath);
            Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
            var startInfo = new ProcessStartInfo
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            if (OperatingSystem.IsWindows())
            {
                var script = $"New-Item -ItemType HardLink -Path '{linkPath.Replace("'", "''")}' " +
                    $"-Value '{existingPath.Replace("'", "''")}' -ErrorAction Stop | Out-Null";
                startInfo.FileName = "powershell.exe";
                startInfo.ArgumentList.Add("-NoProfile");
                startInfo.ArgumentList.Add("-NonInteractive");
                startInfo.ArgumentList.Add("-EncodedCommand");
                startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
            }
            else
            {
                startInfo.FileName = "ln";
                startInfo.ArgumentList.Add(existingPath);
                startInfo.ArgumentList.Add(linkPath);
            }
            using var process = Process.Start(startInfo) ??
                throw new InvalidOperationException("Could not start hard-link creation.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.AreEqual(0, process.ExitCode, $"Hard-link creation failed: {await output} {await error}");
            _hardLinks.Add(linkPath);
            Assert.AreEqual((FileAttributes)0, File.GetAttributes(linkPath) & FileAttributes.ReparsePoint);
        }

        public string[] ReferencePaths() => Directory.GetFiles
        (
            Path.Combine(ArchiveRoot, ".yabt-hist"),
            $"*{ArchiveHistoryFileNames.ReferenceSuffix}",
            SearchOption.AllDirectories
        );

        public string[] SnapshotHistory()
        {
            var historyRoot = Path.Combine(ArchiveRoot, ".yabt-hist");
            return Directory.EnumerateFiles(historyRoot, "*", SearchOption.AllDirectories).
                Order(StringComparer.Ordinal).
                Select
                (
                    path => $"{Path.GetRelativePath(historyRoot, path)}|" +
                        $"{File.GetLastWriteTimeUtc(path).Ticks}|{Convert.ToBase64String(File.ReadAllBytes(path))}"
                ).ToArray();
        }

        public void Dispose()
        {
            Services.Dispose();
            var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            Assert.AreEqual(temporaryRoot, Path.GetDirectoryName(RootPath));
            Assert.IsTrue(Path.GetFileName(RootPath).StartsWith(FixturePrefix, StringComparison.Ordinal));
            foreach (var linkPath in _hardLinks)
            {
                AssertInsideFixture(linkPath);
                File.Delete(linkPath);
            }
            if (Directory.Exists(RootPath)) { Directory.Delete(RootPath, recursive: true); }
        }

        private void AssertInsideFixture(string path) => Assert.IsTrue
        (
            Path.GetFullPath(path).StartsWith(RootPath + Path.DirectorySeparatorChar, StringComparison.Ordinal)
        );
    }
}
