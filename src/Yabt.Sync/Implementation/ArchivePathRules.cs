using System.Collections.Frozen;
using Yabt.Core.Models;
using Yabt.Metadata;

namespace Yabt.Sync.Implementation;

internal static class ArchivePathRules
{
    private static readonly FrozenSet<string> ReservedRootObjectKeys = new[]
    {
        BackupRootFileNames.Primary,
        ArchiveChangeManifest.UncompressedFileName,
        ArchiveChangeManifest.BrotliFileName,
        ArchiveChangeManifest.InvalidationMarkerFileName,
        ArchiveLogicalStateManifest.FileName,
        ArchiveLogicalStateManifest.InvalidationMarkerFileName,
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    internal static bool HaveSameLayout(ArchiveLayout first, ArchiveLayout second) =>
        string.Equals(
            ArchiveLayout.NormalizeObjectKey(first.LivePrefix),
            ArchiveLayout.NormalizeObjectKey(second.LivePrefix),
            StringComparison.Ordinal) &&
        string.Equals(
            ArchiveLayout.NormalizeObjectKey(first.HistPrefix),
            ArchiveLayout.NormalizeObjectKey(second.HistPrefix),
            StringComparison.Ordinal);

    internal static FrozenSet<string> CreateInternalObjectKeys(ArchiveLayout layout)
    {
        if (!string.IsNullOrEmpty(ArchiveLayout.NormalizeObjectKey(layout.LivePrefix)))
        {
            return FrozenSet<string>.Empty;
        }

        return ReservedRootObjectKeys;
    }

    internal static List<string> CreateInternalObjectPrefixes(ArchiveLayout layout)
    {
        if (!string.IsNullOrEmpty(ArchiveLayout.NormalizeObjectKey(layout.LivePrefix)))
        {
            return [];
        }

        var prefixes = new List<string>();
        var histPrefix = ArchiveLayout.NormalizeObjectPrefix(layout.HistPrefix);
        if (histPrefix is not null)
        {
            prefixes.Add(histPrefix);
        }

        prefixes.Add(ArchiveInternalFolderNames.TemporaryUploads);
        return prefixes;
    }

    internal static void ValidateInternalLayoutPrefixes(ArchiveLayout layout)
    {
        var temporaryPrefix = ArchiveInternalFolderNames.TemporaryUploads;
        var livePrefix = ArchiveLayout.NormalizeObjectPrefix(layout.LivePrefix);
        if (livePrefix is not null && PrefixesOverlap(livePrefix, temporaryPrefix))
        {
            throw new YabtSyncException(
                $"Archive live prefix '{livePrefix}' conflicts with reserved internal prefix " +
                    $"'{temporaryPrefix}'.");
        }

        var histPrefix = ArchiveLayout.NormalizeObjectPrefix(layout.HistPrefix);
        if (histPrefix is null)
        {
            throw new YabtSyncException("Archive operations require a nonempty history prefix.");
        }

        if (PrefixesOverlap(histPrefix, temporaryPrefix))
        {
            throw new YabtSyncException(
                $"Archive history prefix '{histPrefix}' conflicts with reserved internal prefix " +
                    $"'{temporaryPrefix}'.");
        }

        foreach (var reservedRootObjectKey in ReservedRootObjectKeys)
        {
            if (livePrefix is not null &&
                PrefixesOverlap(livePrefix, reservedRootObjectKey))
            {
                throw new YabtSyncException(
                    $"Archive live prefix '{livePrefix}' conflicts with reserved root object " +
                        $"'{reservedRootObjectKey}'.");
            }

            if (PrefixesOverlap(histPrefix, reservedRootObjectKey))
            {
                throw new YabtSyncException(
                    $"Archive history prefix '{histPrefix}' conflicts with reserved root object " +
                        $"'{reservedRootObjectKey}'.");
            }
        }

        if (livePrefix is not null &&
            histPrefix is not null &&
            PrefixesOverlap(livePrefix, histPrefix))
        {
            throw new YabtSyncException(
                $"Archive live prefix '{livePrefix}' overlaps history prefix '{histPrefix}'.");
        }
    }

    private static bool PrefixesOverlap(string firstPrefix, string secondPrefix) =>
        IsSameOrUnderPrefix(firstPrefix, secondPrefix) ||
        IsSameOrUnderPrefix(secondPrefix, firstPrefix);

    internal static void ValidateProjectedLiveObjectPath
    (
        string relativePath,
        ArchiveLayout layout
    )
    {
        var liveObjectKey = layout.ToLiveObjectKey(relativePath);
        if (IsInternalObject(liveObjectKey, layout) ||
            IsStrictPrefixAncestor(liveObjectKey, layout.HistPrefix))
        {
            throw new YabtSyncException(
                $"Projected live object '{relativePath}' conflicts with archive history, " +
                    "metadata, or provider plumbing.");
        }
    }

    internal static void ValidateProjectedLiveFolderPath(string relativePath, ArchiveLayout layout)
    {
        var liveFolderKey = layout.ToLiveObjectKey(relativePath);
        if (IsInternalObjectPrefix(liveFolderKey, CreateInternalObjectPrefixes(layout)))
        {
            throw new YabtSyncException
            (
                $"Projected live folder '{relativePath}' conflicts with archive history or provider plumbing."
            );
        }
    }

    internal static bool IsStrictPrefixAncestor(string objectKey, string prefix)
    {
        var normalizedObjectKey = ArchiveLayout.NormalizeObjectKey(objectKey);
        var normalizedPrefix = ArchiveLayout.NormalizeObjectKey(prefix);
        return !string.IsNullOrEmpty(normalizedObjectKey) &&
            normalizedPrefix.StartsWith(
                $"{normalizedObjectKey}/",
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSameOrUnderPrefix(string objectKey, string prefix)
    {
        var normalizedObjectKey = ArchiveLayout.NormalizeObjectKey(objectKey);
        var normalizedPrefix = ArchiveLayout.NormalizeObjectKey(prefix);
        return string.Equals(
                normalizedObjectKey,
                normalizedPrefix,
                StringComparison.OrdinalIgnoreCase) ||
            normalizedObjectKey.StartsWith(
                $"{normalizedPrefix}/",
                StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsInternalObject(string objectKey, ArchiveLayout layout)
    {
        return IsInternalObjectKey(objectKey, CreateInternalObjectKeys(layout)) ||
            IsInternalObjectPrefix(objectKey, CreateInternalObjectPrefixes(layout));
    }

    internal static bool IsInternalObjectKey
    (
        string objectKey,
        IEnumerable<string> internalObjectKeys
    )
    {
        foreach (var internalObjectKey in internalObjectKeys)
        {
            if (string.Equals(
                    objectKey,
                    ArchiveLayout.NormalizeObjectKey(internalObjectKey),
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsInternalObjectPrefix
    (
        string objectKey,
        IEnumerable<string> internalObjectPrefixes
    )
    {
        foreach (var internalObjectPrefix in internalObjectPrefixes)
        {
            var normalizedPrefix = ArchiveLayout.NormalizeObjectPrefix(internalObjectPrefix);
            if (normalizedPrefix is not null &&
                IsSameOrUnderPrefix(objectKey, normalizedPrefix))
            {
                return true;
            }
        }

        return false;
    }

    internal static int GetPathDepth(string path) => path.Count(character => character == '/');

    internal static string GetParentPrefix(string relativePath)
    {
        var normalizedRelativePath = ArchiveLayout.NormalizeObjectKey(relativePath);
        var separator = normalizedRelativePath.LastIndexOf('/');

        return separator < 0 ? string.Empty : normalizedRelativePath[..separator];
    }

    internal static bool IsEmptyFolderMarker(string relativePath)
    {
        var normalizedRelativePath = ArchiveLayout.NormalizeObjectKey(relativePath);
        var separator = normalizedRelativePath.LastIndexOf('/');
        var name = separator < 0 ?
            normalizedRelativePath :
            normalizedRelativePath[(separator + 1)..];

        return string.Equals(
            name,
            ArchiveFolderMarkerFileNames.EmptyFolder,
            StringComparison.Ordinal);
    }

    internal static bool IsYabtControlMetadataPath
    (
        string relativePath,
        bool isDirectory = false,
        ArchiveProjectionProvenance? projection = null
    )
    {
        if (isDirectory) { return false; }

        if (string.Equals(
                projection?.ArtifactRole,
                ArchiveProjectionArtifactRoles.Manifest,
                StringComparison.Ordinal))
        {
            return true;
        }

        var normalizedRelativePath = ArchiveLayout.NormalizeObjectKey(relativePath);
        var separator = normalizedRelativePath.LastIndexOf('/');
        var name = separator < 0 ?
            normalizedRelativePath :
            normalizedRelativePath[(separator + 1)..];

        return ReservedRootObjectKeys.Contains(normalizedRelativePath) ||
            string.Equals(
                name,
                FolderPolicyFileNames.Primary,
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                name,
                ArchiveFolderMarkerFileNames.EmptyFolder,
                StringComparison.OrdinalIgnoreCase);
    }

    internal static string GetLogicalEmptyDirectoryPath(string markerRelativePath)
    {
        var directoryPath = GetParentPrefix(markerRelativePath);
        return string.IsNullOrEmpty(directoryPath) ? "." : directoryPath;
    }
}
