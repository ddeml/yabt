using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Yabt.Tests;
using Yabt.WebDav.Implementation;

namespace Yabt.WebDav.Tests;

[TestClass]
public sealed class WebDavReadinessTests
{
    [TestMethod]
    public async Task EnsureReadyAsyncReadsExistingCollectionWithoutCreatingIt()
    {
        await using var server = new TestHttpServer(207, CreateCollectionResponse());
        var store = CreateStore(server.Endpoint);

        await store.EnsureReadyAsync();

        var request = server.Requests.Single();
        Assert.AreEqual("PROPFIND", request.Method);
        Assert.AreEqual("/archive/", request.Target);
        Assert.AreEqual("0", request.Headers["Depth"]);
    }

    [TestMethod]
    [DataRow(404)]
    [DataRow(403)]
    [DataRow(503)]
    public async Task EnsureReadyAsyncRejectsMissingOrUnavailableCollectionWithoutCreatingIt(int statusCode)
    {
        await using var server = new TestHttpServer(statusCode);
        var store = CreateStore(server.Endpoint);

        await Assert.ThrowsExactlyAsync<YabtWebDavException>(() => store.EnsureReadyAsync());

        Assert.AreEqual("PROPFIND", server.Requests.Single().Method);
    }

    [TestMethod]
    [DataRow("HTTP/1.1 404 Not Found", "/archive/", true)]
    [DataRow("HTTP/1.1 403 Forbidden", "/archive/", true)]
    [DataRow("HTTP/1.1 200 OK", "/archive/", false)]
    [DataRow("HTTP/1.1 200 OK", "/other/", true)]
    public async Task EnsureReadyAsyncRequiresSuccessfulPropertiesForTheConfiguredCollection
    (
        string propertyStatus,
        string href,
        bool isCollection
    )
    {
        await using var server = new TestHttpServer
        (
            207,
            CreateCollectionResponse(propertyStatus, href, isCollection)
        );
        var store = CreateStore(server.Endpoint);

        var exception = await Assert.ThrowsExactlyAsync<YabtWebDavException>
        (
            () => store.EnsureReadyAsync()
        );

        StringAssert.Contains(exception.InnerException!.Message, "readable collection properties");
        Assert.AreEqual("PROPFIND", server.Requests.Single().Method);
    }

    [TestMethod]
    public async Task InitializeAsyncExplicitlyCreatesTheConfiguredCollectionPath()
    {
        await using var server = new TestHttpServer(201);
        var store = CreateStore(server.Endpoint, "parent/archive");

        await store.InitializeAsync();

        var requests = server.Requests;
        CollectionAssert.AreEqual(new[] { "MKCOL", "MKCOL" }, requests.Select(request => request.Method).ToArray());
        CollectionAssert.AreEqual(new[] { "/parent/", "/parent/archive/" }, requests.Select(request => request.Target).ToArray());
    }

    private static WebDavObjectStore CreateStore(Uri endpoint, string rootPath = "archive") => new
    (
        new ReadinessOptionsMonitor(new() { Endpoint = endpoint, RootPath = rootPath }),
        NullLogger<WebDavObjectStore>.Instance
    );

    private static string CreateCollectionResponse
    (
        string propertyStatus = "HTTP/1.1 200 OK",
        string href = "/archive/",
        bool isCollection = true
    ) =>
        "<d:multistatus xmlns:d=\"DAV:\"><d:response>" +
        $"<d:href>{href}</d:href><d:propstat><d:prop><d:resourcetype>" +
        (isCollection ? "<d:collection/>" : string.Empty) +
        "</d:resourcetype></d:prop>" +
        $"<d:status>{propertyStatus}</d:status></d:propstat></d:response></d:multistatus>";

    private sealed class ReadinessOptionsMonitor(WebDavObjectStoreOptions options) : IOptionsMonitor<WebDavObjectStoreOptions>
    {
        public WebDavObjectStoreOptions CurrentValue => options;

        public WebDavObjectStoreOptions Get(string? name) => options;

        public IDisposable? OnChange(Action<WebDavObjectStoreOptions, string?> listener) => null;
    }
}
