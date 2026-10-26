using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;
using Yabt.Common;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.FileSystem.Implementation;

namespace Yabt.FileSystem.Tests;

[TestClass]
public sealed class FileSystemHardLinkTests
{
    [TestMethod]
    public async Task NativeIdentityRecognizesSeparateFilesAndHardLinksFromOpenedHandles()
    {
        using var fixture = new FileSystemLinkFixture();
        var original = Path.Combine(fixture.Outside, "original.txt");
        var alias = Path.Combine(fixture.Archive, "alias.txt");
        var independent = Path.Combine(fixture.Archive, "independent.txt");
        await File.WriteAllTextAsync(original, "same bytes");
        await File.WriteAllTextAsync(independent, "same bytes");
        CreateHardLink(alias, original);

        var originalIdentity = ReadIdentity(original);
        var aliasIdentity = ReadIdentity(alias);
        var independentIdentity = ReadIdentity(independent);
        Assert.AreEqual(originalIdentity.Id, aliasIdentity.Id);
        Assert.AreEqual(2u, originalIdentity.LinkCount);
        Assert.AreEqual(2u, aliasIdentity.LinkCount);
        Assert.AreNotEqual(originalIdentity.Id, independentIdentity.Id);
        Assert.AreEqual(1u, independentIdentity.LinkCount);
        using var handle = File.OpenHandle(original, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var sharedIdentity = NativeFileIdentity.Read(handle, original);
        Assert.AreEqual(originalIdentity.Id, sharedIdentity.Id);
        Assert.AreEqual(originalIdentity.LinkCount, sharedIdentity.LinkCount);
        Assert.ThrowsExactly<YabtException>(() => NativeFileIdentity.RequireIndependent(handle, original));
    }

    [TestMethod]
    public async Task IndependenceCheckRechecksLinkCountWithoutChangingSourceReadBehavior()
    {
        using var fixture = new FileSystemLinkFixture();
        var source = Path.Combine(fixture.Archive, "source.txt");
        var alias = Path.Combine(fixture.Outside, "alias.txt");
        await File.WriteAllTextAsync(source, "shared bytes");
        using var services = CreateServices(fixture.Archive);
        var store = services.GetRequiredService<IObjectStore>();
        var mutable = (IArchiveMutableObjectStore)store;

        Assert.IsTrue(await mutable.IsIndependentMaterializationAsync("source.txt"));
        CreateHardLink(alias, source);
        Assert.IsFalse(await mutable.IsIndependentMaterializationAsync("source.txt"));
        await using (var content = await store.OpenReadAsync("source.txt"))
        {
            using var reader = new StreamReader(content.Content);
            Assert.AreEqual("shared bytes", await reader.ReadToEndAsync());
        }

        File.Delete(alias);
        Assert.IsTrue(await mutable.IsIndependentMaterializationAsync("source.txt"));
        File.Delete(source);
        await Assert.ThrowsExactlyAsync<YabtFileSystemException>
        (
            () => mutable.IsIndependentMaterializationAsync("source.txt")
        );
    }

    [TestMethod]
    public void UnavailableOpenedHandleEvidenceIsRejected()
    {
        using var invalidHandle = new SafeFileHandle(new IntPtr(-1), ownsHandle: false);
        Assert.ThrowsExactly<YabtException>(() => NativeFileIdentity.Read(invalidHandle, "invalid"));
        Assert.ThrowsExactly<YabtFileSystemException>(() => FileSystemFileIdentity.Read(invalidHandle, "invalid"));
    }

    [TestMethod]
    public async Task HardLinkedArchiveLockIsRefusedWithoutChangingEitherNameOrContents()
    {
        using var fixture = new FileSystemLinkFixture();
        var external = Path.Combine(fixture.Outside, "external.txt");
        var lockPath = Path.Combine(fixture.Archive, ".yabt-tmp", "archive-mutation-lock.json");
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        await File.WriteAllTextAsync(external, "do not overwrite");
        CreateHardLink(lockPath, external);
        var originalTime = File.GetLastWriteTimeUtc(external);
        using var services = CreateServices(fixture.Archive);
        var store = (IArchiveMutableObjectStore)services.GetRequiredService<IObjectStore>();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var exception = await Assert.ThrowsExactlyAsync<YabtFileSystemException>
        (
            () => store.AcquireArchiveMutationLockAsync(cancellation.Token)
        );

        StringAssert.Contains(exception.ToString(), "hard links");
        Assert.AreEqual("do not overwrite", await File.ReadAllTextAsync(external));
        Assert.AreEqual("do not overwrite", await File.ReadAllTextAsync(lockPath));
        Assert.AreEqual(originalTime, File.GetLastWriteTimeUtc(external));
        Assert.AreEqual(2u, ReadIdentity(lockPath).LinkCount);
    }

    [TestMethod]
    public async Task HardLinkedConditionalGuardRefusesMutationAndPreservesExternalFile()
    {
        using var fixture = new FileSystemLinkFixture();
        var external = Path.Combine(fixture.Outside, "external.txt");
        var guardPath = Path.Combine(fixture.Archive, ".yabt-tmp", "conditional-mutation.lock");
        var payload = Path.Combine(fixture.Archive, "payload.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(guardPath)!);
        await File.WriteAllTextAsync(external, "guard alias");
        await File.WriteAllTextAsync(payload, "payload");
        CreateHardLink(guardPath, external);
        using var services = CreateServices(fixture.Archive);
        var store = (IArchiveMutableObjectStore)services.GetRequiredService<IObjectStore>();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var exception = await Assert.ThrowsExactlyAsync<YabtFileSystemException>
        (
            () => store.TryDeleteIfContentHashMatchesAsync
            (
                "payload.txt", ArchiveHash.Compute("payload"u8), cancellation.Token
            )
        );

        StringAssert.Contains(exception.ToString(), "hard links");
        Assert.AreEqual("payload", await File.ReadAllTextAsync(payload));
        Assert.AreEqual("guard alias", await File.ReadAllTextAsync(external));
        Assert.AreEqual("guard alias", await File.ReadAllTextAsync(guardPath));
        Assert.AreEqual(2u, ReadIdentity(guardPath).LinkCount);
    }

    [TestMethod]
    public async Task AtomicReplacementOfHardLinkedPayloadLeavesExternalContentsIndependent()
    {
        using var fixture = new FileSystemLinkFixture();
        var external = Path.Combine(fixture.Outside, "external.txt");
        var payload = Path.Combine(fixture.Archive, "payload.txt");
        await File.WriteAllTextAsync(external, "previous");
        CreateHardLink(payload, external);
        using var services = CreateServices(fixture.Archive);
        var store = (IArchiveMutableObjectStore)services.GetRequiredService<IObjectStore>();
        await using var replacement = new MemoryStream("replacement"u8.ToArray());

        Assert.IsTrue
        (
            await store.TryReplaceIfContentHashMatchesAsync
            (
                "payload.txt", ArchiveHash.Compute("previous"u8), replacement,
                "text/plain", new Dictionary<string, string>()
            )
        );

        Assert.AreEqual("previous", await File.ReadAllTextAsync(external));
        Assert.AreEqual("replacement", await File.ReadAllTextAsync(payload));
        Assert.IsTrue(await store.IsIndependentMaterializationAsync("payload.txt"));
        await File.WriteAllTextAsync(external, "later external edit");
        Assert.AreEqual("replacement", await File.ReadAllTextAsync(payload));
    }

    [TestMethod]
    public async Task HardLinkedRestoreStagingRefusesCommitBeforeChangingExternalTimestamp()
    {
        using var fixture = new FileSystemLinkFixture();
        var destination = new FileSystemRestoreDestinationFactory
        (
            [new FileSystemBackupRootStoreResolver(NullLogger<FileSystemObjectStore>.Instance)],
            NullLogger<FileSystemRestoreTarget>.Instance
        ).Create(fixture.Archive);
        await using var area = await destination.CreateStagingAreaAsync();
        var token = area.CreateFileToken();
        string stagingPath;
        await using (var stream = area.OpenWrite(token))
        {
            stagingPath = ((FileStream)stream).Name;
            await stream.WriteAsync("staged content"u8.ToArray());
        }
        var external = Path.Combine(fixture.Outside, "external.txt");
        CreateHardLink(external, stagingPath);
        var originalTime = File.GetLastWriteTimeUtc(external);

        var exception = await Assert.ThrowsExactlyAsync<YabtFileSystemException>
        (
            () => destination.CommitStagedFileAsync
            (
                "restored.txt", token, new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero)
            )
        );

        StringAssert.Contains(exception.ToString(), "hard links");
        Assert.AreEqual(originalTime, File.GetLastWriteTimeUtc(external));
        Assert.AreEqual("staged content", await File.ReadAllTextAsync(external));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Archive, "restored.txt")));
        Assert.IsTrue(File.Exists(stagingPath));
    }

    private static FileSystemFileIdentity ReadIdentity(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return FileSystemFileIdentity.Read(handle, path);
    }

    private static ServiceProvider CreateServices(string rootPath)
    {
        var services = new ServiceCollection();
        var resolver = new FileSystemBackupRootStoreResolver(NullLogger<FileSystemObjectStore>.Instance);
        services.AddSingleton(resolver.ResolveSourceRoot(rootPath));
        return services.BuildServiceProvider();
    }

    private static void CreateHardLink(string path, string target)
    {
        var succeeded = OperatingSystem.IsWindows() ? CreateHardLinkW(path, target, IntPtr.Zero) : Link(target, path) == 0;
        if (!succeeded) { throw new Win32Exception(Marshal.GetLastPInvokeError()); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string fileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link
    (
        [MarshalAs(UnmanagedType.LPUTF8Str)] string existingPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath
    );
}
