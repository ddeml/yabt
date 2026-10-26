using Yabt.Core.Abstractions;
using Yabt.Core.Models;

namespace Yabt.Sync.Tests.Implementation;

internal sealed class DeployedBackupRootStoreResolver
(
    string storeKind,
    IObjectStore _store
) : IBackupRootStoreResolver
{
    public string StoreKind { get; } = storeKind;

    public IObjectStore ResolveStore(BackupRootStore store, string descriptorRootPath) => _store;
}
