using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;

namespace Yabt.FileSystem.Tests;

[TestClass]
public sealed class FileSystemObjectIdentityTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LegalWhitespaceNamesRoundTripWithoutMergingSiblings(bool sourceTraversal)
    {
        using var fixture = new FileSystemLinkFixture();
        var store = CreateStore(fixture.Archive, sourceTraversal);
        var keys = new List<string>
        {
            " documents/ report.txt",
            "documents/report.txt",
            "internal spaces/file name.txt",
            "\u2003/\u00a0",
        };
        if (!OperatingSystem.IsWindows())
        {
            keys.Add(" /file ");
            keys.Add("\t/line\nname.txt");
        }

        foreach (var key in keys)
        {
            await UploadAsync(store, key, $"content of {key}");
        }

        var listed = await ListAsync(store, recursive: true);
        CollectionAssert.AreEquivalent(keys, listed.Select(item => item.Object!.Key).ToArray());
        foreach (var key in keys)
        {
            Assert.IsTrue(await store.ExistsAsync(key));
            Assert.AreEqual($"content of {key}", await ReadAsync(store, key));
            var nativePath = Path.Combine(fixture.Archive, key.Replace('/', Path.DirectorySeparatorChar));
            Assert.AreEqual($"content of {key}", await File.ReadAllTextAsync(nativePath));
        }

        var nativeKeys = Directory.GetFiles(fixture.Archive, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(fixture.Archive, path).Replace(Path.DirectorySeparatorChar, '/'))
            .ToArray();
        CollectionAssert.AreEquivalent(keys, nativeKeys);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task GuardedMutationsAndMovesPreserveSpacedSiblingIdentity(bool sourceTraversal)
    {
        using var fixture = new FileSystemLinkFixture();
        var store = CreateStore(fixture.Archive, sourceTraversal);
        var mutable = (IArchiveMutableObjectStore)store;
        await UploadAsync(store, " report.txt", "spaced");
        await UploadAsync(store, "report.txt", "plain");

        await store.MoveAsync(" report.txt", " history/ report.txt");
        await using var replacement = new MemoryStream("changed"u8.ToArray());
        Assert.IsTrue
        (
            await mutable.TryReplaceIfContentHashMatchesAsync
            (
                " history/ report.txt",
                ArchiveHash.Compute("spaced"u8),
                replacement,
                "text/plain",
                new Dictionary<string, string>()
            )
        );
        await store.MoveFolderAsync(" history", " archived");

        Assert.AreEqual("changed", await ReadAsync(store, " archived/ report.txt"));
        Assert.AreEqual("plain", await ReadAsync(store, "report.txt"));
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Archive, "archived")));
        Assert.IsTrue
        (
            await mutable.TryDeleteIfContentHashMatchesAsync
            (
                " archived/ report.txt",
                ArchiveHash.Compute("changed"u8)
            )
        );
        Assert.AreEqual("plain", await ReadAsync(store, "report.txt"));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Archive, " report.txt")));
    }

    [TestMethod]
    public async Task UnrepresentableKeysCannotReadOrMutateAliases()
    {
        using var fixture = new FileSystemLinkFixture();
        var store = CreateStore(fixture.Archive);
        var mutable = (IArchiveMutableObjectStore)store;
        var sentinelPath = Path.Combine(fixture.Archive, "report.txt");
        await File.WriteAllTextAsync(sentinelPath, "sentinel");
        var keys = new List<string>
        {
            "/report.txt",
            "C:report.txt",
            "folder//report.txt",
            "../report.txt",
            "folder/./report.txt",
            "file\0name",
        };
        if (OperatingSystem.IsWindows())
        {
            keys.AddRange
            (
                [
                    "report.txt ", "report.txt.", "folder./report.txt", "folder /report.txt",
                    "report.txt:stream", "CON", "con.txt", "COM¹", "LPT².log", "CONIN$", "CONOUT$",
                ]
            );
        }

        foreach (var key in keys)
        {
            await Assert.ThrowsExactlyAsync<YabtFileSystemException>(() => store.ExistsAsync(key));
            await Assert.ThrowsExactlyAsync<YabtFileSystemException>(() => store.OpenReadAsync(key));
            await Assert.ThrowsExactlyAsync<YabtFileSystemException>(() => UploadAsync(store, key, "new"));
            await Assert.ThrowsExactlyAsync<YabtFileSystemException>(() => store.MoveAsync("report.txt", key));
            await Assert.ThrowsExactlyAsync<YabtFileSystemException>
            (
                () => mutable.TryDeleteIfContentHashMatchesAsync(key, ArchiveHash.Compute("sentinel"u8))
            );
            Assert.AreEqual("sentinel", await File.ReadAllTextAsync(sentinelPath));
        }

        CollectionAssert.AreEqual(new[] { sentinelPath }, Directory.GetFileSystemEntries(fixture.Archive));
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task NativeUnsupportedNamesAreRefusedIncludingEmptyDirectories
    (
        bool sourceTraversal,
        bool directory
    )
    {
        using var fixture = new FileSystemLinkFixture();
        var store = CreateStore(fixture.Archive, sourceTraversal);
        var nativeName = OperatingSystem.IsWindows() ? "unsupported." : "literal\\backslash";
        var nativePath = Path.Combine(fixture.Archive, nativeName);
        var exactPath = OperatingSystem.IsWindows() ? $"\\\\?\\{nativePath}" : nativePath;
        try
        {
            if (directory)
            {
                Directory.CreateDirectory(exactPath);
            }
            else
            {
                await File.WriteAllTextAsync(exactPath, "must not be renamed");
            }

            await Assert.ThrowsExactlyAsync<YabtFileSystemException>(() => ListAsync(store, recursive: false));
            await Assert.ThrowsExactlyAsync<YabtFileSystemException>(() => ListAsync(store, recursive: true));
        }
        finally
        {
            // Delete only the exact private fixture name, retaining extended Windows syntax.
            if (directory) { Directory.Delete(exactPath, recursive: false); }
            else { File.Delete(exactPath); }
        }
    }

    [TestMethod]
    public async Task LogicalBackslashesAreRefusedWithoutCreatingAnotherHierarchy()
    {
        using var fixture = new FileSystemLinkFixture();
        var store = CreateStore(fixture.Archive);
        await Assert.ThrowsExactlyAsync<YabtFileSystemException>
        (
            () => UploadAsync(store, " leading\\ file.txt", "separators")
        );
        Assert.IsEmpty(Directory.GetFileSystemEntries(fixture.Archive));
    }

    private static IObjectStore CreateStore(string rootPath, bool sourceTraversal = false)
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddYabtFileSystemObjectStore();
        using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<ISourceRootObjectStoreResolver>().ResolveSourceRoot(rootPath);
        if (sourceTraversal)
        {
            ((ISourceDirectoryTraversal)store).ConfigureSourceTraversal
            (
                (_, _) => Task.FromResult(FolderPolicy.Default)
            );
        }
        return store;
    }

    private static async Task UploadAsync(IObjectStore store, string key, string value)
    {
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes(value));
        await store.UploadAsync(key, content, "text/plain", new Dictionary<string, string>());
    }

    private static async Task<string> ReadAsync(IReadOnlyObjectStore store, string key)
    {
        await using var content = await store.OpenReadAsync(key);
        using var reader = new StreamReader(content.Content);
        return await reader.ReadToEndAsync();
    }

    private static async Task<List<ArchiveFolderItem>> ListAsync(IReadOnlyObjectStore store, bool recursive)
    {
        var items = new List<ArchiveFolderItem>();
        var folderItems = store.GetFolderItemsAsync(null, recursive);
        await foreach (var item in folderItems) { items.Add(item); }
        return items;
    }
}
