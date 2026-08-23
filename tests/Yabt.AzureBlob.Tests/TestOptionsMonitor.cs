using Microsoft.Extensions.Options;

namespace Yabt.AzureBlob.Tests;

internal sealed class TestOptionsMonitor<TOptions>(TOptions _currentValue) : IOptionsMonitor<TOptions>
    where TOptions : class
{
    public TOptions CurrentValue { get; private set; } = _currentValue;

    public TOptions Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<TOptions, string?> listener) =>
        TestOptionsMonitorChangeRegistration.Instance;

    public void Set(TOptions value)
    {
        CurrentValue = value;
    }

    private sealed class TestOptionsMonitorChangeRegistration : IDisposable
    {
        public static TestOptionsMonitorChangeRegistration Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
