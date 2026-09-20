using System.Runtime.CompilerServices;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.Metadata;
using Yabt.Sync.Implementation;

namespace Yabt.Sync.Tests;

[TestClass]
public sealed class ArchiveProjectionSourceReadTrackingTests
{
    [TestMethod]
    public async Task OpenReadAsyncMarksSourceOpenFailure()
    {
        var cause = new IOException("The file contains a virus or potentially unwanted software.");
        var store = CreateProjectionStore(new ThrowingOpenReadObjectStore(cause));

        var exception = await Assert.ThrowsExactlyAsync<SourceObjectReadException>(() =>
            store.OpenReadAsync("folder/file.exe"));

        Assert.AreEqual("folder/file.exe", exception.ObjectKey);
        Assert.AreEqual(
            Path.Combine("source", "folder", "file.exe"),
            exception.SourcePath);
        Assert.AreEqual(cause.Message, exception.Reason);
        Assert.AreSame(cause, exception.InnerException);
    }

    [TestMethod]
    public async Task OpenReadAsyncMarksDeferredSynchronousReadFailure()
    {
        var cause = new IOException("Deferred source read failed.");
        var store = CreateProjectionStore(new FailingStreamObjectStore(cause));
        await using var content = await store.OpenReadAsync("file.bin");

        var exception = Assert.ThrowsExactly<SourceObjectReadException>(() =>
            content.Content.ReadByte());

        Assert.AreEqual("file.bin", exception.ObjectKey);
        Assert.AreEqual(Path.Combine("source", "file.bin"), exception.SourcePath);
        Assert.AreSame(cause, exception.InnerException);
    }

    [TestMethod]
    public async Task OpenReadAsyncMarksDeferredAsynchronousReadFailure()
    {
        var cause = new UnauthorizedAccessException("Source access was denied.");
        var store = CreateProjectionStore(new FailingStreamObjectStore(cause));
        await using var content = await store.OpenReadAsync("file.bin");

        var exception = await Assert.ThrowsExactlyAsync<SourceObjectReadException>(async () =>
            await content.Content.ReadExactlyAsync(new byte[1]));

        Assert.AreEqual("file.bin", exception.ObjectKey);
        Assert.AreEqual(cause.Message, exception.Reason);
        Assert.AreSame(cause, exception.InnerException);
    }

    [TestMethod]
    public async Task OpenReadAsyncPreservesCancellation()
    {
        var cancellation = new OperationCanceledException("Source read was canceled.");
        var store = CreateProjectionStore(new ThrowingOpenReadObjectStore(cancellation));

        var exception = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            store.OpenReadAsync("file.bin"));

        Assert.AreSame(cancellation, exception);
    }

    [TestMethod]
    public async Task DeferredReadPreservesCancellation()
    {
        var cancellation = new OperationCanceledException("Source read was canceled.");
        var store = CreateProjectionStore(new FailingStreamObjectStore(cancellation));
        await using var content = await store.OpenReadAsync("file.bin");

        var exception = await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await content.Content.ReadExactlyAsync(new byte[1]));

        Assert.AreSame(cancellation, exception);
    }

    [TestMethod]
    public async Task DeferredReadPreservesExistingMarkedFailure()
    {
        var markedFailure = new SourceObjectReadException
        (
            Path.Combine("source", "inner.bin"),
            "inner.bin",
            new IOException("Already marked.")
        );
        var store = CreateProjectionStore(new FailingStreamObjectStore(markedFailure));
        await using var content = await store.OpenReadAsync("outer.bin");

        var exception = await Assert.ThrowsExactlyAsync<SourceObjectReadException>(async () =>
            await content.Content.ReadExactlyAsync(new byte[1]));

        Assert.AreSame(markedFailure, exception);
    }

    [TestMethod]
    public async Task OpenReadAsyncMarksProjectedObjectOpenFailure()
    {
        var cause = new IOException("Projected package could not read its source.");
        var sourceStore = new PackagedFolderObjectStore();
        var handler = new FailingProjectedObjectFormatHandler(cause);
        var store = new ArchiveProjectionObjectStore
        (
            sourceStore,
            "source",
            sourceRootPrefix: null,
            new FixedFolderPolicyReader(new(handler.FormatName)),
            new Dictionary<string, IArchiveFormatHandler>(StringComparer.Ordinal)
            {
                [handler.FormatName] = handler,
            }
        );

        await foreach (var _ in store.GetFolderItemsAsync(folderPrefix: null))
        {
        }

        var exception = await Assert.ThrowsExactlyAsync<SourceObjectReadException>(() =>
            store.OpenReadAsync("packed.zip"));

        Assert.AreEqual("packed.zip", exception.ObjectKey);
        Assert.AreEqual(Path.Combine("source", "packed.zip"), exception.SourcePath);
        Assert.AreSame(cause, exception.InnerException);
    }

    [TestMethod]
    public void FindAllReturnsDistinctNestedFailures()
    {
        var first = new SourceObjectReadException
        (
            "source/first.bin",
            "first.bin",
            new IOException("First failed.")
        );
        var duplicate = new SourceObjectReadException
        (
            "another-display-name/first.bin",
            "first.bin",
            new IOException("A second observation of the first file failed.")
        );
        var second = new SourceObjectReadException
        (
            "source/second.bin",
            "second.bin",
            new IOException("Second failed.")
        );
        var aggregate = new InvalidOperationException
        (
            "Package failed.",
            new AggregateException(first, duplicate, second)
        );

        var failures = SourceObjectReadException.FindAll(aggregate);

        Assert.HasCount(2, failures);
        Assert.AreSame(first, failures[0]);
        Assert.AreSame(second, failures[1]);
        Assert.IsTrue(SourceObjectReadException.TryFind(aggregate, out var found));
        Assert.AreSame(first, found);
    }

    private static ArchiveProjectionObjectStore CreateProjectionStore(IReadOnlyObjectStore inner) =>
        new
        (
            inner,
            "source",
            sourceRootPrefix: null,
            new FixedFolderPolicyReader(FolderPolicy.Default),
            new Dictionary<string, IArchiveFormatHandler>(StringComparer.Ordinal)
        );

    private sealed class FixedFolderPolicyReader(FolderPolicy _policy) : IFolderPolicyReader
    {
        public Task<FolderPolicy> ReadPolicyAsync
        (
            string folderPath,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(_policy);
    }

    private sealed class ThrowingOpenReadObjectStore(Exception _exception) : IReadOnlyObjectStore
    {
        public Task EnsureReadyAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<ArchiveObjectContent> OpenReadAsync
        (
            string key,
            CancellationToken cancellationToken = default
        ) => Task.FromException<ArchiveObjectContent>(_exception);

        public Task<bool> ExistsAsync
        (
            string key,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(false);

        public async IAsyncEnumerable<ArchiveFolderItem> GetFolderItemsAsync
        (
            string? folderPrefix,
            bool recursive = false,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class FailingStreamObjectStore(Exception _exception) : IReadOnlyObjectStore
    {
        public Task EnsureReadyAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<ArchiveObjectContent> OpenReadAsync
        (
            string key,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(new ArchiveObjectContent(new FailingReadStream(_exception)));

        public Task<bool> ExistsAsync
        (
            string key,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(false);

        public async IAsyncEnumerable<ArchiveFolderItem> GetFolderItemsAsync
        (
            string? folderPrefix,
            bool recursive = false,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class PackagedFolderObjectStore : IReadOnlyObjectStore
    {
        public Task EnsureReadyAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<ArchiveObjectContent> OpenReadAsync
        (
            string key,
            CancellationToken cancellationToken = default
        ) => throw new InvalidOperationException("No physical source object should be opened.");

        public Task<bool> ExistsAsync
        (
            string key,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(string.Equals(
            ArchiveLayout.NormalizeObjectKey(key),
            $"packed/{FolderPolicyFileNames.Primary}",
            StringComparison.Ordinal));

        public async IAsyncEnumerable<ArchiveFolderItem> GetFolderItemsAsync
        (
            string? folderPrefix,
            bool recursive = false,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            await Task.CompletedTask;
            if (string.IsNullOrEmpty(ArchiveLayout.NormalizeObjectPrefix(folderPrefix)))
            {
                yield return ArchiveFolderItem.CreateFolder("packed", "packed");
            }
        }
    }

    private sealed class FailingProjectedObjectFormatHandler(Exception _exception) :
        IArchiveFormatHandler
    {
        public string FormatName => "failing";

        public bool ProjectsBesideSourceFolder => true;

        public bool CanRestoreArtifact(ArchiveProjectedObject artifact) => false;

        public async IAsyncEnumerable<ArchiveProjectedObject> ProjectBackupAsync
        (
            ArchiveProjectionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            await Task.CompletedTask;
            yield return new
            (
                "packed.zip",
                _ => Task.FromException<ArchiveObjectContent>(_exception),
                Projection: new
                (
                    request.LogicalPath ??
                        throw new InvalidOperationException("The projected logical path is required."),
                    FormatName,
                    1,
                    "projection-id",
                    "package"
                )
            );
        }

        public Task<ArchiveRestoreProjection> ProjectRestoreAsync
        (
            ArchiveRestoreRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();
    }

    private sealed class FailingReadStream(Exception _exception) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw _exception;

        public override int Read(Span<byte> buffer) => throw _exception;

        public override int ReadByte() => throw _exception;

        public override Task<int> ReadAsync
        (
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken
        ) => Task.FromException<int>(_exception);

        public override ValueTask<int> ReadAsync
        (
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        ) => ValueTask.FromException<int>(_exception);

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
