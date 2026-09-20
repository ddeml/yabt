namespace Yabt.Common;

public static class YabtEventIds
{
    public const int BackupRequested = 1000;
    public const int SyncRequested = BackupRequested;
    public const int ArchiveSyncCompleted = 1001;
    public const int MultipleTargetStoresWithoutSelection = 1002;
    public const int InvalidChangeManifestIgnored = 1003;
    public const int IgnoringRestoreTemporaryPathDeleteException = 1004;
    public const int RestoreRequested = 1005;
    public const int RestorePathVerificationRequested = 1006;
    public const int RestorePathVerificationCompleted = 1007;
    public const int ArchiveSyncIncomplete = 1008;

    public const int ArchiveObjectUnchanged = 1100;
    public const int BackupObjectAdded = 1101;
    public const int BackupObjectChanged = 1102;
    public const int BackupObjectHistorized = 1103;
    public const int BackupWouldAddObject = 1104;
    public const int BackupWouldChangeObject = 1105;
    public const int BackupWouldHistorizeObject = 1106;
    public const int VerifyDifference = 1107;
    public const int RestoreItemWritten = 1108;
    public const int RestoreWouldWriteItem = 1109;
    public const int RestoreItemHistorized = 1110;
    public const int RestoreWouldHistorizeItem = 1111;
    public const int RestoreItemUnchanged = 1112;
    public const int ControlMetadataOperation = 1113;
    public const int ObjectRead = 1114;
    public const int EmptyDirectoryUnchanged = 1115;
    public const int BackupEmptyDirectoryCreated = 1116;
    public const int BackupEmptyDirectoryChanged = 1117;
    public const int BackupEmptyDirectoryRemoved = 1118;
    public const int BackupWouldCreateEmptyDirectory = 1119;
    public const int BackupWouldChangeEmptyDirectory = 1120;
    public const int BackupWouldRemoveEmptyDirectory = 1121;
    public const int VerifyEmptyDirectoryDifference = 1122;
    public const int RestorePathDifference = 1123;
    public const int RestorePathItemUnchanged = 1124;
    public const int SyncItemReadFailed = 1125;

    public const int HistoryObjectRead = 1200;
    public const int HistoryWouldDeduplicateObject = 1201;
    public const int HistoryObjectDeduplicated = 1202;
    public const int HistoryObjectUnchanged = 1203;
    public const int HistoryControlMetadataOperation = 1204;

    public const int IgnoringTemporaryObjectDeleteException = 2000;
    public const int AbandonedFileSystemOperationFailed = 2001;
    public const int IgnoringListEnumeratorDisposeException = 2002;
    public const int IgnoringAbandonedListChunkException = 2003;

    public const int FileSystemStoreReady = 2100;
    public const int FileSystemObjectUpload = 2101;
    public const int FileSystemObjectRead = 2102;
    public const int FileSystemObjectConditionalReplace = 2103;
    public const int FileSystemObjectConditionalDelete = 2104;
    public const int FileSystemObjectExists = 2105;
    public const int FileSystemFolderList = 2106;
    public const int FileSystemObjectMove = 2107;
    public const int FileSystemFolderMove = 2108;
    public const int FileSystemConditionalMutationRead = 2109;
    public const int FileSystemArchiveMutationLockAcquire = 2110;
    public const int FileSystemPlumbingOperation = 2111;

    public const int MirrorProjectedObject = 3000;
    public const int MirrorProjectionCompleted = 3001;
    public const int IgnoringZipRestoreTemporaryPathDeleteException = 3100;

    public const int ZipPackageRead = 3200;
    public const int ZipEntryRead = 3201;
    public const int ZipSourceObjectRead = 3202;
    public const int ZipRestoreArtifactRead = 3203;
    public const int ZipEmbeddedManifestRead = 3204;
    public const int ZipAdjacentManifestRead = 3205;
    public const int ZipProjectedArtifactRead = 3206;
    public const int ZipTemporaryPlumbingOperation = 3207;

    public const int FallingBackToDownloadedAzureBlobMove = 4000;

    public const int AzureBlobStoreReady = 4100;
    public const int AzureBlobObjectUpload = 4101;
    public const int AzureBlobObjectRead = 4102;
    public const int AzureBlobObjectConditionalReplace = 4103;
    public const int AzureBlobObjectConditionalDelete = 4104;
    public const int AzureBlobObjectExists = 4105;
    public const int AzureBlobFolderList = 4106;
    public const int AzureBlobObjectMove = 4107;
    public const int AzureBlobFolderMove = 4108;
    public const int AzureBlobConditionalMutationRead = 4109;

    public const int WebDavStoreReady = 5100;
    public const int WebDavObjectUpload = 5101;
    public const int WebDavObjectRead = 5102;
    public const int WebDavObjectConditionalReplace = 5103;
    public const int WebDavObjectConditionalDelete = 5104;
    public const int WebDavObjectExists = 5105;
    public const int WebDavFolderList = 5106;
    public const int WebDavPathMove = 5107;
    public const int WebDavConditionalMutationRead = 5108;

    public const int BackupRootStartPathCheck = 6000;
    public const int BackupRootDescriptorCheck = 6001;
    public const int BackupRootDescriptorRead = 6002;
    public const int FolderPolicyCheck = 6003;
    public const int FolderPolicyRead = 6004;
    public const int FolderPolicyDefault = 6005;
}
