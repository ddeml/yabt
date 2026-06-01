using System.IO.Compression;

namespace Yabt.Format.Zip;

public sealed class ZipArchiveFormatOptions
{
    public CompressionLevel? CompressionLevel { get; init; }

    public int? HashBufferSize { get; init; }
}
