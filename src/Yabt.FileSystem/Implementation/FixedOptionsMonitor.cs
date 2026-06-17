using Microsoft.Extensions.Options;

namespace Yabt.FileSystem.Implementation;

internal sealed class FixedOptionsMonitor<TOptions>(TOptions _options) : IOptionsMonitor<TOptions>
    where TOptions : class
{
    public TOptions CurrentValue => _options;

    public TOptions Get(string? name) => _options;

    public IDisposable? OnChange(Action<TOptions, string?> listener) => FixedOptionsMonitorChangeRegistration.Instance;

    private sealed class FixedOptionsMonitorChangeRegistration : IDisposable
    {
        public static FixedOptionsMonitorChangeRegistration Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
