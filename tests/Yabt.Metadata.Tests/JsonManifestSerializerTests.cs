using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Yabt.Core.Models;

namespace Yabt.Metadata.Tests;

[TestClass]
public sealed class JsonManifestSerializerTests
{
    private static readonly DateTimeOffset EarlierTimestamp = new
    (
        2026,
        8,
        24,
        12,
        30,
        7,
        123,
        TimeSpan.Zero
    );

    private static readonly DateTimeOffset LaterTimestamp = EarlierTimestamp.AddMinutes(1);

    [TestMethod]
    public async Task CreateWriteReadAsyncRoundTripsCanonicalVersionedManifest()
    {
        using var serviceProvider = CreateServices().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<IManifestSerializer>();
        var projectionId = ArchiveHash.Compute(Encoding.UTF8.GetBytes("outer projection"));
        var nestedProjectionId = ArchiveHash.Compute(Encoding.UTF8.GetBytes("nested projection"));
        var manifest = serializer.Create
        (
            "photos",
            LaterTimestamp,
            "zip",
            1,
            projectionId,
            "Photos.xxh128-00000000000000000000000000.zip",
            new FolderPolicy
            (
                "zip",
                IncludePatterns: ["*.jpg"],
                Options: new Dictionary<string, object>
                {
                    ["zeta"] = 2,
                    ["alpha"] = true,
                }
            ),
            [
                new
                (
                    ArchiveManifestEntryKinds.FormatArtifact,
                    "child.xxh128-00000000000000000000000000.zip",
                    "child.xxh128-00000000000000000000000000.zip",
                    12,
                    LaterTimestamp,
                    ArchiveHash.Compute(Encoding.UTF8.GetBytes("nested bytes")),
                    new
                    (
                        "child",
                        "zip",
                        1,
                        nestedProjectionId,
                        ArchiveProjectionArtifactRoles.Package
                    )
                ),
                new
                (
                    ArchiveManifestEntryKinds.File,
                    "a.jpg",
                    "a.jpg",
                    3,
                    EarlierTimestamp,
                    ArchiveHash.Compute(Encoding.UTF8.GetBytes("jpg"))
                ),
            ]
        );

        var firstJson = await WriteAsync(serializer, manifest);
        var secondJson = await WriteAsync(serializer, manifest);
        CollectionAssert.AreEqual(firstJson, secondJson);
        StringAssert.Contains(
            Encoding.UTF8.GetString(firstJson),
            "\"documentType\": \"yabt.packageManifest\"");
        StringAssert.Contains(
            Encoding.UTF8.GetString(firstJson),
            "\"alpha\": true");

        await using var source = new MemoryStream(firstJson, writable: false);
        var restored = await serializer.ReadAsync(source);

        Assert.AreEqual(ArchiveManifest.ExpectedSchemaVersion, restored.SchemaVersion);
        Assert.AreEqual(projectionId, restored.ProjectionId);
        Assert.AreEqual(LaterTimestamp, restored.CreatedAtUtc);
        Assert.AreEqual(15, restored.TotalBytes);
        CollectionAssert.AreEqual(
            new[]
            {
                "a.jpg",
                "child.xxh128-00000000000000000000000000.zip",
            },
            restored.Entries.Select(entry => entry.StoredPath).ToArray());
    }

    [TestMethod]
    public async Task ReadAsyncRejectsUnknownProperties()
    {
        using var serviceProvider = CreateServices().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<IManifestSerializer>();
        var json = Encoding.UTF8.GetString(await WriteAsync(
            serializer,
            CreateSimpleManifest(serializer)));
        var invalidJson = json.Replace(
            "\"schemaVersion\": 1,",
            "\"schemaVersion\": 1,\r\n  \"unexpected\": true,",
            StringComparison.Ordinal);
        await using var source = new MemoryStream(
            Encoding.UTF8.GetBytes(invalidJson),
            writable: false);

        var exception = await Assert.ThrowsExactlyAsync<YabtMetadataException>(
            () => serializer.ReadAsync(source));

        StringAssert.Contains(exception.Message, "deserialized");
    }

    [TestMethod]
    public async Task ReadAsyncRejectsTamperedSelfHash()
    {
        using var serviceProvider = CreateServices().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<IManifestSerializer>();
        var json = Encoding.UTF8.GetString(await WriteAsync(
            serializer,
            CreateSimpleManifest(serializer)));
        var invalidJson = json.Replace(
            "\"length\": 3",
            "\"length\": 4",
            StringComparison.Ordinal);
        await using var source = new MemoryStream(
            Encoding.UTF8.GetBytes(invalidJson),
            writable: false);

        var exception = await Assert.ThrowsExactlyAsync<YabtMetadataException>(
            () => serializer.ReadAsync(source));

        StringAssert.Contains(exception.Message, "total bytes");
    }

    [TestMethod]
    public void CreateRejectsNondeterministicCreationTime()
    {
        using var serviceProvider = CreateServices().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<IManifestSerializer>();
        var entry = CreateSimpleEntry();

        var exception = Assert.ThrowsExactly<YabtMetadataException>(() => serializer.Create
        (
            string.Empty,
            entry.LastModifiedUtc.AddSeconds(1),
            "zip",
            1,
            ArchiveHash.Compute(Encoding.UTF8.GetBytes("projection")),
            "root.xxh128-00000000000000000000000000.zip",
            new FolderPolicy("zip"),
            [entry]
        ));

        StringAssert.Contains(exception.Message, "deterministic");
    }

    private static ArchiveManifest CreateSimpleManifest(IManifestSerializer serializer)
    {
        var entry = CreateSimpleEntry();
        return serializer.Create
        (
            string.Empty,
            entry.LastModifiedUtc,
            "zip",
            1,
            ArchiveHash.Compute(Encoding.UTF8.GetBytes("projection")),
            "root.xxh128-00000000000000000000000000.zip",
            new FolderPolicy("zip"),
            [entry]
        );
    }

    private static ArchiveManifestEntry CreateSimpleEntry() => new
    (
        ArchiveManifestEntryKinds.File,
        "file.txt",
        "file.txt",
        3,
        EarlierTimestamp,
        ArchiveHash.Compute(Encoding.UTF8.GetBytes("abc"))
    );

    private static async Task<byte[]> WriteAsync
    (
        IManifestSerializer serializer,
        ArchiveManifest manifest
    )
    {
        using var destination = new MemoryStream();
        await serializer.WriteAsync(manifest, destination);
        return destination.ToArray();
    }

    private static ServiceCollection CreateServices()
    {
        var services = new ServiceCollection();
        services.AddYabtMetadata();
        return services;
    }
}
