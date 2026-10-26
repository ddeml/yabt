namespace Yabt.Core.Abstractions;

/// <summary>
/// Optional provider identity for machine-local, disposable operation progress.
/// It identifies the effective configured location without exposing credentials.
/// Equality is a scheduling hint, never evidence of unchanged content, exclusive
/// ownership, or physical separation from another store.
/// </summary>
public interface IObjectStoreOperationIdentity
{
    /// <summary>
    /// Stable, opaque identity including the provider's effective root and prefix.
    /// Obtaining it must not probe storage or retain secret configuration values.
    /// </summary>
    string OperationIdentity { get; }
}
