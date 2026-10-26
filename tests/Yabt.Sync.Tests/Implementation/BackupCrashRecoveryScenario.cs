using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Yabt.Core.Models;
using Yabt.Metadata;

namespace Yabt.Sync.Tests.Implementation;

internal static class BackupCrashRecoveryScenario
{
    private const string PayloadPath = "branch/item.txt";
    private const string OriginalPayload = "original archived payload";
    private const string ChangedPayload = "replacement committed by the interrupted backup";
    private const string DowntimePayload = "source changed again while the backup process was absent";
    private const string StablePayload = "unchanged sibling payload";
    private const string ObsoletePayload = "obsolete bytes must survive in history";
    private const string BaselinePackageRecord = "baseline-package-name.txt";

    internal static async Task PrepareAsync(CrashRecoveryFixture fixture, string scenario)
    {
        ValidateScenario(scenario);
        await fixture.WriteSourceAsync(PayloadPath, OriginalPayload);
        await fixture.WriteSourceAsync("keep.txt", StablePayload);
        await fixture.WriteSourceAsync("obsolete.txt", ObsoletePayload);
        Directory.CreateDirectory(CrashRecoveryFixture.PathFor(fixture.SourceRoot, "branch/empty"));
        if (scenario == "backup-package")
        {
            await fixture.WriteSourceAsync("branch/.yabt-policy.json", "{\"format\":\"zip\"}");
        }

        var baseline = await fixture.BackupAsync();
        Assert.IsTrue(baseline.Completed, baseline.Message);
        if (scenario == "backup-package")
        {
            var packagePath = Directory.EnumerateFiles(fixture.ArchiveRoot, "*.zip").Single();
            await File.WriteAllTextAsync
            (
                Path.Combine(fixture.RootPath, BaselinePackageRecord), Path.GetFileName(packagePath)
            );
        }
        await SeedEmptyHistoryCatalogAsync(fixture);
        await fixture.WriteSourceAsync(PayloadPath, ChangedPayload);
        File.Delete(CrashRecoveryFixture.PathFor(fixture.SourceRoot, "obsolete.txt"));
    }

    internal static void AssertInterrupted(CrashRecoveryFixture fixture, string scenario)
    {
        ValidateScenario(scenario);
        Assert.IsTrue(File.Exists(Path.Combine(fixture.ArchiveRoot, ArchiveChangeManifest.InvalidationMarkerFileName)));
        Assert.AreEqual
        (
            scenario == "backup-finalization",
            File.Exists(Path.Combine(fixture.ArchiveRoot, ArchiveChangeManifest.UncompressedFileName)),
            "Only the finalization gate may have written the replacement live manifest."
        );
        Assert.IsFalse(File.Exists(Path.Combine(fixture.ArchiveRoot, ArchiveChangeManifest.BrotliFileName)));
        Assert.AreEqual(1, Directory.EnumerateFiles(fixture.ProgressDirectory, "*.sqlite").Count());
        CrashRecoveryFixture.AssertText(fixture.ArchiveRoot, "keep.txt", StablePayload);

        if (scenario == "backup-package")
        {
            var oldPackageName = ReadBaselinePackageName(fixture);
            var livePackages = Directory.EnumerateFiles(fixture.ArchiveRoot, "*.zip").ToArray();
            Assert.HasCount(2, livePackages);
            Assert.IsTrue(File.Exists(Path.Combine(fixture.ArchiveRoot, oldPackageName)));
            Assert.IsTrue(File.Exists(Path.Combine(fixture.ArchiveRoot, oldPackageName + ArchivePackageManifestFileNames.AdjacentSuffix)));
            var partialPackage = livePackages.Single(path => Path.GetFileName(path) != oldPackageName);
            Assert.IsFalse
            (
                File.Exists(partialPackage + ArchivePackageManifestFileNames.AdjacentSuffix),
                "The process must stop after ZIP publication and before publishing its adjacent manifest."
            );
            using var zip = ZipFile.OpenRead(partialPackage);
            AssertZipPayload(zip, ChangedPayload);
        }
        else
        {
            CrashRecoveryFixture.AssertHistoryText(fixture.ArchiveRoot, PayloadPath, OriginalPayload);
            if (scenario == "backup-history")
            {
                Assert.IsFalse(File.Exists(CrashRecoveryFixture.PathFor(fixture.ArchiveRoot, PayloadPath)));
            }
            else
            {
                CrashRecoveryFixture.AssertText(fixture.ArchiveRoot, PayloadPath, ChangedPayload);
            }
            Assert.IsTrue(File.Exists(HistoryPath(fixture, ArchiveHistoryManifest.InvalidationMarkerFileName)));
        }

        if (scenario == "backup-finalization")
        {
            Assert.IsFalse(File.Exists(Path.Combine(fixture.ArchiveRoot, "obsolete.txt")));
            CrashRecoveryFixture.AssertHistoryText(fixture.ArchiveRoot, "obsolete.txt", ObsoletePayload);
        }
        else
        {
            CrashRecoveryFixture.AssertText(fixture.ArchiveRoot, "obsolete.txt", ObsoletePayload);
        }
    }

    internal static Task ChangeDuringDowntimeAsync(CrashRecoveryFixture fixture, string scenario)
    {
        ValidateScenario(scenario);
        return scenario == "backup-commit" ?
            fixture.WriteSourceAsync(PayloadPath, DowntimePayload) : Task.CompletedTask;
    }

    internal static async Task AssertRecoveredAsync(CrashRecoveryFixture fixture, string scenario)
    {
        ValidateScenario(scenario);
        var expectedPayload = scenario == "backup-commit" ? DowntimePayload : ChangedPayload;
        AssertRecoveryMarkersRemoved(fixture);
        Assert.IsFalse(File.Exists(Path.Combine(fixture.ArchiveRoot, "obsolete.txt")));
        CrashRecoveryFixture.AssertHistoryText(fixture.ArchiveRoot, "obsolete.txt", ObsoletePayload);
        CrashRecoveryFixture.AssertText(fixture.ArchiveRoot, "keep.txt", StablePayload);
        AssertDescriptorBytes(fixture.SourceRoot, fixture.ArchiveRoot);

        await using var services = fixture.CreateServices();
        await using (var stream = File.OpenRead(Path.Combine(fixture.ArchiveRoot, ArchiveChangeManifest.UncompressedFileName)))
        {
            using var manifest = await services.GetRequiredService<IChangeManifestSerializer>().ReadAsync(stream);
            Assert.AreEqual("mirror", manifest.RootFormat);
            Assert.IsFalse(manifest.Entries.Any(entry => entry.RelativePath == "obsolete.txt"));
            Assert.IsTrue(manifest.Entries.Any(entry => entry.RelativePath == "keep.txt"));
        }

        if (scenario == "backup-package")
        {
            var livePackagePath = Directory.EnumerateFiles(fixture.ArchiveRoot, "*.zip").Single();
            var oldPackageName = ReadBaselinePackageName(fixture);
            Assert.AreNotEqual(oldPackageName, Path.GetFileName(livePackagePath));
            await AssertPackagePairAsync(services, livePackagePath, expectedPayload);
            var historicalPackagePath = Directory.EnumerateFiles
            (
                Path.Combine(fixture.ArchiveRoot, ".yabt-hist"), oldPackageName, SearchOption.AllDirectories
            ).Single();
            await AssertPackagePairAsync(services, historicalPackagePath, OriginalPayload);
        }
        else
        {
            CrashRecoveryFixture.AssertText(fixture.ArchiveRoot, PayloadPath, expectedPayload);
            CrashRecoveryFixture.AssertHistoryText(fixture.ArchiveRoot, PayloadPath, OriginalPayload);
            if (scenario == "backup-commit")
            {
                CrashRecoveryFixture.AssertHistoryText(fixture.ArchiveRoot, PayloadPath, ChangedPayload);
            }
        }

        var verified = await services.GetRequiredService<IArchiveSynchronizer>().VerifyAsync
        (
            new(fixture.SourceRoot, ByteForByte: true)
        );
        Assert.IsTrue(verified.Completed, verified.Message);
        Assert.AreEqual(0, verified.NewCount);
        Assert.AreEqual(0, verified.ChangedCount);
        Assert.AreEqual(0, verified.ExtraCount);

        var restored = await fixture.RestoreAsync();
        Assert.IsTrue(restored.Completed, restored.Message);
        AssertExactBytes(fixture.DestinationRoot, PayloadPath, expectedPayload);
        AssertExactBytes(fixture.DestinationRoot, "keep.txt", StablePayload);
        Assert.IsTrue(Directory.Exists(CrashRecoveryFixture.PathFor(fixture.DestinationRoot, "branch/empty")));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.DestinationRoot, "obsolete.txt")));
        AssertDescriptorBytes(fixture.ArchiveRoot, fixture.DestinationRoot);
        AssertRecoveryMarkersRemoved(fixture);
    }

    private static async Task SeedEmptyHistoryCatalogAsync(CrashRecoveryFixture fixture)
    {
        await using var services = fixture.CreateServices();
        var serializer = services.GetRequiredService<IHistoryManifestSerializer>();
        using var manifest = serializer.Create([]);
        Directory.CreateDirectory(Path.Combine(fixture.ArchiveRoot, ".yabt-hist"));
        await using var stream = File.Create(HistoryPath(fixture, ArchiveHistoryFileNames.Manifest));
        await serializer.WriteAsync(manifest, stream);
    }

    private static async Task AssertPackagePairAsync
    (
        IServiceProvider services,
        string packagePath,
        string expectedPayload
    )
    {
        var adjacentPath = packagePath + ArchivePackageManifestFileNames.AdjacentSuffix;
        var adjacentBytes = await File.ReadAllBytesAsync(adjacentPath);
        using var zip = ZipFile.OpenRead(packagePath);
        var embedded = zip.GetEntry(ArchivePackageManifestFileNames.EmbeddedEntryName);
        Assert.IsNotNull(embedded);
        await using (var embeddedStream = embedded.Open())
        {
            using var bytes = new MemoryStream();
            await embeddedStream.CopyToAsync(bytes);
            CollectionAssert.AreEqual(adjacentBytes, bytes.ToArray());
        }
        await using (var adjacentStream = File.OpenRead(adjacentPath))
        {
            using var manifest = await services.GetRequiredService<IManifestSerializer>().ReadAsync(adjacentStream);
            Assert.AreEqual(Path.GetFileName(packagePath), manifest.PackageName);
            Assert.AreEqual("branch", manifest.SourcePath);
            Assert.IsTrue(manifest.Entries.Any(entry => entry.RelativePath == "item.txt"));
        }
        AssertZipPayload(zip, expectedPayload);
    }

    private static void AssertZipPayload(ZipArchive zip, string expectedPayload)
    {
        var entry = zip.GetEntry("item.txt");
        Assert.IsNotNull(entry);
        using var payload = entry.Open();
        using var bytes = new MemoryStream();
        payload.CopyTo(bytes);
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(expectedPayload), bytes.ToArray());
    }

    private static void AssertRecoveryMarkersRemoved(CrashRecoveryFixture fixture)
    {
        Assert.IsFalse(File.Exists(Path.Combine(fixture.ArchiveRoot, ArchiveChangeManifest.InvalidationMarkerFileName)));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.ArchiveRoot, ArchiveChangeManifest.BrotliFileName)));
        Assert.IsFalse(File.Exists(HistoryPath(fixture, ArchiveHistoryManifest.InvalidationMarkerFileName)));
        Assert.IsFalse(File.Exists(HistoryPath(fixture, ArchiveHistoryFileNames.Manifest)));
        Assert.IsFalse(Directory.EnumerateFiles(fixture.ProgressDirectory, "*.sqlite").Any());
    }

    private static void AssertExactBytes(string root, string relativePath, string expected) =>
        CollectionAssert.AreEqual
        (
            Encoding.UTF8.GetBytes(expected), File.ReadAllBytes(CrashRecoveryFixture.PathFor(root, relativePath))
        );

    private static void AssertDescriptorBytes(string source, string destination) =>
        CollectionAssert.AreEqual
        (
            File.ReadAllBytes(Path.Combine(source, BackupRootFileNames.Primary)),
            File.ReadAllBytes(Path.Combine(destination, BackupRootFileNames.Primary))
        );

    private static string ReadBaselinePackageName(CrashRecoveryFixture fixture) =>
        File.ReadAllText(Path.Combine(fixture.RootPath, BaselinePackageRecord));

    private static string HistoryPath(CrashRecoveryFixture fixture, string name) =>
        Path.Combine(fixture.ArchiveRoot, ".yabt-hist", name);

    private static void ValidateScenario(string scenario)
    {
        if (scenario is not ("backup-history" or "backup-commit" or "backup-package" or "backup-finalization"))
        {
            throw new ArgumentException("Unknown backup crash-recovery scenario.", nameof(scenario));
        }
    }
}
