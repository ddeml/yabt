using Microsoft.Extensions.Logging;
using Yabt.Common;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.Metadata;

namespace Yabt.Sync.Implementation;

/// <summary>Collects current destination stat evidence without retaining the whole tree in memory.</summary>
internal static class RestoreManifestEvidenceCollector
{
    internal static async Task<ArchiveLogicalStateManifest> CreateAsync
    (
        IObjectStore destination,
        ArchiveLayout layout,
        RestorePlan plan,
        ILogicalStateManifestSerializer serializer,
        ILogger logger,
        CancellationToken cancellationToken
    )
    {
        using var index = TemporaryIndex.Create();
        var comparer = plan.Namespace.PathComparer;
        using var desired = index.CreateDictionary<ArchiveObjectInfo>(comparer: comparer);
        using var observedFiles = index.CreateSet(comparer);
        using var observedFolders = index.CreateSet(comparer);
        using var pending = index.CreateCollection<string>();
        using var entries = index.CreateCollection<ArchiveLogicalStateManifestEntry>();
        foreach (var file in plan.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ArchiveHash.IsValid(file.ContentHash)) { throw MissingEvidence(file.RelativePath); }
            desired.Add(file.RelativePath, new(file.RelativePath, ContentHash: file.ContentHash));
        }

        var internalKeys = ArchivePathRules.CreateInternalObjectKeys(layout);
        var internalPrefixes = ArchivePathRules.CreateInternalObjectPrefixes(layout);
        pending.Add(string.Empty);
        observedFolders.Add(string.Empty);
        for (long cursor = 0; cursor < pending.LongCount; cursor++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folder = pending.Get(cursor);
            var folderKey = ObjectPath.Parse(layout.ToLiveObjectKey(folder));
            logger.LogObjectRead(folder.Length == 0 ? "." : folder, "restore final evidence enumeration");
            var items = destination.GetFolderItemsAsync(folderKey.Value, recursive: false, cancellationToken);
            await foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = ObjectPath.Parse(item.Key);
                if (key.IsRoot || key.Parent != folderKey)
                {
                    throw new YabtSyncException($"Restore destination folder '{folder}' listed non-child '{key}'.");
                }
                if (ArchivePathRules.IsInternalObjectPrefix(key.Value, internalPrefixes)) { continue; }
                var path = ArchiveLayout.RemovePrefix(key.Value, layout.LivePrefix);
                if (item.IsFolder)
                {
                    if (!observedFolders.Add(path))
                    {
                        throw new YabtSyncException($"Restore destination folder '{path}' was listed more than once.");
                    }
                    pending.Add(path);
                    continue;
                }
                if (ArchivePathRules.IsInternalObjectKey(key.Value, internalKeys)) { continue; }
                if (!observedFiles.Add(path))
                {
                    throw new YabtSyncException($"Restore destination file '{path}' was listed more than once.");
                }
                if (!desired.Remove(path, out var wanted)) { continue; }
                if (!ArchiveChangeFingerprint.TryCreate
                (
                    item.Object!.ContentLength,
                    item.Object.LastModifiedUtc,
                    out var fingerprint
                ))
                {
                    throw MissingEvidence(wanted.Key);
                }
                entries.Add(new(wanted.Key, fingerprint, wanted.ContentHash!));
            }
        }
        if (desired.Count != 0) { throw MissingEvidence(desired.Keys.First()); }
        return serializer.Create(entries);
    }

    private static YabtSyncException MissingEvidence(string path) => new
    (
        $"Restore destination file '{path}' does not provide complete logical-state manifest evidence after restore."
    );
}
