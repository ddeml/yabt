using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Yabt.Core.Models;
using Yabt.FileSystem.Implementation;

namespace Yabt.FileSystem.Tests;

[TestClass]
public sealed class FileSystemRestoreDestinationTests
{
    [TestMethod]
    public async Task StagingTokensCommitOnlyThroughTheirOwningDestination()
    {
        using var fixture = new FileSystemLinkFixture();
        var factory = CreateFactory();
        var destination = factory.Create(fixture.Archive);
        var otherDestination = factory.Create(fixture.Outside);
        var modified = new DateTimeOffset(2025, 3, 4, 5, 6, 8, TimeSpan.Zero);
        await using (var area = await destination.CreateStagingAreaAsync())
        {
            var token = area.CreateFileToken();
            Assert.IsFalse(Path.IsPathRooted(token));
            Assert.IsFalse(token.Contains(Path.DirectorySeparatorChar));
            await using (var stream = area.OpenWrite(token))
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes("validated content"));
            }
            await Assert.ThrowsExactlyAsync<YabtFileSystemException>
            (
                () => otherDestination.CommitStagedFileAsync("file.txt", token, modified)
            );
            Assert.ThrowsExactly<YabtFileSystemException>(() => area.OpenWrite("unowned-token"));
            await destination.CommitStagedFileAsync("folder/file.txt", token, modified);
            Assert.ThrowsExactly<YabtFileSystemException>(() => area.OpenWrite(token));
            area.DeleteFile(token);
        }

        var restoredPath = Path.Combine(fixture.Archive, "folder", "file.txt");
        Assert.AreEqual("validated content", await File.ReadAllTextAsync(restoredPath));
        Assert.AreEqual(modified.UtcDateTime, File.GetLastWriteTimeUtc(restoredPath));
        Assert.IsTrue(destination.DirectoryExists(ObjectPath.Parse("folder")));
        Assert.IsEmpty(Directory.GetFileSystemEntries(Path.Combine(fixture.Archive, ".yabt-tmp")));
        Assert.IsEmpty(Directory.GetFileSystemEntries(fixture.Outside));
    }

    [TestMethod]
    public async Task StagingRejectsLinkedPlumbingBeforeWritingOutsideTheDestination()
    {
        using var fixture = new FileSystemLinkFixture();
        fixture.LinkDirectory(Path.Combine(fixture.Archive, ".yabt-tmp"), fixture.Outside);
        var destination = CreateFactory().Create(fixture.Archive);

        await Assert.ThrowsExactlyAsync<YabtFileSystemException>(() => destination.CreateStagingAreaAsync());
        Assert.IsEmpty(Directory.GetFileSystemEntries(fixture.Outside));
    }

    [TestMethod]
    public async Task ReservedTemporaryRootCaseVariantWorksWithoutExemptingOrdinaryNames()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Windows directory semantics test."); }
        using var fixture = new FileSystemLinkFixture();
        var temporaryRoot = Directory.CreateDirectory(Path.Combine(fixture.Archive, ".YABT-TMP")).FullName;
        Directory.CreateDirectory(Path.Combine(fixture.Archive, "live", ".YABT-TMP"));
        var destination = CreateFactory().Create(fixture.Archive);
        await using (var area = await destination.CreateStagingAreaAsync())
        {
            var token = area.CreateFileToken();
            await using (var stream = area.OpenWrite(token))
            {
                await stream.WriteAsync("contents"u8.ToArray());
            }
            await destination.CommitStagedFileAsync("file.txt", token, lastModifiedUtc: null);
        }
        await using (var content = new MemoryStream("provider upload"u8.ToArray()))
        {
            await destination.Store.UploadAsync("upload.txt", content, "text/plain", new Dictionary<string, string>());
        }
        await Assert.ThrowsExactlyAsync<YabtFileSystemException>
        (
            () => destination.Store.ExistsAsync("live/.yabt-tmp/file.txt")
        );
        Assert.IsEmpty(Directory.GetFileSystemEntries(temporaryRoot));
        Assert.AreEqual("contents", await File.ReadAllTextAsync(Path.Combine(fixture.Archive, "file.txt")));
        Assert.AreEqual("provider upload", await File.ReadAllTextAsync(Path.Combine(fixture.Archive, "upload.txt")));
    }

    [TestMethod]
    public async Task ReplayStorageIsSeekableAndRemovedOnClose()
    {
        var factory = CreateFactory();
        string nativePath;
        await using (var replay = factory.CreateComparisonReplayStream())
        {
            Assert.IsTrue(replay.CanSeek);
            Assert.IsTrue(replay.CanRead);
            Assert.IsTrue(replay.CanWrite);
            nativePath = ((FileStream)replay).Name;
            await replay.WriteAsync("snapshot"u8.ToArray());
            Assert.AreEqual(8, replay.Length);
        }
        Assert.IsFalse(File.Exists(nativePath));
    }

    private static FileSystemRestoreDestinationFactory CreateFactory() => new
    (
        [new FileSystemBackupRootStoreResolver(NullLogger<FileSystemObjectStore>.Instance)],
        NullLogger<FileSystemRestoreTarget>.Instance
    );
}
