using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.Metadata;

namespace Yabt.FileSystem.Tests;

[TestClass]
public sealed class FileSystemArchiveLocationResolverTests
{
    [TestMethod]
    public async Task SelectedWhitespaceDirectoryRetainsCanonicalIdentityAndNativeDisplayName()
    {
        using var fixture = new LocationFixture();
        var selected = Directory.CreateDirectory(Path.Combine(fixture.Root, " child\u00a0")).FullName;
        var policyReader = new RecordingPolicyReader();
        using var services = CreateServices(fixture.Root, policyReader: policyReader).BuildServiceProvider();
        var resolver = services.GetRequiredService<IArchiveLocationResolver>();

        var location = await resolver.LocateSourceAsync(Path.Combine(selected, ".") + Path.DirectorySeparatorChar);
        await resolver.ReadPolicyAsync(location, location.SelectionPrefix);

        Assert.AreEqual(" child\u00a0", location.DisplayName);
        Assert.AreEqual(ObjectPath.Parse(" child\u00a0"), location.SourcePrefix);
        Assert.AreEqual(location.SourcePrefix, location.SelectionPrefix);
        Assert.AreEqual(fixture.Root, ((ILocalObjectStore)location.SourceStore).RootPath);
        CollectionAssert.AreEqual(new[] { selected }, policyReader.Paths);
    }

    [TestMethod]
    [DataRow("", "live", "")]
    [DataRow("child", "live/child", "child")]
    [DataRow("live/child", "live/child", "live/child")]
    public async Task ExplicitLivePrefixKeepsSelectedPolicySeparateFromEffectiveDataPrefix
    (
        string selection,
        string expectedSourcePrefix,
        string expectedPolicyPrefix
    )
    {
        using var fixture = new LocationFixture();
        var policyReader = new RecordingPolicyReader();
        using var services = CreateServices(fixture.Root, new("live", "hist"), policyReader).BuildServiceProvider();
        var resolver = services.GetRequiredService<IArchiveLocationResolver>();
        var selected = Path.Combine(fixture.Root, selection.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(selected);

        var location = await resolver.LocateSourceAsync(selected);
        await resolver.ReadPolicyAsync(location, location.SelectionPrefix);

        Assert.AreEqual(ObjectPath.Parse(expectedSourcePrefix), location.SourcePrefix);
        Assert.AreEqual(ObjectPath.Parse(expectedPolicyPrefix), location.SelectionPrefix);
        CollectionAssert.AreEqual(new[] { selected }, policyReader.Paths);
    }

    [TestMethod]
    public async Task RestoreSelectionRequiresTheDescriptorDirectory()
    {
        using var fixture = new LocationFixture();
        using var services = CreateServices(fixture.Root).BuildServiceProvider();
        var resolver = services.GetRequiredService<IArchiveLocationResolver>();

        await resolver.LocateSourceAsync(Path.Combine(fixture.Root, "."), requireRoot: true);
        var trailingRoot = await resolver.LocateSourceAsync
        (
            fixture.Root + Path.DirectorySeparatorChar,
            requireRoot: true
        );
        Assert.IsTrue(trailingRoot.SelectionPrefix.IsRoot);
        var exception = await Assert.ThrowsAsync<YabtFileSystemException>
        (
            () => resolver.LocateSourceAsync(Path.Combine(fixture.Root, "child"), requireRoot: true)
        );

        StringAssert.Contains(exception.Message, "folder containing .yabt-root.json");
    }

    [TestMethod]
    public async Task UnsupportedNativeSpellingFailsBeforeDescriptorLookup()
    {
        using var fixture = new LocationFixture();
        var locator = new RecordingRootLocator(fixture.Root, ArchiveLayout.Default);
        using var services = CreateServices(fixture.Root, locator: locator).BuildServiceProvider();
        var resolver = services.GetRequiredService<IArchiveLocationResolver>();
        var name = OperatingSystem.IsWindows() ? "unsupported. " : "literal\\backslash";

        var exception = await Assert.ThrowsAsync<YabtFileSystemException>
        (
            () => resolver.LocateSourceAsync(Path.Combine(fixture.Root, name))
        );

        StringAssert.Contains(exception.Message, name);
        Assert.AreEqual(0, locator.Calls);
    }

    [TestMethod]
    public async Task RegisteredSourceRootResolverRemainsTheSourceOfTheStore()
    {
        using var fixture = new LocationFixture();
        var registrations = CreateServices(fixture.Root);
        var original = registrations.Single(item => item.ServiceType == typeof(ISourceRootObjectStoreResolver));
        registrations.Remove(original);
        TrackingSourceRootResolver? trackingResolver = null;
        registrations.AddSingleton<ISourceRootObjectStoreResolver>
        (
            provider => trackingResolver = new
            (
                (ISourceRootObjectStoreResolver)original.ImplementationFactory!(provider)
            )
        );
        using var services = registrations.BuildServiceProvider();

        var location = await services.GetRequiredService<IArchiveLocationResolver>().LocateSourceAsync(fixture.Root);

        Assert.IsNotNull(trackingResolver);
        Assert.AreEqual(fixture.Root, trackingResolver.RequestedRoot);
        Assert.AreSame(trackingResolver.ResolvedStore, location.SourceStore);
    }

    [TestMethod]
    public async Task SelectedUnapprovedLinkIsRejectedBeforeItsPolicyReaderIsInvoked()
    {
        using var fixture = new LocationFixture();
        var outside = Directory.CreateDirectory(Path.Combine(fixture.Root, "outside")).FullName;
        var source = Directory.CreateDirectory(Path.Combine(fixture.Root, "source")).FullName;
        var link = Path.Combine(source, "linked");
        fixture.LinkDirectory(link, outside);
        var policyReader = new RecordingPolicyReader();
        using var services = CreateServices(source, policyReader: policyReader).BuildServiceProvider();
        var resolver = services.GetRequiredService<IArchiveLocationResolver>();
        var location = await resolver.LocateSourceAsync(link);
        var target = services.GetRequiredService<ISourceRootObjectStoreResolver>()
            .ResolveSourceRoot(Path.Combine(fixture.Root, "archive"));
        resolver.ConfigureSourceTraversal(location, target);

        var exception = await Assert.ThrowsAsync<YabtFileSystemException>
        (
            () => resolver.ReadPolicyAsync(location, location.SelectionPrefix)
        );

        StringAssert.Contains(exception.ToString(), "linked");
        Assert.IsFalse(policyReader.Paths.Contains(link, StringComparer.Ordinal));
    }

    private static ServiceCollection CreateServices
    (
        string rootPath,
        ArchiveLayout? layout = null,
        RecordingPolicyReader? policyReader = null,
        RecordingRootLocator? locator = null
    )
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddYabtFileSystemObjectStore();
        services.AddSingleton<IBackupRootLocator>(locator ?? new(rootPath, layout ?? ArchiveLayout.Default));
        services.AddSingleton<IFolderPolicyReader>(policyReader ?? new());
        return services;
    }

    private sealed class RecordingPolicyReader : IFolderPolicyReader
    {
        public List<string> Paths { get; } = [];

        public Task<FolderPolicy> ReadPolicyAsync(string folderPath, CancellationToken cancellationToken = default)
        {
            Paths.Add(folderPath);
            return Task.FromResult(FolderPolicy.Default);
        }
    }

    private sealed class RecordingRootLocator(string rootPath, ArchiveLayout layout) : IBackupRootLocator
    {
        public int Calls { get; private set; }

        public Task<BackupRootLocation> LocateRootAsync(string startPath, CancellationToken cancellationToken = default)
        {
            Calls++;
            var descriptor = new BackupRootDescriptor
            (
                BackupRootDescriptor.ExpectedDocumentType,
                BackupRootDescriptor.ExpectedSchemaVersion,
                "location-tests",
                DateTimeOffset.UnixEpoch,
                layout,
                []
            );
            return Task.FromResult(new BackupRootLocation(rootPath, descriptor));
        }
    }

    private sealed class TrackingSourceRootResolver(ISourceRootObjectStoreResolver _inner) : ISourceRootObjectStoreResolver
    {
        public string? RequestedRoot { get; private set; }
        public IObjectStore? ResolvedStore { get; private set; }

        public IObjectStore ResolveSourceRoot(string rootPath)
        {
            RequestedRoot = rootPath;
            return ResolvedStore = _inner.ResolveSourceRoot(rootPath);
        }
    }

    private sealed class LocationFixture : IDisposable
    {
        private readonly string _basePath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "yabt-location-tests"));
        private readonly List<string> _links = [];

        public LocationFixture()
        {
            Root = Directory.CreateDirectory(Path.Combine(_basePath, Guid.NewGuid().ToString("N"))).FullName;
        }

        public string Root { get; }

        public void LinkDirectory(string path, string target)
        {
            FileSystemLinkFixture.CreateDirectoryLink(path, target);
            _links.Add(path);
        }

        public void Dispose()
        {
            Assert.AreEqual(_basePath, Path.GetDirectoryName(Path.GetFullPath(Root)));
            Assert.IsTrue(Guid.TryParseExact(Path.GetFileName(Root), "N", out _));
            foreach (var link in _links.AsEnumerable().Reverse())
            {
                Assert.IsTrue(Path.GetFullPath(link).StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal));
                Directory.Delete(link, recursive: false);
            }

            Directory.Delete(Root, recursive: true);
        }
    }
}
