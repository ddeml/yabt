using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yabt.Core.Models;
using Yabt.Metadata;

namespace Yabt.Metadata.Tests;

[TestClass]
public sealed class BackupRootDocumentTests
{
    [TestMethod]
    public async Task ReadDocumentAsyncPreservesExactValidatedBytes()
    {
        const string json =
            "{\r\n" +
                "  \"documentType\": \"yabt.backupRoot\",\r\n" +
                "  \"schemaVersion\": 1,\r\n" +
                "  \"archiveId\": \"test-archive\",\r\n" +
                "  \"createdAtUtc\": \"2026-08-24T12:00:00Z\",\r\n" +
                "  \"layout\": { \"livePrefix\": \"\", \"histPrefix\": \".yabt-hist\" },\r\n" +
                "  \"stores\": [{ \"id\": \"target\", \"kind\": \"fileSystem\" }]\r\n" +
                "}\r\n";
        var utf8WithBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        byte[] content = [.. utf8WithBom.GetPreamble(), .. Encoding.UTF8.GetBytes(json)];
        var serializer = CreateServiceProvider().GetRequiredService<IBackupRootSerializer>();
        await using var source = new MemoryStream(content, writable: false);

        var document = await serializer.ReadDocumentAsync(source);

        Assert.AreEqual("test-archive", document.Descriptor.ArchiveId);
        Assert.AreEqual(content.LongLength, document.ContentLength);
        Assert.AreEqual(ArchiveHash.Compute(content), document.ContentHash);
        await using var restored = new MemoryStream();
        await document.CopyToAsync(restored);
        CollectionAssert.AreEqual(content, restored.ToArray());

        await using var opened = document.OpenRead();
        Assert.IsFalse(opened.CanWrite);
        await using var openedCopy = new MemoryStream();
        await opened.CopyToAsync(openedCopy);
        CollectionAssert.AreEqual(content, openedCopy.ToArray());
    }

    [TestMethod]
    public async Task ContentEqualsComparesSerializedBytesRatherThanDescriptorValues()
    {
        const string compactJson =
            "{\"documentType\":\"yabt.backupRoot\",\"schemaVersion\":1," +
                "\"archiveId\":\"test-archive\",\"createdAtUtc\":\"2026-08-24T12:00:00Z\"," +
                "\"layout\":{\"livePrefix\":\"\",\"histPrefix\":\".yabt-hist\"}," +
                "\"stores\":[{\"id\":\"target\",\"kind\":\"fileSystem\"}]}";
        const string formattedJson =
            "{ \"documentType\": \"yabt.backupRoot\", \"schemaVersion\": 1," +
                " \"archiveId\": \"test-archive\"," +
                " \"createdAtUtc\": \"2026-08-24T12:00:00Z\"," +
                " \"layout\": { \"livePrefix\": \"\", \"histPrefix\": \".yabt-hist\" }," +
                " \"stores\": [{ \"id\": \"target\", \"kind\": \"fileSystem\" }] }";
        var serializer = CreateServiceProvider().GetRequiredService<IBackupRootSerializer>();
        await using var compactSource = new MemoryStream(Encoding.UTF8.GetBytes(compactJson));
        await using var formattedSource = new MemoryStream(Encoding.UTF8.GetBytes(formattedJson));

        var compact = await serializer.ReadDocumentAsync(compactSource);
        var formatted = await serializer.ReadDocumentAsync(formattedSource);

        Assert.AreEqual(compact.Descriptor.ArchiveId, formatted.Descriptor.ArchiveId);
        Assert.IsTrue(compact.ContentEquals(compact));
        Assert.IsFalse(compact.ContentEquals(formatted));
        Assert.IsFalse(compact.ContentEquals(null));
        Assert.AreNotEqual(compact.ContentHash, formatted.ContentHash);
    }

    [TestMethod]
    public async Task LocateRootAsyncReturnsTheExactDescriptorSnapshot()
    {
        const string json =
            "{\r\n" +
                "  \"documentType\": \"yabt.backupRoot\",\r\n" +
                "  \"schemaVersion\": 1,\r\n" +
                "  \"archiveId\": \"located-archive\",\r\n" +
                "  \"createdAtUtc\": \"2026-08-24T12:00:00Z\",\r\n" +
                "  \"layout\": { \"livePrefix\": \"\", \"histPrefix\": \".yabt-hist\" },\r\n" +
                "  \"stores\": [{ \"id\": \"target\", \"kind\": \"fileSystem\" }]\r\n" +
                "}\r\n";
        var workspace = Path.Combine(
            Path.GetTempPath(),
            $"yabt-root-document-{Guid.NewGuid():N}");
        var childPath = Path.Combine(workspace, "child");
        var descriptorPath = Path.Combine(workspace, BackupRootFileNames.Primary);
        var content = Encoding.UTF8.GetBytes(json);

        try
        {
            Directory.CreateDirectory(childPath);
            await File.WriteAllBytesAsync(descriptorPath, content);
            var locator = CreateServiceProvider().GetRequiredService<IBackupRootLocator>();

            var location = await locator.LocateRootAsync(childPath);

            Assert.AreEqual(Path.GetFullPath(workspace), location.RootPath);
            Assert.AreEqual("located-archive", location.Descriptor.ArchiveId);
            Assert.IsNotNull(location.Document);
            Assert.AreSame(location.Descriptor, location.Document.Descriptor);
            await using var preserved = new MemoryStream();
            await location.Document.CopyToAsync(preserved);
            CollectionAssert.AreEqual(content, preserved.ToArray());
        }
        finally
        {
            if (Directory.Exists(workspace))
            {
                Directory.Delete(workspace, recursive: true);
            }
        }
    }

    [TestMethod]
    public void AddYabtMetadataResolvesFilesystemMetadataReaders()
    {
        using var provider = CreateServiceProvider();

        Assert.IsNotNull(provider.GetRequiredService<IBackupRootLocator>());
        Assert.IsNotNull(provider.GetRequiredService<IBackupRootReader>());
        Assert.IsNotNull(provider.GetRequiredService<IFolderPolicyReader>());
    }

    private static ServiceProvider CreateServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddYabtMetadata();
        return services.BuildServiceProvider();
    }
}
