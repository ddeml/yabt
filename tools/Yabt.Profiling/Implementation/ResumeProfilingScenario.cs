namespace Yabt.Profiling.Implementation;

internal sealed record ResumeProfilingScenario
(
    ProfilingFixture Fixture,
    string Format,
    string Operation,
    bool ProgressEnabled,
    int AbortAfterPayloadCommits,
    int MaximumSeconds,
    long MaximumPrivateBytes
)
{
    public string ProgressDirectory => Path.Combine(Fixture.RootPath, "progress");

    public string DestinationRoot => Path.Combine(Fixture.DestinationsRoot, "resume");
}
