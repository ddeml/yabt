using Yabt.Core.Models;

namespace Yabt.Sync.Implementation;

internal static class RestoreHistoryProtection
{
    internal static IEnumerable<string> GetAncestors(ArchiveLayout layout)
    {
        if (!string.IsNullOrEmpty(layout.LivePrefix)) { yield break; }
        for (var path = ObjectPath.Parse(layout.HistPrefix).Parent; !path.IsRoot; path = path.Parent)
        {
            yield return path.Value;
        }
    }

    internal static bool IsProtectedDirectory(string path, ArchiveLayout layout) =>
        string.IsNullOrEmpty(layout.LivePrefix) &&
        ArchivePathRules.IsStrictPrefixAncestor(path, layout.HistPrefix);
}
