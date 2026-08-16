using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Yabt.Core.Models;
using Yabt.Metadata;

namespace Yabt.Metadata.Tests;

[TestClass]
public sealed class JsonBackupRootSerializerTests
{
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
        var descriptor = new BackupRootDescriptor
        (
            BackupRootDescriptor.ExpectedDocumentType,
            2,
            "test-archive",
            new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero),
            ArchiveLayout.Default,
            [new BackupRootStore("target", "fileSystem")]
        );
        var serializer = CreateSerializer();
        await using var destination = new MemoryStream();

        var exception = await Assert.ThrowsAsync<YabtMetadataException>(
            () => serializer.WriteAsync(descriptor, destination));

        StringAssert.Contains(exception.Message, "schema version");
    }

    private static IBackupRootSerializer CreateSerializer()
    {
        var services = new ServiceCollection();
        services.AddYabtMetadata();

        return services.BuildServiceProvider().GetRequiredService<IBackupRootSerializer>();
    }
}
