using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yabt.Common.Tests;

[TestClass]
public sealed class TemporaryIndexComparisonTests
{
    [TestMethod]
    public void EquivalentKeysPreserveFirstSpellingDuringAddLookupAndUpdate()
    {
        using var index = TemporaryIndex.Create();
        using var values = index.CreateDictionary<int>(comparer: StringComparer.OrdinalIgnoreCase);
        values.Add("Report.TXT", 1);

        Assert.IsFalse(values.TryAdd("report.txt", 2));
        Assert.Throws<ArgumentException>(() => values.Add("REPORT.txt", 3));
        Assert.AreEqual(1, values["report.txt"]);
        values["REPORT.TXT"] = 4;

        Assert.AreEqual(1L, values.LongCount);
        Assert.AreEqual("Report.TXT", values.Keys.Single());
        Assert.AreEqual(4, values["report.txt"]);
        Assert.IsTrue(values.Remove("REPORT.txt", out var removed));
        Assert.AreEqual(4, removed);
        Assert.IsFalse(values.ContainsKey("report.txt"));
    }

    [TestMethod]
    public void HashCollisionsStillDistinguishNonEquivalentKeys()
    {
        using var index = TemporaryIndex.Create();
        using var values = index.CreateDictionary<string>(comparer: new ConstantHashIgnoreCaseComparer());
        values.Add("First", "first");
        values.Add("Second", "second");
        values["THIRD"] = "third";
        values["FIRST"] = "replacement";

        Assert.AreEqual(3L, values.LongCount);
        Assert.AreEqual("replacement", values["first"]);
        Assert.AreEqual("second", values["SECOND"]);
        Assert.AreEqual("third", values["third"]);
        Assert.IsTrue(values.Remove("second"));
        Assert.IsFalse(values.TryGetValue("SECOND", out _));
        Assert.AreEqual("replacement", values["first"]);
        Assert.AreEqual("third", values["third"]);
    }

    [TestMethod]
    public void EachCollectionKeepsItsOwnComparisonAndOrdinalEnumeration()
    {
        using var index = TemporaryIndex.Create();
        using var ordinal = index.CreateDictionary<int>();
        var insensitive = CultureInfo.GetCultureInfo("en-US").CompareInfo.
            GetStringComparer(CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace);
        using var equivalent = index.CreateDictionary<int>(comparer: insensitive);
        ordinal.Add("Résumé", 1);
        ordinal.Add("resume", 2);
        equivalent.Add("Résumé", 3);
        equivalent.Add("Alpha", 4);

        Assert.IsFalse(equivalent.TryAdd("RESUME", 5));
        Assert.AreEqual(3, equivalent["resume"]);
        Assert.AreEqual(2, ordinal["resume"]);
        Assert.AreEqual(2L, ordinal.LongCount);
        CollectionAssert.AreEqual(new[] { "Alpha", "Résumé" }, equivalent.Keys.ToArray());
    }

    [TestMethod]
    public void SetComparisonOperationsRetainProviderEqualityInTemporaryPeerSets()
    {
        using var index = TemporaryIndex.Create();
        using var set = index.CreateSet(StringComparer.OrdinalIgnoreCase);
        set.UnionWith(["Alpha", "Beta", "Gamma"]);

        Assert.IsTrue(set.SetEquals(["ALPHA", "beta", "gamma", "GAMMA"]));
        Assert.IsTrue(set.IsSupersetOf(["alpha", "BETA"]));
        Assert.IsTrue(set.IsProperSubsetOf(["alpha", "beta", "gamma", "delta"]));
        set.IntersectWith(["ALPHA", "GAMMA"]);
        CollectionAssert.AreEqual(new[] { "Alpha", "Gamma" }, set.ToArray());
        set.SymmetricExceptWith(["alpha", "DELTA", "delta"]);
        CollectionAssert.AreEqual(new[] { "DELTA", "Gamma" }, set.ToArray());
        set.ExceptWith(["GAMMA"]);
        Assert.AreEqual("DELTA", set.Single());
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    public void EphemeralStringValuesPreserveExactUtf16CodeUnits(int example)
    {
        using var index = TemporaryIndex.Create();
        using var entries = index.CreateCollection<string>();
        string[] examples =
        [
            "file-\ud800.txt", "file-\udc00.txt", "file-\U0001F680.txt",
            "~yabt-utf16:ANg=", "~yabt-utf16:not base64"
        ];
        var value = examples[example];
        entries.Add(value);

        Assert.IsTrue(string.Equals(value, entries.Single(), StringComparison.Ordinal));
    }

    [TestMethod]
    public void EphemeralRecordPathsPreserveExactUtf16CodeUnits()
    {
        using var index = TemporaryIndex.Create();
        using var entries = index.CreateCollection<PathRecord>();
        using var values = index.CreateDictionary<PathRecord>();
        var record = new PathRecord("file-\ud800.txt");
        entries.Add(record);
        values.Add("record", record);

        Assert.IsTrue(string.Equals(record.RelativePath, entries.Single().RelativePath, StringComparison.Ordinal));
        Assert.IsTrue(string.Equals(record.RelativePath, values["record"].RelativePath, StringComparison.Ordinal));
    }

    [TestMethod]
    public void EphemeralDictionaryStringKeysAndValuesCannotAliasTheEncodingPrefix()
    {
        using var index = TemporaryIndex.Create();
        using var entries = index.CreateCollection<Dictionary<string, string>>();
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["\ud800"] = "\udc00",
            ["~yabt-utf16:ANg="] = "~yabt-utf16:ANw=",
            ["ordinary"] = "ordinary"
        };
        entries.Add(values);

        var restored = entries.Single();
        Assert.AreEqual(3, restored.Count);
        foreach (var pair in values)
        {
            Assert.IsTrue(restored.TryGetValue(pair.Key, out var actual));
            Assert.IsTrue(string.Equals(pair.Value, actual, StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public void EphemeralRecordsRetainRequiredDefaultValuesRegardlessOfDurableOmissionRules()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
        };
        using var index = TemporaryIndex.Create();
        using var entries = index.CreateCollection<RequiredDefaultRecord>(options);
        using var values = index.CreateDictionary<RequiredDefaultRecord>(options);
        var record = new RequiredDefaultRecord(0, false, null);
        entries.Add(record);
        values.Add("record", record);

        Assert.AreEqual(record, entries.Single());
        Assert.AreEqual(record, values["record"]);
        Assert.AreEqual(JsonIgnoreCondition.WhenWritingDefault, options.DefaultIgnoreCondition);
    }

    private sealed record PathRecord(string RelativePath);

    private sealed record RequiredDefaultRecord
    (
        [property: JsonRequired] int Number,
        [property: JsonRequired] bool Flag,
        [property: JsonRequired] string? Optional
    );

    private sealed class ConstantHashIgnoreCaseComparer : IEqualityComparer<string>
    {
        public bool Equals(string? left, string? right) => StringComparer.OrdinalIgnoreCase.Equals(left, right);

        public int GetHashCode(string value) => 0;
    }
}
