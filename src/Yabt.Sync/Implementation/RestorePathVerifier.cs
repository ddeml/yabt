using System.Buffers;
using System.Security;
using System.Text;
using Microsoft.Extensions.Logging;
using Yabt.Core.Models;
using Yabt.Metadata;

namespace Yabt.Sync.Implementation;

internal sealed class RestorePathVerifier
(
    ILogger<RestorePathVerifier> _logger
) : IRestorePathVerifier
{
    private const int BufferSize = 81_920;

    public async Task<RestorePathVerificationResult> VerifyAsync
    (
        RestorePathVerificationRequest request,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(VerifyAsync));

        ArgumentNullException.ThrowIfNull(request);
        try
        {
            return await VerifyCoreAsync(request, cancellationToken);
        }
        catch (YabtSyncException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when
        (IsFileSystemFailure(ex))
        {
            throw new YabtSyncException(
                $"Restore path verification failed while comparing source path " +
                    $"'{request.SourceRoot}' with restore path '{request.RestoreRoot}': " +
                    ex.Message,
                ex);
        }
    }

    private async Task<RestorePathVerificationResult> VerifyCoreAsync
    (
        RestorePathVerificationRequest request,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(request.SourceRoot))
        {
            throw new YabtSyncException("Restore path verification requires a source path.");
        }
        if (string.IsNullOrWhiteSpace(request.RestoreRoot))
        {
            throw new YabtSyncException("Restore path verification requires a restore path.");
        }

        var sourceRoot = Path.GetFullPath(request.SourceRoot);
        var restoreRoot = Path.GetFullPath(request.RestoreRoot);
        EnsureDirectoryExists(sourceRoot, "source");
        EnsureDirectoryExists(restoreRoot, "restore");
        _logger.LogRestorePathVerificationRequested(sourceRoot, restoreRoot);

        var comparison = new ComparisonCounts();
        await CompareDirectoryAsync(
            sourceRoot,
            restoreRoot,
            relativePath: string.Empty,
            isRoot: true,
            comparison,
            cancellationToken);

        var identical = comparison.UncomparedItemCount == 0 &&
            comparison.SourceOnlyCount == 0 &&
            comparison.DifferentCount == 0 &&
            comparison.RestoreOnlyCount == 0;
        var summary = identical ?
            $"Restore path verification completed byte-for-byte; " +
                $"{comparison.ComparedFileCount} file(s) and " +
                $"{comparison.ComparedDirectoryCount} directory path(s) are identical." :
            $"Restore path verification failed; " +
                $"{comparison.SourceOnlyCount} source-only item(s), " +
                $"{comparison.DifferentCount} different item(s), " +
                $"{comparison.RestoreOnlyCount} restore-only item(s), and " +
                $"{comparison.UncomparedItemCount} item(s) could not be compared. " +
                $"Inspected {comparison.ComparedFileCount} matching-path file(s) and " +
                $"{comparison.ComparedDirectoryCount} matching directory path(s).";
        var message = BuildResultMessage(summary, comparison.Failures);
        _logger.LogRestorePathVerificationCompleted(
            identical,
            comparison.SourceOnlyCount,
            comparison.DifferentCount,
            comparison.RestoreOnlyCount,
            comparison.UncomparedItemCount,
            comparison.ComparedFileCount,
            comparison.ComparedDirectoryCount);
        return new(
            identical,
            message,
            comparison.SourceOnlyCount,
            comparison.DifferentCount,
            comparison.RestoreOnlyCount,
            comparison.ComparedFileCount,
            comparison.ComparedDirectoryCount,
            comparison.UncomparedItemCount);
    }

    private async Task CompareDirectoryAsync
    (
        string sourceDirectoryPath,
        string restoreDirectoryPath,
        string relativePath,
        bool isRoot,
        ComparisonCounts comparison,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sourceEntries = EnumerateEntries(
            sourceDirectoryPath,
            relativePath,
            isRoot,
            "source",
            comparison,
            cancellationToken);
        var restoreEntries = EnumerateEntries(
            restoreDirectoryPath,
            relativePath,
            isRoot,
            "restore",
            comparison,
            cancellationToken);
        var names = sourceEntries.Entries.Keys
            .Concat(sourceEntries.UnavailableNames)
            .Concat(restoreEntries.Entries.Keys)
            .Concat(restoreEntries.UnavailableNames)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

        foreach (var name in names)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hasSourceEntry = sourceEntries.Entries.TryGetValue(name, out var sourceEntry);
            var hasRestoreEntry = restoreEntries.Entries.TryGetValue(name, out var restoreEntry);
            if (!hasSourceEntry)
            {
                if (!sourceEntries.Completed || sourceEntries.UnavailableNames.Contains(name))
                {
                    continue;
                }

                comparison.RestoreOnlyCount++;
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogRestorePathDifference(
                        restoreEntry!.RelativePath,
                        $"restore-only {restoreEntry.Kind}");
                }
                continue;
            }
            if (!hasRestoreEntry)
            {
                if (!restoreEntries.Completed || restoreEntries.UnavailableNames.Contains(name))
                {
                    continue;
                }

                comparison.SourceOnlyCount++;
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogRestorePathDifference(
                        sourceEntry!.RelativePath,
                        $"source {sourceEntry.Kind} is missing from the restore path");
                }
                continue;
            }

            if (sourceEntry!.IsDirectory != restoreEntry!.IsDirectory)
            {
                comparison.DifferentCount++;
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogRestorePathDifference(
                        sourceEntry.RelativePath,
                        $"source is a {sourceEntry.Kind} but restore path is a {restoreEntry.Kind}");
                }
                continue;
            }

            if (sourceEntry.IsDirectory)
            {
                comparison.ComparedDirectoryCount++;
                _logger.LogRestorePathItemUnchanged("directory", sourceEntry.RelativePath);
                await CompareDirectoryAsync(
                    sourceEntry.FullPath,
                    restoreEntry.FullPath,
                    sourceEntry.RelativePath,
                    isRoot: false,
                    comparison,
                    cancellationToken);
                continue;
            }

            comparison.ComparedFileCount++;
            var fileComparison = await CompareFilesAsync(
                sourceEntry.FullPath,
                restoreEntry.FullPath,
                cancellationToken);
            foreach (var failure in fileComparison.Failures)
            {
                comparison.AddFailure(sourceEntry.RelativePath, failure, _logger);
            }
            if (fileComparison.Failures.Count > 0)
            {
                continue;
            }

            if (fileComparison.Difference is null)
            {
                _logger.LogRestorePathItemUnchanged("file", sourceEntry.RelativePath);
                continue;
            }

            comparison.DifferentCount++;
            _logger.LogRestorePathDifference(
                sourceEntry.RelativePath,
                fileComparison.Difference);
        }
    }

    private EntryEnumerationResult EnumerateEntries
    (
        string directoryPath,
        string directoryRelativePath,
        bool isRoot,
        string pathRole,
        ComparisonCounts comparison,
        CancellationToken cancellationToken
    )
    {
        var entries = new Dictionary<string, FileSystemEntry>(StringComparer.Ordinal);
        var unavailableNames = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directoryPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(path);
                var relativePath = string.IsNullOrEmpty(directoryRelativePath) ?
                    name :
                    $"{directoryRelativePath}/{name}";
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(path);
                }
                catch (Exception ex) when (IsFileSystemFailure(ex))
                {
                    unavailableNames.Add(name);
                    comparison.AddFailure(
                        relativePath,
                        $"could not inspect {pathRole} item '{path}': {ex.Message}",
                        _logger);
                    continue;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    unavailableNames.Add(name);
                    comparison.AddFailure(
                        relativePath,
                        $"cannot safely compare {pathRole} reparse-point item '{path}'",
                        _logger);
                    continue;
                }

                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                if (isRoot && IsExcludedRootEntry(name, isDirectory)) { continue; }

                var entry = new FileSystemEntry(
                    path,
                    relativePath,
                    isDirectory);
                if (!entries.TryAdd(name, entry))
                {
                    throw new YabtSyncException(
                        $"Filesystem {pathRole} path '{directoryPath}' contains duplicate entry " +
                            $"name '{name}'.");
                }
            }

            return new(entries, unavailableNames, Completed: true);
        }
        catch (YabtSyncException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            var displayRelativePath = string.IsNullOrEmpty(directoryRelativePath) ?
                "." :
                directoryRelativePath;
            comparison.AddFailure(
                displayRelativePath,
                $"could not enumerate {pathRole} directory '{directoryPath}': {ex.Message}",
                _logger);
            return new(entries, unavailableNames, Completed: false);
        }
    }

    private static async Task<FileComparisonResult> CompareFilesAsync
    (
        string sourcePath,
        string restorePath,
        CancellationToken cancellationToken
    )
    {
        var failures = new List<string>();
        FileStream? source = null;
        FileStream? restore = null;
        try
        {
            source = TryOpenRead(
                sourcePath,
                restorePath,
                "source",
                "restore",
                failures);
            restore = TryOpenRead(
                restorePath,
                sourcePath,
                "restore",
                "source",
                failures);
            if (source is null || restore is null)
            {
                return new(null, failures);
            }

            long sourceLength;
            long restoreLength;
            try
            {
                sourceLength = source.Length;
            }
            catch (Exception ex) when (IsFileSystemFailure(ex))
            {
                failures.Add(FormatFileFailure(
                    sourcePath,
                    restorePath,
                    "source",
                    "restore",
                    "inspect",
                    ex));
                sourceLength = default;
            }
            try
            {
                restoreLength = restore.Length;
            }
            catch (Exception ex) when (IsFileSystemFailure(ex))
            {
                failures.Add(FormatFileFailure(
                    restorePath,
                    sourcePath,
                    "restore",
                    "source",
                    "inspect",
                    ex));
                restoreLength = default;
            }
            if (failures.Count > 0)
            {
                return new(null, failures);
            }
            if (sourceLength != restoreLength)
            {
                return new(
                    $"content length differs (source {sourceLength}, " +
                        $"restore {restoreLength})",
                    failures);
            }

            var sourceBuffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            var restoreBuffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                while (true)
                {
                    var sourceRead = await TryReadBlockAsync(
                        source,
                        sourceBuffer,
                        cancellationToken);
                    var restoreRead = await TryReadBlockAsync(
                        restore,
                        restoreBuffer,
                        cancellationToken);
                    if (sourceRead.Failure is not null)
                    {
                        failures.Add(FormatFileFailure(
                            sourcePath,
                            restorePath,
                            "source",
                            "restore",
                            "read",
                            sourceRead.Failure));
                    }
                    if (restoreRead.Failure is not null)
                    {
                        failures.Add(FormatFileFailure(
                            restorePath,
                            sourcePath,
                            "restore",
                            "source",
                            "read",
                            restoreRead.Failure));
                    }
                    if (failures.Count > 0)
                    {
                        return new(null, failures);
                    }
                    var sourceBytesRead = sourceRead.BytesRead;
                    var restoreBytesRead = restoreRead.BytesRead;
                    if (sourceBytesRead != restoreBytesRead ||
                        !sourceBuffer.AsSpan(0, sourceBytesRead).SequenceEqual(
                            restoreBuffer.AsSpan(0, restoreBytesRead)))
                    {
                        return new("file contents differ", failures);
                    }
                    if (sourceBytesRead == 0) { return new(null, failures); }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(sourceBuffer);
                ArrayPool<byte>.Shared.Return(restoreBuffer);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        finally
        {
            if (source is not null) { await source.DisposeAsync(); }
            if (restore is not null) { await restore.DisposeAsync(); }
        }
    }

    private static async Task<FileReadResult> TryReadBlockAsync
    (
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var totalRead = 0;
            while (totalRead < buffer.Length)
            {
                var read = await stream.ReadAsync(
                    buffer.AsMemory(totalRead, buffer.Length - totalRead),
                    cancellationToken);
                if (read == 0) { break; }

                totalRead += read;
            }

            return new(totalRead, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            return new(default, ex);
        }
    }

    private static FileStream? TryOpenRead
    (
        string path,
        string counterpartPath,
        string pathRole,
        string counterpartRole,
        List<string> failures
    )
    {
        try
        {
            return OpenRead(path);
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            failures.Add(FormatFileFailure(
                path,
                counterpartPath,
                pathRole,
                counterpartRole,
                "open",
                ex));
            return null;
        }
    }

    private static string FormatFileFailure
    (
        string path,
        string counterpartPath,
        string pathRole,
        string counterpartRole,
        string operation,
        Exception exception
    ) => $"could not {operation} {pathRole} file '{path}' while comparing it with " +
        $"{counterpartRole} file '{counterpartPath}': {exception.Message}";

    private static string BuildResultMessage
    (
        string summary,
        IReadOnlyList<VerificationFailure> failures
    )
    {
        if (failures.Count == 0) { return summary; }

        var message = new StringBuilder(summary);
        message.AppendLine();
        message.Append("Items that could not be compared:");
        foreach (var failure in failures)
        {
            message.AppendLine();
            message.Append("- ");
            message.Append(failure.RelativePath);
            message.Append(": ");
            message.Append(failure.Reason);
        }

        return message.ToString();
    }

    private static FileStream OpenRead(string path) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        BufferSize,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static bool IsExcludedRootEntry(string name, bool isDirectory) =>
        !isDirectory && string.Equals(
            name,
            ArchiveLogicalStateManifest.FileName,
            StringComparison.OrdinalIgnoreCase) ||
        isDirectory && string.Equals(
            name,
            ArchiveInternalFolderNames.TemporaryUploads,
            StringComparison.OrdinalIgnoreCase);

    private static void EnsureDirectoryExists(string path, string pathRole)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new YabtSyncException(
                $"The {pathRole} path '{path}' does not exist.",
                ex);
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            throw new YabtSyncException(
                $"Could not inspect the {pathRole} path '{path}': {ex.Message}",
                ex);
        }

        if ((attributes & FileAttributes.Directory) == 0)
        {
            throw new YabtSyncException(
                $"The {pathRole} path '{path}' is a file, not a folder.");
        }
    }

    private static bool IsFileSystemFailure(Exception exception) =>
        exception is ArgumentException or
            IOException or
            NotSupportedException or
            SecurityException or
            UnauthorizedAccessException;

    private sealed record FileSystemEntry
    (
        string FullPath,
        string RelativePath,
        bool IsDirectory
    )
    {
        public string Kind => IsDirectory ? "directory" : "file";
    }

    private sealed record EntryEnumerationResult
    (
        Dictionary<string, FileSystemEntry> Entries,
        HashSet<string> UnavailableNames,
        bool Completed
    );

    private sealed record FileComparisonResult
    (
        string? Difference,
        IReadOnlyList<string> Failures
    );

    private sealed record FileReadResult(int BytesRead, Exception? Failure);

    private sealed record VerificationFailure(string RelativePath, string Reason);

    private sealed class ComparisonCounts
    {
        public int SourceOnlyCount { get; set; }

        public int DifferentCount { get; set; }

        public int RestoreOnlyCount { get; set; }

        public int ComparedFileCount { get; set; }

        public int ComparedDirectoryCount { get; set; }

        public int UncomparedItemCount => Failures.Count;

        public List<VerificationFailure> Failures { get; } = [];

        public void AddFailure
        (
            string relativePath,
            string reason,
            ILogger logger
        )
        {
            Failures.Add(new(relativePath, reason));
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogRestorePathDifference(
                    relativePath,
                    $"could not be compared: {reason}");
            }
        }
    }
}
