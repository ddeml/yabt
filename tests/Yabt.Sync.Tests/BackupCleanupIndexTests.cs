using System.Collections;
using Microsoft.Extensions.Logging.Abstractions;
using Yabt.Common;
using Yabt.Core.Models;
using Yabt.Metadata;
using Yabt.Sync.Implementation;
using Yabt.Tests;

namespace Yabt.Sync.Tests;

[TestClass]
public sealed class BackupCleanupIndexTests
{
    [TestMethod]
    public void SelectionPreservesDepthOrdinalOrderAndExactPrefixBoundaries()
    {
        string[] candidates =
        [
            "omega/child/grandchild", "alpha/nested", "alpha", "", "zeta",
            "kept/deep", "Alpha/nested", "Alpha", "omega/child", "alpha", " ",
            "alphabet/child", " /nested"
        ];
        using var folders = BackupCleanupFolderIndex.Create(candidates, default);

        CollectionAssert.AreEqual
        (
            new[] { " ", "Alpha", "alpha", "zeta", "alphabet/child", "kept/deep", "omega/child" },
            folders.Enumerate(default).ToArray()
        );
        Assert.IsFalse(folders.ContainsAncestor("alpha", default));
        Assert.IsTrue(folders.ContainsAncestor("alpha/file", default));
        Assert.IsFalse(folders.ContainsAncestor("ALPHA/file", default));
        Assert.IsFalse(folders.ContainsAncestor("alphabet/file", default));
        Assert.IsFalse(folders.ContainsAncestor("kept/deep", default));
        Assert.IsTrue(folders.ContainsAncestor("kept/deep/file", default));
        Assert.IsTrue(folders.ContainsAncestor(" /file", default));
    }

    [TestMethod]
    [DataRow(100)]
    [DataRow(1000)]
    [DataRow(10000)]
    public void AncestorLookupWorkDependsOnPathDepthInsteadOfFolderCount(int folderCount)
    {
        var folders = new MembershipOnlySet
        (
            Enumerable.Range(0, folderCount).Select(number => $"folder{number:D5}")
        );
        for (var number = 0; number < folderCount; number++)
        {
            Assert.IsTrue(BackupCleanupFolderIndex.ContainsAncestor(
                $"folder{number:D5}/nested/file", folders, default));
            Assert.IsFalse(BackupCleanupFolderIndex.ContainsAncestor(
                $"unrelated{number:D5}/nested/file", folders, default));
            Assert.IsFalse(BackupCleanupFolderIndex.ContainsAncestor(
                $"folder{number:D5}", folders, default));
        }

        Assert.AreEqual(3L * folderCount, folders.LookupCount);
        Assert.AreEqual(0, folders.EnumerationCount);
    }

    [TestMethod]
    public void LargeSiblingSelectionReplaysFromDiskAndReleasesOwnedStorage()
    {
        var directory = Directory.CreateTempSubdirectory("yabt-cleanup-index-");
        try
        {
            using (TemporaryIndex.BeginOperation(new() { TemporaryDirectory = directory.FullName }))
            {
                const int folderCount = 10000;
                var candidates = Enumerable.Range(0, folderCount).Reverse().SelectMany
                (
                    number => new[]
                    {
                        $"parent{number:D5}/nested/deep", $"parent{number:D5}",
                        $"parent{number:D5}/nested", $"parent{number:D5}"
                    }
                );
                using var folders = BackupCleanupFolderIndex.Create(candidates, default);
                Assert.AreEqual(1, directory.EnumerateFiles("*.sqlite").Count());
                var position = 0;
                foreach (var folder in folders.Enumerate(default))
                {
                    Assert.AreEqual($"parent{position:D5}", folder);
                    Assert.IsTrue(folders.ContainsAncestor($"{folder}/file", default));
                    position++;
                }

                Assert.AreEqual(folderCount, position);
                Assert.AreEqual(folderCount, folders.Enumerate(default).Count());
            }

            Assert.AreEqual(0, directory.EnumerateFileSystemInfos().Count());
        }
        finally { directory.Delete(recursive: true); }
    }

    [TestMethod]
    public void SelectionFailureAndCancellationReleaseScratchOwnership()
    {
        var directory = Directory.CreateTempSubdirectory("yabt-cleanup-index-failure-");
        try
        {
            using (TemporaryIndex.BeginOperation(new() { TemporaryDirectory = directory.FullName }))
            {
                Assert.Throws<ArgumentException>(() =>
                    BackupCleanupFolderIndex.Create(["valid", "../invalid"], default));
            }

            Assert.AreEqual(0, directory.EnumerateFileSystemInfos().Count());
            using var cancellation = new CancellationTokenSource();
            IEnumerable<string> CancelDuringInput()
            {
                yield return "first";
                cancellation.Cancel();
                yield return "second";
            }

            using (TemporaryIndex.BeginOperation(new() { TemporaryDirectory = directory.FullName }))
            {
                Assert.Throws<OperationCanceledException>(() =>
                    BackupCleanupFolderIndex.Create(CancelDuringInput(), cancellation.Token));
            }

            Assert.AreEqual(0, directory.EnumerateFileSystemInfos().Count());
        }
        finally { directory.Delete(recursive: true); }
    }

    [TestMethod]
    public void LookupAndOrderedReplayObserveCancellation()
    {
        using var folders = BackupCleanupFolderIndex.Create(["first", "second"], default);
        using var cancellation = new CancellationTokenSource();
        using var enumerator = folders.Enumerate(cancellation.Token).GetEnumerator();
        Assert.IsTrue(enumerator.MoveNext());
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => enumerator.MoveNext());
        Assert.Throws<OperationCanceledException>(() =>
            folders.ContainsAncestor("first/file", cancellation.Token));
    }

    [TestMethod]
    public async Task CleanupMovesAnExactObjectAndItsSameNamedFolderSeparately()
    {
        var target = new MemoryObjectStore();
        string[] paths = ["obsolete", "obsolete/child.txt", "obsolete/nested/deep.txt", "obsolete-other.txt"];
        foreach (var path in paths)
        {
            using var payload = new MemoryStream([1, 2, 3]);
            await target.UploadAsync(path, payload, "application/octet-stream", new Dictionary<string, string>());
        }

        var root = new TargetFolderState();
        root.Objects.Add("obsolete", new("obsolete"));
        root.Objects.Add("obsolete-other.txt", new("obsolete-other.txt"));
        root.Folders.Add("obsolete", "obsolete");
        var child = new TargetFolderState();
        child.Objects.Add("obsolete/child.txt", new("obsolete/child.txt"));
        child.Folders.Add("obsolete/nested", "obsolete/nested");
        var nested = new TargetFolderState();
        nested.Objects.Add("obsolete/nested/deep.txt", new("obsolete/nested/deep.txt"));
        Dictionary<string, TargetFolderState> targetFolders = new(StringComparer.Ordinal)
        {
            [""] = root,
            ["obsolete"] = child,
            ["obsolete/nested"] = nested
        };
        using var index = TemporaryIndex.Create();
        using var previousEntries = index.CreateDictionary<ArchiveChangeManifestEntry>();
        var summary = new ArchiveSyncSummary();
        var reconciler = new BackupTargetReconciler(NullLogger<BackupTargetReconciler>.Instance);
        var timestamp = new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
        var historizer = new ArchiveHistorizer(target, ArchiveLayout.Default, timestamp, NullLogger.Instance);
        var beforeWriteCount = 0;

        await reconciler.MoveRemainingTargetObjectsToHistoryAsync
        (
            target,
            historizer,
            ArchiveLayout.Default,
            targetFolders,
            new HashSet<string>(StringComparer.Ordinal),
            previousEntries,
            summary,
            writeChanges: true,
            verifyOnly: false,
            _ => { beforeWriteCount++; return Task.CompletedTask; },
            default
        );

        Assert.AreEqual(3, beforeWriteCount);
        Assert.AreEqual(4, summary.ExtraCount);
        foreach (var path in paths) { Assert.IsFalse(await target.ExistsAsync(path)); }
        Assert.IsTrue(await target.ExistsAsync(".yabt-hist/20261022T120000Z/obsolete"));
        Assert.IsTrue(await target.ExistsAsync(".yabt-hist/20261022T120000Z/obsolete-other.txt"));
        Assert.IsTrue(await target.ExistsAsync(".yabt-hist/20261022T120000Z-1/obsolete/child.txt"));
        Assert.IsTrue(await target.ExistsAsync(".yabt-hist/20261022T120000Z-1/obsolete/nested/deep.txt"));
    }

    private sealed class MembershipOnlySet(IEnumerable<string> values) : IReadOnlySet<string>
    {
        private readonly HashSet<string> _values = new(values, StringComparer.Ordinal);

        public long LookupCount { get; private set; }
        public int EnumerationCount { get; private set; }
        public int Count => _values.Count;

        public bool Contains(string item)
        {
            LookupCount++;
            return _values.Contains(item);
        }

        public IEnumerator<string> GetEnumerator()
        {
            EnumerationCount++;
            throw new AssertFailedException("Ancestor lookup must not enumerate the folder set.");
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public bool IsProperSubsetOf(IEnumerable<string> other) => throw new NotSupportedException();

        public bool IsProperSupersetOf(IEnumerable<string> other) => throw new NotSupportedException();

        public bool IsSubsetOf(IEnumerable<string> other) => throw new NotSupportedException();

        public bool IsSupersetOf(IEnumerable<string> other) => throw new NotSupportedException();

        public bool Overlaps(IEnumerable<string> other) => throw new NotSupportedException();

        public bool SetEquals(IEnumerable<string> other) => throw new NotSupportedException();
    }
}
