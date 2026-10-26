using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Yabt.Core.Models;
using Yabt.WebDav;

namespace Yabt.Tests.Implementation;

internal static class DeployedWebDavService
{
    public static async Task<DeployedServiceStore> CreateAsync(DeployedServiceConfiguration configuration)
    {
        var services = configuration.CreateServices();
        services.AddYabtWebDavObjectStore("ObjectStores:WebDav");
        var provider = services.BuildServiceProvider();
        HttpClient? client = null;
        try
        {
            var options = provider.GetRequiredService<IOptionsMonitor<WebDavObjectStoreOptions>>().CurrentValue;
            var endpoint = options.Endpoint ?? throw new YabtTestsException("WebDAV endpoint is missing.");
            DeployedServiceConfiguration.ValidateEndpoint(endpoint);
            if (string.IsNullOrWhiteSpace(options.UserName) || string.IsNullOrWhiteSpace(options.Password))
            {
                throw new YabtTestsException("WebDAV service acceptance requires explicit credentials.");
            }
            var endpointPath = endpoint.AbsolutePath.Trim('/');
            var endpointSegments = endpointPath.Length == 0 ? [] : endpointPath.Split('/').Select(Uri.UnescapeDataString).ToArray();
            foreach (var segment in endpointSegments)
            {
                if (ObjectPath.Parse(segment).Value.Contains('/'))
                {
                    throw new YabtTestsException("WebDAV endpoint has an ambiguous path segment.");
                }
            }
            var segments = endpointSegments.Concat(configuration.OwnedPrefix.Split('/'));
            var ownedUri = new UriBuilder(endpoint)
            {
                Path = "/" + string.Join('/', segments.Select(Uri.EscapeDataString)) + "/",
            }.Uri;
            client = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.Authorization = new
            (
                "Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(options.UserName + ":" + options.Password))
            );
            using var request = new HttpRequestMessage(new("PROPFIND"), ownedUri);
            request.Headers.Add("Depth", "0");
            request.Content = new StringContent
            (
                "<d:propfind xmlns:d=\"DAV:\"><d:prop><d:resourcetype/></d:prop></d:propfind>",
                Encoding.UTF8,
                "application/xml"
            );
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (response.StatusCode != HttpStatusCode.NotFound)
            {
                response.EnsureSuccessStatusCode();
                throw new YabtTestsException("The generated WebDAV test collection is already occupied.");
            }
            var cleanupClient = client;
            return new
            (
                provider,
                cancellationToken => CleanupAsync(cleanupClient, ownedUri, configuration, cancellationToken),
                client
            );
        }
        catch (Exception)
        {
            client?.Dispose();
            await provider.DisposeAsync();
            throw;
        }
    }

    private static async Task CleanupAsync
    (
        HttpClient client,
        Uri ownedUri,
        DeployedServiceConfiguration configuration,
        CancellationToken cancellationToken
    )
    {
        if (!configuration.OwnedPrefix.StartsWith(configuration.BasePrefix + "/", StringComparison.Ordinal) ||
            configuration.OwnedPrefix.Length <= configuration.BasePrefix.Length + 1)
        {
            throw new YabtTestsException("Refusing cleanup outside the unique test collection.");
        }
        using var request = new HttpRequestMessage(HttpMethod.Delete, ownedUri);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) { return; }
        response.EnsureSuccessStatusCode();
        using var verification = new HttpRequestMessage(new("PROPFIND"), ownedUri);
        verification.Headers.Add("Depth", "0");
        using var verificationResponse = await client.SendAsync
        (
            verification,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken
        );
        if (verificationResponse.StatusCode != HttpStatusCode.NotFound)
        {
            verificationResponse.EnsureSuccessStatusCode();
            throw new YabtTestsException("WebDAV fixture cleanup left a readable collection.");
        }
    }
}
