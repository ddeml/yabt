using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yabt.Core;
using Yabt.Core.Models;

namespace Yabt.Metadata.Tests;

[TestClass]
public sealed class JsonFolderPolicyReaderTests
{
    [TestMethod]
    public async Task ReadPolicyAsyncWarnsForUnknownPropertiesWithoutDisclosingValues()
    {
        var warnings = new List<string>();
        var policy = await ReadPolicyAsync
        (
            """
            {
              "format": "mirror",
              "excludePaterns": ["private-secret-value"],
              "future": { "token": "secret-token-value" }
            }
            """,
            warnings
        );

        Assert.AreEqual("mirror", policy.Format);
        var configurationSource = FolderPolicyDiagnostics.GetConfigurationSource(policy);
        Assert.IsNotNull(configurationSource);
        Assert.HasCount(2, warnings);
        Assert.IsTrue(warnings.All(message =>
            message.Contains(configurationSource, StringComparison.Ordinal)));
        Assert.IsTrue(warnings.Any(message =>
            message.Contains("$[\"excludePaterns\"]", StringComparison.Ordinal)));
        Assert.IsTrue(warnings.Any(message =>
            message.Contains("$[\"future\"]", StringComparison.Ordinal)));
        Assert.IsTrue(warnings.All(message => !message.Contains("secret", StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow("future.option")]
    [DataRow("quo\"ted")]
    [DataRow("line\nbreak")]
    public async Task ReadPolicyAsyncEscapesUnknownPropertyPaths(string propertyName)
    {
        var warnings = new List<string>();
        var json = JsonSerializer.Serialize(new Dictionary<string, bool> { [propertyName] = true });

        _ = await ReadPolicyAsync(json, warnings);

        var warning = warnings.Single();
        StringAssert.Contains(warning, $"$[{JsonSerializer.Serialize(propertyName)}]");
        Assert.IsFalse(warning.Contains('\n', StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ReadPolicyAsyncKeepsUnsupportedSettingsForTheirFormatHandler()
    {
        var warnings = new List<string>();
        var policy = await ReadPolicyAsync
        (
            """
            {
              "FORMAT": "custom",
              "IncludePatterns": ["*.txt"],
              "excludePatterns": ["*.secret"],
              "options": { "future": true }
            }
            """,
            warnings
        );

        Assert.AreEqual("custom", policy.Format);
        CollectionAssert.AreEqual(new[] { "*.txt" }, policy.IncludePatterns?.ToArray());
        CollectionAssert.AreEqual(new[] { "*.secret" }, policy.ExcludePatterns?.ToArray());
        Assert.AreEqual(JsonValueKind.Object, ((JsonElement)policy.Options!).ValueKind);
        Assert.IsEmpty(warnings);

        var serialized = JsonSerializer.Serialize(policy);
        Assert.IsFalse(serialized.Contains("ConfigurationSource", StringComparison.Ordinal));
        Assert.IsFalse(serialized.Contains
        (
            FolderPolicyDiagnostics.GetConfigurationSource(policy)!,
            StringComparison.Ordinal
        ));
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("[]")]
    [DataRow("\"mirror\"")]
    [DataRow("{\"format\":null}")]
    [DataRow("{\"format\":\"\"}")]
    [DataRow("{\"format\":\" \"}")]
    [DataRow("{\"format\":12}")]
    [DataRow("{\"includePatterns\":\"*.txt\"}")]
    [DataRow("{\"includePatterns\":[null]}")]
    [DataRow("{\"includePatterns\":[\"\"]}")]
    [DataRow("{\"excludePatterns\":12}")]
    [DataRow("{\"excludePatterns\":[false]}")]
    [DataRow("{\"options\":[]}")]
    [DataRow("{\"options\":false}")]
    [DataRow("{\"options\":12}")]
    [DataRow("{\"options\":\"fast\"}")]
    [DataRow("{\"format\":\"mirror\",\"format\":\"zip\"}")]
    [DataRow("{\"format\":")]
    public async Task ReadPolicyAsyncRejectsMalformedKnownSettings(string json)
    {
        await Assert.ThrowsExactlyAsync<YabtMetadataException>(() => ReadPolicyAsync(json));
    }

    [TestMethod]
    [DataRow("{\"future\":1,\"future\":2}")]
    [DataRow("{\"future\":{\"nested\":1,\"nested\":2}}")]
    [DataRow("{\"options\":{\"level\":1,\"level\":2}}")]
    [DataRow("{\"options\":{\"nested\":[{\"level\":1,\"level\":2}]}}")]
    [DataRow("{\"format\":\"mirror\",\"FORMAT\":\"zip\"}")]
    [DataRow("{\"includePatterns\":[],\"IncludePatterns\":[\"*.txt\"]}")]
    [DataRow("{\"excludePatterns\":[],\"ExcludePatterns\":[\"*.secret\"]}")]
    [DataRow("{\"options\":{},\"Options\":{\"level\":1}}")]
    [DataRow("{\"directoryLinks\":{},\"DirectoryLinks\":{}}")]
    [DataRow("{\"directoryLinks\":{\"link\":{\"behavior\":\"follow\",\"Behavior\":\"follow\",\"expectedTarget\":\"..\"}}}")]
    [DataRow("{\"directoryLinks\":{\"link\":{\"behavior\":\"follow\",\"expectedTarget\":\"..\",\"ExpectedTarget\":\"../other\"}}}")]
    public async Task ReadPolicyAsyncRejectsDuplicateSettingsEvenWhenIgnored(string json)
    {
        var warnings = new List<string>();

        await Assert.ThrowsExactlyAsync<YabtMetadataException>(() => ReadPolicyAsync(json, warnings));

        Assert.IsEmpty(warnings);
    }

    [TestMethod]
    [DataRow("{\"includePatterns\":[],\"excludePatterns\":[],\"options\":{}}")]
    [DataRow("{\"includePatterns\":null,\"excludePatterns\":null,\"options\":null}")]
    public async Task ReadPolicyAsyncAcceptsEmptyOptionalSettingsWithoutWarnings(string json)
    {
        var warnings = new List<string>();

        var policy = await ReadPolicyAsync(json, warnings);

        Assert.AreEqual("mirror", policy.Format);
        Assert.IsEmpty(warnings);
    }

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

    private static async Task<FolderPolicy> ReadPolicyAsync
    (
        string json,
        List<string>? warnings = null
    )
    {
        var folderPath = Path.Combine(Path.GetTempPath(), $"yabt-link-policy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folderPath);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(folderPath, FolderPolicyFileNames.Primary), json);
            var services = new ServiceCollection();
            if (warnings is null)
            {
                services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            }
            else
            {
                services.AddSingleton(warnings);
                services.AddSingleton(typeof(ILogger<>), typeof(PolicyCaptureLogger<>));
            }
            services.AddYabtMetadata();
            using var provider = services.BuildServiceProvider();
            return await provider.GetRequiredService<IFolderPolicyReader>().ReadPolicyAsync(folderPath);
        }
        finally
        {
            Directory.Delete(folderPath, recursive: true);
        }
    }

    private sealed class PolicyCaptureLogger<T>(List<string> _warnings) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

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
            if (logLevel == LogLevel.Warning) { _warnings.Add(formatter(state, exception)); }
        }
    }
}
