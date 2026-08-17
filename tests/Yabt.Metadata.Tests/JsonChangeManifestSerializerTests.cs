using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Yabt.Core.Models;
using Yabt.Metadata;

namespace Yabt.Metadata.Tests;

[TestClass]
public sealed class JsonChangeManifestSerializerTests
{
    [TestMethod]
    public async Task CreateAndWriteAsyncProducesDeterministicCanonicalJson()
    {
        var serializer = CreateSerializer();
        var firstEntry = new ArchiveChangeManifestEntry
        (
            "folder\\b.txt",
            "stat-v1:2026-08-16T12:00:00.0000000Z:20",
            ContentHash: CreateContentHash("second")
        );
        var secondEntry = new ArchiveChangeManifestEntry
        (
            "folder/a.txt",
            "stat-v1:2026-08-16T12:00:00.0000000Z:10",
            ContentHash: CreateContentHash("first")
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
            forwardManifest.Entries.Select(entry => entry.RelativePath).ToArray()
        );
        Assert.IsTrue(forwardManifest.ManifestHash.StartsWith("xxh128:", StringComparison.Ordinal));
        StringAssert.Contains(forwardJson, "\"documentType\": \"yabt.changeManifest\"");
        Assert.IsFalse(forwardJson.Contains("artifactLength", StringComparison.Ordinal));
        Assert.IsFalse(forwardJson.Contains("\"length\"", StringComparison.Ordinal));
        Assert.IsFalse(forwardJson.Contains("lastModifiedUtc", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ReadAsyncRoundTripsValidatedManifest()
    {
        var serializer = CreateSerializer();
        var manifest = serializer.Create
        ([
            new
            (
                "file.txt",
                ArchiveHash.Compute(Encoding.UTF8.GetBytes("logical input")),
                ArtifactLength: 7,
                ContentHash: CreateContentHash("content")
            ),
        ]);
        await using var json = new MemoryStream();
        await serializer.WriteAsync(manifest, json);
        json.Position = 0;

        var restored = await serializer.ReadAsync(json);
        var entry = restored.Entries.Single();

        Assert.AreEqual(manifest.ManifestHash, restored.ManifestHash);
        Assert.AreEqual("file.txt", entry.RelativePath);
        Assert.AreEqual(7, entry.ArtifactLength);
        Assert.AreEqual(CreateContentHash("content"), entry.ContentHash);
    }

    [TestMethod]
    public async Task ReadAsyncRejectsManifestWhoseContentsWereChanged()
    {
        var serializer = CreateSerializer();
        var originalContentHash = CreateContentHash("original");
        var manifest = serializer.Create
        ([
            new
            (
                "file.txt",
                "stat-v1:2026-08-16T12:00:00.0000000Z:8",
                ContentHash: originalContentHash
            ),
        ]);
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
    public void CreateRejectsUnqualifiedArtifactContentHash()
    {
        var serializer = CreateSerializer();

        var exception = Assert.Throws<YabtMetadataException>(() => serializer.Create
        ([
            new
            (
                "file.txt",
                "stat-v1:2026-08-16T12:00:00.0000000Z:8",
                ContentHash: "not-qualified"
            ),
        ]));

        StringAssert.Contains(exception.Message, "content hash");
    }

    [TestMethod]
    public void CreateRejectsNegativeArtifactLength()
    {
        var serializer = CreateSerializer();

        var exception = Assert.Throws<YabtMetadataException>(() => serializer.Create
        ([
            new
            (
                "package.zip",
                ArchiveHash.Compute(Encoding.UTF8.GetBytes("logical input")),
                ArtifactLength: -1,
                ContentHash: CreateContentHash("content")
            ),
        ]));

        StringAssert.Contains(exception.Message, "negative artifact length");
    }

    [TestMethod]
    public void CreateRejectsUnsupportedArtifactContentHash()
    {
        var serializer = CreateSerializer();
        string[] invalidHashes =
        [
            "md5:11111111111111111111111111111111",
            "xxh128:not-a-hash",
            "xxh128:11111111111111111111111111111111",
            "xxh128:AAAAAAAAAAAAAAAAAAAAAB",
            "xxh128:AAAAAAAAAAAAAAAAAAAAA=",
            "xxh128:AAAAAAAAAAAAAAAAAAAAA+",
            "xxh128:AAAAAAAAAAAAAAAAAAAAA/",
            "sha256:11111111111111111111111111111111" +
                "11111111111111111111111111111111",
        ];

        foreach (var invalidHash in invalidHashes)
        {
            var exception = Assert.Throws<YabtMetadataException>(() => serializer.Create
            ([
                new
                (
                    "file.txt",
                    "stat-v1:2026-08-16T12:00:00.0000000Z:8",
                    ContentHash: invalidHash
                ),
            ]));

            StringAssert.Contains(exception.Message, "valid xxHash128 hash");
        }
    }

    [TestMethod]
    public async Task ReadAsyncAcceptsProviderQualifiedChangeFingerprint()
    {
        var serializer = CreateSerializer();
        const string providerFingerprint =
            "md5:11111111111111111111111111111111";
        var manifest = serializer.Create
        ([
            new
            (
                "file.txt",
                providerFingerprint,
                ContentHash: CreateContentHash("content")
            ),
        ]);
        await using var stream = new MemoryStream();
        await serializer.WriteAsync(manifest, stream);
        stream.Position = 0;

        var result = await serializer.ReadAsync(stream);

        Assert.AreEqual(providerFingerprint, result.Entries.Single().ChangeFingerprint);
    }

    [TestMethod]
    public void CreateRejectsDuplicateNormalizedPaths()
    {
        var serializer = CreateSerializer();
        var contentHash = CreateContentHash("content");

        var exception = Assert.Throws<YabtMetadataException>(() => serializer.Create
        ([
            new
            (
                "folder/file.txt",
                "stat-v1:2026-08-16T12:00:00.0000000Z:7",
                ContentHash: contentHash
            ),
            new
            (
                "folder\\file.txt",
                "stat-v1:2026-08-16T12:00:00.0000000Z:7",
                ContentHash: contentHash
            ),
        ]));

        StringAssert.Contains(exception.Message, "duplicate entry path");
    }

    private static IChangeManifestSerializer CreateSerializer()
    {
        var services = new ServiceCollection();
        services.AddYabtMetadata();

        return services.BuildServiceProvider().GetRequiredService<IChangeManifestSerializer>();
    }

    private static async Task<string> WriteToStringAsync
    (
        IChangeManifestSerializer serializer,
        ArchiveChangeManifest manifest
    )
    {
        await using var stream = new MemoryStream();
        await serializer.WriteAsync(manifest, stream);

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string CreateContentHash(string value)
        => ArchiveHash.Compute(Encoding.UTF8.GetBytes(value));
}
