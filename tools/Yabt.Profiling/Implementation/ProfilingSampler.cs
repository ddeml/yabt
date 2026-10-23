using System.Diagnostics;

namespace Yabt.Profiling.Implementation;

/// <summary>
/// Keeps bounded samples. Observer allocations remain in process-wide counters.
/// Retention probes deliberately alter GC and are never timing benchmarks.
/// </summary>
internal sealed class ProfilingSampler : IAsyncDisposable
{
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationTokenSource _operationCancellation;
    private readonly string _scratchRoot;
    private readonly bool _retained;
    private readonly long _privateLimitBytes;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Task _sampling;
    private readonly List<ProfilingCheckpoint> _checkpoints = [];

    public ProfilingSampler
    (
        string scratchRoot,
        bool retained,
        long privateLimitBytes,
        CancellationTokenSource operationCancellation
    )
    {
        _scratchRoot = scratchRoot;
        _retained = retained;
        _privateLimitBytes = privateLimitBytes;
        _operationCancellation = operationCancellation;
        _sampling = SampleAsync();
    }

    public const int IntervalMilliseconds = 250;

    public long SampledPeakPrivateBytes { get; private set; }

    public long SampledPeakWorkingSetBytes { get; private set; }

    public long SampledPeakManagedBytes { get; private set; }

    public long SampleCount { get; private set; }

    public string? LimitFailure { get; private set; }

    public IReadOnlyList<ProfilingCheckpoint> Checkpoints => _checkpoints;

    public void Capture(string phase, IReadOnlyDictionary<string, long>? counts = null)
    {
        lock (_gate)
        {
            if (_retained) { Collect(); }
            var elapsed = _clock.Elapsed.TotalMilliseconds;
            var totalAllocated = GC.GetTotalAllocatedBytes(precise: true);
            var managedBytes = GC.GetTotalMemory(forceFullCollection: false);
            var gc = GC.GetGCMemoryInfo();
            _process.Refresh();
            _checkpoints.Add
            (
                new
                (
                    phase,
                    elapsed,
                    managedBytes,
                    gc.HeapSizeBytes,
                    gc.FragmentedBytes,
                    gc.TotalCommittedBytes,
                    totalAllocated,
                    GC.CollectionCount(0),
                    GC.CollectionCount(1),
                    GC.CollectionCount(2),
                    _process.PrivateMemorySize64,
                    _process.WorkingSet64,
                    _process.TotalProcessorTime.TotalMilliseconds,
                    GetScratchSize(),
                    counts
                )
            );
            Sample();
        }
    }

    public static void Collect()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
    }

    private async Task SampleAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(IntervalMilliseconds));
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                lock (_gate) { Sample(); }
                if (LimitFailure is not null) { await _operationCancellation.CancelAsync(); }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private void Sample()
    {
        _process.Refresh();
        var privateBytes = _process.PrivateMemorySize64;
        SampledPeakPrivateBytes = Math.Max(SampledPeakPrivateBytes, privateBytes);
        SampledPeakWorkingSetBytes = Math.Max(SampledPeakWorkingSetBytes, _process.WorkingSet64);
        SampledPeakManagedBytes = Math.Max
        (
            SampledPeakManagedBytes, GC.GetTotalMemory(forceFullCollection: false)
        );
        SampleCount++;
        if (privateBytes > _privateLimitBytes)
        {
            LimitFailure = $"Process private memory exceeded {_privateLimitBytes} bytes.";
        }
    }

    private long GetScratchSize()
    {
        long bytes = 0;
        foreach (var path in Directory.EnumerateFiles(_scratchRoot, "*", SearchOption.AllDirectories))
        {
            try { bytes += new FileInfo(path).Length; }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }

        return bytes;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _sampling;
        _stop.Dispose();
        _process.Dispose();
    }
}
