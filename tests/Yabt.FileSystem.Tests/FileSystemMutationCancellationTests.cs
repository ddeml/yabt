using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.FileSystem.Implementation;

namespace Yabt.FileSystem.Tests;

[TestClass]
public sealed class FileSystemMutationCancellationTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CanceledRestoreCommitRetainsStagingAndArchiveLockUntilWorkerFinishes(bool failMove)
    {
        using var fixture = new FileSystemLinkFixture();
        using var logger = new MutationGateLogger();
        var store = (IArchiveMutableObjectStore)new FileSystemBackupRootStoreResolver(logger)
            .ResolveSourceRoot(fixture.Archive);
        var destination = new FileSystemRestoreTarget(fixture.Archive, store, logger);
        var destinationPath = Path.Combine(fixture.Archive, "restored.txt");
        using var cancellation = new CancellationTokenSource();
        using var secondWriterCancellation = new CancellationTokenSource(TestTimeout);
        var operation = CommitUnderArchiveLockAsync(destination, store, cancellation.Token);
        Task<IArchiveMutationLock>? secondWriter = null;
        IArchiveMutationLock? secondLock = null;
        try
        {
            var stagingPath = await logger.MutationEntered.Task.WaitAsync(TestTimeout);
            Assert.IsTrue(File.Exists(stagingPath));
            if (failMove)
            {
                // Force the actual File.Move to fail after the blocked worker resumes.
                await File.WriteAllTextAsync(destinationPath, "existing destination");
            }

            await cancellation.CancelAsync();
            secondWriter = store.AcquireArchiveMutationLockAsync(secondWriterCancellation.Token);
            var progress = await Task.WhenAny(operation, logger.SecondBusyAttempt.Task).WaitAsync(TestTimeout);

            Assert.AreSame
            (
                logger.SecondBusyAttempt.Task,
                progress,
                "Cancellation completed the owning operation while its commit worker was still blocked."
            );
            Assert.IsFalse(operation.IsCompleted);
            Assert.IsFalse(secondWriter.IsCompleted);
            Assert.IsTrue(File.Exists(stagingPath), "Staging must survive cancellation until the worker finishes.");
            Assert.IsFalse(logger.LockReleased);

            logger.ReleaseMutation();
            var failure = await operation.WaitAsync(TestTimeout);
            if (failMove)
            {
                Assert.IsInstanceOfType<YabtFileSystemException>(failure);
                Assert.IsInstanceOfType<IOException>(failure.InnerException);
                Assert.AreEqual("existing destination", await File.ReadAllTextAsync(destinationPath));
            }
            else
            {
                Assert.IsNull(failure, "A started successful mutation must complete before cancellation is observed.");
                Assert.AreEqual("validated content", await File.ReadAllTextAsync(destinationPath));
            }

            Assert.IsFalse(File.Exists(stagingPath));
            Assert.IsFalse(logger.LockReleasedBeforeMutationRelease);
            secondLock = await secondWriter.WaitAsync(TestTimeout);
            Assert.IsTrue(logger.LockReleased);
        }
        finally
        {
            if (operation.IsCompleted && !logger.MutationReleased) { logger.AbortBlockedMutation(); }
            logger.ReleaseMutation();
            await operation.WaitAsync(TestTimeout);
            if (logger.MutationEntered.Task.IsCompleted) { await logger.MutationExited.Task.WaitAsync(TestTimeout); }
            if (secondWriter is not null && secondLock is null)
            {
                await secondWriterCancellation.CancelAsync();
                try { secondLock = await secondWriter.WaitAsync(TestTimeout); }
                catch (OperationCanceledException) { }
            }
            if (secondLock is not null) { await secondLock.DisposeAsync(); }
        }

        Assert.IsEmpty(Directory.GetFileSystemEntries(Path.Combine(fixture.Archive, ".yabt-tmp")));
    }

    [TestMethod]
    public async Task PreCanceledRestoreCommitLeavesTheStagedFileAndDestinationUntouched()
    {
        using var fixture = new FileSystemLinkFixture();
        var store = new FileSystemBackupRootStoreResolver(NullLogger<FileSystemObjectStore>.Instance)
            .ResolveSourceRoot(fixture.Archive);
        var destination = new FileSystemRestoreTarget(fixture.Archive, store, NullLogger.Instance);
        await using (var area = await destination.CreateStagingAreaAsync())
        {
            var token = area.CreateFileToken();
            await using (var stream = area.OpenWrite(token))
            {
                await stream.WriteAsync("validated content"u8.ToArray());
            }
            var stagingPath = Directory.GetFiles
            (
                Path.Combine(fixture.Archive, ".yabt-tmp"),
                "*.tmp",
                SearchOption.AllDirectories
            ).Single();
            var originalModified = File.GetLastWriteTimeUtc(stagingPath);
            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync();

            var exception = await Assert.ThrowsExactlyAsync<YabtFileSystemException>
            (
                () => destination.CommitStagedFileAsync
                (
                    "new-folder/restored.txt",
                    token,
                    new DateTimeOffset(2020, 1, 2, 3, 4, 6, TimeSpan.Zero),
                    cancellation.Token
                )
            );

            Assert.IsInstanceOfType<OperationCanceledException>(exception.InnerException);
            Assert.AreEqual("validated content", await File.ReadAllTextAsync(stagingPath));
            Assert.AreEqual(originalModified, File.GetLastWriteTimeUtc(stagingPath));
            Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Archive, "new-folder")));
        }
        Assert.IsEmpty(Directory.GetFileSystemEntries(Path.Combine(fixture.Archive, ".yabt-tmp")));
    }

    [TestMethod]
    public async Task CanceledStartedStagingCreationReturnsItsOwnedResourceForCleanup()
    {
        using var fixture = new FileSystemLinkFixture();
        using var logger = new MutationGateLogger("Creating restore staging directory");
        var store = new FileSystemBackupRootStoreResolver(NullLogger<FileSystemObjectStore>.Instance)
            .ResolveSourceRoot(fixture.Archive);
        var destination = new FileSystemRestoreTarget(fixture.Archive, store, logger);
        using var cancellation = new CancellationTokenSource();
        var creation = destination.CreateStagingAreaAsync(cancellation.Token);
        IObjectStoreStagingArea? staging = null;
        string? stagingRoot = null;
        try
        {
            stagingRoot = await logger.MutationEntered.Task.WaitAsync(TestTimeout);
            Assert.IsFalse(Directory.Exists(stagingRoot));
            await cancellation.CancelAsync();

            await Assert.ThrowsExactlyAsync<TimeoutException>
            (
                () => creation.WaitAsync(TimeSpan.FromMilliseconds(250))
            );

            logger.ReleaseMutation();
            staging = await creation.WaitAsync(TestTimeout);
            Assert.IsTrue(Directory.Exists(stagingRoot));
            var token = staging.CreateFileToken();
            await using var stream = staging.OpenWrite(token);
            await stream.WriteAsync("owned staging resource"u8.ToArray());
        }
        finally
        {
            if (creation.IsCompleted && !logger.MutationReleased) { logger.AbortBlockedMutation(); }
            logger.ReleaseMutation();
            if (staging is null)
            {
                try { staging = await creation.WaitAsync(TestTimeout); }
                catch (OperationCanceledException) { }
            }
            if (logger.MutationEntered.Task.IsCompleted) { await logger.MutationExited.Task.WaitAsync(TestTimeout); }
            if (staging is not null) { await staging.DisposeAsync(); }
        }

        Assert.IsFalse(Directory.Exists(stagingRoot));
        Assert.IsEmpty(Directory.GetFileSystemEntries(Path.Combine(fixture.Archive, ".yabt-tmp")));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CanceledConditionalPreparationRetainsResourcesUntilWorkerFinishes(bool replace)
    {
        var fixture = new FileSystemLinkFixture();
        var guardReleased = true;
        try
        {
            using var logger = new MutationGateLogger("Acquired conditional-mutation guard");
            var store = (IArchiveMutableObjectStore)new FileSystemBackupRootStoreResolver(logger)
                .ResolveSourceRoot(fixture.Archive);
            var originalPath = Path.Combine(fixture.Archive, "original.txt");
            await File.WriteAllTextAsync(originalPath, "original content");
            var temporaryRoot = Path.Combine(fixture.Archive, ".yabt-tmp");
            using var cancellation = new CancellationTokenSource();
            using var secondWriterCancellation = new CancellationTokenSource(TestTimeout);
            var operation = MutateConditionallyUnderArchiveLockAsync(store, replace, cancellation.Token);
            Task<IArchiveMutationLock>? secondWriter = null;
            IArchiveMutationLock? secondLock = null;
            try
            {
                var guardPath = await logger.MutationEntered.Task.WaitAsync(TestTimeout);
                var stagedFiles = Directory.GetFiles(temporaryRoot, "*.tmp");
                Assert.HasCount(replace ? 1 : 0, stagedFiles);
                await cancellation.CancelAsync();
                secondWriter = store.AcquireArchiveMutationLockAsync(secondWriterCancellation.Token);
                var progress = await Task.WhenAny(operation, logger.SecondBusyAttempt.Task).WaitAsync(TestTimeout);

                Assert.AreSame
                (
                    logger.SecondBusyAttempt.Task,
                    progress,
                    "Cancellation released resources while the conditional worker was still blocked."
                );
                Assert.IsFalse(operation.IsCompleted);
                Assert.IsFalse(secondWriter.IsCompleted);
                Assert.IsFalse(logger.LockReleased);
                Assert.IsTrue(stagedFiles.All(File.Exists), "Replacement staging must remain owned by the active worker.");
                Assert.ThrowsExactly<IOException>(() =>
                {
                    using var unexpectedGuard = File.Open(guardPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                });

                logger.ReleaseMutation();
                var failure = await operation.WaitAsync(TestTimeout);

                Assert.IsNotNull(failure);
                Assert.IsTrue(failure.IsCancellationException(), failure.ToString());
                Assert.AreEqual("original content", await File.ReadAllTextAsync(originalPath));
                Assert.IsEmpty(Directory.GetFiles(temporaryRoot, "*.tmp"));
                Assert.IsFalse(logger.LockReleasedBeforeMutationRelease);
                secondLock = await secondWriter.WaitAsync(TestTimeout);
                using var releasedGuard = File.Open(guardPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                Assert.AreEqual(0, releasedGuard.Length);
            }
            finally
            {
                var abortedWorker = operation.IsCompleted && !logger.MutationReleased;
                if (abortedWorker) { logger.AbortBlockedMutation(); }
                logger.ReleaseMutation();
                await operation.WaitAsync(TestTimeout);
                if (logger.MutationEntered.Task.IsCompleted) { await logger.MutationExited.Task.WaitAsync(TestTimeout); }
                if (abortedWorker && logger.MutationEntered.Task.IsCompletedSuccessfully)
                {
                    // Logger exit precedes the abandoned worker's using-guard unwind.
                    guardReleased = await WaitForConditionalGuardReleaseAsync(await logger.MutationEntered.Task);
                }
                if (secondWriter is not null && secondLock is null)
                {
                    await secondWriterCancellation.CancelAsync();
                    try { secondLock = await secondWriter.WaitAsync(TestTimeout); }
                    catch (OperationCanceledException) { }
                }
                if (secondLock is not null) { await secondLock.DisposeAsync(); }
            }

            CollectionAssert.AreEqual
            (
                new[] { Path.Combine(temporaryRoot, "conditional-mutation.lock") },
                Directory.GetFileSystemEntries(temporaryRoot)
            );
        }
        finally
        {
            if (guardReleased)
            {
                fixture.Dispose();
            }
            else
            {
                // Preserve the original regression assertion if a broken worker never releases its guard.
                Console.WriteLine($"Conditional guard remained busy; retained test fixture '{fixture.Root}'.");
            }
        }
    }

    [TestMethod]
    [DataRow("upload")]
    [DataRow("move-file")]
    [DataRow("move-folder")]
    [DataRow("replace")]
    [DataRow("delete")]
    [DataRow("archive-lock")]
    [DataRow("restore-directory")]
    [DataRow("restore-staging")]
    public async Task PreCanceledMutationDoesNotChangeTheArchive(string operation)
    {
        using var fixture = new FileSystemLinkFixture();
        var sourceDirectory = Directory.CreateDirectory(Path.Combine(fixture.Archive, "original-folder")).FullName;
        var originalFile = Path.Combine(sourceDirectory, "original.txt");
        await File.WriteAllTextAsync(originalFile, "original content");
        var before = Directory.GetFileSystemEntries(fixture.Archive, "*", SearchOption.AllDirectories);
        var store = (IArchiveMutableObjectStore)new FileSystemBackupRootStoreResolver
        (
            NullLogger<FileSystemObjectStore>.Instance
        ).ResolveSourceRoot(fixture.Archive);
        var destination = new FileSystemRestoreTarget(fixture.Archive, store, NullLogger.Instance);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await using var content = new MemoryStream("new content"u8.ToArray());
        var metadata = new Dictionary<string, string>();
        var contentHash = ArchiveHash.Compute("original content"u8);

        var failure = await Assert.ThrowsAsync<Exception>(async () =>
        {
            switch (operation)
            {
                case "upload":
                    await store.UploadAsync("new/file.txt", content, "text/plain", metadata, cancellation.Token);
                    break;
                case "move-file":
                    await store.MoveAsync("original-folder/original.txt", "new/file.txt", cancellation.Token);
                    break;
                case "move-folder":
                    await store.MoveFolderAsync("original-folder", "new/folder", cancellation.Token);
                    break;
                case "replace":
                    await store.TryReplaceIfContentHashMatchesAsync
                    (
                        "original-folder/original.txt", contentHash, content, "text/plain", metadata, cancellation.Token
                    );
                    break;
                case "delete":
                    await store.TryDeleteIfContentHashMatchesAsync
                    (
                        "original-folder/original.txt", contentHash, cancellation.Token
                    );
                    break;
                case "archive-lock":
                    await using (await store.AcquireArchiveMutationLockAsync(cancellation.Token)) { }
                    break;
                case "restore-directory":
                    await destination.CreateDirectoryAsync("new/folder", cancellation.Token);
                    break;
                case "restore-staging":
                    await using (await destination.CreateStagingAreaAsync(cancellation.Token)) { }
                    break;
                default:
                    Assert.Fail($"Unknown test operation '{operation}'.");
                    break;
            }
        });

        Assert.IsTrue(failure.IsCancellationException(), failure.ToString());
        CollectionAssert.AreEquivalent
        (
            before,
            Directory.GetFileSystemEntries(fixture.Archive, "*", SearchOption.AllDirectories)
        );
        Assert.AreEqual("original content", await File.ReadAllTextAsync(originalFile));
        Assert.AreEqual(0, content.Position);
    }

    [TestMethod]
    public async Task PreCanceledInitializationDoesNotCreateTheRoot()
    {
        using var fixture = new FileSystemLinkFixture();
        var rootPath = Path.Combine(fixture.Root, "new-store");
        var store = new FileSystemBackupRootStoreResolver(NullLogger<FileSystemObjectStore>.Instance)
            .ResolveSourceRoot(rootPath);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var failure = await Assert.ThrowsAsync<Exception>(() => store.InitializeAsync(cancellation.Token));

        Assert.IsTrue(failure.IsCancellationException());
        Assert.IsFalse(Directory.Exists(rootPath));
    }

    private static async Task<bool> WaitForConditionalGuardReleaseAsync(string guardPath)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TestTimeout)
        {
            try
            {
                using var guard = File.Open(guardPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return true;
            }
            catch (FileNotFoundException)
            {
                return true;
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10));
            }
        }
        return false;
    }

    private static async Task<Exception?> CommitUnderArchiveLockAsync
    (
        FileSystemRestoreTarget destination,
        IArchiveMutableObjectStore store,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await using var mutationLock = await store.AcquireArchiveMutationLockAsync(cancellationToken);
            await using var staging = await destination.CreateStagingAreaAsync(cancellationToken);
            var token = staging.CreateFileToken();
            await using (var stream = staging.OpenWrite(token))
            {
                await stream.WriteAsync("validated content"u8.ToArray(), cancellationToken);
            }
            await destination.CommitStagedFileAsync("restored.txt", token, null, cancellationToken);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static async Task<Exception?> MutateConditionallyUnderArchiveLockAsync
    (
        IArchiveMutableObjectStore store,
        bool replace,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await using var mutationLock = await store.AcquireArchiveMutationLockAsync(cancellationToken);
            var hash = ArchiveHash.Compute("original content"u8);
            if (replace)
            {
                await using var replacement = new MemoryStream("replacement content"u8.ToArray());
                await store.TryReplaceIfContentHashMatchesAsync
                (
                    "original.txt", hash, replacement, "text/plain", new Dictionary<string, string>(), cancellationToken
                );
            }
            else
            {
                await store.TryDeleteIfContentHashMatchesAsync("original.txt", hash, cancellationToken);
            }
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private sealed class MutationGateLogger
    (
        string _action = "Moving validated restore staging file into place"
    ) : ILogger<FileSystemObjectStore>, IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private int _busyAttempts;
        private int _lockReleased;
        private int _lockReleasedBeforeMutationRelease;
        private int _abortMutation;

        public TaskCompletionSource<string> MutationEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource MutationExited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondBusyAttempt { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool MutationReleased => _release.IsSet;
        public bool LockReleased => Volatile.Read(ref _lockReleased) != 0;
        public bool LockReleasedBeforeMutationRelease => Volatile.Read(ref _lockReleasedBeforeMutationRelease) != 0;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>
        (
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            if (state is not IEnumerable<KeyValuePair<string, object?>> properties) { return; }
            var action = properties.FirstOrDefault(property => property.Key == "Action").Value as string;
            if (action == _action)
            {
                var path = (string)properties.First(property => property.Key == "Path").Value!;
                MutationEntered.TrySetResult(path);
                try
                {
                    if (!_release.Wait(TestTimeout)) { throw new TimeoutException("Mutation gate was not released."); }
                    if (Volatile.Read(ref _abortMutation) != 0)
                    {
                        // A failing baseline must not leave its abandoned worker racing fixture cleanup.
                        throw new IOException("The test stopped an abandoned worker before it could mutate the fixture.");
                    }
                }
                finally
                {
                    MutationExited.TrySetResult();
                }
            }
            else if (action == "Found busy archive-mutation lock file")
            {
                if (Interlocked.Increment(ref _busyAttempts) >= 2) { SecondBusyAttempt.TrySetResult(); }
            }
            else if (action == "Releasing and deleting archive-mutation lock file")
            {
                Interlocked.Exchange(ref _lockReleased, 1);
                if (!_release.IsSet) { Interlocked.Exchange(ref _lockReleasedBeforeMutationRelease, 1); }
            }
        }

        public void ReleaseMutation() => _release.Set();

        public void AbortBlockedMutation() => Interlocked.Exchange(ref _abortMutation, 1);

        public void Dispose() => _release.Dispose();
    }
}
