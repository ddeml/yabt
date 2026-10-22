using Microsoft.Extensions.Logging.Abstractions;
using Yabt.Common;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.Sync.Implementation;

namespace Yabt.Sync.Tests;

[TestClass]
public sealed class RestorePlanIndexTests
{
    [TestMethod]
    [DataRow(64)]
    [DataRow(512)]
    [DataRow(2048)]
    public async Task DirectoryClassificationDoesNotScanThePlan(int directoryCount)
    {
        var comparer = new CountingComparer(StringComparer.OrdinalIgnoreCase);
        await using var plan = CreatePlan(
            Enumerable.Range(0, directoryCount).Select(number => $"Folder-{number:D8}"),
            [],
            comparer);
        comparer.Reset();

        for (var number = 0; number < directoryCount; number++)
        {
            var path = $"folder-{number:D8}";
            Assert.AreEqual("directory", ArchiveOperationLogging.GetRestoreItemKind(plan, path));
            Assert.IsTrue(plan.IsEmptyDirectory(path));
            Assert.AreEqual("file", ArchiveOperationLogging.GetRestoreItemKind(plan, path + "/file"));
        }

        Assert.IsTrue(comparer.HashCalls <= directoryCount * 3);
        Assert.IsTrue(comparer.EqualityCalls <= directoryCount * 4);
        Assert.IsGreaterThan(0, comparer.HashCalls);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task MembershipUsesProviderEqualityAndPreservesExactNames(bool ignoreCase)
    {
        var comparer = ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        await using var plan = CreatePlan(["Folder", "Folder/Empty"], ["Folder"], comparer);

        Assert.AreEqual(ignoreCase, plan.IsDirectory("folder"));
        Assert.AreEqual(ignoreCase, plan.IsEmptyDirectory("folder/empty"));
        Assert.IsTrue(plan.IsDirectory("Folder"));
        Assert.IsFalse(plan.IsEmptyDirectory("Folder"));
        Assert.IsFalse(plan.IsDirectory("FolderSuffix"));
        CollectionAssert.AreEqual(new[] { "Folder", "Folder/Empty" }, plan.Directories.ToArray());
        CollectionAssert.AreEqual(new[] { "Folder/Empty" }, plan.EmptyDirectories.ToArray());
    }

    [TestMethod]
    public async Task DirectoryEnumerationReplaysInDepthThenOrdinalOrder()
    {
        string[] paths = ["b/child/deep", "b/child", "b", "a/child", "A", "a"];
        await using var plan = CreatePlan(paths, ["b", "b/child"], StringComparer.Ordinal);
        string[] expected = ["A", "a", "b", "a/child", "b/child", "b/child/deep"];
        string[] expectedEmpty = ["A", "a", "a/child", "b/child/deep"];

        Assert.AreEqual(expected.Length, plan.Directories.Count);
        Assert.AreEqual(expectedEmpty.Length, plan.EmptyDirectories.Count);
        CollectionAssert.AreEqual(expected, plan.Directories.ToArray());
        CollectionAssert.AreEqual(expectedEmpty, plan.EmptyDirectories.ToArray());
        CollectionAssert.AreEqual(expected, plan.Directories.ToArray());
    }

    [TestMethod]
    public async Task DisabledUnchangedDryRunLoggingDoesNotClassifyItems()
    {
        var comparer = new CountingComparer(StringComparer.Ordinal);
        await using var plan = CreatePlan(["folder"], [], comparer);
        using var historyMoves = new RestoreHistoryPlan(plan.Namespace.PathComparer);
        var reconciliation = new RestoreReconciliation(
            FilesToWrite: [],
            HistoryMoves: historyMoves,
            NewDesiredPaths: new HashSet<string>(),
            ChangedDesiredPaths: new HashSet<string>(),
            UnchangedDesiredPaths: new HashSet<string>(["folder", "file"]),
            NewCount: 0,
            ChangedCount: 0,
            ExtraCount: 0,
            UnchangedCount: 2,
            Failures: []);
        comparer.Reset();

        NullLogger.Instance.ReportRestoreDryRun(
            plan,
            reconciliation,
            RootDescriptorRestore.None,
            restoreRootNeedsCreation: false);

        Assert.AreEqual(0, comparer.HashCalls);
        Assert.AreEqual(0, comparer.EqualityCalls);
    }

    [TestMethod]
    public async Task DiskOnlyPlanOwnsIndexUntilDisposalAfterOperationEnds()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            RestorePlan plan;
            using (TemporaryIndex.BeginOperation(new()
            {
                TemporaryDirectory = directory,
                MemoryThresholdBytes = 0,
                TotalMemoryBudgetBytes = 0,
            }))
            {
                plan = CreatePlan(["Folder", "Folder/Empty"], ["Folder"], StringComparer.Ordinal);
                Assert.HasCount(1, Directory.GetFiles(directory, "*.sqlite"));
            }

            try
            {
                Assert.HasCount(1, Directory.GetFiles(directory, "*.sqlite"));
                Assert.IsTrue(plan.IsEmptyDirectory("Folder/Empty"));
                CollectionAssert.AreEqual(new[] { "Folder", "Folder/Empty" }, plan.Directories.ToArray());
            }
            finally
            {
                await plan.DisposeAsync();
            }

            Assert.IsEmpty(Directory.GetFiles(directory));
            Assert.ThrowsExactly<ObjectDisposedException>(() => plan.IsDirectory("Folder"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void InterruptedDirectoryIndexConstructionReleasesTemporaryStorage(bool cancellation)
    {
        var directory = CreateTemporaryDirectory();
        using var source = new CancellationTokenSource();
        try
        {
            using (TemporaryIndex.BeginOperation(new() { TemporaryDirectory = directory }))
            {
                IEnumerable<string> Paths()
                {
                    yield return "first";
                    if (cancellation)
                    {
                        source.Cancel();
                        yield return "second";
                    }
                    else
                    {
                        throw new IOException("Injected directory enumeration failure.");
                    }
                }

                void Create() => RestoreDirectoryIndex.Create(
                    Paths(), new HashSet<string>(), StringComparer.Ordinal, source.Token).Dispose();
                if (cancellation) { Assert.ThrowsExactly<OperationCanceledException>(Create); }
                else { Assert.ThrowsExactly<IOException>(Create); }
            }

            Assert.IsEmpty(Directory.GetFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task ProjectionDisposalFailureStillReleasesDirectoryIndex()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            RestorePlan plan;
            using (TemporaryIndex.BeginOperation(new() { TemporaryDirectory = directory }))
            {
                var directories = RestoreDirectoryIndex.Create(
                    ["folder"], new HashSet<string>(), StringComparer.Ordinal);
                plan = new(
                    [],
                    directories,
                    [new ArchiveRestoreProjection(lifetime: new FailingDisposal())],
                    new Dictionary<string, IReadOnlyList<ArchiveRestoreProjection>>(),
                    ObjectStoreNamespace.Ordinal);
            }

            await Assert.ThrowsExactlyAsync<IOException>(() => plan.DisposeAsync().AsTask());

            Assert.IsEmpty(Directory.GetFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static RestorePlan CreatePlan
    (
        IEnumerable<string> directories,
        IEnumerable<string> nonemptyDirectories,
        StringComparer comparer
    ) => new(
        [],
        RestoreDirectoryIndex.Create(
            directories,
            nonemptyDirectories.ToHashSet(StringComparer.Ordinal),
            comparer),
        [],
        new Dictionary<string, IReadOnlyList<ArchiveRestoreProjection>>(),
        new TestNamespace(comparer));

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "yabt-restore-index-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class TestNamespace(StringComparer _comparer) : ObjectStoreNamespace
    {
        public override StringComparer PathComparer => _comparer;

        public override void ValidatePath(ObjectPath path) { }
    }

    private sealed class CountingComparer(StringComparer _inner) : StringComparer
    {
        public int HashCalls { get; private set; }

        public int EqualityCalls { get; private set; }

        public override int Compare(string? x, string? y) => _inner.Compare(x, y);

        public override bool Equals(string? x, string? y)
        {
            EqualityCalls++;
            return _inner.Equals(x, y);
        }

        public override int GetHashCode(string obj)
        {
            HashCalls++;
            return _inner.GetHashCode(obj);
        }

        public void Reset()
        {
            HashCalls = 0;
            EqualityCalls = 0;
        }
    }

    private sealed class FailingDisposal : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.FromException(new IOException("Injected cleanup failure."));
    }
}
