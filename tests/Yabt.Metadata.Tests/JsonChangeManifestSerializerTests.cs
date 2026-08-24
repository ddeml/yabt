using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Yabt.Core.Models;
using Yabt.Metadata;

namespace Yabt.Metadata.Tests;

[TestClass]
public sealed class JsonChangeManifestSerializerTests
{
    private const string RootFormat = "mirror";

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

        var forwardManifest = serializer.Create([firstEntry, secondEntry], RootFormat);
        var reverseManifest = serializer.Create([secondEntry, firstEntry], RootFormat);
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
        ], RootFormat);
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
    public async Task ReadAsyncRoundTripsDurableRootFormatEvidence()
    {
        var serializer = CreateSerializer();
        var manifest = serializer.Create
        (
            [
                new
                (
                    "package.zip",
                    ArchiveHash.Compute(Encoding.UTF8.GetBytes("logical input")),
                    ContentHash: CreateContentHash("content")
                ),
            ],
            rootFormat: "zip"
        );
        await using var json = new MemoryStream();
        await serializer.WriteAsync(manifest, json);
        json.Position = 0;

        var restored = await serializer.ReadAsync(json);

        Assert.AreEqual(ArchiveChangeManifest.ExpectedSchemaVersion, restored.SchemaVersion);
        Assert.AreEqual("zip", restored.RootFormat);
        Assert.AreEqual(manifest.ManifestHash, restored.ManifestHash);
    }

    [TestMethod]
    public async Task CreateAndReadAsyncRoundTripsSchemaV3ProjectionAndRootDescriptorEvidence()
    {
        var serializer = CreateSerializer();
        var rootDescriptorContentHash = CreateContentHash("root descriptor");
        var projectionId = CreateContentHash("logical projection");
        var manifest = serializer.Create
        (
            [
                new
                (
                    "Photos/package.zip",
                    projectionId,
                    ArtifactLength: 123,
                    ContentHash: CreateContentHash("package bytes"),
                    Projection: new
                    (
                        "Photos\\2026",
                        "zip",
                        1,
                        projectionId,
                        ArchiveProjectionArtifactRoles.Package
                    )
                ),
            ],
            rootFormat: "mirror",
            rootDescriptorContentHash,
            rootDescriptorContentLength: 321
        );
        await using var json = new MemoryStream();
        await serializer.WriteAsync(manifest, json);
        json.Position = 0;

        var restored = await serializer.ReadAsync(json);
        var projection = restored.Entries.Single().Projection;

        Assert.AreEqual(ArchiveChangeManifest.ExpectedSchemaVersion, restored.SchemaVersion);
        Assert.AreEqual(rootDescriptorContentHash, restored.RootDescriptorContentHash);
        Assert.AreEqual(321, restored.RootDescriptorContentLength);
        Assert.IsNotNull(projection);
        Assert.AreEqual("Photos/2026", projection.LogicalPath);
        Assert.AreEqual("zip", projection.Format);
        Assert.AreEqual(1, projection.FormatVersion);
        Assert.AreEqual(projectionId, projection.ProjectionId);
        Assert.AreEqual(ArchiveProjectionArtifactRoles.Package, projection.ArtifactRole);
    }

    [TestMethod]
    public void CreateIncludesProjectionAndRootDescriptorEvidenceInManifestHash()
    {
        var serializer = CreateSerializer();
        var projectionId = CreateContentHash("logical projection");
        var baseEntry = new ArchiveChangeManifestEntry
        (
            "package.zip",
            projectionId,
            ContentHash: CreateContentHash("package bytes")
        );
        var projection = new ArchiveProjectionProvenance
        (
            string.Empty,
            "zip",
            1,
            projectionId,
            ArchiveProjectionArtifactRoles.Package
        );
        var rootHash = CreateContentHash("root descriptor");

        var withoutEvidence = serializer.Create([baseEntry], RootFormat);
        var withProjection = serializer.Create
        (
            [baseEntry with { Projection = projection }],
            RootFormat
        );
        var withAllEvidence = serializer.Create
        (
            [baseEntry with { Projection = projection }],
            RootFormat,
            rootHash,
            42
        );

        Assert.AreNotEqual(withoutEvidence.ManifestHash, withProjection.ManifestHash);
        Assert.AreNotEqual(withProjection.ManifestHash, withAllEvidence.ManifestHash);
    }

    [TestMethod]
    public void CreateIncludesRootFormatInManifestHash()
    {
        var serializer = CreateSerializer();
        var entry = new ArchiveChangeManifestEntry
        (
            "file.txt",
            "stat-v1:2026-08-16T12:00:00.0000000Z:7",
            ContentHash: CreateContentHash("content")
        );

        var mirrorManifest = serializer.Create([entry], "mirror");
        var zipManifest = serializer.Create([entry], "zip");

        Assert.AreNotEqual(mirrorManifest.ManifestHash, zipManifest.ManifestHash);
    }

    [TestMethod]
    public async Task ReadAsyncRejectsChangedRootFormat()
    {
        var serializer = CreateSerializer();
        var manifest = serializer.Create
        (
            [
                new
                (
                    "file.txt",
                    "stat-v1:2026-08-16T12:00:00.0000000Z:7",
                    ContentHash: CreateContentHash("content")
                ),
            ],
            "mirror"
        );
        var json = await WriteToStringAsync(serializer, manifest);
        var changedJson = json.Replace
        (
            "\"rootFormat\": \"mirror\"",
            "\"rootFormat\": \"zip\"",
            StringComparison.Ordinal
        );
        await using var changedStream = new MemoryStream(Encoding.UTF8.GetBytes(changedJson));

        var exception = await Assert.ThrowsAsync<YabtMetadataException>
        (
            () => serializer.ReadAsync(changedStream)
        );

        StringAssert.Contains(exception.Message, "self-hash");
    }

    [TestMethod]
    public async Task ReadAsyncRejectsDuplicateJsonProperties()
    {
        var serializer = CreateSerializer();
        var manifest = serializer.Create
        (
            [
                new
                (
                    "file.txt",
                    "stat-v1:2026-08-16T12:00:00.0000000Z:7",
                    ContentHash: CreateContentHash("content")
                ),
            ],
            RootFormat
        );
        var json = await WriteToStringAsync(serializer, manifest);
        var duplicateJson = json.Replace
        (
            "\"rootFormat\": \"mirror\"",
            "\"rootFormat\": \"mirror\",\n  \"rootFormat\": \"mirror\"",
            StringComparison.Ordinal
        );
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(duplicateJson));

        await Assert.ThrowsAsync<YabtMetadataException>(
            () => serializer.ReadAsync(stream));
    }

    [TestMethod]
    public async Task ReadAsyncAcceptsLegacySchemaV1WithoutRootFormat()
    {
        var serializer = CreateSerializer();
        var legacyCanonicalJson =
            "{\"documentType\":\"yabt.changeManifest\",\"schemaVersion\":1,\"entries\":[]}";
        var legacyManifestHash = ArchiveHash.Compute
        (
            Encoding.UTF8.GetBytes(legacyCanonicalJson)
        );
        var legacyJson = $$"""
            {
              "documentType": "yabt.changeManifest",
              "schemaVersion": 1,
              "entries": [],
              "manifestHash": "{{legacyManifestHash}}"
            }
            """;
        await using var legacyStream = new MemoryStream(Encoding.UTF8.GetBytes(legacyJson));

        var manifest = await serializer.ReadAsync(legacyStream);

        Assert.AreEqual(ArchiveChangeManifest.LegacySchemaVersion, manifest.SchemaVersion);
        Assert.IsNull(manifest.RootFormat);
        Assert.AreEqual(legacyManifestHash, manifest.ManifestHash);
    }

    [TestMethod]
    public async Task ReadAsyncAcceptsPreviousSchemaV2WithRootFormat()
    {
        var serializer = CreateSerializer();
        var previousCanonicalJson =
            "{\"documentType\":\"yabt.changeManifest\",\"schemaVersion\":2," +
            "\"rootFormat\":\"mirror\",\"entries\":[]}";
        var previousManifestHash = ArchiveHash.Compute
        (
            Encoding.UTF8.GetBytes(previousCanonicalJson)
        );
        var previousJson = $$"""
            {
              "documentType": "yabt.changeManifest",
              "schemaVersion": 2,
              "entries": [],
              "manifestHash": "{{previousManifestHash}}",
              "rootFormat": "mirror"
            }
            """;
        await using var previousStream = new MemoryStream(
            Encoding.UTF8.GetBytes(previousJson));

        var manifest = await serializer.ReadAsync(previousStream);

        Assert.AreEqual(ArchiveChangeManifest.PreviousSchemaVersion, manifest.SchemaVersion);
        Assert.AreEqual("mirror", manifest.RootFormat);
        Assert.IsNull(manifest.RootDescriptorContentHash);
        Assert.IsNull(manifest.RootDescriptorContentLength);
    }

    [TestMethod]
    public async Task ReadAsyncAcceptsPreviousSchemaEntriesWithoutContentHash()
    {
        const string fingerprint = "stat-v1:2026-08-16T12:00:00.0000000Z:7";
        var serializer = CreateSerializer();
        var legacyCanonicalJson =
            "{\"documentType\":\"yabt.changeManifest\",\"schemaVersion\":1," +
                "\"entries\":[{\"relativePath\":\"file.txt\"," +
                $"\"changeFingerprint\":\"{fingerprint}\"}}]}}";
        var previousCanonicalJson =
            "{\"documentType\":\"yabt.changeManifest\",\"schemaVersion\":2," +
                "\"rootFormat\":\"mirror\"," +
                "\"entries\":[{\"relativePath\":\"file.txt\"," +
                $"\"changeFingerprint\":\"{fingerprint}\"}}]}}";
        var legacyJson = $$"""
            {
              "documentType": "yabt.changeManifest",
              "schemaVersion": 1,
              "entries": [
                {
                  "relativePath": "file.txt",
                  "changeFingerprint": "{{fingerprint}}"
                }
              ],
              "manifestHash": "{{ArchiveHash.Compute(Encoding.UTF8.GetBytes(legacyCanonicalJson))}}"
            }
            """;
        var previousJson = $$"""
            {
              "documentType": "yabt.changeManifest",
              "schemaVersion": 2,
              "entries": [
                {
                  "relativePath": "file.txt",
                  "changeFingerprint": "{{fingerprint}}"
                }
              ],
              "manifestHash": "{{ArchiveHash.Compute(Encoding.UTF8.GetBytes(previousCanonicalJson))}}",
              "rootFormat": "mirror"
            }
            """;
        await using var legacyStream = new MemoryStream(Encoding.UTF8.GetBytes(legacyJson));
        await using var previousStream = new MemoryStream(Encoding.UTF8.GetBytes(previousJson));

        var legacyManifest = await serializer.ReadAsync(legacyStream);
        var previousManifest = await serializer.ReadAsync(previousStream);

        Assert.IsNull(legacyManifest.Entries.Single().ContentHash);
        Assert.IsNull(previousManifest.Entries.Single().ContentHash);
    }

    [TestMethod]
    public async Task ReadAsyncRejectsLegacySchemaV1WithRootFormat()
    {
        var serializer = CreateSerializer();
        var legacyJson = $$"""
            {
              "documentType": "yabt.changeManifest",
              "schemaVersion": 1,
              "entries": [],
              "manifestHash": "{{CreateContentHash("legacy")}}",
              "rootFormat": "mirror"
            }
            """;
        await using var legacyStream = new MemoryStream(Encoding.UTF8.GetBytes(legacyJson));

        var exception = await Assert.ThrowsAsync<YabtMetadataException>
        (
            () => serializer.ReadAsync(legacyStream)
        );

        StringAssert.Contains(exception.Message, "could not be deserialized");
    }

    [TestMethod]
    public async Task WriteAsyncRejectsLegacySchemaV1Manifest()
    {
        var serializer = CreateSerializer();
        var legacyCanonicalJson =
            "{\"documentType\":\"yabt.changeManifest\",\"schemaVersion\":1,\"entries\":[]}";
        var manifest = new ArchiveChangeManifest
        (
            ArchiveChangeManifest.ExpectedDocumentType,
            ArchiveChangeManifest.LegacySchemaVersion,
            [],
            ArchiveHash.Compute(Encoding.UTF8.GetBytes(legacyCanonicalJson))
        );
        await using var destination = new MemoryStream();

        var exception = await Assert.ThrowsAsync<YabtMetadataException>
        (
            () => serializer.WriteAsync(manifest, destination)
        );

        StringAssert.Contains(exception.Message, "can be read but cannot be written");
    }

    [TestMethod]
    public async Task WriteAsyncRejectsPreviousSchemaV2Manifest()
    {
        var serializer = CreateSerializer();
        var previousCanonicalJson =
            "{\"documentType\":\"yabt.changeManifest\",\"schemaVersion\":2," +
            "\"rootFormat\":\"mirror\",\"entries\":[]}";
        var manifest = new ArchiveChangeManifest
        (
            ArchiveChangeManifest.ExpectedDocumentType,
            ArchiveChangeManifest.PreviousSchemaVersion,
            [],
            ArchiveHash.Compute(Encoding.UTF8.GetBytes(previousCanonicalJson)),
            RootFormat
        );
        await using var destination = new MemoryStream();

        var exception = await Assert.ThrowsAsync<YabtMetadataException>
        (
            () => serializer.WriteAsync(manifest, destination)
        );

        StringAssert.Contains(exception.Message, "can be read but cannot be written");
    }

    [TestMethod]
    public async Task ReadAsyncRejectsSchemaV2RootDescriptorEvidence()
    {
        var serializer = CreateSerializer();
        var json = $$"""
            {
              "documentType": "yabt.changeManifest",
              "schemaVersion": 2,
              "entries": [],
              "manifestHash": "{{CreateContentHash("previous")}}",
              "rootFormat": "mirror",
              "rootDescriptorContentHash": "{{CreateContentHash("descriptor")}}",
              "rootDescriptorContentLength": 20
            }
            """;
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var exception = await Assert.ThrowsAsync<YabtMetadataException>
        (
            () => serializer.ReadAsync(stream)
        );

        StringAssert.Contains(exception.Message, "could not be deserialized");
    }

    [TestMethod]
    public async Task ReadAsyncRejectsSchemaV2ProjectionProvenance()
    {
        var serializer = CreateSerializer();
        var projectionId = CreateContentHash("projection");
        var json = $$"""
            {
              "documentType": "yabt.changeManifest",
              "schemaVersion": 2,
              "entries": [
                {
                  "relativePath": "package.zip",
                  "changeFingerprint": "{{projectionId}}",
                  "projection": {
                    "logicalPath": "",
                    "format": "zip",
                    "formatVersion": 1,
                    "projectionId": "{{projectionId}}",
                    "artifactRole": "package"
                  }
                }
              ],
              "manifestHash": "{{CreateContentHash("previous")}}",
              "rootFormat": "mirror"
            }
            """;
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var exception = await Assert.ThrowsAsync<YabtMetadataException>
        (
            () => serializer.ReadAsync(stream)
        );

        StringAssert.Contains(exception.Message, "could not be deserialized");
    }

    [DataRow(1, "\"rootFormat\":null")]
    [DataRow(1, "\"rootDescriptorContentHash\":null")]
    [DataRow(1, "\"rootDescriptorContentLength\":null")]
    [DataRow(2, "\"rootDescriptorContentHash\":null")]
    [DataRow(2, "\"rootDescriptorContentLength\":null")]
    [TestMethod]
    public async Task ReadAsyncRejectsExplicitNullNewerRootPropertyInPreviousSchema
    (
        int schemaVersion,
        string forbiddenProperty
    )
    {
        var serializer = CreateSerializer();
        var canonicalJson = schemaVersion == ArchiveChangeManifest.LegacySchemaVersion ?
            "{\"documentType\":\"yabt.changeManifest\",\"schemaVersion\":1,\"entries\":[]}" :
            "{\"documentType\":\"yabt.changeManifest\",\"schemaVersion\":2," +
                "\"rootFormat\":\"mirror\",\"entries\":[]}";
        List<string> properties =
        [
            "\"documentType\":\"yabt.changeManifest\"",
            $"\"schemaVersion\":{schemaVersion}",
            "\"entries\":[]",
            $"\"manifestHash\":\"{ArchiveHash.Compute(Encoding.UTF8.GetBytes(canonicalJson))}\"",
        ];
        if (schemaVersion == ArchiveChangeManifest.PreviousSchemaVersion)
        {
            properties.Add("\"rootFormat\":\"mirror\"");
        }

        properties.Add(forbiddenProperty);
        var json = $"{{{string.Join(',', properties)}}}";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        await Assert.ThrowsAsync<YabtMetadataException>(
            () => serializer.ReadAsync(stream));
    }

    [DataRow(1)]
    [DataRow(2)]
    [TestMethod]
    public async Task ReadAsyncRejectsExplicitNullProjectionInPreviousSchema(int schemaVersion)
    {
        const string fingerprint = "stat-v1:2026-08-16T12:00:00.0000000Z:7";
        var serializer = CreateSerializer();
        var rootFormatCanonicalProperty =
            schemaVersion == ArchiveChangeManifest.PreviousSchemaVersion ?
                "\"rootFormat\":\"mirror\"," :
                string.Empty;
        var rootFormatSerializedProperty =
            schemaVersion == ArchiveChangeManifest.PreviousSchemaVersion ?
                ",\"rootFormat\":\"mirror\"" :
                string.Empty;
        var canonicalJson =
            $"{{\"documentType\":\"yabt.changeManifest\",\"schemaVersion\":{schemaVersion}," +
                rootFormatCanonicalProperty +
                "\"entries\":[{\"relativePath\":\"file.txt\"," +
                $"\"changeFingerprint\":\"{fingerprint}\"}}]}}";
        var json =
            $"{{\"documentType\":\"yabt.changeManifest\",\"schemaVersion\":{schemaVersion}," +
                "\"entries\":[{\"relativePath\":\"file.txt\"," +
                $"\"changeFingerprint\":\"{fingerprint}\",\"projection\":null}}]," +
                $"\"manifestHash\":\"{ArchiveHash.Compute(Encoding.UTF8.GetBytes(canonicalJson))}\"" +
                rootFormatSerializedProperty +
                "}";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        await Assert.ThrowsAsync<YabtMetadataException>(
            () => serializer.ReadAsync(stream));
    }

    [TestMethod]
    public async Task ReadAsyncRejectsSchemaV2WithoutRootFormat()
    {
        var serializer = CreateSerializer();
        var json = $$"""
            {
              "documentType": "yabt.changeManifest",
              "schemaVersion": 2,
              "entries": [],
              "manifestHash": "{{CreateContentHash("missing-root-format")}}"
            }
            """;
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var exception = await Assert.ThrowsAsync<YabtMetadataException>
        (
            () => serializer.ReadAsync(stream)
        );

        StringAssert.Contains(exception.Message, "root format is required");
    }

    [DataRow("")]
    [DataRow(" ")]
    [DataRow("\t")]
    [TestMethod]
    public void CreateRejectsEmptyRootFormat(string rootFormat)
    {
        var serializer = CreateSerializer();

        var exception = Assert.Throws<YabtMetadataException>
        (
            () => serializer.Create([], rootFormat)
        );

        StringAssert.Contains(exception.Message, "root format is required");
    }

    [TestMethod]
    public void CreateRejectsIncompleteRootDescriptorEvidence()
    {
        var serializer = CreateSerializer();

        var missingLength = Assert.Throws<YabtMetadataException>
        (
            () => serializer.Create
            (
                [],
                RootFormat,
                rootDescriptorContentHash: CreateContentHash("descriptor")
            )
        );
        var missingHash = Assert.Throws<YabtMetadataException>
        (
            () => serializer.Create
            (
                [],
                RootFormat,
                rootDescriptorContentLength: 20
            )
        );

        StringAssert.Contains(missingLength.Message, "both be present or both be absent");
        StringAssert.Contains(missingHash.Message, "both be present or both be absent");
    }

    [TestMethod]
    public void CreateRejectsInvalidRootDescriptorEvidence()
    {
        var serializer = CreateSerializer();

        var invalidHash = Assert.Throws<YabtMetadataException>
        (
            () => serializer.Create([], RootFormat, "not-a-hash", 20)
        );
        var negativeLength = Assert.Throws<YabtMetadataException>
        (
            () => serializer.Create([], RootFormat, CreateContentHash("descriptor"), -1)
        );

        StringAssert.Contains(invalidHash.Message, "root descriptor content hash");
        StringAssert.Contains(negativeLength.Message, "cannot be negative");
    }

    [TestMethod]
    public void CreateRejectsInvalidProjectionProvenance()
    {
        var serializer = CreateSerializer();
        var contentHash = CreateContentHash("content");
        ArchiveChangeManifestEntry CreateEntry(ArchiveProjectionProvenance projection) => new
        (
            "package.zip",
            contentHash,
            ContentHash: contentHash,
            Projection: projection
        );

        var invalidVersion = Assert.Throws<YabtMetadataException>
        (
            () => serializer.Create
            (
                [
                    CreateEntry(new("folder", "zip", 0, contentHash, "package")),
                ],
                RootFormat
            )
        );
        var invalidProjectionId = Assert.Throws<YabtMetadataException>
        (
            () => serializer.Create
            (
                [
                    CreateEntry(new("folder", "zip", 1, "invalid", "package")),
                ],
                RootFormat
            )
        );
        var emptyRole = Assert.Throws<YabtMetadataException>
        (
            () => serializer.Create
            (
                [
                    CreateEntry(new("folder", "zip", 1, contentHash, " ")),
                ],
                RootFormat
            )
        );

        StringAssert.Contains(invalidVersion.Message, "format version must be positive");
        StringAssert.Contains(invalidProjectionId.Message, "projection id");
        StringAssert.Contains(emptyRole.Message, "artifact role");
    }

    [TestMethod]
    public async Task ReadAsyncRejectsNoncanonicalProjectionLogicalPath()
    {
        var serializer = CreateSerializer();
        var projectionId = CreateContentHash("projection");
        var manifest = serializer.Create
        (
            [
                new
                (
                    "package.zip",
                    projectionId,
                    ContentHash: CreateContentHash("package"),
                    Projection: new
                    (
                        "folder/child",
                        "zip",
                        1,
                        projectionId,
                        "package"
                    )
                ),
            ],
            RootFormat
        );
        var json = await WriteToStringAsync(serializer, manifest);
        var noncanonicalJson = json.Replace
        (
            "\"logicalPath\": \"folder/child\"",
            "\"logicalPath\": \"folder\\\\child\"",
            StringComparison.Ordinal
        );
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(noncanonicalJson));

        var exception = await Assert.ThrowsAsync<YabtMetadataException>
        (
            () => serializer.ReadAsync(stream)
        );

        StringAssert.Contains(exception.Message, "noncanonical values");
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
        ], RootFormat);
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
        ], RootFormat));

        StringAssert.Contains(exception.Message, "content hash");
    }

    [TestMethod]
    public void CreateRejectsMissingSchemaV3EntryContentHash()
    {
        var serializer = CreateSerializer();

        var exception = Assert.Throws<YabtMetadataException>(() => serializer.Create
        ([
            new
            (
                "file.txt",
                "stat-v1:2026-08-16T12:00:00.0000000Z:7"
            ),
        ], RootFormat));

        StringAssert.Contains(exception.Message, "content hash");
    }

    [TestMethod]
    public async Task ReadAsyncRejectsMissingSchemaV3EntryContentHash()
    {
        const string fingerprint = "stat-v1:2026-08-16T12:00:00.0000000Z:7";
        var serializer = CreateSerializer();
        var canonicalJson =
            "{\"documentType\":\"yabt.changeManifest\",\"schemaVersion\":3," +
                "\"rootFormat\":\"mirror\"," +
                "\"entries\":[{\"relativePath\":\"file.txt\"," +
                $"\"changeFingerprint\":\"{fingerprint}\"}}]}}";
        var json = $$"""
            {
              "documentType": "yabt.changeManifest",
              "schemaVersion": 3,
              "entries": [
                {
                  "relativePath": "file.txt",
                  "changeFingerprint": "{{fingerprint}}"
                }
              ],
              "manifestHash": "{{ArchiveHash.Compute(Encoding.UTF8.GetBytes(canonicalJson))}}",
              "rootFormat": "mirror"
            }
            """;
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var exception = await Assert.ThrowsAsync<YabtMetadataException>(
            () => serializer.ReadAsync(stream));

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
        ], RootFormat));

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
            ], RootFormat));

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
        ], RootFormat);
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
        ], RootFormat));

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
