using Yabt.Core.Models;

namespace Yabt.Sync.Implementation;

internal sealed class RestoreProjectionUsage
{
    private readonly Lock _gate = new();
    private readonly RestorePlan _plan;
    private readonly Dictionary<ArchiveRestoreProjection, int> _remainingUses = new();

    private RestoreProjectionUsage(RestorePlan plan)
    {
        _plan = plan;
    }

    public static async Task<RestoreProjectionUsage> CreateAsync
    (
        RestorePlan plan,
        IReadOnlyList<ArchiveProjectedObject> filesToOpen
    )
    {
        var usage = new RestoreProjectionUsage(plan);
        foreach (var file in filesToOpen)
        {
            foreach (var projection in plan
                .GetProjectionDependencies(file.RelativePath)
                .Distinct())
            {
                usage._remainingUses.TryGetValue(projection, out var currentUses);
                usage._remainingUses[projection] = currentUses + 1;
            }
        }

        foreach (var projection in plan.Projections)
        {
            if (!usage._remainingUses.ContainsKey(projection))
            {
                await projection.DisposeAsync();
            }
        }

        return usage;
    }

    public async Task ReleaseAsync(string relativePath)
    {
        List<ArchiveRestoreProjection>? completedProjections = null;
        lock (_gate)
        {
            foreach (var projection in _plan
                .GetProjectionDependencies(relativePath)
                .Distinct())
            {
                if (!_remainingUses.TryGetValue(projection, out var remainingUses))
                {
                    continue;
                }

                remainingUses--;
                if (remainingUses == 0)
                {
                    _remainingUses.Remove(projection);
                    (completedProjections ??= []).Add(projection);
                }
                else
                {
                    _remainingUses[projection] = remainingUses;
                }
            }
        }

        if (completedProjections is null) { return; }

        foreach (var projection in completedProjections)
        {
            await projection.DisposeAsync();
        }
    }
}
