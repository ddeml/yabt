namespace Yabt.FileSystem;

public sealed class FileSystemObjectStoreOptions
{
    public string? RootPath { get; init; }

    public int? BufferSize { get; init; }

    public int? ListChunkSize { get; init; }

}
