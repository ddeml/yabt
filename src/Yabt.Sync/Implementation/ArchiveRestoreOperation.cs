using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yabt.Common;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.Metadata;

namespace Yabt.Sync.Implementation;

internal sealed class ArchiveRestoreOperation
(
    ILogger<ArchiveRestoreOperation> _logger,
    IArchiveLocationResolver _locationResolver,
    IRestoreDestinationFactory _restoreDestinationFactory,
    IOptionsMonitor<YabtSyncOptions> _options,
    IOptionsMonitor<TemporaryContentOptions> _temporaryOptions,
    ArchiveOperationContextFactory _contexts,
    ArchiveRootDocuments _rootDocuments,
    RestorePlanner _planner,
    RestoreReconciler _reconciler,
    RestoreExecutor _executor,
    ArchiveManifestStore _manifests,
    IncrementalRestoreOperation _incremental,
    OperationProgressFactory _progressFactory
)
{
    internal async Task<SyncRunResult> RunRestoreOperationAsync
    (
        SyncRunRequest request,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(RunRestoreOperationAsync));

        if (string.IsNullOrEmpty(request.DestinationRoot))
        {
            throw new YabtSyncException(
                "Restore requires a filesystem destination selected with --destination-root.");
        }

        var restoreMaximumConcurrency =
            _options.CurrentValue.GetValidatedRestoreMaximumConcurrency();
        _logger.LogRestoreRequested(
            request.SourceRoot,
            request.DestinationRoot,
            request.DryRun,
            restoreMaximumConcurrency);

        var context = await _contexts.CreateRestoreContextAsync(request, cancellationToken);
        var restoreTarget = _restoreDestinationFactory.Create(request.DestinationRoot);
        var destinationStore = restoreTarget.Store;
        using var temporaryStorage = TemporaryStorageValidation.BeginOperation(path =>
            _locationResolver.ValidateTemporaryStorageLocations(
                path, context.SourceLocation, context.ArchiveStore, restoreTarget));
        TemporaryStorageValidation.ValidateDirectory(_temporaryOptions.CurrentValue.GetEffectiveTemporaryDirectory());
        using var temporaryIndexOperation = TemporaryIndex.BeginOperation(_temporaryOptions.CurrentValue);
        _locationResolver.ValidateRestoreLocations(
            context.SourceLocation,
            restoreTarget,
            context.ArchiveStore);

        using var changeManifestLoad = await _manifests.ReadChangeManifestAsync(
            context.ArchiveStore,
            recoverInvalidManifest: false,
            cancellationToken);
        var changeManifest = changeManifestLoad.Manifest ??
            throw new YabtSyncException(
                "Restore requires a valid live change manifest created by a successful backup.");
        context = await _rootDocuments.LoadArchiveRootDocumentAsync(
            context,
            changeManifest,
            cancellationToken);

        RootDescriptorRestore rootDescriptorRestore;
        ArchiveLayout destinationLayout;
        if (context.ArchiveDocument is not null)
        {
            rootDescriptorRestore = await _rootDocuments.InspectDestinationRootDescriptorAsync
            (
                restoreTarget,
                context.ArchiveDocument,
                request.ReplaceRootDescriptor,
                cancellationToken
            );
            destinationLayout = context.ArchiveDocument.Descriptor.Layout;
        }
        else
        {
            rootDescriptorRestore = RootDescriptorRestore.None;
            destinationLayout = await _rootDocuments.GetRestoreDestinationLayoutAsync
            (
                restoreTarget,
                context.Descriptor.Layout.HistPrefix,
                cancellationToken
            );
        }

        ArchivePathRules.ValidateInternalLayoutPrefixes(destinationLayout);
        RestorePlanner.ValidateRestoreLayoutPaths(destinationLayout, restoreTarget);
        var restoreRootNeedsCreation = !restoreTarget.DirectoryExists(
            ObjectPath.Parse(destinationLayout.LivePrefix));
        await restoreTarget.EnsureSafeAsync(
            destinationLayout.LivePrefix,
            destinationLayout.HistPrefix,
            cancellationToken);

        if (_planner.ResolveFolderProjector(changeManifest) is { } folderProjector)
        {
            return await _incremental.RunAsync(request, context, changeManifest, folderProjector,
                restoreTarget, destinationLayout, rootDescriptorRestore, restoreRootNeedsCreation,
                restoreMaximumConcurrency, cancellationToken);
        }

        using var restoreObjects = await _planner.LoadRestoreObjectsAsync(
            context,
            changeManifest,
            cancellationToken);
        var rootFormatHandler = _planner.ResolveRestoreRootFormatHandler(
            changeManifest,
            restoreObjects);
        context = context with
        {
            RootFormatHandler = rootFormatHandler,
            RootIsPackaged = rootFormatHandler.ProjectsBesideSourceFolder,
        };

        await using var plan = await _planner.CreateRestorePlanAsync
        (
            context,
            restoreObjects,
            restoreTarget.Namespace.Scope(ObjectPath.Parse(destinationLayout.LivePrefix)),
            useProjectionProvenance:
                changeManifest.SchemaVersion == ArchiveChangeManifest.ExpectedSchemaVersion,
            cancellationToken
        );
        RestorePlanner.ValidateRestorePlanInternalPaths(
            plan,
            destinationLayout,
            restoreTarget);

        if (destinationStore is not IArchiveMutableObjectStore mutableDestinationStore)
        {
            throw new YabtSyncException(
                "Restore destination does not provide the guarded filesystem mutations required " +
                    "for historization.");
        }

        await using var mutationLock = request.DryRun ?
            null :
            await mutableDestinationStore.AcquireArchiveMutationLockAsync
            (
                cancellationToken,
                _options.CurrentValue.GetValidatedLockDocumentMaximumBytes()
            );
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
            var preflightLayout = destinationLayout;
            if (context.ArchiveDocument is not null)
            {
                rootDescriptorRestore = await _rootDocuments.InspectDestinationRootDescriptorAsync
                (
                    restoreTarget,
                    context.ArchiveDocument,
                    request.ReplaceRootDescriptor,
                    cancellationToken
                );
                destinationLayout = context.ArchiveDocument.Descriptor.Layout;
            }
            else
            {
                rootDescriptorRestore = RootDescriptorRestore.None;
                destinationLayout = await _rootDocuments.GetRestoreDestinationLayoutAsync
                (
                    restoreTarget,
                    context.Descriptor.Layout.HistPrefix,
                    cancellationToken
                );
            }

            if (!ArchivePathRules.HaveSameLayout(preflightLayout, destinationLayout))
            {
                throw new YabtSyncException
                (
                    "The destination layout changed while restore was preparing its plan. " +
                        "Run restore again so paths are validated against the current destination namespace."
                );
            }

            ArchivePathRules.ValidateInternalLayoutPrefixes(destinationLayout);
            RestorePlanner.ValidateRestoreLayoutPaths(destinationLayout, restoreTarget);
            await restoreTarget.EnsureSafeAsync(
                destinationLayout.LivePrefix,
                destinationLayout.HistPrefix,
                cancellationToken);
            RestorePlanner.ValidateRestorePlanInternalPaths(
                plan,
                destinationLayout,
                restoreTarget);
        }

        using var progress = request.DryRun ? null : _progressFactory.Open
        (
            "restore-projection",
            context.ArchiveStore, destinationStore,
            [context.Descriptor.ArchiveId, destinationLayout.LivePrefix, destinationLayout.HistPrefix,
                changeManifest.ManifestHash, request.ReplaceRootDescriptor.ToString()]
        );
        progress?.StartUnit(string.Empty);
        var failures = new List<SyncItemFailure>();
        if (request.ByteForByte && request.DryRun)
        {
            failures.AddRange(await _executor.ValidateRestorePlanContentAsync(
                plan,
                restoreMaximumConcurrency,
                cancellationToken));
        }

        var blockedRestorePaths = failures
            .Select(failure => failure.RelativePath)
            .ToHashSet(plan.Namespace.PathComparer);

        using var logicalStateManifestLoad = await _manifests.ReadLogicalStateManifestAsync(
            destinationStore,
            cancellationToken);
        using var reconciliation = await _reconciler.CreateRestoreReconciliationAsync
        (
            destinationStore,
            destinationLayout,
            plan,
            logicalStateManifestLoad.Manifest,
            request.ByteForByte,
            blockedRestorePaths,
            cancellationToken
        );

        if (request.DryRun)
        {
            failures.AddRange(reconciliation.Failures);
            _logger.ReportRestoreDryRun(
                plan,
                reconciliation,
                rootDescriptorRestore,
                restoreRootNeedsCreation);
        }
        else
        {
            failures.AddRange(await _executor.ApplyRestorePlanAsync(
                mutableDestinationStore,
                destinationLayout,
                plan,
                reconciliation,
                restoreTarget,
                logicalStateManifestLoad,
                rootDescriptorRestore,
                context.ArchiveDocument,
                restoreRootNeedsCreation,
                request.ByteForByte,
                restoreMaximumConcurrency,
                cancellationToken));
        }

        var operationName = request.DryRun ? "Restore dry run" : "Restore";
        failures = SyncFailureReporting.NormalizeFailures(failures);
        if (failures.Count != 0)
        {
            _logger.LogArchiveSyncIncomplete(operationName, failures.Count);
            return new SyncRunResult
            (
                Completed: false,
                Message: SyncFailureReporting.BuildIncompleteReadMessage(
                    operationName,
                    failures,
                    request.DryRun ?
                        "No changes were made." :
                        "Readable files may have been restored as rerun checkpoints; " +
                            "unrelated extra items and final manifests were left untouched."),
                NewCount: reconciliation.NewCount,
                ChangedCount: reconciliation.ChangedCount,
                ExtraCount: reconciliation.ExtraCount,
                UnchangedCount: reconciliation.UnchangedCount,
                Failures: failures
            );
        }

        progress?.CompleteUnit(string.Empty, []);
        progress?.CompleteOperation();
        _logger.LogArchiveSyncCompleted(
            operationName,
            reconciliation.NewCount,
            reconciliation.ChangedCount,
            reconciliation.ExtraCount,
            reconciliation.UnchangedCount);
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
}
