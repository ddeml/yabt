using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Yabt.Common;
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
    public async Task ReadAsyncWarnsAndIgnoresUnknownRootProperty()
    {
        const string json =
            "{\"documentType\":\"yabt.backupRoot\",\"schemaVersion\":1," +
                "\"archiveId\":\"test-archive\",\"createdAtUtc\":\"2026-08-16T12:00:00Z\"," +
                "\"changeManifestCompresion\":\"none\"," +
                "\"layout\":{\"livePrefix\":\"\",\"histPrefix\":\".yabt-hist\"}," +
                "\"stores\":[{\"id\":\"target\",\"kind\":\"fileSystem\"}]}";
        List<string> warnings = [];
        var serializer = CreateSerializer(warnings);
        await using var source = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var descriptor = await serializer.ReadAsync(source, sourceName: "source/.yabt-root.json");

        Assert.IsNull(descriptor.ChangeManifestCompression);
        Assert.HasCount(1, warnings);
        StringAssert.Contains(warnings[0], "source/.yabt-root.json");
        StringAssert.Contains(warnings[0], "$[\"changeManifestCompresion\"]");
        StringAssert.Contains(warnings[0], "ignored");
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
    public async Task ReadAsyncWarnsAndIgnoresUnknownLayoutProperty()
    {
        var json = CreateRootJson(
            layoutProperty:
                "\"layout\":{\"livePrefix\":\"\",\"histPrefix\":\".yabt-hist\"," +
                    "\"historyPrefix\":\"hist\"}");

        List<string> warnings = [];
        var serializer = CreateSerializer(warnings);
        await using var source = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var descriptor = await serializer.ReadAsync(source);

        Assert.AreEqual(".yabt-hist", descriptor.Layout.HistPrefix);
        Assert.HasCount(1, warnings);
        StringAssert.Contains(warnings[0], ".yabt-root.json");
        StringAssert.Contains(warnings[0], "$.layout[\"historyPrefix\"]");
    }

    [TestMethod]
    public async Task ReadAsyncRejectsIncorrectlyCasedRootPropertyName()
    {
        var json = CreateRootJson(archiveIdProperty: "\"ArchiveId\":\"test-archive\"");

        var exception = await AssertReadRejectedAsync(json);

        StringAssert.Contains(exception.ToString(), "archiveId");
    }

    [TestMethod]
    public async Task ReadAsyncRejectsIncorrectlyCasedLayoutPropertyName()
    {
        var json = CreateRootJson(
            layoutProperty: "\"layout\":{\"LivePrefix\":\"\",\"histPrefix\":\".yabt-hist\"}");

        var exception = await AssertReadRejectedAsync(json);

        StringAssert.Contains(exception.ToString(), "livePrefix");
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
    public async Task ReadAsyncLeavesNonAzureConfigSectionPathValidationToProvider()
    {
        var json = CreateRootJson
        (
            storesProperty:
                "\"stores\":[{\"id\":\"target\",\"kind\":\"fileSystem\"," +
                    "\"rootPath\":\"archive\"," +
                    "\"configSectionPath\":\"ObjectStores:FileSystem\"}]"
        );

        var serializer = CreateSerializer();
        await using var source = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var descriptor = await serializer.ReadAsync(source);

        Assert.AreEqual("ObjectStores:FileSystem", descriptor.Stores.Single().ConfigSectionPath);
    }

    [TestMethod]
    public async Task ReadAsyncAcceptsExplicitNullConfigSectionPathForNonAzureStore()
    {
        var json = CreateRootJson
        (
            storesProperty:
                "\"stores\":[{\"id\":\"target\",\"kind\":\"fileSystem\"," +
                    "\"rootPath\":\"archive\",\"configSectionPath\":null}]"
        );

        var serializer = CreateSerializer();
        await using var source = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var descriptor = await serializer.ReadAsync(source);

        Assert.IsNull(descriptor.Stores.Single().ConfigSectionPath);
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

    [DataRow(false)]
    [DataRow(true)]
    [TestMethod]
    public async Task ReadDocumentAsyncPreservesUnknownPropertiesAndExactBytes(bool includePreamble)
    {
        const string privateValue = "must-not-be-logged";
        var json = CreateRootJson
        (
            layoutProperty:
                "\"layout\":{\"livePrefix\":\"\",\"histPrefix\":\".yabt-hist\",\"futureLayout\":null}",
            additionalProperty: $"\"futureRoot\":{{\"nested\":\"{privateValue}\"}}"
        );
        var bytes = Encoding.UTF8.GetBytes($"  {json}\r\n");
        if (includePreamble) { bytes = [.. Encoding.UTF8.GetPreamble(), .. bytes]; }
        List<string> warnings = [];
        var serializer = CreateSerializer(warnings);
        await using var source = new MemoryStream(bytes);

        var document = await serializer.ReadDocumentAsync(source);

        await using var preserved = new MemoryStream();
        await document.CopyToAsync(preserved);
        CollectionAssert.AreEqual(bytes, preserved.ToArray());
        Assert.HasCount(2, warnings);
        Assert.IsFalse(warnings.Any(warning => warning.Contains(privateValue, StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ReadAsyncDoesNotWarnAboutProviderOwnedProperties()
    {
        var json = CreateRootJson
        (
            storesProperty:
                "\"stores\":[{\"id\":\"target\",\"kind\":\"fileSystem\"," +
                    "\"rootPath\":\"archive\",\"futureProviderSetting\":42}]"
        );
        List<string> warnings = [];
        var serializer = CreateSerializer(warnings);
        await using var source = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var descriptor = await serializer.ReadAsync(source);

        Assert.IsEmpty(warnings);
        Assert.AreEqual(42, descriptor.Stores.Single().ProviderProperties!["futureProviderSetting"].GetInt32());
    }

    [TestMethod]
    public async Task ReadAsyncDeduplicatesWarningsWithinOneOperation()
    {
        var json = CreateRootJson(additionalProperty: "\"futureRoot\":true");
        List<string> warnings = [];
        var serializer = CreateSerializer(warnings);
        using (ConfigurationWarnings.BeginOperation())
        {
            await using var first = new MemoryStream(Encoding.UTF8.GetBytes(json));
            await using var second = new MemoryStream(Encoding.UTF8.GetBytes(json));
            await serializer.ReadAsync(first, sourceName: "source/.yabt-root.json");
            await serializer.ReadAsync(second, sourceName: "source/.yabt-root.json");
        }

        Assert.HasCount(1, warnings);
        using (ConfigurationWarnings.BeginOperation())
        {
            await using var next = new MemoryStream(Encoding.UTF8.GetBytes(json));
            await serializer.ReadAsync(next, sourceName: "source/.yabt-root.json");
        }
        Assert.HasCount(2, warnings);
    }

    [TestMethod]
    public async Task ReadAsyncStillRejectsDuplicateUnknownRootProperties()
    {
        var json = CreateRootJson(additionalProperty: "\"futureRoot\":true,\"futureRoot\":false");

        await AssertReadRejectedAsync(json);
    }

    [TestMethod]
    public async Task ReadAsyncRejectsDuplicateUnknownLayoutProperties()
    {
        var json = CreateRootJson
        (
            layoutProperty:
                "\"layout\":{\"livePrefix\":\"\",\"histPrefix\":\".yabt-hist\"," +
                    "\"futureLayout\":true,\"futureLayout\":false}"
        );

        await AssertReadRejectedAsync(json);
    }

    [TestMethod]
    public async Task ReadAsyncRejectsDuplicatePropertiesInsideIgnoredSettings()
    {
        var json = CreateRootJson
        (
            additionalProperty: "\"futureRoot\":{\"nested\":true,\"nested\":false}"
        );

        await AssertReadRejectedAsync(json);
    }

    [TestMethod]
    public async Task ReadAsyncRejectsDuplicatePropertiesInsideProviderSettings()
    {
        var json = CreateRootJson
        (
            storesProperty:
                "\"stores\":[{\"id\":\"target\",\"kind\":\"fileSystem\"," +
                    "\"futureProvider\":{\"nested\":true,\"nested\":false}}]"
        );

        await AssertReadRejectedAsync(json);
    }

    [DataRow("schemaVersion")]
    [DataRow("historyDeduplicationTinyFileMaximumBytes")]
    [TestMethod]
    public async Task ReadAsyncRejectsStringValuesForNumericSettings(string propertyName)
    {
        var json = propertyName == "schemaVersion" ?
            CreateRootJson().Replace("\"schemaVersion\":1", "\"schemaVersion\":\"1\"", StringComparison.Ordinal) :
            CreateRootJson(additionalProperty: "\"historyDeduplicationTinyFileMaximumBytes\":\"4096\"");

        var error = await AssertReadRejectedAsync(json);

        StringAssert.Contains(error.ToString(), propertyName);
    }

    private static IBackupRootSerializer CreateSerializer(List<string>? warnings = null)
    {
        var services = new ServiceCollection();
        if (warnings is not null)
        {
            services.AddSingleton(warnings);
            services.AddSingleton(typeof(ILogger<>), typeof(CapturingRootConfigurationLogger<>));
        }
        services.AddYabtMetadata();

        return services.BuildServiceProvider().GetRequiredService<IBackupRootSerializer>();
    }

    private sealed class CapturingRootConfigurationLogger<T>(List<string> warnings) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Warning;

        public void Log<TState>
        (
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            if (logLevel == LogLevel.Warning) { warnings.Add(formatter(state, exception)); }
        }
    }
}
