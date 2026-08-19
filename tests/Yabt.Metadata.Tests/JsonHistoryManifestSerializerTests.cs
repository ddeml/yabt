using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Yabt.Core.Models;
using Yabt.Metadata;

namespace Yabt.Metadata.Tests;

[TestClass]
public sealed class JsonHistoryManifestSerializerTests
{
    [TestMethod]
    public async Task CreateAndWriteAsyncProducesDeterministicCanonicalJson()
    {
        var serializer = CreateSerializer();
        var firstEntry = CreateEntry(
            "2026-08-19\\folder\\b.txt",
            ArchiveHistoryEntryRepresentation.Reference,
            storedRelativePath: "2026-08-19/folder/b.txt.yabt-ref.json",
            metadata: new Dictionary<string, string>
            {
                ["z"] = "last",
                ["a"] = "first",
            });
        var secondEntry = CreateEntry("2026-08-19/folder/a.txt");

        var forwardManifest = serializer.Create([firstEntry, secondEntry]);
        var reverseManifest = serializer.Create([secondEntry, firstEntry]);
        var forwardJson = await WriteToStringAsync(serializer, forwardManifest);
        var reverseJson = await WriteToStringAsync(serializer, reverseManifest);

        Assert.AreEqual(forwardManifest.ManifestHash, reverseManifest.ManifestHash);
        Assert.AreEqual(forwardJson, reverseJson);
        CollectionAssert.AreEqual
        (
            new[] { "2026-08-19/folder/a.txt", "2026-08-19/folder/b.txt" },
            forwardManifest.Entries.Select(entry => entry.RelativePath).ToArray()
        );
        StringAssert.Contains(forwardJson, "\"documentType\": \"yabt.historyManifest\"");
        Assert.IsTrue(
            forwardJson.IndexOf("\"a\": \"first\"", StringComparison.Ordinal) <
                forwardJson.IndexOf("\"z\": \"last\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ReadAsyncRoundTripsAllRebuildableMetadata()
    {
        var serializer = CreateSerializer();
        var manifest = serializer.Create
        ([
            CreateEntry(
                "2026-08-19/file.txt",
                lastModifiedUtc: new DateTimeOffset(2026, 8, 19, 14, 0, 0, TimeSpan.FromHours(2)),
                contentType: "text/plain",
                metadata: new Dictionary<string, string> { ["origin"] = "source" }),
        ]);
        await using var json = new MemoryStream();
        await serializer.WriteAsync(manifest, json);
        json.Position = 0;

        var restored = await serializer.ReadAsync(json);
        var entry = restored.Entries.Single();

        Assert.AreEqual(manifest.ManifestHash, restored.ManifestHash);
        Assert.AreEqual("2026-08-19/file.txt", entry.StoredRelativePath);
        Assert.AreEqual(7, entry.ContentLength);
        Assert.AreEqual(CreateContentHash("content"), entry.ContentHash);
        Assert.AreEqual(new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero), entry.LastModifiedUtc);
        Assert.AreEqual("text/plain", entry.ContentType);
        Assert.AreEqual("source", entry.Metadata!["origin"]);
    }

    [TestMethod]
    public async Task ReadAsyncRoundTripsEmptyMaterializedObject()
    {
        var serializer = CreateSerializer();
        var manifest = serializer.Create
        ([
            CreateEntry("2026-08-19/empty.txt", contentLength: 0),
        ]);
        await using var json = new MemoryStream();

        await serializer.WriteAsync(manifest, json);
        var serializedJson = Encoding.UTF8.GetString(json.ToArray());
        json.Position = 0;
        var restored = await serializer.ReadAsync(json);

        Assert.AreEqual(0, restored.Entries.Single().ContentLength);
        StringAssert.Contains(serializedJson, "\"contentLength\": 0");
    }

    [TestMethod]
    public async Task ReadAsyncRejectsManifestWhoseContentsWereChanged()
    {
        var serializer = CreateSerializer();
        var originalContentHash = CreateContentHash("content");
        var manifest = serializer.Create([CreateEntry("2026-08-19/file.txt")]);
        var json = await WriteToStringAsync(serializer, manifest);
        var changedJson = json.Replace(
            originalContentHash,
            CreateContentHash("changed"),
            StringComparison.Ordinal);
        await using var changedStream = new MemoryStream(Encoding.UTF8.GetBytes(changedJson));

        var exception = await Assert.ThrowsAsync<YabtMetadataException>(
            () => serializer.ReadAsync(changedStream));

        StringAssert.Contains(exception.Message, "self-hash");
    }

    [TestMethod]
    public async Task ReadAsyncRejectsUnknownProperty()
    {
        var serializer = CreateSerializer();
        var manifest = serializer.Create([CreateEntry("2026-08-19/file.txt")]);
        var json = await WriteToStringAsync(serializer, manifest);
        var changedJson = json.Insert(
            json.IndexOf('{', StringComparison.Ordinal) + 1,
            "\"generatedAtUtc\":\"2026-08-19T12:00:00Z\",");
        await using var changedStream = new MemoryStream(Encoding.UTF8.GetBytes(changedJson));

        var exception = await Assert.ThrowsAsync<YabtMetadataException>(
            () => serializer.ReadAsync(changedStream));

        StringAssert.Contains(exception.ToString(), "generatedAtUtc");
    }

    [TestMethod]
    public async Task ReadAsyncRejectsUnsupportedSchemaVersion()
    {
        var serializer = CreateSerializer();
        var manifest = serializer.Create([CreateEntry("2026-08-19/file.txt")]);
        var json = await WriteToStringAsync(serializer, manifest);
        var changedJson = json.Replace(
            "\"schemaVersion\": 1",
            "\"schemaVersion\": 2",
            StringComparison.Ordinal);
        await using var changedStream = new MemoryStream(Encoding.UTF8.GetBytes(changedJson));

        var exception = await Assert.ThrowsAsync<YabtMetadataException>(
            () => serializer.ReadAsync(changedStream));

        StringAssert.Contains(exception.Message, "schema version");
    }

    [TestMethod]
    public void CreateRejectsUnsupportedRepresentation()
    {
        var serializer = CreateSerializer();
        var entry = CreateEntry("2026-08-19/file.txt") with
        {
            Representation = "deduplicated",
        };

        var exception = Assert.Throws<YabtMetadataException>(() => serializer.Create([entry]));

        StringAssert.Contains(exception.Message, "representation");
    }

    [TestMethod]
    public void CreateRejectsDuplicateStoredPaths()
    {
        var serializer = CreateSerializer();
        var firstEntry = CreateEntry(
            "2026-08-19/a.txt",
            ArchiveHistoryEntryRepresentation.Reference,
            storedRelativePath: "2026-08-19/a.txt.yabt-ref.json");
        var secondEntry = CreateEntry("2026-08-19/a.txt.yabt-ref.json");

        var exception = Assert.Throws<YabtMetadataException>(
            () => serializer.Create([firstEntry, secondEntry]));

        StringAssert.Contains(exception.Message, "duplicate stored path");
    }

    [TestMethod]
    public void CreateRejectsMaterializedEntryStoredAtDifferentPath()
    {
        var serializer = CreateSerializer();
        var entry = CreateEntry("2026-08-19/file.txt") with
        {
            StoredRelativePath = "2026-08-19/other.txt",
        };

        var exception = Assert.Throws<YabtMetadataException>(() => serializer.Create([entry]));

        StringAssert.Contains(exception.Message, "materialized paths");
    }

    [TestMethod]
    public void CreateRejectsReferenceStoredAtOriginalPath()
    {
        var serializer = CreateSerializer();
        var entry = CreateEntry(
            "2026-08-19/file.txt",
            ArchiveHistoryEntryRepresentation.Reference);

        var exception = Assert.Throws<YabtMetadataException>(() => serializer.Create([entry]));

        StringAssert.Contains(exception.Message, "must preserve the original path");
    }

    [TestMethod]
    public void CreateRejectsReferenceStoredUnderUnrelatedName()
    {
        var serializer = CreateSerializer();
        var entry = CreateEntry(
            "2026-08-19/file.txt",
            ArchiveHistoryEntryRepresentation.Reference,
            storedRelativePath: "2026-08-19/unrelated.yabt-ref.json");

        var exception = Assert.Throws<YabtMetadataException>(() => serializer.Create([entry]));

        StringAssert.Contains(exception.Message, "must preserve the original path");
    }

    [TestMethod]
    public void CreateRejectsHistoryManifestAsAnEntry()
    {
        var serializer = CreateSerializer();
        var entry = CreateEntry(ArchiveHistoryFileNames.Manifest);

        var exception = Assert.Throws<YabtMetadataException>(() => serializer.Create([entry]));

        StringAssert.Contains(exception.Message, "control metadata");
    }

    private static ArchiveHistoryManifestEntry CreateEntry
    (
        string relativePath,
        string representation = ArchiveHistoryEntryRepresentation.Materialized,
        string? storedRelativePath = default,
        DateTimeOffset? lastModifiedUtc = default,
        string? contentType = default,
        IReadOnlyDictionary<string, string>? metadata = default,
        long contentLength = 7
    ) => new
    (
        relativePath,
        storedRelativePath ?? relativePath,
        representation,
        contentLength,
        CreateContentHash("content"),
        lastModifiedUtc,
        contentType,
        metadata
    );

    private static IHistoryManifestSerializer CreateSerializer()
    {
        var services = new ServiceCollection();
        services.AddYabtMetadata();

        return services.BuildServiceProvider().GetRequiredService<IHistoryManifestSerializer>();
    }

    private static async Task<string> WriteToStringAsync
    (
        IHistoryManifestSerializer serializer,
        ArchiveHistoryManifest manifest
    )
    {
        await using var stream = new MemoryStream();
        await serializer.WriteAsync(manifest, stream);

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string CreateContentHash(string value) =>
        ArchiveHash.Compute(Encoding.UTF8.GetBytes(value));
}
