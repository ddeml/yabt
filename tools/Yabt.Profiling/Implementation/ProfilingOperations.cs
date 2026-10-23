using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yabt.Common;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.Metadata;
using Yabt.Sync;
using Yabt.Sync.Implementation;

namespace Yabt.Profiling.Implementation;

/// <summary>
/// Runs the read-only production restore planning components with diagnostic
/// checkpoints. Whole-command timing must use the public synchronizer instead.
/// </summary>
internal sealed class ProfilingOperations(IServiceProvider _services)
{
    private readonly ILogger<ProfilingOperations> _logger =
        _services.GetRequiredService<ILogger<ProfilingOperations>>();

    internal async Task<ProfilingOperationSummary> MeasureRestorePlanningAsync
    (
        SyncRunRequest request,
        Func<string, IReadOnlyDictionary<string, long>?, Task> checkpoint,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(MeasureRestorePlanningAsync));

        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (!request.DryRun)
        {
            throw new ArgumentException
            (
                "Phase measurements require a dry-run restore request.",
                nameof(request)
            );
        }

        if (string.IsNullOrEmpty(request.DestinationRoot))
        {
            throw new ArgumentException
            (
                "Phase measurements require a restore destination.",
                nameof(request)
            );
        }

        // Returning from the owning method is essential: disposing a plan while
        // retaining its managed lists would give a misleading cleanup result.
        var summary = await MeasureRestorePlanningCoreAsync
        (
            request,
            checkpoint,
            cancellationToken
        );
        await checkpoint("released", null);
        return summary;
    }

    private async Task<ProfilingOperationSummary> MeasureRestorePlanningCoreAsync
    (
        SyncRunRequest request,
        Func<string, IReadOnlyDictionary<string, long>?, Task> checkpoint,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(MeasureRestorePlanningCoreAsync));

        using var configurationWarnings = ConfigurationWarnings.BeginOperation();
        var contexts = _services.GetRequiredService<ArchiveOperationContextFactory>();
        var locationResolver = _services.GetRequiredService<IArchiveLocationResolver>();
        var destinationFactory = _services.GetRequiredService<IRestoreDestinationFactory>();
        var rootDocuments = _services.GetRequiredService<ArchiveRootDocuments>();
        var manifests = _services.GetRequiredService<ArchiveManifestStore>();
        var planner = _services.GetRequiredService<RestorePlanner>();
        var reconciler = _services.GetRequiredService<RestoreReconciler>();
        var temporaryOptions = _services.GetRequiredService<IOptionsMonitor<TemporaryContentOptions>>();

        var context = await contexts.CreateRestoreContextAsync(request, cancellationToken);
        var destination = destinationFactory.Create(request.DestinationRoot!);
        using var temporaryStorage = TemporaryStorageValidation.BeginOperation
        (
            path => locationResolver.ValidateTemporaryStorageLocations
            (
                path, context.SourceLocation, context.ArchiveStore, destination
            )
        );
        TemporaryStorageValidation.ValidateDirectory
        (
            temporaryOptions.CurrentValue.GetEffectiveTemporaryDirectory()
        );
        using var temporaryIndexOperation = TemporaryIndex.BeginOperation(temporaryOptions.CurrentValue);
        locationResolver.ValidateRestoreLocations
        (
            context.SourceLocation,
            destination,
            context.ArchiveStore
        );

        using var changeManifestLoad = await manifests.ReadChangeManifestAsync
        (
            context.ArchiveStore,
            recoverInvalidManifest: false,
            cancellationToken
        );
        var changeManifest = changeManifestLoad.Manifest ??
            throw new YabtSyncException("Profiling restore requires a valid live change manifest.");
        context = await rootDocuments.LoadArchiveRootDocumentAsync
        (
            context,
            changeManifest,
            cancellationToken
        );

        ArchiveLayout destinationLayout;
        if (context.ArchiveDocument is not null)
        {
            _ = await rootDocuments.InspectDestinationRootDescriptorAsync
            (
                destination,
                context.ArchiveDocument,
                request.ReplaceRootDescriptor,
                cancellationToken
            );
            destinationLayout = context.ArchiveDocument.Descriptor.Layout;
        }
        else
        {
            destinationLayout = await rootDocuments.GetRestoreDestinationLayoutAsync
            (
                destination,
                context.Descriptor.Layout.HistPrefix,
                cancellationToken
            );
        }

        ArchivePathRules.ValidateInternalLayoutPrefixes(destinationLayout);
        RestorePlanner.ValidateRestoreLayoutPaths(destinationLayout, destination);
        await destination.EnsureSafeAsync
        (
            destinationLayout.LivePrefix,
            destinationLayout.HistPrefix,
            cancellationToken
        );
        await checkpoint("prepared", null);

        using var restoreObjects = await planner.LoadRestoreObjectsAsync
        (
            context,
            changeManifest,
            cancellationToken
        );
        await checkpoint
        (
            "enumerated", new Dictionary<string, long>
            {
                ["archiveObjects"] = restoreObjects.Count,
            }
        );
        var rootFormatHandler = planner.ResolveRestoreRootFormatHandler(changeManifest, restoreObjects);
        context = context with
        {
            RootFormatHandler = rootFormatHandler,
            RootIsPackaged = rootFormatHandler.ProjectsBesideSourceFolder,
        };

        await using var plan = await planner.CreateRestorePlanAsync
        (
            context,
            restoreObjects,
            destination.Namespace.Scope(ObjectPath.Parse(destinationLayout.LivePrefix)),
            useProjectionProvenance:
                changeManifest.SchemaVersion == ArchiveChangeManifest.ExpectedSchemaVersion,
            cancellationToken
        );
        RestorePlanner.ValidateRestorePlanInternalPaths(plan, destinationLayout, destination);
        if (destination.Store is not IArchiveMutableObjectStore)
        {
            throw new YabtSyncException
            (
                "Profiling restore requires the same guarded destination operations as restore."
            );
        }

        await checkpoint
        (
            "planned", new Dictionary<string, long>
            {
                ["files"] = plan.Files.Count,
                ["directories"] = plan.Directories.Count,
                ["emptyDirectories"] = plan.EmptyDirectories.Count,
                ["projections"] = plan.Projections.Count,
            }
        );

        if (request.ByteForByte)
        {
            var executor = _services.GetRequiredService<RestoreExecutor>();
            var options = _services.GetRequiredService<IOptionsMonitor<YabtSyncOptions>>();
            var failures = await executor.ValidateRestorePlanContentAsync
            (
                plan,
                options.CurrentValue.GetValidatedRestoreMaximumConcurrency(),
                cancellationToken
            );
            if (failures.Count != 0)
            {
                throw new YabtSyncException
                (
                    $"Profiling fixture has {failures.Count} restore content validation failure(s)."
                );
            }

            await checkpoint("content-validated", null);
        }

        using var logicalStateManifestLoad = await manifests.ReadLogicalStateManifestAsync
        (
            destination.Store,
            cancellationToken
        );
        await checkpoint("destination-manifest", null);

        // The production reconciler owns destination enumeration internally.
        // This phase includes enumeration and comparison; it does not repeat
        // that traversal merely to manufacture an additional checkpoint.
        using var reconciliation = await reconciler.CreateRestoreReconciliationAsync
        (
            destination.Store,
            destinationLayout,
            plan,
            logicalStateManifestLoad.Manifest,
            request.ByteForByte,
            new HashSet<string>(plan.Namespace.PathComparer),
            cancellationToken
        );
        if (reconciliation.Failures.Count != 0)
        {
            throw new YabtSyncException
            (
                $"Profiling fixture has {reconciliation.Failures.Count} reconciliation failure(s)."
            );
        }

        var summary = new ProfilingOperationSummary
        (
            restoreObjects.Count,
            plan.Files.Count,
            plan.Directories.Count,
            plan.EmptyDirectories.Count,
            plan.Projections.Count,
            reconciliation.FilesToWrite.Count,
            reconciliation.HistoryMoves.Count,
            reconciliation.NewCount,
            reconciliation.ChangedCount,
            reconciliation.ExtraCount,
            reconciliation.UnchangedCount
        );
        await checkpoint
        (
            "reconciled", new Dictionary<string, long>
            {
                ["filesToWrite"] = summary.FilesToWriteCount,
                ["historyMoves"] = summary.HistoryMoveCount,
                ["new"] = summary.NewCount,
                ["changed"] = summary.ChangedCount,
                ["extra"] = summary.ExtraCount,
                ["unchanged"] = summary.UnchangedCount,
            }
        );

        // A diagnostic callback may force collection or await a heap dump.
        // Keep every operation owner rooted until that callback has finished.
        GC.KeepAlive(reconciliation);
        GC.KeepAlive(logicalStateManifestLoad);
        GC.KeepAlive(plan);
        GC.KeepAlive(restoreObjects);
        GC.KeepAlive(changeManifestLoad);
        GC.KeepAlive(context);
        return summary;
    }
}
