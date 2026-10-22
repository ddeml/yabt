namespace Yabt.Metadata;

public sealed class YabtMetadataOptions
{
    public const string DefaultConfigurationSectionPath = "Metadata";
    public const int DefaultSmallDocumentMaximumBytes = 1024 * 1024;

    public long? RootDescriptorMaximumBytes { get; init; }

    public long? FolderPolicyMaximumBytes { get; init; }

    internal long GetValidatedRootDescriptorMaximumBytes() =>
        GetValidatedMaximumBytes(RootDescriptorMaximumBytes, nameof(RootDescriptorMaximumBytes));

    internal long GetValidatedFolderPolicyMaximumBytes() =>
        GetValidatedMaximumBytes(FolderPolicyMaximumBytes, nameof(FolderPolicyMaximumBytes));

    private static long GetValidatedMaximumBytes(long? configuredValue, string name)
    {
        var value = configuredValue ?? DefaultSmallDocumentMaximumBytes;
        if (value <= 0 || value > Array.MaxLength)
        {
            throw new YabtMetadataException
            (
                $"Metadata setting '{name}' must be between 1 and {Array.MaxLength} bytes."
            );
        }

        return value;
    }
}
