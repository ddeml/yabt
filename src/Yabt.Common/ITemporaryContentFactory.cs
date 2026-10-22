namespace Yabt.Common;

/// <summary>
/// Creates owned replayable content sharing one host's memory budget.
/// </summary>
public interface ITemporaryContentFactory
{
    /// <summary>
    /// Gets the allocated capacity currently owned by content and its readers.
    /// This excludes I/O buffers and other process allocations.
    /// </summary>
    long ReservedMemoryBytes { get; }

    TemporaryContent Create();
}
