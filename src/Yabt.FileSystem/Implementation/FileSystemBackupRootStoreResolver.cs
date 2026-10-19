using System.Text.Json;
using Microsoft.Extensions.Logging;
using Yabt.Common;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.Metadata;

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
        _logger.LogTrace(nameof(ResolveStore));
        ArgumentNullException.ThrowIfNull(store);

        if (!string.Equals(store.Kind, FileSystemObjectStoreKind.Value, StringComparison.Ordinal))
        {
            throw new YabtFileSystemException(
                $"Store '{store.Id}' is not a filesystem object store.");
        }

        ReportUnsupportedProperties(store, descriptorRootPath);
        var rootPath = GetStringProviderProperty(store, "rootPath");
        if (string.IsNullOrEmpty(rootPath))
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
        _logger.LogTrace(nameof(ResolveSourceRoot));
        if (string.IsNullOrEmpty(rootPath))
        {
            throw new YabtFileSystemException("Source root object store requires a root path.");
        }

        FileSystemNativePath.ValidateSpelling(rootPath);

        return CreateStore(new FileSystemObjectStoreOptions
        {
            RootPath = Path.GetFullPath(rootPath),
        });
    }

    private void ReportUnsupportedProperties
    (
        BackupRootStore store,
        string descriptorRootPath
    )
    {
        _logger.LogTrace(nameof(ReportUnsupportedProperties));
        var sourceName = Path.Combine(descriptorRootPath, BackupRootFileNames.Primary);
        var propertyPath = $"$.stores[id={JsonSerializer.Serialize(store.Id)}]";
        if (store.ConfigSectionPath is not null)
        {
            ConfigurationWarnings.Report
            (
                _logger,
                sourceName,
                $"{propertyPath}.configSectionPath",
                "Filesystem runtime configuration sections are unsupported and ignored; settings in this store declaration are used."
            );
        }
        if (store.CredentialRef is not null)
        {
            ConfigurationWarnings.Report
            (
                _logger,
                sourceName,
                $"{propertyPath}.credentialRef",
                "Filesystem credential references are unsupported and ignored; access uses the current process identity."
            );
        }

        if (store.ProviderProperties is null) { return; }
        foreach (var property in store.ProviderProperties)
        {
            if (string.Equals(property.Key, "rootPath", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(property.Key, "bufferSize", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(property.Key, "listChunkSize", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            ConfigurationWarnings.Report
            (
                _logger,
                sourceName,
                $"{propertyPath}[{JsonSerializer.Serialize(property.Key)}]",
                "Unknown filesystem setting is ignored."
            );
        }
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
            throw new YabtFileSystemException(
                $"Filesystem object store '{store.Id}' provider property '{name}' must be a string.");
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
            value.TryGetInt32(out var result) && result > 0 ?
            result :
            throw new YabtFileSystemException(
                $"Filesystem object store '{store.Id}' provider property '{name}' must be a positive integer.");
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
        FileSystemNativePath.ValidateSpelling(configuredPath);
        if (Path.IsPathRooted(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        return Path.GetFullPath(configuredPath, descriptorRootPath);
    }
}
