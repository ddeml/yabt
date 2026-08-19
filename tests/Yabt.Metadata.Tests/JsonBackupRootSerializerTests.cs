using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Yabt.Core.Models;
using Yabt.Metadata;

namespace Yabt.Metadata.Tests;

[TestClass]
public sealed class JsonBackupRootSerializerTests
{
    [TestMethod]
    public async Task ReadAsyncDefaultsMissingHistoryDeduplicationTinyFileMaximumBytes()
    {
        const string json =
            "{\"documentType\":\"yabt.backupRoot\",\"schemaVersion\":1," +
                "\"archiveId\":\"test-archive\",\"createdAtUtc\":\"2026-08-16T12:00:00Z\"," +
                "\"layout\":{\"livePrefix\":\"\",\"histPrefix\":\".yabt-hist\"}," +
                "\"stores\":[{\"id\":\"target\",\"kind\":\"fileSystem\"}]}";
        var serializer = CreateSerializer();
        await using var source = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var descriptor = await serializer.ReadAsync(source);

        Assert.IsNull(descriptor.HistoryDeduplicationTinyFileMaximumBytes);
        Assert.AreEqual
        (
            ArchiveHistoryDeduplication.DefaultTinyFileMaximumBytes,
            ArchiveHistoryDeduplication.GetEffectiveTinyFileMaximumBytes(
                descriptor.HistoryDeduplicationTinyFileMaximumBytes)
        );
    }

    [TestMethod]
    public async Task WriteAndReadAsyncRoundTripsHistoryDeduplicationTinyFileMaximumBytes()
    {
        var descriptor = CreateDescriptor(
            BackupRootDescriptor.ExpectedSchemaVersion,
            historyDeduplicationTinyFileMaximumBytes: 8192);
        var serializer = CreateSerializer();
        await using var document = new MemoryStream();

        await serializer.WriteAsync(descriptor, document);
        document.Position = 0;
        var restored = await serializer.ReadAsync(document);

        Assert.AreEqual(8192, restored.HistoryDeduplicationTinyFileMaximumBytes);
    }

    [TestMethod]
    public async Task ReadAsyncRejectsNegativeHistoryDeduplicationTinyFileMaximumBytes()
    {
        const string json =
            "{\"documentType\":\"yabt.backupRoot\",\"schemaVersion\":1," +
                "\"archiveId\":\"test-archive\",\"createdAtUtc\":\"2026-08-16T12:00:00Z\"," +
                "\"historyDeduplicationTinyFileMaximumBytes\":-1," +
                "\"layout\":{\"livePrefix\":\"\",\"histPrefix\":\".yabt-hist\"}," +
                "\"stores\":[{\"id\":\"target\",\"kind\":\"fileSystem\"}]}";
        var serializer = CreateSerializer();
        await using var source = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var exception = await Assert.ThrowsAsync<YabtMetadataException>(
            () => serializer.ReadAsync(source));

        StringAssert.Contains(exception.Message, "tiny-file maximum size");
    }

    [TestMethod]
    public async Task WriteAsyncRejectsNegativeHistoryDeduplicationTinyFileMaximumBytes()
    {
        var descriptor = CreateDescriptor(
            BackupRootDescriptor.ExpectedSchemaVersion,
            historyDeduplicationTinyFileMaximumBytes: -1);
        var serializer = CreateSerializer();
        await using var destination = new MemoryStream();

        var exception = await Assert.ThrowsAsync<YabtMetadataException>(
            () => serializer.WriteAsync(descriptor, destination));

        StringAssert.Contains(exception.Message, "tiny-file maximum size");
    }

    [TestMethod]
    public async Task ReadAsyncDefaultsMissingChangeManifestCompressionToBrotli()
    {
        const string json =
            "{\"documentType\":\"yabt.backupRoot\",\"schemaVersion\":1," +
                "\"archiveId\":\"test-archive\",\"createdAtUtc\":\"2026-08-16T12:00:00Z\"," +
                "\"layout\":{\"livePrefix\":\"\",\"histPrefix\":\".yabt-hist\"}," +
                "\"stores\":[{\"id\":\"target\",\"kind\":\"fileSystem\"}]}";
        var serializer = CreateSerializer();
        await using var source = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var descriptor = await serializer.ReadAsync(source);

        Assert.IsNull(descriptor.ChangeManifestCompression);
        Assert.AreEqual(
            ArchiveChangeManifestCompression.Brotli,
            ArchiveChangeManifestCompression.GetEffective(descriptor.ChangeManifestCompression));
    }

    [TestMethod]
    public async Task WriteAndReadAsyncRoundTripsSupportedChangeManifestCompressions()
    {
        var serializer = CreateSerializer();
        string[] compressions =
        [
            ArchiveChangeManifestCompression.Brotli,
            ArchiveChangeManifestCompression.None,
        ];

        foreach (var compression in compressions)
        {
            var descriptor = CreateDescriptor(
                BackupRootDescriptor.ExpectedSchemaVersion,
                compression);
            await using var document = new MemoryStream();

            await serializer.WriteAsync(descriptor, document);
            document.Position = 0;
            var restored = await serializer.ReadAsync(document);

            Assert.AreEqual(compression, restored.ChangeManifestCompression);
        }
    }

    [TestMethod]
    public async Task ReadAsyncRejectsUnsupportedChangeManifestCompression()
    {
        const string json =
            "{\"documentType\":\"yabt.backupRoot\",\"schemaVersion\":1," +
                "\"archiveId\":\"test-archive\",\"createdAtUtc\":\"2026-08-16T12:00:00Z\"," +
                "\"changeManifestCompression\":\"Brotli\"," +
                "\"layout\":{\"livePrefix\":\"\",\"histPrefix\":\".yabt-hist\"}," +
                "\"stores\":[{\"id\":\"target\",\"kind\":\"fileSystem\"}]}";
        var serializer = CreateSerializer();
        await using var source = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var exception = await Assert.ThrowsAsync<YabtMetadataException>(
            () => serializer.ReadAsync(source));

        StringAssert.Contains(exception.Message, "change manifest compression");
    }

    [TestMethod]
    public async Task ReadAsyncRejectsUnknownRootProperty()
    {
        const string json =
            "{\"documentType\":\"yabt.backupRoot\",\"schemaVersion\":1," +
                "\"archiveId\":\"test-archive\",\"createdAtUtc\":\"2026-08-16T12:00:00Z\"," +
                "\"changeManifestCompresion\":\"none\"," +
                "\"layout\":{\"livePrefix\":\"\",\"histPrefix\":\".yabt-hist\"}," +
                "\"stores\":[{\"id\":\"target\",\"kind\":\"fileSystem\"}]}";
        var serializer = CreateSerializer();
        await using var source = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var exception = await Assert.ThrowsAsync<YabtMetadataException>(
            () => serializer.ReadAsync(source));

        StringAssert.Contains(exception.ToString(), "changeManifestCompresion");
    }

    [TestMethod]
    public async Task WriteAsyncRejectsUnsupportedChangeManifestCompression()
    {
        var descriptor = CreateDescriptor(
            BackupRootDescriptor.ExpectedSchemaVersion,
            "br");
        var serializer = CreateSerializer();
        await using var destination = new MemoryStream();

        var exception = await Assert.ThrowsAsync<YabtMetadataException>(
            () => serializer.WriteAsync(descriptor, destination));

        StringAssert.Contains(exception.Message, "change manifest compression");
    }

    [TestMethod]
    public async Task ReadAsyncRejectsUnsupportedSchemaVersion()
    {
        const string json =
            "{\"documentType\":\"yabt.backupRoot\",\"schemaVersion\":2," +
                "\"archiveId\":\"test-archive\",\"createdAtUtc\":\"2026-08-16T12:00:00Z\"," +
                "\"layout\":{\"livePrefix\":\"\",\"histPrefix\":\".yabt-hist\"}," +
                "\"stores\":[{\"id\":\"target\",\"kind\":\"fileSystem\"}]}";
        var serializer = CreateSerializer();
        await using var source = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var exception = await Assert.ThrowsAsync<YabtMetadataException>(
            () => serializer.ReadAsync(source));

        StringAssert.Contains(exception.Message, "schema version");
    }

    [TestMethod]
    public async Task WriteAsyncRejectsUnsupportedSchemaVersion()
    {
        var descriptor = CreateDescriptor(2);
        var serializer = CreateSerializer();
        await using var destination = new MemoryStream();

        var exception = await Assert.ThrowsAsync<YabtMetadataException>(
            () => serializer.WriteAsync(descriptor, destination));

        StringAssert.Contains(exception.Message, "schema version");
    }

    private static BackupRootDescriptor CreateDescriptor
    (
        int schemaVersion,
        string? changeManifestCompression = default,
        long? historyDeduplicationTinyFileMaximumBytes = default
    ) => new
    (
        BackupRootDescriptor.ExpectedDocumentType,
        schemaVersion,
        "test-archive",
        new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero),
        ArchiveLayout.Default,
        [new BackupRootStore("target", "fileSystem")],
        ChangeManifestCompression: changeManifestCompression,
        HistoryDeduplicationTinyFileMaximumBytes: historyDeduplicationTinyFileMaximumBytes
    );

    private static IBackupRootSerializer CreateSerializer()
    {
        var services = new ServiceCollection();
        services.AddYabtMetadata();

        return services.BuildServiceProvider().GetRequiredService<IBackupRootSerializer>();
    }
}
