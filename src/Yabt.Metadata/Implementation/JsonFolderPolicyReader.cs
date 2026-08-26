using System.Text.Json;
using Microsoft.Extensions.Logging;
using Yabt.Core.Models;

namespace Yabt.Metadata.Implementation;

internal sealed class JsonFolderPolicyReader
(
    ILogger<JsonFolderPolicyReader> _logger
) : IFolderPolicyReader
{
    private readonly JsonSerializerOptions _jsonOptions = JsonMetadataOptions.Create();

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
        FolderPolicy? policy;

        try
        {
            policy = await JsonSerializer.DeserializeAsync<FolderPolicy>(
                stream,
                _jsonOptions,
                cancellationToken);
        }
        catch (Exception ex)
        {
            throw new YabtMetadataException($"Folder policy JSON '{policyPath}' could not be deserialized.", ex);
        }

        return policy ?? FolderPolicy.Default;
    }
}
