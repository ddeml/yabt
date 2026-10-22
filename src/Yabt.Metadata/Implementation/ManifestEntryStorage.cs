using System.Text.Json;
using Yabt.Common;

namespace Yabt.Metadata.Implementation;

internal static class ManifestEntryStorage
{
    public static TemporaryManifestEntries<TEntry> CreateCanonical<TEntry>
    (
        IEnumerable<TEntry> entries,
        Func<TEntry, TEntry> canonicalize,
        Func<TEntry, string> key,
        string description,
        string keyDescription,
        JsonSerializerOptions jsonOptions,
        TemporaryContentOptions? temporaryOptions,
        Func<TEntry, string>? secondaryKey = null
    )
    {
        var result = new TemporaryManifestEntries<TEntry>(temporaryOptions, jsonOptions, ordered: true);
        try
        {
            using var paths = result.CreatePathSet();
            using var secondaryPaths = secondaryKey is null ? null : result.CreatePathSet();
            foreach (var entry in entries)
            {
                var canonical = canonicalize(entry);
                var path = key(canonical);
                if (!paths.TryAdd(path, 0))
                {
                    throw new YabtMetadataException($"{description} contains duplicate {keyDescription} '{path}'.");
                }
                if (secondaryKey is not null && !secondaryPaths!.TryAdd(secondaryKey(canonical), 0))
                {
                    throw new YabtMetadataException($"{description} contains duplicate stored path '{secondaryKey(canonical)}'.");
                }
                result.Add(canonical, path);
            }
            result.Seal();
            return result;
        }
        catch (Exception)
        {
            result.Dispose();
            throw;
        }
    }

    public static TemporaryManifestEntries<TEntry> ValidateCanonical<TEntry>
    (
        IEnumerable<TEntry> entries,
        Func<TEntry, TEntry> canonicalize,
        Func<TEntry, string> key,
        Func<TEntry, TEntry, bool> equal,
        string description,
        string keyDescription,
        JsonSerializerOptions jsonOptions,
        TemporaryContentOptions? temporaryOptions,
        Func<TEntry, string>? secondaryKey = null
    )
    {
        var existing = entries as TemporaryManifestEntries<TEntry>;
        var result = existing ?? new TemporaryManifestEntries<TEntry>(temporaryOptions, jsonOptions, ordered: false);
        try
        {
            using var secondaryPaths = secondaryKey is null ? null : result.CreatePathSet();
            string? previous = null;
            foreach (var entry in entries)
            {
                var canonical = canonicalize(entry);
                var path = key(canonical);
                if (previous is not null && string.Equals(previous, path, StringComparison.Ordinal))
                {
                    throw new YabtMetadataException($"{description} contains duplicate {keyDescription} '{path}'.");
                }
                if (!equal(entry, canonical) || previous is not null && string.CompareOrdinal(previous, path) > 0)
                {
                    throw new YabtMetadataException
                    (
                        $"{description} entries are not in canonical path order or contain noncanonical values."
                    );
                }
                if (secondaryKey is not null && !secondaryPaths!.TryAdd(secondaryKey(canonical), 0))
                {
                    throw new YabtMetadataException($"{description} contains duplicate stored path '{secondaryKey(canonical)}'.");
                }
                if (existing is null) { result.Add(canonical); }
                previous = path;
            }
            result.Seal();
            return result;
        }
        catch (Exception)
        {
            if (existing is null) { result.Dispose(); }
            throw;
        }
    }
}
