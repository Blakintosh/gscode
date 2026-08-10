using GSCode.Core;

namespace GSCode.Workspace.Analysis;

/// <summary>Which broad dialect shape a file looks like, from its import directives.</summary>
public enum GameShape
{
    /// <summary>No decisive signal either way.</summary>
    Unknown,

    /// <summary>Black Ops III onward: <c>#using</c> / <c>#namespace</c> / <c>#insert</c>.</summary>
    BlackOps3,

    /// <summary>Everything before BO3: <c>#include</c>.</summary>
    PreBlackOps3,
}

/// <summary>
/// A cheap check of whether a file matches the selected game, so the editor can offer to switch the
/// version when it clearly does not. It keys off the import directive, which is the one unambiguous
/// tell: BO3 uses <c>#using</c> and never <c>#include</c>; every earlier game is the reverse.
///
/// Deliberately shallow — it reads the directive lines, not the whole grammar. A wrong guess only
/// produces a dismissable prompt, so a rare false positive costs nothing; being fast and running on
/// every opened file matters more.
/// </summary>
public static class GameShapeDetector
{
    public static GameShape Detect(string text)
    {
        foreach ( string rawLine in text.Split('\n') )
        {
            string line = rawLine.TrimStart();

            // #include is the pre-BO3 tell and appears nowhere in BO3.
            if ( line.StartsWith("#include", StringComparison.Ordinal) )
            {
                return GameShape.PreBlackOps3;
            }

            // #namespace and #insert are BO3-only. #using is too, but must not be confused with
            // #using_animtree, which both families have.
            if ( line.StartsWith("#namespace", StringComparison.Ordinal)
                || line.StartsWith("#insert", StringComparison.Ordinal)
                || IsPlainUsing(line) )
            {
                return GameShape.BlackOps3;
            }
        }

        return GameShape.Unknown;
    }

    /// <summary>Whether the selected profile disagrees with what the file looks like.</summary>
    public static bool Mismatches(GameProfile active, GameShape shape)
    {
        switch ( shape )
        {
            case GameShape.BlackOps3:
                return active.ImportStyle != ImportStyle.Namespace;
            case GameShape.PreBlackOps3:
                return active.ImportStyle != ImportStyle.Include;
            default:
                return false;
        }
    }

    private static bool IsPlainUsing(string line)
    {
        if ( !line.StartsWith("#using", StringComparison.Ordinal) )
        {
            return false;
        }

        // The character after "#using" must not continue the word, or "#using_animtree" matches.
        return line.Length == "#using".Length || !(char.IsLetterOrDigit(line["#using".Length]) || line["#using".Length] == '_');
    }
}
