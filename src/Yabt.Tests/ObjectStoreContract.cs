using System.Text;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;

namespace Yabt.Tests;

/// <summary>
/// Provider-independent behavioral cases. Each case requires a fresh writable store;
/// failed assertions throw so test frameworks can report the same contract everywhere.
/// </summary>
public static class ObjectStoreContract
{
    public static async Task VerifyFolderLocalTraversalAsync(IObjectStore store)
    {
        const string folder = "folder";
        const string file = "folder/ rÃ©sumÃ© #100%.txt";
        await UploadTextAsync(store, file, "exact name");
        await UploadTextAsync(store, "folder/child/deep.txt", "nested");
        await UploadTextAsync(store, "folder/empty/.yabt-empty", "");
        await UploadTextAsync(store, "folder-neighbor/untouched.txt", "sibling");

        var items = new List<ArchiveFolderItem>();
        await foreach (var item in store.GetFolderItemsAsync(folder))
        {
            items.Add(item);
        }

        Require(items.Count == 3, "Folder-local traversal must return one file and two immediate folders.");
        Require(items.Single(item => item.Key == file).Name == " rÃ©sumÃ© #100%.txt", "Exact names must survive listing.");
        Require(items.Single(item => item.Key == "folder/child").IsFolder, "A descendant must appear as an immediate folder.");
        Require(items.Single(item => item.Key == "folder/empty").IsFolder, "A marker-backed empty folder must remain visible.");
        Require(await ReadTextAsync(store, file) == "exact name", "Listed exact keys must reopen the original bytes.");

        var recursiveKeys = new List<string>();
        await foreach (var item in store.GetFolderItemsAsync(folder, recursive: true))
        {
            Require(item.IsObject, "Recursive listing must return objects.");
            recursiveKeys.Add(item.Key);
        }

        Require
        (
            recursiveKeys.Order(StringComparer.Ordinal).SequenceEqual
            (
                new[] { file, "folder/child/deep.txt" }.Order(StringComparer.Ordinal),
                StringComparer.Ordinal
            ),
            "Recursive listing must stay inside the exact prefix and hide empty-folder markers."
        );
    }

    public static async Task VerifyCreateIfAbsentAsync(IObjectStore store)
    {
        await UploadTextAsync(store, "immutable.txt", "original");
        await RequireFailureAsync(() => UploadTextAsync(store, "immutable.txt", "replacement"));
        Require(await ReadTextAsync(store, "immutable.txt") == "original", "A duplicate upload must preserve the original bytes.");
    }

    public static async Task VerifyConditionalMutationsAsync(IArchiveMutableObjectStore store)
    {
        const string key = "guarded.txt";
        var originalHash = ArchiveHash.Compute("original"u8);
        var replacementHash = ArchiveHash.Compute("replacement"u8);
        await UploadTextAsync(store, key, "original");

        using (var wrong = new MemoryStream("replacement"u8.ToArray()))
        {
            Require
            (
                !await store.TryReplaceIfContentHashMatchesAsync(key, replacementHash, wrong, "text/plain", EmptyMetadata),
                "A mismatched hash must refuse replacement."
            );
        }
        Require(!await store.TryDeleteIfContentHashMatchesAsync(key, replacementHash), "A mismatched hash must refuse deletion.");
        Require(await ReadTextAsync(store, key) == "original", "Failed guards must leave the original bytes intact.");

        using (var replacement = new MemoryStream("replacement"u8.ToArray()))
        {
            Require
            (
                await store.TryReplaceIfContentHashMatchesAsync(key, originalHash, replacement, "text/plain", EmptyMetadata),
                "The matching hash must permit replacement."
            );
        }
        Require(!await store.TryDeleteIfContentHashMatchesAsync(key, originalHash), "A stale hash must not delete replacement bytes.");
        Require(await ReadTextAsync(store, key) == "replacement", "Replacement bytes must remain complete and readable.");
        Require(await store.TryDeleteIfContentHashMatchesAsync(key, replacementHash), "The matching hash must permit deletion.");
        Require(!await store.ExistsAsync(key), "Successful deletion must remove the object.");
        Require(!await store.TryDeleteIfContentHashMatchesAsync(key, replacementHash), "A missing object must fail a deletion guard.");
        using var missingReplacement = new MemoryStream("replacement"u8.ToArray());
        Require
        (
            !await store.TryReplaceIfContentHashMatchesAsync(key, replacementHash, missingReplacement, "text/plain", EmptyMetadata),
            "A conditional replacement must not create a missing object."
        );
    }

    public static async Task VerifyWholeFolderMoveAsync(IObjectStore store)
    {
        await UploadTextAsync(store, "tree/file.txt", "parent");
        await UploadTextAsync(store, "tree/nested/file.txt", "nested");
        await UploadTextAsync(store, "tree/empty/.yabt-empty", "");
        await UploadTextAsync(store, "tree-sibling/file.txt", "sibling");
        await store.MoveFolderAsync("tree", "history/tree");

        Require(!await store.ExistsAsync("tree/file.txt"), "Moved source files must no longer exist.");
        Require(!await store.ExistsAsync("tree/empty/.yabt-empty"), "Moving a folder must include its hidden markers.");
        Require(await ReadTextAsync(store, "history/tree/file.txt") == "parent", "The moved root payload must remain intact.");
        Require(await ReadTextAsync(store, "history/tree/nested/file.txt") == "nested", "The moved subtree must remain intact.");
        Require(await store.ExistsAsync("history/tree/empty/.yabt-empty"), "The moved empty-folder marker must be retained.");
        Require(await ReadTextAsync(store, "tree-sibling/file.txt") == "sibling", "A similarly prefixed sibling must not move.");

        await UploadTextAsync(store, "another/file.txt", "another");
        await RequireFailureAsync(() => store.MoveFolderAsync("another", "history/tree"));
        Require(await ReadTextAsync(store, "another/file.txt") == "another", "A destination collision must preserve the source.");
        Require(await ReadTextAsync(store, "history/tree/file.txt") == "parent", "A destination collision must preserve the destination.");
    }

    public static async Task VerifyFailedUploadAsync(IObjectStore store)
    {
        using var content = new FailingContractReadStream();
        await RequireFailureAsync
        (
            () => store.UploadAsync("incomplete.txt", content, "application/octet-stream", EmptyMetadata)
        );
        Require(!await store.ExistsAsync("incomplete.txt"), "A failed upload must not expose an incomplete final object.");
        await UploadTextAsync(store, "incomplete.txt", "retry");
        Require(await ReadTextAsync(store, "incomplete.txt") == "retry", "A failed upload must leave the final key usable for a retry.");
    }

    public static async Task VerifyLockContentionAndReacquisitionAsync(IArchiveMutableObjectStore store)
    {
        await using (var first = await store.AcquireArchiveMutationLockAsync())
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            await RequireFailureAsync(async () =>
            {
                await using var unexpected = await store.AcquireArchiveMutationLockAsync(cancellation.Token);
            });
            Require(cancellation.IsCancellationRequested, "A competing archive lock must wait until cancelled.");
            Require(!first.LockLostToken.IsCancellationRequested, "A rejected competitor must not invalidate the owner.");
        }

        await using var second = await store.AcquireArchiveMutationLockAsync();
        Require(!second.LockLostToken.IsCancellationRequested, "A released archive lock must allow a new owner.");
    }

    private static IReadOnlyDictionary<string, string> EmptyMetadata { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private static async Task UploadTextAsync(IObjectStore store, string key, string value)
    {
        using var content = new MemoryStream(Encoding.UTF8.GetBytes(value));
        await store.UploadAsync(key, content, "text/plain", EmptyMetadata);
    }

    private static async Task<string> ReadTextAsync(IReadOnlyObjectStore store, string key)
    {
        await using var content = await store.OpenReadAsync(key);
        using var reader = new StreamReader(content.Content, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    private static async Task RequireFailureAsync(Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (Exception)
        {
            return;
        }

        throw new InvalidOperationException("The provider operation unexpectedly succeeded.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }
}
