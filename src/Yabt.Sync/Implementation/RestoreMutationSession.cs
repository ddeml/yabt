using Microsoft.Extensions.Logging;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.Metadata;

namespace Yabt.Sync.Implementation;

// One session owns invalidation and history across every folder in a restore.
internal sealed class RestoreMutationSession
(
    IArchiveMutableObjectStore _destinationStore,
    ArchiveLayout _destinationLayout,
    IRestoreDestination _restoreTarget,
    LogicalStateManifestLoad manifestLoad,
    RootDescriptorRestore _rootDescriptorRestore,
    BackupRootDocument? _archiveRootDocument,
    ArchiveManifestStore _manifests,
    ArchiveRootDocuments _rootDocuments,
    TimeProvider _timeProvider,
    ILogger _logger
)
{
    private readonly RestoreManifestTransaction _manifestTransaction =
        new(_manifests, _destinationStore, manifestLoad, _logger);
    private ArchiveHistorizer? _historizer;
    private bool _mutationStarted;

    internal async Task EnsureMutationStartedAsync
    (
        bool liveStateChanged,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(EnsureMutationStartedAsync));
        if (_mutationStarted) { return; }
        await _manifestTransaction.BeginAsync(
            liveStateChanged, _rootDescriptorRestore.NeedsWrite, cancellationToken);
        _historizer = new(_destinationStore, _destinationLayout, _timeProvider.GetUtcNow(), _logger);
        await _historizer.InspectRecoveryStateAsync(cancellationToken);
        _mutationStarted = true;
    }

    internal async Task MoveHistoryItemAsync
    (
        RestoreHistoryMove historyMove,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(MoveHistoryItemAsync));
        await EnsureMutationStartedAsync(true, cancellationToken);
        if (historyMove.IsFolder)
        {
            await _historizer!.MoveFolderAsync(historyMove.RelativePath, "Restore", cancellationToken);
        }
        else
        {
            await _historizer!.MoveObjectAsync(historyMove.RelativePath, "Restore", cancellationToken);
        }
        _logger.ReportRestoreItemHistorized(historyMove);
    }

    internal async Task BeforeFileCommitAsync
    (
        RestoreReconciliation reconciliation,
        ArchiveProjectedObject file,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(BeforeFileCommitAsync));
        await EnsureMutationStartedAsync(true, cancellationToken);
        foreach (var move in reconciliation.HistoryMoves.GetBlockers(file.RelativePath, cancellationToken))
        {
            await MoveHistoryItemAsync(move, cancellationToken);
            reconciliation.HistoryMoves.MarkCompleted(move);
        }
    }

    internal async Task CreateUnitDirectoriesAsync
    (
        RestorePlan plan,
        RestoreReconciliation reconciliation,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(CreateUnitDirectoriesAsync));
        foreach (var directory in plan.Directories)
        {
            foreach (var move in reconciliation.HistoryMoves.GetBlockers(directory, cancellationToken))
            {
                await MoveHistoryItemAsync(move, cancellationToken);
                reconciliation.HistoryMoves.MarkCompleted(move);
            }
            var destinationPath = _destinationLayout.ToLiveObjectKey(directory);
            if (!_restoreTarget.DirectoryExists(ObjectPath.Parse(destinationPath)))
            {
                await EnsureMutationStartedAsync(true, cancellationToken);
                await _restoreTarget.CreateDirectoryAsync(destinationPath, cancellationToken);
            }
            if (reconciliation.NewDesiredPaths.Contains(directory) ||
                reconciliation.ChangedDesiredPaths.Contains(directory))
            {
                _logger.ReportRestoreItemWritten(
                    ArchiveOperationLogging.GetRestoreChangeKind(reconciliation, directory),
                    "directory", directory);
            }
        }
    }

    internal async Task CompleteAsync
    (
        RestorePlan plan,
        RestoreReconciliation reconciliation,
        bool restoreRootNeedsCreation,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(CompleteAsync));
        await EnsureMutationStartedAsync(
            reconciliation.NewCount != 0 || reconciliation.ChangedCount != 0 ||
                reconciliation.ExtraCount != 0, cancellationToken);
        foreach (var historyMove in reconciliation.HistoryMoves.EnumeratePending(cancellationToken))
        {
            await MoveHistoryItemAsync(historyMove, cancellationToken);
            reconciliation.HistoryMoves.MarkCompleted(historyMove);
        }

        await _restoreTarget.CreateDirectoryAsync(
            _destinationLayout.ToLiveObjectKey(string.Empty),
            cancellationToken);
        if (restoreRootNeedsCreation)
        {
            _logger.ReportRestoreItemWritten("new", "directory", ".");
        }
        foreach (var directoryPath in plan.EmptyDirectories)
        {
            await _restoreTarget.CreateDirectoryAsync(
                _destinationLayout.ToLiveObjectKey(directoryPath),
                cancellationToken);
            if (reconciliation.NewDesiredPaths.Contains(directoryPath) ||
                reconciliation.ChangedDesiredPaths.Contains(directoryPath))
            {
                _logger.ReportRestoreItemWritten(
                    ArchiveOperationLogging.GetRestoreChangeKind(reconciliation, directoryPath),
                    "directory",
                    directoryPath);
            }
        }

        var changedDirectories = reconciliation.ChangedDesiredPaths
            .Where(path => plan.IsDirectory(path) && !plan.IsEmptyDirectory(path));
        foreach (var directoryPath in changedDirectories)
        {
            _logger.ReportRestoreItemWritten("changed", "directory", directoryPath);
        }

        if (_rootDescriptorRestore.NeedsWrite)
        {
            if (_archiveRootDocument is null)
            {
                throw new YabtSyncException(
                    "Restore planned a root descriptor write without preserved archive bytes.");
            }

            if (_rootDescriptorRestore.MoveExistingToHistory)
            {
                await (_historizer ??
                    throw new YabtSyncException(
                        "Restore root descriptor historization was not initialized."))
                    .MoveRootObjectAsync(
                    BackupRootFileNames.Primary,
                    "Restore",
                    cancellationToken);
                _logger.LogControlMetadataOperation(
                    "Moved to restore history",
                    BackupRootFileNames.Primary);
            }

            await _rootDocuments.UploadRootDocumentAsync(
                _destinationStore,
                _archiveRootDocument,
                cancellationToken);
        }

        await (_historizer ??
            throw new YabtSyncException(
                "Restore mutation did not initialize historization."))
            .CompleteAsync(cancellationToken);

        using var nextLogicalStateManifest = await _manifests.CreateLogicalStateManifestAsync
        (
            _destinationStore,
            _destinationLayout,
            plan,
            cancellationToken
        );
        await _manifestTransaction.PublishAsync(nextLogicalStateManifest, cancellationToken);

    }
}
