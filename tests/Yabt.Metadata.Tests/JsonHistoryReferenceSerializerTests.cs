using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Yabt.Core.Models;
using Yabt.Metadata;

namespace Yabt.Metadata.Tests;

[TestClass]
public sealed class JsonHistoryReferenceSerializerTests
{
    [TestMethod]
    public async Task CreateAndReadAsyncRoundTripsCompleteManifestEntry()
    {
        var serializer = CreateSerializer();
        var originalPath = "2026-08-19/folder/report.pdf";
        var entry = new ArchiveHistoryManifestEntry
        (
            originalPath,
            ArchiveHistoryFileNames.CreateReferencePath(originalPath),
            ArchiveHistoryEntryRepresentation.Reference,
            12000,
            ArchiveHash.Compute(Encoding.UTF8.GetBytes("same content")),
            new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero),
            "application/pdf",
            new Dictionary<string, string> { ["owner"] = "archive" }
        );
        var reference = serializer.Create(entry);
        await using var json = new MemoryStream();

        await serializer.WriteAsync(reference, json);
        var serializedJson = Encoding.UTF8.GetString(json.ToArray());
        json.Position = 0;
        var restored = await serializer.ReadAsync(json);

        Assert.AreEqual(ArchiveHistoryContentReference.DefaultMessage, restored.Message);
        Assert.AreEqual(entry.RelativePath, restored.Entry.RelativePath);
        Assert.AreEqual(entry.StoredRelativePath, restored.Entry.StoredRelativePath);
        Assert.AreEqual(entry.Representation, restored.Entry.Representation);
        Assert.AreEqual(entry.ContentLength, restored.Entry.ContentLength);
        Assert.AreEqual(entry.ContentHash, restored.Entry.ContentHash);
        Assert.AreEqual(entry.LastModifiedUtc, restored.Entry.LastModifiedUtc);
        Assert.AreEqual(entry.ContentType, restored.Entry.ContentType);
        Assert.AreEqual("archive", restored.Entry.Metadata!["owner"]);
        Assert.IsTrue(ArchiveHash.IsValid(restored.ReferenceHash));
        StringAssert.Contains(serializedJson, "\"message\"");
        StringAssert.Contains(serializedJson, "\"relativePath\"");
        StringAssert.Contains(serializedJson, "\"storedRelativePath\"");
        StringAssert.Contains(serializedJson, "\"contentLength\"");
        StringAssert.Contains(serializedJson, "\"contentHash\"");
        StringAssert.Contains(serializedJson, "\"lastModifiedUtc\"");
        StringAssert.Contains(serializedJson, "\"contentType\"");
        StringAssert.Contains(serializedJson, "\"metadata\"");
    }

    [TestMethod]
    public async Task ReadAsyncRejectsReferenceWhoseEntryWasChanged()
    {
        var serializer = CreateSerializer();
        var originalHash = ArchiveHash.Compute(Encoding.UTF8.GetBytes("original"));
        var entry = new ArchiveHistoryManifestEntry
        (
            "2026-08-19/file.txt",
            "2026-08-19/file.txt.yabt-ref.json",
            ArchiveHistoryEntryRepresentation.Reference,
            8,
            originalHash
        );
        var reference = serializer.Create(entry);
        await using var json = new MemoryStream();
        await serializer.WriteAsync(reference, json);
        var changedJson = Encoding.UTF8.GetString(json.ToArray()).Replace(
            originalHash,
            ArchiveHash.Compute(Encoding.UTF8.GetBytes("changed")),
            StringComparison.Ordinal);
        await using var changedStream = new MemoryStream(Encoding.UTF8.GetBytes(changedJson));

        var exception = await Assert.ThrowsAsync<YabtMetadataException>(
            () => serializer.ReadAsync(changedStream));

        StringAssert.Contains(exception.Message, "self-hash");
    }

    [TestMethod]
    public void CreateRejectsMaterializedEntry()
    {
        var serializer = CreateSerializer();
        var entry = new ArchiveHistoryManifestEntry
        (
            "2026-08-19/file.txt",
            "2026-08-19/file.txt",
            ArchiveHistoryEntryRepresentation.Materialized,
            7,
            ArchiveHash.Compute(Encoding.UTF8.GetBytes("content"))
        );

        var exception = Assert.Throws<YabtMetadataException>(() => serializer.Create(entry));

        StringAssert.Contains(exception.Message, "not marked as a reference");
    }

    [TestMethod]
    public async Task ReadAsyncRejectsUnknownProperty()
    {
        var hash = ArchiveHash.Compute(Encoding.UTF8.GetBytes("content"));
        var json =
            "{\"documentType\":\"yabt.historyContentReference\",\"schemaVersion\":1," +
                "\"message\":\"duplicate\",\"canonicalPath\":\"other.txt\"," +
                "\"entry\":{\"relativePath\":\"2026-08-19/file.txt\"," +
                "\"storedRelativePath\":\"2026-08-19/file.txt.yabt-ref.json\"," +
                "\"representation\":\"reference\",\"contentLength\":7," +
                $"\"contentHash\":\"{hash}\"}}";
        var serializer = CreateSerializer();
        await using var source = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var exception = await Assert.ThrowsAsync<YabtMetadataException>(
            () => serializer.ReadAsync(source));

        StringAssert.Contains(exception.ToString(), "canonicalPath");
    }

    [TestMethod]
    public async Task ReadAsyncRejectsUnsupportedSchemaVersion()
    {
        var serializer = CreateSerializer();
        var entry = new ArchiveHistoryManifestEntry
        (
            "2026-08-19/file.txt",
            "2026-08-19/file.txt.yabt-ref.json",
            ArchiveHistoryEntryRepresentation.Reference,
            7,
            ArchiveHash.Compute(Encoding.UTF8.GetBytes("content"))
        );
        var reference = serializer.Create(entry);
        await using var json = new MemoryStream();
        await serializer.WriteAsync(reference, json);
        var changedJson = Encoding.UTF8.GetString(json.ToArray()).Replace(
            "\"schemaVersion\": 1",
            "\"schemaVersion\": 2",
            StringComparison.Ordinal);
        await using var changedStream = new MemoryStream(Encoding.UTF8.GetBytes(changedJson));

        var exception = await Assert.ThrowsAsync<YabtMetadataException>(
            () => serializer.ReadAsync(changedStream));

        StringAssert.Contains(exception.Message, "schema version");
    }

    [TestMethod]
    public void CreateReferencePathPreservesOriginalFileTypeIndicator()
    {
        var referencePath = ArchiveHistoryFileNames.CreateReferencePath(
            "2026-08-19/folder/report.pdf");

        Assert.AreEqual("2026-08-19/folder/report.pdf.yabt-ref.json", referencePath);
    }

    [TestMethod]
    public void IsReferencePathForAcceptsPreferredAndCanonicalCollisionNames()
    {
        const string originalPath = "2026-08-19/folder/report.pdf";

        Assert.IsTrue(ArchiveHistoryFileNames.IsReferencePathFor(
            originalPath,
            "2026-08-19/folder/report.pdf.yabt-ref.json"));
        Assert.IsTrue(ArchiveHistoryFileNames.IsReferencePathFor(
            originalPath,
            "2026-08-19/folder/report.pdf.1.yabt-ref.json"));
        Assert.IsFalse(ArchiveHistoryFileNames.IsReferencePathFor(
            originalPath,
            "2026-08-19/folder/report.pdf.01.yabt-ref.json"));
        Assert.IsFalse(ArchiveHistoryFileNames.IsReferencePathFor(
            originalPath,
            "2026-08-19/folder/unrelated.yabt-ref.json"));
    }

    [TestMethod]
    public void CreateRejectsReferenceStoredUnderUnrelatedName()
    {
        var serializer = CreateSerializer();
        var entry = new ArchiveHistoryManifestEntry
        (
            "2026-08-19/file.txt",
            "2026-08-19/unrelated.yabt-ref.json",
            ArchiveHistoryEntryRepresentation.Reference,
            7,
            ArchiveHash.Compute(Encoding.UTF8.GetBytes("content"))
        );

        var exception = Assert.Throws<YabtMetadataException>(() => serializer.Create(entry));

        StringAssert.Contains(exception.Message, "must preserve the original path");
    }

    private static IHistoryReferenceSerializer CreateSerializer()
    {
        var services = new ServiceCollection();
        services.AddYabtMetadata();

        return services.BuildServiceProvider().GetRequiredService<IHistoryReferenceSerializer>();
    }
}
