using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.Format.Zip;
using Yabt.Metadata;
using Yabt.Sync.Implementation;
using Yabt.Tests;

namespace Yabt.Sync.Tests;

[TestClass]
public sealed class ArchiveProjectionPathBoundaryTests
{
    [TestMethod]
    public async Task NestedProjectionsReadPoliciesByFullCanonicalPrefixAndKeepDisplayLeaves()
    {
        const string sourcePrefix = " source prefix ";
        const string outerPrefix = $"{sourcePrefix}/ outer ";
        const string innerPrefix = $"{outerPrefix}/ inner ";
        var sourceStore = new MemoryObjectStore(provideContentHash: true);
        await UploadAsync(sourceStore, $"{outerPrefix}/{FolderPolicyFileNames.Primary}", "{}");
        await UploadAsync(sourceStore, $"{innerPrefix}/{FolderPolicyFileNames.Primary}", "{}");
        await UploadAsync(sourceStore, $"{innerPrefix}/ file.txt ", "nested content");
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddYabtMetadata();
        services.AddYabtZipFormatHandler();
        using var provider = services.BuildServiceProvider();
        var handler = new RecordingFormatHandler(provider.GetRequiredService<IArchiveFormatHandler>());
        var policyPrefixes = new List<string>();
        using var cancellation = new CancellationTokenSource();
        var store = new ArchiveProjectionObjectStore
        (
            sourceStore,
            "selected root",
            sourcePrefix,
            (prefix, cancellationToken) =>
            {
                Assert.AreEqual(cancellation.Token, cancellationToken);
                policyPrefixes.Add(prefix);
                return Task.FromResult(new FolderPolicy(handler.FormatName));
            },
            new Dictionary<string, IArchiveFormatHandler>(StringComparer.Ordinal)
            {
                [handler.FormatName] = handler,
            }
        );
        var items = new List<ArchiveFolderItem>();
        var sourceItems = store.GetFolderItemsAsync(sourcePrefix, cancellationToken: cancellation.Token);
        await foreach (var item in sourceItems)
        {
            items.Add(item);
        }

        CollectionAssert.AreEqual(new[] { outerPrefix, innerPrefix }, policyPrefixes);
        CollectionAssert.AreEqual
        (
            new[] { " outer ", " inner " },
            handler.Requests.Select(request => request.SourceDisplayName).ToArray()
        );
        CollectionAssert.AreEqual
        (
            new[] { outerPrefix, innerPrefix },
            handler.Requests.Select(request => request.SourcePrefix).ToArray()
        );
        Assert.HasCount(2, items);
        Assert.IsTrue(items.All(item => item.Name.StartsWith(" outer .xxh128-", StringComparison.Ordinal)));
        var expectedNamespace = ((IReadOnlyObjectStore)sourceStore).Namespace;
        Assert.AreSame(expectedNamespace, store.Namespace);
        foreach (var request in handler.Requests)
        {
            Assert.AreSame(expectedNamespace, request.SourceStore.Namespace);
        }
    }

    private static async Task UploadAsync(MemoryObjectStore store, string key, string text)
    {
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes(text));
        await store.UploadAsync(key, content, "application/octet-stream", new Dictionary<string, string>());
    }

    private sealed class RecordingFormatHandler(IArchiveFormatHandler _inner) : IArchiveFormatHandler
    {
        public List<ArchiveProjectionRequest> Requests { get; } = [];

        public string FormatName => _inner.FormatName;

        public bool ProjectsBesideSourceFolder => _inner.ProjectsBesideSourceFolder;

        public bool CanRestoreArtifact(ArchiveProjectedObject artifact) => _inner.CanRestoreArtifact(artifact);

        public ArchiveBackupProjection ProjectBackupAsync
        (
            ArchiveProjectionRequest request,
            CancellationToken cancellationToken = default
        )
        {
            Requests.Add(request);
            return _inner.ProjectBackupAsync(request, cancellationToken);
        }

        public Task<ArchiveRestoreProjection> ProjectRestoreAsync
        (
            ArchiveRestoreRequest request,
            CancellationToken cancellationToken = default
        ) => _inner.ProjectRestoreAsync(request, cancellationToken);
    }
}
