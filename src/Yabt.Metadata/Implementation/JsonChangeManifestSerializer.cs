using System.Text.Json;
using System.Text.Json.Serialization;
using Yabt.Core.Models;

namespace Yabt.Metadata.Implementation;

internal sealed class JsonChangeManifestSerializer(JsonSerializerOptions _jsonOptions) :
    IChangeManifestSerializer
{
    private readonly JsonSerializerOptions _strictJsonOptions = new(_jsonOptions)
    {
        AllowDuplicateProperties = false,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public JsonChangeManifestSerializer()
        : this(JsonMetadataOptions.Create())
    {
    }

    public ArchiveChangeManifest Create
    (
        IEnumerable<ArchiveChangeManifestEntry> entries,
        string rootFormat,
        string? rootDescriptorContentHash = default,
        long? rootDescriptorContentLength = default
    )
    {
        ArgumentNullException.ThrowIfNull(entries);
        ValidateRequiredRootFormat(rootFormat);
        ValidateRootDescriptorEvidence
        (
            rootDescriptorContentHash,
            rootDescriptorContentLength
        );

        var canonicalEntries = CreateCanonicalEntries
        (
            entries,
            ArchiveChangeManifest.ExpectedSchemaVersion
        );
        var manifest = new ArchiveChangeManifest
        (
            ArchiveChangeManifest.ExpectedDocumentType,
            ArchiveChangeManifest.ExpectedSchemaVersion,
            canonicalEntries,
            string.Empty,
            rootFormat,
            rootDescriptorContentHash,
            rootDescriptorContentLength
        );

        return manifest with
        {
            ManifestHash = ComputeManifestHash
            (
                manifest,
                canonicalEntries
            ),
        };
    }

    public async Task WriteAsync
    (
        ArchiveChangeManifest manifest,
        Stream destination,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(destination);

        var validatedManifest = ValidateManifest
        (
            manifest,
            allowPreviousSchemas: false
        );
        try
        {
            await JsonSerializer.SerializeAsync(
                destination,
                validatedManifest,
                _strictJsonOptions,
                cancellationToken);
        }
        catch (Exception ex)
        {
            throw new YabtMetadataException("Change manifest JSON could not be serialized.", ex);
        }
    }

    public async Task<ArchiveChangeManifest> ReadAsync
    (
        Stream source,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(source);

        ArchiveChangeManifest? manifest;
        try
        {
            using var document = await JsonDocument.ParseAsync(
                source,
                cancellationToken: cancellationToken);
            manifest = document.RootElement.Deserialize<ArchiveChangeManifest>(
                _strictJsonOptions);
            ValidateSchemaPropertyPresence(document.RootElement, manifest);
        }
        catch (Exception ex)
        {
            throw new YabtMetadataException("Change manifest JSON could not be deserialized.", ex);
        }

        if (manifest is null)
        {
            throw new YabtMetadataException("Change manifest JSON did not contain a manifest object.");
        }

        return ValidateManifest
        (
            manifest,
            allowPreviousSchemas: true
        );
    }

    private static ArchiveChangeManifest ValidateManifest
    (
        ArchiveChangeManifest manifest,
        bool allowPreviousSchemas
    )
    {
        if (!string.Equals(
                manifest.DocumentType,
                ArchiveChangeManifest.ExpectedDocumentType,
                StringComparison.Ordinal))
        {
            throw new YabtMetadataException("Change manifest JSON has an unexpected document type.");
        }

        ValidateSchemaAndRootEvidence(manifest, allowPreviousSchemas);

        if (manifest.Entries is null)
        {
            throw new YabtMetadataException("Change manifest JSON does not contain an entries collection.");
        }

        var serializedEntries = manifest.Entries.ToArray();
        var canonicalEntries = CreateCanonicalEntries
        (
            serializedEntries,
            manifest.SchemaVersion
        );
        EnsureEntriesAreCanonical(serializedEntries, canonicalEntries);
        ValidateManifestHash(manifest.ManifestHash);

        var expectedHash = ComputeManifestHash
        (
            manifest,
            canonicalEntries
        );
        if (!string.Equals(manifest.ManifestHash, expectedHash, StringComparison.Ordinal))
        {
            throw new YabtMetadataException("Change manifest self-hash does not match its contents.");
        }

        return manifest with
        {
            Entries = canonicalEntries,
        };
    }

    private static ArchiveChangeManifestEntry[] CreateCanonicalEntries
    (
        IEnumerable<ArchiveChangeManifestEntry> entries,
        int schemaVersion
    )
    {
        var canonicalEntries = new List<ArchiveChangeManifestEntry>();
        foreach (var entry in entries)
        {
            if (entry is null)
            {
                throw new YabtMetadataException("Change manifest entries cannot contain null values.");
            }

            string relativePath;
            try
            {
                relativePath = ArchiveLayout.NormalizeObjectKey(entry.RelativePath);
            }
            catch (Exception ex)
            {
                throw new YabtMetadataException(
                    $"Change manifest entry path '{entry.RelativePath}' is invalid.",
                    ex);
            }

            if (string.IsNullOrEmpty(relativePath))
            {
                throw new YabtMetadataException("Change manifest entry paths cannot be empty.");
            }

            if (entry.ArtifactLength < 0)
            {
                throw new YabtMetadataException(
                    $"Change manifest entry '{relativePath}' has a negative artifact length.");
            }

            ValidateFingerprint(
                entry.ChangeFingerprint,
                $"Change manifest entry '{relativePath}' change fingerprint");

            if (entry.ContentHash is not null)
            {
                ValidateContentHash(
                    entry.ContentHash,
                    $"Change manifest entry '{relativePath}' content hash");
            }
            else if (schemaVersion == ArchiveChangeManifest.ExpectedSchemaVersion)
            {
                throw new YabtMetadataException(
                    $"Change manifest entry '{relativePath}' content hash is required by " +
                        $"schema version {schemaVersion}.");
            }

            if (schemaVersion != ArchiveChangeManifest.ExpectedSchemaVersion &&
                entry.Projection is not null)
            {
                throw new YabtMetadataException
                (
                    $"Schema version {schemaVersion} change manifest entry " +
                    $"'{relativePath}' must not contain projection provenance."
                );
            }

            var projection = entry.Projection is null
                ? null
                : CreateCanonicalProjection(entry.Projection, relativePath);

            canonicalEntries.Add(entry with
            {
                RelativePath = relativePath,
                Projection = projection,
            });
        }

        canonicalEntries.Sort(static (left, right) =>
            string.Compare(left.RelativePath, right.RelativePath, StringComparison.Ordinal));

        for (var index = 1; index < canonicalEntries.Count; index++)
        {
            if (string.Equals(
                    canonicalEntries[index - 1].RelativePath,
                    canonicalEntries[index].RelativePath,
                    StringComparison.Ordinal))
            {
                throw new YabtMetadataException(
                    $"Change manifest contains duplicate entry path '{canonicalEntries[index].RelativePath}'.");
            }
        }

        return [.. canonicalEntries];
    }

    private static void EnsureEntriesAreCanonical
    (
        ArchiveChangeManifestEntry[] serializedEntries,
        ArchiveChangeManifestEntry[] canonicalEntries
    )
    {
        for (var index = 0; index < serializedEntries.Length; index++)
        {
            var serializedEntry = serializedEntries[index];
            var canonicalEntry = canonicalEntries[index];

            if (!string.Equals(
                    serializedEntry.RelativePath,
                    canonicalEntry.RelativePath,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    serializedEntry.ChangeFingerprint,
                    canonicalEntry.ChangeFingerprint,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    serializedEntry.ContentHash,
                    canonicalEntry.ContentHash,
                    StringComparison.Ordinal) ||
                serializedEntry.ArtifactLength != canonicalEntry.ArtifactLength ||
                serializedEntry.Projection != canonicalEntry.Projection)
            {
                throw new YabtMetadataException(
                    "Change manifest entries are not in canonical path order or contain noncanonical values.");
            }
        }
    }

    private static void ValidateFingerprint(string value, string description)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new YabtMetadataException($"{description} is required.");
        }

        var separator = value.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || separator == value.Length - 1)
        {
            throw new YabtMetadataException($"{description} must include a type and value.");
        }

        var qualifier = value.AsSpan(0, separator);
        var fingerprintValue = value.AsSpan(separator + 1);
        if (!IsValidQualifier(qualifier) ||
            fingerprintValue.IndexOfAny([' ', '\t', '\r', '\n']) >= 0)
        {
            throw new YabtMetadataException($"{description} is not a valid type-qualified fingerprint.");
        }
    }

    private static void ValidateSchemaAndRootEvidence
    (
        ArchiveChangeManifest manifest,
        bool allowPreviousSchemas
    )
    {
        if (manifest.SchemaVersion == ArchiveChangeManifest.LegacySchemaVersion)
        {
            if (!allowPreviousSchemas)
            {
                throw new YabtMetadataException(
                    "Previous change manifest schema versions can be read but cannot be written.");
            }

            if (manifest.RootFormat is not null)
            {
                throw new YabtMetadataException(
                    "Legacy schema version 1 change manifests must not contain a root format.");
            }

            EnsurePreviousSchemaHasNoRootDescriptorEvidence(manifest);

            return;
        }

        if (manifest.SchemaVersion == ArchiveChangeManifest.PreviousSchemaVersion)
        {
            if (!allowPreviousSchemas)
            {
                throw new YabtMetadataException
                (
                    "Previous change manifest schema versions can be read but cannot be written."
                );
            }

            ValidateRequiredRootFormat(manifest.RootFormat);
            EnsurePreviousSchemaHasNoRootDescriptorEvidence(manifest);
            return;
        }

        if (manifest.SchemaVersion != ArchiveChangeManifest.ExpectedSchemaVersion)
        {
            throw new YabtMetadataException("Change manifest JSON has an unsupported schema version.");
        }

        ValidateRequiredRootFormat(manifest.RootFormat);
        ValidateRootDescriptorEvidence
        (
            manifest.RootDescriptorContentHash,
            manifest.RootDescriptorContentLength
        );
    }

    private static void ValidateSchemaPropertyPresence
    (
        JsonElement root,
        ArchiveChangeManifest? manifest
    )
    {
        if (manifest is null || root.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (manifest.SchemaVersion == ArchiveChangeManifest.LegacySchemaVersion &&
            root.TryGetProperty("rootFormat", out _))
        {
            throw new YabtMetadataException(
                "Schema version 1 change manifests must not contain rootFormat, even when null.");
        }

        if (manifest.SchemaVersion is
                ArchiveChangeManifest.LegacySchemaVersion or
                ArchiveChangeManifest.PreviousSchemaVersion)
        {
            RejectPresentProperty(
                root,
                "rootDescriptorContentHash",
                manifest.SchemaVersion);
            RejectPresentProperty(
                root,
                "rootDescriptorContentLength",
                manifest.SchemaVersion);

            if (!root.TryGetProperty("entries", out var entries) ||
                entries.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object &&
                    entry.TryGetProperty("projection", out _))
                {
                    throw new YabtMetadataException(
                        $"Schema version {manifest.SchemaVersion} change manifest entries " +
                            "must not contain projection, even when null.");
                }
            }
        }
    }

    private static void RejectPresentProperty
    (
        JsonElement root,
        string propertyName,
        int schemaVersion
    )
    {
        if (root.TryGetProperty(propertyName, out _))
        {
            throw new YabtMetadataException(
                $"Schema version {schemaVersion} change manifests must not contain " +
                    $"{propertyName}, even when null.");
        }
    }

    private static ArchiveProjectionProvenance CreateCanonicalProjection
    (
        ArchiveProjectionProvenance projection,
        string relativePath
    )
    {
        string logicalPath;
        try
        {
            logicalPath = ArchiveLayout.NormalizeObjectKey(projection.LogicalPath);
        }
        catch (Exception ex)
        {
            throw new YabtMetadataException
            (
                $"Change manifest entry '{relativePath}' projection logical path " +
                $"'{projection.LogicalPath}' is invalid.",
                ex
            );
        }

        ValidateProjectionName
        (
            projection.Format,
            $"Change manifest entry '{relativePath}' projection format"
        );
        if (projection.FormatVersion <= 0)
        {
            throw new YabtMetadataException
            (
                $"Change manifest entry '{relativePath}' projection format version " +
                "must be positive."
            );
        }

        ValidateFingerprint
        (
            projection.ProjectionId,
            $"Change manifest entry '{relativePath}' projection id"
        );
        ValidateProjectionName
        (
            projection.ArtifactRole,
            $"Change manifest entry '{relativePath}' projection artifact role"
        );

        return projection with
        {
            LogicalPath = logicalPath,
        };
    }

    private static void ValidateProjectionName(string value, string description)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !value.AsSpan().SequenceEqual(value.Trim().AsSpan()))
        {
            throw new YabtMetadataException($"{description} is required and must be nonempty.");
        }

        if (value.AsSpan().IndexOfAny(['\r', '\n', '\t']) >= 0)
        {
            throw new YabtMetadataException($"{description} contains invalid whitespace.");
        }
    }

    private static void EnsurePreviousSchemaHasNoRootDescriptorEvidence
    (
        ArchiveChangeManifest manifest
    )
    {
        if (manifest.RootDescriptorContentHash is not null ||
            manifest.RootDescriptorContentLength is not null)
        {
            throw new YabtMetadataException
            (
                $"Schema version {manifest.SchemaVersion} change manifests must not contain " +
                "root descriptor evidence."
            );
        }
    }

    private static void ValidateRootDescriptorEvidence
    (
        string? contentHash,
        long? contentLength
    )
    {
        if ((contentHash is null) != (contentLength is null))
        {
            throw new YabtMetadataException
            (
                "Change manifest root descriptor content hash and content length must either " +
                "both be present or both be absent."
            );
        }

        if (contentHash is null)
        {
            return;
        }

        ValidateContentHash(contentHash, "Change manifest root descriptor content hash");
        if (contentLength < 0)
        {
            throw new YabtMetadataException
            (
                "Change manifest root descriptor content length cannot be negative."
            );
        }
    }

    private static void ValidateRequiredRootFormat(string? rootFormat)
    {
        if (string.IsNullOrWhiteSpace(rootFormat))
        {
            throw new YabtMetadataException(
                "Change manifest root format is required and must be nonempty.");
        }
    }

    private static bool IsValidQualifier(ReadOnlySpan<char> qualifier)
    {
        foreach (var character in qualifier)
        {
            if ((character < 'a' || character > 'z') &&
                (character < '0' || character > '9') &&
                character is not '-' and not '_' and not '.')
            {
                return false;
            }
        }

        return true;
    }

    private static void ValidateManifestHash(string value)
    {
        if (!ArchiveHash.IsValid(value))
        {
            throw new YabtMetadataException(
                "Change manifest self-hash is not a valid xxHash128 hash.");
        }
    }

    private static void ValidateContentHash(string value, string description)
    {
        if (!ArchiveHash.IsValid(value))
        {
            throw new YabtMetadataException(
                $"{description} is not a valid xxHash128 hash.");
        }
    }

    private static string ComputeManifestHash
    (
        ArchiveChangeManifest manifest,
        IEnumerable<ArchiveChangeManifestEntry> entries
    )
    {
        using var canonicalJson = new MemoryStream();
        using (var writer = new Utf8JsonWriter(canonicalJson))
        {
            writer.WriteStartObject();
            writer.WriteString("documentType", manifest.DocumentType);
            writer.WriteNumber("schemaVersion", manifest.SchemaVersion);
            if (manifest.RootFormat is not null)
            {
                writer.WriteString("rootFormat", manifest.RootFormat);
            }
            if (manifest.RootDescriptorContentHash is not null)
            {
                writer.WriteString
                (
                    "rootDescriptorContentHash",
                    manifest.RootDescriptorContentHash
                );
            }
            if (manifest.RootDescriptorContentLength.HasValue)
            {
                writer.WriteNumber
                (
                    "rootDescriptorContentLength",
                    manifest.RootDescriptorContentLength.Value
                );
            }
            writer.WriteStartArray("entries");

            foreach (var entry in entries)
            {
                writer.WriteStartObject();
                writer.WriteString("relativePath", entry.RelativePath);
                writer.WriteString("changeFingerprint", entry.ChangeFingerprint);
                if (entry.ArtifactLength.HasValue)
                {
                    writer.WriteNumber("artifactLength", entry.ArtifactLength.Value);
                }
                if (entry.ContentHash is not null)
                {
                    writer.WriteString("contentHash", entry.ContentHash);
                }
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
            writer.WriteEndObject();
        }

        var canonicalBytes = canonicalJson.GetBuffer().AsSpan(0, checked((int)canonicalJson.Length));
        return ArchiveHash.Compute(canonicalBytes);
    }
}
