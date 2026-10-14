using Microsoft.Extensions.Logging;
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

    private string ResolvePhysicalPath(string path)
    {
        _logger.LogTrace(nameof(ResolvePhysicalPath));

        var fullPath = Path.GetFullPath(path);
        try
        {
            var rootPath = Path.GetPathRoot(fullPath) ??
                throw new IOException($"Path '{fullPath}' does not have a filesystem root.");
            var relativePath = Path.GetRelativePath(rootPath, fullPath);
            var segments = relativePath.Split
            (
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries
            );
            var currentPath = rootPath;
            for (var segmentIndex = 0; segmentIndex < segments.Length; segmentIndex++)
            {
                var candidatePath = Path.Combine(currentPath, segments[segmentIndex]);
                FileSystemInfo? fileSystemInfo = Directory.Exists(candidatePath) ?
                    new DirectoryInfo(candidatePath) :
                    File.Exists(candidatePath) ? new FileInfo(candidatePath) : null;
                if (fileSystemInfo is null)
                {
                    for (; segmentIndex < segments.Length; segmentIndex++)
                    {
                        currentPath = Path.Combine(currentPath, segments[segmentIndex]);
                    }

                    break;
                }

                if ((fileSystemInfo.Attributes & FileAttributes.ReparsePoint) == 0)
                {
                    currentPath = candidatePath;
                    continue;
                }

                var linkTarget = fileSystemInfo.ResolveLinkTarget(returnFinalTarget: true) ??
                    throw new IOException($"Reparse point '{candidatePath}' does not expose a resolvable target.");
                currentPath = Path.GetFullPath(linkTarget.FullName);
            }

            return Path.GetFullPath(currentPath);
        }
        catch (Exception ex)
        {
            throw new YabtFileSystemException
            (
                $"Restore could not safely resolve filesystem path '{fullPath}'.",
                path: fullPath,
                innerException: ex
            );
        }
    }
}
