using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace AdsPlcSimulator.Host.Project;

internal sealed class RunningProjectFileProvider(ProjectHost host) : IFileProvider
{
    private static readonly NullFileProvider Empty = new();

    public IFileInfo GetFileInfo(string subpath) => Current.GetFileInfo(subpath);

    public IDirectoryContents GetDirectoryContents(string subpath) => Current.GetDirectoryContents(subpath);

    public IChangeToken Watch(string filter) => NullChangeToken.Singleton;

    private IFileProvider Current => host.Current?.SimulationFiles ?? Empty;
}
