using System.Text.Json;
using Yabt.Core.Models;

namespace Yabt.Metadata.Implementation;

internal sealed class JsonFolderPolicyReader(JsonSerializerOptions _jsonOptions) : IFolderPolicyReader
{
    public JsonFolderPolicyReader()
        : this(JsonMetadataOptions.Create())
    {
    }

    public async Task<FolderPolicy> ReadPolicyAsync
    (
        string folderPath,
        CancellationToken cancellationToken = default
    )
    {
        var policyPath = Path.Combine(folderPath, FolderPolicyFileNames.Primary);
        if (!File.Exists(policyPath))
        {
            return FolderPolicy.Default;
        }

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
