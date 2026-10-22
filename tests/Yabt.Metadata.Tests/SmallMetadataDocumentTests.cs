using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Yabt.Metadata.Tests;

[TestClass]
public sealed class SmallMetadataDocumentTests
{
    private const string RootJson =
        "{\"documentType\":\"yabt.backupRoot\",\"schemaVersion\":1," +
        "\"archiveId\":\"bounded-test\",\"createdAtUtc\":\"2026-10-22T12:00:00Z\"," +
        "\"layout\":{\"livePrefix\":\"\",\"histPrefix\":\".yabt-hist\"}," +
        "\"stores\":[{\"id\":\"target\",\"kind\":\"fileSystem\"}]}";

    [TestMethod]
    public async Task RootDescriptorDefaultLimitRejectsTrailingWhitespaceBeforeParsing()
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<IBackupRootSerializer>();
        using var stream = new MemoryStream
        (
            Encoding.UTF8.GetBytes(RootJson + new string(' ', 1024 * 1024))
        );

        var error = await Assert.ThrowsExactlyAsync<YabtMetadataException>
        (
            () => serializer.ReadDocumentAsync(stream, sourceName: "source/.yabt-root.json")
        );

        Assert.AreEqual(1024 * 1024 + 1, stream.Position);
        StringAssert.Contains(error.ToString(), "source/.yabt-root.json");
        StringAssert.Contains(error.ToString(), "1048576 bytes");
    }

    [TestMethod]
    public async Task RootDescriptorExactLimitPreservesBomAndOriginalBytes()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(RootJson + "\r\n")];
        using var configuration = CreateConfiguration(rootMaximum: bytes.Length);
        using var services = CreateServices(configuration);
        var serializer = services.GetRequiredService<IBackupRootSerializer>();
        using var source = new MemoryStream(bytes, writable: false);

        var document = await serializer.ReadDocumentAsync(source);

        using var restored = new MemoryStream();
        await document.CopyToAsync(restored);
        CollectionAssert.AreEqual(bytes, restored.ToArray());
    }

    [TestMethod]
    public async Task RootDescriptorObservesReloadedTrustedRuntimeLimit()
    {
        var bytes = Encoding.UTF8.GetBytes(RootJson);
        using var configuration = CreateConfiguration(rootMaximum: bytes.Length - 1);
        using var services = CreateServices(configuration);
        var serializer = services.GetRequiredService<IBackupRootSerializer>();
        using var rejected = new MemoryStream(bytes);
        await Assert.ThrowsExactlyAsync<YabtMetadataException>
        (
            () => serializer.ReadDocumentAsync(rejected)
        );

        configuration["Metadata:RootDescriptorMaximumBytes"] = bytes.Length.ToString();
        configuration.Reload();
        using var accepted = new MemoryStream(bytes);

        var document = await serializer.ReadDocumentAsync(accepted);

        Assert.AreEqual("bounded-test", document.Descriptor.ArchiveId);
    }

    [TestMethod]
    public async Task RootWriterAppliesTheSameLimitBeforeWritingAnyDestinationBytes()
    {
        using var configuration = CreateConfiguration();
        using var services = CreateServices(configuration);
        var serializer = services.GetRequiredService<IBackupRootSerializer>();
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(RootJson));
        var descriptor = await serializer.ReadAsync(source);
        using var generated = new MemoryStream();
        await serializer.WriteAsync(descriptor, generated);

        configuration["Metadata:RootDescriptorMaximumBytes"] = (generated.Length - 1).ToString();
        configuration.Reload();
        using var rejected = new MemoryStream();
        var error = await Assert.ThrowsExactlyAsync<YabtMetadataException>
        (
            () => serializer.WriteAsync(descriptor, rejected)
        );
        StringAssert.Contains(error.ToString(), "maximum size");
        Assert.AreEqual(0, rejected.Length);

        configuration["Metadata:RootDescriptorMaximumBytes"] = generated.Length.ToString();
        configuration.Reload();
        using var accepted = new MemoryStream();
        await serializer.WriteAsync(descriptor, accepted);
        accepted.Position = 0;
        Assert.AreEqual(descriptor.ArchiveId, (await serializer.ReadAsync(accepted)).ArchiveId);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    public async Task RootDescriptorRejectsInvalidRuntimeLimits(int maximum)
    {
        using var configuration = CreateConfiguration(rootMaximum: maximum);
        using var services = CreateServices(configuration);
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(RootJson));

        var error = await Assert.ThrowsExactlyAsync<YabtMetadataException>
        (
            () => services.GetRequiredService<IBackupRootSerializer>().ReadDocumentAsync(source)
        );

        StringAssert.Contains(error.ToString(), nameof(YabtMetadataOptions.RootDescriptorMaximumBytes));
        Assert.AreEqual(0, source.Position);
    }

    [TestMethod]
    public async Task PolicyDefaultLimitRejectsOversizedDocumentsWithoutDefaulting()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"format\":\"zip\"}" + new string(' ', 1024 * 1024));
        await WithPolicyFileAsync
        (
            bytes,
            async path =>
            {
                using var services = CreateServices();
                var error = await Assert.ThrowsExactlyAsync<YabtMetadataException>
                (
                    () => services.GetRequiredService<IFolderPolicyReader>().ReadPolicyAsync(path)
                );
                StringAssert.Contains(error.ToString(), FolderPolicyFileNames.Primary);
                StringAssert.Contains(error.ToString(), "1048576 bytes");
            }
        );
    }

    [TestMethod]
    public async Task PolicyExactLimitAcceptsBomAndCountsUtf8Bytes()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("{\"format\":\"zip\",\"note\":\"ä\"}")];
        await WithPolicyFileAsync
        (
            bytes,
            async path =>
            {
                using var configuration = CreateConfiguration(policyMaximum: bytes.Length);
                using var services = CreateServices(configuration);
                var reader = services.GetRequiredService<IFolderPolicyReader>();
                Assert.AreEqual("zip", (await reader.ReadPolicyAsync(path)).Format);

                configuration["Metadata:FolderPolicyMaximumBytes"] = (bytes.Length - 1).ToString();
                configuration.Reload();
                await Assert.ThrowsExactlyAsync<YabtMetadataException>(() => reader.ReadPolicyAsync(path));
            }
        );
    }

    private static ConfigurationRoot CreateConfiguration(int? rootMaximum = null, int? policyMaximum = null)
    {
        return (ConfigurationRoot)new ConfigurationBuilder().AddInMemoryCollection
        (
            new Dictionary<string, string?>
            {
                ["Metadata:RootDescriptorMaximumBytes"] = rootMaximum?.ToString(),
                ["Metadata:FolderPolicyMaximumBytes"] = policyMaximum?.ToString(),
            }
        ).Build();
    }

    private static ServiceProvider CreateServices(IConfiguration? configuration = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        if (configuration is not null) { services.AddSingleton(configuration); }
        services.AddYabtMetadata(configuration is null ? null : "Metadata");
        return services.BuildServiceProvider();
    }

    private static async Task WithPolicyFileAsync(byte[] bytes, Func<string, Task> action)
    {
        var folder = Path.Combine(Path.GetTempPath(), $"yabt-bounded-policy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(folder, FolderPolicyFileNames.Primary), bytes);
            await action(folder);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
