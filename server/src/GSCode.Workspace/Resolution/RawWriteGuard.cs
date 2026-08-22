using GSCode.Workspace.Api;

namespace GSCode.Workspace.Resolution;

/// <summary>How aggressively to warn when a file under the game's raw folder is saved.</summary>
public enum RawFileWarningMode
{
    /// <summary>Never warn.</summary>
    Off,

    /// <summary>Warn only for scripts that shipped with the mod tools.</summary>
    Stock,

    /// <summary>Warn for anything under raw.</summary>
    All,
}

/// <summary>
/// Decides whether saving a file deserves the raw-folder warning. Only the raw root is
/// protected: mod and workspace files are the user's own, so they never warn no matter
/// what the mode is.
/// </summary>
public static class RawWriteGuard
{
    /// <summary>Parses the client setting; anything unrecognised falls back to the "stock" default.</summary>
    public static RawFileWarningMode ParseMode(string value)
    {
        if ( string.Equals(value, "off", StringComparison.OrdinalIgnoreCase) )
        {
            return RawFileWarningMode.Off;
        }

        if ( string.Equals(value, "all", StringComparison.OrdinalIgnoreCase) )
        {
            return RawFileWarningMode.All;
        }

        return RawFileWarningMode.Stock;
    }

    public static bool ShouldWarn(
        RawFileWarningMode mode,
        ResolutionContext context,
        string relativePath,
        StockScripts stockScripts)
    {
        if ( mode == RawFileWarningMode.Off )
        {
            return false;
        }

        // A mod file shadowing a stock script is the correct way to work; never warn there.
        if ( context.Kind != ResolutionContextKind.Raw )
        {
            return false;
        }

        if ( mode == RawFileWarningMode.All )
        {
            return true;
        }

        return stockScripts.Contains(relativePath);
    }
}
