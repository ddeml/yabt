using Yabt.Core.Abstractions;
using Yabt.Core.Models;

namespace Yabt.Metadata;

/// <summary>Resolves user location inputs and owns native location and traversal rules.</summary>
public interface IArchiveLocationResolver
{
    Task<ArchiveSourceLocation> LocateSourceAsync
    (
        string input,
        bool requireRoot = false,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Rejects known overlap using bounded provider location checks before target mutation.
    /// Must not write storage, scan trees, or probe remote services. Returning normally means
    /// no overlap was detected; it does not prove separation across unknown aliases.
    /// </summary>
    void ValidateBackupLocations(ArchiveSourceLocation source, IObjectStore targetStore);

    /// <summary>Rejects temporary files inside known read or write trees without scanning them.</summary>
    void ValidateTemporaryStorageLocations
    (
        string temporaryDirectory,
        ArchiveSourceLocation? source,
        IObjectStore archiveStore,
        IRestoreDestination? destination = null
    );

    void ConfigureSourceTraversal(ArchiveSourceLocation source, IObjectStore targetStore);

    Task<FolderPolicy> ReadPolicyAsync
    (
        ArchiveSourceLocation source,
        ObjectPath prefix,
        CancellationToken cancellationToken = default
    );

    void ValidateRestoreLocations
    (
        ArchiveSourceLocation descriptorLocation,
        IRestoreDestination destination,
        IObjectStore archiveStore
    );
}
