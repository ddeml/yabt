using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Yabt.AzureBlob;

namespace Yabt.Tests.Implementation;

internal static class DeployedAzureService
{
    public static async Task<DeployedServiceStore> CreateAsync(DeployedServiceConfiguration configuration)
    {
        var services = configuration.CreateServices();
        services.AddYabtAzureBlobObjectStore();
        var provider = services.BuildServiceProvider();
        try
        {
            var options = provider.GetRequiredService<IOptionsMonitor<AzureBlobObjectStoreOptions>>().CurrentValue;
            if (string.IsNullOrWhiteSpace(options.ContainerName))
            {
                throw new YabtTestsException("Service acceptance requires an explicit existing container.");
            }
            if (options.ServiceUri is not null) { DeployedServiceConfiguration.ValidateEndpoint(options.ServiceUri); }
            var clientOptions = new BlobClientOptions
            {
                Diagnostics = { IsLoggingEnabled = false, IsLoggingContentEnabled = false },
                Retry = { MaxRetries = 1, NetworkTimeout = TimeSpan.FromSeconds(30) },
            };
            var client = string.IsNullOrWhiteSpace(options.ConnectionString) ?
                new BlobServiceClient
                (
                    options.ServiceUri ?? throw new YabtTestsException("Azure service URI is missing."),
                    provider.GetRequiredService<TokenCredential>(),
                    clientOptions
                ).GetBlobContainerClient(options.ContainerName) :
                new BlobContainerClient(options.ConnectionString, options.ContainerName, clientOptions);
            DeployedServiceConfiguration.ValidateEndpoint(client.Uri);
            using var preflight = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            // Refuse a missing selected container before provider initialization can create it.
            await client.GetPropertiesAsync(cancellationToken: preflight.Token);
            if ((await client.GetBlobClient(configuration.OwnedPrefix).ExistsAsync(preflight.Token)).Value)
            {
                throw new YabtTestsException("The generated test prefix is already occupied.");
            }
            var existingBlobs = client.GetBlobsAsync
            (
                BlobTraits.None,
                BlobStates.None,
                prefix: configuration.OwnedPrefix + "/",
                cancellationToken: preflight.Token
            );
            await foreach (var _ in existingBlobs)
            {
                throw new YabtTestsException("The generated test prefix already has descendants.");
            }
            return new(provider, cancellationToken => CleanupAsync(client, configuration, cancellationToken));
        }
        catch (Exception)
        {
            await provider.DisposeAsync();
            throw;
        }
    }

    private static async Task CleanupAsync
    (
        BlobContainerClient client,
        DeployedServiceConfiguration configuration,
        CancellationToken cancellationToken
    )
    {
        var prefix = configuration.OwnedPrefix + "/";
        if (!prefix.StartsWith(configuration.BasePrefix + "/", StringComparison.Ordinal) ||
            prefix.Length <= configuration.BasePrefix.Length + 1)
        {
            throw new YabtTestsException("Refusing cleanup outside the unique test prefix.");
        }
        var blobs = client.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix, cancellationToken);
        await foreach (var blob in blobs)
        {
            if (!blob.Name.StartsWith(prefix, StringComparison.Ordinal))
            {
                throw new YabtTestsException("The service listed an object outside the owned prefix.");
            }
            await client.GetBlobClient(blob.Name).DeleteAsync
            (
                conditions: new BlobRequestConditions
                {
                    IfMatch = blob.Properties.ETag ?? throw new YabtTestsException
                    (
                        "Fixture cleanup requires the current blob ETag."
                    ),
                },
                cancellationToken: cancellationToken
            );
        }
        var remainingBlobs = client.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix, cancellationToken);
        await foreach (var _ in remainingBlobs)
        {
            throw new YabtTestsException("Remote fixture cleanup left remaining objects.");
        }
    }
}
