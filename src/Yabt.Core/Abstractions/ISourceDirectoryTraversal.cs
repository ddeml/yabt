using Yabt.Core.Models;

namespace Yabt.Core.Abstractions;

/// <summary>
/// Configures explicitly approved directory-link reads for one resolved source store.
/// Archive and destination mutations never inherit these source permissions.
/// </summary>
public interface ISourceDirectoryTraversal
{
    void ConfigureSourceTraversal
    (
        Func<string, CancellationToken, Task<FolderPolicy>> readFolderPolicyAsync,
        IEnumerable<string>? forbiddenPhysicalRootPaths = null
    );
}
