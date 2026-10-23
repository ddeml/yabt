namespace Yabt.Profiling.Implementation;

internal sealed record ProfilingFixture
(
    string RootPath,
    int FileCount,
    string Shape,
    string Format
)
{
    public string SourceRoot => Path.Combine(RootPath, "source");

    public string ArchiveRoot => Path.Combine(RootPath, "archive");

    public string ScratchRoot => Path.Combine(RootPath, "scratch");

    public string DestinationsRoot => Path.Combine(RootPath, "destinations");
}
