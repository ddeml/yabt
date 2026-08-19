namespace Yabt.Core.Models;

public static class ArchiveInternalObjectKeys
{
    public const string MutationLock =
        $"{ArchiveInternalFolderNames.TemporaryUploads}/archive-mutation-lock.json";

    public const string ConditionalMutationLock =
        $"{ArchiveInternalFolderNames.TemporaryUploads}/conditional-mutation.lock";
}
