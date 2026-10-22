using Yabt.Core.Abstractions;
using Yabt.Core.Models;

namespace Yabt.Core.Tests;

[TestClass]
public sealed class ArchiveMutationLockDocumentTests
{
    [TestMethod]
    public async Task DefaultLimitStopsAnOversizedRemoteLockWithoutMutatingIt()
    {
        var store = new ExistingLockStore(1024 * 1024);

        var error = await Assert.ThrowsExactlyAsync<YabtCoreException>
        (
            () => ((IArchiveMutationLockProvider)store).AcquireArchiveMutationLockAsync()
        );

        StringAssert.Contains(error.ToString(), ArchiveInternalObjectKeys.MutationLock);
        StringAssert.Contains(error.ToString(), "65536 bytes");
        Assert.AreEqual(65537, store.Content.BytesRead);
        Assert.IsTrue(store.Content.Disposed);
        Assert.AreEqual(0, store.Mutations);
    }

    [TestMethod]
    public async Task TrustedRuntimeLimitCanRaiseTheRemoteLockBound()
    {
        var store = new ExistingLockStore(100_000);

        var error = await Assert.ThrowsExactlyAsync<YabtCoreException>
        (
            () => ((IArchiveMutationLockProvider)store).AcquireArchiveMutationLockAsync
            (
                maximumDocumentBytes: 128 * 1024
            )
        );

        Assert.IsFalse(error.ToString().Contains("maximum size", StringComparison.Ordinal));
        Assert.AreEqual(100_000, store.Content.BytesRead);
        Assert.AreEqual(0, store.Mutations);
    }

    [TestMethod]
    public async Task InvalidLimitFailsBeforeAnyStoreAccess()
    {
        var store = new ExistingLockStore(0);

        await Assert.ThrowsExactlyAsync<YabtCoreException>
        (
            () => ((IArchiveMutationLockProvider)store).AcquireArchiveMutationLockAsync
            (
                maximumDocumentBytes: 0
            )
        );

        Assert.AreEqual(0, store.CreateAttempts);
        Assert.AreEqual(0, store.Content.BytesRead);
    }

    [TestMethod]
    public async Task CannotPublishAnOwnLockLargerThanTheConfiguredReadLimit()
    {
        var store = new ExistingLockStore(0);

        var error = await Assert.ThrowsExactlyAsync<YabtCoreException>
        (
            () => ((IArchiveMutationLockProvider)store).AcquireArchiveMutationLockAsync
            (
                maximumDocumentBytes: 16
            )
        );

        StringAssert.Contains(error.ToString(), "16 bytes");
        Assert.AreEqual(0, store.CreateAttempts);
    }

    private sealed class ExistingLockStore(int contentLength) : IArchiveMutableObjectStore
    {
        public ExistingLockStream Content { get; } = new(contentLength);
        public int CreateAttempts { get; private set; }
        public int Mutations { get; private set; }

        public Task EnsureReadyAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<ArchiveObjectContent> OpenReadAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ArchiveObjectContent(Content));
        public IAsyncEnumerable<ArchiveFolderItem> GetFolderItemsAsync
        (
            string? folderPrefix,
            bool recursive = false,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task UploadAsync
        (
            string key,
            Stream content,
            string contentType,
            IReadOnlyDictionary<string, string> metadata,
            CancellationToken cancellationToken = default
        )
        {
            CreateAttempts++;
            throw new IOException("The lock already exists.");
        }

        public Task MoveAsync(string source, string destination, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task MoveFolderAsync(string sourcePrefix, string destinationPrefix, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<bool> IsIndependentMaterializationAsync(string key, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<bool> TryReplaceIfContentHashMatchesAsync
        (
            string key,
            string expectedContentHash,
            Stream replacementContent,
            string contentType,
            IReadOnlyDictionary<string, string> metadata,
            CancellationToken cancellationToken = default
        )
        {
            Mutations++;
            throw new NotSupportedException();
        }

        public Task<bool> TryDeleteIfContentHashMatchesAsync
        (
            string key,
            string expectedContentHash,
            CancellationToken cancellationToken = default
        )
        {
            Mutations++;
            throw new NotSupportedException();
        }
    }

    private sealed class ExistingLockStream(int length) : Stream
    {
        public int BytesRead { get; private set; }
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 1;
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = Math.Min(buffer.Length, length - BytesRead);
            buffer.Span[..count].Fill((byte)' ');
            BytesRead += count;
            return ValueTask.FromResult(count);
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
