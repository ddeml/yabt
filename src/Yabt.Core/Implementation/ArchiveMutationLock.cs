using System.Runtime.ExceptionServices;
using System.Text.Json;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;

namespace Yabt.Core.Implementation;

internal sealed class ArchiveMutationLock : IArchiveMutationLock
{
    private const int SchemaVersion = 1;
    private const string DocumentType = "yabt-archive-mutation-lock";
    private const string ContentType = "application/json";
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RenewalInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RenewalTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan LeaseSafetyMargin = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(250);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly IArchiveMutableObjectStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _renewalCancellationSource = new();
    private readonly CancellationTokenSource _lockLostCancellationSource = new();
    private readonly Lock _gate = new();
    private readonly string _ownerId;
    private readonly DateTimeOffset _acquiredUtc;
    private readonly Task _renewalTask;
    private string _currentContentHash;
    private Exception? _lockLossException;
    private long _generation;
    private long _lastConfirmedWriteTimestamp;
    private int _disposeStarted;

    private ArchiveMutationLock
    (
        IArchiveMutableObjectStore store,
        TimeProvider timeProvider,
        string ownerId,
        DateTimeOffset acquiredUtc,
        string currentContentHash,
        long lastConfirmedWriteTimestamp
    )
    {
        _store = store;
        _timeProvider = timeProvider;
        _ownerId = ownerId;
        _acquiredUtc = acquiredUtc;
        _currentContentHash = currentContentHash;
        _lastConfirmedWriteTimestamp = lastConfirmedWriteTimestamp;
        _renewalTask = RenewAsync();
    }

    public CancellationToken LockLostToken => _lockLostCancellationSource.Token;

    public static async Task<IArchiveMutationLock> AcquireAsync
    (
        IArchiveMutableObjectStore store,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(store);

        var timeProvider = TimeProvider.System;
        var ownerId = Guid.NewGuid().ToString("N");
        var acquiredUtc = timeProvider.GetUtcNow();
        string? observedContentHash = null;
        var observedTimestamp = timeProvider.GetTimestamp();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var now = timeProvider.GetUtcNow();
            var newLockContent = CreateLockContent(
                ownerId,
                acquiredUtc,
                now + LeaseDuration,
                generation: 0);
            var writeAttemptTimestamp = timeProvider.GetTimestamp();

            if (await TryCreateAsync(store, newLockContent, cancellationToken))
            {
                return new ArchiveMutationLock
                (
                    store,
                    timeProvider,
                    ownerId,
                    acquiredUtc,
                    ArchiveHash.Compute(newLockContent),
                    writeAttemptTimestamp
                );
            }

            var existingLock = await TryReadAsync(store, cancellationToken);
            if (existingLock is null)
            {
                continue;
            }

            if (!string.Equals(
                    observedContentHash,
                    existingLock.ContentHash,
                    StringComparison.Ordinal))
            {
                observedContentHash = existingLock.ContentHash;
                observedTimestamp = timeProvider.GetTimestamp();
            }

            // Do not decide that a remote lock is stale from its UTC timestamp because clocks on
            // two machines may differ. Every renewal changes Generation and therefore the bytes;
            // takeover is safe only after the same bytes remain visible for one complete lease.
            if (timeProvider.GetElapsedTime(observedTimestamp) >= LeaseDuration)
            {
                var takeoverAttemptTimestamp = timeProvider.GetTimestamp();
                await using var replacementContent = new MemoryStream(
                    newLockContent,
                    writable: false);
                var replaced = await store.TryReplaceIfContentHashMatchesAsync
                (
                    ArchiveInternalObjectKeys.MutationLock,
                    existingLock.ContentHash,
                    replacementContent,
                    ContentType,
                    new Dictionary<string, string>(StringComparer.Ordinal),
                    cancellationToken
                );
                if (replaced)
                {
                    return new ArchiveMutationLock
                    (
                        store,
                        timeProvider,
                        ownerId,
                        acquiredUtc,
                        ArchiveHash.Compute(newLockContent),
                        takeoverAttemptTimestamp
                    );
                }

                continue;
            }

            await Task.Delay(RetryInterval, timeProvider, cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        await _renewalCancellationSource.CancelAsync();
        await _renewalTask;

        Exception? releaseException = null;
        try
        {
            var currentContentHash = GetCurrentContentHash();
            var released = await _store.TryDeleteIfContentHashMatchesAsync
            (
                ArchiveInternalObjectKeys.MutationLock,
                currentContentHash
            );
            if (!released)
            {
                releaseException = new YabtCoreException(
                    $"Archive mutation lock '{ArchiveInternalObjectKeys.MutationLock}' " +
                    "was changed or removed before it could be released.");
                SignalLockLost(releaseException);
            }
        }
        catch (Exception ex)
        {
            releaseException = new YabtCoreException(
                $"Release failed for archive mutation lock '{ArchiveInternalObjectKeys.MutationLock}'.",
                ex);
            SignalLockLost(releaseException);
        }
        finally
        {
            _renewalCancellationSource.Dispose();
            _lockLostCancellationSource.Dispose();
        }

        if (releaseException is not null)
        {
            ExceptionDispatchInfo.Capture(releaseException).Throw();
        }

        var lockLossException = GetLockLossException();
        if (lockLossException is not null)
        {
            throw new YabtCoreException(
                $"Ownership was lost for archive mutation lock '{ArchiveInternalObjectKeys.MutationLock}'.",
                lockLossException);
        }
    }

    private async Task RenewAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(
                    RenewalInterval,
                    _timeProvider,
                    _renewalCancellationSource.Token);

                var lockContent = CreateLockContent
                (
                    _ownerId,
                    _acquiredUtc,
                    _timeProvider.GetUtcNow() + LeaseDuration,
                    Interlocked.Increment(ref _generation)
                );
                var renewalAttemptTimestamp = _timeProvider.GetTimestamp();
                var elapsedSinceConfirmedWrite = _timeProvider.GetElapsedTime(
                    _lastConfirmedWriteTimestamp,
                    renewalAttemptTimestamp);
                var remainingSafeLease = LeaseDuration -
                    LeaseSafetyMargin -
                    elapsedSinceConfirmedWrite;
                if (remainingSafeLease <= TimeSpan.Zero)
                {
                    SignalLockLost(new YabtCoreException(
                        $"Renewal deadline expired for archive mutation lock " +
                        $"'{ArchiveInternalObjectKeys.MutationLock}'."));
                    return;
                }

                var renewalTimeout = remainingSafeLease < RenewalTimeout ?
                    remainingSafeLease :
                    RenewalTimeout;
                var replaced = await TryRenewBeforeDeadlineAsync(
                    lockContent,
                    renewalTimeout);
                if (!replaced)
                {
                    SignalLockLost(new YabtCoreException(
                        $"Archive mutation lock '{ArchiveInternalObjectKeys.MutationLock}' " +
                        "was changed or removed before it could be renewed."));
                    return;
                }

                SetCurrentContentHash(ArchiveHash.Compute(lockContent));
                _lastConfirmedWriteTimestamp = renewalAttemptTimestamp;
            }
        }
        catch (Exception ex)
        {
            if (_renewalCancellationSource.IsCancellationRequested &&
                ex.IsCancellationException())
            {
                return;
            }

            SignalLockLost(new YabtCoreException(
                $"Renewal failed for archive mutation lock '{ArchiveInternalObjectKeys.MutationLock}'.",
                ex));
        }
    }

    private async Task<bool> TryRenewBeforeDeadlineAsync
    (
        byte[] lockContent,
        TimeSpan renewalTimeout
    )
    {
        var replacementContent = new MemoryStream(lockContent, writable: false);
        var renewalAttemptCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(
            _renewalCancellationSource.Token);
        var expectedContentHash = GetCurrentContentHash();
        var replacementTask = _store.TryReplaceIfContentHashMatchesAsync
        (
            ArchiveInternalObjectKeys.MutationLock,
            expectedContentHash,
            replacementContent,
            ContentType,
            new Dictionary<string, string>(StringComparer.Ordinal),
            renewalAttemptCancellationSource.Token
        );
        var timeoutTask = Task.Delay(renewalTimeout, _timeProvider);
        await Task.WhenAny(replacementTask, timeoutTask);
        if (!replacementTask.IsCompleted)
        {
            await renewalAttemptCancellationSource.CancelAsync();
            SignalLockLost(new YabtCoreException(
                $"Renewal timed out for archive mutation lock " +
                $"'{ArchiveInternalObjectKeys.MutationLock}'."));
            _ = ObserveLateRenewalAsync(
                replacementTask,
                replacementContent,
                renewalAttemptCancellationSource);
            return false;
        }

        try
        {
            return await replacementTask;
        }
        finally
        {
            await replacementContent.DisposeAsync();
            renewalAttemptCancellationSource.Dispose();
        }
    }

    private async Task ObserveLateRenewalAsync
    (
        Task<bool> replacementTask,
        Stream replacementContent,
        CancellationTokenSource renewalAttemptCancellationSource
    )
    {
        try
        {
            await replacementTask;
        }
        catch (Exception ex)
        {
            SignalLockLost(new YabtCoreException(
                $"Late renewal failed for archive mutation lock " +
                $"'{ArchiveInternalObjectKeys.MutationLock}'.",
                ex));
        }
        finally
        {
            await replacementContent.DisposeAsync();
            renewalAttemptCancellationSource.Dispose();
        }
    }

    private static async Task<bool> TryCreateAsync
    (
        IArchiveMutableObjectStore store,
        byte[] content,
        CancellationToken cancellationToken
    )
    {
        Exception? uploadException = null;
        try
        {
            await UploadLockAsync(store, content, cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            uploadException = ex;
        }

        if (await store.ExistsAsync(ArchiveInternalObjectKeys.MutationLock, cancellationToken))
        {
            return false;
        }

        // The previous holder may have released the lock between our failed create and existence
        // probe. Retry once so an ordinary handoff does not surface the stale create conflict.
        try
        {
            await UploadLockAsync(store, content, cancellationToken);
            return true;
        }
        catch (Exception retryException)
        {
            if (await store.ExistsAsync(ArchiveInternalObjectKeys.MutationLock, cancellationToken))
            {
                return false;
            }

            throw new YabtCoreException(
                $"Create failed for archive mutation lock '{ArchiveInternalObjectKeys.MutationLock}'.",
                new AggregateException(uploadException, retryException));
        }
    }

    private static async Task UploadLockAsync
    (
        IObjectStore store,
        byte[] content,
        CancellationToken cancellationToken
    )
    {
        await using var lockContent = new MemoryStream(content, writable: false);
        await store.UploadAsync
        (
            ArchiveInternalObjectKeys.MutationLock,
            lockContent,
            ContentType,
            new Dictionary<string, string>(StringComparer.Ordinal),
            cancellationToken
        );
    }

    private static async Task<ExistingLock?> TryReadAsync
    (
        IArchiveMutableObjectStore store,
        CancellationToken cancellationToken
    )
    {
        ArchiveObjectContent? objectContent = null;
        Exception? firstReadException = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                objectContent = await store.OpenReadAsync(
                    ArchiveInternalObjectKeys.MutationLock,
                    cancellationToken);
                break;
            }
            catch (Exception ex)
            {
                if (!await store.ExistsAsync(
                        ArchiveInternalObjectKeys.MutationLock,
                        cancellationToken))
                {
                    return null;
                }

                firstReadException ??= ex;
                if (attempt == 1)
                {
                    throw new YabtCoreException(
                        $"Read failed for archive mutation lock " +
                        $"'{ArchiveInternalObjectKeys.MutationLock}'.",
                        new AggregateException(firstReadException, ex));
                }
            }
        }

        if (objectContent is null)
        {
            throw new YabtCoreException(
                $"Read failed for archive mutation lock '{ArchiveInternalObjectKeys.MutationLock}'.");
        }

        await using (objectContent)
        {
            using var content = new MemoryStream();
            await objectContent.Content.CopyToAsync(content, cancellationToken);
            var bytes = content.ToArray();
            LockDocument? document;
            try
            {
                document = JsonSerializer.Deserialize<LockDocument>(bytes, JsonOptions);
            }
            catch (Exception ex)
            {
                throw new YabtCoreException(
                    $"Archive mutation lock '{ArchiveInternalObjectKeys.MutationLock}' " +
                    "contains invalid JSON.",
                    ex);
            }
            if (document is null ||
                !string.Equals(document.DocumentType, DocumentType, StringComparison.Ordinal) ||
                document.SchemaVersion != SchemaVersion ||
                string.IsNullOrWhiteSpace(document.OwnerId) ||
                document.Generation < 0 ||
                document.ExpiresUtc <= document.AcquiredUtc)
            {
                throw new YabtCoreException(
                    $"Archive mutation lock '{ArchiveInternalObjectKeys.MutationLock}' is invalid.");
            }

            return new(document, ArchiveHash.Compute(bytes));
        }
    }

    private static byte[] CreateLockContent
    (
        string ownerId,
        DateTimeOffset acquiredUtc,
        DateTimeOffset expiresUtc,
        long generation
    )
    {
        var document = new LockDocument
        (
            DocumentType,
            SchemaVersion,
            ownerId,
            acquiredUtc,
            expiresUtc,
            generation
        );
        return JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
    }

    private string GetCurrentContentHash()
    {
        lock (_gate)
        {
            return _currentContentHash;
        }
    }

    private void SetCurrentContentHash(string contentHash)
    {
        lock (_gate)
        {
            _currentContentHash = contentHash;
        }
    }

    private Exception? GetLockLossException()
    {
        lock (_gate)
        {
            return _lockLossException;
        }
    }

    private void SignalLockLost(Exception exception)
    {
        lock (_gate)
        {
            _lockLossException ??= exception;
        }

        if (!_lockLostCancellationSource.IsCancellationRequested)
        {
            _lockLostCancellationSource.Cancel();
        }
    }

    private sealed record LockDocument
    (
        string DocumentType,
        int SchemaVersion,
        string OwnerId,
        DateTimeOffset AcquiredUtc,
        DateTimeOffset ExpiresUtc,
        long Generation
    );

    private sealed record ExistingLock
    (
        LockDocument Document,
        string ContentHash
    );
}
