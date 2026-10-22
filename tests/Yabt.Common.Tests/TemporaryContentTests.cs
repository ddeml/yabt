using System.IO.Compression;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Yabt.Common.Tests;

[TestClass]
public sealed class TemporaryContentTests
{
    private string _temporaryDirectory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _temporaryDirectory = Path.Combine(Path.GetTempPath(), $"yabt-content-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_temporaryDirectory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        Assert.IsEmpty(Directory.EnumerateFileSystemEntries(_temporaryDirectory));
        Directory.Delete(_temporaryDirectory);
    }

    [TestMethod]
    public void DefaultsUseEightMiBPerContentAndSixtyFourMiBPerHost()
    {
        var options = new TemporaryContentOptions();

        Assert.AreEqual(8 * 1024 * 1024, options.GetEffectiveMemoryThresholdBytes());
        Assert.AreEqual(64L * 1024 * 1024, options.GetEffectiveTotalMemoryBudgetBytes());
        Assert.AreEqual(Path.GetTempPath(), options.GetEffectiveTemporaryDirectory());
    }

    [TestMethod]
    public async Task CrossingThresholdSpillsBeforeWritingAndPreservesEarlierBytes()
    {
        using var provider = CreateServices(threshold: 64);
        var factory = provider.GetRequiredService<ITemporaryContentFactory>();
        await using var content = factory.Create();
        byte[] first = Enumerable.Range(0, 64).Select(index => (byte)index).ToArray();
        await content.WriteAsync(first);

        Assert.IsFalse(content.IsFileBacked);
        Assert.AreEqual(64L, factory.ReservedMemoryBytes);
        await content.WriteAsync(new byte[] { 64 });

        Assert.IsTrue(content.IsFileBacked);
        Assert.AreEqual(0L, factory.ReservedMemoryBytes);
        Assert.HasCount(1, Directory.EnumerateFiles(_temporaryDirectory));
        await content.SealAsync();
        await using var reader = content.OpenRead();
        byte[] actual = new byte[65];
        await reader.ReadExactlyAsync(actual);
        CollectionAssert.AreEqual(Enumerable.Range(0, 65).Select(index => (byte)index).ToArray(), actual);
    }

    [TestMethod]
    public async Task SingleLargeWriteGoesDirectlyToDiskWithoutMemoryReservation()
    {
        using var provider = CreateServices(threshold: 32);
        var factory = provider.GetRequiredService<ITemporaryContentFactory>();
        await using var content = factory.Create();
        var bytes = new byte[256 * 1024];
        Random.Shared.NextBytes(bytes);

        await content.WriteAsync(bytes);
        await content.SealAsync();

        Assert.IsTrue(content.IsFileBacked);
        Assert.AreEqual(0L, factory.ReservedMemoryBytes);
        await using var reader = content.OpenRead();
        var actual = new byte[bytes.Length];
        await reader.ReadExactlyAsync(actual);
        CollectionAssert.AreEqual(bytes, actual);
    }

    [TestMethod]
    public void ExhaustedSharedBudgetSpillsWithoutWaitingAndReleasedCapacityCanBeReused()
    {
        using var provider = CreateServices(threshold: 8, budget: 16);
        var factory = provider.GetRequiredService<ITemporaryContentFactory>();
        using var first = factory.Create();
        using var second = factory.Create();
        using var third = factory.Create();
        first.WriteByte(1);
        second.WriteByte(2);
        third.WriteByte(3);

        Assert.AreEqual(16L, factory.ReservedMemoryBytes);
        Assert.IsFalse(first.IsFileBacked);
        Assert.IsFalse(second.IsFileBacked);
        Assert.IsTrue(third.IsFileBacked);
        first.Dispose();
        using var fourth = factory.Create();
        fourth.WriteByte(4);
        Assert.IsFalse(fourth.IsFileBacked);
        Assert.AreEqual(16L, factory.ReservedMemoryBytes);
    }

    [TestMethod]
    public void BudgetAccountsForAllocatedSegmentCapacityInsteadOfWrittenLength()
    {
        using var provider = CreateServices(threshold: 128 * 1024, budget: 64 * 1024);
        var factory = provider.GetRequiredService<ITemporaryContentFactory>();
        using var first = factory.Create();
        using var second = factory.Create();
        first.WriteByte(1);

        Assert.AreEqual(1L, first.Length);
        Assert.AreEqual(64 * 1024L, factory.ReservedMemoryBytes);
        second.WriteByte(2);
        Assert.IsTrue(second.IsFileBacked);
        Assert.AreEqual(64 * 1024L, factory.ReservedMemoryBytes);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(128 * 1024)]
    public async Task SeekOverwriteTruncateAndExtensionHaveConsistentMemoryAndDiskSemantics(int threshold)
    {
        using var provider = CreateServices(threshold);
        var factory = provider.GetRequiredService<ITemporaryContentFactory>();
        await using var content = factory.Create();
        var bytes = new byte[70 * 1024];
        Array.Fill(bytes, (byte)42);
        await content.WriteAsync(bytes);
        content.Position = 64 * 1024 - 1;
        content.Write(new byte[] { 1, 2, 3 });
        content.SetLength(64 * 1024 + 1);
        content.SetLength(bytes.Length);
        content.Position = bytes.Length + 2;
        content.WriteByte(4);
        await content.SealAsync();

        await using var reader = content.OpenRead();
        reader.Seek(64 * 1024 - 1, SeekOrigin.Begin);
        Assert.AreEqual(1, reader.ReadByte());
        Assert.AreEqual(2, reader.ReadByte());
        var remaining = new byte[bytes.Length - 64 * 1024 + 2];
        await reader.ReadExactlyAsync(remaining);
        Assert.IsTrue(remaining.Take(remaining.Length - 1).All(value => value == 0));
        Assert.AreEqual(4, remaining[^1]);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(64)]
    public async Task IndependentReadersRetainBackingAfterOwnerDisposal(int threshold)
    {
        using var provider = CreateServices(threshold);
        var factory = provider.GetRequiredService<ITemporaryContentFactory>();
        var content = factory.Create();
        await content.WriteAsync(new byte[] { 10, 20, 30 });
        await content.SealAsync();
        var first = content.OpenRead();
        var second = content.OpenRead();
        try
        {
            Assert.AreEqual(10, first.ReadByte());
            Assert.AreEqual(10, second.ReadByte());
            await content.DisposeAsync();
            Assert.AreEqual(threshold == 0 ? 0L : 64L, factory.ReservedMemoryBytes);
            Assert.AreEqual(threshold == 0 ? 1 : 0, Directory.EnumerateFiles(_temporaryDirectory).Count());
            Assert.Throws<ObjectDisposedException>(() => content.OpenRead());
            second.Position = 2;
            Assert.AreEqual(30, second.ReadByte());
            Assert.AreEqual(20, first.ReadByte());
            await first.DisposeAsync();
            Assert.AreEqual(threshold == 0 ? 0L : 64L, factory.ReservedMemoryBytes);
        }
        finally
        {
            await first.DisposeAsync();
            await second.DisposeAsync();
            await content.DisposeAsync();
        }
        Assert.AreEqual(0L, factory.ReservedMemoryBytes);
        Assert.IsEmpty(Directory.EnumerateFiles(_temporaryDirectory));
    }

    [TestMethod]
    public async Task SealingPreventsMutationAndUnsealedContentCannotOpenReaders()
    {
        using var provider = CreateServices(threshold: 64);
        var factory = provider.GetRequiredService<ITemporaryContentFactory>();
        await using var content = factory.Create();
        Assert.Throws<InvalidOperationException>(() => content.OpenRead());
        content.WriteByte(7);
        await content.SealAsync();
        await content.SealAsync();

        Assert.IsTrue(content.IsSealed);
        Assert.IsFalse(content.CanWrite);
        Assert.Throws<NotSupportedException>(() => content.WriteByte(8));
        Assert.Throws<NotSupportedException>(() => content.SetLength(0));
        await using var reader = content.OpenRead();
        Assert.IsFalse(reader.CanWrite);
        Assert.Throws<NotSupportedException>(() => reader.WriteByte(9));
        Assert.AreEqual(7, reader.ReadByte());
    }

    [TestMethod]
    public async Task PreCanceledWriteCreatesNeitherMemoryNorTemporaryFile()
    {
        using var provider = CreateServices(threshold: 1);
        var factory = provider.GetRequiredService<ITemporaryContentFactory>();
        await using var content = factory.Create();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>
        (
            async () => await content.WriteAsync(new byte[100], cancellation.Token)
        );

        Assert.AreEqual(0L, content.Length);
        Assert.AreEqual(0L, factory.ReservedMemoryBytes);
        Assert.IsEmpty(Directory.EnumerateFiles(_temporaryDirectory));
    }

    [TestMethod]
    public async Task CanceledSealDoesNotPublishContentAndDisposalCleansDisk()
    {
        using var provider = CreateServices(threshold: 0);
        var factory = provider.GetRequiredService<ITemporaryContentFactory>();
        await using var content = factory.Create();
        await content.WriteAsync(new byte[100]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>
        (
            async () => await content.SealAsync(cancellation.Token)
        );

        Assert.IsFalse(content.IsSealed);
        Assert.Throws<InvalidOperationException>(() => content.OpenRead());
        await content.DisposeAsync();
        Assert.IsEmpty(Directory.EnumerateFiles(_temporaryDirectory));
    }

    [TestMethod]
    public async Task FailedSpillPreservesMemoryAndDisposalReleasesBudget()
    {
        using var provider = CreateServices
        (
            threshold: 2,
            temporaryDirectory: Path.Combine(_temporaryDirectory, "missing")
        );
        var factory = provider.GetRequiredService<ITemporaryContentFactory>();
        await using var content = factory.Create();
        content.WriteByte(19);

        await Assert.ThrowsAsync<DirectoryNotFoundException>
        (
            async () => await content.WriteAsync(new byte[100])
        );

        Assert.IsFalse(content.IsFileBacked);
        Assert.AreEqual(1L, content.Length);
        Assert.AreEqual(2L, factory.ReservedMemoryBytes);
        content.Position = 0;
        Assert.AreEqual(19, content.ReadByte());
        await content.DisposeAsync();
        Assert.AreEqual(0L, factory.ReservedMemoryBytes);
    }

    [TestMethod]
    public void ZeroSharedBudgetForcesDiskEvenBelowIndividualThreshold()
    {
        using var provider = CreateServices(threshold: 64, budget: 0);
        var factory = provider.GetRequiredService<ITemporaryContentFactory>();
        using var content = factory.Create();
        content.WriteByte(7);

        Assert.IsTrue(content.IsFileBacked);
        Assert.AreEqual(0L, factory.ReservedMemoryBytes);
        content.SetLength(0);
        Assert.IsTrue(content.IsFileBacked);
    }

    [TestMethod]
    public async Task ConcurrentContentsShareOneBoundedCapacityBudget()
    {
        using var provider = CreateServices(threshold: 64 * 1024, budget: 128 * 1024);
        var factory = provider.GetRequiredService<ITemporaryContentFactory>();
        var contents = Enumerable.Range(0, 24).Select(_ => factory.Create()).ToArray();
        try
        {
            await Task.WhenAll(contents.Select(content => Task.Run
            (
                async () =>
                {
                    await content.WriteAsync(new byte[] { 1, 2, 3 });
                    await content.SealAsync();
                }
            )));

            Assert.AreEqual(128 * 1024L, factory.ReservedMemoryBytes);
            Assert.HasCount(2, contents.Where(content => !content.IsFileBacked));
            Assert.HasCount(22, Directory.EnumerateFiles(_temporaryDirectory));
            foreach (var content in contents)
            {
                await using var reader = content.OpenRead();
                Assert.AreEqual(1, reader.ReadByte());
            }
        }
        finally
        {
            foreach (var content in contents) { await content.DisposeAsync(); }
        }
        Assert.AreEqual(0L, factory.ReservedMemoryBytes);
    }

    [TestMethod]
    public async Task ZipArchiveCanSeekDuringCreationAndReopenIndependentSealedReaders()
    {
        using var provider = CreateServices(threshold: 64);
        var factory = provider.GetRequiredService<ITemporaryContentFactory>();
        await using var content = factory.Create();
        using (var archive = new ZipArchive(content, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("payload", CompressionLevel.NoCompression);
            await using var entryContent = entry.Open();
            await entryContent.WriteAsync(new byte[100 * 1024]);
        }
        await content.SealAsync();

        await using var first = content.OpenRead();
        await using var second = content.OpenRead();
        using var firstArchive = new ZipArchive(first, ZipArchiveMode.Read, leaveOpen: true);
        using var secondArchive = new ZipArchive(second, ZipArchiveMode.Read, leaveOpen: true);
        Assert.AreEqual(100 * 1024L, firstArchive.GetEntry("payload")!.Length);
        Assert.AreEqual(100 * 1024L, secondArchive.GetEntry("payload")!.Length);
        Assert.AreEqual(0L, factory.ReservedMemoryBytes);
    }

    [TestMethod]
    public void RepeatedRegistrationKeepsOneFactoryAndItsSharedBudget()
    {
        var services = new ServiceCollection();
        services.AddYabtTemporaryContent();
        services.AddYabtTemporaryContent();
        using var provider = services.BuildServiceProvider();

        Assert.HasCount(1, provider.GetServices<ITemporaryContentFactory>());
        Assert.AreSame
        (
            provider.GetRequiredService<ITemporaryContentFactory>(),
            provider.GetRequiredService<ITemporaryContentFactory>()
        );
    }

    [TestMethod]
    public void RegistrationPreservesExplicitFactory()
    {
        using var originalProvider = CreateServices(threshold: 64);
        var originalFactory = originalProvider.GetRequiredService<ITemporaryContentFactory>();
        var services = new ServiceCollection();
        services.AddSingleton(originalFactory);
        services.AddYabtTemporaryContent();
        using var provider = services.BuildServiceProvider();

        Assert.AreSame(originalFactory, provider.GetRequiredService<ITemporaryContentFactory>());
    }

    [TestMethod]
    public void ReloadedBudgetAppliesToNewReservationsWithoutDiscardingExistingContent()
    {
        using var provider = CreateServices(threshold: 64, budget: 128);
        var monitor = (TemporaryContentTestOptionsMonitor)provider.
            GetRequiredService<IOptionsMonitor<TemporaryContentOptions>>();
        var factory = provider.GetRequiredService<ITemporaryContentFactory>();
        using var first = factory.Create();
        first.WriteByte(42);
        monitor.Set(new()
        {
            MemoryThresholdBytes = 8,
            TotalMemoryBudgetBytes = 8,
            TemporaryDirectory = _temporaryDirectory
        });
        using var second = factory.Create();
        second.WriteByte(1);

        Assert.IsTrue(second.IsFileBacked);
        Assert.AreEqual(64L, factory.ReservedMemoryBytes);
        first.Position = 0;
        Assert.AreEqual(42, first.ReadByte());
        first.Dispose();
        using var third = factory.Create();
        third.WriteByte(2);
        Assert.IsFalse(third.IsFileBacked);
        Assert.AreEqual(8L, factory.ReservedMemoryBytes);
    }

    [TestMethod]
    [DataRow(-1, 64L)]
    [DataRow(1, -1L)]
    public void InvalidResourceSettingsFailBeforeCreatingContent(int threshold, long budget)
    {
        using var provider = CreateServices(threshold, budget);
        var factory = provider.GetRequiredService<ITemporaryContentFactory>();

        Assert.Throws<ArgumentOutOfRangeException>(() => factory.Create());
        Assert.AreEqual(0L, factory.ReservedMemoryBytes);
    }

    private ServiceProvider CreateServices
    (
        int threshold,
        long? budget = null,
        string? temporaryDirectory = null
    )
    {
        var services = new ServiceCollection();
        services.AddSingleton<IOptionsMonitor<TemporaryContentOptions>>
        (
            new TemporaryContentTestOptionsMonitor
            (
                new()
                {
                    MemoryThresholdBytes = threshold,
                    TotalMemoryBudgetBytes = budget,
                    TemporaryDirectory = temporaryDirectory ?? _temporaryDirectory
                }
            )
        );
        services.AddYabtTemporaryContent();
        return services.BuildServiceProvider();
    }

    private sealed class TemporaryContentTestOptionsMonitor
    (
        TemporaryContentOptions _currentValue
    ) : IOptionsMonitor<TemporaryContentOptions>
    {
        public TemporaryContentOptions CurrentValue { get; private set; } = _currentValue;

        public TemporaryContentOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<TemporaryContentOptions, string?> listener) => null;

        public void Set(TemporaryContentOptions options) => CurrentValue = options;
    }
}
