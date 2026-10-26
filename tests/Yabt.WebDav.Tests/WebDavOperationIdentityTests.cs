using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Yabt.Core.Abstractions;
using Yabt.WebDav.Implementation;

namespace Yabt.WebDav.Tests;

[TestClass]
public sealed class WebDavOperationIdentityTests
{
    [TestMethod]
    public void IdentityDistinguishesEffectiveEndpointAndExactRoot()
    {
        var original = Identity("https://example.invalid/dav/", "root");

        Assert.AreEqual(original, Identity("https://example.invalid/dav/root/", null));
        Assert.AreNotEqual(original, Identity("https://other.invalid/dav/", "root"));
        Assert.AreNotEqual(original, Identity("https://example.invalid/other/", "root"));
        Assert.AreNotEqual(original, Identity("https://example.invalid/dav/", "Root"));
        Assert.AreNotEqual(original, Identity("https://example.invalid/dav/", "root "));
        Assert.IsFalse(original.Contains("example", StringComparison.Ordinal));
    }

    [TestMethod]
    public void IdentityExcludesAuthenticationAndIgnoredUriComponents()
    {
        var original = Identity("https://example.invalid/dav/", "root");
        var store = CreateStore(new(new()
        {
            Endpoint = new("https://user:secret@example.invalid/dav/?token=secret#secret"),
            RootPath = "root",
            UserName = "another-user",
            Password = "another-secret",
            CredentialRef = "credential-secret",
        }));

        Assert.AreEqual(original, ((IObjectStoreOperationIdentity)store).OperationIdentity);
        Assert.IsFalse(store.OperationIdentity.Contains("secret", StringComparison.Ordinal));
    }

    [TestMethod]
    public void IdentityObservesReloadedLocation()
    {
        var options = new MutableOptionsMonitor(new() { Endpoint = new("https://example.invalid/dav/") });
        var store = CreateStore(options);
        var original = store.OperationIdentity;

        options.CurrentValue = new() { Endpoint = new("https://other.invalid/dav/") };

        Assert.AreNotEqual(original, store.OperationIdentity);
    }

    private static string Identity(string endpoint, string? root) => CreateStore(new(new()
    {
        Endpoint = new(endpoint),
        RootPath = root,
    })).OperationIdentity;

    private static WebDavObjectStore CreateStore(MutableOptionsMonitor options) => new
    (
        options,
        NullLogger<WebDavObjectStore>.Instance
    );

    private sealed class MutableOptionsMonitor(WebDavObjectStoreOptions value) : IOptionsMonitor<WebDavObjectStoreOptions>
    {
        public WebDavObjectStoreOptions CurrentValue { get; set; } = value;

        public WebDavObjectStoreOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<WebDavObjectStoreOptions, string?> listener) => null;
    }
}
