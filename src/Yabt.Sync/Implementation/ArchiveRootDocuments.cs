using Microsoft.Extensions.Logging;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.Metadata;

namespace Yabt.Sync.Implementation;

internal sealed class ArchiveRootDocuments
(
    ILogger<ArchiveRootDocuments> _logger,
    IBackupRootSerializer _backupRootSerializer
)
{
    internal async Task<ArchiveLayout> GetRestoreDestinationLayoutAsync
    (
        IRestoreDestination destination,
        string defaultHistoryPrefix,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(GetRestoreDestinationLayoutAsync));

        if (destination.DirectoryExists(ObjectPath.Parse(BackupRootFileNames.Primary)))
        {
            throw new YabtSyncException("Restore destination metadata path must be a file, not a folder.");
        }

        if (!await destination.Store.ExistsAsync(BackupRootFileNames.Primary, cancellationToken))
        {
            return new ArchiveLayout(HistPrefix: defaultHistoryPrefix);
        }

        await using var content = await destination.Store.OpenReadAsync
        (
            BackupRootFileNames.Primary,
            cancellationToken
        );
        var document = await _backupRootSerializer.ReadDocumentAsync
        (
            content.Content,
            cancellationToken,
            sourceName: $"Restore destination: {BackupRootFileNames.Primary}"
        );
        return document.Descriptor.Layout;
    }

    internal async Task<RestoreContext> LoadArchiveRootDocumentAsync
    (
        RestoreContext context,
        ArchiveChangeManifest changeManifest,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(LoadArchiveRootDocumentAsync));

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
            _logger.LogControlMetadataOperation(
                "Reading",
                BackupRootFileNames.Primary);
            await using var content = await context.ArchiveStore.OpenReadAsync(
                BackupRootFileNames.Primary,
                cancellationToken);
            archiveDocument = await _backupRootSerializer.ReadDocumentAsync(
                content.Content,
                cancellationToken,
                sourceName: $"Archive store: {BackupRootFileNames.Primary}");
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

        ArchivePathRules.ValidateInternalLayoutPrefixes(archiveDocument.Descriptor.Layout);
        return context with
        {
            Descriptor = archiveDocument.Descriptor,
            ArchiveDocument = archiveDocument,
        };
    }

    internal async Task<RootDescriptorRestore> InspectDestinationRootDescriptorAsync
    (
        IRestoreDestination destination,
        BackupRootDocument archiveDocument,
        bool replaceRootDescriptor,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(InspectDestinationRootDescriptorAsync));

        var descriptorPath = BackupRootFileNames.Primary;
        _logger.LogControlMetadataOperation("Checking", descriptorPath);
        if (destination.DirectoryExists(ObjectPath.Parse(descriptorPath)))
        {
            throw new YabtSyncException(
                $"Restore destination metadata path '{descriptorPath}' must be a file, not a folder.");
        }

        if (!await destination.Store.ExistsAsync(descriptorPath, cancellationToken))
        {
            return new(NeedsWrite: true, MoveExistingToHistory: false);
        }

        BackupRootDocument destinationDocument;
        try
        {
            _logger.LogControlMetadataOperation("Reading", descriptorPath);
            await using var content = await destination.Store.OpenReadAsync
            (
                descriptorPath,
                cancellationToken
            );
            destinationDocument = await _backupRootSerializer.ReadDocumentAsync(
                content.Content,
                cancellationToken,
                sourceName: $"Restore destination: {descriptorPath}");
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
            _logger.LogControlMetadataOperation(
                "Determined unchanged",
                descriptorPath);
            return RootDescriptorRestore.None;
        }

        if (!string.Equals(
                archiveDocument.Descriptor.ArchiveId,
                destinationDocument.Descriptor.ArchiveId,
                StringComparison.Ordinal) ||
            !ArchivePathRules.HaveSameLayout(
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

    internal static void ValidateRootDescriptorManifestEvidence
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

    internal async Task<BackupRootDocument> ReadStoredRootDocumentAsync
    (
        IObjectStore store,
        string description,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(ReadStoredRootDocumentAsync));

        try
        {
            _logger.LogControlMetadataOperation(
                "Reading",
                BackupRootFileNames.Primary);
            await using var storedContent = await store.OpenReadAsync(
                BackupRootFileNames.Primary,
                cancellationToken);
            return await _backupRootSerializer.ReadDocumentAsync(
                storedContent.Content,
                cancellationToken,
                sourceName: $"{description} store: {BackupRootFileNames.Primary}");
        }
        catch (Exception ex)
        {
            throw new YabtSyncException(
                $"{description} root descriptor '{BackupRootFileNames.Primary}' could not be validated.",
                ex);
        }
    }

    internal async Task UploadRootDocumentAsync
    (
        IObjectStore store,
        BackupRootDocument document,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(UploadRootDocumentAsync));

        try
        {
            using var content = document.OpenRead();
            await store.UploadAsync(
                BackupRootFileNames.Primary,
                content,
                "application/json",
                ArchiveManifestStore.EmptyMetadata,
                cancellationToken);
            _logger.LogControlMetadataOperation(
                "Wrote",
                BackupRootFileNames.Primary);
        }
        catch (Exception ex)
        {
            throw new YabtSyncException(
                $"Root descriptor '{BackupRootFileNames.Primary}' could not be uploaded exactly.",
                ex);
        }
    }
}
