using Yabt.Core.Models;

namespace Yabt.Metadata;

public interface IFolderPolicyReader
{
    Task<FolderPolicy> ReadPolicyAsync
    (
        string folderPath,
        CancellationToken cancellationToken = default
    );
}
