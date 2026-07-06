using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.Tests;

namespace Yabt.Format.Zip.Tests;

[TestClass]
public sealed class ZipArchiveFormatProjectorTests
{
    [TestMethod]
    public void ServiceRegistrationRegistersZipFormatProjector()
    {
        using var serviceProvider = CreateServices().BuildServiceProvider();

        var projectors = serviceProvider.GetServices<IArchiveFormatProjector>().ToArray();

        Assert.AreEqual(1, projectors.Length);
        var projector = projectors[0];
        Assert.AreEqual(ZipArchiveFormatName.Value, projector.FormatName);
    }

    [TestMethod]
    public async Task ProjectAsyncProjectsSourceFolderToSingleZipPackage()
    {
        using var serviceProvider = CreateServices().BuildServiceProvider();
        var projector = serviceProvider.GetRequiredService<IArchiveFormatProjector>();
        var sourceStore = new MemoryObjectStore(provideContentHash: true);

        await UploadTextAsync(sourceStore, "folder/file.txt", "source content");

        var projectedObjects = await CollectProjectedObjectsAsync(projector.ProjectAsync(new
        (
            sourceStore,
            Policy: new FolderPolicy(ZipArchiveFormatName.Value),
            SourceDisplayName: "Photos"
        )));

        var projectedObject = projectedObjects.Single();
        StringAssert.StartsWith(projectedObject.RelativePath, "Photos.");
        StringAssert.EndsWith(projectedObject.RelativePath, ".zip");

        await using var content = await projectedObject.OpenContentAsync(default);
        using var archive = new ZipArchive(content.Content, ZipArchiveMode.Read);
        var entry = archive.GetEntry("folder/file.txt");

        Assert.IsNotNull(entry);
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        Assert.AreEqual("source content", await reader.ReadToEndAsync());
    }

    [TestMethod]
    public async Task MemoryObjectStoreGetFolderItemsAsyncProvidesContentHashWhenEnabled()
    {
        var sourceStore = new MemoryObjectStore(provideContentHash: true);

        await UploadTextAsync(sourceStore, "folder/file.txt", "source content");

        var sourceObjects = new List<ArchiveObjectInfo>();
        var sourceFolderItems = sourceStore.GetFolderItemsAsync("folder");
        await foreach (var sourceFolderItem in sourceFolderItems)
        {
            if (sourceFolderItem.Object is not null)
            {
                sourceObjects.Add(sourceFolderItem.Object);
            }
        }

        var contentHash = sourceObjects.Single().ContentHash ?? string.Empty;
        StringAssert.StartsWith(contentHash, "xxh128:");
        Assert.AreEqual(39, contentHash.Length);
    }

    private static ServiceCollection CreateServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddYabtZipFormatProjector();

        return services;
    }

    private static async Task UploadTextAsync
    (
        MemoryObjectStore store,
        string key,
        string content
    )
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        await store.UploadAsync(
            key,
            stream,
            "text/plain",
            new Dictionary<string, string>(StringComparer.Ordinal));
    }

    private static async Task<IReadOnlyList<ArchiveProjectedObject>> CollectProjectedObjectsAsync
    (
        IAsyncEnumerable<ArchiveProjectedObject> projectedObjects
    )
    {
        var result = new List<ArchiveProjectedObject>();
        await foreach (var projectedObject in projectedObjects)
        {
            result.Add(projectedObject);
        }

        return result;
    }
}
