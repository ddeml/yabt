using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Yabt.Core.Abstractions;
using Yabt.FileSystem.Implementation;

namespace Yabt.FileSystem.Tests;

[TestClass]
public sealed class FileSystemOperationIdentityTests
{
    [TestMethod]
    public void IdentityUsesExactEffectiveRootWithoutCreatingIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "yabt-identity-" + Guid.NewGuid().ToString("N"));
        var store = CreateStore(root);
        var identity = ((IObjectStoreOperationIdentity)store).OperationIdentity;

        Assert.AreEqual(identity, CreateStore(root + Path.DirectorySeparatorChar).OperationIdentity);
        Assert.AreNotEqual(identity, CreateStore(root + "-other").OperationIdentity);
        Assert.AreNotEqual(identity, CreateStore(Path.Combine(root, "Child")).OperationIdentity);
        Assert.AreNotEqual
        (
            CreateStore(Path.Combine(root, "Child")).OperationIdentity,
            CreateStore(Path.Combine(root, "child")).OperationIdentity
        );
        Assert.IsFalse(identity.Contains(root, StringComparison.Ordinal));
        Assert.IsFalse(Directory.Exists(root));
    }

    [TestMethod]
    public void IdentityObservesReloadedRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "yabt-identity-" + Guid.NewGuid().ToString("N"));
        var options = new MutableOptionsMonitor(new() { RootPath = root });
        var store = new FileSystemObjectStore(options, NullLogger<FileSystemObjectStore>.Instance);
        var original = store.OperationIdentity;

        options.CurrentValue = new() { RootPath = root + "-changed" };

        Assert.AreNotEqual(original, store.OperationIdentity);
    }

    private static FileSystemObjectStore CreateStore(string root) => new
    (
        new MutableOptionsMonitor(new() { RootPath = root }),
        NullLogger<FileSystemObjectStore>.Instance
    );

    private sealed class MutableOptionsMonitor(FileSystemObjectStoreOptions value)
        : IOptionsMonitor<FileSystemObjectStoreOptions>
    {
        public FileSystemObjectStoreOptions CurrentValue { get; set; } = value;

        public FileSystemObjectStoreOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<FileSystemObjectStoreOptions, string?> listener) => null;
    }
}
