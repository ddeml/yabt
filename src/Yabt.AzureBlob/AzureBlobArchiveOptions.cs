namespace Yabt.AzureBlob;

public sealed class AzureBlobArchiveOptions
{
    public string? ConnectionString { get; init; }

    public Uri? ServiceUri { get; init; }

    /// <summary>
    /// Default is <c>archive</c>.
    /// </summary>
    public string? ContainerName { get; init; }

    public string GetEffectiveContainerName() => ContainerName ?? "archive";
}
