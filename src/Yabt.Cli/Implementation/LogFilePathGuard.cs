using System.Text.Json;
using Yabt.Common;
using Yabt.Core.Models;
using Yabt.FileSystem;
using Yabt.Metadata;

namespace Yabt.Cli.Implementation;

internal sealed class LogFilePathGuard(IBackupRootLocator _backupRootLocator)
{
    public async Task EnsureOutsideOperationRootsAsync
    (
        string logFilePath,
        string commandName,
        string commandRoot,
        string? targetStoreId,
        string? destinationRoot,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await EnsureOutsideOperationRootsCoreAsync(
                logFilePath,
                commandName,
                commandRoot,
                targetStoreId,
                destinationRoot,
                cancellationToken);
        }
        catch (YabtException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when
        (
            ex is ArgumentException or
                IOException or
                NotSupportedException or
                System.Security.SecurityException or
                UnauthorizedAccessException
        )
        {
            throw new YabtCliException(
                $"Could not safely validate log file location '{logFilePath}'.",
                ex);
        }
    }

    private async Task EnsureOutsideOperationRootsCoreAsync
    (
        string logFilePath,
        string commandName,
        string commandRoot,
        string? targetStoreId,
        string? destinationRoot,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandRoot);

        var roots = new List<OperationRoot>
        {
            new("command root", commandRoot),
        };
        if (!string.IsNullOrWhiteSpace(destinationRoot))
        {
            roots.Add(new("restore destination", destinationRoot));
        }

        if (CommandUsesArchiveDescriptor(commandName))
        {
            var location = await _backupRootLocator.LocateRootAsync(
                commandRoot,
                cancellationToken);
            var selectedStore = SelectStore(location.Descriptor, targetStoreId);
            var fileSystemRoot = TryResolveFileSystemRoot(selectedStore, location.RootPath);
            if (fileSystemRoot is not null)
            {
                roots.Add(new("filesystem archive store", fileSystemRoot));
            }
        }

        var canonicalLogPath = CanonicalizePath(logFilePath, pathIsFile: true);
        foreach (var root in roots)
        {
            var canonicalRootPath = CanonicalizePath(root.Path, pathIsFile: false);
            if (IsSameOrDescendant(canonicalLogPath, canonicalRootPath) ||
                IsSameOrDescendant(canonicalRootPath, canonicalLogPath))
            {
                throw new YabtCliException(
                    $"Log file '{logFilePath}' overlaps the {root.Description} " +
                        $"'{Path.GetFullPath(root.Path)}'. Choose a path outside every " +
                        "source, archive, and restore root with --log-file=<path>.");
            }
        }
    }

    private static bool CommandUsesArchiveDescriptor(string commandName) => commandName is
        YabtCliCommandNames.Backup or
        YabtCliCommandNames.Restore or
        YabtCliCommandNames.Verify or
        YabtCliCommandNames.Deduplicate;

    private static BackupRootStore? SelectStore
    (
        BackupRootDescriptor descriptor,
        string? requestedStoreId
    )
    {
        if (descriptor.Stores is null)
        {
            return null;
        }

        var selectedStoreId = string.IsNullOrWhiteSpace(requestedStoreId) ?
            descriptor.DefaultStoreId :
            requestedStoreId;
        if (!string.IsNullOrWhiteSpace(selectedStoreId))
        {
            return descriptor.Stores.FirstOrDefault(store => string.Equals(
                store.Id,
                selectedStoreId,
                StringComparison.OrdinalIgnoreCase));
        }

        return descriptor.Stores.FirstOrDefault();
    }

    private static string? TryResolveFileSystemRoot
    (
        BackupRootStore? store,
        string descriptorRootPath
    )
    {
        if (store is null ||
            !string.Equals(
                store.Kind,
                FileSystemObjectStoreKind.Value,
                StringComparison.Ordinal) ||
            store.ProviderProperties is null)
        {
            return null;
        }

        foreach (var property in store.ProviderProperties)
        {
            if (!string.Equals(property.Key, "rootPath", StringComparison.OrdinalIgnoreCase) ||
                property.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                continue;
            }

            var configuredPath = property.Value.ValueKind == JsonValueKind.String ?
                property.Value.GetString() :
                property.Value.ToString();
            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                return null;
            }

            return Path.IsPathRooted(configuredPath) ?
                Path.GetFullPath(configuredPath) :
                Path.GetFullPath(configuredPath, descriptorRootPath);
        }

        return null;
    }

    private static bool IsSameOrDescendant(string path, string rootPath)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ?
            StringComparison.OrdinalIgnoreCase :
            StringComparison.Ordinal;
        if (string.Equals(path, rootPath, comparison))
        {
            return true;
        }

        var rootedPrefix = Path.EndsInDirectorySeparator(rootPath) ?
            rootPath :
            $"{rootPath}{Path.DirectorySeparatorChar}";
        return path.StartsWith(rootedPrefix, comparison);
    }

    private static string CanonicalizePath(string path, bool pathIsFile)
    {
        var fullPath = Path.GetFullPath(path);
        if (!pathIsFile)
        {
            return ResolveDirectoryLinks(fullPath);
        }

        var parentPath = Path.GetDirectoryName(fullPath);
        return string.IsNullOrEmpty(parentPath) ?
            fullPath :
            Path.Combine(
                ResolveDirectoryLinks(parentPath),
                Path.GetFileName(fullPath));
    }

    private static string ResolveDirectoryLinks(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
        {
            return fullPath;
        }

        var currentPath = root;
        var relativePath = fullPath[root.Length..];
        foreach (var segment in relativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, segment);
            if (!Directory.Exists(currentPath))
            {
                continue;
            }

            var directory = new DirectoryInfo(currentPath);
            if ((directory.Attributes & FileAttributes.ReparsePoint) == 0)
            {
                continue;
            }

            currentPath = directory.ResolveLinkTarget(returnFinalTarget: true)?.FullName ??
                throw new IOException(
                    $"Could not resolve reparse-point directory '{currentPath}'.");
        }

        return Path.GetFullPath(currentPath);
    }

    private sealed record OperationRoot(string Description, string Path);
}
