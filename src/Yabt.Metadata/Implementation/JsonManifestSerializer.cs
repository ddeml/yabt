using System.Text.Json;
using System.Text.Json.Serialization;
using Yabt.Core.Models;

namespace Yabt.Metadata.Implementation;

internal sealed class JsonManifestSerializer : IManifestSerializer
{
    private readonly JsonSerializerOptions _jsonOptions;

    public JsonManifestSerializer()
        : this(JsonMetadataOptions.Create())
    {
    }

    public JsonManifestSerializer(JsonSerializerOptions jsonOptions)
    {
        ArgumentNullException.ThrowIfNull(jsonOptions);

        _jsonOptions = new(jsonOptions)
        {
            AllowDuplicateProperties = false,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
    }

    public ArchiveManifest Create
    (
        string sourcePath,
        DateTimeOffset createdAtUtc,
        string format,
        int formatVersion,
        string projectionId,
        string packageName,
        FolderPolicy policy,
        IEnumerable<ArchiveManifestEntry> entries
    )
    {
        ArgumentNullException.ThrowIfNull(entries);

        var canonicalEntries = CreateCanonicalEntries(entries);
        var manifest = new ArchiveManifest
        (
            ArchiveManifest.ExpectedDocumentType,
            ArchiveManifest.ExpectedSchemaVersion,
            sourcePath,
            createdAtUtc,
            format,
            formatVersion,
            projectionId,
            packageName,
            policy,
            canonicalEntries,
            ComputeTotalBytes(canonicalEntries),
            string.Empty
        );
        var canonicalManifest = ValidateManifest(
            manifest,
            validateHash: false);

        return canonicalManifest with
        {
            ManifestHash = ComputeManifestHash(canonicalManifest),
        };
    }

    public async Task WriteAsync
    (
        ArchiveManifest manifest,
        Stream destination,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(destination);

        var validatedManifest = ValidateManifest(
            manifest,
            validateHash: true);
        try
        {
            using var writer = new Utf8JsonWriter(destination, new()
            {
                Indented = true,
            });
            WriteCanonicalManifest(
                writer,
                validatedManifest,
                includeManifestHash: true);
            await writer.FlushAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            throw new YabtMetadataException(
                "Package manifest JSON could not be serialized.",
                ex);
        }
    }

    public async Task<ArchiveManifest> ReadAsync
    (
        Stream source,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(source);

        ArchiveManifest? manifest;
        try
        {
            manifest = await JsonSerializer.DeserializeAsync<ArchiveManifest>(
                source,
                _jsonOptions,
                cancellationToken);
        }
        catch (Exception ex)
        {
            throw new YabtMetadataException(
                "Package manifest JSON could not be deserialized.",
                ex);
        }

        if (manifest is null)
        {
            throw new YabtMetadataException(
                "Package manifest JSON did not contain a manifest object.");
        }

        return ValidateManifest(
            manifest,
            validateHash: true);
    }

    private ArchiveManifest ValidateManifest
    (
        ArchiveManifest manifest,
        bool validateHash
    )
    {
        if (!string.Equals(
                manifest.DocumentType,
                ArchiveManifest.ExpectedDocumentType,
                StringComparison.Ordinal))
        {
            throw new YabtMetadataException(
                "Package manifest JSON has an unexpected document type.");
        }

        if (manifest.SchemaVersion != ArchiveManifest.ExpectedSchemaVersion)
        {
            throw new YabtMetadataException(
                "Package manifest JSON has an unsupported schema version.");
        }

        var sourcePath = NormalizeLogicalPath(
            manifest.SourcePath,
            allowEmpty: true,
            "Package manifest source path");
        ValidateNonemptyToken(manifest.Format, "Package manifest format");
        if (manifest.FormatVersion <= 0)
        {
            throw new YabtMetadataException(
                "Package manifest format version must be greater than zero.");
        }

        ValidateHash(manifest.ProjectionId, "Package manifest projection id");
        var packageName = NormalizeLogicalPath(
            manifest.PackageName,
            allowEmpty: false,
            "Package manifest package name");
        if (packageName.Contains('/'))
        {
            throw new YabtMetadataException(
                "Package manifest package name must be a file name without a folder path.");
        }

        var policy = ValidatePolicy(manifest.Policy, manifest.Format);
        if (manifest.CreatedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new YabtMetadataException(
                "Package manifest creation time must use the UTC offset.");
        }

        if (manifest.Entries is null)
        {
            throw new YabtMetadataException(
                "Package manifest JSON does not contain an entries collection.");
        }

        var serializedEntries = manifest.Entries.ToArray();
        if (serializedEntries.Length == 0)
        {
            throw new YabtMetadataException(
                "Package manifest JSON must contain at least one entry.");
        }

        var canonicalEntries = CreateCanonicalEntries(serializedEntries);
        EnsureEntriesAreCanonical(serializedEntries, canonicalEntries);
        var totalBytes = ComputeTotalBytes(canonicalEntries);
        if (manifest.TotalBytes != totalBytes)
        {
            throw new YabtMetadataException(
                "Package manifest total bytes do not match its entries.");
        }

        var expectedCreatedAtUtc = GetDeterministicCreationTime(canonicalEntries);
        if (manifest.CreatedAtUtc != expectedCreatedAtUtc)
        {
            throw new YabtMetadataException(
                "Package manifest creation time is not the deterministic entry timestamp.");
        }

        var canonicalManifest = manifest with
        {
            SourcePath = sourcePath,
            PackageName = packageName,
            Policy = policy,
            Entries = canonicalEntries,
            TotalBytes = totalBytes,
        };

        if (!validateHash) { return canonicalManifest; }

        ValidateHash(
            canonicalManifest.ManifestHash,
            "Package manifest self-hash");
        var expectedHash = ComputeManifestHash(canonicalManifest);
        if (!string.Equals(
                canonicalManifest.ManifestHash,
                expectedHash,
                StringComparison.Ordinal))
        {
            throw new YabtMetadataException(
                "Package manifest self-hash does not match its contents.");
        }

        return canonicalManifest;
    }

    private static FolderPolicy ValidatePolicy
    (
        FolderPolicy? policy,
        string manifestFormat
    )
    {
        if (policy is null)
        {
            throw new YabtMetadataException(
                "Package manifest policy is required.");
        }

        ValidateNonemptyToken(policy.Format, "Package manifest policy format");
        if (!string.Equals(
                policy.Format,
                manifestFormat,
                StringComparison.Ordinal))
        {
            throw new YabtMetadataException(
                "Package manifest policy format does not match the artifact format.");
        }

        return policy with
        {
            IncludePatterns = ValidatePatterns(
                policy.IncludePatterns,
                "include"),
            ExcludePatterns = ValidatePatterns(
                policy.ExcludePatterns,
                "exclude"),
        };
    }

    private static string[]? ValidatePatterns
    (
        IEnumerable<string>? patterns,
        string description
    )
    {
        if (patterns is null) { return null; }

        var snapshot = patterns.ToArray();
        if (snapshot.Any(string.IsNullOrWhiteSpace))
        {
            throw new YabtMetadataException(
                $"Package manifest policy {description} patterns cannot contain empty values.");
        }

        return snapshot;
    }

    private static ArchiveManifestEntry[] CreateCanonicalEntries
    (
        IEnumerable<ArchiveManifestEntry> entries
    )
    {
        var canonicalEntries = new List<ArchiveManifestEntry>();
        foreach (var entry in entries)
        {
            if (entry is null)
            {
                throw new YabtMetadataException(
                    "Package manifest entries cannot contain null values.");
            }

            var storedPath = NormalizeLogicalPath(
                entry.StoredPath,
                allowEmpty: false,
                "Package manifest stored entry path");
            var allowEmptyRelativePath = string.Equals(
                entry.Kind,
                ArchiveManifestEntryKinds.Directory,
                StringComparison.Ordinal);
            var relativePath = NormalizeLogicalPath(
                entry.RelativePath,
                allowEmptyRelativePath,
                "Package manifest logical entry path");
            if (entry.Length < 0)
            {
                throw new YabtMetadataException(
                    $"Package manifest entry '{storedPath}' has a negative length.");
            }

            if (entry.LastModifiedUtc.Offset != TimeSpan.Zero)
            {
                throw new YabtMetadataException(
                    $"Package manifest entry '{storedPath}' timestamp must use the UTC offset.");
            }

            ValidateHash(
                entry.ContentHash,
                $"Package manifest entry '{storedPath}' content hash");
            var projection = ValidateEntryShape(
                entry,
                relativePath,
                storedPath);
            canonicalEntries.Add(entry with
            {
                RelativePath = relativePath,
                StoredPath = storedPath,
                Projection = projection,
            });
        }

        canonicalEntries.Sort(static (left, right) =>
            string.Compare(left.StoredPath, right.StoredPath, StringComparison.Ordinal));
        for (var index = 1; index < canonicalEntries.Count; index++)
        {
            if (string.Equals(
                    canonicalEntries[index - 1].StoredPath,
                    canonicalEntries[index].StoredPath,
                    StringComparison.Ordinal))
            {
                throw new YabtMetadataException(
                    $"Package manifest contains duplicate stored path " +
                    $"'{canonicalEntries[index].StoredPath}'.");
            }
        }

        return [.. canonicalEntries];
    }

    private static ArchiveProjectionProvenance? ValidateEntryShape
    (
        ArchiveManifestEntry entry,
        string relativePath,
        string storedPath
    )
    {
        switch (entry.Kind)
        {
            case ArchiveManifestEntryKinds.File:
                if (entry.Projection is not null)
                {
                    throw new YabtMetadataException(
                        $"Package manifest file entry '{storedPath}' cannot contain projection provenance.");
                }

                return null;

            case ArchiveManifestEntryKinds.Directory:
                if (entry.Projection is not null ||
                    entry.Length != 0 ||
                    !storedPath.EndsWith(
                        $"/{ArchiveFolderMarkerFileNames.EmptyFolder}",
                        StringComparison.Ordinal) &&
                    !string.Equals(
                        storedPath,
                        ArchiveFolderMarkerFileNames.EmptyFolder,
                        StringComparison.Ordinal))
                {
                    throw new YabtMetadataException(
                        $"Package manifest directory entry '{storedPath}' is not a valid empty-folder marker.");
                }

                var expectedDirectoryPath = GetParentPath(storedPath);
                if (!string.Equals(
                        relativePath,
                        expectedDirectoryPath,
                        StringComparison.Ordinal))
                {
                    throw new YabtMetadataException(
                        $"Package manifest directory entry '{storedPath}' has the wrong logical path.");
                }

                return null;

            case ArchiveManifestEntryKinds.FormatArtifact:
                if (entry.Projection is null)
                {
                    throw new YabtMetadataException(
                        $"Package manifest format artifact '{storedPath}' has no projection provenance.");
                }

                return ValidateProjection(entry.Projection, storedPath);

            default:
                throw new YabtMetadataException(
                    $"Package manifest entry '{storedPath}' has unsupported kind '{entry.Kind}'.");
        }
    }

    private static ArchiveProjectionProvenance ValidateProjection
    (
        ArchiveProjectionProvenance projection,
        string storedPath
    )
    {
        var logicalPath = NormalizeLogicalPath(
            projection.LogicalPath,
            allowEmpty: true,
            $"Package manifest entry '{storedPath}' projection logical path");
        ValidateNonemptyToken(
            projection.Format,
            $"Package manifest entry '{storedPath}' projection format");
        if (projection.FormatVersion <= 0)
        {
            throw new YabtMetadataException(
                $"Package manifest entry '{storedPath}' projection format version must be greater than zero.");
        }

        ValidateHash(
            projection.ProjectionId,
            $"Package manifest entry '{storedPath}' projection id");
        ValidateNonemptyToken(
            projection.ArtifactRole,
            $"Package manifest entry '{storedPath}' projection artifact role");

        return projection with
        {
            LogicalPath = logicalPath,
        };
    }

    private static void EnsureEntriesAreCanonical
    (
        ArchiveManifestEntry[] serializedEntries,
        ArchiveManifestEntry[] canonicalEntries
    )
    {
        if (serializedEntries.Length != canonicalEntries.Length)
        {
            throw new YabtMetadataException(
                "Package manifest entry count changed during canonicalization.");
        }

        for (var index = 0; index < serializedEntries.Length; index++)
        {
            if (serializedEntries[index] != canonicalEntries[index])
            {
                throw new YabtMetadataException(
                    "Package manifest entries are not in canonical path order or contain noncanonical values.");
            }
        }
    }

    private string ComputeManifestHash(ArchiveManifest manifest)
    {
        using var canonicalJson = new MemoryStream();
        using (var writer = new Utf8JsonWriter(canonicalJson))
        {
            WriteCanonicalManifest(
                writer,
                manifest,
                includeManifestHash: false);
        }

        return ArchiveHash.Compute(
            canonicalJson.GetBuffer().AsSpan(
                0,
                checked((int)canonicalJson.Length)));
    }

    private void WriteCanonicalManifest
    (
        Utf8JsonWriter writer,
        ArchiveManifest manifest,
        bool includeManifestHash
    )
    {
        writer.WriteStartObject();
        writer.WriteString("documentType", manifest.DocumentType);
        writer.WriteNumber("schemaVersion", manifest.SchemaVersion);
        writer.WriteString("sourcePath", manifest.SourcePath);
        WriteUtcTimestamp(writer, "createdAtUtc", manifest.CreatedAtUtc);
        writer.WriteString("format", manifest.Format);
        writer.WriteNumber("formatVersion", manifest.FormatVersion);
        writer.WriteString("projectionId", manifest.ProjectionId);
        writer.WriteString("packageName", manifest.PackageName);
        WritePolicy(writer, manifest.Policy);
        writer.WriteStartArray("entries");
        foreach (var entry in manifest.Entries)
        {
            writer.WriteStartObject();
            writer.WriteString("kind", entry.Kind);
            writer.WriteString("relativePath", entry.RelativePath);
            writer.WriteString("storedPath", entry.StoredPath);
            writer.WriteNumber("length", entry.Length);
            WriteUtcTimestamp(
                writer,
                "lastModifiedUtc",
                entry.LastModifiedUtc);
            writer.WriteString("contentHash", entry.ContentHash);
            if (entry.Projection is not null)
            {
                writer.WriteStartObject("projection");
                writer.WriteString("logicalPath", entry.Projection.LogicalPath);
                writer.WriteString("format", entry.Projection.Format);
                writer.WriteNumber("formatVersion", entry.Projection.FormatVersion);
                writer.WriteString("projectionId", entry.Projection.ProjectionId);
                writer.WriteString("artifactRole", entry.Projection.ArtifactRole);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteNumber("totalBytes", manifest.TotalBytes);
        if (includeManifestHash)
        {
            writer.WriteString("manifestHash", manifest.ManifestHash);
        }

        writer.WriteEndObject();
    }

    private void WritePolicy(Utf8JsonWriter writer, FolderPolicy policy)
    {
        writer.WriteStartObject("policy");
        writer.WriteString("format", policy.Format);
        WriteStringArray(writer, "includePatterns", policy.IncludePatterns);
        WriteStringArray(writer, "excludePatterns", policy.ExcludePatterns);
        if (policy.Options is not null)
        {
            writer.WritePropertyName("options");
            var options = JsonSerializer.SerializeToElement(
                policy.Options,
                _jsonOptions);
            WriteCanonicalJsonValue(writer, options);
        }

        writer.WriteEndObject();
    }

    private static void WriteStringArray
    (
        Utf8JsonWriter writer,
        string propertyName,
        IEnumerable<string>? values
    )
    {
        if (values is null) { return; }

        writer.WriteStartArray(propertyName);
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static void WriteCanonicalJsonValue
    (
        Utf8JsonWriter writer,
        JsonElement value
    )
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var properties = value.EnumerateObject()
                    .OrderBy(property => property.Name, StringComparer.Ordinal);
                foreach (var property in properties)
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJsonValue(writer, property.Value);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    WriteCanonicalJsonValue(writer, item);
                }

                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;

            case JsonValueKind.Number:
                value.WriteTo(writer);
                break;

            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;

            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;

            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;

            default:
                throw new YabtMetadataException(
                    "Package manifest policy options contain an unsupported JSON value.");
        }
    }

    private static void WriteUtcTimestamp
    (
        Utf8JsonWriter writer,
        string propertyName,
        DateTimeOffset value
    ) => writer.WriteString(propertyName, value.UtcDateTime);

    private static long ComputeTotalBytes(IEnumerable<ArchiveManifestEntry> entries)
    {
        long totalBytes = 0;
        foreach (var entry in entries)
        {
            totalBytes = checked(totalBytes + entry.Length);
        }

        return totalBytes;
    }

    private static DateTimeOffset GetDeterministicCreationTime
    (
        ArchiveManifestEntry[] entries
    ) => entries.Length == 0 ?
        DateTimeOffset.UnixEpoch :
        entries.Max(entry => entry.LastModifiedUtc);

    private static string NormalizeLogicalPath
    (
        string? value,
        bool allowEmpty,
        string description
    )
    {
        string normalizedValue;
        try
        {
            normalizedValue = ArchiveLayout.NormalizeObjectKey(value);
        }
        catch (Exception ex)
        {
            throw new YabtMetadataException(
                $"{description} '{value}' is invalid.",
                ex);
        }

        if (!allowEmpty && string.IsNullOrEmpty(normalizedValue))
        {
            throw new YabtMetadataException(
                $"{description} cannot be empty.");
        }

        return normalizedValue;
    }

    private static void ValidateNonemptyToken(string? value, string description)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.IndexOfAny([' ', '\t', '\r', '\n', '/', '\\']) >= 0)
        {
            throw new YabtMetadataException(
                $"{description} is not a valid nonempty token.");
        }
    }

    private static void ValidateHash(string? value, string description)
    {
        if (!ArchiveHash.IsValid(value))
        {
            throw new YabtMetadataException(
                $"{description} is not a valid xxHash128 hash.");
        }
    }

    private static string GetParentPath(string relativePath)
    {
        var separator = relativePath.LastIndexOf('/');
        return separator < 0 ? string.Empty : relativePath[..separator];
    }
}
