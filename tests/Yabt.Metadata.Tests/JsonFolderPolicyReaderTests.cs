using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yabt.Core.Models;

namespace Yabt.Metadata.Tests;

[TestClass]
public sealed class JsonFolderPolicyReaderTests
{
    [TestMethod]
    public async Task ReadPolicyAsyncPreservesDirectoryLinkNamesAndPortableTargets()
    {
        var policy = await ReadPolicyAsync("""
            {
              "format": "zip",
              "directoryLinks": {
                " photos": { "behavior": "follow", "expectedTarget": "D:\\Photos" },
                "Photos": { "behavior": "follow", "expectedTarget": "../other/photos" },
                " ": { "behavior": "follow", "expectedTarget": " " }
              }
            }
            """);

        Assert.IsNotNull(policy.DirectoryLinks);
        Assert.HasCount(3, policy.DirectoryLinks);
        Assert.AreEqual(new DirectoryLinkPolicy("follow", @"D:\Photos"), policy.DirectoryLinks[" photos"]);
        Assert.AreEqual("../other/photos", policy.DirectoryLinks["Photos"].ExpectedTarget);
        Assert.AreEqual(" ", policy.DirectoryLinks[" "].ExpectedTarget);
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("{\"format\":\"mirror\"}")]
    [DataRow("{\"directoryLinks\":null}")]
    [DataRow("{\"directoryLinks\":{}}")]
    public async Task ReadPolicyAsyncAcceptsAbsentOrEmptyDirectoryLinks(string json)
    {
        var policy = await ReadPolicyAsync(json);

        Assert.IsNull(policy.DirectoryLinks);
    }

    [TestMethod]
    [DataRow("{\"\":{\"behavior\":\"follow\",\"expectedTarget\":\"../photos\"}}")]
    [DataRow("{\".\":{\"behavior\":\"follow\",\"expectedTarget\":\"../photos\"}}")]
    [DataRow("{\"..\":{\"behavior\":\"follow\",\"expectedTarget\":\"../photos\"}}")]
    [DataRow("{\"child/link\":{\"behavior\":\"follow\",\"expectedTarget\":\"../photos\"}}")]
    [DataRow("{\"child\\\\link\":{\"behavior\":\"follow\",\"expectedTarget\":\"../photos\"}}")]
    [DataRow("{\"child\\u0000\":{\"behavior\":\"follow\",\"expectedTarget\":\"../photos\"}}")]
    [DataRow("{\"link\":null}")]
    [DataRow("{\"link\":{\"expectedTarget\":\"../photos\"}}")]
    [DataRow("{\"link\":{\"behavior\":\"follow\"}}")]
    [DataRow("{\"link\":{\"behavior\":\"skip\",\"expectedTarget\":\"../photos\"}}")]
    [DataRow("{\"link\":{\"behavior\":\"Follow\",\"expectedTarget\":\"../photos\"}}")]
    [DataRow("{\"link\":{\"behavior\":\"follow\",\"expectedTarget\":\"\"}}")]
    [DataRow("{\"link\":{\"behavior\":\"follow\",\"expectedTarget\":\"../photos\\u0000\"}}")]
    [DataRow("{\"link\":{\"behavior\":\"follow\",\"expectedTarget\":\"../photos\",\"recursive\":true}}")]
    [DataRow("{\"link\":{\"behavior\":\"follow\",\"expectedTarget\":\"a\",\"expectedTarget\":\"b\"}}")]
    [DataRow("{\"link\":{\"behavior\":\"follow\",\"expectedTarget\":\"a\"},\"link\":{\"behavior\":\"follow\",\"expectedTarget\":\"b\"}}")]
    [DataRow("[]")]
    public async Task ReadPolicyAsyncRejectsInvalidDirectoryLinks(string directoryLinksJson)
    {
        var json = "{\"format\":\"mirror\",\"directoryLinks\":" + directoryLinksJson + "}";

        await Assert.ThrowsExactlyAsync<YabtMetadataException>(() => ReadPolicyAsync(json));
    }

    private static async Task<FolderPolicy> ReadPolicyAsync(string json)
    {
        var folderPath = Path.Combine(Path.GetTempPath(), $"yabt-link-policy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folderPath);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(folderPath, FolderPolicyFileNames.Primary), json);
            var services = new ServiceCollection();
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            services.AddYabtMetadata();
            using var provider = services.BuildServiceProvider();
            return await provider.GetRequiredService<IFolderPolicyReader>().ReadPolicyAsync(folderPath);
        }
        finally
        {
            Directory.Delete(folderPath, recursive: true);
        }
    }
}
