using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Yabt.Core.Abstractions;

namespace Yabt.FileSystem.Tests;

[TestClass]
public sealed class FileSystemReadinessTests
{
    [TestMethod]
    public async Task EnsureReadyAsyncRefusesMissingRootWithoutCreatingDirectories()
    {
        using var fixture = new FileSystemLinkFixture();
        var rootPath = Path.Combine(fixture.Archive, "missing", "source");
        using var services = CreateServices(rootPath);
        var store = services.GetRequiredService<IObjectStore>();

        var exception = await Assert.ThrowsExactlyAsync<YabtFileSystemException>
        (
            () => store.EnsureReadyAsync()
        );

        Assert.AreEqual(rootPath, exception.Path);
        Assert.IsNotNull(exception.InnerException);
        Assert.IsFalse(Directory.Exists(rootPath));
        Assert.IsEmpty(Directory.GetFileSystemEntries(fixture.Archive));
    }

    [TestMethod]
    public async Task EnsureReadyAsyncRefusesFileInPlaceOfRootWithoutChangingIt()
    {
        using var fixture = new FileSystemLinkFixture();
        var rootPath = Path.Combine(fixture.Archive, "source");
        await File.WriteAllTextAsync(rootPath, "leave this file intact");
        using var services = CreateServices(rootPath);
        var store = services.GetRequiredService<IObjectStore>();

        var exception = await Assert.ThrowsExactlyAsync<YabtFileSystemException>
        (
            () => store.EnsureReadyAsync()
        );

        Assert.AreEqual(rootPath, exception.Path);
        Assert.AreEqual("leave this file intact", await File.ReadAllTextAsync(rootPath));
        CollectionAssert.AreEqual(new[] { rootPath }, Directory.GetFileSystemEntries(fixture.Archive));
    }

    [TestMethod]
    public async Task EnsureReadyAsyncAcceptsEmptyRootWithoutCreatingPlumbing()
    {
        using var fixture = new FileSystemLinkFixture();
        using var services = CreateServices(fixture.Archive);
        var store = services.GetRequiredService<IObjectStore>();

        await store.EnsureReadyAsync();

        Assert.IsTrue(Directory.Exists(fixture.Archive));
        Assert.IsEmpty(Directory.GetFileSystemEntries(fixture.Archive));
    }

    [TestMethod]
    public async Task EnsureReadyAsyncLeavesExistingContentsUnchanged()
    {
        using var fixture = new FileSystemLinkFixture();
        var folderPath = Directory.CreateDirectory(Path.Combine(fixture.Archive, "folder")).FullName;
        var filePath = Path.Combine(folderPath, "data.txt");
        await File.WriteAllTextAsync(filePath, "existing source data");
        var lastWriteTime = File.GetLastWriteTimeUtc(filePath);
        using var services = CreateServices(fixture.Archive);
        var store = services.GetRequiredService<IObjectStore>();

        await store.EnsureReadyAsync();

        CollectionAssert.AreEqual(new[] { folderPath }, Directory.GetFileSystemEntries(fixture.Archive));
        CollectionAssert.AreEqual(new[] { filePath }, Directory.GetFileSystemEntries(folderPath));
        Assert.AreEqual("existing source data", await File.ReadAllTextAsync(filePath));
        Assert.AreEqual(lastWriteTime, File.GetLastWriteTimeUtc(filePath));
    }

    [TestMethod]
    public async Task EnsureReadyAsyncRechecksRootWithoutRecreatingItAfterRemoval()
    {
        using var fixture = new FileSystemLinkFixture();
        var rootPath = Directory.CreateDirectory(Path.Combine(fixture.Archive, "source")).FullName;
        using var services = CreateServices(rootPath);
        var store = services.GetRequiredService<IObjectStore>();
        await store.EnsureReadyAsync();
        Directory.Delete(rootPath);

        var exception = await Assert.ThrowsExactlyAsync<YabtFileSystemException>
        (
            () => store.EnsureReadyAsync()
        );

        Assert.AreEqual(rootPath, exception.Path);
        Assert.IsFalse(Directory.Exists(rootPath));
        Assert.IsEmpty(Directory.GetFileSystemEntries(fixture.Archive));
    }

    [TestMethod]
    public async Task EnsureReadyAsyncCancellationDoesNotCreateMissingRoot()
    {
        using var fixture = new FileSystemLinkFixture();
        var rootPath = Path.Combine(fixture.Archive, "source");
        using var services = CreateServices(rootPath);
        var store = services.GetRequiredService<IObjectStore>();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var exception = await Assert.ThrowsExactlyAsync<YabtFileSystemException>
        (
            () => store.EnsureReadyAsync(cancellation.Token)
        );

        Assert.IsTrue(exception.IsCancellationException());
        Assert.IsFalse(Directory.Exists(rootPath));
        Assert.IsEmpty(Directory.GetFileSystemEntries(fixture.Archive));
    }

    [TestMethod]
    public async Task InitializeAsyncExplicitlyCreatesMissingRoot()
    {
        using var fixture = new FileSystemLinkFixture();
        var rootPath = Path.Combine(fixture.Archive, "new", "destination");
        using var services = CreateServices(rootPath);
        var store = services.GetRequiredService<IObjectStore>();

        await store.InitializeAsync();
        await store.EnsureReadyAsync();

        Assert.IsTrue(Directory.Exists(rootPath));
        Assert.IsEmpty(Directory.GetFileSystemEntries(rootPath));
    }

    [TestMethod]
    public async Task EnsureReadyAsyncAcceptsConfiguredLinkedRootWithoutChangingItsTarget()
    {
        using var fixture = new FileSystemLinkFixture();
        var rootPath = Path.Combine(fixture.Archive, "linked-root");
        var filePath = Path.Combine(fixture.Outside, "data.txt");
        await File.WriteAllTextAsync(filePath, "linked source data");
        fixture.LinkDirectory(rootPath, fixture.Outside);
        using var services = CreateServices(rootPath);
        var store = services.GetRequiredService<IObjectStore>();

        await store.EnsureReadyAsync();

        Assert.IsNotNull(new DirectoryInfo(rootPath).LinkTarget);
        Assert.AreEqual("linked source data", await File.ReadAllTextAsync(filePath));
        CollectionAssert.AreEqual(new[] { filePath }, Directory.GetFileSystemEntries(fixture.Outside));
    }

    [TestMethod]
    public async Task EnsureReadyAsyncRefusesRootWithoutDirectoryListingPermission()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows directory ACL test.");
            return;
        }
        using var fixture = new FileSystemLinkFixture();
        var rootPath = Path.Combine(fixture.Archive, "source");
        var directory = Directory.CreateDirectory(rootPath);
        var filePath = Path.Combine(rootPath, "data.txt");
        await File.WriteAllTextAsync(filePath, "inaccessible source data");
        using var services = CreateServices(rootPath);
        var store = services.GetRequiredService<IObjectStore>();
        using var identity = WindowsIdentity.GetCurrent();
        Assert.IsNotNull(identity.User);
        var originalAccess = directory.GetAccessControl(AccessControlSections.Access);
        var deniedAccess = directory.GetAccessControl(AccessControlSections.Access);
        deniedAccess.AddAccessRule
        (
            new FileSystemAccessRule
            (
                identity.User,
                FileSystemRights.ListDirectory,
                InheritanceFlags.None,
                PropagationFlags.None,
                AccessControlType.Deny
            )
        );

        try
        {
            directory.SetAccessControl(deniedAccess);
            Assert.ThrowsExactly<UnauthorizedAccessException>(() => Directory.GetFileSystemEntries(rootPath));

            var exception = await Assert.ThrowsExactlyAsync<YabtFileSystemException>
            (
                () => store.EnsureReadyAsync()
            );

            Assert.AreEqual(rootPath, exception.Path);
            Assert.IsInstanceOfType<UnauthorizedAccessException>(exception.GetBaseException());
        }
        finally
        {
            var restoredAccess = new DirectorySecurity();
            restoredAccess.SetSecurityDescriptorBinaryForm
            (
                originalAccess.GetSecurityDescriptorBinaryForm(),
                AccessControlSections.Access
            );
            directory.SetAccessControl(restoredAccess);
        }

        Assert.AreEqual("inaccessible source data", await File.ReadAllTextAsync(filePath));
        CollectionAssert.AreEqual(new[] { filePath }, Directory.GetFileSystemEntries(rootPath));
    }

    private static ServiceProvider CreateServices(string rootPath)
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddYabtFileSystemObjectStore();
        services.AddSingleton<IOptionsMonitor<FileSystemObjectStoreOptions>>
        (
            new ReadinessOptionsMonitor(new() { RootPath = rootPath })
        );
        return services.BuildServiceProvider();
    }

    private sealed class ReadinessOptionsMonitor(FileSystemObjectStoreOptions _value)
        : IOptionsMonitor<FileSystemObjectStoreOptions>
    {
        public FileSystemObjectStoreOptions CurrentValue => _value;

        public FileSystemObjectStoreOptions Get(string? name) => _value;

        public IDisposable? OnChange(Action<FileSystemObjectStoreOptions, string?> listener) => null;
    }
}
