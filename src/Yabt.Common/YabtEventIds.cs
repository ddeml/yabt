namespace Yabt.Common;

public static class YabtEventIds
{
    public const int SyncRequested = 1000;
    public const int ArchiveSyncCompleted = 1001;

    public const int IgnoringTemporaryObjectDeleteException = 2000;
    public const int AbandonedFileSystemOperationFailed = 2001;
    public const int IgnoringListEnumeratorDisposeException = 2002;
    public const int IgnoringAbandonedListChunkException = 2003;

    public const int MirrorProjectedObject = 3000;
    public const int MirrorProjectionCompleted = 3001;

    public const int FallingBackToDownloadedAzureBlobMove = 4000;
}
