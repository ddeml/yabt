using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Yabt.AzureBlob.Implementation;
using Yabt.Tests;

namespace Yabt.AzureBlob.Tests;

/// <summary>
/// Controlled HTTP responses exercise the actual Azure SDK transport and provider code.
/// Only the small subset used by these contracts is modeled; service integration is separate.
/// </summary>
internal sealed class AzureBlobProtocolFixture : IAsyncDisposable
{
    private readonly Dictionary<string, BlobState> _blobs = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Name, string Block), byte[]> _blocks = new();
    private long _version;

    public AzureBlobProtocolFixture()
    {
        Server = new ScriptedHttpServer(Handle);
        var serviceEndpoint = new Uri(Server.Endpoint, "testaccount");
        var options = new AzureBlobObjectStoreOptions
        {
            ConnectionString = "DefaultEndpointsProtocol=http;AccountName=testaccount;" +
                $"AccountKey={Convert.ToBase64String(new byte[32])};BlobEndpoint={serviceEndpoint}",
            ContainerName = "archive",
            Prefix = "scope",
            Retry = new AzureBlobRetryOptions { MaximumRetries = 0 },
        };
        Store = new
        (
            new TestOptionsMonitor<AzureBlobObjectStoreOptions>(options),
            new AzureBlobContainerClientFactory(new TestTokenCredential()),
            NullLogger<AzureBlobObjectStore>.Instance,
            TimeProvider.System
        );
    }

    public ScriptedHttpServer Server { get; }
    public AzureBlobObjectStore Store { get; }
    public Func<HttpTestRequest, HttpTestResponse?>? Intercept { get; set; }

    public void SetContent(string key, string value) =>
        _blobs[$"scope/{key}"] = new(Encoding.UTF8.GetBytes(value), $"\"version-{++_version}\"");

    public string? GetContent(string key) => _blobs.TryGetValue($"scope/{key}", out var blob) ?
        Encoding.UTF8.GetString(blob.Bytes) : null;

    public ValueTask DisposeAsync() => Server.DisposeAsync();

    public static HttpTestResponse Error(int statusCode, string code) => new
    (
        statusCode,
        Encoding.UTF8.GetBytes(new XElement("Error", new XElement("Code", code), new XElement("Message", code)).ToString()),
        new Dictionary<string, string> { ["x-ms-error-code"] = code, ["Content-Type"] = "application/xml" }
    );

    private HttpTestResponse Handle(HttpTestRequest request)
    {
        if (Intercept?.Invoke(request) is { } intercepted) { return intercepted; }
        var uri = new Uri(Server.Endpoint, request.Target);
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(parts => Uri.UnescapeDataString(parts[0]), parts => parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : "");
        if (query.TryGetValue("comp", out var component) && component == "list")
        {
            return List(query);
        }
        if (query.TryGetValue("restype", out var resourceType) && resourceType == "container")
        {
            return new(request.Method == "PUT" ? 201 : 200, Headers: StandardHeaders());
        }

        if (!uri.AbsolutePath.StartsWith("/testaccount/archive/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unexpected Azure fixture request: {request.Method} {request.Target} -> {uri}");
        }
        var name = Uri.UnescapeDataString(uri.AbsolutePath)["/testaccount/archive/".Length..];
        if (request.Method == "PUT" && component == "block")
        {
            _blocks[(name, query["blockid"])] = request.Body;
            return new(201, Headers: StandardHeaders());
        }
        _blobs.TryGetValue(name, out var existing);
        if (request.Headers.TryGetValue("If-Match", out var ifMatch) && ifMatch != existing?.ETag ||
            request.Headers.TryGetValue("If-None-Match", out var ifNoneMatch) && ifNoneMatch == "*" && existing is not null)
        {
            return Error(412, "ConditionNotMet");
        }

        if (request.Method is "GET" or "HEAD")
        {
            return existing is null ? Error(404, "BlobNotFound") :
                new(200, existing.Bytes, BlobHeaders(existing));
        }
        if (request.Method == "DELETE")
        {
            if (existing is null) { return Error(404, "BlobNotFound"); }
            _blobs.Remove(name);
            return new(202);
        }
        if (request.Method == "PUT")
        {
            var bytes = request.Body;
            if (component == "blocklist")
            {
                using var blockDocument = new MemoryStream(request.Body);
                var blocks = XElement.Load(blockDocument);
                bytes = blocks.Elements().SelectMany(block => _blocks[(name, block.Value)]).ToArray();
            }
            if (request.Headers.TryGetValue("x-ms-copy-source", out var copySource))
            {
                var sourceName = Uri.UnescapeDataString(new Uri(copySource).AbsolutePath)["/testaccount/archive/".Length..];
                if (!_blobs.TryGetValue(sourceName, out var source)) { return Error(404, "BlobNotFound"); }
                if (request.Headers.TryGetValue("x-ms-source-if-match", out var sourceMatch) && sourceMatch != source.ETag)
                {
                    return Error(412, "ConditionNotMet");
                }
                bytes = source.Bytes;
            }

            var blob = new BlobState(bytes, $"\"version-{++_version}\"");
            _blobs[name] = blob;
            var headers = BlobHeaders(blob);
            headers["Content-Length"] = "0";
            headers["x-ms-copy-id"] = Guid.NewGuid().ToString();
            headers["x-ms-copy-status"] = "success";
            return new(copySource is null ? 201 : 202, Headers: headers);
        }

        return Error(400, "UnsupportedFixtureRequest");
    }

    private HttpTestResponse List(IReadOnlyDictionary<string, string> query)
    {
        var prefix = query.GetValueOrDefault("prefix") ?? "";
        var delimiter = query.GetValueOrDefault("delimiter");
        var nodes = new SortedDictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var (name, blob) in _blobs)
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) { continue; }
            var remainder = name[prefix.Length..];
            if (delimiter is not null && remainder.IndexOf(delimiter, StringComparison.Ordinal) is var separator && separator >= 0)
            {
                var folder = name[..(prefix.Length + separator + delimiter.Length)];
                nodes[folder] = new XElement("BlobPrefix", new XElement("Name", folder));
                continue;
            }

            nodes[name] = new XElement
            (
                "Blob",
                new XElement("Name", name),
                new XElement
                (
                    "Properties",
                    new XElement("Last-Modified", "Mon, 01 Jan 2024 00:00:00 GMT"),
                    new XElement("Etag", blob.ETag),
                    new XElement("Content-Length", blob.Bytes.Length),
                    new XElement("Content-Type", "application/octet-stream"),
                    new XElement("BlobType", "BlockBlob")
                )
            );
        }

        // Deliberately tiny pages exercise continuation through the real SDK's pageable API.
        var offset = int.TryParse(query.GetValueOrDefault("marker"), out var parsed) ? parsed : 0;
        var next = offset + 2 < nodes.Count ? (offset + 2).ToString(CultureInfo.InvariantCulture) : "";
        var document = new XElement
        (
            "EnumerationResults",
            new XAttribute("ServiceEndpoint", Server.Endpoint),
            new XAttribute("ContainerName", "archive"),
            new XElement("Prefix", prefix),
            new XElement("Delimiter", delimiter),
            new XElement("Marker", offset == 0 ? "" : offset.ToString(CultureInfo.InvariantCulture)),
            new XElement("MaxResults", 2),
            new XElement("Blobs", nodes.Values.Skip(offset).Take(2)),
            new XElement("NextMarker", next)
        );
        return new
        (
            200,
            Encoding.UTF8.GetBytes(document.ToString()),
            new Dictionary<string, string> { ["Content-Type"] = "application/xml" }
        );
    }

    private static Dictionary<string, string> StandardHeaders() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["ETag"] = "\"container\"",
        ["Last-Modified"] = "Mon, 01 Jan 2024 00:00:00 GMT",
        ["x-ms-request-id"] = "fixture",
        ["x-ms-version"] = "2025-11-05",
    };

    private static Dictionary<string, string> BlobHeaders(BlobState blob)
    {
        var headers = StandardHeaders();
        headers["ETag"] = blob.ETag;
        headers["Content-Length"] = blob.Bytes.Length.ToString(CultureInfo.InvariantCulture);
        headers["x-ms-blob-type"] = "BlockBlob";
        return headers;
    }

    private sealed record BlobState(byte[] Bytes, string ETag);
}
