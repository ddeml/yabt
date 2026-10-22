using Microsoft.Extensions.Options;

namespace Yabt.Common.Implementation;

internal sealed class TemporaryContentFactory
(
    IOptionsMonitor<TemporaryContentOptions> options
) : ITemporaryContentFactory
{
    private readonly IOptionsMonitor<TemporaryContentOptions> _options = Check.NotNull(options);
    private readonly Lock _gate = new();
    private long _reservedMemoryBytes;

    public long ReservedMemoryBytes
    {
        get { lock (_gate) { return _reservedMemoryBytes; } }
    }

    public TemporaryContent Create()
    {
        var options = _options.CurrentValue;
        _ = options.GetEffectiveTotalMemoryBudgetBytes();
        var directory = options.GetEffectiveTemporaryDirectory();
        TemporaryStorageValidation.ValidateDirectory(directory);
        return new SpillableTemporaryContent
        (
            this,
            options.GetEffectiveMemoryThresholdBytes(),
            directory
        );
    }

    internal bool TryReserve(int bytes)
    {
        var budget = _options.CurrentValue.GetEffectiveTotalMemoryBudgetBytes();
        lock (_gate)
        {
            if (bytes > budget - _reservedMemoryBytes) { return false; }
            _reservedMemoryBytes += bytes;
            return true;
        }
    }

    internal void Release(int bytes)
    {
        lock (_gate) { _reservedMemoryBytes -= bytes; }
    }
}
