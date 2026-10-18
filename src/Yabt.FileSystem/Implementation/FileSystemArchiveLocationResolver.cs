using Microsoft.Extensions.Logging;
using Yabt.Core;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.Metadata;

namespace Yabt.FileSystem.Implementation;

internal sealed class FileSystemArchiveLocationResolver
(
    IBackupRootLocator _backupRootLocator,
    IFolderPolicyReader _folderPolicyReader,
    IEnumerable<ISourceRootObjectStoreResolver> sourceRootObjectStoreResolvers,
    ILogger<FileSystemArchiveLocationResolver> _logger
) : IArchiveLocationResolver
{
    private const int MaximumResolvedDirectoryLinks = 64;

    private readonly ISourceRootObjectStoreResolver _sourceRootObjectStoreResolver =
        sourceRootObjectStoreResolvers.SingleOrDefault() ??
        throw new YabtFileSystemException("Exactly one source root object store resolver must be registered.");

    public async Task<ArchiveSourceLocation> LocateSourceAsync
    (
        string input,
        bool requireRoot = false,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(LocateSourceAsync));

        FileSystemNativePath.ValidateSpelling(input);
        var selectedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(input));
        var root = await _backupRootLocator.LocateRootAsync(selectedPath, cancellationToken);
        var selectionPrefix = ToObjectPath(Path.GetRelativePath(root.RootPath, selectedPath));
        if (requireRoot && !selectionPrefix.IsRoot)
        {
            throw new YabtFileSystemException
            (
                "Restore currently requires the source-root argument to be the folder containing .yabt-root.json.",
                path: input
            );
        }

        var displayName = Path.GetFileName(Path.TrimEndingDirectorySeparator(selectedPath));
        return new
        (
            root,
            CreateSourcePrefix(selectionPrefix, root.Descriptor.Layout),
            selectionPrefix,
            displayName.Length == 0 ? "root" : displayName,
            _sourceRootObjectStoreResolver.ResolveSourceRoot(root.RootPath)
        );
    }

    public void ValidateBackupLocations(ArchiveSourceLocation source, IObjectStore targetStore)
    {
        _logger.LogTrace(nameof(ValidateBackupLocations));

        // Other providers may expose this filesystem, but their endpoints alone cannot prove it.
        if (targetStore is not ILocalObjectStore localArchive) { return; }

        var archiveRoot = ResolvePhysicalPath(localArchive.RootPath, requireKnownLinkTargets: false);
        var sourceRoot = ResolvePhysicalPath
        (
            ToNativePath(source.Root.RootPath, source.SourcePrefix),
            requireKnownLinkTargets: false
        );
        if
        (
            FileSystemObjectStoreNamespace.IsNativePathUnder(archiveRoot, sourceRoot, allowUnknownComparison: true) ||
            FileSystemObjectStoreNamespace.IsNativePathUnder(sourceRoot, archiveRoot, allowUnknownComparison: true)
        )
        {
            throw CreateBackupOverlapException(sourceRoot, archiveRoot, "selected source data");
        }

        // Descriptor and initial policy reads can be outside an explicit live prefix. Protect
        // their containing directories without excluding a safe sibling target below that root.
        var descriptorRoot = ResolvePhysicalPath(source.Root.RootPath, requireKnownLinkTargets: false);
        if (FileSystemObjectStoreNamespace.IsNativePathUnder(descriptorRoot, archiveRoot, allowUnknownComparison: true))
        {
            throw CreateBackupOverlapException(descriptorRoot, archiveRoot, "source root metadata");
        }

        var policyRoot = ResolvePhysicalPath
        (
            ToNativePath(source.Root.RootPath, source.SelectionPrefix),
            requireKnownLinkTargets: false
        );
        if (FileSystemObjectStoreNamespace.IsNativePathUnder(policyRoot, archiveRoot, allowUnknownComparison: true))
        {
            throw CreateBackupOverlapException(policyRoot, archiveRoot, "source folder policy");
        }
    }

    private static YabtFileSystemException CreateBackupOverlapException
    (
        string sourcePath,
        string archivePath,
        string sourceDescription
    )
    {
        var message = $"Backup {sourceDescription} at '{sourcePath}' overlaps target storage " +
            $"at '{archivePath}'. Choose a target outside the source read locations.";
        return new(message, path: sourcePath, innerException: new ObjectStoreOverlapException(message));
    }

    public void ConfigureSourceTraversal(ArchiveSourceLocation source, IObjectStore targetStore)
    {
        _logger.LogTrace(nameof(ConfigureSourceTraversal));

        if (source.SourceStore is not ISourceDirectoryTraversal sourceTraversal) { return; }

        var forbiddenRoots = new List<string>
        {
            ToNativePath(source.Root.RootPath, ObjectPath.Parse(source.Root.Descriptor.Layout.HistPrefix)),
            ToNativePath(source.Root.RootPath, ObjectPath.Parse(ArchiveInternalFolderNames.TemporaryUploads)),
        };
        if (targetStore is ILocalObjectStore localArchive)
        {
            forbiddenRoots.Add(localArchive.RootPath);
        }

        sourceTraversal.ConfigureSourceTraversal
        (
            (folderPrefix, cancellationToken) => _folderPolicyReader.ReadPolicyAsync
            (
                ToNativePath(source.Root.RootPath, ObjectPath.Parse(folderPrefix)),
                cancellationToken
            ),
            forbiddenRoots
        );
    }

    public async Task<FolderPolicy> ReadPolicyAsync
    (
        ArchiveSourceLocation source,
        ObjectPath prefix,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(ReadPolicyAsync));

        // Validate linked ancestors before the native reader can open a policy inside them.
        // Traversal's own callback reads only the already validated containing parent's policy.
        if (source.SourceStore is ISourceDirectoryTraversal)
        {
            await source.SourceStore.ExistsAsync
            (
                prefix.Combine(ObjectPath.Parse(FolderPolicyFileNames.Primary)).Value,
                cancellationToken
            );
        }

        return await _folderPolicyReader.ReadPolicyAsync
        (
            ToNativePath(source.Root.RootPath, prefix),
            cancellationToken
        );
    }

    public void ValidateRestoreLocations
    (
        ArchiveSourceLocation descriptorLocation,
        IRestoreDestination destination,
        IObjectStore archiveStore
    )
    {
        _logger.LogTrace(nameof(ValidateRestoreLocations));

        var descriptorRoot = ResolvePhysicalPath(descriptorLocation.Root.RootPath);
        var destinationRoot = ResolvePhysicalPath(destination.RootPath);
        var destinationUnderSource = FileSystemObjectStoreNamespace.IsNativePathUnder(destinationRoot, descriptorRoot);
        var sourceUnderDestination = FileSystemObjectStoreNamespace.IsNativePathUnder(descriptorRoot, destinationRoot);
        if (destinationUnderSource != sourceUnderDestination)
        {
            throw new YabtFileSystemException
            (
                "Restore destination may be the configured source root itself, but it must not be " +
                    "one of that root's ancestors or descendants. " +
                    $"The source root resolves to '{descriptorRoot}', and the destination " +
                    $"resolves to '{destinationRoot}'. Use a separate sibling folder instead."
            );
        }

        if (archiveStore is not ILocalObjectStore localArchive) { return; }

        var archiveRoot = ResolvePhysicalPath(localArchive.RootPath);
        if
        (
            FileSystemObjectStoreNamespace.IsNativePathUnder(archiveRoot, destinationRoot) ||
            FileSystemObjectStoreNamespace.IsNativePathUnder(destinationRoot, archiveRoot)
        )
        {
            throw new YabtFileSystemException
            (
                "Restore destination must not be the selected filesystem archive or one of its " +
                    "ancestors or descendants."
            );
        }
    }

    private static ObjectPath CreateSourcePrefix(ObjectPath selectionPrefix, ArchiveLayout layout)
    {
        var livePrefix = ObjectPath.Parse(layout.LivePrefix);
        return selectionPrefix.IsUnder(livePrefix) ? selectionPrefix : livePrefix.Combine(selectionPrefix);
    }

    private static ObjectPath ToObjectPath(string relativePath) => relativePath == "." ?
        ObjectPath.Root : ObjectPath.Parse(relativePath.Replace(Path.DirectorySeparatorChar, '/'));

    private static string ToNativePath(string rootPath, ObjectPath path) =>
        Path.Combine(rootPath, path.Value.Replace('/', Path.DirectorySeparatorChar));

    private string ResolvePhysicalPath(string path, bool requireKnownLinkTargets = true)
    {
        _logger.LogTrace(nameof(ResolvePhysicalPath));

        var fullPath = Path.GetFullPath(path);
        try
        {
            var pendingPath = fullPath;
            var resolvedLinkCount = 0;
            while (true)
            {
                var rootPath = Path.GetPathRoot(pendingPath) ??
                    throw new IOException($"Path '{pendingPath}' does not have a filesystem root.");
                var relativePath = Path.GetRelativePath(rootPath, pendingPath);
                var segments = relativePath == "." ? [] : relativePath.Split
                (
                    [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                    StringSplitOptions.RemoveEmptyEntries
                );
                var currentPath = rootPath;
                var followedLink = false;
                for (var segmentIndex = 0; segmentIndex < segments.Length; segmentIndex++)
                {
                    var candidatePath = Path.Combine(currentPath, segments[segmentIndex]);
                    var attributes = FileSystemPathSafety.TryGetAttributes(candidatePath);
                    if (!attributes.HasValue)
                    {
                        for (; segmentIndex < segments.Length; segmentIndex++)
                        {
                            currentPath = Path.Combine(currentPath, segments[segmentIndex]);
                        }

                        return currentPath;
                    }

                    if ((attributes.Value & FileAttributes.ReparsePoint) == 0)
                    {
                        currentPath = candidatePath;
                        continue;
                    }

                    FileSystemInfo fileSystemInfo = (attributes.Value & FileAttributes.Directory) != 0 ?
                        new DirectoryInfo(candidatePath) : new FileInfo(candidatePath);
                    var linkTarget = fileSystemInfo.ResolveLinkTarget(returnFinalTarget: false);
                    if (linkTarget is null)
                    {
                        if (requireKnownLinkTargets)
                        {
                            throw new IOException($"Reparse point '{candidatePath}' does not expose a resolvable target.");
                        }

                        // An unsupported reparse tag provides no alias evidence. Ordinary source
                        // access and existing descendant-link safety rules still apply separately.
                        currentPath = candidatePath;
                        continue;
                    }

                    // Resolve one link at a time, including links in its target's ancestors.
                    // Bound substitutions so cyclic or excessively indirect paths cannot loop.
                    if (++resolvedLinkCount > MaximumResolvedDirectoryLinks)
                    {
                        throw new IOException($"Filesystem path '{fullPath}' exceeds the directory-link resolution limit.");
                    }

                    pendingPath = Path.GetFullPath(linkTarget.FullName);
                    for (var tailIndex = segmentIndex + 1; tailIndex < segments.Length; tailIndex++)
                    {
                        pendingPath = Path.Combine(pendingPath, segments[tailIndex]);
                    }

                    followedLink = true;
                    break;
                }

                if (!followedLink) { return currentPath; }
            }
        }
        catch (Exception ex)
        {
            throw new YabtFileSystemException
            (
                $"Archive operation could not safely resolve filesystem path '{fullPath}'.",
                path: fullPath,
                innerException: ex
            );
        }
    }
}
