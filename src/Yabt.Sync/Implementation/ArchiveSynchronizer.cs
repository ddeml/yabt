using System.Buffers;
using System.Collections.Frozen;
using System.IO.Compression;
using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.Metadata;

namespace Yabt.Sync.Implementation;

internal sealed class ArchiveSynchronizer
(
    ILogger<ArchiveSynchronizer> _logger,
    IBackupRootLocator _backupRootLocator,
    IFolderPolicyReader _folderPolicyReader,
    IEnumerable<IArchiveFormatHandler> formatHandlers,
    IEnumerable<IBackupRootStoreResolver> storeResolvers,
    IEnumerable<ISourceRootObjectStoreResolver> sourceRootObjectStoreResolvers,
    IChangeManifestSerializer _changeManifestSerializer,
    IBackupRootSerializer _backupRootSerializer,
    ILogicalStateManifestSerializer _logicalStateManifestSerializer,
    TimeProvider _timeProvider
) : IArchiveSynchronizer
{
    private const int DefaultBufferSize = 81_920;

    private readonly FrozenDictionary<string, IArchiveFormatHandler> _formatHandlers =
        formatHandlers.ToFrozenDictionary
    (
        formatHandler => formatHandler.FormatName,
        StringComparer.Ordinal
    );

    private readonly FrozenDictionary<string, IBackupRootStoreResolver> _storeResolvers = storeResolvers.ToFrozenDictionary
    (
        resolver => resolver.StoreKind,
        StringComparer.Ordinal
    );

    private readonly ISourceRootObjectStoreResolver _sourceRootObjectStoreResolver =
        sourceRootObjectStoreResolvers.SingleOrDefault() ??
        throw new YabtSyncException("Exactly one source root object store resolver must be registered.");

    private static readonly IReadOnlyDictionary<string, string> EmptyMetadata =
        new Dictionary<string, string>(StringComparer.Ordinal).ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly byte[] LogicalStateManifestInvalidationMarkerContent = Encoding.UTF8.GetBytes
    (
        "{\"documentType\":\"yabt.logicalStateManifestInvalidation\",\"schemaVersion\":1}\n"
    );

    private static readonly FrozenSet<string> ReservedRootObjectKeys = new[]
    {
        BackupRootFileNames.Primary,
        ArchiveChangeManifest.UncompressedFileName,
        ArchiveChangeManifest.BrotliFileName,
        ArchiveChangeManifest.InvalidationMarkerFileName,
        ArchiveLogicalStateManifest.FileName,
        ArchiveLogicalStateManifest.InvalidationMarkerFileName,
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public Task<SyncRunResult> BackupAsync
    (
        SyncRunRequest request,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(BackupAsync));

        return RunArchiveOperationAsync(
            request,
            ArchiveOperationDirection.Backup,
            cancellationToken);
    }

    public Task<SyncRunResult> SyncAsync
    (
        SyncRunRequest request,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(SyncAsync));

        return BackupAsync(request, cancellationToken);
    }

    public Task<SyncRunResult> RestoreAsync
    (
        SyncRunRequest request,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(RestoreAsync));

        return RunArchiveOperationAsync(
            request,
            ArchiveOperationDirection.Restore,
            cancellationToken);
    }

    private Task<SyncRunResult> RunArchiveOperationAsync
    (
        SyncRunRequest request,
        ArchiveOperationDirection direction,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(RunArchiveOperationAsync));

        ArgumentNullException.ThrowIfNull(request);
        return direction switch
        {
            ArchiveOperationDirection.Backup => RunBackupOperationAsync(
                request,
                cancellationToken),
            ArchiveOperationDirection.Restore => RunRestoreOperationAsync(
                request,
                cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, null),
        };
    }

    private async Task<SyncRunResult> RunBackupOperationAsync
    (
        SyncRunRequest request,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(RunBackupOperationAsync));

        _logger.LogBackupRequested(
            request.SourceRoot,
            request.DryRun);

        var context = await CreateContextAsync(request, cancellationToken);

        return await ApplyProjectionAsync(
            context,
            writeChanges: !request.DryRun,
            verifyOnly: false,
            byteForByte: request.ByteForByte,
            operationName: request.DryRun ? "backup dry run" : "backup",
            cancellationToken);
    }

    private async Task<SyncRunResult> RunRestoreOperationAsync
    (
        SyncRunRequest request,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(RunRestoreOperationAsync));

        if (string.IsNullOrWhiteSpace(request.DestinationRoot))
        {
            throw new YabtSyncException(
                "Restore requires a filesystem destination selected with --destination-root.");
        }

        var context = await CreateRestoreContextAsync(request, cancellationToken);
        var restoreTarget = new FileSystemRestoreTarget(
            request.DestinationRoot,
            _logger);
        ValidateRestoreLocations(
            context.DescriptorRootPath,
            restoreTarget.RootPath,
            context.LocalArchiveRootPath);

        var changeManifestLoad = await ReadChangeManifestAsync(
            context.ArchiveStore,
            recoverInvalidManifest: false,
            cancellationToken);
        var changeManifest = changeManifestLoad.Manifest ??
            throw new YabtSyncException(
                "Restore requires a valid live change manifest created by a successful backup.");
        context = await LoadArchiveRootDocumentAsync(
            context,
            changeManifest,
            cancellationToken);

        RootDescriptorRestore rootDescriptorRestore;
        ArchiveLayout destinationLayout;
        if (context.ArchiveDocument is not null)
        {
            rootDescriptorRestore = await InspectDestinationRootDescriptorAsync
            (
                restoreTarget.RootPath,
                context.ArchiveDocument,
                request.ReplaceRootDescriptor,
                cancellationToken
            );
            destinationLayout = context.ArchiveDocument.Descriptor.Layout;
        }
        else
        {
            rootDescriptorRestore = RootDescriptorRestore.None;
            destinationLayout = await GetRestoreDestinationLayoutAsync
            (
                restoreTarget.RootPath,
                context.Descriptor.Layout.HistPrefix,
                cancellationToken
            );
        }

        ValidateInternalLayoutPrefixes(destinationLayout);
        ValidateRestoreLayoutPaths(destinationLayout, restoreTarget);
        await restoreTarget.EnsureSafeAsync(
            destinationLayout.LivePrefix,
            destinationLayout.HistPrefix,
            cancellationToken);

        var restoreObjects = await LoadRestoreObjectsAsync(
            context,
            changeManifest,
            cancellationToken);
        var rootFormatHandler = ResolveRestoreRootFormatHandler(
            changeManifest,
            restoreObjects);
        context = context with
        {
            RootFormatHandler = rootFormatHandler,
            RootIsPackaged = rootFormatHandler.ProjectsBesideSourceFolder,
        };

        await using var plan = await CreateRestorePlanAsync
        (
            context,
            restoreObjects,
            restoreTarget,
            useProjectionProvenance:
                changeManifest.SchemaVersion == ArchiveChangeManifest.ExpectedSchemaVersion,
            cancellationToken
        );
        ValidateRestorePlanInternalPaths(
            plan,
            destinationLayout,
            restoreTarget);

        var destinationStore = _sourceRootObjectStoreResolver.ResolveSourceRoot(
            restoreTarget.RootPath);
        if (destinationStore is not IArchiveMutableObjectStore mutableDestinationStore)
        {
            throw new YabtSyncException(
                "Restore destination does not provide the guarded filesystem mutations required " +
                    "for historization.");
        }

        await using var mutationLock = request.DryRun ?
            null :
            await mutableDestinationStore.AcquireArchiveMutationLockAsync(cancellationToken);
        using var operationCancellation = mutationLock is null ?
            null :
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                mutationLock.LockLostToken);
        if (operationCancellation is not null)
        {
            cancellationToken = operationCancellation.Token;
        }

        if (mutationLock is not null)
        {
            if (context.ArchiveDocument is not null)
            {
                rootDescriptorRestore = await InspectDestinationRootDescriptorAsync
                (
                    restoreTarget.RootPath,
                    context.ArchiveDocument,
                    request.ReplaceRootDescriptor,
                    cancellationToken
                );
                destinationLayout = context.ArchiveDocument.Descriptor.Layout;
            }
            else
            {
                rootDescriptorRestore = RootDescriptorRestore.None;
                destinationLayout = await GetRestoreDestinationLayoutAsync
                (
                    restoreTarget.RootPath,
                    context.Descriptor.Layout.HistPrefix,
                    cancellationToken
                );
            }

            ValidateInternalLayoutPrefixes(destinationLayout);
            ValidateRestoreLayoutPaths(destinationLayout, restoreTarget);
            await restoreTarget.EnsureSafeAsync(
                destinationLayout.LivePrefix,
                destinationLayout.HistPrefix,
                cancellationToken);
            ValidateRestorePlanInternalPaths(
                plan,
                destinationLayout,
                restoreTarget);
        }

        if (request.ByteForByte)
        {
            await ValidateRestorePlanContentAsync(
                plan.Files,
                cancellationToken);
        }

        var logicalStateManifestLoad = await ReadLogicalStateManifestAsync(
            destinationStore,
            cancellationToken);
        var reconciliation = await CreateRestoreReconciliationAsync
        (
            destinationStore,
            destinationLayout,
            plan,
            logicalStateManifestLoad.Manifest,
            request.ByteForByte,
            cancellationToken
        );

        if (!request.DryRun)
        {
            await ApplyRestorePlanAsync(
                mutableDestinationStore,
                destinationLayout,
                plan,
                reconciliation,
                restoreTarget,
                logicalStateManifestLoad,
                rootDescriptorRestore,
                context.ArchiveDocument,
                cancellationToken);
        }

        var operationName = request.DryRun ? "Restore dry run" : "Restore";
        return new SyncRunResult
        (
            Completed: true,
            Message: $"{operationName} completed; {reconciliation.NewCount} new item(s), " +
                $"{reconciliation.ChangedCount} changed item(s), " +
                $"{reconciliation.ExtraCount} extra item(s), and " +
                $"{reconciliation.UnchangedCount} unchanged item(s). " +
                $"{reconciliation.HistoryMoves.Count} existing item(s) " +
                $"{(request.DryRun ? "would be moved" : "were moved")} to history.",
            NewCount: reconciliation.NewCount,
            ChangedCount: reconciliation.ChangedCount,
            ExtraCount: reconciliation.ExtraCount,
            UnchangedCount: reconciliation.UnchangedCount
        );
    }

    private async Task<ArchiveLayout> GetRestoreDestinationLayoutAsync
    (
        string destinationRootPath,
        string defaultHistoryPrefix,
        CancellationToken cancellationToken
    )
    {
        var descriptorPath = Path.Combine(
            destinationRootPath,
            BackupRootFileNames.Primary);
        if (Directory.Exists(descriptorPath))
        {
            throw new YabtSyncException(
                $"Restore destination metadata path '{descriptorPath}' must be a file, not a folder.");
        }

        if (!File.Exists(descriptorPath))
        {
            return new ArchiveLayout(HistPrefix: defaultHistoryPrefix);
        }

        var destinationLocation = await _backupRootLocator.LocateRootAsync(
            destinationRootPath,
            cancellationToken);
        if (!string.Equals(
                ResolvePhysicalPath(destinationLocation.RootPath),
                ResolvePhysicalPath(destinationRootPath),
                GetFileSystemPathComparison()))
        {
            throw new YabtSyncException(
                $"Restore destination descriptor '{descriptorPath}' did not resolve to its own folder.");
        }

        return destinationLocation.Descriptor.Layout;
    }

    private async Task<RestoreContext> CreateRestoreContextAsync
    (
        SyncRunRequest request,
        CancellationToken cancellationToken
    )
    {
        var descriptorRootPath = Path.GetFullPath(request.SourceRoot);
        var sourceLocation = await _backupRootLocator.LocateRootAsync(
            descriptorRootPath,
            cancellationToken);
        if (!string.Equals(
                descriptorRootPath,
                Path.GetFullPath(sourceLocation.RootPath),
                GetFileSystemPathComparison()))
        {
            throw new YabtSyncException(
                "Restore currently requires the source-root argument to be the folder containing .yabt-root.json.");
        }

        ValidateInternalLayoutPrefixes(sourceLocation.Descriptor.Layout);
        var archiveStoreConfiguration = GetTargetStoreConfiguration(
            sourceLocation.Descriptor,
            request.TargetStoreId);
        if (!_storeResolvers.TryGetValue(archiveStoreConfiguration.Kind, out var archiveStoreResolver))
        {
            throw new YabtSyncException(
                $"No object store resolver is registered for store kind '{archiveStoreConfiguration.Kind}'.");
        }

        return new
        (
            descriptorRootPath,
            archiveStoreResolver.ResolveStore(
                archiveStoreConfiguration,
                sourceLocation.RootPath),
            sourceLocation.Descriptor,
            sourceLocation.Document,
            ArchiveDocument: null,
            RootFormatHandler: null,
            RootIsPackaged: false,
            TryResolveConfiguredLocalArchiveRootPath(
                archiveStoreConfiguration,
                sourceLocation.RootPath)
        );
    }

    private async Task<RestoreContext> LoadArchiveRootDocumentAsync
    (
        RestoreContext context,
        ArchiveChangeManifest changeManifest,
        CancellationToken cancellationToken
    )
    {
        var descriptorExists = await context.ArchiveStore.ExistsAsync(
            BackupRootFileNames.Primary,
            cancellationToken);
        if (!descriptorExists)
        {
            if (changeManifest.RootDescriptorContentHash is not null ||
                changeManifest.RootDescriptorContentLength.HasValue)
            {
                throw new YabtSyncException(
                    $"The live change manifest requires root descriptor " +
                        $"'{BackupRootFileNames.Primary}', but that object is missing from the archive.");
            }

            return context;
        }

        BackupRootDocument archiveDocument;
        try
        {
            await using var content = await context.ArchiveStore.OpenReadAsync(
                BackupRootFileNames.Primary,
                cancellationToken);
            archiveDocument = await _backupRootSerializer.ReadDocumentAsync(
                content.Content,
                cancellationToken);
        }
        catch (Exception ex)
        {
            throw new YabtSyncException(
                $"Archive root descriptor '{BackupRootFileNames.Primary}' could not be validated.",
                ex);
        }

        if (changeManifest.RootDescriptorContentLength.HasValue &&
            changeManifest.RootDescriptorContentLength.Value != archiveDocument.ContentLength ||
            changeManifest.RootDescriptorContentHash is not null &&
            !string.Equals(
                changeManifest.RootDescriptorContentHash,
                archiveDocument.ContentHash,
                StringComparison.Ordinal))
        {
            throw new YabtSyncException(
                $"Archive root descriptor '{BackupRootFileNames.Primary}' does not match the " +
                    "content evidence in the live change manifest.");
        }

        if (!string.Equals(
                context.Descriptor.ArchiveId,
                archiveDocument.Descriptor.ArchiveId,
                StringComparison.Ordinal))
        {
            throw new YabtSyncException(
                "The selected archive contains a root descriptor for a different archive id.");
        }

        ValidateInternalLayoutPrefixes(archiveDocument.Descriptor.Layout);
        return context with
        {
            Descriptor = archiveDocument.Descriptor,
            ArchiveDocument = archiveDocument,
        };
    }

    private async Task<RootDescriptorRestore> InspectDestinationRootDescriptorAsync
    (
        string destinationRootPath,
        BackupRootDocument archiveDocument,
        bool replaceRootDescriptor,
        CancellationToken cancellationToken
    )
    {
        var descriptorPath = Path.Combine(
            destinationRootPath,
            BackupRootFileNames.Primary);
        if (Directory.Exists(descriptorPath))
        {
            throw new YabtSyncException(
                $"Restore destination metadata path '{descriptorPath}' must be a file, not a folder.");
        }

        if (!File.Exists(descriptorPath))
        {
            return new(NeedsWrite: true, MoveExistingToHistory: false);
        }

        BackupRootDocument destinationDocument;
        try
        {
            await using var content = new FileStream
            (
                descriptorPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                DefaultBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan
            );
            destinationDocument = await _backupRootSerializer.ReadDocumentAsync(
                content,
                cancellationToken);
        }
        catch (Exception ex)
        {
            throw new YabtSyncException(
                $"Existing destination root descriptor '{descriptorPath}' could not be validated. " +
                    "Move it aside manually before restoring.",
                ex);
        }

        if (archiveDocument.ContentEquals(destinationDocument))
        {
            return RootDescriptorRestore.None;
        }

        if (!string.Equals(
                archiveDocument.Descriptor.ArchiveId,
                destinationDocument.Descriptor.ArchiveId,
                StringComparison.Ordinal) ||
            !HaveSameLayout(
                archiveDocument.Descriptor.Layout,
                destinationDocument.Descriptor.Layout))
        {
            throw new YabtSyncException(
                "The existing destination root descriptor has a different archive id or layout. " +
                    "YABT will not combine those roots; use a separate destination or move the " +
                    "existing descriptor aside manually.");
        }

        if (!replaceRootDescriptor)
        {
            throw new YabtSyncException(
                "The existing destination .yabt-root.json differs from the exact archived copy. " +
                    "Re-run restore with --replace-root-descriptor to move the existing descriptor " +
                    "to history and restore the archived bytes.");
        }

        return new(NeedsWrite: true, MoveExistingToHistory: true);
    }

    private static bool HaveSameLayout(ArchiveLayout first, ArchiveLayout second) =>
        string.Equals(
            ArchiveLayout.NormalizeObjectKey(first.LivePrefix),
            ArchiveLayout.NormalizeObjectKey(second.LivePrefix),
            StringComparison.Ordinal) &&
        string.Equals(
            ArchiveLayout.NormalizeObjectKey(first.HistPrefix),
            ArchiveLayout.NormalizeObjectKey(second.HistPrefix),
            StringComparison.Ordinal);

    private IArchiveFormatHandler ResolveRestoreRootFormatHandler
    (
        ArchiveChangeManifest changeManifest,
        IReadOnlyList<ArchiveProjectedObject> restoreObjects
    )
    {
        if (changeManifest.RootFormat is not null)
        {
            if (!_formatHandlers.TryGetValue(changeManifest.RootFormat, out var rootFormatHandler))
            {
                throw new YabtSyncException(
                    "No archive format handler is registered for durable root format " +
                        $"'{changeManifest.RootFormat}'.");
            }

            return rootFormatHandler;
        }

        var packageCount = 0;
        foreach (var restoreObject in restoreObjects)
        {
            if (_formatHandlers.Values.Any(formatHandler =>
                    formatHandler.ProjectsBesideSourceFolder &&
                    formatHandler.CanRestoreArtifact(restoreObject)))
            {
                packageCount++;
            }
        }

        if (packageCount == 0 || restoreObjects.Count > 1)
        {
            var fallbackHandlers = _formatHandlers.Values
                .Where(formatHandler =>
                    !formatHandler.ProjectsBesideSourceFolder &&
                    restoreObjects.All(formatHandler.CanRestoreArtifact))
                .ToArray();
            if (fallbackHandlers.Length == 1)
            {
                return fallbackHandlers[0];
            }

            throw new YabtSyncException(
                "The legacy change manifest does not identify one unambiguous root format handler.");
        }

        throw new YabtSyncException(
            "This archive change manifest predates durable root-format evidence, so YABT " +
                "cannot safely distinguish a packaged root from a sole packaged child folder. " +
                "Run backup once with the current YABT version before restoring it.");
    }

    private static void ValidateRestoreLocations
    (
        string descriptorRootPath,
        string destinationRootPath,
        string? localArchiveRootPath
    )
    {
        var descriptorRoot = EnsureTrailingDirectorySeparator(
            ResolvePhysicalPath(descriptorRootPath));
        var destinationRoot = EnsureTrailingDirectorySeparator(
            ResolvePhysicalPath(destinationRootPath));
        var comparison = GetFileSystemPathComparison();
        if (!string.Equals(descriptorRoot, destinationRoot, comparison) &&
            (descriptorRoot.StartsWith(destinationRoot, comparison) ||
                destinationRoot.StartsWith(descriptorRoot, comparison)))
        {
            throw new YabtSyncException(
                "Restore destination may be the configured source root itself, but it must not be " +
                    "one of that root's ancestors or descendants. " +
                    $"The source root resolves to '{descriptorRoot}', and the destination " +
                    $"resolves to '{destinationRoot}'. Use a separate sibling folder instead.");
        }

        if (localArchiveRootPath is null) { return; }

        var localArchiveRoot = EnsureTrailingDirectorySeparator(
            ResolvePhysicalPath(localArchiveRootPath));
        if (localArchiveRoot.StartsWith(destinationRoot, comparison) ||
            destinationRoot.StartsWith(localArchiveRoot, comparison))
        {
            throw new YabtSyncException(
                "Restore destination must not be the selected filesystem archive or one of its " +
                    "ancestors or descendants.");
        }
    }

    private static string? TryResolveConfiguredLocalArchiveRootPath
    (
        BackupRootStore store,
        string descriptorRootPath
    )
    {
        if (store.ProviderProperties is null) { return null; }

        foreach (var providerProperty in store.ProviderProperties)
        {
            if (!string.Equals(
                    providerProperty.Key,
                    "rootPath",
                    StringComparison.OrdinalIgnoreCase) ||
                providerProperty.Value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var configuredPath = providerProperty.Value.GetString();
            if (string.IsNullOrWhiteSpace(configuredPath)) { return null; }

            return Path.IsPathRooted(configuredPath) ?
                Path.GetFullPath(configuredPath) :
                Path.GetFullPath(configuredPath, descriptorRootPath);
        }

        return null;
    }

    private async Task<IReadOnlyList<ArchiveProjectedObject>> LoadRestoreObjectsAsync
    (
        RestoreContext context,
        ArchiveChangeManifest changeManifest,
        CancellationToken cancellationToken
    )
    {
        var listedObjects = new Dictionary<string, ArchiveObjectInfo>(StringComparer.Ordinal);
        var livePrefix = ArchiveLayout.NormalizeObjectPrefix(context.Descriptor.Layout.LivePrefix);
        var liveItems = context.ArchiveStore.GetFolderItemsAsync(
            livePrefix,
            recursive: true,
            cancellationToken);
        await foreach (var item in liveItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Object is null) { continue; }

            var archiveKey = ArchiveLayout.NormalizeObjectKey(item.Object.Key);
            if (IsInternalObject(archiveKey, context.Descriptor.Layout)) { continue; }

            var relativePath = ArchiveLayout.RemovePrefix(archiveKey, livePrefix);
            if (!listedObjects.TryAdd(relativePath, item.Object))
            {
                throw new YabtSyncException(
                    $"Archive live object '{relativePath}' was listed more than once.");
            }
        }

        var manifestEntries = new Dictionary<string, ArchiveChangeManifestEntry>(StringComparer.Ordinal);
        foreach (var entry in changeManifest.Entries)
        {
            var relativePath = ArchiveLayout.NormalizeObjectKey(entry.RelativePath);
            if (string.IsNullOrEmpty(relativePath) ||
                !ArchiveHash.IsValid(entry.ContentHash))
            {
                throw new YabtSyncException(
                    $"Change manifest entry '{entry.RelativePath}' does not contain safe restore evidence.");
            }

            if (!manifestEntries.TryAdd(relativePath, entry))
            {
                throw new YabtSyncException(
                    $"Change manifest contains duplicate restore path '{relativePath}'.");
            }
        }

        var unexpectedObjects = listedObjects.Keys
            .Where(relativePath => !manifestEntries.ContainsKey(relativePath))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (unexpectedObjects.Length != 0)
        {
            throw new YabtSyncException(
                $"Archive contains live object '{unexpectedObjects[0]}' that is not covered by the change manifest.");
        }

        var restoreObjects = new List<ArchiveProjectedObject>(manifestEntries.Count);
        foreach (var pair in manifestEntries.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var archiveKey = context.Descriptor.Layout.ToLiveObjectKey(pair.Key);
            listedObjects.TryGetValue(pair.Key, out var archiveObjectInfo);
            if (archiveObjectInfo is null &&
                !await context.ArchiveStore.ExistsAsync(archiveKey, cancellationToken))
            {
                throw new YabtSyncException(
                    $"Change manifest restore object '{pair.Key}' is missing from the archive.");
            }

            var lastModifiedUtc = archiveObjectInfo?.LastModifiedUtc;
            if (ArchiveChangeFingerprint.TryParse(
                    pair.Value.ChangeFingerprint,
                    out _,
                    out var sourceLastModifiedUtc))
            {
                lastModifiedUtc = sourceLastModifiedUtc;
            }

            var contentHash = pair.Value.ContentHash ??
                throw new YabtSyncException(
                    $"Restore object '{pair.Key}' has no content hash.");
            restoreObjects.Add(new
            (
                pair.Key,
                currentCancellationToken => context.ArchiveStore.OpenReadAsync(
                    archiveKey,
                    currentCancellationToken),
                archiveObjectInfo?.ContentLength ?? pair.Value.ArtifactLength,
                lastModifiedUtc,
                contentHash,
                pair.Value.ChangeFingerprint,
                pair.Value.Projection
            ));
        }

        return restoreObjects;
    }

    private async Task<RestorePlan> CreateRestorePlanAsync
    (
        RestoreContext context,
        IReadOnlyList<ArchiveProjectedObject> restoreObjects,
        FileSystemRestoreTarget restoreTarget,
        bool useProjectionProvenance,
        CancellationToken cancellationToken
    )
    {
        var pathComparer = OperatingSystem.IsWindows() ?
            StringComparer.OrdinalIgnoreCase :
            StringComparer.Ordinal;
        var filePaths = new HashSet<string>(pathComparer);
        var directoryPaths = new Dictionary<string, string>(pathComparer);
        var files = new List<ArchiveProjectedObject>();
        var projections = new List<ArchiveRestoreProjection>();
        var rootFormatHandler = context.RootFormatHandler ??
            throw new YabtSyncException("Restore root format handler was not resolved.");

        try
        {
            void AddProjectionDirectories(ArchiveRestoreProjection projection)
            {
                foreach (var directoryPath in projection.Directories)
                {
                    AddRestoreDirectory(
                        directoryPath,
                        restoreTarget,
                        filePaths,
                        directoryPaths);
                }
            }

            void AddFinalRestoreObject
            (
                string formatName,
                ArchiveProjectedObject projectedObject
            )
            {
                if (!ArchiveHash.IsValid(projectedObject.ContentHash))
                {
                    throw new YabtSyncException(
                        $"Archive format handler '{formatName}' produced restore object " +
                            $"'{projectedObject.RelativePath}' without a valid content hash.");
                }

                var relativePath = ArchiveLayout.NormalizeObjectKey(
                    projectedObject.RelativePath);
                AddRestoreFilePath(
                    relativePath,
                    restoreTarget,
                    filePaths,
                    directoryPaths);
                files.Add(projectedObject with
                {
                    RelativePath = relativePath,
                    Projection = null,
                });
            }

            async Task<ArchiveRestoreProjection> ProjectArtifactsAsync
            (
                IArchiveFormatHandler formatHandler,
                IEnumerable<ArchiveProjectedObject> artifacts,
                bool restoreAsRoot,
                bool requireCompleteProjection = false
            )
            {
                var artifactSnapshot = artifacts.ToArray();
                try
                {
                    var projection = await formatHandler.ProjectRestoreAsync
                    (
                        new ArchiveRestoreRequest(
                            artifactSnapshot,
                            restoreAsRoot,
                            requireCompleteProjection),
                        cancellationToken
                    );
                    projections.Add(projection);
                    AddProjectionDirectories(projection);
                    return projection;
                }
                catch (Exception ex)
                {
                    if (ex is YabtSyncException) { throw; }

                    var artifactDescription = string.Join(
                        ", ",
                        artifactSnapshot.Select(artifact => $"'{artifact.RelativePath}'"));
                    throw new YabtSyncException(
                        $"Archive format handler '{formatHandler.FormatName}' could not restore " +
                            $"artifact(s) {artifactDescription}: {ex.Message}",
                        ex);
                }
            }

            if (useProjectionProvenance)
            {
                var processedProjectionKeys = new HashSet<RestoreProjectionKey>();

                async Task ProcessObjectsAsync
                (
                    IEnumerable<ArchiveProjectedObject> projectedObjects,
                    bool initialLevel
                )
                {
                    var objectSnapshot = projectedObjects.ToArray();
                    var plainObjects = objectSnapshot
                        .Where(projectedObject => projectedObject.Projection is null)
                        .ToArray();
                    var projectionGroups = objectSnapshot
                        .Where(projectedObject => projectedObject.Projection is not null)
                        .GroupBy(projectedObject =>
                        {
                            var provenance = projectedObject.Projection ??
                                throw new YabtSyncException(
                                    "A grouped restore artifact lost its projection provenance.");
                            return new RestoreProjectionKey(
                                provenance.LogicalPath,
                                provenance.Format,
                                provenance.FormatVersion,
                                provenance.ProjectionId);
                        })
                        .OrderBy(group => group.Key.LogicalPath, StringComparer.Ordinal)
                        .ThenBy(group => group.Key.Format, StringComparer.Ordinal)
                        .ToArray();

                    if (initialLevel && context.RootIsPackaged)
                    {
                        if (plainObjects.Length != 0 ||
                            projectionGroups.Length != 1 ||
                            !string.IsNullOrEmpty(projectionGroups[0].Key.LogicalPath) ||
                            !string.Equals(
                                projectionGroups[0].Key.Format,
                                rootFormatHandler.FormatName,
                                StringComparison.Ordinal))
                        {
                            throw new YabtSyncException(
                                "The live archive does not contain one unambiguous packaged-root " +
                                    "projection. Run backup with the current YABT version before restoring.");
                        }
                    }
                    else if (initialLevel)
                    {
                        foreach (var projectionGroup in projectionGroups)
                        {
                            if (string.IsNullOrEmpty(projectionGroup.Key.LogicalPath))
                            {
                                throw new YabtSyncException(
                                    "A child archive projection must have a nonempty logical path.");
                            }
                        }
                    }

                    foreach (var plainObject in plainObjects)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!initialLevel)
                        {
                            AddFinalRestoreObject(rootFormatHandler.FormatName, plainObject);
                            continue;
                        }

                        var projection = await ProjectArtifactsAsync(
                            rootFormatHandler,
                            [plainObject],
                            restoreAsRoot: context.RootIsPackaged);
                        await ProcessObjectsAsync(
                            projection.Objects,
                            initialLevel: false);
                    }

                    foreach (var projectionGroup in projectionGroups)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var projectionKey = projectionGroup.Key;
                        if (!processedProjectionKeys.Add(projectionKey))
                        {
                            throw new YabtSyncException(
                                $"Archive projection '{projectionKey.LogicalPath}' with id " +
                                    $"'{projectionKey.ProjectionId}' was encountered more than once.");
                        }

                        if (!_formatHandlers.TryGetValue(
                                projectionKey.Format,
                                out var formatHandler))
                        {
                            throw new YabtSyncException(
                                $"No archive format handler is registered for durable format " +
                                    $"'{projectionKey.Format}'.");
                        }

                        var artifacts = projectionGroup
                            .OrderBy(artifact => artifact.Projection?.ArtifactRole, StringComparer.Ordinal)
                            .ToArray();
                        var duplicateRole = artifacts
                            .GroupBy(
                                artifact => artifact.Projection?.ArtifactRole,
                                StringComparer.Ordinal)
                            .FirstOrDefault(roleGroup => roleGroup.Count() > 1);
                        if (duplicateRole is not null)
                        {
                            throw new YabtSyncException(
                                $"Archive projection '{projectionKey.LogicalPath}' contains duplicate " +
                                    $"artifact role '{duplicateRole.Key}'.");
                        }

                        var restoreAsRoot = initialLevel && context.RootIsPackaged;
                        var projection = await ProjectArtifactsAsync(
                            formatHandler,
                            artifacts,
                            restoreAsRoot,
                            requireCompleteProjection: true);
                        await ProcessObjectsAsync(
                            projection.Objects,
                            initialLevel: false);
                    }
                }

                await ProcessObjectsAsync(restoreObjects, initialLevel: true);
            }
            else
            {
                if (context.RootIsPackaged &&
                    (restoreObjects.Count != 1 ||
                        !rootFormatHandler.CanRestoreArtifact(restoreObjects[0]) ||
                        !string.IsNullOrEmpty(GetParentPrefix(restoreObjects[0].RelativePath))))
                {
                    throw new YabtSyncException(
                        "The current root policy describes a packaged root, but the live archive " +
                            "does not contain exactly one root package. Back up the source before restoring it.");
                }

                foreach (var restoreObject in restoreObjects)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var formatHandler = ResolveRestoreArtifactFormatHandler(
                        rootFormatHandler,
                        restoreObject,
                        context.RootIsPackaged);
                    var projection = await ProjectArtifactsAsync(
                        formatHandler,
                        [restoreObject],
                        context.RootIsPackaged);
                    foreach (var projectedObject in projection.Objects)
                    {
                        AddFinalRestoreObject(formatHandler.FormatName, projectedObject);
                    }
                }
            }

            var directories = directoryPaths.Values
                .OrderBy(GetPathDepth)
                .ThenBy(directoryPath => directoryPath, StringComparer.Ordinal)
                .ToArray();
            var nonemptyDirectories = new HashSet<string>(pathComparer);
            foreach (var file in files)
            {
                var parentPath = GetParentPrefix(file.RelativePath);
                while (!string.IsNullOrEmpty(parentPath))
                {
                    nonemptyDirectories.Add(parentPath);
                    parentPath = GetParentPrefix(parentPath);
                }
            }

            var emptyDirectories = directoryPaths.Values
                .Where(directoryPath => !nonemptyDirectories.Contains(directoryPath))
                .OrderBy(GetPathDepth)
                .ThenBy(directoryPath => directoryPath, StringComparer.Ordinal)
                .ToArray();
            return new(files, directories, emptyDirectories, projections);
        }
        catch (Exception)
        {
            foreach (var projection in projections)
            {
                await projection.DisposeAsync();
            }

            throw;
        }
    }

    private IArchiveFormatHandler ResolveRestoreArtifactFormatHandler
    (
        IArchiveFormatHandler rootFormatHandler,
        ArchiveProjectedObject restoreObject,
        bool restoreAsRoot
    )
    {
        if (restoreAsRoot)
        {
            if (!rootFormatHandler.CanRestoreArtifact(restoreObject))
            {
                throw new YabtSyncException(
                    $"Root format handler '{rootFormatHandler.FormatName}' does not recognize " +
                        $"archive artifact '{restoreObject.RelativePath}'.");
            }

            return rootFormatHandler;
        }

        var matchingHandlers = _formatHandlers.Values
            .Where(formatHandler =>
                formatHandler.ProjectsBesideSourceFolder &&
                formatHandler.CanRestoreArtifact(restoreObject))
            .ToArray();
        if (matchingHandlers.Length > 1)
        {
            throw new YabtSyncException(
                $"Archive artifact '{restoreObject.RelativePath}' is claimed by multiple " +
                    "format handlers.");
        }

        if (matchingHandlers.Length == 1)
        {
            return matchingHandlers[0];
        }

        if (!rootFormatHandler.CanRestoreArtifact(restoreObject))
        {
            throw new YabtSyncException(
                $"No archive format handler recognizes artifact '{restoreObject.RelativePath}'.");
        }

        return rootFormatHandler;
    }

    private static FileStream CreateRestoreStagingFileStream
    (
        string path,
        FileAccess access
    )
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = access,
            Share = FileShare.None,
            BufferSize = DefaultBufferSize,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return new(path, options);
    }

    private static FileStream OpenRestoreStagingFileStream(string path) => new
    (
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.None,
        DefaultBufferSize,
        FileOptions.Asynchronous | FileOptions.SequentialScan
    );

    private static void TryDeleteRestoreTemporaryPath
    (
        ILogger logger,
        string path,
        Action delete
    )
    {
        logger.LogTrace(nameof(TryDeleteRestoreTemporaryPath));

        try
        {
            delete();
        }
        catch (Exception ex)
        {
            logger.LogIgnoringRestoreTemporaryPathDeleteException(ex, path);
        }
    }

    private static void AddRestoreFilePath
    (
        string relativePath,
        FileSystemRestoreTarget restoreTarget,
        HashSet<string> filePaths,
        Dictionary<string, string> directoryPaths
    )
    {
        restoreTarget.ValidateRelativePath(relativePath);
        if (!filePaths.Add(relativePath) || directoryPaths.ContainsKey(relativePath))
        {
            throw new YabtSyncException(
                $"Multiple archive items restore to the same path '{relativePath}'.");
        }

        var parentPath = GetParentPrefix(relativePath);
        while (!string.IsNullOrEmpty(parentPath))
        {
            if (filePaths.Contains(parentPath))
            {
                throw new YabtSyncException(
                    $"Restore file '{relativePath}' conflicts with file '{parentPath}'.");
            }

            AddRestoreDirectoryPath(parentPath, directoryPaths);
            parentPath = GetParentPrefix(parentPath);
        }
    }

    private static void AddRestoreDirectory
    (
        string relativePath,
        FileSystemRestoreTarget restoreTarget,
        HashSet<string> filePaths,
        Dictionary<string, string> directoryPaths
    )
    {
        if (string.IsNullOrEmpty(relativePath)) { return; }

        restoreTarget.ValidateRelativePath(relativePath);
        if (filePaths.Contains(relativePath))
        {
            throw new YabtSyncException(
                $"Restore directory '{relativePath}' conflicts with a file at the same path.");
        }

        AddRestoreDirectoryPath(relativePath, directoryPaths);
        var parentPath = GetParentPrefix(relativePath);
        while (!string.IsNullOrEmpty(parentPath))
        {
            if (filePaths.Contains(parentPath))
            {
                throw new YabtSyncException(
                    $"Restore directory '{relativePath}' conflicts with file '{parentPath}'.");
            }

            AddRestoreDirectoryPath(parentPath, directoryPaths);
            parentPath = GetParentPrefix(parentPath);
        }
    }

    private static void AddRestoreDirectoryPath
    (
        string directoryPath,
        Dictionary<string, string> directoryPaths
    )
    {
        if (!directoryPaths.TryGetValue(directoryPath, out var existingDirectoryPath))
        {
            directoryPaths.Add(directoryPath, directoryPath);
            return;
        }

        if (!string.Equals(
                existingDirectoryPath,
                directoryPath,
                StringComparison.Ordinal))
        {
            throw new YabtSyncException(
                $"Restore directory paths '{existingDirectoryPath}' and '{directoryPath}' " +
                    "differ only by case and cannot both be represented on this filesystem.");
        }
    }

    private static async Task<RestoreReconciliation> CreateRestoreReconciliationAsync
    (
        IObjectStore destinationStore,
        ArchiveLayout destinationLayout,
        RestorePlan restorePlan,
        ArchiveLogicalStateManifest? logicalStateManifest,
        bool byteForByte,
        CancellationToken cancellationToken
    )
    {
        var pathComparer = OperatingSystem.IsWindows() ?
            StringComparer.OrdinalIgnoreCase :
            StringComparer.Ordinal;
        var destinationState = await LoadRestoreDestinationStateAsync
        (
            destinationStore,
            destinationLayout,
            pathComparer,
            cancellationToken
        );
        var desiredFiles = restorePlan.Files.ToDictionary(
            file => file.RelativePath,
            pathComparer);
        var desiredDirectories = restorePlan.Directories.ToHashSet(pathComparer);
        var desiredEmptyDirectories = restorePlan.EmptyDirectories.ToHashSet(pathComparer);
        var filesToWrite = restorePlan.Files.ToHashSet();
        var logicalStateEntries = new Dictionary<string, ArchiveLogicalStateManifestEntry>(
            pathComparer);
        if (logicalStateManifest is not null)
        {
            foreach (var logicalStateEntry in logicalStateManifest.Entries)
            {
                if (!logicalStateEntries.TryAdd(
                        logicalStateEntry.LogicalRelativePath,
                        logicalStateEntry))
                {
                    throw new YabtSyncException(
                        "Logical-state manifest paths cannot be represented distinctly on " +
                            $"this filesystem: '{logicalStateEntry.LogicalRelativePath}'.");
                }
            }
        }
        var historyMoves = new List<RestoreHistoryMove>();
        var changedDesiredPaths = new HashSet<string>(pathComparer);
        var unchangedDesiredPaths = new HashSet<string>(pathComparer);
        var extraCount = 0;

        var folderMoves = new List<RestoreHistoryMove>();
        var movedFolderPaths = new HashSet<string>(pathComparer);
        var orderedDestinationDirectories = destinationState.Directories
            .OrderBy(GetPathDepth)
            .ThenBy(path => path, StringComparer.Ordinal);
        foreach (var directoryPath in orderedDestinationDirectories)
        {
            if (IsUnderRestoreFolderMove(directoryPath, movedFolderPaths))
            {
                continue;
            }

            if (desiredFiles.ContainsKey(directoryPath))
            {
                folderMoves.Add(new(directoryPath, IsFolder: true));
                movedFolderPaths.Add(directoryPath);
                changedDesiredPaths.Add(directoryPath);
                continue;
            }

            if (!desiredDirectories.Contains(directoryPath))
            {
                folderMoves.Add(new(directoryPath, IsFolder: true));
                movedFolderPaths.Add(directoryPath);
                extraCount++;
            }
        }

        historyMoves.AddRange(folderMoves);
        var remainingDestinationFiles = destinationState.Files.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            pathComparer);
        var changedDestinationFilePaths = new HashSet<string>(pathComparer);

        Task<RestoreDestinationFile?> TakeExistingRestoreObjectAsync
        (
            ArchiveProjectedObject desiredObject,
            string relativePath,
            CancellationToken currentCancellationToken
        )
        {
            _ = desiredObject;
            currentCancellationToken.ThrowIfCancellationRequested();
            if (IsUnderRestoreFolderMove(relativePath, movedFolderPaths))
            {
                return Task.FromResult<RestoreDestinationFile?>(null);
            }

            remainingDestinationFiles.Remove(relativePath, out var destinationFile);
            return Task.FromResult(destinationFile);
        }

        async Task ConsumeRestoreObjectAsync
        (
            ArchiveProjectedObject desiredObject,
            string relativePath,
            RestoreDestinationFile? destinationFile,
            CancellationToken currentCancellationToken
        )
        {
            _ = desiredObject;
            if (destinationFile is null) { return; }

            var desiredFile = desiredFiles[relativePath];
            if (!byteForByte &&
                logicalStateEntries.TryGetValue(relativePath, out var logicalStateEntry) &&
                ArchiveChangeFingerprint.TryCreate(
                    destinationFile.ContentLength,
                    destinationFile.LastModifiedUtc,
                    out var currentStatFingerprint) &&
                string.Equals(
                    currentStatFingerprint,
                    logicalStateEntry.StatFingerprint,
                    StringComparison.Ordinal) &&
                string.Equals(
                    desiredFile.ContentHash,
                    logicalStateEntry.ContentHash,
                    StringComparison.Ordinal))
            {
                filesToWrite.Remove(desiredFile);
                unchangedDesiredPaths.Add(relativePath);
                return;
            }

            var destinationHash = await ComputeStoredObjectHashAsync(
                destinationStore,
                destinationFile.ArchiveKey,
                currentCancellationToken);
            if (string.Equals(
                    destinationHash,
                    desiredFile.ContentHash,
                    StringComparison.Ordinal))
            {
                filesToWrite.Remove(desiredFile);
                unchangedDesiredPaths.Add(relativePath);
                return;
            }

            changedDestinationFilePaths.Add(relativePath);
            changedDesiredPaths.Add(relativePath);
        }

        await ArchiveReconciler.ReconcileAsync
        (
            restorePlan.Files,
            restorePlan.Directories,
            pathComparer,
            static _ => { },
            TakeExistingRestoreObjectAsync,
            ConsumeRestoreObjectAsync,
            cancellationToken
        );

        var orderedDestinationFiles = destinationState.Files.Values
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal);
        foreach (var destinationFile in orderedDestinationFiles)
        {
            if (IsUnderRestoreFolderMove(destinationFile.RelativePath, movedFolderPaths))
            {
                continue;
            }

            if (changedDestinationFilePaths.Contains(destinationFile.RelativePath))
            {
                historyMoves.Add(new(destinationFile.RelativePath, IsFolder: false));
                continue;
            }

            if (!remainingDestinationFiles.ContainsKey(destinationFile.RelativePath))
            {
                continue;
            }

            historyMoves.Add(new(destinationFile.RelativePath, IsFolder: false));
            if (desiredDirectories.Contains(destinationFile.RelativePath))
            {
                changedDesiredPaths.Add(destinationFile.RelativePath);
            }
            else
            {
                extraCount++;
            }
        }

        foreach (var emptyDirectory in desiredEmptyDirectories)
        {
            if (changedDesiredPaths.Contains(emptyDirectory)) { continue; }

            if (destinationState.Directories.Contains(emptyDirectory))
            {
                unchangedDesiredPaths.Add(emptyDirectory);
            }
        }

        var newCount = desiredFiles.Keys.Count(path =>
                !changedDesiredPaths.Contains(path) &&
                !unchangedDesiredPaths.Contains(path)) +
            desiredEmptyDirectories.Count(path =>
                !changedDesiredPaths.Contains(path) &&
                !unchangedDesiredPaths.Contains(path));

        return new
        (
            filesToWrite
                .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
                .ToArray(),
            historyMoves,
            NewCount: newCount,
            ChangedCount: changedDesiredPaths.Count,
            ExtraCount: extraCount,
            UnchangedCount: unchangedDesiredPaths.Count
        );
    }

    private static bool IsUnderRestoreFolderMove
    (
        string relativePath,
        HashSet<string> movedFolderPaths
    )
    {
        var candidatePath = relativePath;
        while (!string.IsNullOrEmpty(candidatePath))
        {
            if (movedFolderPaths.Contains(candidatePath)) { return true; }

            candidatePath = GetParentPrefix(candidatePath);
        }

        return false;
    }

    private static void ValidateRestorePlanInternalPaths
    (
        RestorePlan restorePlan,
        ArchiveLayout destinationLayout,
        FileSystemRestoreTarget restoreTarget
    )
    {
        foreach (var file in restorePlan.Files)
        {
            var destinationPath = destinationLayout.ToLiveObjectKey(file.RelativePath);
            restoreTarget.ValidateRelativePath(destinationPath);
            if (IsInternalObject(destinationPath, destinationLayout) ||
                IsStrictPrefixAncestor(destinationPath, destinationLayout.HistPrefix))
            {
                throw new YabtSyncException(
                    $"Restore path '{file.RelativePath}' conflicts with destination history, " +
                        "metadata, or provider plumbing.");
            }
        }

        foreach (var directoryPath in restorePlan.Directories)
        {
            var destinationPath = destinationLayout.ToLiveObjectKey(directoryPath);
            restoreTarget.ValidateRelativePath(destinationPath);
            if (IsInternalObject(destinationPath, destinationLayout))
            {
                throw new YabtSyncException(
                    $"Restore folder '{directoryPath}' conflicts with destination history or " +
                        "provider plumbing.");
            }
        }
    }

    private static void ValidateRestoreLayoutPaths
    (
        ArchiveLayout destinationLayout,
        FileSystemRestoreTarget restoreTarget
    )
    {
        var livePrefix = ArchiveLayout.NormalizeObjectPrefix(destinationLayout.LivePrefix);
        if (livePrefix is not null)
        {
            restoreTarget.ValidateRelativePath(livePrefix);
        }

        restoreTarget.ValidateRelativePath(destinationLayout.HistPrefix);
    }

    private static string ResolvePhysicalPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        try
        {
            var rootPath = Path.GetPathRoot(fullPath) ??
                throw new IOException($"Path '{fullPath}' does not have a filesystem root.");
            var relativePath = Path.GetRelativePath(rootPath, fullPath);
            var segments = relativePath.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
            var currentPath = rootPath;
            for (var segmentIndex = 0; segmentIndex < segments.Length; segmentIndex++)
            {
                var candidatePath = Path.Combine(currentPath, segments[segmentIndex]);
                FileSystemInfo? fileSystemInfo = Directory.Exists(candidatePath) ?
                    new DirectoryInfo(candidatePath) :
                    File.Exists(candidatePath) ?
                        new FileInfo(candidatePath) :
                        null;
                if (fileSystemInfo is null)
                {
                    for (; segmentIndex < segments.Length; segmentIndex++)
                    {
                        currentPath = Path.Combine(currentPath, segments[segmentIndex]);
                    }

                    break;
                }

                if ((fileSystemInfo.Attributes & FileAttributes.ReparsePoint) == 0)
                {
                    currentPath = candidatePath;
                    continue;
                }

                var linkTarget = fileSystemInfo.ResolveLinkTarget(returnFinalTarget: true) ??
                    throw new IOException(
                        $"Reparse point '{candidatePath}' does not expose a resolvable target.");
                currentPath = Path.GetFullPath(linkTarget.FullName);
            }

            return Path.GetFullPath(currentPath);
        }
        catch (Exception ex)
        {
            throw new YabtSyncException(
                $"Restore could not safely resolve filesystem path '{fullPath}'.",
                ex);
        }
    }

    private static async Task<RestoreDestinationState> LoadRestoreDestinationStateAsync
    (
        IObjectStore destinationStore,
        ArchiveLayout destinationLayout,
        StringComparer pathComparer,
        CancellationToken cancellationToken
    )
    {
        var files = new Dictionary<string, RestoreDestinationFile>(pathComparer);
        var directories = new HashSet<string>(pathComparer);

        async Task LoadFolderAsync(string relativeFolderPath)
        {
            var folderKey = destinationLayout.ToLiveObjectKey(relativeFolderPath);
            var folderItems = destinationStore.GetFolderItemsAsync(
                folderKey,
                recursive: false,
                cancellationToken);
            await foreach (var item in folderItems)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var archiveKey = ArchiveLayout.NormalizeObjectKey(item.Key);
                if (IsInternalObject(archiveKey, destinationLayout)) { continue; }

                var relativePath = ArchiveLayout.RemovePrefix(
                    archiveKey,
                    destinationLayout.LivePrefix);
                if (item.IsFolder)
                {
                    if (!directories.Add(relativePath))
                    {
                        throw new YabtSyncException(
                            $"Restore destination folder '{relativePath}' was listed more than once.");
                    }

                    await LoadFolderAsync(relativePath);
                    continue;
                }

                if (item.Object is not null &&
                    !files.TryAdd(
                        relativePath,
                        new(
                            relativePath,
                            archiveKey,
                            item.Object.ContentLength,
                            item.Object.LastModifiedUtc)))
                {
                    throw new YabtSyncException(
                        $"Restore destination file '{relativePath}' was listed more than once.");
                }
            }
        }

        await LoadFolderAsync(string.Empty);
        return new(files, directories);
    }

    private async Task<StagedRestoreFiles> StageRestoreFilesAsync
    (
        IEnumerable<ArchiveProjectedObject> files,
        CancellationToken cancellationToken
    )
    {
        var stagingRootPath = Path.Combine(
            Path.GetTempPath(),
            $"yabt-restore-{Guid.NewGuid():N}");
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(stagingRootPath);
            }
            else
            {
                Directory.CreateDirectory(
                    stagingRootPath,
                    UnixFileMode.UserRead |
                        UnixFileMode.UserWrite |
                        UnixFileMode.UserExecute);
            }
        }
        catch (Exception ex)
        {
            throw new YabtSyncException(
                $"Restore staging folder '{stagingRootPath}' could not be created.",
                ex);
        }

        var stagedFiles = new StagedRestoreFiles(stagingRootPath, _logger);
        try
        {
            async Task StageFileAsync(ArchiveProjectedObject file, Stream source)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stagingPath = Path.Combine(
                    stagingRootPath,
                    $"{Guid.NewGuid():N}.tmp");
                stagedFiles.Add(file.RelativePath, stagingPath);
                try
                {
                    await using var stagedContent = CreateRestoreStagingFileStream(
                        stagingPath,
                        FileAccess.Write);
                    await CopyRestoreContentToStagingAsync(
                        file,
                        source,
                        stagedContent,
                        cancellationToken);
                    await stagedContent.FlushAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    if (ex is YabtSyncException) { throw; }

                    throw new YabtSyncException(
                        $"Archive object for restore path '{file.RelativePath}' could not be staged.",
                        ex);
                }
            }

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await using var projectedContent = await file.OpenContentAsync(
                        cancellationToken);
                    await StageFileAsync(file, projectedContent.Content);
                }
                catch (Exception ex)
                {
                    if (ex is YabtSyncException) { throw; }

                    throw new YabtSyncException(
                        $"Archive object for restore path '{file.RelativePath}' could not be staged.",
                        ex);
                }
            }

            return stagedFiles;
        }
        catch (Exception)
        {
            await stagedFiles.DisposeAsync();
            throw;
        }
    }

    private static async Task CopyRestoreContentToStagingAsync
    (
        ArchiveProjectedObject file,
        Stream source,
        Stream destination,
        CancellationToken cancellationToken
    )
    {
        var hash = new XxHash128();
        long contentLength = 0;
        var buffer = new byte[DefaultBufferSize];
        while (true)
        {
            var bytesRead = await source.ReadAsync(buffer, cancellationToken);
            if (bytesRead == 0) { break; }

            hash.Append(buffer.AsSpan(0, bytesRead));
            contentLength += bytesRead;
            await destination.WriteAsync(
                buffer.AsMemory(0, bytesRead),
                cancellationToken);
        }

        if (file.ContentLength.HasValue && file.ContentLength.Value != contentLength)
        {
            throw new YabtSyncException(
                $"Archive object for restore path '{file.RelativePath}' has length {contentLength}, " +
                    $"but {file.ContentLength.Value} bytes were expected.");
        }

        var contentHash = ArchiveHash.Format(hash.GetHashAndReset());
        if (!string.Equals(file.ContentHash, contentHash, StringComparison.Ordinal))
        {
            throw new YabtSyncException(
                $"Archive object for restore path '{file.RelativePath}' failed its content hash check.");
        }
    }

    private async Task ApplyRestorePlanAsync
    (
        IArchiveMutableObjectStore destinationStore,
        ArchiveLayout destinationLayout,
        RestorePlan plan,
        RestoreReconciliation reconciliation,
        FileSystemRestoreTarget restoreTarget,
        LogicalStateManifestLoad logicalStateManifestLoad,
        RootDescriptorRestore rootDescriptorRestore,
        BackupRootDocument? archiveRootDocument,
        CancellationToken cancellationToken
    )
    {
        await using var stagedFiles = await StageRestoreFilesAsync(
            reconciliation.FilesToWrite,
            cancellationToken);
        var liveStateChanged = reconciliation.NewCount != 0 ||
            reconciliation.ChangedCount != 0 ||
            reconciliation.ExtraCount != 0;
        var changeManifestInvalidationMarkerActive =
            await PrepareRestoreChangeManifestMutationAsync(
                destinationStore,
                liveStateChanged || rootDescriptorRestore.NeedsWrite,
                cancellationToken);
        var logicalStateManifestInvalidationMarkerActive =
            logicalStateManifestLoad.InvalidationMarkerExists;
        if (liveStateChanged &&
            logicalStateManifestLoad.ManifestExists &&
            !logicalStateManifestInvalidationMarkerActive)
        {
            await UploadLogicalStateManifestInvalidationMarkerAsync(
                destinationStore,
                cancellationToken);
            logicalStateManifestInvalidationMarkerActive = true;
        }
        var historizer = new ArchiveHistorizer(
            destinationStore,
            destinationLayout,
            _timeProvider.GetUtcNow());
        await historizer.InspectRecoveryStateAsync(cancellationToken);
        foreach (var historyMove in reconciliation.HistoryMoves)
        {
            if (historyMove.IsFolder)
            {
                await historizer.MoveFolderAsync(
                    historyMove.RelativePath,
                    "Restore",
                    cancellationToken);
            }
            else
            {
                await historizer.MoveObjectAsync(
                    historyMove.RelativePath,
                    "Restore",
                    cancellationToken);
            }
        }

        await restoreTarget.CreateDirectoryAsync(
            destinationLayout.ToLiveObjectKey(string.Empty),
            cancellationToken);
        foreach (var directoryPath in plan.EmptyDirectories)
        {
            await restoreTarget.CreateDirectoryAsync(
                destinationLayout.ToLiveObjectKey(directoryPath),
                cancellationToken);
        }

        foreach (var file in reconciliation.FilesToWrite)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var stagedContent = stagedFiles.OpenContent(file.RelativePath);
            await restoreTarget.WriteFileAsync
            (
                destinationLayout.ToLiveObjectKey(file.RelativePath),
                stagedContent,
                file.ContentHash,
                file.ContentLength,
                file.LastModifiedUtc,
                cancellationToken
            );
        }

        if (rootDescriptorRestore.NeedsWrite)
        {
            if (archiveRootDocument is null)
            {
                throw new YabtSyncException(
                    "Restore planned a root descriptor write without preserved archive bytes.");
            }

            if (rootDescriptorRestore.MoveExistingToHistory)
            {
                await historizer.MoveRootObjectAsync(
                    BackupRootFileNames.Primary,
                    "Restore",
                    cancellationToken);
            }

            await UploadRootDocumentAsync(
                destinationStore,
                archiveRootDocument,
                cancellationToken);
        }

        await historizer.CompleteAsync(cancellationToken);

        var nextLogicalStateManifest = await CreateLogicalStateManifestAsync
        (
            destinationStore,
            destinationLayout,
            plan,
            cancellationToken
        );
        var logicalStateManifestNeedsWrite =
            logicalStateManifestInvalidationMarkerActive ||
            logicalStateManifestLoad.Manifest is null ||
            !string.Equals(
                logicalStateManifestLoad.Manifest.ManifestHash,
                nextLogicalStateManifest.ManifestHash,
                StringComparison.Ordinal);
        if (logicalStateManifestNeedsWrite)
        {
            if (logicalStateManifestLoad.ManifestExists &&
                !logicalStateManifestInvalidationMarkerActive)
            {
                await UploadLogicalStateManifestInvalidationMarkerAsync(
                    destinationStore,
                    cancellationToken);
                logicalStateManifestInvalidationMarkerActive = true;
            }

            if (logicalStateManifestLoad.ManifestExists)
            {
                await DeleteInternalObjectAsync(
                    destinationStore,
                    ArchiveLogicalStateManifest.FileName,
                    "Obsolete logical state manifest",
                    cancellationToken);
            }

            await UploadLogicalStateManifestAsync(
                destinationStore,
                nextLogicalStateManifest,
                cancellationToken);
        }

        if (logicalStateManifestInvalidationMarkerActive)
        {
            await DeleteInternalObjectAsync(
                destinationStore,
                ArchiveLogicalStateManifest.InvalidationMarkerFileName,
                "Logical state manifest invalidation marker",
                cancellationToken);
        }

        if (changeManifestInvalidationMarkerActive)
        {
            await DeleteInternalObjectAsync(
                destinationStore,
                ArchiveChangeManifest.InvalidationMarkerFileName,
                "Change manifest invalidation marker",
                cancellationToken);
        }
    }

    private static async Task<bool> PrepareRestoreChangeManifestMutationAsync
    (
        IArchiveMutableObjectStore destinationStore,
        bool liveStateChanged,
        CancellationToken cancellationToken
    )
    {
        var invalidationMarkerExists = await destinationStore.ExistsAsync(
            ArchiveChangeManifest.InvalidationMarkerFileName,
            cancellationToken);
        if (!liveStateChanged && !invalidationMarkerExists)
        {
            return false;
        }

        var existingManifestFileNames = new List<string>();
        foreach (var manifestFileName in GetChangeManifestFileNames())
        {
            if (await destinationStore.ExistsAsync(
                    manifestFileName,
                    cancellationToken))
            {
                existingManifestFileNames.Add(manifestFileName);
            }
        }

        if (existingManifestFileNames.Count != 0 && !invalidationMarkerExists)
        {
            await UploadChangeManifestInvalidationMarkerAsync(
                destinationStore,
                cancellationToken);
            invalidationMarkerExists = true;
        }

        await DeleteChangeManifestsAsync(
            destinationStore,
            existingManifestFileNames,
            cancellationToken);
        return invalidationMarkerExists;
    }

    private static async Task ValidateRestorePlanContentAsync
    (
        IEnumerable<ArchiveProjectedObject> files,
        CancellationToken cancellationToken
    )
    {
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var content = await file.OpenContentAsync(cancellationToken);
                await CopyRestoreContentToStagingAsync(
                    file,
                    content.Content,
                    Stream.Null,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                if (ex is YabtSyncException) { throw; }

                throw new YabtSyncException(
                    $"Archive object for restore path '{file.RelativePath}' failed its " +
                        "byte-for-byte validation.",
                    ex);
            }
        }
    }

    private async Task<LogicalStateManifestLoad> ReadLogicalStateManifestAsync
    (
        IObjectStore destinationStore,
        CancellationToken cancellationToken
    )
    {
        var markerExists = await destinationStore.ExistsAsync(
            ArchiveLogicalStateManifest.InvalidationMarkerFileName,
            cancellationToken);
        var manifestExists = await destinationStore.ExistsAsync(
            ArchiveLogicalStateManifest.FileName,
            cancellationToken);
        if (markerExists || !manifestExists)
        {
            return new(manifestExists, markerExists, Manifest: null);
        }

        try
        {
            await using var content = await destinationStore.OpenReadAsync(
                ArchiveLogicalStateManifest.FileName,
                cancellationToken);
            var manifest = await _logicalStateManifestSerializer.ReadAsync(
                content.Content,
                cancellationToken);
            return new(manifestExists, markerExists, manifest);
        }
        catch (Exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(manifestExists, markerExists, Manifest: null);
        }
    }

    private async Task<ArchiveLogicalStateManifest> CreateLogicalStateManifestAsync
    (
        IObjectStore destinationStore,
        ArchiveLayout destinationLayout,
        RestorePlan plan,
        CancellationToken cancellationToken
    )
    {
        var pathComparer = OperatingSystem.IsWindows() ?
            StringComparer.OrdinalIgnoreCase :
            StringComparer.Ordinal;
        var destinationState = await LoadRestoreDestinationStateAsync
        (
            destinationStore,
            destinationLayout,
            pathComparer,
            cancellationToken
        );
        var entries = new List<ArchiveLogicalStateManifestEntry>(plan.Files.Count);
        foreach (var file in plan.Files)
        {
            var contentHash = file.ContentHash;
            if (!destinationState.Files.TryGetValue(file.RelativePath, out var destinationFile) ||
                !ArchiveChangeFingerprint.TryCreate(
                    destinationFile.ContentLength,
                    destinationFile.LastModifiedUtc,
                    out var statFingerprint) ||
                contentHash is null ||
                !ArchiveHash.IsValid(contentHash))
            {
                throw new YabtSyncException(
                    $"Restore destination file '{file.RelativePath}' does not provide complete " +
                        "logical-state manifest evidence after restore.");
            }

            entries.Add(new(
                file.RelativePath,
                statFingerprint,
                contentHash));
        }

        return _logicalStateManifestSerializer.Create(entries);
    }

    private async Task UploadLogicalStateManifestAsync
    (
        IObjectStore destinationStore,
        ArchiveLogicalStateManifest manifest,
        CancellationToken cancellationToken
    )
    {
        using var content = new MemoryStream();
        await _logicalStateManifestSerializer.WriteAsync(
            manifest,
            content,
            cancellationToken);
        content.Position = 0;
        await destinationStore.UploadAsync(
            ArchiveLogicalStateManifest.FileName,
            content,
            "application/json",
            EmptyMetadata,
            cancellationToken);
    }

    private static async Task UploadLogicalStateManifestInvalidationMarkerAsync
    (
        IObjectStore destinationStore,
        CancellationToken cancellationToken
    )
    {
        await using var markerContent = new MemoryStream(
            LogicalStateManifestInvalidationMarkerContent,
            writable: false);
        await destinationStore.UploadAsync(
            ArchiveLogicalStateManifest.InvalidationMarkerFileName,
            markerContent,
            "application/json",
            EmptyMetadata,
            cancellationToken);
    }

    public Task<SyncRunResult> ScanAsync
    (
        SyncRunRequest request,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(ScanAsync));

        _ = request;
        cancellationToken.ThrowIfCancellationRequested();

        //TODO: Add scalable change detection abstractions before implementing real scan output.
        return Task.FromResult(new SyncRunResult
        (
            Completed: true,
            Message: "Scan completed as a no-op placeholder; change detection is not implemented yet."
        ));
    }

    public async Task<SyncRunResult> VerifyAsync
    (
        SyncRunRequest request,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(VerifyAsync));

        var context = await CreateContextAsync(request, cancellationToken);

        return await ApplyProjectionAsync(
            context,
            writeChanges: false,
            verifyOnly: true,
            byteForByte: request.ByteForByte,
            operationName: "verify",
            cancellationToken);
    }

    public Task<SyncRunResult> PackAsync
    (
        SyncRunRequest request,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(PackAsync));

        _ = request;
        cancellationToken.ThrowIfCancellationRequested();

        //TODO: Decide whether pack writes package artifacts locally, to a target store, or only previews projection output.
        return Task.FromResult(new SyncRunResult
        (
            Completed: false,
            Message: "Pack command wiring is implemented, but standalone pack semantics are still intentionally open."
        ));
    }

    public Task<SyncRunResult> ReconcileAsync
    (
        SyncRunRequest request,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(ReconcileAsync));

        _ = request;
        cancellationToken.ThrowIfCancellationRequested();

        //TODO: Define bidirectional reconciliation conflict handling before mutating either side.
        return Task.FromResult(new SyncRunResult
        (
            Completed: false,
            Message: "Reconcile command wiring is implemented, but conflict semantics are still intentionally open."
        ));
    }

    private async Task<ArchiveSyncContext> CreateContextAsync
    (
        SyncRunRequest request,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        var sourceRootPath = Path.GetFullPath(request.SourceRoot);
        var sourceLocation = await _backupRootLocator.LocateRootAsync(
            sourceRootPath,
            cancellationToken);
        var sourceDescriptor = sourceLocation.Descriptor;
        ValidateInternalLayoutPrefixes(sourceDescriptor.Layout);
        var targetStoreConfiguration = GetTargetStoreConfiguration(
            sourceDescriptor,
            request.TargetStoreId);

        if (!_storeResolvers.TryGetValue(targetStoreConfiguration.Kind, out var targetStoreResolver))
        {
            throw new YabtSyncException(
                $"No object store resolver is registered for store kind '{targetStoreConfiguration.Kind}'.");
        }

        var policy = await _folderPolicyReader.ReadPolicyAsync(
            sourceRootPath,
            cancellationToken);
        if (!_formatHandlers.TryGetValue(policy.Format, out var formatHandler))
        {
            throw new YabtSyncException($"No archive format handler is registered for format '{policy.Format}'.");
        }

        var sourceStore = _sourceRootObjectStoreResolver.ResolveSourceRoot(sourceLocation.RootPath);
        var targetStore = targetStoreResolver.ResolveStore(
            targetStoreConfiguration,
            sourceLocation.RootPath);

        return new
        (
            sourceRootPath,
            CreateSourcePrefix(sourceRootPath, sourceLocation),
            sourceStore,
            targetStore,
            sourceDescriptor,
            sourceDescriptor,
            sourceLocation.Document,
            policy,
            formatHandler
        );
    }

    private BackupRootStore GetTargetStoreConfiguration
    (
        BackupRootDescriptor descriptor,
        string? requestedStoreId
    )
    {
        if (descriptor.Stores is null)
        {
            throw new YabtSyncException("Backup root descriptor does not define any target stores.");
        }

        var effectiveStoreId = string.IsNullOrWhiteSpace(requestedStoreId) ?
            descriptor.DefaultStoreId :
            requestedStoreId;
        if (!string.IsNullOrWhiteSpace(effectiveStoreId))
        {
            foreach (var store in descriptor.Stores)
            {
                if (string.Equals(store.Id, effectiveStoreId, StringComparison.OrdinalIgnoreCase))
                {
                    return store;
                }
            }

            throw new YabtSyncException(
                $"Backup root descriptor does not define target store '{effectiveStoreId}'.");
        }

        BackupRootStore? firstStore = null;
        var hasMultipleStores = false;
        foreach (var store in descriptor.Stores)
        {
            if (firstStore is null)
            {
                firstStore = store;
                continue;
            }

            hasMultipleStores = true;
        }

        if (firstStore is null)
        {
            throw new YabtSyncException("Backup root descriptor does not define any target stores.");
        }

        if (hasMultipleStores)
        {
            _logger.LogMultipleTargetStoresWithoutSelection(
                descriptor.ArchiveId,
                firstStore.Id);
        }

        return firstStore;
    }

    private static string? CreateSourcePrefix
    (
        string sourceRootPath,
        BackupRootLocation sourceLocation
    )
    {
        var relativePath = ToArchiveRelativePath(
            Path.GetRelativePath(sourceLocation.RootPath, sourceRootPath));
        var livePrefix = ArchiveLayout.NormalizeObjectKey(sourceLocation.Descriptor.Layout.LivePrefix);

        if (string.IsNullOrEmpty(relativePath))
        {
            return livePrefix;
        }

        if (string.IsNullOrEmpty(livePrefix) ||
            ArchiveLayout.IsUnderPrefix(relativePath, livePrefix))
        {
            return relativePath;
        }

        return ArchiveLayout.CombinePrefixAndRelativePath(livePrefix, relativePath);
    }

    private static string ToArchiveRelativePath(string relativePath)
    {
        if (string.Equals(relativePath, ".", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return ArchiveLayout.NormalizeObjectKey(relativePath);
    }

    private async Task<SyncRunResult> ApplyProjectionAsync
    (
        ArchiveSyncContext context,
        bool writeChanges,
        bool verifyOnly,
        bool byteForByte,
        string operationName,
        CancellationToken cancellationToken
    )
    {
        await context.SourceStore.EnsureReadyAsync(cancellationToken);
        await context.TargetStore.EnsureReadyAsync(cancellationToken);

        if (writeChanges && context.TargetStore is not IArchiveMutableObjectStore)
        {
            throw new YabtSyncException(
                "The selected target store does not provide the guarded mutations required " +
                "for synchronization.");
        }

        var mutableTargetStore = writeChanges ?
            (IArchiveMutableObjectStore)context.TargetStore :
            null;
        await using var mutationLock = mutableTargetStore is not null ?
            await mutableTargetStore.AcquireArchiveMutationLockAsync(cancellationToken) :
            null;
        using var operationCancellation = mutationLock is null ?
            null :
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                mutationLock.LockLostToken);
        if (operationCancellation is not null)
        {
            cancellationToken = operationCancellation.Token;
        }

        var changeManifestFileName = GetChangeManifestFileName(
            context.TargetDescriptor.ChangeManifestCompression);
        var changeManifestLoad = await ReadChangeManifestAsync(
            context.TargetStore,
            recoverInvalidManifest: writeChanges || byteForByte,
            cancellationToken);
        var previousChangeManifest = changeManifestLoad.Manifest;
        var previousManifestEntries = previousChangeManifest?.Entries.ToDictionary(
            entry => entry.RelativePath,
            StringComparer.Ordinal) ??
            new Dictionary<string, ArchiveChangeManifestEntry>(StringComparer.Ordinal);
        var nextManifestEntries = new Dictionary<string, ArchiveChangeManifestEntry>(StringComparer.Ordinal);

        var targetFolders = new Dictionary<string, TargetFolderState>(StringComparer.Ordinal);
        await LoadTargetFolderStateAsync(
            context.TargetStore,
            context.TargetDescriptor.Layout,
            targetFolders,
            string.Empty,
            cancellationToken);

        var projectedObjects = context.FormatHandler.ProjectBackupAsync(
            CreateProjectionRequest(context),
            cancellationToken);
        var summary = new ArchiveSyncSummary();
        RestoreProjectionKey? rootBackupProjectionKey = null;
        var rootBackupArtifactRoles = new HashSet<string>(StringComparer.Ordinal);
        var backupProjectionGroups = new Dictionary<
            RestoreProjectionKey,
            BackupProjectionConsistencyGroup>();
        var historizer = mutableTargetStore is null ?
            null :
            new ArchiveHistorizer(
                mutableTargetStore,
                context.TargetDescriptor.Layout,
                _timeProvider.GetUtcNow());
        if (historizer is not null)
        {
            await historizer.InspectRecoveryStateAsync(cancellationToken);
        }

        var rootDescriptorExists = false;
        var rootDescriptorNeedsWrite = false;
        if (context.SourceDocument is not null)
        {
            rootDescriptorExists = await context.TargetStore.ExistsAsync(
                BackupRootFileNames.Primary,
                cancellationToken);
            if (rootDescriptorExists)
            {
                var targetRootDocument = await ReadStoredRootDocumentAsync(
                    context.TargetStore,
                    "Target",
                    cancellationToken);
                ValidateRootDescriptorManifestEvidence(
                    previousChangeManifest,
                    targetRootDocument);
                if (!string.Equals(
                        context.SourceDocument.Descriptor.ArchiveId,
                        targetRootDocument.Descriptor.ArchiveId,
                        StringComparison.Ordinal) ||
                    !HaveSameLayout(
                        context.SourceDocument.Descriptor.Layout,
                        targetRootDocument.Descriptor.Layout))
                {
                    throw new YabtSyncException(
                        "The selected target contains a root descriptor for a different archive " +
                            "id or layout. YABT will not combine those archives.");
                }

                rootDescriptorNeedsWrite = !context.SourceDocument.ContentEquals(
                    targetRootDocument);
            }
            else
            {
                rootDescriptorNeedsWrite = true;
            }
        }

        var changeManifestInvalidated = false;
        var changeManifestInvalidationMarkerActive =
            changeManifestLoad.InvalidationMarkerExists;

        async Task EnsureChangeManifestInvalidatedAsync(CancellationToken currentCancellationToken)
        {
            if (!changeManifestLoad.Exists || changeManifestInvalidated)
            {
                return;
            }

            if (!changeManifestInvalidationMarkerActive)
            {
                await UploadChangeManifestInvalidationMarkerAsync(
                    context.TargetStore,
                    currentCancellationToken);
                changeManifestInvalidationMarkerActive = true;
            }

            await DeleteChangeManifestsAsync(
                mutableTargetStore ??
                    throw new YabtSyncException("A mutating backup requires a mutable target store."),
                changeManifestLoad.ExistingFileNames,
                currentCancellationToken);
            changeManifestInvalidated = true;
        }

        async Task<ArchiveObjectInfo?> TakeExistingBackupObjectAsync
        (
            ArchiveProjectedObject desiredObject,
            string relativePath,
            CancellationToken currentCancellationToken
        )
        {
            _ = desiredObject;
            var targetFolder = await LoadTargetFolderStateAsync(
                context.TargetStore,
                context.TargetDescriptor.Layout,
                targetFolders,
                GetParentPrefix(relativePath),
                currentCancellationToken);

            if (!targetFolder.Objects.Remove(relativePath, out var targetObject) &&
                IsEmptyFolderMarker(relativePath) &&
                await context.TargetStore.ExistsAsync(
                    context.TargetDescriptor.Layout.ToLiveObjectKey(relativePath),
                    currentCancellationToken))
            {
                targetObject = new(context.TargetDescriptor.Layout.ToLiveObjectKey(relativePath));
            }

            return targetObject;
        }

        async Task ConsumeBackupObjectAsync
        (
            ArchiveProjectedObject projectedObject,
            string relativePath,
            ArchiveObjectInfo? targetObject,
            CancellationToken currentCancellationToken
        )
        {
            BackupProjectionConsistencyGroup? projectionConsistencyGroup = null;
            if (projectedObject.Projection is not null)
            {
                var consistencyKey = new RestoreProjectionKey
                (
                    projectedObject.Projection.LogicalPath,
                    projectedObject.Projection.Format,
                    projectedObject.Projection.FormatVersion,
                    projectedObject.Projection.ProjectionId
                );
                if (!backupProjectionGroups.TryGetValue(
                        consistencyKey,
                        out projectionConsistencyGroup))
                {
                    projectionConsistencyGroup = new();
                    backupProjectionGroups.Add(
                        consistencyKey,
                        projectionConsistencyGroup);
                }
            }

            if (context.FormatHandler.ProjectsBesideSourceFolder)
            {
                var projection = projectedObject.Projection ??
                    throw new YabtSyncException(
                        $"Packaged root format handler '{context.FormatHandler.FormatName}' " +
                            "produced an artifact without projection provenance.");
                if (!string.IsNullOrEmpty(
                        ArchiveLayout.NormalizeObjectKey(projection.LogicalPath)) ||
                    !string.Equals(
                        projection.Format,
                        context.FormatHandler.FormatName,
                        StringComparison.Ordinal))
                {
                    throw new YabtSyncException(
                        $"Packaged root format handler '{context.FormatHandler.FormatName}' " +
                            "produced inconsistent root projection provenance.");
                }

                var projectionKey = new RestoreProjectionKey(
                    projection.LogicalPath,
                    projection.Format,
                    projection.FormatVersion,
                    projection.ProjectionId);
                rootBackupProjectionKey ??= projectionKey;
                if (rootBackupProjectionKey != projectionKey ||
                    !rootBackupArtifactRoles.Add(projection.ArtifactRole))
                {
                    throw new YabtSyncException(
                        $"Packaged root format handler '{context.FormatHandler.FormatName}' " +
                            "produced artifacts from multiple projections or with duplicate roles.");
                }
            }

            if (targetObject is not null)
            {
                previousManifestEntries.TryGetValue(relativePath, out var previousManifestEntry);
                var comparison = await CompareProjectedObjectAsync(
                        projectedObject,
                        context.TargetStore,
                        targetObject,
                        previousManifestEntry,
                        byteForByte,
                        prepareChangedContent: writeChanges,
                        currentCancellationToken);
                await using var preparedContent = comparison.PreparedContent;
                if (comparison.Same)
                {
                    summary.AddUnchanged();
                    projectionConsistencyGroup?.MatchedObjects.Add(new
                    (
                        projectedObject,
                        relativePath,
                        targetObject,
                        previousManifestEntry
                    ));
                    nextManifestEntries.Add(
                        relativePath,
                        comparison.ManifestEntry ??
                            throw new YabtSyncException(
                                $"Content comparison for '{relativePath}' did not produce manifest evidence."));
                    return;
                }

                summary.AddChanged();
                if (projectionConsistencyGroup is not null)
                {
                    projectionConsistencyGroup.RequiresValidation = true;
                }
                if (writeChanges)
                {
                    await EnsureChangeManifestInvalidatedAsync(currentCancellationToken);
                    await (historizer ??
                        throw new YabtSyncException(
                            "A mutating backup requires destination historization."))
                        .MoveObjectAsync(
                        relativePath,
                        "Backup",
                        currentCancellationToken);

                    var manifestEntry = await UploadProjectedObjectAsync(
                        context.TargetStore,
                        context.TargetDescriptor.Layout,
                        projectedObject,
                        relativePath,
                        currentCancellationToken,
                        preparedContent);
                    nextManifestEntries.Add(relativePath, manifestEntry);
                }

                return;
            }

            summary.AddNew();
            if (projectionConsistencyGroup is not null)
            {
                projectionConsistencyGroup.RequiresValidation = true;
            }
            if (writeChanges)
            {
                await EnsureChangeManifestInvalidatedAsync(currentCancellationToken);

                var manifestEntry = await UploadProjectedObjectAsync(
                    context.TargetStore,
                    context.TargetDescriptor.Layout,
                    projectedObject,
                    relativePath,
                    currentCancellationToken);
                nextManifestEntries.Add(relativePath, manifestEntry);
            }
        }

        var reconciliationTraversal = await ArchiveReconciler.ReconcileAsync
        (
            projectedObjects,
            explicitDirectories: null,
            StringComparer.Ordinal,
            relativePath => ValidateProjectedLiveObjectPath(
                relativePath,
                context.TargetDescriptor.Layout),
            TakeExistingBackupObjectAsync,
            ConsumeBackupObjectAsync,
            cancellationToken
        );
        if (context.FormatHandler.ProjectsBesideSourceFolder &&
            rootBackupProjectionKey is null)
        {
            throw new YabtSyncException(
                $"Packaged root format handler '{context.FormatHandler.FormatName}' did not " +
                    "produce any projection artifacts.");
        }

        if (writeChanges && !byteForByte)
        {
            var groupsRequiringValidation = backupProjectionGroups.Values
                .Where(group => group.RequiresValidation);
            foreach (var projectionGroup in groupsRequiringValidation)
            {
                foreach (var matchedObject in projectionGroup.MatchedObjects)
                {
                    var comparison = await CompareProjectedObjectAsync
                    (
                        matchedObject.ProjectedObject,
                        context.TargetStore,
                        matchedObject.TargetObject,
                        matchedObject.PreviousManifestEntry,
                        byteForByte: true,
                        prepareChangedContent: writeChanges,
                        cancellationToken
                    );
                    await using var preparedContent = comparison.PreparedContent;
                    if (comparison.Same)
                    {
                        if (writeChanges)
                        {
                            nextManifestEntries[matchedObject.RelativePath] =
                                comparison.ManifestEntry ??
                                throw new YabtSyncException(
                                    "Projection consistency comparison for " +
                                        $"'{matchedObject.RelativePath}' did not produce " +
                                        "manifest evidence.");
                        }

                        continue;
                    }

                    summary.PromoteUnchangedToChanged();
                    if (!writeChanges) { continue; }

                    await EnsureChangeManifestInvalidatedAsync(cancellationToken);
                    await (historizer ??
                        throw new YabtSyncException(
                            "A mutating backup requires destination historization."))
                        .MoveObjectAsync(
                            matchedObject.RelativePath,
                            "Backup",
                            cancellationToken);
                    var manifestEntry = await UploadProjectedObjectAsync
                    (
                        context.TargetStore,
                        context.TargetDescriptor.Layout,
                        matchedObject.ProjectedObject,
                        matchedObject.RelativePath,
                        cancellationToken,
                        preparedContent ??
                            throw new YabtSyncException(
                                "Changed projection artifact " +
                                    $"'{matchedObject.RelativePath}' did not retain its " +
                                    "validated source snapshot.")
                    );
                    nextManifestEntries[matchedObject.RelativePath] = manifestEntry;
                }
            }
        }

        await ReconcileDesiredTargetStateAsync(
            context.TargetStore,
            context.TargetDescriptor.Layout,
            targetFolders,
            reconciliationTraversal.DesiredFolderPaths,
            cancellationToken);

        await MoveRemainingTargetObjectsToHistoryAsync(
            context.TargetStore,
            historizer,
            context.TargetDescriptor.Layout,
            targetFolders,
            summary,
            writeChanges,
            EnsureChangeManifestInvalidatedAsync,
            cancellationToken);

        if (writeChanges)
        {
            if (rootDescriptorNeedsWrite)
            {
                await EnsureChangeManifestInvalidatedAsync(cancellationToken);
                if (rootDescriptorExists)
                {
                    await (historizer ??
                        throw new YabtSyncException(
                            "A mutating backup requires destination historization."))
                        .MoveRootObjectAsync(
                            BackupRootFileNames.Primary,
                            "Backup",
                            cancellationToken);
                }

                await UploadRootDocumentAsync(
                    context.TargetStore,
                    context.SourceDocument ??
                        throw new YabtSyncException(
                            "A root descriptor write requires preserved source bytes."),
                    cancellationToken);
            }

            var nextChangeManifest = _changeManifestSerializer.Create(
                nextManifestEntries.Values,
                context.Policy.Format,
                context.SourceDocument?.ContentHash,
                context.SourceDocument?.ContentLength);
            var changeManifestNeedsWrite = changeManifestInvalidated ||
                changeManifestLoad.NeedsRepresentationRewrite(changeManifestFileName) ||
                previousChangeManifest is null ||
                !string.Equals(
                    previousChangeManifest.ManifestHash,
                    nextChangeManifest.ManifestHash,
                    StringComparison.Ordinal);
            if (changeManifestNeedsWrite)
            {
                await EnsureChangeManifestInvalidatedAsync(cancellationToken);
                await UploadChangeManifestAsync(
                    context.TargetStore,
                    nextChangeManifest,
                    changeManifestFileName,
                    cancellationToken);
                if (changeManifestInvalidationMarkerActive)
                {
                    await DeleteInternalObjectAsync(
                        mutableTargetStore ??
                            throw new YabtSyncException(
                                "A mutating backup requires a mutable target store."),
                        ArchiveChangeManifest.InvalidationMarkerFileName,
                        "Change manifest invalidation marker",
                        cancellationToken);
                    changeManifestInvalidationMarkerActive = false;
                }
            }

            if (historizer is not null)
            {
                await historizer.CompleteAsync(cancellationToken);
            }
        }

        _logger.LogArchiveSyncCompleted(
            operationName,
            summary.NewCount,
            summary.ChangedCount,
            summary.ExtraCount,
            summary.UnchangedCount);

        var completed = !verifyOnly ||
            summary.NewCount == 0 &&
            summary.ChangedCount == 0 &&
            summary.ExtraCount == 0 &&
            !rootDescriptorNeedsWrite;
        var message = verifyOnly && rootDescriptorNeedsWrite ?
            $"Archive {operationName} did not verify, although " +
                $"{summary.UnchangedCount} live object(s) appear unchanged." :
            BuildSummaryMessage(
                operationName,
                summary,
                verifyOnly,
                byteForByte);
        if (rootDescriptorNeedsWrite)
        {
            var descriptorState = rootDescriptorExists ?
                "differs from the source descriptor" :
                "is missing";
            if (writeChanges)
            {
                message += rootDescriptorExists ?
                    $" The target root descriptor {descriptorState} and was replaced." :
                    " The missing target root descriptor was installed.";
            }
            else if (verifyOnly)
            {
                message += $" The target root descriptor {descriptorState}.";
            }
            else
            {
                message += $" The target root descriptor {descriptorState}; a mutating backup " +
                    "would install the exact source bytes.";
            }
        }

        return new
        (
            completed,
            message,
            summary.NewCount,
            summary.ChangedCount,
            summary.ExtraCount,
            summary.UnchangedCount
        );
    }

    private static void ValidateRootDescriptorManifestEvidence
    (
        ArchiveChangeManifest? manifest,
        BackupRootDocument targetDocument
    )
    {
        if (manifest?.RootDescriptorContentHash is null) { return; }

        if (manifest.RootDescriptorContentLength != targetDocument.ContentLength ||
            !string.Equals(
                manifest.RootDescriptorContentHash,
                targetDocument.ContentHash,
                StringComparison.Ordinal))
        {
            throw new YabtSyncException(
                "The target root descriptor does not match the content evidence in the live " +
                    "change manifest.");
        }
    }

    private ArchiveProjectionRequest CreateProjectionRequest(ArchiveSyncContext context)
    {
        var filteredSourceStore = new ArchiveFilteredObjectStore
        (
            context.SourceStore,
            CreateInternalObjectKeys(context.SourceDescriptor.Layout),
            CreateInternalObjectPrefixes(context.SourceDescriptor.Layout)
        );
        var projectionSourceStore = new ArchiveProjectionObjectStore
        (
            filteredSourceStore,
            context.SourceRoot,
            context.SourcePrefix,
            _folderPolicyReader,
            _formatHandlers
        );

        return new
        (
            projectionSourceStore,
            context.SourcePrefix,
            context.Policy,
            context.SourceRoot
        );
    }

    private static async Task<TargetFolderState> LoadTargetFolderStateAsync
    (
        IObjectStore targetStore,
        ArchiveLayout targetLayout,
        Dictionary<string, TargetFolderState> targetFolders,
        string relativeFolderPrefix,
        CancellationToken cancellationToken
    )
    {
        var normalizedRelativeFolderPrefix = ArchiveLayout.NormalizeObjectKey(relativeFolderPrefix);
        if (targetFolders.TryGetValue(normalizedRelativeFolderPrefix, out var cachedState))
        {
            return cachedState;
        }

        MarkTargetFolderVisited(
            targetFolders,
            normalizedRelativeFolderPrefix);

        var targetFolder = new TargetFolderState();
        var livePrefix = ArchiveLayout.NormalizeObjectPrefix(targetLayout.LivePrefix);
        var targetFolderPrefix = targetLayout.ToLiveObjectKey(normalizedRelativeFolderPrefix);
        var folderItems = targetStore.GetFolderItemsAsync(
            targetFolderPrefix,
            recursive: false,
            cancellationToken);

        await foreach (var folderItem in folderItems)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var targetKey = ArchiveLayout.NormalizeObjectKey(folderItem.Key);
            if (IsInternalObject(targetKey, targetLayout))
            {
                continue;
            }

            var relativePath = ArchiveLayout.RemovePrefix(targetKey, livePrefix);
            if (string.IsNullOrEmpty(relativePath))
            {
                continue;
            }

            if (folderItem.IsFolder)
            {
                targetFolder.Folders.TryAdd(relativePath, relativePath);
                continue;
            }

            if (folderItem.Object is not null)
            {
                targetFolder.Objects.TryAdd(relativePath, folderItem.Object with
                {
                    Key = targetKey,
                });
            }
        }

        var emptyFolderMarkerPath = ArchiveLayout.CombinePrefixAndRelativePath(
            normalizedRelativeFolderPrefix,
            ArchiveFolderMarkerFileNames.EmptyFolder);
        var emptyFolderMarkerKey = targetLayout.ToLiveObjectKey(emptyFolderMarkerPath);
        if (await targetStore.ExistsAsync(emptyFolderMarkerKey, cancellationToken))
        {
            targetFolder.Objects.TryAdd(
                emptyFolderMarkerPath,
                new(emptyFolderMarkerKey));
        }

        targetFolders[normalizedRelativeFolderPrefix] = targetFolder;
        return targetFolder;
    }

    private static void MarkTargetFolderVisited
    (
        Dictionary<string, TargetFolderState> targetFolders,
        string relativeFolderPrefix
    )
    {
        var parentPrefix = GetParentPrefix(relativeFolderPrefix);
        if (targetFolders.TryGetValue(parentPrefix, out var parentFolder))
        {
            parentFolder.Folders.Remove(relativeFolderPrefix);
        }
    }

    private static async Task ReconcileDesiredTargetStateAsync
    (
        IObjectStore targetStore,
        ArchiveLayout targetLayout,
        Dictionary<string, TargetFolderState> targetFolders,
        IReadOnlySet<string> desiredFolderPaths,
        CancellationToken cancellationToken
    )
    {
        var orderedDesiredFolderPaths = desiredFolderPaths
            .OrderBy(GetPathDepth)
            .ThenBy(path => path, StringComparer.Ordinal);
        foreach (var desiredFolderPath in orderedDesiredFolderPaths)
        {
            await LoadTargetFolderStateAsync(
                targetStore,
                targetLayout,
                targetFolders,
                desiredFolderPath,
                cancellationToken);
        }

        foreach (var desiredFolderPath in desiredFolderPaths)
        {
            var parentPrefix = GetParentPrefix(desiredFolderPath);
            if (targetFolders.TryGetValue(parentPrefix, out var parentFolder))
            {
                parentFolder.Folders.Remove(desiredFolderPath);
            }
        }
    }

    private static async Task MoveRemainingTargetObjectsToHistoryAsync
    (
        IObjectStore targetStore,
        ArchiveHistorizer? historizer,
        ArchiveLayout targetLayout,
        Dictionary<string, TargetFolderState> targetFolders,
        ArchiveSyncSummary summary,
        bool writeChanges,
        Func<CancellationToken, Task> beforeFirstWriteAsync,
        CancellationToken cancellationToken
    )
    {
        var extraFolderPaths = targetFolders.Values
            .SelectMany(targetFolder => targetFolder.Folders.Keys)
            .Distinct(StringComparer.Ordinal);
        var topLevelExtraFolderPaths = SelectTopLevelFolderPaths(extraFolderPaths);

        foreach (var targetFolder in targetFolders.Values)
        {
            foreach (var relativePath in targetFolder.Objects.Keys)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (topLevelExtraFolderPaths.Any(
                        folderPath => !string.Equals(relativePath, folderPath, StringComparison.Ordinal) &&
                            ArchiveLayout.IsUnderPrefix(relativePath, folderPath)))
                {
                    continue;
                }

                summary.AddExtra();
                if (writeChanges)
                {
                    await beforeFirstWriteAsync(cancellationToken);

                    await (historizer ??
                        throw new YabtSyncException(
                            "A mutating backup requires destination historization."))
                        .MoveObjectAsync(
                        relativePath,
                        "Backup",
                        cancellationToken);
                }
            }
        }

        foreach (var relativeFolderPath in topLevelExtraFolderPaths)
        {
            await MoveTargetFolderToHistoryAsync(
                targetStore,
                historizer,
                targetLayout,
                relativeFolderPath,
                summary,
                writeChanges,
                beforeFirstWriteAsync,
                cancellationToken);
        }
    }

    private static async Task MoveTargetFolderToHistoryAsync
    (
        IObjectStore targetStore,
        ArchiveHistorizer? historizer,
        ArchiveLayout targetLayout,
        string relativeFolderPath,
        ArchiveSyncSummary summary,
        bool writeChanges,
        Func<CancellationToken, Task> beforeWriteAsync,
        CancellationToken cancellationToken
    )
    {
        var targetFolderPrefix = targetLayout.ToLiveObjectKey(relativeFolderPath);
        var targetItems = targetStore.GetFolderItemsAsync(
            targetFolderPrefix,
            recursive: true,
            cancellationToken);
        var livePrefix = ArchiveLayout.NormalizeObjectPrefix(targetLayout.LivePrefix);
        var visibleObjectCount = 0;

        await foreach (var targetItem in targetItems)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (targetItem.Object is null)
            {
                continue;
            }

            var targetObject = targetItem.Object;
            var targetKey = ArchiveLayout.NormalizeObjectKey(targetObject.Key);
            if (IsInternalObject(targetKey, targetLayout))
            {
                continue;
            }

            var relativePath = ArchiveLayout.RemovePrefix(targetKey, livePrefix);
            if (string.IsNullOrEmpty(relativePath))
            {
                continue;
            }

            visibleObjectCount++;
            summary.AddExtra();
        }

        if (visibleObjectCount == 0)
        {
            summary.AddExtra();
        }

        if (!writeChanges)
        {
            return;
        }

        await beforeWriteAsync(cancellationToken);

        await (historizer ??
            throw new YabtSyncException(
                "A mutating backup requires destination historization."))
            .MoveFolderAsync(
            relativeFolderPath,
            "Backup",
            cancellationToken);
    }

    private static async Task<ArchiveChangeManifestEntry> UploadProjectedObjectAsync
    (
        IObjectStore targetStore,
        ArchiveLayout targetLayout,
        ArchiveProjectedObject projectedObject,
        string relativePath,
        CancellationToken cancellationToken,
        ArchiveObjectContent? preparedContent = default
    )
    {
        var targetKey = targetLayout.ToLiveObjectKey(relativePath);
        ArchiveObjectContent? openedContent = null;
        try
        {
            try
            {
                var content = preparedContent;
                if (content is null)
                {
                    openedContent = await projectedObject.OpenContentAsync(cancellationToken);
                    content = openedContent;
                }

                using var hashingContent = new ContentHashingReadStream(content.Content);

                await targetStore.UploadAsync(
                    targetKey,
                    hashingContent,
                    content.ContentType,
                    content.Metadata ?? EmptyMetadata,
                    cancellationToken);

                var endProbe = new byte[1];
                var unreadByteCount = await hashingContent.ReadAsync(
                    endProbe,
                    cancellationToken);
                if (unreadByteCount != 0)
                {
                    throw new InvalidDataException(
                        $"Target upload for '{targetKey}' completed before consuming all projected content.");
                }

                return CreateManifestEntry(
                    relativePath,
                    projectedObject,
                    hashingContent.BytesRead,
                    hashingContent.CompleteHash());
            }
            finally
            {
                if (openedContent is not null)
                {
                    await openedContent.DisposeAsync();
                }
            }
        }
        catch (Exception ex)
        {
            throw new YabtSyncException(
                $"Backup upload failed for projected object '{relativePath}' to target object '{targetKey}'.",
                ex);
        }
    }

    private async Task<ChangeManifestLoad> ReadChangeManifestAsync
    (
        IObjectStore targetStore,
        bool recoverInvalidManifest,
        CancellationToken cancellationToken
    )
    {
        var existingFileNames = new List<string>(2);
        foreach (var fileName in GetChangeManifestFileNames())
        {
            if (await targetStore.ExistsAsync(fileName, cancellationToken))
            {
                existingFileNames.Add(fileName);
            }
        }

        var invalidationMarkerExists = await targetStore.ExistsAsync(
            ArchiveChangeManifest.InvalidationMarkerFileName,
            cancellationToken);
        if (existingFileNames.Count == 0 && !invalidationMarkerExists)
        {
            return new(existingFileNames, false, null);
        }

        try
        {
            if (invalidationMarkerExists)
            {
                throw new InvalidDataException(
                    "A prior change-manifest replacement did not complete.");
            }

            ArchiveChangeManifest? selectedManifest = null;
            foreach (var fileName in existingFileNames)
            {
                var manifest = await ReadChangeManifestFileAsync(
                    targetStore,
                    fileName,
                    cancellationToken);
                if (selectedManifest is not null &&
                    !string.Equals(
                        selectedManifest.ManifestHash,
                        manifest.ManifestHash,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "The compressed and uncompressed change manifests describe different live states.");
                }

                selectedManifest = manifest;
            }

            return new(existingFileNames, false, selectedManifest);
        }
        catch (Exception ex)
        {
            var evidenceFileNames = invalidationMarkerExists ?
                existingFileNames.Append(ArchiveChangeManifest.InvalidationMarkerFileName) :
                existingFileNames;
            var manifestNames = string.Join("', '", evidenceFileNames);
            if (recoverInvalidManifest)
            {
                _logger.LogInvalidChangeManifestIgnored(
                    manifestNames,
                    ex);
                return new(existingFileNames, invalidationMarkerExists, null);
            }

            throw new YabtSyncException(
                $"Change manifest state containing '{manifestNames}' could not be read or validated.",
                ex);
        }
    }

    private async Task<ArchiveChangeManifest> ReadChangeManifestFileAsync
    (
        IObjectStore targetStore,
        string fileName,
        CancellationToken cancellationToken
    )
    {
        await using var content = await targetStore.OpenReadAsync(
            fileName,
            cancellationToken);
        if (!string.Equals(
                fileName,
                ArchiveChangeManifest.BrotliFileName,
                StringComparison.Ordinal))
        {
            return await _changeManifestSerializer.ReadAsync(
                content.Content,
                cancellationToken);
        }

        await using var decompressedContent = new BrotliStream(
            content.Content,
            CompressionMode.Decompress,
            leaveOpen: true);
        return await _changeManifestSerializer.ReadAsync(
            decompressedContent,
            cancellationToken);
    }

    private async Task UploadChangeManifestAsync
    (
        IObjectStore targetStore,
        ArchiveChangeManifest manifest,
        string fileName,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await using var content = new MemoryStream();
            if (string.Equals(
                    fileName,
                    ArchiveChangeManifest.BrotliFileName,
                    StringComparison.Ordinal))
            {
                await using (var compressedContent = new BrotliStream(
                    content,
                    CompressionLevel.Optimal,
                    leaveOpen: true))
                {
                    await _changeManifestSerializer.WriteAsync(
                        manifest,
                        compressedContent,
                        cancellationToken);
                }
            }
            else
            {
                await _changeManifestSerializer.WriteAsync(
                    manifest,
                    content,
                    cancellationToken);
            }

            content.Position = 0;

            await targetStore.UploadAsync(
                fileName,
                content,
                string.Equals(
                    fileName,
                    ArchiveChangeManifest.BrotliFileName,
                    StringComparison.Ordinal) ?
                        "application/octet-stream" :
                        "application/json",
                EmptyMetadata,
                cancellationToken);
        }
        catch (Exception ex)
        {
            throw new YabtSyncException(
                $"Change manifest '{fileName}' could not be uploaded.",
                ex);
        }
    }

    private static async Task UploadChangeManifestInvalidationMarkerAsync
    (
        IObjectStore targetStore,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await using var content = new MemoryStream([], writable: false);
            await targetStore.UploadAsync(
                ArchiveChangeManifest.InvalidationMarkerFileName,
                content,
                "application/octet-stream",
                EmptyMetadata,
                cancellationToken);
        }
        catch (Exception ex)
        {
            throw new YabtSyncException(
                $"Change manifest invalidation marker " +
                    $"'{ArchiveChangeManifest.InvalidationMarkerFileName}' could not be uploaded.",
                ex);
        }
    }

    private static async Task DeleteChangeManifestsAsync
    (
        IArchiveMutableObjectStore targetStore,
        IEnumerable<string> fileNames,
        CancellationToken cancellationToken
    )
    {
        foreach (var fileName in fileNames)
        {
            await DeleteInternalObjectAsync(
                targetStore,
                fileName,
                $"Obsolete change manifest '{fileName}'",
                cancellationToken);
        }
    }

    private static async Task DeleteInternalObjectAsync
    (
        IArchiveMutableObjectStore targetStore,
        string key,
        string description,
        CancellationToken cancellationToken
    )
    {
        try
        {
            if (!await targetStore.ExistsAsync(key, cancellationToken))
            {
                return;
            }

            var expectedContentHash = await ComputeStoredObjectHashAsync(
                targetStore,
                key,
                cancellationToken);
            var deleted = await targetStore.TryDeleteIfContentHashMatchesAsync(
                key,
                expectedContentHash,
                cancellationToken);
            if (!deleted)
            {
                throw new YabtSyncException(
                    $"{description} changed before it could be deleted.");
            }
        }
        catch (Exception ex)
        {
            throw new YabtSyncException(
                $"{description} at '{key}' could not be deleted.",
                ex);
        }
    }

    private static async Task<string> ComputeStoredObjectHashAsync
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

    private static async Task<ProjectedObjectComparison> CompareProjectedObjectAsync
    (
        ArchiveProjectedObject projectedObject,
        IObjectStore targetStore,
        ArchiveObjectInfo targetObject,
        ArchiveChangeManifestEntry? previousManifestEntry,
        bool byteForByte,
        bool prepareChangedContent,
        CancellationToken cancellationToken
    )
    {
        var expectedArtifactLength = projectedObject.ContentLength ??
            (byteForByte ? null : previousManifestEntry?.ArtifactLength);
        if (expectedArtifactLength.HasValue &&
            targetObject.ContentLength.HasValue &&
            expectedArtifactLength.Value != targetObject.ContentLength.Value)
        {
            return new(false, null, null);
        }

        if (!byteForByte &&
            TryCreateFastManifestEntry(
                projectedObject,
                targetObject,
                previousManifestEntry,
                out var fastManifestEntry))
        {
            return new(true, fastManifestEntry, null);
        }

        ArchiveObjectContent? sourceContent = null;
        FileStream? replayContent = null;
        try
        {
            try
            {
                sourceContent = await projectedObject.OpenContentAsync(cancellationToken);
                if (prepareChangedContent)
                {
                    replayContent = CreateComparisonReplayStream();
                }

                StreamComparison streamComparison;
                await using (var targetContent = await targetStore.OpenReadAsync(
                    targetObject.Key,
                    cancellationToken))
                {
                    streamComparison = await CompareStreamsAsync(
                        sourceContent.Content,
                        targetContent.Content,
                        DefaultBufferSize,
                        replayContent,
                        cancellationToken);
                }

                if (!streamComparison.Same)
                {
                    if (replayContent is null)
                    {
                        return new(false, null, null);
                    }

                    await sourceContent.Content.CopyToAsync(
                        replayContent,
                        DefaultBufferSize,
                        cancellationToken);
                    await replayContent.FlushAsync(cancellationToken);
                    if (projectedObject.ContentLength.HasValue &&
                        projectedObject.ContentLength.Value != replayContent.Length)
                    {
                        throw new YabtSyncException(
                            $"Projected object '{projectedObject.RelativePath}' reported length " +
                            $"{projectedObject.ContentLength.Value}, but its content contained " +
                            $"{replayContent.Length} bytes.");
                    }

                    replayContent.Position = 0;

                    var preparedContentType = sourceContent.ContentType;
                    var preparedContentMetadata = sourceContent.Metadata;
                    var completedSourceContent = sourceContent;
                    sourceContent = null;
                    await completedSourceContent.DisposeAsync();

                    var preparedContent = new ArchiveObjectContent
                    (
                        replayContent,
                        preparedContentType,
                        preparedContentMetadata
                    );
                    replayContent = null;

                    return new(false, null, preparedContent);
                }

                return new
                (
                    true,
                    CreateManifestEntry(
                        ArchiveLayout.NormalizeObjectKey(projectedObject.RelativePath),
                        projectedObject,
                        streamComparison.SourceLength,
                        streamComparison.SourceContentHash ??
                            throw new YabtSyncException(
                                "Successful byte comparison did not produce a content hash.")),
                    null
                );
            }
            finally
            {
                try
                {
                    if (sourceContent is not null)
                    {
                        await sourceContent.DisposeAsync();
                    }
                }
                finally
                {
                    if (replayContent is not null)
                    {
                        await replayContent.DisposeAsync();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            throw new YabtSyncException(
                $"Backup content comparison failed for target object '{targetObject.Key}'.",
                ex);
        }
    }

    private static bool TryCreateFastManifestEntry
    (
        ArchiveProjectedObject projectedObject,
        ArchiveObjectInfo targetObject,
        ArchiveChangeManifestEntry? previousManifestEntry,
        out ArchiveChangeManifestEntry? manifestEntry
    )
    {
        // A matching metadata fingerprint is a quick presumption, not byte-level verification.
        // --byte-for-byte bypasses this method, and incomplete target evidence falls back to streams.
        manifestEntry = null;
        if (previousManifestEntry is null ||
            string.IsNullOrWhiteSpace(projectedObject.ChangeFingerprint) ||
            string.IsNullOrWhiteSpace(previousManifestEntry.ContentHash) ||
            !ArchiveHash.IsValid(previousManifestEntry.ContentHash) ||
            previousManifestEntry.Projection != projectedObject.Projection ||
            !string.Equals(
                projectedObject.ChangeFingerprint,
                previousManifestEntry.ChangeFingerprint,
                StringComparison.Ordinal))
        {
            return false;
        }

        var expectedArtifactLength = projectedObject.ContentLength ??
            previousManifestEntry.ArtifactLength;
        if (!expectedArtifactLength.HasValue)
        {
            return false;
        }

        if (!targetObject.ContentLength.HasValue)
        {
            if (!IsEmptyFolderMarker(projectedObject.RelativePath))
            {
                return false;
            }
        }
        else if (targetObject.ContentLength.Value != expectedArtifactLength.Value)
        {
            return false;
        }

        if (HaveSameHashAlgorithm(
                targetObject.ContentHash,
                previousManifestEntry.ContentHash) &&
            !string.Equals(
                targetObject.ContentHash,
                previousManifestEntry.ContentHash,
                StringComparison.Ordinal))
        {
            return false;
        }

        manifestEntry = CreateManifestEntry(
            previousManifestEntry.RelativePath,
            projectedObject,
            expectedArtifactLength.Value,
            previousManifestEntry.ContentHash);
        return true;
    }

    private static ArchiveChangeManifestEntry CreateManifestEntry
    (
        string relativePath,
        ArchiveProjectedObject projectedObject,
        long contentLength,
        string contentHash
    )
    {
        if (projectedObject.ContentLength.HasValue &&
            projectedObject.ContentLength.Value != contentLength)
        {
            throw new YabtSyncException(
                $"Projected object '{relativePath}' reported length " +
                $"{projectedObject.ContentLength.Value}, but its content contained {contentLength} bytes.");
        }

        var changeFingerprint = string.IsNullOrWhiteSpace(projectedObject.ChangeFingerprint) ?
            contentHash :
            projectedObject.ChangeFingerprint;
        return new
        (
            ArchiveLayout.NormalizeObjectKey(relativePath),
            changeFingerprint,
            ArtifactLength: projectedObject.ContentLength.HasValue ? null : contentLength,
            ContentHash: contentHash,
            Projection: projectedObject.Projection
        );
    }

    private static bool HaveSameHashAlgorithm
    (
        string? firstHash,
        string? secondHash
    )
    {
        if (string.IsNullOrWhiteSpace(firstHash) ||
            string.IsNullOrWhiteSpace(secondHash))
        {
            return false;
        }

        var firstSeparator = firstHash.IndexOf(':', StringComparison.Ordinal);
        var secondSeparator = secondHash.IndexOf(':', StringComparison.Ordinal);
        return firstSeparator > 0 &&
            secondSeparator == firstSeparator &&
            firstHash.AsSpan(0, firstSeparator).SequenceEqual(secondHash.AsSpan(0, secondSeparator));
    }

    private static async Task<StreamComparison> CompareStreamsAsync
    (
        Stream source,
        Stream target,
        int bufferSize,
        Stream? sourceReplay,
        CancellationToken cancellationToken
    )
    {
        var sourceBuffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        var targetBuffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        var sourceHash = new XxHash128();
        long sourceLength = 0;

        try
        {
            while (true)
            {
                var sourceBytesRead = await FillBufferAsync(
                    source,
                    sourceBuffer.AsMemory(0, bufferSize),
                    cancellationToken);
                var targetBytesRead = await FillBufferAsync(
                    target,
                    targetBuffer.AsMemory(0, bufferSize),
                    cancellationToken);

                sourceHash.Append(sourceBuffer.AsSpan(0, sourceBytesRead));
                sourceLength += sourceBytesRead;
                if (sourceReplay is not null && sourceBytesRead != 0)
                {
                    await sourceReplay.WriteAsync(
                        sourceBuffer.AsMemory(0, sourceBytesRead),
                        cancellationToken);
                }

                if (sourceBytesRead != targetBytesRead)
                {
                    return new(false, sourceLength, null);
                }

                if (sourceBytesRead == 0)
                {
                    var hash = sourceHash.GetHashAndReset();
                    return new
                    (
                        true,
                        sourceLength,
                        ArchiveHash.Format(hash)
                    );
                }

                var sourceSpan = sourceBuffer.AsSpan(0, sourceBytesRead);
                var targetSpan = targetBuffer.AsSpan(0, targetBytesRead);
                if (!sourceSpan.SequenceEqual(targetSpan))
                {
                    return new(false, sourceLength, null);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(sourceBuffer);
            ArrayPool<byte>.Shared.Return(targetBuffer);
        }
    }

    private static FileStream CreateComparisonReplayStream()
    {
        // This private local replay is operation-scoped and distinct from a target provider's
        // .yabt-tmp upload staging. DeleteOnClose removes it on every normal disposal path.
        var temporaryPath = Path.Combine(
            Path.GetTempPath(),
            $"yabt-source-replay-{Guid.NewGuid():N}.tmp");
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            BufferSize = DefaultBufferSize,
            Options = FileOptions.Asynchronous |
                FileOptions.SequentialScan |
                FileOptions.DeleteOnClose,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return new FileStream(temporaryPath, options);
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

    private static FrozenSet<string> CreateInternalObjectKeys(ArchiveLayout layout)
    {
        if (!string.IsNullOrEmpty(ArchiveLayout.NormalizeObjectKey(layout.LivePrefix)))
        {
            return FrozenSet<string>.Empty;
        }

        return ReservedRootObjectKeys;
    }

    private static IEnumerable<string> GetChangeManifestFileNames() =>
    [
        ArchiveChangeManifest.BrotliFileName,
        ArchiveChangeManifest.UncompressedFileName,
    ];

    private static string GetChangeManifestFileName(string? configuredCompression)
    {
        var effectiveCompression = ArchiveChangeManifestCompression.GetEffective(
            configuredCompression);
        return effectiveCompression switch
        {
            ArchiveChangeManifestCompression.Brotli => ArchiveChangeManifest.BrotliFileName,
            ArchiveChangeManifestCompression.None => ArchiveChangeManifest.UncompressedFileName,
            _ => throw new YabtSyncException(
                $"Unsupported change manifest compression '{effectiveCompression}'."),
        };
    }

    private static List<string> CreateInternalObjectPrefixes(ArchiveLayout layout)
    {
        if (!string.IsNullOrEmpty(ArchiveLayout.NormalizeObjectKey(layout.LivePrefix)))
        {
            return [];
        }

        var prefixes = new List<string>();
        var histPrefix = ArchiveLayout.NormalizeObjectPrefix(layout.HistPrefix);
        if (histPrefix is not null)
        {
            prefixes.Add(histPrefix);
        }

        prefixes.Add(ArchiveInternalFolderNames.TemporaryUploads);
        return prefixes;
    }

    private static void ValidateInternalLayoutPrefixes(ArchiveLayout layout)
    {
        var temporaryPrefix = ArchiveInternalFolderNames.TemporaryUploads;
        var livePrefix = ArchiveLayout.NormalizeObjectPrefix(layout.LivePrefix);
        if (livePrefix is not null && PrefixesOverlap(livePrefix, temporaryPrefix))
        {
            throw new YabtSyncException(
                $"Archive live prefix '{livePrefix}' conflicts with reserved internal prefix " +
                    $"'{temporaryPrefix}'.");
        }

        var histPrefix = ArchiveLayout.NormalizeObjectPrefix(layout.HistPrefix);
        if (histPrefix is null)
        {
            throw new YabtSyncException("Archive operations require a nonempty history prefix.");
        }

        if (PrefixesOverlap(histPrefix, temporaryPrefix))
        {
            throw new YabtSyncException(
                $"Archive history prefix '{histPrefix}' conflicts with reserved internal prefix " +
                    $"'{temporaryPrefix}'.");
        }

        foreach (var reservedRootObjectKey in ReservedRootObjectKeys)
        {
            if (livePrefix is not null &&
                PrefixesOverlap(livePrefix, reservedRootObjectKey))
            {
                throw new YabtSyncException(
                    $"Archive live prefix '{livePrefix}' conflicts with reserved root object " +
                        $"'{reservedRootObjectKey}'.");
            }

            if (PrefixesOverlap(histPrefix, reservedRootObjectKey))
            {
                throw new YabtSyncException(
                    $"Archive history prefix '{histPrefix}' conflicts with reserved root object " +
                        $"'{reservedRootObjectKey}'.");
            }
        }

        if (livePrefix is not null &&
            histPrefix is not null &&
            PrefixesOverlap(livePrefix, histPrefix))
        {
            throw new YabtSyncException(
                $"Archive live prefix '{livePrefix}' overlaps history prefix '{histPrefix}'.");
        }
    }

    private static bool PrefixesOverlap(string firstPrefix, string secondPrefix) =>
        IsSameOrUnderPrefix(firstPrefix, secondPrefix) ||
        IsSameOrUnderPrefix(secondPrefix, firstPrefix);

    private static void ValidateProjectedLiveObjectPath
    (
        string relativePath,
        ArchiveLayout layout
    )
    {
        var liveObjectKey = layout.ToLiveObjectKey(relativePath);
        if (IsInternalObject(liveObjectKey, layout) ||
            IsStrictPrefixAncestor(liveObjectKey, layout.HistPrefix))
        {
            throw new YabtSyncException(
                $"Projected live object '{relativePath}' conflicts with archive history, " +
                    "metadata, or provider plumbing.");
        }
    }

    private static bool IsStrictPrefixAncestor(string objectKey, string prefix)
    {
        var normalizedObjectKey = ArchiveLayout.NormalizeObjectKey(objectKey);
        var normalizedPrefix = ArchiveLayout.NormalizeObjectKey(prefix);
        return !string.IsNullOrEmpty(normalizedObjectKey) &&
            normalizedPrefix.StartsWith(
                $"{normalizedObjectKey}/",
                StringComparison.OrdinalIgnoreCase);
    }

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

    private static bool IsInternalObject(string objectKey, ArchiveLayout layout)
    {
        return IsInternalObjectKey(objectKey, CreateInternalObjectKeys(layout)) ||
            IsInternalObjectPrefix(objectKey, CreateInternalObjectPrefixes(layout));
    }

    private static bool IsInternalObjectKey
    (
        string objectKey,
        IEnumerable<string> internalObjectKeys
    )
    {
        foreach (var internalObjectKey in internalObjectKeys)
        {
            if (string.Equals(
                    objectKey,
                    ArchiveLayout.NormalizeObjectKey(internalObjectKey),
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsInternalObjectPrefix
    (
        string objectKey,
        IEnumerable<string> internalObjectPrefixes
    )
    {
        foreach (var internalObjectPrefix in internalObjectPrefixes)
        {
            var normalizedPrefix = ArchiveLayout.NormalizeObjectPrefix(internalObjectPrefix);
            if (normalizedPrefix is not null &&
                IsSameOrUnderPrefix(objectKey, normalizedPrefix))
            {
                return true;
            }
        }

        return false;
    }

    private static List<string> SelectTopLevelFolderPaths(IEnumerable<string> folderPaths)
    {
        var selectedFolderPaths = new List<string>();
        var orderedFolderPaths = folderPaths
            .Select(ArchiveLayout.NormalizeObjectKey)
            .Where(folderPath => !string.IsNullOrEmpty(folderPath))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(GetPathDepth)
            .ThenBy(folderPath => folderPath, StringComparer.Ordinal);

        foreach (var folderPath in orderedFolderPaths)
        {
            if (selectedFolderPaths.Any(
                    selectedFolderPath => ArchiveLayout.IsUnderPrefix(folderPath, selectedFolderPath)))
            {
                continue;
            }

            selectedFolderPaths.Add(folderPath);
        }

        return selectedFolderPaths;
    }

    private static int GetPathDepth(string path) => path.Count(character => character == '/');

    private static string GetParentPrefix(string relativePath)
    {
        var normalizedRelativePath = ArchiveLayout.NormalizeObjectKey(relativePath);
        var separator = normalizedRelativePath.LastIndexOf('/');

        return separator < 0 ? string.Empty : normalizedRelativePath[..separator];
    }

    private static bool IsEmptyFolderMarker(string relativePath)
    {
        var normalizedRelativePath = ArchiveLayout.NormalizeObjectKey(relativePath);
        var separator = normalizedRelativePath.LastIndexOf('/');
        var name = separator < 0 ?
            normalizedRelativePath :
            normalizedRelativePath[(separator + 1)..];

        return string.Equals(
            name,
            ArchiveFolderMarkerFileNames.EmptyFolder,
            StringComparison.Ordinal);
    }

    private static string EnsureTrailingDirectorySeparator(string path) =>
        Path.EndsInDirectorySeparator(path) ?
            path :
            $"{path}{Path.DirectorySeparatorChar}";

    private static StringComparison GetFileSystemPathComparison() => OperatingSystem.IsWindows() ?
        StringComparison.OrdinalIgnoreCase :
        StringComparison.Ordinal;

    private static string BuildSummaryMessage
    (
        string operationName,
        ArchiveSyncSummary summary,
        bool verifyOnly,
        bool byteForByte
    )
    {
        if (verifyOnly &&
            summary.NewCount == 0 &&
            summary.ChangedCount == 0 &&
            summary.ExtraCount == 0)
        {
            return byteForByte ?
                $"Archive {operationName} completed byte-for-byte; " +
                    $"verified {summary.UnchangedCount} unchanged object(s)." :
                $"Archive {operationName} quick check completed from metadata fingerprints; " +
                    $"{summary.UnchangedCount} object(s) appear unchanged. " +
                    "Use --byte-for-byte for a full content comparison.";
        }

        if (verifyOnly && !byteForByte)
        {
            operationName += " quick metadata check";
        }

        return $"Archive {operationName} completed; {summary.NewCount} new object(s), " +
            $"{summary.ChangedCount} changed object(s), {summary.ExtraCount} extra object(s), " +
            $"and {summary.UnchangedCount} unchanged object(s).";
    }

    private sealed class TargetFolderState
    {
        public Dictionary<string, ArchiveObjectInfo> Objects { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> Folders { get; } = new(StringComparer.Ordinal);
    }

    private sealed record ProjectedObjectComparison
    (
        bool Same,
        ArchiveChangeManifestEntry? ManifestEntry,
        ArchiveObjectContent? PreparedContent
    );

    private sealed class BackupProjectionConsistencyGroup
    {
        public bool RequiresValidation { get; set; }

        public List<BackupProjectionMatchedObject> MatchedObjects { get; } = [];
    }

    private sealed record BackupProjectionMatchedObject
    (
        ArchiveProjectedObject ProjectedObject,
        string RelativePath,
        ArchiveObjectInfo TargetObject,
        ArchiveChangeManifestEntry? PreviousManifestEntry
    );

    private sealed record RestoreContext
    (
        string DescriptorRootPath,
        IObjectStore ArchiveStore,
        BackupRootDescriptor Descriptor,
        BackupRootDocument? SourceDocument,
        BackupRootDocument? ArchiveDocument,
        IArchiveFormatHandler? RootFormatHandler,
        bool RootIsPackaged,
        string? LocalArchiveRootPath
    );

    private sealed record RestoreDestinationFile
    (
        string RelativePath,
        string ArchiveKey,
        long? ContentLength,
        DateTimeOffset? LastModifiedUtc
    );

    private sealed record RestoreDestinationState
    (
        IReadOnlyDictionary<string, RestoreDestinationFile> Files,
        IReadOnlySet<string> Directories
    );

    private sealed record RestoreHistoryMove
    (
        string RelativePath,
        bool IsFolder
    );

    private sealed record RootDescriptorRestore
    (
        bool NeedsWrite,
        bool MoveExistingToHistory
    )
    {
        public static RootDescriptorRestore None { get; } = new(
            NeedsWrite: false,
            MoveExistingToHistory: false);
    }

    private sealed record RestoreProjectionKey
    (
        string LogicalPath,
        string Format,
        int FormatVersion,
        string ProjectionId
    );

    private sealed record RestoreReconciliation
    (
        IReadOnlyList<ArchiveProjectedObject> FilesToWrite,
        IReadOnlyList<RestoreHistoryMove> HistoryMoves,
        int NewCount,
        int ChangedCount,
        int ExtraCount,
        int UnchangedCount
    );

    private sealed class RestorePlan
    (
        IReadOnlyList<ArchiveProjectedObject> _files,
        IReadOnlyList<string> _directories,
        IReadOnlyList<string> _emptyDirectories,
        IReadOnlyList<ArchiveRestoreProjection> _projections
    ) : IAsyncDisposable
    {
        public IReadOnlyList<ArchiveProjectedObject> Files => _files;

        public IReadOnlyList<string> Directories => _directories;

        public IReadOnlyList<string> EmptyDirectories => _emptyDirectories;

        public async ValueTask DisposeAsync()
        {
            foreach (var projection in _projections)
            {
                await projection.DisposeAsync();
            }
        }
    }

    private sealed class StagedRestoreFiles
    (
        string _rootPath,
        ILogger _logger
    ) : IAsyncDisposable
    {
        private readonly Dictionary<string, string> _files = new(
            OperatingSystem.IsWindows() ?
                StringComparer.OrdinalIgnoreCase :
                StringComparer.Ordinal);

        public void Add(string relativePath, string stagingPath)
        {
            _logger.LogTrace(nameof(Add));

            if (!_files.TryAdd(relativePath, stagingPath))
            {
                throw new YabtSyncException(
                    $"Restore path '{relativePath}' was staged more than once.");
            }
        }

        public FileStream OpenContent(string relativePath)
        {
            _logger.LogTrace(nameof(OpenContent));

            if (!_files.TryGetValue(relativePath, out var stagingPath))
            {
                throw new YabtSyncException(
                    $"Restore path '{relativePath}' does not have staged content.");
            }

            try
            {
                return OpenRestoreStagingFileStream(stagingPath);
            }
            catch (Exception ex)
            {
                throw new YabtSyncException(
                    $"Restore staged content for '{relativePath}' could not be opened.",
                    ex);
            }
        }

        public ValueTask DisposeAsync()
        {
            _logger.LogTrace(nameof(DisposeAsync));

            foreach (var stagingPath in _files.Values)
            {
                TryDeleteRestoreTemporaryPath(
                    _logger,
                    stagingPath,
                    () => File.Delete(stagingPath));
            }

            TryDeleteRestoreTemporaryPath(
                _logger,
                _rootPath,
                () => Directory.Delete(_rootPath));
            return ValueTask.CompletedTask;
        }
    }

    private sealed record ChangeManifestLoad
    (
        IReadOnlyList<string> ExistingFileNames,
        bool InvalidationMarkerExists,
        ArchiveChangeManifest? Manifest
    )
    {
        public bool Exists => ExistingFileNames.Count != 0 || InvalidationMarkerExists;

        public bool RequiresInvalidationMarker =>
            InvalidationMarkerExists ||
            Manifest is null && ExistingFileNames.Count > 1;

        public bool NeedsRepresentationRewrite(string expectedFileName) =>
            InvalidationMarkerExists ||
            ExistingFileNames.Count != 1 ||
            !string.Equals(
                ExistingFileNames[0],
                expectedFileName,
                StringComparison.Ordinal);
    }

    private async Task<BackupRootDocument> ReadStoredRootDocumentAsync
    (
        IObjectStore store,
        string description,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await using var storedContent = await store.OpenReadAsync(
                BackupRootFileNames.Primary,
                cancellationToken);
            return await _backupRootSerializer.ReadDocumentAsync(
                storedContent.Content,
                cancellationToken);
        }
        catch (Exception ex)
        {
            throw new YabtSyncException(
                $"{description} root descriptor '{BackupRootFileNames.Primary}' could not be validated.",
                ex);
        }
    }

    private static async Task UploadRootDocumentAsync
    (
        IObjectStore store,
        BackupRootDocument document,
        CancellationToken cancellationToken
    )
    {
        try
        {
            using var content = document.OpenRead();
            await store.UploadAsync(
                BackupRootFileNames.Primary,
                content,
                "application/json",
                EmptyMetadata,
                cancellationToken);
        }
        catch (Exception ex)
        {
            throw new YabtSyncException(
                $"Root descriptor '{BackupRootFileNames.Primary}' could not be uploaded exactly.",
                ex);
        }
    }

    private sealed record LogicalStateManifestLoad
    (
        bool ManifestExists,
        bool InvalidationMarkerExists,
        ArchiveLogicalStateManifest? Manifest
    );

    private sealed record StreamComparison
    (
        bool Same,
        long SourceLength,
        string? SourceContentHash
    );

}
