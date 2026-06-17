using System.Text.Json;
using Microsoft.Extensions.Logging;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;

namespace Yabt.FileSystem.Implementation;

internal sealed class FileSystemBackupRootStoreResolver
(
    ILogger<FileSystemObjectStore> _logger
) : IBackupRootStoreResolver, ISourceRootObjectStoreResolver
{
    public string StoreKind => FileSystemObjectStoreKind.Value;

    public IObjectStore ResolveStore
    (
        BackupRootStore store,
        string descriptorRootPath
    )
    {
        ArgumentNullException.ThrowIfNull(store);

        if (!string.Equals(store.Kind, FileSystemObjectStoreKind.Value, StringComparison.Ordinal))
        {
            throw new YabtFileSystemException(
                $"Store '{store.Id}' is not a filesystem object store.");
        }

        var rootPath = GetStringProviderProperty(store, "rootPath");
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new YabtFileSystemException(
                $"Filesystem object store '{store.Id}' requires a rootPath provider property.");
        }

        return CreateStore(new FileSystemObjectStoreOptions
        {
            RootPath = ResolvePath(descriptorRootPath, rootPath),
            BufferSize = GetInt32ProviderProperty(store, "bufferSize"),
            ListChunkSize = GetInt32ProviderProperty(store, "listChunkSize"),
        });
    }

    public IObjectStore ResolveSourceRoot(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new YabtFileSystemException("Source root object store requires a root path.");
        }

        return CreateStore(new FileSystemObjectStoreOptions
        {
            RootPath = Path.GetFullPath(rootPath),
        });
    }

    private FileSystemObjectStore CreateStore(FileSystemObjectStoreOptions options) =>
        new FileSystemObjectStore
        (
            new FixedOptionsMonitor<FileSystemObjectStoreOptions>(options),
            _logger
        );

    private static string? GetStringProviderProperty
    (
        BackupRootStore store,
        string name
    )
    {
        if (!TryGetProviderProperty(store, name, out var value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ?
            value.GetString() :
            value.ToString();
    }

    private static int? GetInt32ProviderProperty
    (
        BackupRootStore store,
        string name
    )
    {
        if (!TryGetProviderProperty(store, name, out var value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out var result) ?
            result :
            throw new YabtFileSystemException(
                $"Filesystem object store '{store.Id}' provider property '{name}' must be an integer.");
    }

    private static bool TryGetProviderProperty
    (
        BackupRootStore store,
        string name,
        out JsonElement value
    )
    {
        if (store.ProviderProperties is null)
        {
            value = default;
            return false;
        }

        foreach (var providerProperty in store.ProviderProperties)
        {
            if (string.Equals(providerProperty.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = providerProperty.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string ResolvePath
    (
        string descriptorRootPath,
        string configuredPath
    )
    {
        if (Path.IsPathRooted(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        return Path.GetFullPath(configuredPath, descriptorRootPath);
    }
}
