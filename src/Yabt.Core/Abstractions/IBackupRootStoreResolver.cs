using Yabt.Core.Models;

namespace Yabt.Core.Abstractions;

public interface IBackupRootStoreResolver
{
    string StoreKind { get; }

    IObjectStore ResolveStore
    (
        BackupRootStore store,
        string descriptorRootPath
    );
}
