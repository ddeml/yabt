using System.Buffers;
using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.Metadata;

namespace Yabt.Sync.Implementation;

internal sealed class HistoryDeduplicator
(
    ILogger<HistoryDeduplicator> _logger,
    IBackupRootLocator _backupRootLocator,
    IEnumerable<IBackupRootStoreResolver> storeResolvers,
    IHistoryManifestSerializer _historyManifestSerializer,
    IHistoryReferenceSerializer _historyReferenceSerializer
) : IHistoryDeduplicator
{
    private const int BufferSize = 81_920;

    private static readonly byte[] InvalidationMarkerContent = Encoding.UTF8.GetBytes
    (
        "{\"documentType\":\"yabt.historyManifestInvalidation\",\"schemaVersion\":1}\n"
    );

    private static readonly IReadOnlyDictionary<string, string> EmptyMetadata =
        new Dictionary<string, string>(StringComparer.Ordinal).ToFrozenDictionary(StringComparer.Ordinal);

    private readonly FrozenDictionary<string, IBackupRootStoreResolver> _storeResolvers =
        storeResolvers.ToFrozenDictionary(resolver => resolver.StoreKind, StringComparer.Ordinal);

    public async Task<HistoryDeduplicationResult> DeduplicateAsync
    (
        HistoryDeduplicationRequest request,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(DeduplicateAsync));

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ArchiveRoot))
        {
            throw new YabtSyncException("History deduplication requires an archive root path.");
        }

        try
        {
            var archiveRootPath = Path.GetFullPath(request.ArchiveRoot);
            var rootLocation = await _backupRootLocator.LocateRootAsync(
                archiveRootPath,
                cancellationToken);
            var descriptor = rootLocation.Descriptor;
            ValidateLayout(descriptor.Layout);
            var historyPrefix = ArchiveLayout.NormalizeObjectKey(descriptor.Layout.HistPrefix);

            var tinyFileMaximumBytes = ArchiveHistoryDeduplication.GetEffectiveTinyFileMaximumBytes(
                descriptor.HistoryDeduplicationTinyFileMaximumBytes);
            if (!ArchiveHistoryDeduplication.IsSupportedTinyFileMaximumBytes(tinyFileMaximumBytes))
            {
                throw new YabtSyncException(
                    "History deduplication tiny-file maximum bytes must not be negative.");
            }

            var targetStore = ResolveTargetStore(
                descriptor,
                rootLocation.RootPath,
                request.TargetStoreId);
            if (targetStore is not IArchiveMutableObjectStore mutableTargetStore)
            {
                throw new YabtSyncException(
                    "The selected target store does not support guarded history compaction mutations.");
            }

            if (!request.DryRun)
            {
                await targetStore.EnsureReadyAsync(cancellationToken);
            }

            await using IArchiveMutationLock? mutationLock = request.DryRun ?
                null :
                await mutableTargetStore.AcquireArchiveMutationLockAsync(cancellationToken);
            using var operationCancellation = mutationLock is null ?
                null :
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    mutationLock.LockLostToken);
            var operationCancellationToken = operationCancellation?.Token ?? cancellationToken;

            var manifestKey = descriptor.Layout.ToHistoryObjectKey(ArchiveHistoryFileNames.Manifest);
            var invalidationMarkerKey = descriptor.Layout.ToHistoryObjectKey(
                ArchiveHistoryManifest.InvalidationMarkerFileName);
            var markerExists = await targetStore.ExistsAsync(
                invalidationMarkerKey,
                operationCancellationToken);
            var existingManifest = await ReadExistingManifestAsync(
                targetStore,
                manifestKey,
                trustManifest: !markerExists,
                operationCancellationToken);
            var scan = await ScanHistoryAsync(
                targetStore,
                descriptor.Layout,
                operationCancellationToken);
            EnsureReferencesHaveMaterializedBacking(scan.Occurrences);

            var plans = await CreateDeduplicationPlansAsync(
                targetStore,
                descriptor.Layout,
                scan.Occurrences,
                scan.StoredRelativePaths,
                tinyFileMaximumBytes,
                operationCancellationToken);

            var nextEntries = scan.Occurrences.ToDictionary(
                occurrence => occurrence.Entry.RelativePath,
                occurrence => occurrence.Entry,
                StringComparer.Ordinal);
            foreach (var plan in plans)
            {
                nextEntries[plan.Original.Entry.RelativePath] = plan.Reference.Entry;
            }

            var nextManifest = _historyManifestSerializer.Create(nextEntries.Values);
            var duplicateGroupCount = CountDuplicateGroups(scan.Occurrences);
            var tinyObjectCount = scan.Occurrences.Count(
                occurrence => occurrence.IsDeduplicationEligible &&
                    occurrence.Entry.ContentLength <= tinyFileMaximumBytes);
            var bytesSaved = plans.Sum(plan => plan.BytesSaved);

            if (request.DryRun)
            {
                return CreateResult(
                    dryRun: true,
                    scan,
                    duplicateGroupCount,
                    plans.Count,
                    tinyObjectCount,
                    bytesSaved);
            }

            var manifestNeedsWrite = markerExists ||
                existingManifest.Manifest is null ||
                !string.Equals(
                    existingManifest.Manifest.ManifestHash,
                    nextManifest.ManifestHash,
                    StringComparison.Ordinal) ||
                plans.Count != 0 ||
                scan.OrphanReferences.Count != 0;
            if (manifestNeedsWrite)
            {
                await ApplyPlansAsync(
                    mutableTargetStore,
                    descriptor.Layout,
                    manifestKey,
                    invalidationMarkerKey,
                    markerExists,
                    existingManifest,
                    nextManifest,
                    plans,
                    scan.OrphanReferences,
                    operationCancellationToken);
            }

            return CreateResult(
                dryRun: false,
                scan,
                duplicateGroupCount,
                plans.Count,
                tinyObjectCount,
                bytesSaved);
        }
        catch (Exception ex)
        {
            throw new YabtSyncException(
                $"History deduplication failed for archive root '{request.ArchiveRoot}'.",
                ex);
        }
    }

    private IObjectStore ResolveTargetStore
    (
        BackupRootDescriptor descriptor,
        string descriptorRootPath,
        string? requestedStoreId
    )
    {
        _logger.LogTrace(nameof(ResolveTargetStore));

        var targetStoreConfiguration = GetTargetStoreConfiguration(descriptor, requestedStoreId);
        if (!_storeResolvers.TryGetValue(targetStoreConfiguration.Kind, out var resolver))
        {
            throw new YabtSyncException(
                $"No object store resolver is registered for store kind '{targetStoreConfiguration.Kind}'.");
        }

        return resolver.ResolveStore(targetStoreConfiguration, descriptorRootPath);
    }

    private BackupRootStore GetTargetStoreConfiguration
    (
        BackupRootDescriptor descriptor,
        string? requestedStoreId
    )
    {
        _logger.LogTrace(nameof(GetTargetStoreConfiguration));

        var stores = descriptor.Stores?.ToArray() ?? [];
        if (stores.Length == 0)
        {
            throw new YabtSyncException("Backup root descriptor does not define any target stores.");
        }

        var effectiveStoreId = string.IsNullOrWhiteSpace(requestedStoreId) ?
            descriptor.DefaultStoreId :
            requestedStoreId;
        if (!string.IsNullOrWhiteSpace(effectiveStoreId))
        {
            var selectedStore = stores.FirstOrDefault(store => string.Equals(
                store.Id,
                effectiveStoreId,
                StringComparison.OrdinalIgnoreCase));
            return selectedStore ??
                throw new YabtSyncException(
                    $"Backup root descriptor does not define target store '{effectiveStoreId}'.");
        }

        if (stores.Length > 1)
        {
            _logger.LogMultipleTargetStoresWithoutSelection(descriptor.ArchiveId, stores[0].Id);
        }

        return stores[0];
    }

    private async Task<HistoryScan> ScanHistoryAsync
    (
        IObjectStore targetStore,
        ArchiveLayout layout,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(ScanHistoryAsync));

        var historyPrefix = ArchiveLayout.NormalizeObjectKey(layout.HistPrefix);
        var listedObjects = new List<ArchiveObjectInfo>();
        var folderItems = targetStore.GetFolderItemsAsync(
            historyPrefix,
            recursive: true,
            cancellationToken);
        await foreach (var item in folderItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Object is not null)
            {
                listedObjects.Add(item.Object);
            }
        }

        listedObjects.Sort(static (left, right) =>
            string.Compare(left.Key, right.Key, StringComparison.Ordinal));

        var actuals = new Dictionary<string, HistoryOccurrence>(StringComparer.Ordinal);
        var references = new Dictionary<string, List<HistoryOccurrence>>(StringComparer.Ordinal);
        var storedRelativePaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var archiveObject in listedObjects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = ArchiveLayout.RemovePrefix(archiveObject.Key, historyPrefix);
            if (IsHistoryControlPath(relativePath))
            {
                continue;
            }

            storedRelativePaths.Add(relativePath);
            var referenceOccurrence = await TryReadReferenceAsync(
                targetStore,
                archiveObject,
                relativePath,
                cancellationToken);
            if (referenceOccurrence is not null)
            {
                if (!references.TryGetValue(
                        referenceOccurrence.Entry.RelativePath,
                        out var samePathReferences))
                {
                    samePathReferences = [];
                    references.Add(referenceOccurrence.Entry.RelativePath, samePathReferences);
                }

                samePathReferences.Add(referenceOccurrence);
                continue;
            }

            var occurrence = await ReadMaterializedOccurrenceAsync(
                targetStore,
                archiveObject,
                relativePath,
                cancellationToken);
            if (!actuals.TryAdd(relativePath, occurrence))
            {
                throw new YabtSyncException(
                    $"History contains more than one materialized occurrence for '{relativePath}'.");
            }
        }

        var occurrences = new List<HistoryOccurrence>(actuals.Count + references.Count);
        occurrences.AddRange(actuals.Values);
        var orphanReferences = new List<HistoryOccurrence>();
        foreach (var pair in references.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            pair.Value.Sort(static (left, right) => string.Compare(
                left.Entry.StoredRelativePath,
                right.Entry.StoredRelativePath,
                StringComparison.Ordinal));

            if (actuals.ContainsKey(pair.Key))
            {
                foreach (var referenceOccurrence in pair.Value)
                {
                    EnsureActualAndReferenceDescribeSameOccurrence(
                        actuals[pair.Key],
                        referenceOccurrence);
                }

                orphanReferences.AddRange(pair.Value);
                continue;
            }

            var selectedReference = pair.Value[0];
            for (var index = 1; index < pair.Value.Count; index++)
            {
                EnsureReferencesDescribeSameContent(selectedReference, pair.Value[index]);
                orphanReferences.Add(pair.Value[index]);
            }

            occurrences.Add(selectedReference);
        }

        occurrences.Sort(static (left, right) => string.Compare(
            left.Entry.RelativePath,
            right.Entry.RelativePath,
            StringComparison.Ordinal));

        return new(occurrences, orphanReferences, storedRelativePaths);
    }

    private async Task<HistoryOccurrence?> TryReadReferenceAsync
    (
        IObjectStore targetStore,
        ArchiveObjectInfo archiveObject,
        string storedRelativePath,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(TryReadReferenceAsync));

        if (!storedRelativePath.EndsWith(
                ArchiveHistoryFileNames.ReferenceSuffix,
                StringComparison.Ordinal))
        {
            return null;
        }

        await using var content = await targetStore.OpenReadAsync(
            archiveObject.Key,
            cancellationToken);
        using var serializedReference = new MemoryStream();
        await content.Content.CopyToAsync(serializedReference, cancellationToken);
        var bytes = serializedReference.ToArray();
        if (!HasHistoryReferenceDocumentType(bytes))
        {
            return null;
        }

        serializedReference.Position = 0;
        var reference = await _historyReferenceSerializer.ReadAsync(
            serializedReference,
            cancellationToken);
        if (!string.Equals(
                reference.Entry.StoredRelativePath,
                storedRelativePath,
                StringComparison.Ordinal))
        {
            throw new YabtSyncException(
                $"History reference '{storedRelativePath}' declares stored path " +
                $"'{reference.Entry.StoredRelativePath}'.");
        }

        return new
        (
            reference.Entry,
            archiveObject.Key,
            ArchiveHash.Compute(bytes),
            IsDeduplicationEligiblePath(reference.Entry.RelativePath)
        );
    }

    private static bool HasHistoryReferenceDocumentType(ReadOnlyMemory<byte> serializedReference)
    {
        try
        {
            using var document = JsonDocument.Parse(serializedReference);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("documentType", out var documentType) &&
                string.Equals(
                    documentType.GetString(),
                    ArchiveHistoryContentReference.ExpectedDocumentType,
                    StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task<HistoryOccurrence> ReadMaterializedOccurrenceAsync
    (
        IObjectStore targetStore,
        ArchiveObjectInfo archiveObject,
        string relativePath,
        CancellationToken cancellationToken
    )
    {
        await using var content = await targetStore.OpenReadAsync(
            archiveObject.Key,
            cancellationToken);
        using var hashingContent = new ContentHashingReadStream(content.Content);
        await hashingContent.CopyToAsync(Stream.Null, cancellationToken);
        var contentHash = hashingContent.CompleteHash();
        var metadata = content.Metadata is null ?
            null :
            new Dictionary<string, string>(content.Metadata, StringComparer.Ordinal);
        var entry = new ArchiveHistoryManifestEntry
        (
            relativePath,
            relativePath,
            ArchiveHistoryEntryRepresentation.Materialized,
            hashingContent.BytesRead,
            contentHash,
            archiveObject.LastModifiedUtc?.ToUniversalTime(),
            content.ContentType,
            metadata
        );

        return new
        (
            entry,
            archiveObject.Key,
            contentHash,
            IsDeduplicationEligiblePath(relativePath)
        );
    }

    private async Task<List<ReferencePlan>> CreateDeduplicationPlansAsync
    (
        IObjectStore targetStore,
        ArchiveLayout layout,
        IReadOnlyList<HistoryOccurrence> occurrences,
        HashSet<string> storedRelativePaths,
        long tinyFileMaximumBytes,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(CreateDeduplicationPlansAsync));

        var plans = new List<ReferencePlan>();
        var groups = occurrences
            .Where(occurrence => occurrence.IsDeduplicationEligible)
            .GroupBy(
                occurrence => (occurrence.Entry.ContentHash, occurrence.Entry.ContentLength),
                occurrence => occurrence)
            .Where(group => group.Count() > 1)
            .OrderBy(group => group.Key.ContentHash, StringComparer.Ordinal)
            .ThenBy(group => group.Key.ContentLength);

        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var materialized = group
                .Where(occurrence => string.Equals(
                    occurrence.Entry.Representation,
                    ArchiveHistoryEntryRepresentation.Materialized,
                    StringComparison.Ordinal))
                .OrderBy(occurrence => occurrence.Entry.RelativePath, StringComparer.Ordinal)
                .ToArray();
            if (materialized.Length == 0)
            {
                throw new YabtSyncException(
                    $"History references for hash '{group.Key.ContentHash}' have no materialized backing object.");
            }

            if (group.Key.ContentLength <= tinyFileMaximumBytes)
            {
                continue;
            }

            var canonical = materialized[0];
            for (var index = 1; index < materialized.Length; index++)
            {
                var duplicate = materialized[index];
                if (!await HaveSameBytesAsync(
                        targetStore,
                        canonical.PhysicalKey,
                        duplicate.PhysicalKey,
                        cancellationToken))
                {
                    throw new YabtSyncException(
                        $"History objects '{canonical.Entry.RelativePath}' and " +
                        $"'{duplicate.Entry.RelativePath}' have the same content hash and length " +
                        "but different bytes.");
                }

                var referenceRelativePath = await AllocateReferenceRelativePathAsync(
                    targetStore,
                    layout,
                    duplicate.Entry.RelativePath,
                    storedRelativePaths,
                    cancellationToken);
                var referenceEntry = duplicate.Entry with
                {
                    StoredRelativePath = referenceRelativePath,
                    Representation = ArchiveHistoryEntryRepresentation.Reference,
                };
                var reference = _historyReferenceSerializer.Create(referenceEntry);
                using var serializedReference = new MemoryStream();
                await _historyReferenceSerializer.WriteAsync(
                    reference,
                    serializedReference,
                    cancellationToken);
                var referenceBytes = serializedReference.ToArray();
                var manifestEntryAdditionalBytes = GetManifestEntryAdditionalBytes(
                    duplicate.Entry,
                    referenceEntry);
                var bytesSaved = duplicate.Entry.ContentLength -
                    referenceBytes.LongLength -
                    manifestEntryAdditionalBytes;
                if (bytesSaved <= 0)
                {
                    storedRelativePaths.Remove(referenceRelativePath);
                    continue;
                }

                plans.Add(new
                (
                    canonical,
                    duplicate,
                    reference,
                    referenceBytes,
                    bytesSaved
                ));
            }
        }

        return plans;
    }

    private static async Task<string> AllocateReferenceRelativePathAsync
    (
        IObjectStore targetStore,
        ArchiveLayout layout,
        string originalRelativePath,
        HashSet<string> storedRelativePaths,
        CancellationToken cancellationToken
    )
    {
        for (var sequence = 0; ; sequence++)
        {
            var candidate = sequence == 0 ?
                ArchiveHistoryFileNames.CreateReferencePath(originalRelativePath) :
                $"{originalRelativePath}.{sequence}{ArchiveHistoryFileNames.ReferenceSuffix}";
            if (!storedRelativePaths.Add(candidate))
            {
                continue;
            }

            var candidateKey = layout.ToHistoryObjectKey(candidate);
            if (!await targetStore.ExistsAsync(candidateKey, cancellationToken))
            {
                return candidate;
            }

            storedRelativePaths.Remove(candidate);
        }
    }

    private async Task ApplyPlansAsync
    (
        IArchiveMutableObjectStore targetStore,
        ArchiveLayout layout,
        string manifestKey,
        string invalidationMarkerKey,
        bool markerExists,
        ExistingManifest existingManifest,
        ArchiveHistoryManifest nextManifest,
        IReadOnlyList<ReferencePlan> plans,
        IReadOnlyList<HistoryOccurrence> orphanReferences,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(ApplyPlansAsync));

        var markerHash = markerExists ?
            await ComputeObjectHashAsync(targetStore, invalidationMarkerKey, cancellationToken) :
            ArchiveHash.Compute(InvalidationMarkerContent);
        if (!markerExists)
        {
            await targetStore.UploadAsync(
                invalidationMarkerKey,
                new MemoryStream(InvalidationMarkerContent, writable: false),
                "application/json",
                EmptyMetadata,
                cancellationToken);
        }

        foreach (var plan in plans)
        {
            var referenceKey = layout.ToHistoryObjectKey(plan.Reference.Entry.StoredRelativePath);
            await targetStore.UploadAsync(
                referenceKey,
                new MemoryStream(plan.SerializedReference, writable: false),
                "application/json",
                EmptyMetadata,
                cancellationToken);
        }

        using var serializedManifest = new MemoryStream();
        await _historyManifestSerializer.WriteAsync(
            nextManifest,
            serializedManifest,
            cancellationToken);
        serializedManifest.Position = 0;
        if (existingManifest.Exists)
        {
            var replaced = await targetStore.TryReplaceIfContentHashMatchesAsync(
                manifestKey,
                existingManifest.RawContentHash ??
                    throw new YabtSyncException("Existing history manifest did not have a byte hash."),
                serializedManifest,
                "application/json",
                EmptyMetadata,
                cancellationToken);
            if (!replaced)
            {
                throw new YabtSyncException(
                    "History manifest changed while the replacement was being published.");
            }
        }
        else
        {
            await targetStore.UploadAsync(
                manifestKey,
                serializedManifest,
                "application/json",
                EmptyMetadata,
                cancellationToken);
        }

        await EnsureCanonicalBackingsStillMatchAsync(
            targetStore,
            plans,
            cancellationToken);

        // Remove obsolete references while their corresponding materialized objects still exist.
        // If cleanup is interrupted, the next rebuild can therefore prefer the complete object.
        // Deleting planned originals only after this loop prevents two disagreeing references from
        // becoming the only descriptions of one logical historical occurrence.
        foreach (var orphanReference in orphanReferences)
        {
            var deleted = await targetStore.TryDeleteIfContentHashMatchesAsync(
                orphanReference.PhysicalKey,
                orphanReference.StoredObjectContentHash,
                cancellationToken);
            if (!deleted)
            {
                throw new YabtSyncException(
                    $"Orphan history reference '{orphanReference.Entry.StoredRelativePath}' changed before cleanup.");
            }
        }

        foreach (var plan in plans)
        {
            var deleted = await targetStore.TryDeleteIfContentHashMatchesAsync(
                plan.Original.PhysicalKey,
                plan.Original.StoredObjectContentHash,
                cancellationToken);
            if (!deleted)
            {
                throw new YabtSyncException(
                    $"Historical object '{plan.Original.Entry.RelativePath}' changed before it could be replaced.");
            }
        }

        var markerDeleted = await targetStore.TryDeleteIfContentHashMatchesAsync(
            invalidationMarkerKey,
            markerHash,
            cancellationToken);
        if (!markerDeleted)
        {
            throw new YabtSyncException(
                "History manifest invalidation marker changed before it could be cleared.");
        }
    }

    private async Task<ExistingManifest> ReadExistingManifestAsync
    (
        IObjectStore targetStore,
        string manifestKey,
        bool trustManifest,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(ReadExistingManifestAsync));

        if (!await targetStore.ExistsAsync(manifestKey, cancellationToken))
        {
            return new(false, null, null);
        }

        await using var content = await targetStore.OpenReadAsync(manifestKey, cancellationToken);
        using var serializedManifest = new MemoryStream();
        await content.Content.CopyToAsync(serializedManifest, cancellationToken);
        var bytes = serializedManifest.ToArray();
        ArchiveHistoryManifest? manifest = null;
        if (trustManifest)
        {
            try
            {
                serializedManifest.Position = 0;
                manifest = await _historyManifestSerializer.ReadAsync(
                    serializedManifest,
                    cancellationToken);
            }
            catch (Exception)
            {
                manifest = null;
            }
        }

        return new(true, manifest, ArchiveHash.Compute(bytes));
    }

    private static async Task<string> ComputeObjectHashAsync
    (
        IObjectStore targetStore,
        string key,
        CancellationToken cancellationToken
    )
    {
        await using var content = await targetStore.OpenReadAsync(key, cancellationToken);
        using var hashingContent = new ContentHashingReadStream(content.Content);
        await hashingContent.CopyToAsync(Stream.Null, cancellationToken);
        return hashingContent.CompleteHash();
    }

    private static async Task EnsureCanonicalBackingsStillMatchAsync
    (
        IObjectStore targetStore,
        IEnumerable<ReferencePlan> plans,
        CancellationToken cancellationToken
    )
    {
        var canonicalOccurrences = plans
            .Select(plan => plan.Canonical)
            .DistinctBy(occurrence => occurrence.PhysicalKey, StringComparer.Ordinal);
        foreach (var canonicalOccurrence in canonicalOccurrences)
        {
            var currentHash = await ComputeObjectHashAsync(
                targetStore,
                canonicalOccurrence.PhysicalKey,
                cancellationToken);
            if (!string.Equals(
                    currentHash,
                    canonicalOccurrence.StoredObjectContentHash,
                    StringComparison.Ordinal))
            {
                throw new YabtSyncException(
                    $"Materialized history backing '{canonicalOccurrence.Entry.RelativePath}' " +
                    "changed before redundant objects could be removed.");
            }
        }
    }

    private static async Task<bool> HaveSameBytesAsync
    (
        IObjectStore targetStore,
        string leftKey,
        string rightKey,
        CancellationToken cancellationToken
    )
    {
        await using var left = await targetStore.OpenReadAsync(leftKey, cancellationToken);
        await using var right = await targetStore.OpenReadAsync(rightKey, cancellationToken);
        var leftBuffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        var rightBuffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            while (true)
            {
                var leftLength = await FillBufferAsync(
                    left.Content,
                    leftBuffer.AsMemory(0, BufferSize),
                    cancellationToken);
                var rightLength = await FillBufferAsync(
                    right.Content,
                    rightBuffer.AsMemory(0, BufferSize),
                    cancellationToken);
                if (leftLength != rightLength)
                {
                    return false;
                }

                if (leftLength == 0)
                {
                    return true;
                }

                if (!leftBuffer.AsSpan(0, leftLength).SequenceEqual(
                        rightBuffer.AsSpan(0, rightLength)))
                {
                    return false;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(leftBuffer);
            ArrayPool<byte>.Shared.Return(rightBuffer);
        }
    }

    private static async Task<int> FillBufferAsync
    (
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken
    )
    {
        var totalBytesRead = 0;
        while (totalBytesRead < buffer.Length)
        {
            var bytesRead = await stream.ReadAsync(buffer[totalBytesRead..], cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            totalBytesRead += bytesRead;
        }

        return totalBytesRead;
    }

    private static int CountDuplicateGroups(IEnumerable<HistoryOccurrence> occurrences) =>
        occurrences
            .Where(occurrence => occurrence.IsDeduplicationEligible)
            .GroupBy(occurrence => (occurrence.Entry.ContentHash, occurrence.Entry.ContentLength))
            .Count(group => group.Count() > 1);

    private static bool IsHistoryControlPath(string relativePath) =>
        string.Equals(relativePath, ArchiveHistoryFileNames.Manifest, StringComparison.Ordinal) ||
        string.Equals(
            relativePath,
            ArchiveHistoryManifest.InvalidationMarkerFileName,
            StringComparison.Ordinal);

    private static bool IsDeduplicationEligiblePath(string relativePath)
    {
        var normalizedPath = ArchiveLayout.NormalizeObjectKey(relativePath);
        var separator = normalizedPath.LastIndexOf('/');
        var fileName = separator < 0 ? normalizedPath : normalizedPath[(separator + 1)..];
        return !string.Equals(fileName, ArchiveFolderMarkerFileNames.EmptyFolder, StringComparison.Ordinal) &&
            !fileName.EndsWith(ArchiveHistoryFileNames.ReferenceSuffix, StringComparison.Ordinal) &&
            !string.Equals(fileName, BackupRootFileNames.Primary, StringComparison.Ordinal) &&
            !string.Equals(fileName, FolderPolicyFileNames.Primary, StringComparison.Ordinal) &&
            !string.Equals(fileName, ArchiveChangeManifest.UncompressedFileName, StringComparison.Ordinal) &&
            !string.Equals(fileName, ArchiveChangeManifest.BrotliFileName, StringComparison.Ordinal) &&
            !string.Equals(fileName, ArchiveChangeManifest.InvalidationMarkerFileName, StringComparison.Ordinal) &&
            !string.Equals(fileName, ArchiveHistoryFileNames.Manifest, StringComparison.Ordinal) &&
            !string.Equals(
                fileName,
                ArchiveHistoryManifest.InvalidationMarkerFileName,
                StringComparison.Ordinal);
    }

    private static void ValidateLayout(ArchiveLayout layout)
    {
        var historyPrefix = ArchiveLayout.NormalizeObjectPrefix(layout.HistPrefix);
        if (historyPrefix is null)
        {
            throw new YabtSyncException("History deduplication requires a nonempty history prefix.");
        }

        var temporaryPrefix = ArchiveInternalFolderNames.TemporaryUploads;
        var livePrefix = ArchiveLayout.NormalizeObjectPrefix(layout.LivePrefix);
        if (livePrefix is not null && PrefixesOverlap(livePrefix, temporaryPrefix))
        {
            throw new YabtSyncException(
                $"Archive live prefix '{livePrefix}' conflicts with reserved internal prefix " +
                $"'{temporaryPrefix}'.");
        }

        if (PrefixesOverlap(historyPrefix, temporaryPrefix))
        {
            throw new YabtSyncException(
                $"Archive history prefix '{historyPrefix}' conflicts with reserved internal prefix " +
                $"'{temporaryPrefix}'.");
        }

        if (livePrefix is not null && PrefixesOverlap(livePrefix, historyPrefix))
        {
            throw new YabtSyncException(
                $"Archive live prefix '{livePrefix}' overlaps history prefix '{historyPrefix}'.");
        }
    }

    private static bool PrefixesOverlap(string firstPrefix, string secondPrefix) =>
        IsSameOrUnderPrefix(firstPrefix, secondPrefix) ||
        IsSameOrUnderPrefix(secondPrefix, firstPrefix);

    private static bool IsSameOrUnderPrefix(string objectKey, string prefix)
    {
        var normalizedObjectKey = ArchiveLayout.NormalizeObjectKey(objectKey);
        var normalizedPrefix = ArchiveLayout.NormalizeObjectKey(prefix);
        return string.Equals(
                normalizedObjectKey,
                normalizedPrefix,
                StringComparison.OrdinalIgnoreCase) ||
            normalizedObjectKey.StartsWith(
                $"{normalizedPrefix}/",
                StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureReferencesHaveMaterializedBacking
    (
        IEnumerable<HistoryOccurrence> occurrences
    )
    {
        var contentGroups = occurrences.GroupBy(
            occurrence => (occurrence.Entry.ContentHash, occurrence.Entry.ContentLength));
        foreach (var contentGroup in contentGroups)
        {
            var hasReference = false;
            var hasMaterialized = false;
            foreach (var occurrence in contentGroup)
            {
                hasReference |= string.Equals(
                    occurrence.Entry.Representation,
                    ArchiveHistoryEntryRepresentation.Reference,
                    StringComparison.Ordinal);
                hasMaterialized |= string.Equals(
                    occurrence.Entry.Representation,
                    ArchiveHistoryEntryRepresentation.Materialized,
                    StringComparison.Ordinal);
            }

            if (hasReference && !hasMaterialized)
            {
                throw new YabtSyncException(
                    $"History references for hash '{contentGroup.Key.ContentHash}' have no " +
                    "materialized backing object.");
            }
        }
    }

    private static void EnsureActualAndReferenceDescribeSameOccurrence
    (
        HistoryOccurrence actual,
        HistoryOccurrence reference
    )
    {
        var actualEntry = actual.Entry;
        var referenceEntry = reference.Entry;
        if (!string.Equals(actualEntry.ContentHash, referenceEntry.ContentHash, StringComparison.Ordinal) ||
            actualEntry.ContentLength != referenceEntry.ContentLength ||
            !HaveSameExactTimestamp(actualEntry.LastModifiedUtc, referenceEntry.LastModifiedUtc) ||
            !string.Equals(actualEntry.ContentType, referenceEntry.ContentType, StringComparison.Ordinal) ||
            !HaveSameMetadata(actualEntry.Metadata, referenceEntry.Metadata))
        {
            throw new YabtSyncException(
                $"Materialized history object '{actualEntry.RelativePath}' and its reference " +
                "describe different occurrence metadata; both were preserved for manual recovery.");
        }
    }

    private static bool HaveSameExactTimestamp(DateTimeOffset? left, DateTimeOffset? right) =>
        left.HasValue && right.HasValue ?
            left.Value.EqualsExact(right.Value) :
            left.HasValue == right.HasValue;

    private static bool HaveSameMetadata
    (
        IReadOnlyDictionary<string, string>? left,
        IReadOnlyDictionary<string, string>? right
    )
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (var pair in left)
        {
            if (!right.TryGetValue(pair.Key, out var rightValue) ||
                !string.Equals(pair.Value, rightValue, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static long GetManifestEntryAdditionalBytes
    (
        ArchiveHistoryManifestEntry materializedEntry,
        ArchiveHistoryManifestEntry referenceEntry
    )
    {
        var materializedStoredPathLength = JsonSerializer.SerializeToUtf8Bytes(
            materializedEntry.StoredRelativePath).LongLength;
        var referenceStoredPathLength = JsonSerializer.SerializeToUtf8Bytes(
            referenceEntry.StoredRelativePath).LongLength;
        var materializedRepresentationLength = JsonSerializer.SerializeToUtf8Bytes(
            materializedEntry.Representation).LongLength;
        var referenceRepresentationLength = JsonSerializer.SerializeToUtf8Bytes(
            referenceEntry.Representation).LongLength;
        return referenceStoredPathLength - materializedStoredPathLength +
            referenceRepresentationLength - materializedRepresentationLength;
    }

    private static void EnsureReferencesDescribeSameContent
    (
        HistoryOccurrence first,
        HistoryOccurrence second
    )
    {
        if (!string.Equals(
                first.Entry.ContentHash,
                second.Entry.ContentHash,
                StringComparison.Ordinal) ||
            first.Entry.ContentLength != second.Entry.ContentLength ||
            !HaveSameExactTimestamp(
                first.Entry.LastModifiedUtc,
                second.Entry.LastModifiedUtc) ||
            !string.Equals(
                first.Entry.ContentType,
                second.Entry.ContentType,
                StringComparison.Ordinal) ||
            !HaveSameMetadata(first.Entry.Metadata, second.Entry.Metadata))
        {
            throw new YabtSyncException(
                $"History references for '{first.Entry.RelativePath}' disagree about their " +
                "original content or occurrence metadata.");
        }
    }

    private static HistoryDeduplicationResult CreateResult
    (
        bool dryRun,
        HistoryScan scan,
        int duplicateGroupCount,
        int replacedObjectCount,
        int tinyObjectCount,
        long bytesSaved
    )
    {
        var action = dryRun ? "would replace" : "replaced";
        var message =
            $"History deduplication {(dryRun ? "dry run " : string.Empty)}completed: " +
            $"scanned {scan.Occurrences.Count} object(s), found {duplicateGroupCount} duplicate group(s), " +
            $"{action} {replacedObjectCount} object(s), skipped {tinyObjectCount} tiny object(s), " +
            $"and {(dryRun ? "would save" : "saved")} {bytesSaved} byte(s).";
        return new
        (
            true,
            message,
            scan.Occurrences.Count,
            scan.Occurrences.Count(occurrence => string.Equals(
                occurrence.Entry.Representation,
                ArchiveHistoryEntryRepresentation.Reference,
                StringComparison.Ordinal)),
            duplicateGroupCount,
            replacedObjectCount,
            tinyObjectCount,
            bytesSaved
        );
    }

    private sealed record ExistingManifest
    (
        bool Exists,
        ArchiveHistoryManifest? Manifest,
        string? RawContentHash
    );

    private sealed record HistoryOccurrence
    (
        ArchiveHistoryManifestEntry Entry,
        string PhysicalKey,
        string StoredObjectContentHash,
        bool IsDeduplicationEligible
    );

    private sealed record HistoryScan
    (
        IReadOnlyList<HistoryOccurrence> Occurrences,
        IReadOnlyList<HistoryOccurrence> OrphanReferences,
        HashSet<string> StoredRelativePaths
    );

    private sealed record ReferencePlan
    (
        HistoryOccurrence Canonical,
        HistoryOccurrence Original,
        ArchiveHistoryContentReference Reference,
        byte[] SerializedReference,
        long BytesSaved
    );
}
