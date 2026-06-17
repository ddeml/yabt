namespace Yabt.Metadata;

public interface IBackupRootLocator
{
    Task<BackupRootLocation> LocateRootAsync
    (
        string startPath,
        CancellationToken cancellationToken = default
    );
}
