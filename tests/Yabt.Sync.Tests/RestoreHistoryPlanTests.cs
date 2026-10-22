using System.Globalization;
using Yabt.Common;
using Yabt.Sync.Implementation;

namespace Yabt.Sync.Tests;

[TestClass]
public sealed class RestoreHistoryPlanTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ExactObjectsAndFoldersBlockTheSameDesiredPath(bool isFolder)
    {
        using var plan = new RestoreHistoryPlan(StringComparer.Ordinal);
        var move = new RestoreHistoryMove("item", isFolder);
        plan.Add(move);

        CollectionAssert.AreEqual(new[] { move }, plan.GetBlockers("item").ToArray());
        Assert.IsEmpty(plan.GetBlockers("item-sibling"));
        Assert.IsEmpty(plan.GetBlockers("item-sibling/file"));
        Assert.AreEqual(isFolder ? 0 : 1, plan.GetBlockers("item/child").Count);
    }

    [TestMethod]
    public void BlockersFollowOriginalMoveOrderInsteadOfAncestorTraversalOrder()
    {
        using var plan = new RestoreHistoryPlan(StringComparer.Ordinal);
        RestoreHistoryMove[] moves =
        [
            new("root/child", IsFolder: false),
            new("unrelated", IsFolder: false),
            new("root", IsFolder: false),
            new("root/child/item", IsFolder: true)
        ];
        foreach (var move in moves) { plan.Add(move); }

        CollectionAssert.AreEqual
        (
            new[] { moves[0], moves[2], moves[3] },
            plan.GetBlockers("root/child/item").ToArray()
        );
        Assert.IsEmpty(plan.GetBlockers("root-sibling/child/item"));
        Assert.IsEmpty(plan.GetBlockers("unrelated-child"));
    }

    [TestMethod]
    public void AncestorFoldersAndDescendantsDoNotBlockAFile()
    {
        using var plan = new RestoreHistoryPlan(StringComparer.Ordinal);
        plan.Add(new("parent", IsFolder: true));
        plan.Add(new("parent/child", IsFolder: true));
        plan.Add(new("parent/child/file/descendant", IsFolder: false));
        plan.Add(new("parent/child/file-sibling", IsFolder: false));

        Assert.IsEmpty(plan.GetBlockers("parent/child/file"));
    }

    [TestMethod]
    public void CompletionChangesPendingEvidenceWithoutChangingTheOrderedPlan()
    {
        using var plan = new RestoreHistoryPlan(StringComparer.Ordinal);
        RestoreHistoryMove[] moves =
        [
            new("second", IsFolder: false),
            new("first", IsFolder: true),
            new("third", IsFolder: false)
        ];
        foreach (var move in moves) { plan.Add(move); }
        plan.MarkCompleted(moves[1]);

        Assert.AreEqual(3, plan.Count);
        CollectionAssert.AreEqual(moves, plan.ToArray());
        CollectionAssert.AreEqual(new[] { moves[0], moves[2] }, plan.EnumeratePending().ToArray());
        Assert.IsEmpty(plan.GetBlockers("first"));
        Assert.HasCount(1, plan.GetBlockers("second"));

        // Deferred execution completes records while traversing the immutable
        // sequence. Mutating a dictionary-backed enumerator would fail here.
        foreach (var move in plan.EnumeratePending()) { plan.MarkCompleted(move); }

        Assert.IsEmpty(plan.EnumeratePending());
        Assert.IsEmpty(plan.GetBlockers("second"));
        CollectionAssert.AreEqual(moves, plan.ToArray());
        plan.MarkCompleted(moves[0]);
        Assert.IsEmpty(plan.EnumeratePending());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void BlockersUseProviderCaseComparisonAndRetainOriginalSpelling(bool ignoreCase)
    {
        using var plan = new RestoreHistoryPlan
        (
            ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal
        );
        var move = new RestoreHistoryMove("Folder/R\u00e9sum\u00e9", IsFolder: false);
        plan.Add(move);

        var blockers = plan.GetBlockers("folder/R\u00c9SUM\u00c9/child");
        Assert.AreEqual(ignoreCase ? 1 : 0, blockers.Count);
        Assert.IsEmpty(plan.GetBlockers("Folder/Resume/child"));
        if (ignoreCase)
        {
            Assert.AreEqual(move, blockers[0]);
            plan.MarkCompleted(blockers[0]);
            Assert.IsEmpty(plan.GetBlockers("FOLDER/r\u00e9sum\u00e9/child"));
        }
    }

    [TestMethod]
    public void ProviderAccentComparisonAlsoAppliesToAncestorLookup()
    {
        var comparer = StringComparer.Create
        (
            CultureInfo.InvariantCulture,
            CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace
        );
        using var plan = new RestoreHistoryPlan(comparer);
        var move = new RestoreHistoryMove("R\u00e9sum\u00e9", IsFolder: false);
        plan.Add(move);

        CollectionAssert.AreEqual(new[] { move }, plan.GetBlockers("RESUME/child").ToArray());
        CollectionAssert.AreEqual(new[] { move }, plan.GetBlockers("re\u0301sume\u0301").ToArray());
        Assert.IsEmpty(plan.GetBlockers("resume-extra/child"));
        plan.MarkCompleted(plan.GetBlockers("resume")[0]);
        Assert.IsEmpty(plan.GetBlockers("R\u00e9sum\u00e9/child"));
    }

    [TestMethod]
    public void HashCollisionsNeverMakeDistinctPathsEquivalent()
    {
        using var plan = new RestoreHistoryPlan(new ConstantHashComparer());
        RestoreHistoryMove[] moves =
        [
            new("first", IsFolder: false),
            new("second", IsFolder: true),
            new("third/nested", IsFolder: false)
        ];
        foreach (var move in moves) { plan.Add(move); }

        CollectionAssert.AreEqual(new[] { moves[0] }, plan.GetBlockers("FIRST/child").ToArray());
        CollectionAssert.AreEqual(new[] { moves[1] }, plan.GetBlockers("SECOND").ToArray());
        CollectionAssert.AreEqual(new[] { moves[2] }, plan.GetBlockers("THIRD/NESTED/file").ToArray());
        Assert.IsEmpty(plan.GetBlockers("unknown/path"));
        plan.MarkCompleted(moves[0]);
        Assert.IsEmpty(plan.GetBlockers("first/child"));
        CollectionAssert.AreEqual(new[] { moves[1], moves[2] }, plan.EnumeratePending().ToArray());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void DuplicateEquivalentMovesFailWithoutReplacingTheOriginal(bool ignoreCase)
    {
        using var plan = new RestoreHistoryPlan
        (
            ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal
        );
        var original = new RestoreHistoryMove("Original", IsFolder: false);
        plan.Add(original);

        Assert.ThrowsExactly<YabtSyncException>(() =>
            plan.Add(new(ignoreCase ? "ORIGINAL" : "Original", IsFolder: true)));

        Assert.AreEqual(1, plan.Count);
        CollectionAssert.AreEqual(new[] { original }, plan.EnumeratePending().ToArray());
        CollectionAssert.AreEqual(new[] { original }, plan.GetBlockers("Original/child").ToArray());
    }

    [TestMethod]
    public void OrdinalNamesKeepCaseAccentsAndWhitespaceDistinct()
    {
        using var plan = new RestoreHistoryPlan(StringComparer.Ordinal);
        string[] paths = ["A", "a", "\u00e1", "a\u0301", "a ", " a", " "];
        foreach (var path in paths) { plan.Add(new(path, IsFolder: false)); }

        Assert.AreEqual(paths.Length, plan.Count);
        foreach (var path in paths)
        {
            var blockers = plan.GetBlockers(path + "/file");
            Assert.HasCount(1, blockers);
            Assert.AreEqual(path, blockers[0].RelativePath);
        }
    }

    [TestMethod]
    public void LookupAndPendingEnumerationObserveCancellation()
    {
        using var plan = new RestoreHistoryPlan(StringComparer.Ordinal);
        plan.Add(new("first", IsFolder: false));
        plan.Add(new("second", IsFolder: false));
        using var cancellation = new CancellationTokenSource();
        using var enumerator = plan.EnumeratePending(cancellation.Token).GetEnumerator();
        Assert.IsTrue(enumerator.MoveNext());
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => enumerator.MoveNext());
        Assert.Throws<OperationCanceledException>(() =>
            plan.GetBlockers("first/nested/file", cancellation.Token));
        Assert.Throws<OperationCanceledException>(() =>
            plan.EnumeratePending(cancellation.Token).ToArray());
        Assert.AreEqual(2, plan.Count);
    }

    [TestMethod]
    public void OwnedIndexSurvivesOperationScopeAndIsReleasedAfterItsLastPlan()
    {
        var directory = Directory.CreateTempSubdirectory("yabt-history-plan-");
        try
        {
            RestoreHistoryPlan first;
            RestoreHistoryPlan second;
            using (TemporaryIndex.BeginOperation(new() { TemporaryDirectory = directory.FullName }))
            {
                first = new(StringComparer.Ordinal);
                second = new(StringComparer.Ordinal);
                first.Add(new("first", IsFolder: false));
                second.Add(new("second", IsFolder: false));
                Assert.HasCount(1, directory.GetFiles("*.sqlite"));
            }

            try
            {
                first.Dispose();
                Assert.HasCount(1, directory.GetFiles("*.sqlite"));
                Assert.HasCount(1, second.GetBlockers("second"));
                Assert.ThrowsExactly<ObjectDisposedException>(() => first.GetBlockers("first"));
            }
            finally
            {
                first.Dispose();
                second.Dispose();
            }

            Assert.IsEmpty(directory.GetFileSystemInfos());
            Assert.ThrowsExactly<ObjectDisposedException>(() => { _ = second.Count; });
        }
        finally { directory.Delete(recursive: true); }
    }

    private sealed class ConstantHashComparer : StringComparer
    {
        public override int Compare(string? x, string? y) => StringComparer.OrdinalIgnoreCase.Compare(x, y);

        public override bool Equals(string? x, string? y) => StringComparer.OrdinalIgnoreCase.Equals(x, y);

        public override int GetHashCode(string obj) => 0;
    }
}
