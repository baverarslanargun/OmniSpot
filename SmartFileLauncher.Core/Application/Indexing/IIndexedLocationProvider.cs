namespace SmartFileLauncher.Core.Application.Indexing;

public interface IIndexedLocationProvider
{
    IndexLocations Resolve();
}
