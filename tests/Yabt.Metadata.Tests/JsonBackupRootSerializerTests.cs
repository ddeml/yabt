using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Yabt.Core.Models;
using Yabt.Metadata;

namespace Yabt.Metadata.Tests;

[TestClass]
public sealed class JsonBackupRootSerializerTests
{
    [TestMethod]
    public async Task WriteAndReadAsyncRoundTripsStoreConfigSectionPath()
    {
        const string configSectionPath = "ObjectStores:MainAzure";
        var descriptor = new BackupRootDescriptor
        (
            BackupRootDescriptor.ExpectedDocumentType,
            BackupRootDescriptor.ExpectedSchemaVersion,
            "test-archive",
            new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero),
            ArchiveLayout.Default,
            [new BackupRootStore("target", "azureBlob", ConfigSectionPath: configSectionPath)]
        );
        var serializer = CreateSerializer();
        await using var document = new MemoryStream();

        await serializer.WriteAsync(descriptor, document);
        document.Position = 0;
        var restored = await serializer.ReadAsync(document);

        var restoredStore = restored.Stores.Single();
        Assert.AreEqual(configSectionPath, restoredStore.ConfigSectionPath);
        Assert.IsNull(restoredStore.CredentialRef);
        Assert.IsNull(restoredStore.ProviderProperties);
    }

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
    public async Task ReadAsyncRejectsMissingArchiveId()
    {
        var json = CreateRootJson(archiveIdProperty: null);

        var exception = await AssertReadRejectedAsync(json);

        StringAssert.Contains(exception.ToString(), "archiveId");
    }

    [TestMethod]
    public async Task ReadAsyncRejectsNullArchiveId()
    {
        var json = CreateRootJson(archiveIdProperty: "\"archiveId\":null");

        var exception = await AssertReadRejectedAsync(json);

        StringAssert.Contains(exception.Message, "archive id is required");
    }

    [TestMethod]
    public async Task ReadAsyncRejectsMissingLayout()
    {
        var json = CreateRootJson(layoutProperty: null);

        var exception = await AssertReadRejectedAsync(json);

        StringAssert.Contains(exception.ToString(), "layout");
    }

    [TestMethod]
    public async Task ReadAsyncRejectsNullLayout()
    {
        var json = CreateRootJson(layoutProperty: "\"layout\":null");

        var exception = await AssertReadRejectedAsync(json);

        StringAssert.Contains(exception.Message, "layout is required");
    }

    [TestMethod]
    public async Task ReadAsyncRejectsMissingStores()
    {
        var json = CreateRootJson(storesProperty: null);

        var exception = await AssertReadRejectedAsync(json);

        StringAssert.Contains(exception.ToString(), "stores");
    }

    [TestMethod]
    public async Task ReadAsyncRejectsNullStores()
    {
        var json = CreateRootJson(storesProperty: "\"stores\":null");

        var exception = await AssertReadRejectedAsync(json);

        StringAssert.Contains(exception.Message, "stores collection is required");
    }

    [TestMethod]
    public async Task ReadAsyncRejectsEmptyStoreId()
    {
        var json = CreateRootJson(
            storesProperty: "\"stores\":[{\"id\":\"\",\"kind\":\"fileSystem\"}]");

        var exception = await AssertReadRejectedAsync(json);

        StringAssert.Contains(exception.Message, "object store id is required");
    }

    [TestMethod]
    public async Task ReadAsyncRejectsDuplicateStoreIdsIgnoringCase()
    {
        var json = CreateRootJson(
            storesProperty:
                "\"stores\":[{\"id\":\"target\",\"kind\":\"fileSystem\"}," +
                    "{\"id\":\"TARGET\",\"kind\":\"azureBlob\"}]");

        var exception = await AssertReadRejectedAsync(json);

        StringAssert.Contains(exception.Message, "duplicate object store id");
    }

    [TestMethod]
    public async Task ReadAsyncRejectsInvalidRootRole()
    {
        var json = CreateRootJson(additionalProperty: "\"rootRole\":\"Source\"");

        var exception = await AssertReadRejectedAsync(json);

        StringAssert.Contains(exception.Message, "root role");
    }

    [TestMethod]
    public async Task ReadAsyncRejectsUnknownLayoutProperty()
    {
        var json = CreateRootJson(
            layoutProperty:
                "\"layout\":{\"livePrefix\":\"\",\"histPrefix\":\".yabt-hist\"," +
                    "\"historyPrefix\":\"hist\"}");

        var exception = await AssertReadRejectedAsync(json);

        StringAssert.Contains(exception.ToString(), "historyPrefix");
    }

    [TestMethod]
    public async Task ReadAsyncRejectsIncorrectlyCasedRootPropertyName()
    {
        var json = CreateRootJson(archiveIdProperty: "\"ArchiveId\":\"test-archive\"");

        var exception = await AssertReadRejectedAsync(json);

        StringAssert.Contains(exception.ToString(), "ArchiveId");
    }

    [TestMethod]
    public async Task ReadAsyncRejectsIncorrectlyCasedLayoutPropertyName()
    {
        var json = CreateRootJson(
            layoutProperty: "\"layout\":{\"LivePrefix\":\"\",\"histPrefix\":\".yabt-hist\"}");

        var exception = await AssertReadRejectedAsync(json);

        StringAssert.Contains(exception.ToString(), "LivePrefix");
    }

    [DataRow("\"credentialRef\":\"legacy-secret\"")]
    [DataRow("\"connectionString\":\"UseDevelopmentStorage=true\"")]
    [TestMethod]
    public async Task ReadAsyncRejectsForbiddenUnselectedAzureStoreProperty
    (
        string forbiddenProperty
    )
    {
        var json = CreateRootJson
        (
            storesProperty:
                "\"stores\":[{\"id\":\"selected\",\"kind\":\"fileSystem\"," +
                    "\"rootPath\":\"archive\"},{\"id\":\"unselected-azure\"," +
                    $"\"kind\":\"azureBlob\",{forbiddenProperty}}}]",
            additionalProperty: "\"defaultStoreId\":\"selected\""
        );

        await AssertReadRejectedAsync(json);
    }

    [TestMethod]
    public async Task ReadAsyncRejectsExplicitNullCredentialRefForAzureStore()
    {
        var json = CreateRootJson
        (
            storesProperty:
                "\"stores\":[{\"id\":\"target\",\"kind\":\"azureBlob\"," +
                    "\"credentialRef\":null}]"
        );

        await AssertReadRejectedAsync(json);
    }

    [TestMethod]
    public async Task ReadAsyncRejectsConfigSectionPathForNonAzureStore()
    {
        var json = CreateRootJson
        (
            storesProperty:
                "\"stores\":[{\"id\":\"target\",\"kind\":\"fileSystem\"," +
                    "\"rootPath\":\"archive\"," +
                    "\"configSectionPath\":\"ObjectStores:FileSystem\"}]"
        );

        await AssertReadRejectedAsync(json);
    }

    [TestMethod]
    public async Task ReadAsyncRejectsExplicitNullConfigSectionPathForNonAzureStore()
    {
        var json = CreateRootJson
        (
            storesProperty:
                "\"stores\":[{\"id\":\"target\",\"kind\":\"fileSystem\"," +
                    "\"rootPath\":\"archive\",\"configSectionPath\":null}]"
        );

        await AssertReadRejectedAsync(json);
    }

    [TestMethod]
    public async Task ReadAsyncRejectsNullLivePrefix()
    {
        var json = CreateRootJson(
            layoutProperty: "\"layout\":{\"livePrefix\":null,\"histPrefix\":\".yabt-hist\"}");

        await AssertReadRejectedAsync(json);
    }

    [TestMethod]
    public async Task ReadAsyncRejectsDuplicateJsonProperties()
    {
        var json = CreateRootJson(
            archiveIdProperty:
                "\"archiveId\":\"test-archive\",\"archiveId\":\"test-archive\"");

        await AssertReadRejectedAsync(json);
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

    private static string CreateRootJson
    (
        string? archiveIdProperty = "\"archiveId\":\"test-archive\"",
        string? layoutProperty =
            "\"layout\":{\"livePrefix\":\"\",\"histPrefix\":\".yabt-hist\"}",
        string? storesProperty =
            "\"stores\":[{\"id\":\"target\",\"kind\":\"fileSystem\"}]",
        string? additionalProperty = default
    )
    {
        List<string> properties =
        [
            "\"documentType\":\"yabt.backupRoot\"",
            "\"schemaVersion\":1",
            "\"createdAtUtc\":\"2026-08-16T12:00:00Z\"",
        ];

        AddProperty(archiveIdProperty);
        AddProperty(layoutProperty);
        AddProperty(storesProperty);
        AddProperty(additionalProperty);
        return $"{{{string.Join(',', properties)}}}";

        void AddProperty(string? property)
        {
            if (property is not null)
            {
                properties.Add(property);
            }
        }
    }

    private static async Task<YabtMetadataException> AssertReadRejectedAsync(string json)
    {
        var serializer = CreateSerializer();
        await using var source = new MemoryStream(Encoding.UTF8.GetBytes(json));

        return await Assert.ThrowsAsync<YabtMetadataException>(
            () => serializer.ReadAsync(source));
    }

    private static IBackupRootSerializer CreateSerializer()
    {
        var services = new ServiceCollection();
        services.AddYabtMetadata();

        return services.BuildServiceProvider().GetRequiredService<IBackupRootSerializer>();
    }
}
