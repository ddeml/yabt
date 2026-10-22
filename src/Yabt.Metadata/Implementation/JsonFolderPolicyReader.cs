using System.Collections.Frozen;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yabt.Common;
using Yabt.Core;
using Yabt.Core.Models;

namespace Yabt.Metadata.Implementation;

internal sealed class JsonFolderPolicyReader
(
    ILogger<JsonFolderPolicyReader> _logger,
    IOptionsMonitor<YabtMetadataOptions> _options
) : IFolderPolicyReader
{
    private static readonly FrozenSet<string> KnownProperties = new[]
    {
        "format", "includePatterns", "excludePatterns", "options", "directoryLinks",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private readonly JsonSerializerOptions _jsonOptions = new(JsonMetadataOptions.Create())
    {
        AllowDuplicateProperties = false,
    };

    public async Task<FolderPolicy> ReadPolicyAsync
    (
        string folderPath,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(ReadPolicyAsync));

        var policyPath = Path.Combine(folderPath, FolderPolicyFileNames.Primary);
        _logger.LogFolderPolicyCheck(policyPath);
        if (!File.Exists(policyPath))
        {
            _logger.LogFolderPolicyDefault(policyPath);
            return FolderPolicy.Default;
        }

        _logger.LogFolderPolicyRead(policyPath);
        await using var stream = File.OpenRead(policyPath);
        FolderPolicy policy;

        try
        {
            var content = await SmallDocument.ReadAllBytesAsync
            (
                stream,
                _options.CurrentValue.GetValidatedFolderPolicyMaximumBytes(),
                policyPath,
                cancellationToken
            );
            using var boundedStream = new MemoryStream(content, writable: false);
            using var document = await JsonDocument.ParseAsync
            (
                boundedStream,
                new JsonDocumentOptions { AllowDuplicateProperties = false },
                cancellationToken: cancellationToken
            );
            policy = document.RootElement.Deserialize<FolderPolicy>(_jsonOptions) ??
                throw new YabtMetadataException("A folder policy must be a JSON object.");
            policy = policy with
            {
                DirectoryLinks = FolderPolicyDirectoryLinks.Validate(policy.DirectoryLinks),
            };
            FolderPolicyDiagnostics.Validate(policy);
            FolderPolicyDiagnostics.SetConfigurationSource(policy, policyPath);

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (KnownProperties.Contains(property.Name)) { continue; }

                ConfigurationWarnings.Report
                (
                    _logger,
                    policyPath,
                    $"$[{JsonSerializer.Serialize(property.Name)}]",
                    "This property is unknown and will be ignored."
                );
            }
        }
        catch (Exception ex)
        {
            throw new YabtMetadataException($"Folder policy JSON '{policyPath}' could not be deserialized.", ex);
        }

        return policy;
    }
}
