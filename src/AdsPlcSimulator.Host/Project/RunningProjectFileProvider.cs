using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace AdsPlcSimulator.Host.Project;

internal sealed class RunningProjectFileProvider(ProjectHost host) : IFileProvider
{
    public IFileInfo GetFileInfo(string subpath)
    {
        try
        {
            return Current.GetFileInfo(subpath);
        }
        catch (ObjectDisposedException)
        {
            return new NotFoundFileInfo(subpath);
        }
    }

    public IDirectoryContents GetDirectoryContents(string subpath)
    {
        try
        {
            return Current.GetDirectoryContents(subpath);
        }
        catch (ObjectDisposedException)
        {
            return NotFoundDirectoryContents.Singleton;
        }
    }

    public IChangeToken Watch(string filter) => NullChangeToken.Singleton;

    private IFileProvider Current => host.Current?.SimulationFiles ?? new NullFileProvider();
}
