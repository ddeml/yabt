using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yabt.Core.Models;

namespace Yabt.Tests.Implementation;

internal sealed record DeployedServiceConfiguration
(
    IConfiguration Configuration,
    string BasePrefix,
    string OwnedPrefix
)
{
    private const int MaximumSettingsBytes = 1024 * 1024;

    public static async Task<DeployedServiceConfiguration> LoadAsync(string providerKind, string caseId)
    {
        var settingsPath = Environment.GetEnvironmentVariable("YABT_SERVICE_ACCEPTANCE_SETTINGS")!;
        if (!Path.IsPathFullyQualified(settingsPath))
        {
            throw new YabtTestsException("The private settings path must be absolute.");
        }
        await using var stream = File.OpenRead(settingsPath);
        var bytes = new byte[MaximumSettingsBytes + 1];
        var length = await stream.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false);
        if (length > MaximumSettingsBytes)
        {
            throw new YabtTestsException("The private service settings exceed the one MiB limit.");
        }
        var content = bytes.AsMemory(0, length);
        if (content.Span.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) { content = content[3..]; }
        using var document = JsonDocument.Parse(content);
        var sectionName = providerKind == "azureBlob" ? "AzureBlob" : "WebDav";
        var section = document.RootElement.GetProperty("ObjectStores").GetProperty(sectionName);
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        Flatten(section, $"ObjectStores:{sectionName}", values);
        var prefixKey = $"ObjectStores:{sectionName}:" + (providerKind == "azureBlob" ? "Prefix" : "RootPath");
        if (!values.TryGetValue(prefixKey, out var basePrefix) || string.IsNullOrWhiteSpace(basePrefix))
        {
            throw new YabtTestsException("Service acceptance requires a nonempty dedicated test prefix.");
        }
        var basePath = ObjectPath.Parse(basePrefix);
        var ownedPrefix = basePath.Combine(ObjectPath.Parse(caseId)).Value;
        values[prefixKey] = ownedPrefix;
        if (providerKind == "azureBlob")
        {
            // Bound failures without changing the operations being accepted.
            values["ObjectStores:AzureBlob:Retry:MaximumRetries"] = "1";
            values["ObjectStores:AzureBlob:Retry:NetworkTimeout"] = "00:00:30";
        }
        return new(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), basePrefix, ownedPrefix);
    }

    public ServiceCollection CreateServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Configuration);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        return services;
    }

    public static void ValidateEndpoint(Uri endpoint)
    {
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttps ||
            endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
        {
            throw new YabtTestsException("Deployed-service endpoints must be clean absolute HTTPS URIs.");
        }
    }

    private static void Flatten(JsonElement element, string path, Dictionary<string, string?> values)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                Flatten(property.Value, path + ":" + property.Name, values);
            }
            return;
        }
        values.Add(path, element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Null => null,
            _ => element.GetRawText(),
        });
    }
}
