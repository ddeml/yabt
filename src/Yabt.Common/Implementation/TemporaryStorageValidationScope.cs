namespace Yabt.Common.Implementation;

internal sealed class TemporaryStorageValidationScope
(
    Action<string> _validateDirectory,
    TemporaryStorageValidationScope? _previous
) : IDisposable
{
    private readonly Lock _gate = new();
    private readonly HashSet<string> _directories = new(StringComparer.Ordinal);
    private bool _disposed;

    public void ValidateDirectory(string directory)
    {
        var fullPath = Path.GetFullPath(directory);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _validateDirectory(fullPath);
            _directories.Add(fullPath);
        }
    }

    public IReadOnlyList<string> GetDirectories()
    {
        lock (_gate) { return _directories.ToArray(); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) { return; }
            _disposed = true;
            if (ReferenceEquals(TemporaryStorageValidation.ActiveOperation.Value, this))
            {
                TemporaryStorageValidation.ActiveOperation.Value = _previous;
            }
        }
    }
}
