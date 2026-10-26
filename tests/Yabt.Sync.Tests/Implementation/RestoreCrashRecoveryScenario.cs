using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Yabt.Core.Models;
using Yabt.Metadata;

namespace Yabt.Sync.Tests.Implementation;

internal static class RestoreCrashRecoveryScenario
{
    private const string PayloadPath = "branch/item.txt";
    private const string OldPayload = "original restored payload";
    private const string NewPayload = "replacement restored payload after interruption";
    private const string StablePath = "stable.txt";
    private const string StablePayload = "unchanged neighboring payload";
    private const string ExtraPath = "obsolete.txt";
    private const string ExtraPayload = "destination-only bytes to preserve";
    private const string NestedPath = "branch/inner/deep.txt";
    private const string OldNestedPayload = "original nested package bytes";
    private const string NewNestedPayload = "replacement nested package bytes after interruption";
    private const string HistoryDirectory = ".yabt-hist";

    internal static async Task PrepareAsync(CrashRecoveryFixture fixture, string scenario)
    {
        ValidateScenario(scenario);
        await fixture.WriteSourceAsync(PayloadPath, OldPayload);
        await fixture.WriteSourceAsync(StablePath, StablePayload);
        if (scenario == "restore-commit")
        {
            await fixture.WriteSourceAsync("branch/.yabt-policy.json", "{\"format\":\"zip\"}");
            await fixture.WriteSourceAsync("branch/inner/.yabt-policy.json", "{\"format\":\"zip\"}");
            await fixture.WriteSourceAsync(NestedPath, OldNestedPayload);
        }

        var baselineBackup = await fixture.BackupAsync();
        Assert.IsTrue(baselineBackup.Completed, baselineBackup.Message);
        var baselineRestore = await fixture.RestoreAsync();
        Assert.IsTrue(baselineRestore.Completed, baselineRestore.Message);
        CrashRecoveryFixture.AssertText(fixture.DestinationRoot, PayloadPath, OldPayload);
        Assert.IsTrue(File.Exists(PathFor(fixture.DestinationRoot, ArchiveLogicalStateManifest.FileName)));

        await fixture.WriteSourceAsync(PayloadPath, NewPayload);
        if (scenario == "restore-commit")
        {
            await fixture.WriteSourceAsync(NestedPath, NewNestedPayload);
        }
        var updatedBackup = await fixture.BackupAsync();
        Assert.IsTrue(updatedBackup.Completed, updatedBackup.Message);
        await File.WriteAllTextAsync(PathFor(fixture.DestinationRoot, ExtraPath), ExtraPayload);

        // A valid, empty catalog describes this destination before its first
        // history mutation. Its invalidation and removal are recovery evidence.
        await using var services = fixture.CreateServices();
        var serializer = services.GetRequiredService<IHistoryManifestSerializer>();
        using var manifest = serializer.Create([]);
        var historyRoot = PathFor(fixture.DestinationRoot, HistoryDirectory);
        Directory.CreateDirectory(historyRoot);
        await using var content = File.Create(PathFor(historyRoot, ArchiveHistoryFileNames.Manifest));
        await serializer.WriteAsync(manifest, content);
    }

    internal static void AssertInterrupted(CrashRecoveryFixture fixture, string scenario)
    {
        ValidateScenario(scenario);
        Assert.IsTrue
        (
            File.Exists(PathFor(fixture.DestinationRoot, ArchiveLogicalStateManifest.InvalidationMarkerFileName)),
            "The killed restore must not publish trusted logical-state evidence."
        );
        Assert.IsTrue(File.Exists(PathFor(fixture.DestinationRoot, ArchiveLogicalStateManifest.FileName)));
        Assert.AreEqual(1, Directory.EnumerateFiles(fixture.ProgressDirectory, "*.sqlite").Count());
        CrashRecoveryFixture.AssertText(fixture.DestinationRoot, StablePath, StablePayload);
        AssertSingleHistoryOccurrence(fixture.DestinationRoot, PayloadPath, OldPayload);

        if (scenario == "restore-history")
        {
            Assert.IsFalse
            (
                File.Exists(PathFor(fixture.DestinationRoot, PayloadPath)),
                "The stop point must follow historization and precede replacement installation."
            );
        }
        else
        {
            CrashRecoveryFixture.AssertText(fixture.DestinationRoot, PayloadPath, NewPayload);
        }

        var historyRoot = PathFor(fixture.DestinationRoot, HistoryDirectory);
        if (scenario == "restore-finalization")
        {
            Assert.IsFalse(File.Exists(PathFor(fixture.DestinationRoot, ExtraPath)));
            AssertSingleHistoryOccurrence(fixture.DestinationRoot, ExtraPath, ExtraPayload);
            Assert.IsFalse(File.Exists(PathFor(historyRoot, ArchiveHistoryManifest.InvalidationMarkerFileName)));
            Assert.IsFalse(File.Exists(PathFor(historyRoot, ArchiveHistoryFileNames.Manifest)));
        }
        else
        {
            CrashRecoveryFixture.AssertText(fixture.DestinationRoot, ExtraPath, ExtraPayload);
            Assert.IsTrue(File.Exists(PathFor(historyRoot, ArchiveHistoryManifest.InvalidationMarkerFileName)));
            Assert.IsTrue(File.Exists(PathFor(historyRoot, ArchiveHistoryFileNames.Manifest)));
        }
    }

    internal static async Task AssertRecoveredAsync(CrashRecoveryFixture fixture, string scenario)
    {
        ValidateScenario(scenario);
        CrashRecoveryFixture.AssertText(fixture.DestinationRoot, PayloadPath, NewPayload);
        CrashRecoveryFixture.AssertText(fixture.DestinationRoot, StablePath, StablePayload);
        AssertSingleHistoryOccurrence(fixture.DestinationRoot, PayloadPath, OldPayload);
        AssertSingleHistoryOccurrence(fixture.DestinationRoot, ExtraPath, ExtraPayload);
        Assert.IsFalse(File.Exists(PathFor(fixture.DestinationRoot, ExtraPath)));
        if (scenario == "restore-commit")
        {
            CrashRecoveryFixture.AssertText(fixture.DestinationRoot, NestedPath, NewNestedPayload);
            AssertSingleHistoryOccurrence(fixture.DestinationRoot, NestedPath, OldNestedPayload);
        }

        Assert.IsFalse(File.Exists(PathFor(fixture.DestinationRoot, ArchiveLogicalStateManifest.InvalidationMarkerFileName)));
        Assert.IsFalse(File.Exists(PathFor(fixture.DestinationRoot, ArchiveChangeManifest.InvalidationMarkerFileName)));
        var historyRoot = PathFor(fixture.DestinationRoot, HistoryDirectory);
        Assert.IsFalse(File.Exists(PathFor(historyRoot, ArchiveHistoryManifest.InvalidationMarkerFileName)));
        Assert.IsFalse(File.Exists(PathFor(historyRoot, ArchiveHistoryFileNames.Manifest)));
        Assert.AreEqual(0, Directory.EnumerateFiles(fixture.ProgressDirectory, "*.sqlite").Count());

        await using var services = fixture.CreateServices();
        var serializer = services.GetRequiredService<ILogicalStateManifestSerializer>();
        await using var content = File.OpenRead(PathFor(fixture.DestinationRoot, ArchiveLogicalStateManifest.FileName));
        using var manifest = await serializer.ReadAsync(content);
        var actualPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in manifest.Entries)
        {
            Assert.IsTrue(actualPaths.Add(entry.LogicalRelativePath), entry.LogicalRelativePath);
            var restoredPath = PathFor(fixture.DestinationRoot, entry.LogicalRelativePath);
            Assert.IsTrue(File.Exists(restoredPath), entry.LogicalRelativePath);
            // This acceptance fixture intentionally contains only tiny payloads.
            var bytes = await File.ReadAllBytesAsync(restoredPath);
            Assert.AreEqual(ArchiveHash.Compute(bytes), entry.ContentHash, entry.LogicalRelativePath);
        }

        var sourcePaths = Directory.EnumerateFiles(fixture.SourceRoot, "*", SearchOption.AllDirectories);
        var expectedPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sourcePath in sourcePaths)
        {
            var relativePath = Path.GetRelativePath(fixture.SourceRoot, sourcePath).
                Replace(Path.DirectorySeparatorChar, '/');
            if (relativePath == BackupRootFileNames.Primary) { continue; }
            expectedPaths.Add(relativePath);
            CollectionAssert.AreEqual
            (
                await File.ReadAllBytesAsync(sourcePath),
                await File.ReadAllBytesAsync(PathFor(fixture.DestinationRoot, relativePath)),
                relativePath
            );
        }
        Assert.IsTrue(expectedPaths.SetEquals(actualPaths), "Logical evidence must cover exactly the restored payloads.");
        CollectionAssert.AreEqual
        (
            await File.ReadAllBytesAsync(PathFor(fixture.SourceRoot, BackupRootFileNames.Primary)),
            await File.ReadAllBytesAsync(PathFor(fixture.DestinationRoot, BackupRootFileNames.Primary)),
            "The restored root descriptor must retain its exact archived bytes."
        );
    }

    private static void AssertSingleHistoryOccurrence(string root, string relativePath, string expected)
    {
        var historyRoot = PathFor(root, HistoryDirectory);
        var nativeSuffix = Path.DirectorySeparatorChar + relativePath.Replace('/', Path.DirectorySeparatorChar);
        var historyFiles = Directory.EnumerateFiles(historyRoot, "*", SearchOption.AllDirectories);
        var occurrences = historyFiles.Where(path => path.EndsWith(nativeSuffix, StringComparison.Ordinal)).ToArray();
        Assert.HasCount(1, occurrences, relativePath);
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(expected), File.ReadAllBytes(occurrences[0]), relativePath);
    }

    private static string PathFor(string root, string relativePath) => CrashRecoveryFixture.PathFor(root, relativePath);

    private static void ValidateScenario(string scenario)
    {
        if (scenario is not ("restore-history" or "restore-commit" or "restore-finalization"))
        {
            throw new ArgumentException("Unknown restore crash scenario.", nameof(scenario));
        }
    }
}
