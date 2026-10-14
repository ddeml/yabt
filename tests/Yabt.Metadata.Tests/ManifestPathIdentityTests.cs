using Microsoft.Extensions.DependencyInjection;
using Yabt.Core.Models;

namespace Yabt.Metadata.Tests;

[TestClass]
public sealed class ManifestPathIdentityTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly string ContentHash = ArchiveHash.Compute("abc"u8);
    private static readonly string Fingerprint = ArchiveChangeFingerprint.Create(3, Timestamp);
    private static readonly string[] Paths =
    [
        " documents/report.txt",
        "documents/report.txt",
        "folder/file.txt ",
        "folder/file.txt",
        " / ",
        "\tfolder/\tfile.txt",
        "\u00a0folder/file\u00a0",
        "\nfolder/file\n",
    ];

    [TestMethod]
    public async Task LiveManifestPreservesWhitespaceInPathsAndProjectionProvenance()
    {
        using var provider = CreateServices();
        var serializer = provider.GetRequiredService<IChangeManifestSerializer>();
        var entries = Paths.Select
        (
            path => new ArchiveChangeManifestEntry
            (
                path,
                Fingerprint,
                ContentHash: ContentHash,
                Projection: new(path, "zip", 1, ContentHash, ArchiveProjectionArtifactRoles.Package)
            )
        );
        var manifest = serializer.Create(entries, "mirror");
        await using var json = new MemoryStream();
        await serializer.WriteAsync(manifest, json);
        json.Position = 0;

        var restored = await serializer.ReadAsync(json);

        AssertPreservedPaths(restored.Entries.Select(entry => entry.RelativePath));
        AssertPreservedPaths(restored.Entries.Select(entry => entry.Projection!.LogicalPath));
    }

    [TestMethod]
    public async Task LogicalStateManifestPreservesWhitespaceInPaths()
    {
        using var provider = CreateServices();
        var serializer = provider.GetRequiredService<ILogicalStateManifestSerializer>();
        var entries = Paths.Select(path => new ArchiveLogicalStateManifestEntry(path, Fingerprint, ContentHash));
        var manifest = serializer.Create(entries);
        await using var json = new MemoryStream();
        await serializer.WriteAsync(manifest, json);
        json.Position = 0;

        var restored = await serializer.ReadAsync(json);

        AssertPreservedPaths(restored.Entries.Select(entry => entry.LogicalRelativePath));
    }

    [TestMethod]
    public async Task HistoryManifestPreservesWhitespaceInLogicalAndStoredPaths()
    {
        using var provider = CreateServices();
        var serializer = provider.GetRequiredService<IHistoryManifestSerializer>();
        var entries = Paths.Select
        (
            path => new ArchiveHistoryManifestEntry
            (
                path,
                path,
                ArchiveHistoryEntryRepresentation.Materialized,
                3,
                ContentHash
            )
        );
        var manifest = serializer.Create(entries);
        await using var json = new MemoryStream();
        await serializer.WriteAsync(manifest, json);
        json.Position = 0;

        var restored = await serializer.ReadAsync(json);

        AssertPreservedPaths(restored.Entries.Select(entry => entry.RelativePath));
        AssertPreservedPaths(restored.Entries.Select(entry => entry.StoredRelativePath));
    }

    [TestMethod]
    public async Task HistoryReferencesPreserveWhitespaceInOriginalAndReferenceNames()
    {
        using var provider = CreateServices();
        var serializer = provider.GetRequiredService<IHistoryReferenceSerializer>();
        foreach (var path in Paths)
        {
            var referencePath = ArchiveHistoryFileNames.CreateReferencePath(path);
            Assert.AreEqual($"{path}.yabt-ref.json", referencePath);
            var reference = serializer.Create
            (
                new
                (
                    path,
                    referencePath,
                    ArchiveHistoryEntryRepresentation.Reference,
                    3,
                    ContentHash
                )
            );
            await using var json = new MemoryStream();
            await serializer.WriteAsync(reference, json);
            json.Position = 0;

            var restored = await serializer.ReadAsync(json);

            Assert.AreEqual(path, restored.Entry.RelativePath);
            Assert.AreEqual(referencePath, restored.Entry.StoredRelativePath);
        }
    }

    [TestMethod]
    public async Task PackageManifestPreservesWhitespaceInSourcePayloadAndNestedProjectionPaths()
    {
        using var provider = CreateServices();
        var serializer = provider.GetRequiredService<IManifestSerializer>();
        const string sourcePath = " parent / package ";
        const string packageName = " package .xxh128-00000000000000000000000000.zip";
        var entries = Paths.Select
        (
            path => new ArchiveManifestEntry
            (
                ArchiveManifestEntryKinds.FormatArtifact,
                path,
                path,
                3,
                Timestamp,
                ContentHash,
                new(path, "zip", 1, ContentHash, ArchiveProjectionArtifactRoles.Package)
            )
        );
        var manifest = serializer.Create
        (
            sourcePath,
            Timestamp,
            "zip",
            1,
            ContentHash,
            packageName,
            new("zip"),
            entries
        );
        await using var json = new MemoryStream();
        await serializer.WriteAsync(manifest, json);
        json.Position = 0;

        var restored = await serializer.ReadAsync(json);

        Assert.AreEqual(sourcePath, restored.SourcePath);
        Assert.AreEqual(packageName, restored.PackageName);
        AssertPreservedPaths(restored.Entries.Select(entry => entry.RelativePath));
        AssertPreservedPaths(restored.Entries.Select(entry => entry.StoredPath));
        AssertPreservedPaths(restored.Entries.Select(entry => entry.Projection!.LogicalPath));
    }

    [TestMethod]
    [DataRow("/file.txt")]
    [DataRow("C:\\file.txt")]
    [DataRow("folder\\file.txt")]
    [DataRow("folder//file.txt")]
    [DataRow("folder/./file.txt")]
    [DataRow("folder/../file.txt")]
    [DataRow("folder/file.txt/")]
    [DataRow("folder/\0file.txt")]
    public void ManifestCreationRejectsPathsThatWouldNeedStructuralRepair(string path)
    {
        using var provider = CreateServices();
        var liveSerializer = provider.GetRequiredService<IChangeManifestSerializer>();
        var logicalSerializer = provider.GetRequiredService<ILogicalStateManifestSerializer>();
        var historySerializer = provider.GetRequiredService<IHistoryManifestSerializer>();
        var packageSerializer = provider.GetRequiredService<IManifestSerializer>();

        Assert.ThrowsExactly<YabtMetadataException>
        (
            () => liveSerializer.Create([new(path, Fingerprint, ContentHash: ContentHash)], "mirror")
        );
        Assert.ThrowsExactly<YabtMetadataException>
        (
            () => logicalSerializer.Create([new(path, Fingerprint, ContentHash)])
        );
        Assert.ThrowsExactly<YabtMetadataException>
        (
            () => historySerializer.Create
            (
                [new(path, path, ArchiveHistoryEntryRepresentation.Materialized, 3, ContentHash)]
            )
        );
        Assert.ThrowsExactly<YabtMetadataException>
        (
            () => packageSerializer.Create
            (
                string.Empty,
                Timestamp,
                "zip",
                1,
                ContentHash,
                "package.zip",
                new("zip"),
                [new(ArchiveManifestEntryKinds.File, path, path, 3, Timestamp, ContentHash)]
            )
        );
    }

    [TestMethod]
    public async Task LiveManifestWriterRejectsBackslashEntryWithoutWritingOutput()
    {
        using var provider = CreateServices();
        var serializer = provider.GetRequiredService<IChangeManifestSerializer>();
        var manifest = serializer.Create
        (
            [new("folder/file.txt", Fingerprint, ContentHash: ContentHash)],
            "mirror"
        );
        var invalidManifest = manifest with
        {
            Entries = [manifest.Entries.Single() with { RelativePath = "folder\\file.txt" }],
        };
        await using var output = new MemoryStream();

        var exception = await Assert.ThrowsExactlyAsync<YabtMetadataException>
        (
            () => serializer.WriteAsync(invalidManifest, output)
        );

        StringAssert.Contains(exception.Message, "invalid");
        Assert.AreEqual(0, output.Length);
    }

    private static void AssertPreservedPaths(IEnumerable<string> actualPaths)
    {
        CollectionAssert.AreEqual(Paths.Order(StringComparer.Ordinal).ToArray(), actualPaths.ToArray());
    }

    private static ServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        services.AddYabtMetadata();
        return services.BuildServiceProvider();
    }
}
