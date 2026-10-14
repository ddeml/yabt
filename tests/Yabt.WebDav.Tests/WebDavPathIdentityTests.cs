using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Yabt.WebDav.Implementation;

namespace Yabt.WebDav.Tests;

[TestClass]
public sealed class WebDavPathIdentityTests
{
    [TestMethod]
    [DataRow(" directory / file.txt ")]
    [DataRow(" ")]
    [DataRow("\u00a0/name\u00a0")]
    [DataRow("folder/line\nbreak")]
    public void RequestsRetainExactWhitespaceNames(string key)
    {
        var store = new WebDavObjectStore
        (
            new FixedOptionsMonitor(new() { Endpoint = new("https://example.invalid/dav/"), RootPath = " store " }),
            NullLogger<WebDavObjectStore>.Instance
        );

        var uri = store.GetObjectUri(key);

        Assert.AreEqual("/dav/ store /" + key, Uri.UnescapeDataString(uri.AbsolutePath));
    }

    [TestMethod]
    public void ListingsRetainDistinctWhitespaceNames()
    {
        var context = new WebDavPathContext(new("https://example.invalid/dav/"), ["dav"], [" store "]);
        var document = CreateListing
        (
            ["/dav/%20store%20/name", "/dav/%20store%20/%20name", "/dav/%20store%20/name%20", "/dav/%20store%20/%20", "/dav/%20store%20/name\u00a0"]
        );

        var items = WebDavObjectStore.ToArchiveFolderItems(context, document, string.Empty);

        CollectionAssert.AreEqual(new[] { "name", " name", "name ", " ", "name\u00a0" }, items.Select(item => item.Key).ToArray());
    }

    [TestMethod]
    [DataRow("/dav/folder%2Fhidden")]
    [DataRow("/dav/folder%5Chidden")]
    [DataRow("/dav/folder//hidden")]
    [DataRow("/dav/folder/%00name")]
    [DataRow("https://example.invalid//")]
    [DataRow("/dav/a\\report.txt")]
    [DataRow("/dav/report.txt ")]
    [DataRow(" /dav/report.txt")]
    [DataRow("/dav/a/../report.txt")]
    [DataRow("/dav/a/./report.txt")]
    [DataRow("/dav/a/%2e%2e/report.txt")]
    [DataRow("a/%2e/report.txt")]
    [DataRow("https://example.invalid/dav/a/%2e%2e/report.txt")]
    [DataRow("https:report.txt")]
    [DataRow("/dav/report.txt?other-resource")]
    [DataRow("/dav/report.txt#other-resource")]
    public void ListingsRefuseUnrepresentablePathsInsteadOfAliasingObjects(string href)
    {
        var context = new WebDavPathContext(new("https://example.invalid/dav/"), ["dav"], []);

        Assert.ThrowsExactly<YabtWebDavException>
        (
            () => WebDavObjectStore.ToArchiveFolderItems(context, CreateListing([href]), string.Empty)
        );
    }

    private static XDocument CreateListing(IEnumerable<string> hrefs)
    {
        XNamespace dav = "DAV:";
        return new
        (
            new XElement
            (
                dav + "multistatus",
                hrefs.Select(href => new XElement
                (
                    dav + "response",
                    new XElement(dav + "href", href),
                    new XElement(dav + "propstat", new XElement(dav + "prop", new XElement(dav + "getcontentlength", 12)))
                ))
            )
        );
    }

    private sealed class FixedOptionsMonitor(WebDavObjectStoreOptions options) : IOptionsMonitor<WebDavObjectStoreOptions>
    {
        public WebDavObjectStoreOptions CurrentValue => options;

        public WebDavObjectStoreOptions Get(string? name) => options;

        public IDisposable? OnChange(Action<WebDavObjectStoreOptions, string?> listener) => null;
    }
}
