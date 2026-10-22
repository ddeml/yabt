using System.Text.Json;

namespace Yabt.Common.Tests;

[TestClass]
public sealed class TemporaryIndexTests
{
    [TestMethod]
    public void DictionaryPreservesOrdinalIdentityAndExactUtf16Keys()
    {
        using var index = TemporaryIndex.Create();
        using var values = index.CreateDictionary<int>();
        string[] keys = ["A", "a", "a ", "á", "a\u0301", "\U00010000", "\uE000", "\uD800", "\0", ""];
        for (var position = 0; position < keys.Length; position++) { values.Add(keys[position], position); }
        Assert.AreEqual(keys.Length, values.Count);
        CollectionAssert.AreEqual(keys.Order(StringComparer.Ordinal).ToArray(), values.Keys.ToArray());
        for (var position = 0; position < keys.Length; position++) { Assert.AreEqual(position, values[keys[position]]); }
        Assert.IsFalse(values.TryAdd("A", 42));
        Assert.Throws<ArgumentException>(() => values.Add("A", 42));
        Assert.AreEqual(0, values["A"]);
        values["A"] = 42;
        Assert.AreEqual(42, values["A"]);
        Assert.IsTrue(values.Remove("A", out var removed));
        Assert.AreEqual(42, removed);
        Assert.IsFalse(values.ContainsKey("A"));
    }

    [TestMethod]
    public void SequenceReplaysAndSortsWithoutChangingInsertionOrder()
    {
        using var index = TemporaryIndex.Create();
        using var entries = index.CreateCollection<TestRecord>();
        entries.Add(new("second", 2), "b");
        entries.Add(new("first", 1), "a");
        entries.Add(new("third", 3), "b");
        CollectionAssert.AreEqual(new[] { 2, 1, 3 }, entries.Select(entry => entry.Number).ToArray());
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, entries.EnumerateOrdered().Select(entry => entry.Number).ToArray());
        Assert.AreEqual(3L, entries.LongCount);
        entries.Set(0, new("updated", 4), "c");
        Assert.AreEqual("updated", entries.Get(0).Name);
        CollectionAssert.AreEqual(new[] { 1, 3, 4 }, entries.EnumerateOrdered().Select(entry => entry.Number).ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => entries.Get(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => entries.Get(3));
    }

    [TestMethod]
    public void DisposingOneCollectionDoesNotAffectOtherCollections()
    {
        using var index = TemporaryIndex.Create();
        var first = index.CreateDictionary<int>();
        using var second = index.CreateDictionary<string>();
        first.Add("same", 1);
        second.Add("same", "other");
        first.Dispose();
        first.Dispose();
        Assert.AreEqual("other", second["same"]);
        Assert.Throws<ObjectDisposedException>(() => first.ContainsKey("same"));
    }

    [TestMethod]
    public async Task EnumerationCanCrossAwaitAndReadAnotherCollection()
    {
        using var index = TemporaryIndex.Create();
        using var sequence = index.CreateCollection<int>();
        using var values = index.CreateDictionary<int>();
        for (var number = 0; number < 10; number++) { sequence.Add(number); values.Add(number.ToString(), number); }
        var total = 0;
        foreach (var number in sequence)
        {
            await Task.Yield();
            total += values[number.ToString()];
        }

        Assert.AreEqual(45, total);
    }

    [TestMethod]
    public void EnumeratorsRejectMutationAndRespectCancellation()
    {
        using var index = TemporaryIndex.Create();
        using var values = index.CreateDictionary<int>();
        values.Add("a", 1);
        values.Add("b", 2);
        using var enumerator = values.GetEnumerator();
        Assert.IsTrue(enumerator.MoveNext());
        values["b"] = 3;
        Assert.Throws<InvalidOperationException>(() => enumerator.MoveNext());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => values.Enumerate(cancellation.Token).ToArray());
    }

    [TestMethod]
    public void SetOperationsUseDistinctOrdinalValues()
    {
        using var index = TemporaryIndex.Create();
        using var values = index.CreateSet();
        values.UnionWith(["a", "b", "b", "B"]);
        Assert.IsTrue(values.SetEquals(["B", "b", "a", "a"]));
        Assert.IsTrue(values.IsProperSubsetOf(["a", "b", "B", "c"]));
        Assert.IsTrue(values.IsProperSupersetOf(["a", "a"]));
        values.IntersectWith(["a", "B"]);
        Assert.IsTrue(values.SetEquals(["a", "B"]));
        values.SymmetricExceptWith(["a", "c", "c"]);
        Assert.IsTrue(values.SetEquals(["B", "c"]));
        values.ExceptWith(values);
        Assert.AreEqual(0, values.Count);
    }

    [TestMethod]
    public void OperationSharesOneDatabaseAndFinalLeaseControlsCleanup()
    {
        var directory = CreateDirectory();
        try
        {
            var scope = TemporaryIndex.BeginOperation(new() { TemporaryDirectory = directory });
            var first = TemporaryIndex.Create();
            var second = TemporaryIndex.Create();
            using var entries = second.CreateCollection<int>();
            entries.Add(42);
            Assert.HasCount(1, Directory.GetFiles(directory, "*.sqlite"));
            first.Dispose();
            scope.Dispose();
            Assert.HasCount(1, Directory.GetFiles(directory, "*.sqlite"));
            Assert.AreEqual(42, entries[0]);
            second.Dispose();
            Assert.IsEmpty(Directory.GetFiles(directory));
            Assert.Throws<ObjectDisposedException>(() => entries.Get(0));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public async Task AmbientOperationFlowsToParallelTasksAndNestedScopesRestoreParent()
    {
        var directory = CreateDirectory();
        try
        {
            using (TemporaryIndex.BeginOperation(new() { TemporaryDirectory = directory }))
            {
                await Task.WhenAll(Enumerable.Range(0, 5).Select(async number =>
                {
                    using var index = TemporaryIndex.Create();
                    using var entries = index.CreateCollection<int>();
                    entries.Add(number);
                    await Task.Yield();
                    Assert.AreEqual(number, entries[0]);
                }));
                Assert.HasCount(1, Directory.GetFiles(directory, "*.sqlite"));
                using (TemporaryIndex.BeginOperation(new() { TemporaryDirectory = directory }))
                {
                    using var inner = TemporaryIndex.Create();
                    Assert.HasCount(2, Directory.GetFiles(directory, "*.sqlite"));
                }

                using var restoredParent = TemporaryIndex.Create();
                Assert.HasCount(1, Directory.GetFiles(directory, "*.sqlite"));
            }

            Assert.IsEmpty(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public void LargeSequenceSpillsBeyondTheBoundedPageCacheAndReplaysInOrder()
    {
        var directory = CreateDirectory();
        try
        {
            using (var index = TemporaryIndex.Create(new() { TemporaryDirectory = directory }))
            {
                using var values = index.CreateCollection<TestRecord>();
                for (var number = 15000; number >= 0; number--)
                {
                    values.Add(new(new string('x', 512), number), number.ToString("D8"));
                }

                var database = new FileInfo(Directory.GetFiles(directory, "*.sqlite").Single());
                Assert.IsGreaterThan((long)TemporaryIndex.PageCacheBytes, database.Length);
                var expected = 0;
                foreach (var value in values.EnumerateOrdered()) { Assert.AreEqual(expected++, value.Number); }
                Assert.AreEqual(15001, expected);
            }

            Assert.IsEmpty(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public void RecordSerializationFailureDoesNotInsertAPartialRow()
    {
        using var index = TemporaryIndex.Create();
        using var entries = index.CreateCollection<object>();
        Assert.Throws<NotSupportedException>(() => entries.Add((Action)(() => { })));
        Assert.AreEqual(0, entries.Count);
        entries.Add("valid");
        Assert.AreEqual("valid", ((JsonElement)entries[0]).GetString());
    }

    [TestMethod]
    public void OperationDoesNotAllocateBeforeTheFirstLeaseAndValidatesBeforeFileCreation()
    {
        var directory = CreateDirectory();
        try
        {
            using var operation = TemporaryIndex.BeginOperation(new() { TemporaryDirectory = directory });
            Assert.IsEmpty(Directory.GetFiles(directory));
            using var validation = TemporaryStorageValidation.BeginOperation
            (
                _ => throw new IOException("Temporary location is unsafe.")
            );

            Assert.ThrowsExactly<IOException>(() => TemporaryIndex.Create());

            Assert.IsEmpty(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory); }
    }

    private static string CreateDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"yabt-index-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed record TestRecord(string Name, int Number);
}
