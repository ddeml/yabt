using Yabt.Common.Implementation;

namespace Yabt.Common;

/// <summary>Applies operation-owned location checks before temporary storage is used.</summary>
public static class TemporaryStorageValidation
{
    internal static readonly AsyncLocal<TemporaryStorageValidationScope?> ActiveOperation = new();

    public static IDisposable BeginOperation(Action<string> validateDirectory)
    {
        ArgumentNullException.ThrowIfNull(validateDirectory);
        var scope = new TemporaryStorageValidationScope(validateDirectory, ActiveOperation.Value);
        ActiveOperation.Value = scope;
        return scope;
    }

    public static void ValidateDirectory(string directory) => ActiveOperation.Value?.ValidateDirectory(directory);

    /// <summary>Returns locations used by the current operation, including earlier option values.</summary>
    public static IReadOnlyList<string> GetDirectories() => ActiveOperation.Value?.GetDirectories() ?? [];
}
