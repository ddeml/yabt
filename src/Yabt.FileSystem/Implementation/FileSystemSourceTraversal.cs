using Yabt.Core;
using Yabt.Core.Models;

namespace Yabt.FileSystem.Implementation;

internal sealed class FileSystemSourceTraversal
(
    Func<string, CancellationToken, Task<FolderPolicy>> _readFolderPolicyAsync,
    IEnumerable<string>? forbiddenPhysicalRootPaths
)
{
    private readonly string[] _forbiddenRootPaths = forbiddenPhysicalRootPaths?
        .Select(Path.GetFullPath).ToArray() ?? [];

    public async Task ValidatePathAsync
    (
        string rootPath,
        string path,
        CancellationToken cancellationToken = default
    )
    {
        var relativePath = Path.GetRelativePath(rootPath, path);
        if
        (
            Path.IsPathRooted(relativePath) || relativePath == ".." ||
            relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        )
        {
            throw new YabtFileSystemException("Source path is outside its configured root.", path: path);
        }

        if (relativePath == ".") { return; }

        var ancestorPaths = new List<string> { rootPath };
        HashSet<string>? ancestorIdentities = null;
        FileSystemDirectoryIdentity? previousIdentity = null;
        var parentPath = rootPath;
        var parentKey = string.Empty;
        foreach (var segment in relativePath.Split(Path.DirectorySeparatorChar))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentPath = Path.Combine(parentPath, segment);
            var attributes = FileSystemPathSafety.TryGetAttributes(currentPath);
            if (!attributes.HasValue) { return; }
            var isDirectory = (attributes.Value & FileAttributes.Directory) != 0;
            var isLink = (attributes.Value & FileAttributes.ReparsePoint) != 0;
            if (isLink && !isDirectory)
            {
                throw new YabtFileSystemException
                (
                    $"Source file '{currentPath}' is a link or reparse point and cannot be read safely.",
                    path: currentPath
                );
            }

            if (isDirectory)
            {
                // Ordinary source trees do not depend on platform-specific identity APIs.
                // At the first link, reconstruct every ancestor before permitting traversal.
                if (isLink && ancestorIdentities is null)
                {
                    ancestorIdentities = ancestorPaths
                        .Select(ancestor => FileSystemDirectoryIdentity.Read(ancestor).Id)
                        .ToHashSet(StringComparer.Ordinal);
                }

                if (ancestorIdentities is not null)
                {
                    var identity = FileSystemDirectoryIdentity.Read(currentPath);
                    if
                    (
                        !isLink && previousIdentity?.MountId is not null &&
                        previousIdentity.MountId != identity.MountId
                    )
                    {
                        throw new YabtFileSystemException
                        (
                            $"Source directory '{currentPath}' crosses a mount boundary inside an approved linked tree. " +
                            "Following nested mount points is not supported.", path: currentPath
                        );
                    }
                    if (!ancestorIdentities.Add(identity.Id))
                    {
                        throw new YabtFileSystemException
                        (
                            $"Source directory '{currentPath}' repeats a physical ancestor and creates a traversal cycle.",
                            path: currentPath
                        );
                    }

                    if (isLink)
                    {
                        await ValidateDirectoryLinkAsync
                        (
                            parentPath, parentKey, segment, currentPath, identity, cancellationToken
                        );
                    }
                    previousIdentity = identity;
                }
                ancestorPaths.Add(currentPath);
            }

            parentPath = currentPath;
            parentKey = string.IsNullOrEmpty(parentKey) ? segment : $"{parentKey}/{segment}";
        }
    }

    private async Task ValidateDirectoryLinkAsync
    (
        string parentPath,
        string parentKey,
        string childName,
        string linkPath,
        FileSystemDirectoryIdentity identity,
        CancellationToken cancellationToken
    )
    {
        // The parent has already been validated. Its policy cannot itself redirect the read.
        var policyPath = Path.Combine(parentPath, ".yabt-policy.json");
        var policyAttributes = FileSystemPathSafety.TryGetAttributes(policyPath);
        if
        (
            policyAttributes.HasValue &&
            (policyAttributes.Value & FileAttributes.ReparsePoint) != 0
        )
        {
            throw new YabtFileSystemException
            (
                $"Source directory-link policy '{policyPath}' is a link or reparse point.", path: policyPath
            );
        }

        var policy = await _readFolderPolicyAsync(parentKey, cancellationToken);
        var approval = policy.DirectoryLinks?
            .FirstOrDefault(pair => string.Equals(pair.Key, childName, StringComparison.Ordinal)).Value;
        if (approval is null || !string.Equals(approval.Behavior, DirectoryLinkPolicy.FollowBehavior, StringComparison.Ordinal))
        {
            throw new YabtFileSystemException
            (
                $"Source directory link '{linkPath}' has no explicit follow approval in its parent's .yabt-policy.json.",
                path: linkPath
            );
        }

        var physicalParentPath = FileSystemDirectoryIdentity.Read(parentPath).PhysicalPath;
        var expectedPath = Path.GetFullPath(approval.ExpectedTarget, physicalParentPath);
        var expectedIdentity = FileSystemDirectoryIdentity.Read(expectedPath);
        if
        (
            !string.Equals(identity.Id, expectedIdentity.Id, StringComparison.Ordinal) ||
            !string.Equals(identity.PhysicalPath, expectedIdentity.PhysicalPath, GetPathComparison())
        )
        {
            throw new YabtFileSystemException
            (
                $"Source directory link '{linkPath}' does not point to its approved expected target '{approval.ExpectedTarget}'.",
                path: linkPath
            );
        }

        foreach (var forbiddenPath in _forbiddenRootPaths)
        {
            var physicalForbiddenPath = FileSystemDirectoryIdentity.ResolvePhysicalPath(forbiddenPath);
            if
            (
                IsSameOrDescendant(identity.PhysicalPath, physicalForbiddenPath) ||
                IsSameOrDescendant(physicalForbiddenPath, identity.PhysicalPath) ||
                HasIdentityOverlap(identity, physicalForbiddenPath)
            )
            {
                var message = $"Source directory link '{linkPath}' overlaps reserved or archive storage '{forbiddenPath}'.";
                throw new YabtFileSystemException
                (
                    message,
                    path: linkPath,
                    innerException: new ObjectStoreOverlapException(message)
                );
            }
            var nearestForbiddenIdentity = ReadExistingAncestorIdentities(physicalForbiddenPath).FirstOrDefault();
            if (nearestForbiddenIdentity is not null)
            {
                RejectAmbiguousAliasBoundary(identity, nearestForbiddenIdentity, linkPath);
            }
        }

        // A callback must not authorize a link that was retargeted while its policy was being read.
        var confirmedIdentity = FileSystemDirectoryIdentity.Read(linkPath);
        if (!string.Equals(identity.Id, confirmedIdentity.Id, StringComparison.Ordinal))
        {
            throw new YabtFileSystemException
            (
                $"Source directory link '{linkPath}' changed while its approval was being checked.", path: linkPath
            );
        }
    }

    private static void RejectAmbiguousAliasBoundary
    (
        FileSystemDirectoryIdentity target,
        FileSystemDirectoryIdentity forbidden,
        string linkPath
    )
    {
        if
        (
            target.MountId.HasValue && forbidden.MountId.HasValue &&
            string.Equals(target.DeviceId, forbidden.DeviceId, StringComparison.Ordinal) &&
            target.MountId != forbidden.MountId
        )
        {
            throw new YabtFileSystemException
            (
                $"Source directory link '{linkPath}' and reserved or archive storage use different mounts of " +
                "the same device. Bind-mount alias containment cannot be proved safely.", path: linkPath
            );
        }
    }

    private static bool HasIdentityOverlap
    (
        FileSystemDirectoryIdentity targetIdentity,
        string forbiddenPath
    )
    {
        // Do not compare arbitrary ancestor sets: independent directories on one volume share
        // common parents. Only either boundary's own identity can establish an overlap.
        foreach (var ancestor in ReadExistingAncestorIdentities(forbiddenPath))
        {
            if (string.Equals(targetIdentity.Id, ancestor.Id, StringComparison.Ordinal)) { return true; }
        }

        if (!FileSystemPathSafety.TryGetAttributes(forbiddenPath).HasValue) { return false; }
        var forbiddenIdentity = FileSystemDirectoryIdentity.Read(forbiddenPath);
        foreach (var ancestor in ReadExistingAncestorIdentities(targetIdentity.PhysicalPath))
        {
            if (string.Equals(forbiddenIdentity.Id, ancestor.Id, StringComparison.Ordinal)) { return true; }
        }
        return false;
    }

    private static IEnumerable<FileSystemDirectoryIdentity> ReadExistingAncestorIdentities(string path)
    {
        for
        (
            string? current = Path.TrimEndingDirectorySeparator(path); current is not null;
            current = Path.GetDirectoryName(current)
        )
        {
            if (FileSystemPathSafety.TryGetAttributes(current).HasValue)
            {
                yield return FileSystemDirectoryIdentity.Read(current);
            }
        }
    }

    private static bool IsSameOrDescendant(string path, string rootPath)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(rootPath);
        return string.Equals(Path.TrimEndingDirectorySeparator(path), normalizedRoot, GetPathComparison()) ||
            path.StartsWith
            (
                Path.EndsInDirectorySeparator(normalizedRoot) ? normalizedRoot : normalizedRoot + Path.DirectorySeparatorChar,
                GetPathComparison()
            );
    }

    private static StringComparison GetPathComparison() => OperatingSystem.IsWindows() ?
        StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
