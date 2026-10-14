using Yabt.Core.Models;

namespace Yabt.Core.Abstractions;

/// <summary>Provider-owned native destination operations used by restore.</summary>
public interface IRestoreDestination
{
    /// <summary>Display identity; consumers must not interpret it as an object key or native path.</summary>
    string RootPath { get; }

    IObjectStore Store { get; }

    IObjectStoreNamespace Namespace { get; }

    Task EnsureSafeAsync
    (
        string? livePrefix,
        string? historyPrefix,
        CancellationToken cancellationToken = default
    );

    void ValidateRelativePath(string relativePath);

    bool DirectoryExists(ObjectPath path);

    Task CreateDirectoryAsync
    (
        string? relativePath,
        CancellationToken cancellationToken = default
    );

    Task<IObjectStoreStagingArea> CreateStagingAreaAsync(CancellationToken cancellationToken = default);

    Task CommitStagedFileAsync
    (
        string relativePath,
        string stagingToken,
        DateTimeOffset? lastModifiedUtc,
        CancellationToken cancellationToken = default
    );
}
