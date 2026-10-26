using Yabt.Core.Models;

namespace Yabt.Sync.Implementation;

internal static class RestorePackageScope
{
    internal static void Validate(RestorePlan plan, string packagePath)
    {
        var packageRoot = ObjectPath.Parse(packagePath);
        if
        (
            !plan.IsDirectory(packagePath) ||
            plan.Files.Any
            (
                file => ObjectPath.Parse(file.RelativePath) == packageRoot ||
                    !ObjectPath.Parse(file.RelativePath).IsUnder(packageRoot)
            ) ||
            plan.Directories.Any
            (
                path =>
                !ObjectPath.Parse(path).IsUnder(packageRoot) &&
                !packageRoot.IsUnder(ObjectPath.Parse(path))
            )
        )
        {
            throw new YabtSyncException
            (
                $"Package '{packagePath}' produced output outside its owning logical folder."
            );
        }
    }
}
