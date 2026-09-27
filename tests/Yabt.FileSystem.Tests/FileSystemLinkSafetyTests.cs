using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.FileSystem.Implementation;

namespace Yabt.FileSystem.Tests;

[TestClass]
public sealed class FileSystemLinkSafetyTests
{
    [TestMethod]
    public async Task OrdinaryStoreRejectsLinkedAncestorsAcrossOperations()
    {
        using var fixture = new FileSystemLinkFixture();
        Directory.CreateDirectory(Path.Combine(fixture.Outside, "history"));
        var outsideFile = Path.Combine(fixture.Outside, "history", "file.txt");
        await File.WriteAllTextAsync(outsideFile, "outside");
        await File.WriteAllTextAsync(Path.Combine(fixture.Archive, "inside.txt"), "inside");
        fixture.LinkDirectory(Path.Combine(fixture.Archive, "linked"), fixture.Outside);
        using var services = CreateServices(fixture.Archive);
        var store = services.GetRequiredService<IObjectStore>();
        var mutable = (IArchiveMutableObjectStore)store;
        var outsideHash = ArchiveHash.Compute("outside"u8);

        await Assert.ThrowsExactlyAsync<YabtFileSystemException>(() => store.OpenReadAsync("linked/history/file.txt"));
        await Assert.ThrowsExactlyAsync<YabtFileSystemException>(() => store.ExistsAsync("linked/history/file.txt"));
        await Assert.ThrowsExactlyAsync<YabtFileSystemException>(() => ListAsync(store, "linked/history"));
        await Assert.ThrowsExactlyAsync<YabtFileSystemException>(() => ListAsync(store, null));
        await Assert.ThrowsExactlyAsync<YabtFileSystemException>(() => UploadAsync(store, "linked/history/new.txt"));
        await Assert.ThrowsExactlyAsync<YabtFileSystemException>(() => store.MoveAsync("linked/history/file.txt", "moved.txt"));
        await Assert.ThrowsExactlyAsync<YabtFileSystemException>(() => store.MoveAsync("inside.txt", "linked/history/moved.txt"));
        await Assert.ThrowsExactlyAsync<YabtFileSystemException>(() => store.MoveFolderAsync("linked/history", "moved"));
        await Assert.ThrowsExactlyAsync<YabtFileSystemException>(() => mutable.TryDeleteIfContentHashMatchesAsync("linked/history/file.txt", outsideHash));
        await using var replacement = new MemoryStream("replacement"u8.ToArray());
        await Assert.ThrowsExactlyAsync<YabtFileSystemException>
        (
            () => mutable.TryReplaceIfContentHashMatchesAsync
        (
            "linked/history/file.txt", outsideHash, replacement, "text/plain", new Dictionary<string, string>()
        )
        );

        Assert.AreEqual("outside", await File.ReadAllTextAsync(outsideFile));
        CollectionAssert.AreEquivalent(new[] { outsideFile }, Directory.GetFiles(fixture.Outside, "*", SearchOption.AllDirectories));
        Assert.AreEqual("inside", await File.ReadAllTextAsync(Path.Combine(fixture.Archive, "inside.txt")));
    }

    [TestMethod]
    public async Task LinkedStagingRefusesUploadConditionalMutationAndArchiveLock()
    {
        using var fixture = new FileSystemLinkFixture();
        fixture.LinkDirectory(Path.Combine(fixture.Archive, ".yabt-tmp"), fixture.Outside);
        await File.WriteAllTextAsync(Path.Combine(fixture.Archive, "file.txt"), "inside");
        using var services = CreateServices(fixture.Archive);
        var store = services.GetRequiredService<IObjectStore>();
        var mutable = (IArchiveMutableObjectStore)store;
        await Assert.ThrowsExactlyAsync<YabtFileSystemException>(() => UploadAsync(store, "new.txt"));
        await Assert.ThrowsExactlyAsync<YabtFileSystemException>(() => mutable.AcquireArchiveMutationLockAsync());
        await Assert.ThrowsExactlyAsync<YabtFileSystemException>
        (
            () => mutable.TryDeleteIfContentHashMatchesAsync
        (
            "file.txt", ArchiveHash.Compute("inside"u8)
        )
        );
        Assert.IsEmpty(Directory.GetFileSystemEntries(fixture.Outside));
        Assert.AreEqual("inside", await File.ReadAllTextAsync(Path.Combine(fixture.Archive, "file.txt")));
    }

    [TestMethod]
    public async Task ConfiguredLinkedRootRemainsUsable()
    {
        using var fixture = new FileSystemLinkFixture();
        var linkedRoot = Path.Combine(fixture.Root, "linked-root");
        fixture.LinkDirectory(linkedRoot, fixture.Archive);
        using var services = CreateServices(linkedRoot);
        var store = services.GetRequiredService<IObjectStore>();
        await UploadAsync(store, "folder/file.txt");
        await using var mutationLock = await ((IArchiveMutableObjectStore)store).AcquireArchiveMutationLockAsync();
        var items = await ListAsync(store, "folder");
        Assert.AreEqual("folder/file.txt", items.Single().Object!.Key);
        Assert.AreEqual("uploaded", await File.ReadAllTextAsync(Path.Combine(fixture.Archive, "folder", "file.txt")));
    }

    [TestMethod]
    public async Task SourceApprovalAllowsReadButNeverMutationAndRechecksRetargeting()
    {
        using var fixture = new FileSystemLinkFixture();
        await File.WriteAllTextAsync(Path.Combine(fixture.Outside, "file.txt"), "outside");
        var link = Path.Combine(fixture.Archive, "linked");
        fixture.LinkDirectory(link, fixture.Outside);
        using var services = CreateServices(fixture.Archive);
        var store = services.GetRequiredService<IObjectStore>();
        var callbacks = new List<string>();
        ((ISourceDirectoryTraversal)store).ConfigureSourceTraversal
        (
            (parent, _) =>
        {
            callbacks.Add(parent);
            return Task.FromResult(Approve("linked", "../outside"));
        }
        );

        var items = await ListAsync(store, "linked");
        Assert.AreEqual("linked/file.txt", items.Single().Object!.Key);
        await using (var content = await store.OpenReadAsync("linked/file.txt"))
        {
            using var reader = new StreamReader(content.Content);
            Assert.AreEqual("outside", await reader.ReadToEndAsync());
        }
        Assert.IsTrue(callbacks.All(parent => parent == string.Empty));
        await Assert.ThrowsExactlyAsync<YabtFileSystemException>(() => UploadAsync(store, "linked/new.txt"));
        await Assert.ThrowsExactlyAsync<YabtFileSystemException>(() => store.MoveAsync("linked/file.txt", "new.txt"));

        Directory.Delete(link, recursive: false);
        var changedTarget = Directory.CreateDirectory(Path.Combine(fixture.Root, "changed-target")).FullName;
        FileSystemLinkFixture.CreateDirectoryLink(link, changedTarget);
        var exception = await Assert.ThrowsExactlyAsync<YabtFileSystemException>(() => ListAsync(store, "linked"));
        StringAssert.Contains(exception.Message, "approved expected target");
    }

    [TestMethod]
    public async Task SourceApprovalRejectsForbiddenTargetAndSnapshotsConfiguration()
    {
        using var fixture = new FileSystemLinkFixture();
        fixture.LinkDirectory(Path.Combine(fixture.Archive, "linked"), fixture.Outside);
        var forbidden = new List<string> { fixture.Outside };
        using var services = CreateServices(fixture.Archive);
        var store = services.GetRequiredService<IObjectStore>();
        var traversal = (ISourceDirectoryTraversal)store;
        traversal.ConfigureSourceTraversal((_, _) => Task.FromResult(Approve("linked", "../outside")), forbidden);
        forbidden.Clear();
        var exception = await Assert.ThrowsExactlyAsync<YabtFileSystemException>(() => ListAsync(store, "linked"));
        StringAssert.Contains(exception.Message, "overlaps reserved or archive storage");
        Assert.ThrowsExactly<YabtFileSystemException>(() => traversal.ConfigureSourceTraversal((_, _) => Task.FromResult(FolderPolicy.Default)));
    }

    [TestMethod]
    public async Task SourceApprovalIsExactAndNoncyclicAliasesRemainSeparate()
    {
        using var fixture = new FileSystemLinkFixture();
        await File.WriteAllTextAsync(Path.Combine(fixture.Outside, "file.txt"), "outside");
        fixture.LinkDirectory(Path.Combine(fixture.Archive, "one"), fixture.Outside);
        fixture.LinkDirectory(Path.Combine(fixture.Archive, "two"), fixture.Outside);
        using var services = CreateServices(fixture.Archive);
        var store = services.GetRequiredService<IObjectStore>();
        ((ISourceDirectoryTraversal)store).ConfigureSourceTraversal
        (
            (_, _) => Task.FromResult
        (
            new FolderPolicy
        (
            DirectoryLinks: new Dictionary<string, DirectoryLinkPolicy>
            {
                ["one"] = new("follow", "../outside"),
                ["two"] = new("follow", "../outside"),
            }
        )
        )
        );
        var items = await ListAsync(store, null, recursive: true);
        CollectionAssert.AreEquivalent(new[] { "one/file.txt", "two/file.txt" }, items.Select(item => item.Object!.Key).ToArray());
    }

    [TestMethod]
    public async Task SourceDirectoryCycleIsRejectedAcrossSeparateFolderListings()
    {
        using var fixture = new FileSystemLinkFixture();
        fixture.LinkDirectory(Path.Combine(fixture.Archive, "linked"), fixture.Outside);
        fixture.LinkDirectory(Path.Combine(fixture.Outside, "back"), fixture.Archive);
        using var services = CreateServices(fixture.Archive);
        var store = services.GetRequiredService<IObjectStore>();
        ((ISourceDirectoryTraversal)store).ConfigureSourceTraversal
        (
            (parent, _) => Task.FromResult
        (
            parent == string.Empty ? Approve("linked", "../outside") : Approve("back", "../archive")
        )
        );
        var rootItems = await ListAsync(store, null);
        Assert.AreEqual("linked", rootItems.Single().Name);
        var exception = await Assert.ThrowsExactlyAsync<YabtFileSystemException>(() => ListAsync(store, "linked"));
        StringAssert.Contains(exception.Message, "traversal cycle");
    }

    [TestMethod]
    public async Task NestedRelativeApprovalUsesPhysicalPolicyDirectory()
    {
        using var fixture = new FileSystemLinkFixture();
        var first = Directory.CreateDirectory(Path.Combine(fixture.Outside, "first")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(fixture.Outside, "second")).FullName;
        await File.WriteAllTextAsync(Path.Combine(second, "file.txt"), "nested");
        fixture.LinkDirectory(Path.Combine(fixture.Archive, "linked"), first);
        fixture.LinkDirectory(Path.Combine(first, "next"), second);
        using var services = CreateServices(fixture.Archive);
        var store = services.GetRequiredService<IObjectStore>();
        ((ISourceDirectoryTraversal)store).ConfigureSourceTraversal
        (
            (parent, _) => Task.FromResult
        (
            parent == string.Empty ? Approve("linked", "../outside/first") : Approve("next", "../second")
        )
        );

        var files = await ListAsync(store, "linked/next");
        Assert.AreEqual("linked/next/file.txt", files.Single().Object!.Key);
        await using var content = await store.OpenReadAsync("linked/next/file.txt");
        using var reader = new StreamReader(content.Content);
        Assert.AreEqual("nested", await reader.ReadToEndAsync());
    }

    [TestMethod]
    public async Task ApprovalRequiresExactChildNameAndPolicyCannotBeALink()
    {
        using var fixture = new FileSystemLinkFixture();
        fixture.LinkDirectory(Path.Combine(fixture.Archive, "linked"), fixture.Outside);
        using var services = CreateServices(fixture.Archive);
        var store = services.GetRequiredService<IObjectStore>();
        var callbackCount = 0;
        ((ISourceDirectoryTraversal)store).ConfigureSourceTraversal
        (
            (_, _) =>
        {
            callbackCount++;
            return Task.FromResult(Approve("Linked", "../outside"));
        }
        );
        var exception = await Assert.ThrowsExactlyAsync<YabtFileSystemException>(() => ListAsync(store, "linked"));
        StringAssert.Contains(exception.Message, "no explicit follow approval");
        var priorCallbackCount = callbackCount;

        fixture.LinkDirectory(Path.Combine(fixture.Archive, ".yabt-policy.json"), fixture.Outside);
        exception = await Assert.ThrowsExactlyAsync<YabtFileSystemException>(() => ListAsync(store, "linked"));
        StringAssert.Contains(exception.Message, "policy");
        Assert.AreEqual(priorCallbackCount, callbackCount);
    }

    [TestMethod]
    public async Task OrdinarySourceDirectoriesDoNotRequireLinkPolicyOrPhysicalIdentitySupport()
    {
        using var fixture = new FileSystemLinkFixture();
        Directory.CreateDirectory(Path.Combine(fixture.Archive, "folder"));
        await File.WriteAllTextAsync(Path.Combine(fixture.Archive, "folder", "file.txt"), "ordinary");
        using var services = CreateServices(fixture.Archive);
        var store = services.GetRequiredService<IObjectStore>();
        ((ISourceDirectoryTraversal)store).ConfigureSourceTraversal
        (
            (_, _) =>
            throw new InvalidOperationException("Ordinary directories do not need link approvals.")
        );
        var files = await ListAsync(store, null, recursive: true);
        Assert.AreEqual("folder/file.txt", files.Single().Object!.Key);
    }

    [TestMethod]
    public void WindowsIdentityPathsRejectRemoteShareAliasesAndPreserveVolumeRoots()
    {
        Assert.ThrowsExactly<YabtFileSystemException>
        (
            () =>
            FileSystemDirectoryIdentity.NormalizeWindowsPhysicalPath(@"\\server\share\folder")
        );
        Assert.ThrowsExactly<YabtFileSystemException>
        (
            () =>
            FileSystemDirectoryIdentity.NormalizeWindowsPhysicalPath(@"\\?\UNC\server\share\folder")
        );
        Assert.AreEqual(@"C:\folder", FileSystemDirectoryIdentity.NormalizeWindowsPhysicalPath(@"\\?\C:\folder"));
        const string volumePath = @"\\?\Volume{649d0c62-e577-46a3-8c61-24f3bca9e706}\folder";
        Assert.AreEqual(volumePath, FileSystemDirectoryIdentity.NormalizeWindowsPhysicalPath(volumePath));
    }

    private static FolderPolicy Approve(string name, string expectedTarget) => new
    (
        DirectoryLinks: new Dictionary<string, DirectoryLinkPolicy>
        {
            [name] = new(DirectoryLinkPolicy.FollowBehavior, expectedTarget),
        }
    );

    private static ServiceProvider CreateServices(string rootPath)
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddYabtFileSystemObjectStore();
        var provider = services.BuildServiceProvider();
        var resolver = provider.GetRequiredService<ISourceRootObjectStoreResolver>();
        var store = resolver.ResolveSourceRoot(rootPath);
        provider.Dispose();
        var stores = new ServiceCollection();
        stores.AddSingleton(store);
        return stores.BuildServiceProvider();
    }

    private static async Task UploadAsync(IObjectStore store, string key)
    {
        await using var content = new MemoryStream("uploaded"u8.ToArray());
        await store.UploadAsync(key, content, "text/plain", new Dictionary<string, string>());
    }

    private static async Task<List<ArchiveFolderItem>> ListAsync(IReadOnlyObjectStore store, string? prefix, bool recursive = false)
    {
        var items = new List<ArchiveFolderItem>();
        await foreach (var item in store.GetFolderItemsAsync(prefix, recursive)) { items.Add(item); }
        return items;
    }
}
