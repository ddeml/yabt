using System.Diagnostics;
using System.Text;
using Microsoft.Data.Sqlite;
using Yabt.Common;
using Yabt.Sync.Implementation;

namespace Yabt.Sync.Tests;

[TestClass]
public sealed class OperationProgressJournalTests
{
    [TestMethod]
    public void CompletionAndImmediateFrontierSurviveReopening()
    {
        using var directory = new ProgressTestDirectory();
        using (var journal = directory.Open())
        {
            Assert.IsFalse(journal.IsResuming);
            Assert.IsTrue(journal.HasUnfinishedSubtree(""));
            journal.CompleteUnit("", ["alpha", "alphabet"]);
            journal.CompleteUnit("alpha", ["alpha/child"]);
            journal.CompleteUnit("alphabet", []);
        }

        using var reopened = directory.Open();
        Assert.IsTrue(reopened.IsResuming);
        Assert.AreEqual(3L, reopened.CompletedUnitCount);
        Assert.IsTrue(reopened.WasCompleted("alpha"));
        Assert.IsFalse(reopened.WasCompleted("alpha/child"));
        Assert.IsTrue(reopened.HasUnfinishedSubtree("alpha"));
        Assert.IsFalse(reopened.HasUnfinishedSubtree("alphabet"));
        Assert.IsTrue(reopened.HasUnfinishedSubtree("unknown"));
        reopened.CompleteUnit("alpha/child", []);
        Assert.IsFalse(reopened.HasUnfinishedSubtree("alpha"));
        Assert.IsFalse(reopened.HasUnfinishedSubtree(""));
    }

    [TestMethod]
    public void RestartedUnitBecomesPendingWithoutForgettingOtherCompletedUnits()
    {
        using var directory = new ProgressTestDirectory();
        using (var journal = directory.Open())
        {
            journal.CompleteUnit("", ["one", "two"]);
            journal.CompleteUnit("one", []);
            journal.CompleteUnit("two", []);
            journal.StartUnit("one");
            journal.StartUnit("one");
            Assert.AreEqual(2L, journal.CompletedUnitCount);
        }

        using var reopened = directory.Open();
        Assert.IsFalse(reopened.WasCompleted("one"));
        Assert.IsTrue(reopened.HasUnfinishedSubtree("one"));
        Assert.IsTrue(reopened.WasCompleted("two"));
        reopened.CompleteUnit("one", []);
        reopened.CompleteUnit("one", []);
        Assert.AreEqual(3L, reopened.CompletedUnitCount);
    }

    [TestMethod]
    public void RepeatedParentDiscoveryDoesNotMakeCompletedChildrenPending()
    {
        using var directory = new ProgressTestDirectory();
        using var journal = directory.Open();
        journal.CompleteUnit("", ["child"]);
        journal.CompleteUnit("child", []);
        journal.StartUnit("");
        journal.CompleteUnit("", ["child", "new"]);
        Assert.IsTrue(journal.WasCompleted("child"));
        Assert.IsFalse(journal.WasCompleted("new"));
        Assert.AreEqual(2L, journal.CompletedUnitCount);
    }

    [TestMethod]
    public void FailedChildEnumerationRollsBackTheWholeUnit()
    {
        using var directory = new ProgressTestDirectory();
        using (var journal = directory.Open())
        {
            Assert.ThrowsExactly<IOException>(() => journal.CompleteUnit("", FailingChildren()));
            Assert.IsFalse(journal.WasCompleted(""));
            Assert.AreEqual(0L, journal.CompletedUnitCount);
            journal.Flush();
        }

        using var connection = directory.OpenReader();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM units;";
        Assert.AreEqual(1L, command.ExecuteScalar());

        static IEnumerable<string> FailingChildren()
        {
            yield return "first";
            throw new IOException("An incomplete folder listing cannot be checkpointed.");
        }
    }

    [TestMethod]
    [DataRow("../escape")]
    [DataRow("child/deeper")]
    [DataRow("/rooted")]
    [DataRow("")]
    public void InvalidChildPathsNeverProduceCompletedEvidence(string child)
    {
        using var directory = new ProgressTestDirectory();
        using var journal = directory.Open();
        Assert.ThrowsExactly<ArgumentException>(() => journal.CompleteUnit("", [child]));
        Assert.IsFalse(journal.WasCompleted(""));
        Assert.AreEqual(0L, journal.CompletedUnitCount);
    }

    [TestMethod]
    public void BatchesAreDurableOnlyAtTheConfiguredBoundary()
    {
        using var directory = new ProgressTestDirectory();
        using var journal = directory.Open(batchSize: 2);
        journal.CompleteUnit("", ["child"]);
        Assert.AreEqual(0L, directory.ReadCompletedCount());
        journal.CompleteUnit("child", []);
        Assert.AreEqual(2L, directory.ReadCompletedCount());
        journal.StartUnit("child");
        Assert.AreEqual(2L, directory.ReadCompletedCount());
        journal.Flush();
        Assert.AreEqual(1L, directory.ReadCompletedCount());
    }

    [TestMethod]
    public void ElapsedTimeFlushesTheNextUnitBoundary()
    {
        using var directory = new ProgressTestDirectory();
        var clock = new ProgressTestTimeProvider();
        using var journal = directory.Open(timeProvider: clock);
        journal.CompleteUnit("", ["child"]);
        Assert.AreEqual(0L, directory.ReadCompletedCount());
        clock.Advance(TimeSpan.FromSeconds(6));
        journal.StartUnit("child");
        Assert.AreEqual(1L, directory.ReadCompletedCount());
    }

    [TestMethod]
    public void CanonicalNamesRemainOrdinalIncludingLoneSurrogatesAndWhitespace()
    {
        using var directory = new ProgressTestDirectory();
        string[] names = ["Case", "case", "é", "e\u0301", " ", "\uD800", "\uD801"];
        using var journal = directory.Open();
        journal.CompleteUnit("", names);
        foreach (var name in names)
        {
            journal.CompleteUnit(name, []);
            Assert.IsTrue(journal.WasCompleted(name));
            Assert.IsFalse(journal.HasUnfinishedSubtree(name));
        }
        Assert.AreEqual(1L + names.Length, journal.CompletedUnitCount);
    }

    [TestMethod]
    public void QueriesUseOneIndexedProbeRegardlessOfSiblingCount()
    {
        using var directory = new ProgressTestDirectory();
        using var journal = directory.Open(batchSize: 2048);
        var children = Enumerable.Range(0, 1000).Select(index => $"folder-{index:D4}");
        journal.CompleteUnit("", children);
        foreach (var child in children) { journal.CompleteUnit(child, []); }
        var before = journal.QueryCount;
        foreach (var child in children) { Assert.IsFalse(journal.HasUnfinishedSubtree(child)); }
        Assert.AreEqual(1000L, journal.QueryCount - before);
        journal.Flush();

        using var connection = directory.OpenReader();
        using var command = connection.CreateCommand();
        command.CommandText = """
            EXPLAIN QUERY PLAN SELECT 1 FROM units
            WHERE completed = 0 AND path >= '' AND path < 'G';
            """;
        using var reader = command.ExecuteReader();
        Assert.IsTrue(reader.Read());
        StringAssert.Contains(reader.GetString(3), "SEARCH units USING COVERING INDEX pending_units");
    }

    [TestMethod]
    public void OperationIdentityIsHashedAndDifferentOperationsAreIsolated()
    {
        using var directory = new ProgressTestDirectory();
        const string identity = "backup credential-looking-secret / source";
        var path = OperationProgressJournal.GetDatabasePath(directory.Path, identity);
        Assert.IsFalse(path.Contains("credential", StringComparison.Ordinal));
        using (var first = OperationProgressJournal.Open(directory.Path, identity, TimeProvider.System))
        {
            first.CompleteUnit("", []);
        }
        using var second = directory.Open();
        Assert.IsFalse(second.IsResuming);
        Assert.IsFalse(second.WasCompleted(""));
    }

    [TestMethod]
    public void ConcurrentOpenerCannotMutateTheSameProgress()
    {
        using var directory = new ProgressTestDirectory();
        using var journal = directory.Open();
        Assert.ThrowsExactly<IOException>(() => directory.Open());
        journal.CompleteUnit("", []);
        journal.Flush();
        Assert.AreEqual(1L, directory.ReadCompletedCount());
    }

    [TestMethod]
    public void UnsupportedSchemaIsRefusedForCallerToDiscard()
    {
        using var directory = new ProgressTestDirectory();
        using (var journal = directory.Open()) { journal.CompleteUnit("", []); }
        using (var connection = directory.OpenConnection(readOnly: false))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA user_version = 999;";
            command.ExecuteNonQuery();
        }
        Assert.ThrowsExactly<InvalidDataException>(() => directory.Open());
    }

    [TestMethod]
    public void CorruptDatabaseIsRefusedWithoutDeletingOtherFiles()
    {
        using var directory = new ProgressTestDirectory();
        File.WriteAllText(directory.DatabasePath, "This is not a SQLite database.");
        Assert.ThrowsExactly<SqliteException>(() => directory.Open());
        Assert.AreEqual("This is not a SQLite database.", File.ReadAllText(directory.DatabasePath));
    }

    [TestMethod]
    public void SuccessfulOperationDeletesOnlyItsDatabaseAndRetainsTheLeaseEntry()
    {
        using var directory = new ProgressTestDirectory();
        using (var journal = directory.Open())
        {
            journal.CompleteUnit("", ["child"]);
            journal.CompleteOperation();
        }
        Assert.IsFalse(File.Exists(directory.DatabasePath));
        Assert.IsFalse(File.Exists(directory.DatabasePath + "-journal"));
        Assert.IsTrue(File.Exists(directory.DatabasePath + ".lock"));
        using var reopened = directory.Open();
        Assert.IsFalse(reopened.IsResuming);
    }

    [TestMethod]
    public async Task AbruptProcessExitRollsBackUnflushedProgress()
    {
        const string childDirectoryVariable = "YABT_PROGRESS_CRASH_TEST_DIRECTORY";
        var childDirectory = Environment.GetEnvironmentVariable(childDirectoryVariable);
        if (childDirectory is not null)
        {
            using var childJournal = OperationProgressJournal.Open
            (
                childDirectory, "test-operation", TimeProvider.System,
                int.MaxValue, TimeSpan.FromHours(1)
            );
            childJournal.StartUnit("committed");
            childJournal.CompleteUnit
            (
                "unfinished", Enumerable.Range(0, 10000).Select(index => $"unfinished/child-{index:D5}")
            );
            File.WriteAllText(System.IO.Path.Combine(childDirectory, "crash-ready"), "ready");
            // Exit bypasses managed disposal and leaves the SQLite transaction
            // uncommitted, as abrupt process termination would.
            Environment.Exit(73);
            throw new InvalidOperationException("The crash-test child must not continue.");
        }

        using var directory = new ProgressTestDirectory();
        using (var journal = directory.Open(batchSize: 1))
        {
            journal.CompleteUnit("", ["committed", "unfinished"]);
            journal.CompleteUnit("committed", []);
        }
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("vstest");
        start.ArgumentList.Add(typeof(OperationProgressJournalTests).Assembly.Location);
        start.ArgumentList.Add
        (
            "/TestCaseFilter:FullyQualifiedName=" +
                "Yabt.Sync.Tests.OperationProgressJournalTests.AbruptProcessExitRollsBackUnflushedProgress"
        );
        start.Environment[childDirectoryVariable] = directory.Path;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The crash-test child did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        var childOutput = await output + await error;
        Assert.AreNotEqual(0, process.ExitCode, childOutput);
        Assert.IsTrue(File.Exists(System.IO.Path.Combine(directory.Path, "crash-ready")), childOutput);
        using var reopened = directory.Open();
        Assert.IsTrue(reopened.WasCompleted("committed"));
        Assert.IsFalse(reopened.WasCompleted("unfinished"));
        Assert.AreEqual(2L, reopened.CompletedUnitCount);
        using var connection = directory.OpenReader();
        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM units;";
        Assert.AreEqual(3L, count.ExecuteScalar());
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(".lock")]
    [DataRow("-journal")]
    [DataRow("-wal")]
    [DataRow("-shm")]
    public async Task SharedProgressFilesAreRefusedBeforeWritableUse(string suffix)
    {
        using var directory = new ProgressTestDirectory();
        var sharedPath = directory.DatabasePath + suffix;
        var externalPath = System.IO.Path.Combine(directory.Path, "external.bin");
        byte[] content = [1, 2, 3, 4, 5];
        await File.WriteAllBytesAsync(externalPath, content);
        await directory.CreateHardLinkAsync(sharedPath, externalPath);

        Assert.ThrowsExactly<YabtException>(() => directory.Open());
        CollectionAssert.AreEqual(content, await File.ReadAllBytesAsync(externalPath));
        CollectionAssert.AreEqual(content, await File.ReadAllBytesAsync(sharedPath));
    }

    [TestMethod]
    public async Task CompletionRefusesToDeleteADatabaseThatBecameShared()
    {
        using var directory = new ProgressTestDirectory();
        using var journal = directory.Open();
        journal.CompleteUnit("", []);
        journal.Flush();
        var externalPath = System.IO.Path.Combine(directory.Path, "external.sqlite");
        await directory.CreateHardLinkAsync(externalPath, directory.DatabasePath);
        byte[] original;
        await using (var content = new FileStream
        (
            externalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete
        ))
        {
            original = new byte[content.Length];
            await content.ReadExactlyAsync(original);
        }

        Assert.ThrowsExactly<YabtException>(journal.CompleteOperation);
        Assert.IsTrue(File.Exists(directory.DatabasePath));
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(externalPath));
    }

    private sealed class ProgressTestDirectory : IDisposable
    {
        private const string Identity = "test-operation";
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("yabt-progress-tests-");

        public string Path => _directory.FullName;

        public string DatabasePath => OperationProgressJournal.GetDatabasePath(Path, Identity);

        public OperationProgressJournal Open(int batchSize = 32, TimeProvider? timeProvider = null) =>
            OperationProgressJournal.Open(Path, Identity, timeProvider ?? TimeProvider.System, batchSize);

        public SqliteConnection OpenReader() => OpenConnection(readOnly: true);

        public SqliteConnection OpenConnection(bool readOnly)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
                Pooling = false,
                DefaultTimeout = 1,
            }.ToString());
            connection.Open();
            return connection;
        }

        public long ReadCompletedCount()
        {
            using var connection = OpenReader();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT completed_units FROM operation WHERE singleton = 1;";
            return (long)command.ExecuteScalar()!;
        }

        public async Task CreateHardLinkAsync(string linkPath, string existingPath)
        {
            Assert.IsTrue(linkPath.StartsWith(Path + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal));
            Assert.IsTrue(existingPath.StartsWith(Path + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal));
            var start = new ProcessStartInfo
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            if (OperatingSystem.IsWindows())
            {
                var script = $"New-Item -ItemType HardLink -Path '{linkPath.Replace("'", "''")}' " +
                    $"-Value '{existingPath.Replace("'", "''")}' -ErrorAction Stop | Out-Null";
                start.FileName = "powershell.exe";
                start.ArgumentList.Add("-NoProfile");
                start.ArgumentList.Add("-NonInteractive");
                start.ArgumentList.Add("-EncodedCommand");
                start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
            }
            else
            {
                start.FileName = "ln";
                start.ArgumentList.Add(existingPath);
                start.ArgumentList.Add(linkPath);
            }
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not create a hard link.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.AreEqual(0, process.ExitCode, $"{await output} {await error}");
        }

        public void Dispose() => _directory.Delete(recursive: true);
    }

    private sealed class ProgressTestTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
    }
}
