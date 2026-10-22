using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yabt.Common;
using Yabt.Common.Async;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;

namespace Yabt.FileSystem.Implementation;

internal sealed class FileSystemObjectStore
(
    IOptionsMonitor<FileSystemObjectStoreOptions> _options,
    ILogger<FileSystemObjectStore> _logger
) : IArchiveMutableObjectStore, ISourceDirectoryTraversal, ILocalObjectStore
{
    private const int DefaultBufferSize = 81_920;
    private const int DefaultListChunkSize = 1_000;
    private FileSystemSourceTraversal? _sourceTraversal;

    public string RootPath => GetRootPath();

    public IObjectStoreNamespace Namespace => new FileSystemObjectStoreNamespace(GetRootPath());

    public void ConfigureSourceTraversal
    (
        Func<string, CancellationToken, Task<FolderPolicy>> readFolderPolicyAsync,
        IEnumerable<string>? forbiddenPhysicalRootPaths = null
    )
    {
        _logger.LogTrace(nameof(ConfigureSourceTraversal));
        Check.NotNull(readFolderPolicyAsync);
        var traversal = new FileSystemSourceTraversal(readFolderPolicyAsync, forbiddenPhysicalRootPaths);
        if (Interlocked.CompareExchange(ref _sourceTraversal, traversal, null) is not null)
        {
            throw new YabtFileSystemException("Source directory traversal can be configured only once per store.");
        }
    }

    public async Task EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogTrace(nameof(EnsureReadyAsync));

        var rootPath = GetRootPath();
        try
        {
            await ValidateReadPathAsync(rootPath, rootPath, cancellationToken);
            _logger.LogFileSystemFolderList(string.Empty, rootPath, recursive: false);
            await YabtTask.Run
            (
                () =>
                {
                    using var entries = Directory.EnumerateFileSystemEntries(rootPath).GetEnumerator();
                    _ = entries.MoveNext();
                },
                ex => _logger.LogAbandonedFileSystemOperationFailed
                (
                    ex,
                    nameof(Directory.EnumerateFileSystemEntries),
                    rootPath
                ),
                cancellationToken
            );
        }
        catch (Exception ex)
        {
            throw new YabtFileSystemException
            (
                $"Filesystem object-store root '{rootPath}' could not be read; it must not be treated as empty.",
                path: rootPath,
                innerException: ex
            );
        }
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogTrace(nameof(InitializeAsync));

        var rootPath = GetRootPath();
        _logger.LogFileSystemStoreReady(rootPath);
        return FileSystemMutation.RunAsync
        (
            () =>
            {
                Directory.CreateDirectory(rootPath);
            },
            cancellationToken);
    }

    public async Task UploadAsync
    (
        string key,
        Stream content,
        string contentType,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(UploadAsync));

        var normalizedKey = NormalizeObjectKey(key);
        var rootPath = GetRootPath();
        var destinationPath = GetObjectPath(rootPath, normalizedKey);
        _logger.LogFileSystemObjectUpload(normalizedKey, destinationPath);
        var destinationDirectory = Path.GetDirectoryName(destinationPath) ??
            throw new YabtFileSystemException
            (
                "Filesystem object path did not include a directory.",
                normalizedKey,
                destinationPath
            );
        var temporaryDirectory = GetTemporaryDirectory(rootPath);
        var temporaryPath = Path.Combine
        (
            temporaryDirectory,
            $"{Guid.NewGuid():N}.tmp"
        );
        var bufferSize = GetEffectiveBufferSize();
        try
        {
            _logger.LogFileSystemPlumbingOperation(
                "Creating temporary upload file",
                temporaryPath);
            await using (var destination = await FileSystemMutation.RunAsync
            (
                () =>
                {
                    ValidateMutationPath(rootPath, destinationPath);
                    ValidateMutationPath(rootPath, temporaryPath);
                    Directory.CreateDirectory(destinationDirectory);
                    Directory.CreateDirectory(temporaryDirectory);
                    ValidateMutationPath(rootPath, destinationPath);
                    ValidateMutationPath(rootPath, temporaryPath);
                    return new FileStream
                    (
                        temporaryPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        bufferSize,
                        FileOptions.Asynchronous | FileOptions.SequentialScan
                    );
                },
                cancellationToken
            ))
            {
                await content.CopyToAsync(destination, cancellationToken);
            }
            _logger.LogFileSystemPlumbingOperation(
                "Finished writing temporary upload file",
                temporaryPath);
            await FileSystemMutation.RunAsync
            (
                () =>
                {
                    ValidateMutationPath(rootPath, temporaryPath);
                    ValidateMutationPath(rootPath, destinationPath);
                    File.Move(temporaryPath, destinationPath);
                },
                cancellationToken
            );
            _logger.LogFileSystemPlumbingOperation(
                "Moved completed temporary upload file into place",
                temporaryPath);
        }
        catch (Exception ex)
        {
            throw new YabtFileSystemException
            (
                $"Upload failed for filesystem object '{normalizedKey}'.",
                normalizedKey,
                destinationPath,
                ex
            );
        }
        finally
        {
            await FileSystemMutation.RunAsync(() => TryDeleteFile(temporaryPath));
        }
    }

    public async Task<ArchiveObjectContent> OpenReadAsync
    (
        string key,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(OpenReadAsync));

        var normalizedKey = NormalizeObjectKey(key);
        try
        {
            var rootPath = GetRootPath();
            var path = await GetReadObjectPathAsync(rootPath, normalizedKey, cancellationToken);
            _logger.LogFileSystemObjectRead(normalizedKey, path);
            var bufferSize = GetEffectiveBufferSize();
            var stream = await YabtTask.Run<FileStream>
            (
                async () =>
                {
                    await ValidateReadPathAsync(rootPath, path, cancellationToken);
                    return new FileStream
                    (
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        bufferSize,
                        FileOptions.Asynchronous | FileOptions.SequentialScan
                    );
                },
                abandonedStream => abandonedStream.Dispose(),
                ex => _logger.LogAbandonedFileSystemOperationFailed
                (
                    ex,
                    nameof(FileStream),
                    path
                ),
                cancellationToken
            );
            return new(stream);
        }
        catch (Exception ex)
        {
            throw new YabtFileSystemException(
                $"Open read failed for filesystem object '{normalizedKey}'.",
                normalizedKey,
                innerException: ex);
        }
    }

    public async Task<bool> IsIndependentMaterializationAsync
    (
        string key,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(IsIndependentMaterializationAsync));

        var normalizedKey = NormalizeObjectKey(key);
        var rootPath = GetRootPath();
        var path = GetObjectPath(rootPath, normalizedKey);
        try
        {
            return await YabtTask.Run
            (
                () =>
                {
                    ValidateMutationPath(rootPath, path);
                    using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    return FileSystemFileIdentity.Read(handle, path).LinkCount == 1;
                },
                abandonedExceptionHandler: ex => _logger.LogAbandonedFileSystemOperationFailed
                (
                    ex, "Inspect object materialization independence", path
                ),
                cancellationToken: cancellationToken
            );
        }
        catch (Exception ex)
        {
            throw new YabtFileSystemException
            (
                $"Materialization independence could not be verified for filesystem object '{normalizedKey}'.",
                normalizedKey,
                path,
                ex
            );
        }
    }

    public async Task<bool> TryReplaceIfContentHashMatchesAsync
    (
        string key,
        string expectedContentHash,
        Stream replacementContent,
        string contentType,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(TryReplaceIfContentHashMatchesAsync));

        ArgumentNullException.ThrowIfNull(replacementContent);
        ArgumentNullException.ThrowIfNull(metadata);
        ValidateExpectedContentHash(expectedContentHash);
        _ = contentType;

        var normalizedKey = NormalizeObjectKey(key);
        var rootPath = GetRootPath();
        var destinationPath = GetObjectPath(rootPath, normalizedKey);
        _logger.LogFileSystemObjectConditionalReplace(normalizedKey, destinationPath);
        var temporaryDirectory = GetTemporaryDirectory(rootPath);
        var temporaryPath = Path.Combine(
            temporaryDirectory,
            $"{Guid.NewGuid():N}.tmp");
        var bufferSize = GetEffectiveBufferSize();

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateMutationPath(rootPath, temporaryPath);
            Directory.CreateDirectory(temporaryDirectory);
            ValidateMutationPath(rootPath, temporaryPath);
            _logger.LogFileSystemPlumbingOperation(
                "Creating temporary conditional-replacement file",
                temporaryPath);
            await using (var temporaryContent = new FileStream
            (
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan
            ))
            {
                await replacementContent.CopyToAsync(temporaryContent, cancellationToken);
            }
            _logger.LogFileSystemPlumbingOperation(
                "Finished writing temporary conditional-replacement file",
                temporaryPath);

            return await RunConditionalMutationAsync
            (
                rootPath,
                destinationPath,
                expectedContentHash,
                () =>
                {
                    ValidateMutationPath(rootPath, temporaryPath);
                    ValidateMutationPath(rootPath, destinationPath);
                    File.Move(temporaryPath, destinationPath, overwrite: true);
                },
                cancellationToken
            );
        }
        catch (Exception ex)
        {
            throw new YabtFileSystemException
            (
                $"Conditional replacement failed for filesystem object '{normalizedKey}'.",
                normalizedKey,
                destinationPath,
                ex
            );
        }
        finally
        {
            await FileSystemMutation.RunAsync(() => TryDeleteFile(temporaryPath));
        }
    }

    public async Task<bool> TryDeleteIfContentHashMatchesAsync
    (
        string key,
        string expectedContentHash,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(TryDeleteIfContentHashMatchesAsync));

        ValidateExpectedContentHash(expectedContentHash);

        var normalizedKey = NormalizeObjectKey(key);
        var rootPath = GetRootPath();
        var path = GetObjectPath(rootPath, normalizedKey);
        _logger.LogFileSystemObjectConditionalDelete(normalizedKey, path);
        try
        {
            return await RunConditionalMutationAsync
            (
                rootPath,
                path,
                expectedContentHash,
                () => File.Delete(path),
                cancellationToken
            );
        }
        catch (Exception ex)
        {
            throw new YabtFileSystemException
            (
                $"Conditional deletion failed for filesystem object '{normalizedKey}'.",
                normalizedKey,
                path,
                ex
            );
        }
    }

    public Task<IArchiveMutationLock> AcquireArchiveMutationLockAsync
    (
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(AcquireArchiveMutationLockAsync));

        var lockPath = GetObjectPath(GetRootPath(), ArchiveInternalObjectKeys.MutationLock);
        _logger.LogFileSystemArchiveMutationLockAcquire(lockPath);
        return FileSystemArchiveMutationLock.AcquireAsync(
            GetRootPath(),
            lockPath,
            _logger,
            cancellationToken);
    }

    public async Task<bool> ExistsAsync
    (
        string key,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(ExistsAsync));
        var normalizedKey = NormalizeObjectKey(key);
        var path = await GetReadObjectPathAsync(GetRootPath(), normalizedKey, cancellationToken);
        _logger.LogFileSystemObjectExists(normalizedKey, path);
        return await YabtTask.Run(() => File.Exists(path), cancellationToken: cancellationToken);
    }

    public async IAsyncEnumerable<ArchiveFolderItem> GetFolderItemsAsync
    (
        string? folderPrefix,
        bool recursive = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(GetFolderItemsAsync));

        var chunkSize = GetEffectiveListChunkSize();
        var rootPath = GetRootPath();
        var normalizedPrefix = NormalizeObjectPrefix(folderPrefix);
        var listRootPath = string.IsNullOrEmpty(normalizedPrefix) ?
            rootPath :
            await GetReadObjectPathAsync(rootPath, normalizedPrefix, cancellationToken);
        _logger.LogFileSystemFolderList(normalizedPrefix, listRootPath, recursive);
        if (_sourceTraversal is not null)
        {
            var sourceItems = EnumerateSourceFolderItemsAsync(rootPath, listRootPath, recursive, cancellationToken);
            await foreach (var item in sourceItems)
            {
                yield return item;
            }
            yield break;
        }
        var items = new List<ArchiveFolderItem>(chunkSize);
        var enumerator = default(IEnumerator<ArchiveFolderItem>);
        void ReadChunk()
        {
            items.Clear();
            while (items.Count < chunkSize && enumerator!.MoveNext())
            {
                items.Add(enumerator.Current);
            }
        }
        try
        {
            await YabtTask.Run
            (
                () =>
                {
                    ValidateMutationPath(rootPath, listRootPath);
                    if (!Directory.Exists(listRootPath)) { return; }
                    var folderItems = EnumerateFolderItems(
                        rootPath,
                        listRootPath,
                        recursive);
                    enumerator = folderItems.GetEnumerator();
                    ReadChunk();
                },
                ex => _logger.LogAbandonedFileSystemOperationFailed
                (
                    ex,
                    "Initialize folder item list enumerator",
                    listRootPath
                ),
                cancellationToken
            );
            while (items.Count > 0)
            {
                foreach (var item in items) { yield return item; }
                await YabtTask.Run
                (
                    ReadChunk,
                    ex => _logger.LogIgnoringAbandonedListChunkException(ex, listRootPath),
                    cancellationToken
                );
            }
        }
        finally
        {
            if (enumerator is not null)
            {
                _ = YabtTask.Run
                (
                    () =>
                    {
                        try
                        {
                            enumerator.Dispose();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogIgnoringListEnumeratorDisposeException(ex, listRootPath);
                        }
                    },
                    cancellationToken: default
                );
            }
        }
    }

    public Task MoveAsync
    (
        string source,
        string destination,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(MoveAsync));

        var normalizedSource = NormalizeObjectKey(source);
        var normalizedDestination = NormalizeObjectKey(destination);
        var rootPath = GetRootPath();
        var sourcePath = GetObjectPath(rootPath, normalizedSource);
        var destinationPath = GetObjectPath(rootPath, normalizedDestination);
        _logger.LogFileSystemObjectMove(normalizedSource, normalizedDestination);
        var destinationDirectory = Path.GetDirectoryName(destinationPath)
            ?? throw new YabtFileSystemException(
                "Filesystem object path did not include a directory.",
                normalizedDestination,
                destinationPath);

        return FileSystemMutation.RunAsync
        (
            () =>
            {
                ValidateMutationPath(rootPath, sourcePath);
                ValidateMutationPath(rootPath, destinationPath);
                Directory.CreateDirectory(destinationDirectory);
                ValidateMutationPath(rootPath, destinationPath);
                File.Move(sourcePath, destinationPath);
            },
            cancellationToken
        );
    }

    public async Task MoveFolderAsync
    (
        string sourcePrefix,
        string destinationPrefix,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(MoveFolderAsync));

        var normalizedSourcePrefix = NormalizeObjectKey(sourcePrefix);
        var normalizedDestinationPrefix = NormalizeObjectKey(destinationPrefix);
        if (ArchiveLayout.IsUnderPrefix(normalizedDestinationPrefix, normalizedSourcePrefix))
        {
            throw new YabtFileSystemException(
                "Filesystem folder destination must not be the source folder or one of its descendants.",
                normalizedDestinationPrefix);
        }

        var rootPath = GetRootPath();
        var sourcePath = GetObjectPath(rootPath, normalizedSourcePrefix);
        var destinationPath = GetObjectPath(rootPath, normalizedDestinationPrefix);
        _logger.LogFileSystemFolderMove(
            normalizedSourcePrefix,
            normalizedDestinationPrefix);
        var destinationParent = Path.GetDirectoryName(destinationPath)
            ?? throw new YabtFileSystemException(
                "Filesystem folder path did not include a parent directory.",
                normalizedDestinationPrefix,
                destinationPath);

        try
        {
            await FileSystemMutation.RunAsync
            (
                () =>
                {
                    ValidateMutationPath(rootPath, sourcePath);
                    ValidateMutationPath(rootPath, destinationPath);
                    Directory.CreateDirectory(destinationParent);
                    ValidateMutationPath(rootPath, destinationPath);
                    Directory.Move(sourcePath, destinationPath);
                },
                cancellationToken
            );
        }
        catch (Exception ex)
        {
            throw new YabtFileSystemException(
                $"Move failed for filesystem folder '{normalizedSourcePrefix}' to '{normalizedDestinationPrefix}'.",
                normalizedSourcePrefix,
                sourcePath,
                ex);
        }
    }

    private async Task<string> GetReadObjectPathAsync
    (
        string rootPath,
        string key,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(GetReadObjectPathAsync));
        var path = ResolveObjectPath(rootPath, key);
        await ValidateReadPathAsync(rootPath, path, cancellationToken);
        return path;
    }

    private async Task ValidateReadPathAsync
    (
        string rootPath,
        string path,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(ValidateReadPathAsync));
        if (_sourceTraversal is not null)
        {
            await _sourceTraversal.ValidatePathAsync(rootPath, path, cancellationToken);
            ValidatePathNamespace(rootPath, path);
            return;
        }
        ValidateMutationPath(rootPath, path);
    }

    private async IAsyncEnumerable<ArchiveFolderItem> EnumerateSourceFolderItemsAsync
    (
        string rootPath,
        string directoryPath,
        bool recursive,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(EnumerateSourceFolderItemsAsync));
        await ValidateReadPathAsync(rootPath, directoryPath, cancellationToken);
        var childDirectories = await ReadSourceEntryPathsAsync(directoryPath, folders: true, cancellationToken);
        Array.Sort(childDirectories, StringComparer.Ordinal);
        foreach (var childDirectory in childDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileSystemObjectName.Validate(Path.GetFileName(childDirectory));
            var childKey = ToArchiveRelativePath(Path.GetRelativePath(rootPath, childDirectory));
            await ValidateReadPathAsync(rootPath, childDirectory, cancellationToken);
            if (recursive)
            {
                var descendants = EnumerateSourceFolderItemsAsync(rootPath, childDirectory, true, cancellationToken);
                await foreach (var descendant in descendants)
                {
                    yield return descendant;
                }
            }
            else
            {
                yield return ArchiveFolderItem.CreateFolder(
                    Path.GetFileName(childDirectory),
                    childKey);
            }
        }

        var files = await ReadSourceEntryPathsAsync(directoryPath, folders: false, cancellationToken);
        Array.Sort(files, StringComparer.Ordinal);
        var chunkSize = GetEffectiveListChunkSize();
        for (var offset = 0; offset < files.Length; offset += chunkSize)
        {
            var chunk = await ReadSourceFileChunkAsync
            (
                rootPath,
                files,
                offset,
                Math.Min(chunkSize, files.Length - offset),
                cancellationToken
            );
            foreach (var item in chunk)
            {
                yield return item;
            }
        }
    }

    private async Task<string[]> ReadSourceEntryPathsAsync
    (
        string directoryPath,
        bool folders,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(ReadSourceEntryPathsAsync));
        try
        {
            return await YabtTask.Run
            (
                () => folders ? Directory.GetDirectories(directoryPath) : Directory.GetFiles(directoryPath),
                abandonedExceptionHandler: ex => _logger.LogAbandonedFileSystemOperationFailed
                (
                    ex,
                    "List source directory entries",
                    directoryPath
                ),
                cancellationToken: cancellationToken
            );
        }
        catch (Exception ex)
        {
            throw new YabtFileSystemException
            (
                $"Source directory '{directoryPath}' could not be listed; it must not be treated as empty.",
                path: directoryPath,
                innerException: ex
            );
        }
    }

    private Task<List<ArchiveFolderItem>> ReadSourceFileChunkAsync
    (
        string rootPath,
        string[] files,
        int offset,
        int count,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(ReadSourceFileChunkAsync));
        return YabtTask.Run<List<ArchiveFolderItem>>
        (
            async () =>
            {
                var items = new List<ArchiveFolderItem>(count);
                for (var index = offset; index < offset + count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var filePath = files[index];
                    var name = Path.GetFileName(filePath);
                    FileSystemObjectName.Validate(name);
                    await ValidateReadPathAsync(rootPath, filePath, cancellationToken);
                    if (string.Equals(name, ArchiveFolderMarkerFileNames.EmptyFolder, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    try
                    {
                        var info = new FileInfo(filePath);
                        items.Add
                        (
                            ArchiveFolderItem.CreateObject
                            (
                                name,
                                new
                                (
                                    ToArchiveRelativePath(Path.GetRelativePath(rootPath, filePath)),
                                    info.Length,
                                    new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)
                                )
                            )
                        );
                    }
                    catch (Exception ex)
                    {
                        throw new YabtFileSystemException
                        (
                            $"Source file metadata could not be read for '{filePath}'.",
                            path: filePath,
                            innerException: ex
                        );
                    }
                }
                return items;
            },
            abandonedExceptionHandler: ex => _logger.LogAbandonedFileSystemOperationFailed
            (
                ex,
                "Read source file metadata",
                rootPath
            ),
            cancellationToken: cancellationToken
        );
    }

    private string GetRootPath()
    {
        var rootPath = _options.CurrentValue.RootPath;
        if (string.IsNullOrEmpty(rootPath))
        {
            throw new YabtFileSystemException("Filesystem object store requires a root path.");
        }
        return Path.GetFullPath(rootPath);
    }

    private int GetEffectiveBufferSize()
    {
        var bufferSize = _options.CurrentValue.BufferSize ?? DefaultBufferSize;
        if (bufferSize <= 0)
        {
            throw new YabtFileSystemException("Filesystem object store buffer size must be greater than zero.");
        }
        return bufferSize;
    }

    private int GetEffectiveListChunkSize()
    {
        var chunkSize = _options.CurrentValue.ListChunkSize ?? DefaultListChunkSize;
        if (chunkSize <= 0)
        {
            throw new YabtFileSystemException("Filesystem object store list chunk size must be greater than zero.");
        }
        return chunkSize;
    }

    private static string GetObjectPath(string rootPath, string key)
    {
        try
        {
            var path = ResolveObjectPath(rootPath, key);
            ValidateMutationPath(rootPath, path);
            return path;
        }
        catch (Exception ex)
        {
            throw new YabtFileSystemException(
                $"Filesystem object path for '{key}' could not be resolved.",
                key,
                innerException: ex);
        }
    }

    private Task<bool> RunConditionalMutationAsync
    (
        string rootPath,
        string path,
        string expectedContentHash,
        Action mutation,
        CancellationToken cancellationToken
    )
    {
        return FileSystemMutation.RunAsync
        (
            () => RunWithMutationGuard
            (
                rootPath,
                () =>
                {
                    ValidateMutationPath(rootPath, path);
                    if (!CurrentContentHashMatches
                        (
                            path,
                            expectedContentHash,
                            cancellationToken
                        ))
                    {
                        return false;
                    }

                    ValidateMutationPath(rootPath, path);
                    cancellationToken.ThrowIfCancellationRequested();
                    mutation();
                    return true;
                },
                cancellationToken
            ),
            cancellationToken: cancellationToken
        );
    }

    private bool CurrentContentHashMatches
    (
        string path,
        string expectedContentHash,
        CancellationToken cancellationToken
    )
    {
        try
        {
            _logger.LogFileSystemConditionalMutationRead(path);
            using var content = new FileStream
            (
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                GetEffectiveBufferSize(),
                FileOptions.SequentialScan
            );
            var contentHash = ArchiveHash.Compute(content, cancellationToken);
            return string.Equals(
                contentHash,
                expectedContentHash,
                StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            if (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return false;
            }

            throw;
        }
    }

    private T RunWithMutationGuard<T>
    (
        string rootPath,
        Func<T> action,
        CancellationToken cancellationToken
    )
    {
        var guardPath = GetObjectPath(rootPath, ArchiveInternalObjectKeys.ConditionalMutationLock);
        _logger.LogFileSystemPlumbingOperation(
            "Acquiring conditional-mutation guard",
            guardPath);
        Directory.CreateDirectory(Path.GetDirectoryName(guardPath)!);
        ValidateMutationPath(rootPath, guardPath);
        using var guard = AcquireConditionalMutationGuard(guardPath, cancellationToken);
        _logger.LogFileSystemPlumbingOperation(
            "Acquired conditional-mutation guard",
            guardPath);
        return action();
    }

    private FileStream AcquireConditionalMutationGuard
    (
        string guardPath,
        CancellationToken cancellationToken
    )
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var guard = new FileStream
                (
                    guardPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.None
                );
                try
                {
                    FileSystemFileIdentity.RequireIndependent(guard.SafeFileHandle, guardPath);
                    return guard;
                }
                catch
                {
                    guard.Dispose();
                    throw;
                }
            }
            catch (IOException) when (File.Exists(guardPath))
            {
                _logger.LogFileSystemPlumbingOperation(
                    "Found busy conditional-mutation guard",
                    guardPath);
                if (cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(50)))
                {
                    throw new OperationCanceledException(cancellationToken);
                }
            }
        }
    }

    private static string GetTemporaryDirectory(string rootPath) => GetObjectPath
    (
        rootPath,
        ArchiveInternalFolderNames.TemporaryUploads
    );

    private static IEnumerable<ArchiveFolderItem> EnumerateFolderItems
    (
        string rootPath,
        string directoryPath,
        bool recursive
    )
    {
        if (recursive)
        {
            return EnumerateRecursiveObjectItems(
                rootPath,
                directoryPath);
        }

        return EnumerateImmediateFolderItems(
            rootPath,
            directoryPath);
    }

    private static IEnumerable<ArchiveFolderItem> EnumerateImmediateFolderItems
    (
        string rootPath,
        string directoryPath
    )
    {
        ValidateMutationPath(rootPath, directoryPath);
        var childDirectoryPaths = Directory.EnumerateDirectories(directoryPath).
            OrderBy(childDirectoryPath => childDirectoryPath, StringComparer.Ordinal);
        foreach (var childDirectoryPath in childDirectoryPaths)
        {
            FileSystemObjectName.Validate(Path.GetFileName(childDirectoryPath));
            ValidateMutationPath(rootPath, childDirectoryPath);
            var name = Path.GetFileName(childDirectoryPath);
            var key = ToArchiveRelativePath(Path.GetRelativePath(rootPath, childDirectoryPath));
            yield return ArchiveFolderItem.CreateFolder(name, key);
        }

        var filePaths = Directory.EnumerateFiles(directoryPath).
            OrderBy(filePath => filePath, StringComparer.Ordinal);
        foreach (var filePath in filePaths)
        {
            var name = Path.GetFileName(filePath);
            FileSystemObjectName.Validate(name);
            ValidateMutationPath(rootPath, filePath);
            if (string.Equals(
                    name,
                    ArchiveFolderMarkerFileNames.EmptyFolder,
                    StringComparison.Ordinal))
            {
                continue;
            }

            var info = new FileInfo(filePath);
            var key = ToArchiveRelativePath(Path.GetRelativePath(rootPath, filePath));
            yield return ArchiveFolderItem.CreateObject(
                name,
                new
                (
                    key,
                    info.Length,
                    new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)
                ));
        }
    }

    private static IEnumerable<ArchiveFolderItem> EnumerateRecursiveObjectItems
    (
        string rootPath,
        string directoryPath
    )
    {
        ValidateMutationPath(rootPath, directoryPath);
        var childDirectoryPaths = Directory.EnumerateDirectories(directoryPath).
            OrderBy(childDirectoryPath => childDirectoryPath, StringComparer.Ordinal);
        foreach (var childDirectoryPath in childDirectoryPaths)
        {
            FileSystemObjectName.Validate(Path.GetFileName(childDirectoryPath));
            _ = ToArchiveRelativePath(Path.GetRelativePath(rootPath, childDirectoryPath));
            ValidateMutationPath(rootPath, childDirectoryPath);
            var childItems = EnumerateRecursiveObjectItems(
                rootPath,
                childDirectoryPath);

            foreach (var childItem in childItems)
            {
                yield return childItem;
            }
        }

        var fileItems = EnumerateFileItems(
            rootPath,
            directoryPath);
        foreach (var fileItem in fileItems)
        {
            yield return fileItem;
        }
    }

    private static IEnumerable<ArchiveFolderItem> EnumerateFileItems
    (
        string rootPath,
        string directoryPath
    )
    {
        var filePaths = Directory.EnumerateFiles(directoryPath).
            OrderBy(filePath => filePath, StringComparer.Ordinal);
        foreach (var filePath in filePaths)
        {
            var name = Path.GetFileName(filePath);
            FileSystemObjectName.Validate(name);
            ValidateMutationPath(rootPath, filePath);
            if (string.Equals(
                    name,
                    ArchiveFolderMarkerFileNames.EmptyFolder,
                    StringComparison.Ordinal))
            {
                continue;
            }

            var info = new FileInfo(filePath);
            var key = ToArchiveRelativePath(Path.GetRelativePath(rootPath, filePath));
            yield return ArchiveFolderItem.CreateObject(
                name,
                new
                (
                    key,
                    info.Length,
                    new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)
                ));
        }
    }

    private static void ValidateMutationPath(string rootPath, string path)
    {
        FileSystemPathSafety.RejectDescendantLinks(rootPath, path);
        ValidatePathNamespace(rootPath, path);
    }

    private static void ValidatePathNamespace(string rootPath, string path)
    {
        var relativePath = Path.GetRelativePath(rootPath, path);
        var objectPath = relativePath == "." ? ObjectPath.Root : ObjectPath.Parse(ToArchiveRelativePath(relativePath));
        new FileSystemObjectStoreNamespace(rootPath, allowRootTemporaryDirectoryAlias: true).ValidatePath(objectPath);
    }

    private static string ResolveObjectPath(string rootPath, string objectPath)
    {
        var rootedPath = EnsureTrailingDirectorySeparator(Path.GetFullPath(rootPath));
        var relativePath = NormalizeRelativeObjectPath(objectPath);
        var fullPath = Path.GetFullPath(Path.Combine(rootedPath, relativePath));

        if (!fullPath.StartsWith(rootedPath, GetPathComparison()))
        {
            throw new YabtFileSystemException("Filesystem object path resolved outside the archive root.");
        }

        return fullPath;
    }

    private static string NormalizeRelativeObjectPath(string value)
    {
        var normalized = NormalizeObjectKey(value);
        var segments = normalized.Split('/');

        foreach (var segment in segments)
        {
            FileSystemObjectName.Validate(segment);
        }

        return Path.Combine(segments);
    }

    private static string NormalizeObjectKey(string? value)
    {
        var normalized = NormalizeObjectPrefix(value);
        if (string.IsNullOrEmpty(normalized))
        {
            throw new YabtFileSystemException("Object path must not be empty.");
        }
        return normalized;
    }

    private static string NormalizeObjectPrefix(string? value)
    {
        try
        {
            return ArchiveLayout.NormalizeObjectKey(value);
        }
        catch (Exception ex)
        {
            throw new YabtFileSystemException
            (
                $"Filesystem object key '{value}' cannot be represented without changing its identity.",
                key: value,
                innerException: ex
            );
        }
    }

    private static void ValidateExpectedContentHash(string expectedContentHash)
    {
        if (!ArchiveHash.IsValid(expectedContentHash))
        {
            throw new ArgumentException
            (
                "The expected content hash must be a canonical YABT xxHash128 hash.",
                nameof(expectedContentHash)
            );
        }
    }

    private static string ToArchiveRelativePath(string relativePath)
    {
        var segments = relativePath.Split(Path.DirectorySeparatorChar);
        foreach (var segment in segments)
        {
            FileSystemObjectName.Validate(segment);
        }
        return NormalizeObjectKey(string.Join('/', segments));
    }

    private static string EnsureTrailingDirectorySeparator(string path) => Path.EndsInDirectorySeparator(path) ?
        path : $"{path}{Path.DirectorySeparatorChar}";

    private static StringComparison GetPathComparison() => OperatingSystem.IsWindows() ?
        StringComparison.OrdinalIgnoreCase :
        StringComparison.Ordinal;

    private void TryDeleteFile(string path)
    {
        try
        {
            ValidateMutationPath(GetRootPath(), path);
            _logger.LogFileSystemPlumbingOperation(
                "Checking temporary file for cleanup",
                path);
            if (File.Exists(path))
            {
                File.Delete(path);
                _logger.LogFileSystemPlumbingOperation("Deleted temporary file", path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogIgnoringTemporaryObjectDeleteException(ex, path);
        }
    }

}
