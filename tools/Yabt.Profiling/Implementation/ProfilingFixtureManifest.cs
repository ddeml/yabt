namespace Yabt.Profiling.Implementation;

internal sealed record ProfilingFixtureManifest
(
    string DocumentType,
    int SchemaVersion,
    int FileCount,
    string Shape,
    string Format,
    string State
);
