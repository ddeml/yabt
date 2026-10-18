using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yabt.Core;
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

    [TestMethod]
    [DataRow("")]
    [DataRow("archive")]
    [DataRow("../")]
    public async Task BackupRejectsEqualOrNestedRootsWithoutCreatingTheTarget(string targetRelativePath)
    {
        using var fixture = new LocationFixture();
        using var services = CreateServices(fixture.Root).BuildServiceProvider();
        var resolver = services.GetRequiredService<IArchiveLocationResolver>();
        var location = await resolver.LocateSourceAsync(fixture.Root);
        var targetPath = Path.GetFullPath(Path.Combine(fixture.Root, targetRelativePath));
        var target = services.GetRequiredService<ISourceRootObjectStoreResolver>().ResolveSourceRoot(targetPath);

        var exception = Assert.ThrowsExactly<YabtFileSystemException>
        (
            () => resolver.ValidateBackupLocations(location, target)
        );

        Assert.IsInstanceOfType<ObjectStoreOverlapException>(exception.InnerException);
        StringAssert.Contains(exception.Message, "overlaps target storage");
        Assert.IsEmpty(Directory.GetFileSystemEntries(fixture.Root));
    }

    [TestMethod]
    [DataRow("", "photos", "archive")]
    [DataRow("", "photos", "photos-archive")]
    [DataRow("live", "photos", "archive")]
    [DataRow("live", "", "archive")]
    public async Task BackupAllowsSiblingTargetOutsideSelectedDataAndMetadata
    (
        string livePrefix,
        string selection,
        string targetRelativePath
    )
    {
        using var fixture = new LocationFixture();
        var sourcePath = Directory.CreateDirectory(Path.Combine(fixture.Root, selection)).FullName;
        Directory.CreateDirectory(Path.Combine(fixture.Root, livePrefix, selection));
        using var services = CreateServices(fixture.Root, new(livePrefix, "hist")).BuildServiceProvider();
        var resolver = services.GetRequiredService<IArchiveLocationResolver>();
        var location = await resolver.LocateSourceAsync(sourcePath);
        var targetPath = Path.Combine(fixture.Root, targetRelativePath);
        var target = services.GetRequiredService<ISourceRootObjectStoreResolver>().ResolveSourceRoot(targetPath);

        resolver.ValidateBackupLocations(location, target);

        Assert.IsFalse(Directory.Exists(targetPath));
    }

    [TestMethod]
    public async Task BackupProtectsPolicyLocationOutsideExplicitLivePrefix()
    {
        using var fixture = new LocationFixture();
        var policyPath = Directory.CreateDirectory(Path.Combine(fixture.Root, "photos")).FullName;
        Directory.CreateDirectory(Path.Combine(fixture.Root, "live", "photos"));
        using var services = CreateServices(fixture.Root, new("live", "hist")).BuildServiceProvider();
        var resolver = services.GetRequiredService<IArchiveLocationResolver>();
        var location = await resolver.LocateSourceAsync(policyPath);
        var target = services.GetRequiredService<ISourceRootObjectStoreResolver>().ResolveSourceRoot(policyPath);

        var exception = Assert.ThrowsExactly<YabtFileSystemException>
        (
            () => resolver.ValidateBackupLocations(location, target)
        );

        StringAssert.Contains(exception.Message, "source folder policy");
        Assert.IsInstanceOfType<ObjectStoreOverlapException>(exception.InnerException);
    }

    [TestMethod]
    public async Task BackupProtectsDescriptorWhenSelectedDataResolvesOutsideItsRoot()
    {
        using var fixture = new LocationFixture();
        var sourcePath = Directory.CreateDirectory(Path.Combine(fixture.Root, "source")).FullName;
        var dataPath = Directory.CreateDirectory(Path.Combine(fixture.Root, "data")).FullName;
        fixture.LinkDirectory(Path.Combine(sourcePath, "live"), dataPath);
        using var services = CreateServices(sourcePath, new("live", "hist")).BuildServiceProvider();
        var resolver = services.GetRequiredService<IArchiveLocationResolver>();
        var location = await resolver.LocateSourceAsync(sourcePath);
        var target = services.GetRequiredService<ISourceRootObjectStoreResolver>().ResolveSourceRoot(sourcePath);

        var exception = Assert.ThrowsExactly<YabtFileSystemException>
        (
            () => resolver.ValidateBackupLocations(location, target)
        );

        StringAssert.Contains(exception.Message, "source root metadata");
        Assert.IsInstanceOfType<ObjectStoreOverlapException>(exception.InnerException);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BackupResolvesTargetAliasesAndMissingSuffixes(bool existingSuffix)
    {
        using var fixture = new LocationFixture();
        var sourcePath = Directory.CreateDirectory(Path.Combine(fixture.Root, "source")).FullName;
        var aliasPath = Path.Combine(fixture.Root, "alias");
        fixture.LinkDirectory(aliasPath, sourcePath);
        if (existingSuffix) { Directory.CreateDirectory(Path.Combine(sourcePath, "archive")); }
        using var services = CreateServices(sourcePath).BuildServiceProvider();
        var resolver = services.GetRequiredService<IArchiveLocationResolver>();
        var location = await resolver.LocateSourceAsync(sourcePath);
        var targetPath = Path.Combine(aliasPath, "archive");
        var target = services.GetRequiredService<ISourceRootObjectStoreResolver>().ResolveSourceRoot(targetPath);

        var exception = Assert.ThrowsExactly<YabtFileSystemException>
        (
            () => resolver.ValidateBackupLocations(location, target)
        );

        Assert.IsInstanceOfType<ObjectStoreOverlapException>(exception.InnerException);
        Assert.AreEqual(existingSuffix, Directory.Exists(targetPath));
    }

    [TestMethod]
    public async Task BackupResolvesLinksInAnotherLinksTargetAncestors()
    {
        using var fixture = new LocationFixture();
        var sourcePath = Directory.CreateDirectory(Path.Combine(fixture.Root, "source")).FullName;
        Directory.CreateDirectory(Path.Combine(sourcePath, "archive"));
        var firstAlias = Path.Combine(fixture.Root, "first");
        var secondAlias = Path.Combine(fixture.Root, "second");
        fixture.LinkDirectory(firstAlias, sourcePath);
        fixture.LinkDirectory(secondAlias, Path.Combine(firstAlias, "archive"));
        using var services = CreateServices(sourcePath).BuildServiceProvider();
        var resolver = services.GetRequiredService<IArchiveLocationResolver>();
        var location = await resolver.LocateSourceAsync(sourcePath);
        var target = services.GetRequiredService<ISourceRootObjectStoreResolver>().ResolveSourceRoot(secondAlias);

        var exception = Assert.ThrowsExactly<YabtFileSystemException>
        (
            () => resolver.ValidateBackupLocations(location, target)
        );

        Assert.IsInstanceOfType<ObjectStoreOverlapException>(exception.InnerException);
    }

    [TestMethod]
    public async Task BackupAliasResolutionTerminatesForCyclicLinks()
    {
        using var fixture = new LocationFixture();
        var sourcePath = Directory.CreateDirectory(Path.Combine(fixture.Root, "source")).FullName;
        var cyclePath = Path.Combine(fixture.Root, "cycle");
        fixture.LinkDirectory(cyclePath, cyclePath);
        using var services = CreateServices(sourcePath).BuildServiceProvider();
        var resolver = services.GetRequiredService<IArchiveLocationResolver>();
        var location = await resolver.LocateSourceAsync(sourcePath);
        var target = services.GetRequiredService<ISourceRootObjectStoreResolver>().ResolveSourceRoot(cyclePath);

        var exception = Assert.ThrowsExactly<YabtFileSystemException>
        (
            () => resolver.ValidateBackupLocations(location, target)
        );

        StringAssert.Contains(exception.Message, "could not safely resolve filesystem path");
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
