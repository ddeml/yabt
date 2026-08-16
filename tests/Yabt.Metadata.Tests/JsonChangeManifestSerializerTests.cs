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
            20,
            new DateTimeOffset(2026, 8, 16, 14, 0, 0, TimeSpan.FromHours(2)),
            "yabt-stat-v1-xxh128:22222222222222222222222222222222",
            CreateContentHash("second")
        );
        var secondEntry = new ArchiveChangeManifestEntry
        (
            "folder/a.txt",
            10,
            new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero),
            "yabt-stat-v1-xxh128:11111111111111111111111111111111",
            CreateContentHash("first")
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
        StringAssert.Contains(forwardJson, "\"lastModifiedUtc\": \"2026-08-16T12:00:00+00:00\"");
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
                7,
                LastModifiedUtc: null,
                "xxh128:11111111111111111111111111111111",
                CreateContentHash("content")
            ),
        ]);
        await using var json = new MemoryStream();
        await serializer.WriteAsync(manifest, json);
        json.Position = 0;

        var restored = await serializer.ReadAsync(json);
        var entry = restored.Entries.Single();

        Assert.AreEqual(manifest.ManifestHash, restored.ManifestHash);
        Assert.AreEqual("file.txt", entry.RelativePath);
        Assert.AreEqual(7, entry.Length);
        Assert.IsNull(entry.LastModifiedUtc);
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
                8,
                DateTimeOffset.Parse("2026-08-16T12:00:00Z"),
                "xxh128:11111111111111111111111111111111",
                originalContentHash
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
                8,
                DateTimeOffset.Parse("2026-08-16T12:00:00Z"),
                "xxh128:11111111111111111111111111111111",
                "not-qualified"
            ),
        ]));

        StringAssert.Contains(exception.Message, "content hash");
    }

    [TestMethod]
    public void CreateRejectsUnsupportedArtifactContentHash()
    {
        var serializer = CreateSerializer();
        string[] invalidHashes =
        [
            "md5:11111111111111111111111111111111",
            "xxh128:not-a-hash",
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
                    8,
                    DateTimeOffset.Parse("2026-08-16T12:00:00Z"),
                    "xxh128:11111111111111111111111111111111",
                    invalidHash
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
                8,
                DateTimeOffset.Parse("2026-08-16T12:00:00Z"),
                providerFingerprint,
                CreateContentHash("content")
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
                7,
                DateTimeOffset.Parse("2026-08-16T12:00:00Z"),
                "xxh128:11111111111111111111111111111111",
                contentHash
            ),
            new
            (
                "folder\\file.txt",
                7,
                DateTimeOffset.Parse("2026-08-16T12:00:00Z"),
                "xxh128:11111111111111111111111111111111",
                contentHash
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
