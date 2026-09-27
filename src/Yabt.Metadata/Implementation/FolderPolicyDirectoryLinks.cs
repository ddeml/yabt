using System.Collections.Frozen;
using Yabt.Core.Models;

namespace Yabt.Metadata.Implementation;

internal static class FolderPolicyDirectoryLinks
{
    public static IReadOnlyDictionary<string, DirectoryLinkPolicy>? Validate
    (
        IReadOnlyDictionary<string, DirectoryLinkPolicy>? directoryLinks
    )
    {
        if (directoryLinks is null || directoryLinks.Count == 0) { return null; }

        var snapshot = new Dictionary<string, DirectoryLinkPolicy>(StringComparer.Ordinal);
        foreach (var pair in directoryLinks)
        {
            var name = pair.Key;
            if (string.IsNullOrEmpty(name) ||
                name is "." or ".." ||
                name.IndexOfAny(['/', '\\', '\0']) >= 0)
            {
                throw new YabtMetadataException(
                    $"Directory link policy key '{name}' must name one immediate child directory.");
            }

            var link = pair.Value;
            if (link is null ||
                !string.Equals(link.Behavior, DirectoryLinkPolicy.FollowBehavior, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(link.ExpectedTarget) ||
                link.ExpectedTarget.Contains('\0', StringComparison.Ordinal))
            {
                throw new YabtMetadataException(
                    $"Directory link policy '{name}' requires behavior 'follow' and a nonempty expectedTarget.");
            }

            snapshot.Add(name, link);
        }

        return snapshot.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
