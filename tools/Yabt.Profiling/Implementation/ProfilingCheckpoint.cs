namespace Yabt.Profiling.Implementation;

internal sealed record ProfilingCheckpoint
(
    string Phase,
    double ElapsedMilliseconds,
    long ManagedBytes,
    long ManagedHeapBytesAtLastCollection,
    long FragmentedBytesAtLastCollection,
    long GcCommittedBytes,
    long TotalAllocatedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    long PrivateBytes,
    long WorkingSetBytes,
    double ProcessCpuMilliseconds,
    long ScratchLogicalBytes,
    IReadOnlyDictionary<string, long>? Counts
);
