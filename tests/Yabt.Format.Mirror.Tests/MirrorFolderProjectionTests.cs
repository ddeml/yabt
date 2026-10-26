using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.Tests;

namespace Yabt.Format.Mirror.Tests;

[TestClass]
public sealed class MirrorFolderProjectionTests
{
    [TestMethod]
    public async Task FolderProjectionListsOnlyImmediateChildrenAndKeepsPayloadsLazy()
    {
        using var services = CreateServices();
        var projector = GetProjector(services);
        var inner = new MemoryObjectStore();
        await WriteAsync(inner, "selected/file.txt", "root payload");
        await WriteAsync(inner, "selected/child/deep.txt", "child payload");
        var source = new ObservedSource(inner, "selected");

        await using var unit = await projector.ProjectBackupFolderAsync
        (
            new(source, SourcePrefix: "selected"), string.Empty
        );

        Assert.AreEqual(string.Empty, unit.RelativePath);
        CollectionAssert.AreEqual(new[] { "file.txt" }, unit.Objects.Select(item => item.RelativePath).ToArray());
        CollectionAssert.AreEqual(new[] { "child" }, unit.ChildFolderPaths.ToArray());
        CollectionAssert.AreEqual(new[] { "selected" }, source.ListedFolders);
        Assert.AreEqual(1, source.ReadinessCount);
        Assert.AreEqual(0, source.ReadCount);
        await using var content = await unit.Objects[0].OpenContentAsync(default);
        using var reader = new StreamReader(content.Content);
        Assert.AreEqual("root payload", await reader.ReadToEndAsync());
        Assert.AreEqual(1, source.ReadCount);
    }

    [TestMethod]
    public async Task FolderProjectionKeepsPathsRelativeToSelectedRoot()
    {
        using var services = CreateServices();
        var inner = new MemoryObjectStore();
        await WriteAsync(inner, "selected/child/ spaced .txt", "payload");
        await WriteAsync(inner, "selected/child/grandchild/file.txt", "descendant");
        var source = new ObservedSource(inner, "selected/child");

        await using var unit = await GetProjector(services).ProjectBackupFolderAsync
        (
            new(source, SourcePrefix: "selected"), "child"
        );

        Assert.AreEqual("child", unit.RelativePath);
        Assert.AreEqual("child/ spaced .txt", unit.Objects.Single().RelativePath);
        CollectionAssert.AreEqual(new[] { "child/grandchild" }, unit.ChildFolderPaths.ToArray());
        Assert.AreEqual(0, source.ReadinessCount, "Descendant work should not add a readiness request per folder.");
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("child")]
    public async Task EmptyFolderProjectionEmitsOnlyItsMarker(string relativeFolder)
    {
        using var services = CreateServices();
        var source = new ObservedSource
        (
            new MemoryObjectStore(), ArchiveLayout.CombinePrefixAndRelativePath("selected", relativeFolder)
        );

        await using var unit = await GetProjector(services).ProjectBackupFolderAsync
        (
            new(source, SourcePrefix: "selected"), relativeFolder
        );

        Assert.AreEqual
        (
            ArchiveLayout.CombinePrefixAndRelativePath(relativeFolder, ".yabt-empty"),
            unit.Objects.Single().RelativePath
        );
        Assert.IsEmpty(unit.ChildFolderPaths);
        Assert.AreEqual(0, source.ReadCount);
    }

    [TestMethod]
    public async Task FolderWithOnlyChildrenDoesNotEmitEmptyMarker()
    {
        using var services = CreateServices();
        var inner = new MemoryObjectStore();
        await WriteAsync(inner, "child/file.txt", "payload");
        var source = new ObservedSource(inner, string.Empty);

        await using var unit = await GetProjector(services).ProjectBackupFolderAsync(new(source), string.Empty);

        Assert.IsEmpty(unit.Objects);
        CollectionAssert.AreEqual(new[] { "child" }, unit.ChildFolderPaths.ToArray());
    }

    [TestMethod]
    public async Task IncompleteListingDoesNotReturnWorkUnitOrReadPayloads()
    {
        using var services = CreateServices();
        var inner = new MemoryObjectStore();
        await WriteAsync(inner, "file.txt", "payload");
        var source = new ObservedSource(inner, string.Empty) { FailAfterFirstItem = true };

        await Assert.ThrowsExactlyAsync<IOException>
        (
            () => GetProjector(services).ProjectBackupFolderAsync(new(source), string.Empty)
        );

        Assert.AreEqual(0, source.ReadCount);
        CollectionAssert.AreEqual(new[] { string.Empty }, source.ListedFolders);
    }

    private static ServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddYabtMirrorFormatHandler();
        return services.BuildServiceProvider();
    }

    private static IArchiveBackupFolderProjector GetProjector(ServiceProvider services) =>
        (IArchiveBackupFolderProjector)services.GetRequiredService<IArchiveFormatHandler>();

    private static async Task WriteAsync(MemoryObjectStore store, string path, string text)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        await store.UploadAsync(path, stream, "text/plain", new Dictionary<string, string>(StringComparer.Ordinal));
    }

    private sealed class ObservedSource(MemoryObjectStore _inner, string _expectedFolder) : IReadOnlyObjectStore
    {
        public List<string> ListedFolders { get; } = [];

        public int ReadCount { get; private set; }

        public int ReadinessCount { get; private set; }

        public bool FailAfterFirstItem { get; init; }

        public IObjectStoreNamespace Namespace => ((IReadOnlyObjectStore)_inner).Namespace;

        public Task EnsureReadyAsync(CancellationToken cancellationToken = default)
        {
            ReadinessCount++;
            return _inner.EnsureReadyAsync(cancellationToken);
        }

        public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) =>
            _inner.ExistsAsync(key, cancellationToken);

        public Task<ArchiveObjectContent> OpenReadAsync(string key, CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return _inner.OpenReadAsync(key, cancellationToken);
        }

        public async IAsyncEnumerable<ArchiveFolderItem> GetFolderItemsAsync
        (
            string? folderPrefix,
            bool recursive = false,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            Assert.IsFalse(recursive);
            var folder = ObjectPath.Parse(folderPrefix).Value;
            Assert.AreEqual(_expectedFolder, folder, "The projector descended into a child folder.");
            ListedFolders.Add(folder);
            var items = _inner.GetFolderItemsAsync(folderPrefix, recursive, cancellationToken);
            await foreach (var item in items)
            {
                yield return item;
                if (FailAfterFirstItem) { throw new IOException("Interrupted immediate folder listing."); }
            }
        }
    }
}
