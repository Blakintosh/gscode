using GSCode.Workspace.Resolution;

namespace GSCode.Server.Configuration;

/// <summary>
/// Holds the current PathResolver. The real one is built during initialize (settings +
/// workspace folders arrive there); consumers read Current at call time so the swap
/// (and future workspace-folder rebuilds) need no re-wiring.
/// </summary>
public sealed class ResolverHolder
{
    private volatile PathResolver _current;

    public ResolverHolder(IFileSystem fileSystem)
    {
        _current = new PathResolver(RootConfig.Empty, fileSystem);
    }

    public PathResolver Current
    {
        get { return _current; }
        set { _current = value; }
    }
}
