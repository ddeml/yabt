using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;

namespace Yabt.FileSystem.Tests;

[TestClass]
public sealed class LinuxFileSystemPermissionTests
{
    [TestMethod]
    public async Task EnsureReadyAsyncRejectsUnreadableRootWithoutCreatingPlumbing()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("Linux filesystem permission test; requires an ordinary Linux user.");
            return;
        }
        RequireNonRootUser();
        using var fixture = new FileSystemLinkFixture();
        var sourceRoot = Directory.CreateDirectory(Path.Combine(fixture.Archive, "source")).FullName;
        var payloadPath = Path.Combine(sourceRoot, "data.txt");
        await File.WriteAllTextAsync(payloadPath, "unreadable source contents");
        var originalTimestamp = File.GetLastWriteTimeUtc(payloadPath);
        var originalMode = File.GetUnixFileMode(sourceRoot);
        using var services = CreateServices(sourceRoot);
        var store = services.GetRequiredService<IObjectStore>();

        try
        {
            // Search permission keeps the directory and known children visible;
            // removing read permission specifically denies its complete listing.
            File.SetUnixFileMode(sourceRoot, UnixFileMode.UserExecute);
            RequirePermissionDenial(() => Directory.GetFileSystemEntries(sourceRoot));

            var exception = await Assert.ThrowsExactlyAsync<YabtFileSystemException>
            (
                () => store.EnsureReadyAsync()
            );

            Assert.AreEqual(sourceRoot, exception.Path);
            AssertPermissionDenied(exception);
        }
        finally
        {
            File.SetUnixFileMode(sourceRoot, originalMode);
        }

        Assert.AreEqual("unreadable source contents", await File.ReadAllTextAsync(payloadPath));
        Assert.AreEqual(originalTimestamp, File.GetLastWriteTimeUtc(payloadPath));
        CollectionAssert.AreEqual(new[] { sourceRoot }, Directory.GetFileSystemEntries(fixture.Archive));
        CollectionAssert.AreEqual(new[] { payloadPath }, Directory.GetFileSystemEntries(sourceRoot));
    }

    [TestMethod]
    public async Task GetFolderItemsAsyncRejectsPermissionLostAfterReadiness()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("Linux filesystem permission test; requires an ordinary Linux user.");
            return;
        }
        RequireNonRootUser();
        using var fixture = new FileSystemLinkFixture();
        var restrictedPath = Directory.CreateDirectory(Path.Combine(fixture.Archive, "restricted")).FullName;
        var payloadPath = Path.Combine(restrictedPath, "data.txt");
        await File.WriteAllTextAsync(payloadPath, "later listing must not report an empty folder");
        var originalMode = File.GetUnixFileMode(restrictedPath);
        using var services = CreateServices(fixture.Archive);
        var store = services.GetRequiredService<IObjectStore>();
        await store.EnsureReadyAsync();

        try
        {
            File.SetUnixFileMode(restrictedPath, UnixFileMode.UserExecute);
            RequirePermissionDenial(() => Directory.GetFileSystemEntries(restrictedPath));
            var emittedItems = 0;
            var exception = await Assert.ThrowsAsync<Exception>(async () =>
            {
                var items = store.GetFolderItemsAsync("restricted");
                await foreach (var item in items)
                {
                    emittedItems++;
                }
            });

            AssertPermissionDenied(exception);
            Assert.AreEqual(0, emittedItems, "An inaccessible listing must fail rather than yield incomplete entries.");
        }
        finally
        {
            File.SetUnixFileMode(restrictedPath, originalMode);
        }

        Assert.AreEqual("later listing must not report an empty folder", await File.ReadAllTextAsync(payloadPath));
        CollectionAssert.AreEqual(new[] { restrictedPath }, Directory.GetFileSystemEntries(fixture.Archive));
        CollectionAssert.AreEqual(new[] { payloadPath }, Directory.GetFileSystemEntries(restrictedPath));
        var recoveredItems = new List<string>();
        var recoveredListing = store.GetFolderItemsAsync("restricted");
        await foreach (var item in recoveredListing)
        {
            Assert.IsNotNull(item.Object);
            recoveredItems.Add(item.Object.Key);
        }
        CollectionAssert.AreEqual(new[] { "restricted/data.txt" }, recoveredItems);
    }

    [TestMethod]
    public async Task OpenReadAsyncRejectsUnreadableFileWithoutChangingIt()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("Linux filesystem permission test; requires an ordinary Linux user.");
            return;
        }
        RequireNonRootUser();
        using var fixture = new FileSystemLinkFixture();
        var payloadPath = Path.Combine(fixture.Archive, "data.txt");
        await File.WriteAllTextAsync(payloadPath, "source read failure preserves bytes");
        var originalTimestamp = File.GetLastWriteTimeUtc(payloadPath);
        var originalMode = File.GetUnixFileMode(payloadPath);
        using var services = CreateServices(fixture.Archive);
        var store = services.GetRequiredService<IObjectStore>();

        try
        {
            File.SetUnixFileMode(payloadPath, UnixFileMode.None);
            RequirePermissionDenial(() => File.ReadAllBytes(payloadPath));
            // Readiness checks directory access; it is not a promise that all
            // later payload reads will succeed.
            await store.EnsureReadyAsync();
            var exception = await Assert.ThrowsExactlyAsync<YabtFileSystemException>(async () =>
            {
                await using var content = await store.OpenReadAsync("data.txt");
                await content.Content.CopyToAsync(Stream.Null);
            });

            Assert.AreEqual("data.txt", exception.Key);
            AssertPermissionDenied(exception);
        }
        finally
        {
            File.SetUnixFileMode(payloadPath, originalMode);
        }

        Assert.AreEqual("source read failure preserves bytes", await File.ReadAllTextAsync(payloadPath));
        Assert.AreEqual(originalTimestamp, File.GetLastWriteTimeUtc(payloadPath));
        CollectionAssert.AreEqual(new[] { payloadPath }, Directory.GetFileSystemEntries(fixture.Archive));
    }

    private static ServiceProvider CreateServices(string rootPath)
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddYabtFileSystemObjectStore();
        services.AddSingleton<IOptionsMonitor<FileSystemObjectStoreOptions>>
        (
            new LinuxPermissionOptionsMonitor(new() { RootPath = rootPath })
        );
        var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IObjectStore>();
        ((ISourceDirectoryTraversal)store).ConfigureSourceTraversal((_, _) => Task.FromResult(FolderPolicy.Default));
        return provider;
    }

    private static void RequireNonRootUser()
    {
        if (GetEffectiveUserId() == 0)
        {
            Assert.Inconclusive("Permission-denial acceptance requires a non-root Linux process.");
        }
    }

    private static void RequirePermissionDenial(Action probe)
    {
        try
        {
            probe();
        }
        catch (Exception exception)
        {
            Assert.IsInstanceOfType<UnauthorizedAccessException>(exception);
            return;
        }
        Assert.Inconclusive("This process can bypass Unix mode restrictions; use an ordinary unprivileged Linux user.");
    }

    private static void AssertPermissionDenied(Exception exception)
    {
        var cause = exception.GetBaseException();
        Assert.IsTrue
        (
            cause is UnauthorizedAccessException || cause is Win32Exception { NativeErrorCode: 13 },
            "The provider must propagate the actual permission error: " + exception
        );
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();

    private sealed class LinuxPermissionOptionsMonitor(FileSystemObjectStoreOptions _value)
        : IOptionsMonitor<FileSystemObjectStoreOptions>
    {
        public FileSystemObjectStoreOptions CurrentValue => _value;

        public FileSystemObjectStoreOptions Get(string? name) => _value;

        public IDisposable? OnChange(Action<FileSystemObjectStoreOptions, string?> listener) => null;
    }
}
