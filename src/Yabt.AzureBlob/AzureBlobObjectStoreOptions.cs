namespace Yabt.AzureBlob;

public sealed class AzureBlobObjectStoreOptions
{
    public const string DefaultConfigurationSectionPath = "ObjectStores:AzureBlob";

    public string? ConnectionString { get; init; }

    public Uri? ServiceUri { get; init; }

    /// <summary>
    /// Default is <c>archive</c>.
    /// </summary>
    public string? ContainerName { get; init; }

    public string? Prefix { get; init; }

    public string GetEffectiveContainerName() => ContainerName ?? "archive";
}
