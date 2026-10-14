namespace Yabt.Core.Abstractions;

public interface IRestoreDestinationFactory
{
    IRestoreDestination Create(string rootPath, IObjectStore? store = null);

    /// <summary>Creates private replay storage outside the archive's provider staging namespace.</summary>
    Stream CreateComparisonReplayStream();
}
