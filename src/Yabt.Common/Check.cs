using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Yabt.Common;

/// <summary>
/// Convenience methods to easily check for null/default values with a more meaningful exception message.
/// Prefer using these methods over the nullable override operator (!)
/// </summary>
public static class Check
{
    /// <summary>
    /// Checks if <paramref name="arg"/> is null and throws a meaningful <see cref="ArgumentNullException"/> otherwise.
    /// </summary>
    [DebuggerStepThrough]
    public static T NotNull<T>([NotNull] T? arg, [CallerArgumentExpression(nameof(arg))] string? expression = default)
        where T : class => arg ?? throw new ArgumentNullException(null, expression);

    /// <summary>
    /// Checks if <paramref name="arg"/> is null and throws a meaningful <see cref="ArgumentNullException"/> otherwise.
    /// </summary>
    [DebuggerStepThrough]
    public static T NotNull<T>([NotNull] T? arg, [CallerArgumentExpression(nameof(arg))] string? expression = default)
            where T : struct => arg ?? throw new ArgumentNullException(null, expression);

}
