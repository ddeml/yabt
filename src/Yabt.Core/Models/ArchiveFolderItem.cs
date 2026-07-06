namespace Yabt.Core.Models;

public sealed record ArchiveFolderItem
(
    string Name,
    string Key,
    ArchiveObjectInfo? Object = default
)
{
    public bool IsFolder => Object is null;

    public bool IsObject => Object is not null;

    public static ArchiveFolderItem CreateFolder
    (
        string name,
        string key
    ) => new
    (
        name,
        ArchiveLayout.NormalizeObjectKey(key)
    );

    public static ArchiveFolderItem CreateObject
    (
        string name,
        ArchiveObjectInfo archiveObject
    ) => new
    (
        name,
        ArchiveLayout.NormalizeObjectKey(archiveObject.Key),
        archiveObject with
        {
            Key = ArchiveLayout.NormalizeObjectKey(archiveObject.Key),
        }
    );
}
