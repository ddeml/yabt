using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;

namespace Yabt.WebDav.Implementation;

internal sealed class WebDavObjectStore
(
    IOptionsMonitor<WebDavObjectStoreOptions> _options,
    ILogger<WebDavObjectStore> _logger
) : IObjectStore
{
    private const string DepthHeaderName = "Depth";
    private const string DestinationHeaderName = "Destination";
    private const string OverwriteHeaderName = "Overwrite";

    private static readonly HttpClient HttpClient = new();

    private static readonly HttpMethod MkColMethod = new("MKCOL");
    private static readonly HttpMethod MoveMethod = new("MOVE");
    private static readonly HttpMethod PropFindMethod = new("PROPFIND");
    private static readonly XNamespace DavNamespace = "DAV:";

    public async Task EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogTrace(nameof(EnsureReadyAsync));

        try
        {
            var pathContext = GetPathContext();

            await EnsureConfiguredRootExistsAsync(pathContext, cancellationToken);
            await EnsureCollectionPathExistsAsync(
                pathContext,
                [ArchiveLayout.Default.LivePrefix],
                cancellationToken);
            await EnsureCollectionPathExistsAsync(
                pathContext,
                [ArchiveLayout.Default.HistPrefix],
                cancellationToken);
        }
        catch (Exception ex)
        {
            throw new YabtWebDavException(
                "WebDAV object store could not ensure the configured collections exist.",
                ex);
        }
    }

    public async Task UploadAsync
    (
        ArchiveObjectKey key,
        Stream content,
        string contentType,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(UploadAsync));

        _ = metadata;

        try
        {
            var pathContext = GetPathContext();
            var objectSegments = NormalizePathSegments(key.ToObjectPath());
            var objectUri = BuildUri(pathContext, objectSegments, trailingSlash: false);

            await EnsureParentCollectionExistsAsync(pathContext, objectSegments, cancellationToken);

            using var request = CreateRequest(HttpMethod.Put, objectUri);
            request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
            request.Content = new StreamContent(new WebDavNonDisposingStream(content));

            if (!string.IsNullOrWhiteSpace(contentType))
            {
                if (MediaTypeHeaderValue.TryParse(contentType, out var mediaType))
                {
                    request.Content.Headers.ContentType = mediaType;
                }
                else
                {
                    request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
                }
            }

            using var response = await HttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            await EnsureSuccessAsync(
                response,
                $"PUT {objectUri.AbsoluteUri}",
                cancellationToken);
        }
        catch (Exception ex)
        {
            throw new YabtWebDavException(
                $"Upload failed for WebDAV object '{key.ToObjectPath()}'.",
                ex);
        }
    }

    public async Task<ArchiveObjectContent> OpenReadAsync
    (
        ArchiveObjectKey key,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(OpenReadAsync));

        HttpResponseMessage? response = null;

        try
        {
            var objectUri = GetObjectUri(key);

            using var request = CreateRequest(HttpMethod.Get, objectUri);
            response = await HttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                using (response)
                {
                    await EnsureSuccessAsync(
                        response,
                        $"GET {objectUri.AbsoluteUri}",
                        cancellationToken);
                }
            }

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

            return new(
                new WebDavResponseContentStream(stream, response),
                response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream");
        }
        catch (Exception ex)
        {
            response?.Dispose();
            throw new YabtWebDavException(
                $"Open read failed for WebDAV object '{key.ToObjectPath()}'.",
                ex);
        }
    }

    public async Task<bool> ExistsAsync
    (
        ArchiveObjectKey key,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(ExistsAsync));

        try
        {
            var objectUri = GetObjectUri(key);

            using var request = CreateRequest(HttpMethod.Head, objectUri);
            using var response = await HttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return false;
            }

            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            if (response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)
            {
                return await PropFindExistsAsync(objectUri, cancellationToken);
            }

            await EnsureSuccessAsync(
                response,
                $"HEAD {objectUri.AbsoluteUri}",
                cancellationToken);

            return true;
        }
        catch (Exception ex)
        {
            throw new YabtWebDavException(
                $"WebDAV object existence check failed for '{key.ToObjectPath()}'.",
                ex);
        }
    }

    public async IAsyncEnumerable<ArchiveObjectInfo> ListAsync
    (
        ArchiveArea area,
        string? prefix,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(ListAsync));

        var objects = await ListObjectsAsync(area, prefix, cancellationToken);
        foreach (var archiveObject in objects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return archiveObject;
        }
    }

    public async Task MoveAsync
    (
        ArchiveObjectKey source,
        ArchiveObjectKey destination,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(MoveAsync));

        try
        {
            var pathContext = GetPathContext();
            var sourceSegments = NormalizePathSegments(source.ToObjectPath());
            var destinationSegments = NormalizePathSegments(destination.ToObjectPath());
            var sourceUri = BuildUri(pathContext, sourceSegments, trailingSlash: false);
            var destinationUri = BuildUri(pathContext, destinationSegments, trailingSlash: false);

            await EnsureParentCollectionExistsAsync(pathContext, destinationSegments, cancellationToken);

            using var request = CreateRequest(MoveMethod, sourceUri);
            request.Headers.TryAddWithoutValidation(DestinationHeaderName, destinationUri.AbsoluteUri);
            request.Headers.TryAddWithoutValidation(OverwriteHeaderName, "F");

            using var response = await HttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            await EnsureSuccessAsync(
                response,
                $"MOVE {sourceUri.AbsoluteUri} to {destinationUri.AbsoluteUri}",
                cancellationToken);
        }
        catch (Exception ex)
        {
            throw new YabtWebDavException(
                $"Move failed for WebDAV object '{source.ToObjectPath()}' to '{destination.ToObjectPath()}'.",
                ex);
        }
    }

    private async Task<IReadOnlyList<ArchiveObjectInfo>> ListObjectsAsync
    (
        ArchiveArea area,
        string? prefix,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var pathContext = GetPathContext();
            var areaRoot = GetAreaRoot(area);
            var objectSegments = string.IsNullOrWhiteSpace(prefix) ?
                [areaRoot] :
                NormalizePathSegments(new ArchiveObjectKey(area, prefix).ToObjectPath());
            var collectionUri = BuildUri(pathContext, objectSegments, trailingSlash: true);

            using var request = CreateRequest(PropFindMethod, collectionUri);
            request.Headers.TryAddWithoutValidation(DepthHeaderName, "infinity");

            using var response = await HttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return [];
            }

            await EnsureMultiStatusAsync(
                response,
                $"PROPFIND {collectionUri.AbsoluteUri}",
                cancellationToken);

            await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
            var document = await LoadXmlDocumentAsync(content, cancellationToken);

            return ToArchiveObjects(area, pathContext, collectionUri, document);
        }
        catch (Exception ex)
        {
            throw new YabtWebDavException(
                $"WebDAV object listing failed for archive area '{area}' with prefix '{prefix}'.",
                ex);
        }
    }

    private async Task<bool> PropFindExistsAsync(Uri objectUri, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(PropFindMethod, objectUri);
        request.Headers.TryAddWithoutValidation(DepthHeaderName, "0");

        using var response = await HttpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        await EnsureMultiStatusAsync(
            response,
            $"PROPFIND {objectUri.AbsoluteUri}",
            cancellationToken);

        return true;
    }

    private async Task EnsureConfiguredRootExistsAsync
    (
        WebDavPathContext pathContext,
        CancellationToken cancellationToken
    )
    {
        if (pathContext.RootSegments.Count == 0)
        {
            await EnsureCollectionExistsAsync(
                BuildUri(pathContext, [], trailingSlash: true),
                cancellationToken);

            return;
        }

        for (var index = 1; index <= pathContext.RootSegments.Count; index++)
        {
            await EnsureCollectionExistsAsync(
                BuildUriFromEndpoint(
                    pathContext,
                    pathContext.RootSegments.Take(index),
                    trailingSlash: true),
                cancellationToken);
        }
    }

    private async Task EnsureParentCollectionExistsAsync
    (
        WebDavPathContext pathContext,
        string[] objectSegments,
        CancellationToken cancellationToken
    )
    {
        await EnsureConfiguredRootExistsAsync(pathContext, cancellationToken);

        if (objectSegments.Length < 2)
        {
            return;
        }

        await EnsureCollectionPathExistsAsync(
            pathContext,
            objectSegments.Take(objectSegments.Length - 1),
            cancellationToken);
    }

    private async Task EnsureCollectionPathExistsAsync
    (
        WebDavPathContext pathContext,
        IEnumerable<string> objectSegments,
        CancellationToken cancellationToken
    )
    {
        var segments = objectSegments.ToArray();
        for (var index = 1; index <= segments.Length; index++)
        {
            await EnsureCollectionExistsAsync(
                BuildUri(pathContext, segments.Take(index), trailingSlash: true),
                cancellationToken);
        }
    }

    private async Task EnsureCollectionExistsAsync(Uri collectionUri, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(MkColMethod, collectionUri);
        using var response = await HttpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.MethodNotAllowed)
        {
            return;
        }

        await EnsureSuccessAsync(
            response,
            $"MKCOL {collectionUri.AbsoluteUri}",
            cancellationToken);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, Uri requestUri)
    {
        var request = new HttpRequestMessage(method, requestUri);
        var options = _options.CurrentValue;

        if (string.IsNullOrWhiteSpace(options.UserName) && string.IsNullOrWhiteSpace(options.Password))
        {
            return request;
        }

        if (string.IsNullOrWhiteSpace(options.UserName) || string.IsNullOrWhiteSpace(options.Password))
        {
            throw new YabtWebDavException(
                "WebDAV basic authentication requires both user name and password.");
        }

        var token = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{options.UserName}:{options.Password}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);

        return request;
    }

    private Uri GetObjectUri(ArchiveObjectKey key)
    {
        var pathContext = GetPathContext();
        return BuildUri(
            pathContext,
            NormalizePathSegments(key.ToObjectPath()),
            trailingSlash: false);
    }

    private WebDavPathContext GetPathContext()
    {
        var options = _options.CurrentValue;
        var endpoint = options.Endpoint
            ?? throw new YabtWebDavException("WebDAV object store requires an endpoint.");

        if (!endpoint.IsAbsoluteUri)
        {
            throw new YabtWebDavException("WebDAV object store endpoint must be an absolute URI.");
        }

        return new(
            endpoint,
            NormalizeUriPathSegments(endpoint.AbsolutePath),
            NormalizePathSegments(options.RootPath));
    }

    private static Uri BuildUri
    (
        WebDavPathContext pathContext,
        IEnumerable<string> objectSegments,
        bool trailingSlash
    )
    {
        var segments = pathContext.RootSegments.Concat(objectSegments);

        return BuildUriFromEndpoint(pathContext, segments, trailingSlash);
    }

    private static Uri BuildUriFromEndpoint
    (
        WebDavPathContext pathContext,
        IEnumerable<string> segments,
        bool trailingSlash
    )
    {
        var uriSegments = pathContext.EndpointSegments.Concat(segments);
        var path = "/" + string.Join('/', uriSegments.Select(Uri.EscapeDataString));

        if (trailingSlash && !path.EndsWith('/'))
        {
            path += "/";
        }

        var builder = new UriBuilder(pathContext.Endpoint)
        {
            Path = path,
            Query = string.Empty,
            Fragment = string.Empty,
        };

        return builder.Uri;
    }

    private static List<string> NormalizeUriPathSegments(string value)
    {
        var segments = SplitPathSegments(value);
        return segments
            .Select(Uri.UnescapeDataString)
            .ToList();
    }

    private static string[] NormalizePathSegments(string? value)
    {
        var segments = SplitPathSegments(value);
        foreach (var segment in segments)
        {
            if (segment is "." or "..")
            {
                throw new YabtWebDavException("WebDAV object path contains an invalid segment.");
            }
        }

        return segments;
    }

    private static string[] SplitPathSegments(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value
            .Replace('\\', '/')
            .Trim('/')
            .Split(
                '/',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static List<ArchiveObjectInfo> ToArchiveObjects
    (
        ArchiveArea area,
        WebDavPathContext pathContext,
        Uri collectionUri,
        XDocument document
    )
    {
        var result = new List<ArchiveObjectInfo>();
        var areaSegments = pathContext.EndpointSegments
            .Concat(pathContext.RootSegments)
            .Append(GetAreaRoot(area))
            .ToArray();

        foreach (var response in document.Descendants(DavNamespace + "response"))
        {
            if (IsCollection(response))
            {
                continue;
            }

            var href = response.Element(DavNamespace + "href")?.Value;
            if (string.IsNullOrWhiteSpace(href))
            {
                continue;
            }

            var hrefSegments = GetHrefPathSegments(collectionUri, href);
            if (!StartsWithSegments(hrefSegments, areaSegments) ||
                hrefSegments.Count == areaSegments.Length)
            {
                continue;
            }

            var relativePath = string.Join('/', hrefSegments.Skip(areaSegments.Length));
            result.Add(new(
                new(area, relativePath),
                GetContentLength(response),
                GetLastModifiedUtc(response)));
        }

        return result;
    }

    private static List<string> GetHrefPathSegments(Uri baseUri, string href)
    {
        var hrefUri = Uri.TryCreate(href, UriKind.Absolute, out var absoluteUri) ?
            absoluteUri :
            new Uri(baseUri, href);

        return NormalizeUriPathSegments(hrefUri.AbsolutePath);
    }

    private static bool StartsWithSegments
    (
        List<string> value,
        string[] prefix
    )
    {
        if (value.Count < prefix.Length)
        {
            return false;
        }

        for (var index = 0; index < prefix.Length; index++)
        {
            if (!string.Equals(value[index], prefix[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static long? GetContentLength(XElement response)
    {
        var value = GetSuccessfulPropValue(response, "getcontentlength");
        return long.TryParse(
            value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var contentLength) ?
            contentLength :
            null;
    }

    private static DateTimeOffset? GetLastModifiedUtc(XElement response)
    {
        var value = GetSuccessfulPropValue(response, "getlastmodified");
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var lastModified) ?
            lastModified.ToUniversalTime() :
            null;
    }

    private static bool IsCollection(XElement response)
    {
        return GetSuccessfulProps(response)
            .Elements(DavNamespace + "resourcetype")
            .Elements(DavNamespace + "collection")
            .Any();
    }

    private static string? GetSuccessfulPropValue(XElement response, string propName)
    {
        return GetSuccessfulProps(response)
            .Elements(DavNamespace + propName)
            .FirstOrDefault()
            ?.Value;
    }

    private static IEnumerable<XElement> GetSuccessfulProps(XElement response)
    {
        foreach (var propStat in response.Elements(DavNamespace + "propstat"))
        {
            var status = propStat.Element(DavNamespace + "status")?.Value;
            if (!IsSuccessStatus(status))
            {
                continue;
            }

            var prop = propStat.Element(DavNamespace + "prop");
            if (prop is not null)
            {
                yield return prop;
            }
        }
    }

    private static bool IsSuccessStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return false;
        }

        var parts = status.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 &&
            int.TryParse(parts[1], CultureInfo.InvariantCulture, out var statusCode) &&
            statusCode is >= 200 and <= 299;
    }

    private static string GetAreaRoot(ArchiveArea area)
    {
        return area switch
        {
            ArchiveArea.Live => ArchiveLayout.Default.LivePrefix,
            ArchiveArea.Hist => ArchiveLayout.Default.HistPrefix,
            _ => throw new ArgumentOutOfRangeException(nameof(area), area, null),
        };
    }

    private static async Task EnsureMultiStatusAsync
    (
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken
    )
    {
        if (response.StatusCode == HttpStatusCode.MultiStatus)
        {
            return;
        }

        var errorContent = await ReadErrorContentAsync(response, cancellationToken);
        throw new YabtWebDavException(
            $"WebDAV operation '{operation}' returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}); " +
            $"expected 207 Multi-Status.{errorContent}");
    }

    private static async Task EnsureSuccessAsync
    (
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken
    )
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var errorContent = await ReadErrorContentAsync(response, cancellationToken);
        throw new YabtWebDavException(
            $"WebDAV operation '{operation}' returned HTTP {(int)response.StatusCode} " +
            $"({response.ReasonPhrase}).{errorContent}");
    }

    private static async Task<XDocument> LoadXmlDocumentAsync
    (
        Stream content,
        CancellationToken cancellationToken
    )
    {
        var settings = new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
        };

        using var reader = XmlReader.Create(content, settings);
        return await XDocument.LoadAsync(
            reader,
            LoadOptions.None,
            cancellationToken);
    }

    private static async Task<string> ReadErrorContentAsync
    (
        HttpResponseMessage response,
        CancellationToken cancellationToken
    )
    {
        if (response.Content is null)
        {
            return string.Empty;
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        var trimmed = content.Trim();
        var preview = trimmed.Length <= 2048 ?
            trimmed :
            trimmed[..2048];

        return $" Response body: {preview}";
    }
}

internal sealed record WebDavPathContext
(
    Uri Endpoint,
    IReadOnlyList<string> EndpointSegments,
    IReadOnlyList<string> RootSegments
);

internal sealed class WebDavNonDisposingStream(Stream _inner) : Stream
{
    public override bool CanRead => _inner.CanRead;

    public override bool CanSeek => _inner.CanSeek;

    public override bool CanWrite => _inner.CanWrite;

    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override void Flush()
    {
        _inner.Flush();
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        return _inner.FlushAsync(cancellationToken);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return _inner.Read(buffer, offset, count);
    }

    public override ValueTask<int> ReadAsync
    (
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        return _inner.ReadAsync(buffer, cancellationToken);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        return _inner.Seek(offset, origin);
    }

    public override void SetLength(long value)
    {
        _inner.SetLength(value);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        _inner.Write(buffer, offset, count);
    }

    public override ValueTask WriteAsync
    (
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        return _inner.WriteAsync(buffer, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
    }

    public override ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}

internal sealed class WebDavResponseContentStream(Stream _inner, IDisposable _response) : Stream
{
    public override bool CanRead => _inner.CanRead;

    public override bool CanSeek => _inner.CanSeek;

    public override bool CanWrite => _inner.CanWrite;

    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override void Flush()
    {
        _inner.Flush();
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        return _inner.FlushAsync(cancellationToken);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return _inner.Read(buffer, offset, count);
    }

    public override ValueTask<int> ReadAsync
    (
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        return _inner.ReadAsync(buffer, cancellationToken);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        return _inner.Seek(offset, origin);
    }

    public override void SetLength(long value)
    {
        _inner.SetLength(value);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        _inner.Write(buffer, offset, count);
    }

    public override ValueTask WriteAsync
    (
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        return _inner.WriteAsync(buffer, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
            _response.Dispose();
        }
    }

    public override async ValueTask DisposeAsync()
    {
        try
        {
            await _inner.DisposeAsync();
        }
        finally
        {
            _response.Dispose();
        }
    }
}
