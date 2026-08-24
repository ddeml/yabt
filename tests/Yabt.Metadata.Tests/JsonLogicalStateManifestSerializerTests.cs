using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Yabt.Core.Models;
using Yabt.Metadata;

namespace Yabt.Metadata.Tests;

[TestClass]
public sealed class JsonLogicalStateManifestSerializerTests
{
    [TestMethod]
    public async Task CreateAndWriteAsyncProducesDeterministicCanonicalJson()
    {
        var serializer = CreateSerializer();
        var firstEntry = CreateEntry
        (
            "folder\\b.txt",
            "second",
            new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero)
        );
        var secondEntry = CreateEntry
        (
            "folder/a.txt",
            "first",
            new DateTimeOffset(2026, 8, 24, 11, 0, 0, TimeSpan.Zero)
        );

        var forwardManifest = serializer.Create([firstEntry, secondEntry]);
        var reverseManifest = serializer.Create([secondEntry, firstEntry]);
        var forwardJson = await WriteToStringAsync(serializer, forwardManifest);
        var reverseJson = await WriteToStringAsync(serializer, reverseManifest);

        Assert.AreEqual(forwardManifest.ManifestHash, reverseManifest.ManifestHash);
        Assert.AreEqual(forwardJson, reverseJson);
        CollectionAssert.AreEqual
        (
            new[] { "folder/a.txt", "folder/b.txt" },
            forwardManifest.Entries
                .Select(entry => entry.LogicalRelativePath)
                .ToArray()
        );
        Assert.IsTrue(forwardManifest.ManifestHash.StartsWith("xxh128:", StringComparison.Ordinal));
        StringAssert.Contains(forwardJson, "\"documentType\": \"yabt.logicalStateManifest\"");
        StringAssert.Contains(forwardJson, "\"schemaVersion\": 1");
        StringAssert.Contains(forwardJson, "\"logicalRelativePath\": \"folder/a.txt\"");
        StringAssert.Contains(forwardJson, "\"statFingerprint\": \"stat-v1:");
        StringAssert.Contains(forwardJson, "\"contentHash\": \"xxh128:");
        Assert.IsFalse(forwardJson.Contains("contentLength", StringComparison.Ordinal));
        Assert.IsFalse(forwardJson.Contains("lastModifiedUtc", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ReadAsyncRoundTripsValidatedManifest()
    {
        var serializer = CreateSerializer();
        var entry = CreateEntry
        (
            "folder/file.txt",
            "contents",
            new DateTimeOffset(2026, 8, 24, 12, 34, 56, TimeSpan.Zero)
        );
        var manifest = serializer.Create([entry]);
        await using var stream = new MemoryStream();
        await serializer.WriteAsync(manifest, stream);
        stream.Position = 0;

        var restored = await serializer.ReadAsync(stream);
        var restoredEntry = restored.Entries.Single();

        Assert.AreEqual(manifest.ManifestHash, restored.ManifestHash);
        Assert.AreEqual(entry.LogicalRelativePath, restoredEntry.LogicalRelativePath);
        Assert.AreEqual(entry.StatFingerprint, restoredEntry.StatFingerprint);
        Assert.AreEqual(entry.ContentHash, restoredEntry.ContentHash);
    }

    [TestMethod]
    public async Task ReadAsyncRejectsUnknownProperties()
    {
        var serializer = CreateSerializer();
        var manifest = serializer.Create([]);
        var json = await WriteToStringAsync(serializer, manifest);
        var changedJson = json.Replace(
            "\"schemaVersion\": 1,",
            "\"schemaVersion\": 1,\r\n  \"unknown\": true,",
            StringComparison.Ordinal);
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(changedJson));

        var exception = await Assert.ThrowsAsync<YabtMetadataException>
        (
            () => serializer.ReadAsync(stream)
        );

        StringAssert.Contains(exception.Message, "could not be deserialized");
    }

    [TestMethod]
    public async Task ReadAsyncRejectsDuplicateJsonProperties()
    {
        var serializer = CreateSerializer();
        var manifest = serializer.Create([]);
        var json = await WriteToStringAsync(serializer, manifest);
        var duplicateJson = json.Replace
        (
            "\"schemaVersion\": 1",
            "\"schemaVersion\": 1,\n  \"schemaVersion\": 1",
            StringComparison.Ordinal
        );
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(duplicateJson));

        await Assert.ThrowsAsync<YabtMetadataException>(
            () => serializer.ReadAsync(stream));
    }

    [TestMethod]
    public async Task ReadAsyncRejectsUnsupportedDocumentType()
    {
        var serializer = CreateSerializer();
        var manifest = serializer.Create([]);
        var json = await WriteToStringAsync(serializer, manifest);
        var changedJson = json.Replace(
            ArchiveLogicalStateManifest.ExpectedDocumentType,
            "yabt.changeManifest",
            StringComparison.Ordinal);
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(changedJson));

        var exception = await Assert.ThrowsAsync<YabtMetadataException>
        (
            () => serializer.ReadAsync(stream)
        );

        StringAssert.Contains(exception.Message, "unexpected document type");
    }

    [TestMethod]
    public async Task ReadAsyncRejectsUnsupportedSchemaVersion()
    {
        var serializer = CreateSerializer();
        var manifest = serializer.Create([]);
        var json = await WriteToStringAsync(serializer, manifest);
        var changedJson = json.Replace(
            "\"schemaVersion\": 1",
            "\"schemaVersion\": 2",
            StringComparison.Ordinal);
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(changedJson));

        var exception = await Assert.ThrowsAsync<YabtMetadataException>
        (
            () => serializer.ReadAsync(stream)
        );

        StringAssert.Contains(exception.Message, "unsupported schema version");
    }

    [TestMethod]
    public async Task ReadAsyncRejectsChangedContents()
    {
        var serializer = CreateSerializer();
        var originalHash = CreateContentHash("original");
        var changedHash = CreateContentHash("changed");
        var manifest = serializer.Create
        ([
            new
            (
                "file.txt",
                ArchiveChangeFingerprint.Create(
                    8,
                    new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero)),
                originalHash
            ),
        ]);
        var json = await WriteToStringAsync(serializer, manifest);
        var changedJson = json.Replace(originalHash, changedHash, StringComparison.Ordinal);
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(changedJson));

        var exception = await Assert.ThrowsAsync<YabtMetadataException>
        (
            () => serializer.ReadAsync(stream)
        );

        StringAssert.Contains(exception.Message, "self-hash");
    }

    [DataRow("")]
    [DataRow("provider:abc")]
    [DataRow("stat-v1:2026-08-24T12:00:00.0000000+01:00:7")]
    [DataRow("stat-v1:2026-08-24T12:00:00.0000000Z:-1")]
    [TestMethod]
    public void CreateRejectsNoncanonicalStatFingerprint(string statFingerprint)
    {
        var serializer = CreateSerializer();

        var exception = Assert.Throws<YabtMetadataException>
        (
            () => serializer.Create
            ([
                new("file.txt", statFingerprint, CreateContentHash("content")),
            ])
        );

        StringAssert.Contains(exception.Message, "canonical stat-v1 fingerprint");
    }

    [DataRow("")]
    [DataRow("md5:11111111111111111111111111111111")]
    [DataRow("xxh128:not-a-hash")]
    [DataRow("xxh128:AAAAAAAAAAAAAAAAAAAAAB")]
    [TestMethod]
    public void CreateRejectsInvalidContentHash(string contentHash)
    {
        var serializer = CreateSerializer();

        var exception = Assert.Throws<YabtMetadataException>
        (
            () => serializer.Create
            ([
                new
                (
                    "file.txt",
                    ArchiveChangeFingerprint.Create(
                        7,
                        new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero)),
                    contentHash
                ),
            ])
        );

        StringAssert.Contains(exception.Message, "valid xxHash128 hash");
    }

    [TestMethod]
    public void CreateRejectsDuplicateNormalizedPaths()
    {
        var serializer = CreateSerializer();
        var first = CreateEntry
        (
            "folder/file.txt",
            "first",
            new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero)
        );
        var second = CreateEntry
        (
            "folder\\file.txt",
            "second",
            new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero)
        );

        var exception = Assert.Throws<YabtMetadataException>
        (
            () => serializer.Create([first, second])
        );

        StringAssert.Contains(exception.Message, "duplicate entry path");
    }

    [TestMethod]
    public async Task WriteAsyncRejectsNoncanonicalEntryOrder()
    {
        var serializer = CreateSerializer();
        var canonicalManifest = serializer.Create
        ([
            CreateEntry
            (
                "a.txt",
                "first",
                new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero)
            ),
            CreateEntry
            (
                "b.txt",
                "second",
                new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero)
            ),
        ]);
        var noncanonicalManifest = canonicalManifest with
        {
            Entries = canonicalManifest.Entries.Reverse(),
        };
        await using var destination = new MemoryStream();

        var exception = await Assert.ThrowsAsync<YabtMetadataException>
        (
            () => serializer.WriteAsync(noncanonicalManifest, destination)
        );

        StringAssert.Contains(exception.Message, "canonical path order");
    }

    private static ILogicalStateManifestSerializer CreateSerializer()
    {
        var services = new ServiceCollection();
        services.AddYabtMetadata();

        return services.BuildServiceProvider()
            .GetRequiredService<ILogicalStateManifestSerializer>();
    }

    private static ArchiveLogicalStateManifestEntry CreateEntry
    (
        string logicalRelativePath,
        string content,
        DateTimeOffset lastModifiedUtc
    ) => new
    (
        logicalRelativePath,
        ArchiveChangeFingerprint.Create(
            Encoding.UTF8.GetByteCount(content),
            lastModifiedUtc),
        CreateContentHash(content)
    );

    private static async Task<string> WriteToStringAsync
    (
        ILogicalStateManifestSerializer serializer,
        ArchiveLogicalStateManifest manifest
    )
    {
        await using var stream = new MemoryStream();
        await serializer.WriteAsync(manifest, stream);

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string CreateContentHash(string value)
        => ArchiveHash.Compute(Encoding.UTF8.GetBytes(value));
}
