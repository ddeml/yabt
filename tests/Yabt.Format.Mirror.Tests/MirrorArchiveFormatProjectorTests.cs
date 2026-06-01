using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.Tests;

namespace Yabt.Format.Mirror.Tests;

[TestClass]
public sealed class MirrorArchiveFormatProjectorTests
{
    [TestMethod]
    public void ServiceRegistrationRegistersMirrorFormatProjector()
    {
        using var serviceProvider = CreateServices().BuildServiceProvider();

        var projectors = serviceProvider.GetServices<IArchiveFormatProjector>().ToArray();

        Assert.AreEqual(1, projectors.Length);
        var projector = projectors[0];
        Assert.AreEqual(MirrorArchiveFormatName.Value, projector.FormatName);
    }

    [TestMethod]
    public async Task ProjectAsyncMapsSourceObjectsOneToOne()
    {
        using var serviceProvider = CreateServices().BuildServiceProvider();
        var projector = serviceProvider.GetRequiredService<IArchiveFormatProjector>();
        var sourceStore = new MemoryObjectStore();

        await UploadTextAsync(sourceStore, "folder/file.txt", "source content");

        var projection = await projector.ProjectAsync(new
        (
            sourceStore,
            Policy: new FolderPolicy(MirrorArchiveFormatName.Value)
        ));

        var projectedObject = projection.Objects.Single();
        Assert.AreEqual("folder/file.txt", projectedObject.RelativePath);
        await AssertProjectedTextAsync(projectedObject, "source content");
    }

    [TestMethod]
    public async Task ProjectAsyncRemovesConfiguredSourcePrefix()
    {
        using var serviceProvider = CreateServices().BuildServiceProvider();
        var projector = serviceProvider.GetRequiredService<IArchiveFormatProjector>();
        var sourceStore = new MemoryObjectStore();

        await UploadTextAsync(sourceStore, "live/folder/file.txt", "source content");

        var projection = await projector.ProjectAsync(new
        (
            sourceStore,
            SourcePrefix: "live",
            Policy: new FolderPolicy(MirrorArchiveFormatName.Value)
        ));

        var projectedObject = projection.Objects.Single();
        Assert.AreEqual("folder/file.txt", projectedObject.RelativePath);
    }

    private static ServiceCollection CreateServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddYabtMirrorFormatProjector();

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

    private static async Task AssertProjectedTextAsync
    (
        ArchiveProjectedObject projectedObject,
        string expectedContent
    )
    {
        await using var content = await projectedObject.OpenContentAsync(default);
        using var reader = new StreamReader(content.Content, Encoding.UTF8);
        Assert.AreEqual(expectedContent, await reader.ReadToEndAsync());
    }
}
