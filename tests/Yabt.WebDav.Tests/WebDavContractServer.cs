using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Yabt.Tests;
using Yabt.WebDav.Implementation;

namespace Yabt.WebDav.Tests;

/// <summary>
/// A small stateful DAV endpoint for exercising the provider's real HTTP transport.
/// It implements only the resource operations used by these contracts, not a full DAV server.
/// </summary>
internal sealed class WebDavContractServer : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Resource> _resources = new(StringComparer.Ordinal)
    {
        ["/"] = new(true, [], "\"0\""),
    };
    private readonly ScriptedHttpServer _server;
    private long _version;

    public WebDavContractServer()
    {
        _server = new(HandleRequest);
        Store = new
        (
            new FixedOptionsMonitor(new() { Endpoint = _server.Endpoint, RootPath = "archive" }),
            NullLogger<WebDavObjectStore>.Instance
        );
    }

    public WebDavObjectStore Store { get; }

    public IReadOnlyList<HttpTestRequest> Requests => _server.Requests;

    public Action<HttpTestRequest>? BeforeRequest { get; set; }

    public Func<HttpTestRequest, HttpTestResponse?>? OverrideResponse { get; set; }

    public bool OmitEntityTags { get; set; }

    public bool UseWeakEntityTags { get; set; }

    public void SetObject(string key, byte[] content)
    {
        lock (_gate)
        {
            _resources["/archive/" + key] = new(false, content, NextEntityTag());
        }
    }

    public ValueTask DisposeAsync() => _server.DisposeAsync();

    private HttpTestResponse HandleRequest(HttpTestRequest request)
    {
        lock (_gate)
        {
            BeforeRequest?.Invoke(request);
            if (OverrideResponse?.Invoke(request) is { } response) { return response; }

            var path = DecodePath(request.Target);
            return request.Method switch
            {
                "MKCOL" => CreateCollection(path),
                "PUT" => PutObject(path, request),
                "GET" or "HEAD" => ReadObject(path),
                "PROPFIND" => ListCollection(path, request),
                "MOVE" => MoveResource(path, request),
                "DELETE" => DeleteObject(path, request),
                _ => new(405),
            };
        }
    }

    private HttpTestResponse CreateCollection(string path)
    {
        if (_resources.ContainsKey(path)) { return new(405); }
        if (!HasParentCollection(path)) { return new(409); }

        _resources.Add(path, new(true, [], NextEntityTag()));
        return new(201);
    }

    private HttpTestResponse PutObject(string path, HttpTestRequest request)
    {
        _resources.TryGetValue(path, out var existing);
        if (!MatchesConditions(request, existing)) { return new(412); }
        if (existing?.IsCollection == true) { return new(405); }
        if (!HasParentCollection(path)) { return new(409); }

        _resources[path] = new(false, request.Body, NextEntityTag());
        return new(existing is null ? 201 : 204);
    }

    private HttpTestResponse ReadObject(string path)
    {
        if (!_resources.TryGetValue(path, out var resource)) { return new(404); }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = "application/octet-stream",
        };
        if (!OmitEntityTags)
        {
            headers["ETag"] = UseWeakEntityTags ? "W/" + resource.EntityTag : resource.EntityTag;
        }

        return new(200, resource.Content, headers);
    }

    private HttpTestResponse ListCollection(string path, HttpTestRequest request)
    {
        if (!_resources.TryGetValue(path, out var resource)) { return new(404); }

        XNamespace dav = "DAV:";
        var responses = new List<XElement> { CreateProperties(path, resource) };
        if (request.Headers.GetValueOrDefault("Depth") == "1")
        {
            foreach (var pair in _resources)
            {
                if (pair.Key != path && GetParentPath(pair.Key) == path)
                {
                    responses.Add(CreateProperties(pair.Key, pair.Value));
                }
            }
        }

        var document = new XDocument(new XElement(dav + "multistatus", responses));
        return new
        (
            207,
            Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting)),
            new Dictionary<string, string> { ["Content-Type"] = "application/xml" }
        );
    }

    private HttpTestResponse MoveResource(string path, HttpTestRequest request)
    {
        if (!_resources.TryGetValue(path, out var resource)) { return new(404); }
        if (!request.Headers.TryGetValue("Destination", out var destinationHeader)) { return new(400); }

        var destination = DecodePath(new Uri(destinationHeader).AbsolutePath);
        if (!HasParentCollection(destination)) { return new(409); }
        if (_resources.ContainsKey(destination) && request.Headers.GetValueOrDefault("Overwrite") == "F")
        {
            return new(412);
        }

        // MOVE transfers a collection's complete subtree, including hidden markers and empties.
        var moved = _resources.Where
        (
            pair => pair.Key == path || resource.IsCollection && pair.Key.StartsWith(path + "/", StringComparison.Ordinal)
        ).ToArray();
        foreach (var pair in moved)
        {
            _resources.Remove(pair.Key);
            _resources[destination + pair.Key[path.Length..]] = pair.Value;
        }

        return new(201);
    }

    private HttpTestResponse DeleteObject(string path, HttpTestRequest request)
    {
        if (!_resources.TryGetValue(path, out var resource)) { return new(404); }
        if (!MatchesConditions(request, resource)) { return new(412); }

        _resources.Remove(path);
        return new(204);
    }

    private bool HasParentCollection(string path) =>
        _resources.TryGetValue(GetParentPath(path), out var parent) && parent.IsCollection;

    private string NextEntityTag() => "\"" + (++_version).ToString(CultureInfo.InvariantCulture) + "\"";

    private static bool MatchesConditions(HttpTestRequest request, Resource? existing)
    {
        if (request.Headers.GetValueOrDefault("If-None-Match") == "*" && existing is not null) { return false; }
        return !request.Headers.TryGetValue("If-Match", out var entityTag) || existing?.EntityTag == entityTag;
    }

    private static string DecodePath(string target)
    {
        var path = Uri.UnescapeDataString(target);
        return path == "/" ? path : path.TrimEnd('/');
    }

    private static string GetParentPath(string path)
    {
        var separator = path.LastIndexOf('/');
        return separator <= 0 ? "/" : path[..separator];
    }

    private static XElement CreateProperties(string path, Resource resource)
    {
        XNamespace dav = "DAV:";
        var href = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        if (resource.IsCollection && !href.EndsWith('/')) { href += "/"; }

        return new
        (
            dav + "response",
            new XElement(dav + "href", href),
            new XElement
            (
                dav + "propstat",
                new XElement
                (
                    dav + "prop",
                    new XElement(dav + "resourcetype", resource.IsCollection ? new XElement(dav + "collection") : null),
                    new XElement(dav + "getcontentlength", resource.Content.Length),
                    new XElement(dav + "getlastmodified", "Mon, 01 Jan 2024 00:00:00 GMT"),
                    new XElement(dav + "getetag", resource.EntityTag)
                ),
                new XElement(dav + "status", "HTTP/1.1 200 OK")
            )
        );
    }

    private sealed record Resource(bool IsCollection, byte[] Content, string EntityTag);

    private sealed class FixedOptionsMonitor(WebDavObjectStoreOptions options) : IOptionsMonitor<WebDavObjectStoreOptions>
    {
        public WebDavObjectStoreOptions CurrentValue => options;

        public WebDavObjectStoreOptions Get(string? name) => options;

        public IDisposable? OnChange(Action<WebDavObjectStoreOptions, string?> listener) => null;
    }
}
