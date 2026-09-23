using GSCode.Core;
using GSCode.Core.Paths;
using GSCode.Parser;
using GSCode.Parser.Syntax.Ast;
using Position = GSCode.Core.Text.Position;

namespace GSCode.Server.Handlers;

/// <summary>
/// The two facts every handler that WRITES an import directive needs: the form a script path takes
/// inside one, and where a new one goes in the file.
///
/// Shared rather than private to <see cref="CodeActionHandler"/> because a second writer exists —
/// completion offering a function from a file the document has not imported, which inserts the same
/// directive from a different request. The spelling has to be identical across both: the set built
/// from a file's OWN directives is compared against the ones built from a declaring record's
/// relative path, so two spellings that drift mean offering an import the file already has. This
/// file's history is the argument — four spellings of <see cref="PathOf"/> were collapsed into one
/// for exactly that reason, and a copy in another handler would be the fifth.
/// </summary>
internal static class ImportEdits
{
    /// <summary>
    /// A path in the form an import directive names a script in: normalized, and without the
    /// language's own extension.
    ///
    /// Not <c>RelativePathIndex.Normalize</c>, which strips ANY extension: only the server and
    /// client script extensions come off here, since <c>#insert</c> names a header in full.
    /// </summary>
    public static string PathOf(string path)
    {
        return StripExtension(PathUtil.NormalizeScriptPath(path));
    }

    /// <summary>
    /// Where a new import belongs: just after the last one of its kind, else the top of the file.
    /// <paramref name="beforeLine"/> caps which directives count, so moving a misplaced <c>#using</c>
    /// does not target a point below itself — the directive being moved is the very thing that must
    /// not anchor the insertion.
    /// </summary>
    /// <typeparam name="TNode">
    /// <c>UsingNode</c> or <c>IncludeNode</c>. Written once for both rather than per directive:
    /// <see cref="CodeActionHandler"/> learned the same lesson at its <c>FindRemovableDuplicates</c>,
    /// where a <c>#using</c>-only helper left the four merge games with a lint and no fix behind it.
    /// </typeparam>
    public static Position InsertionPoint<TNode>(ParseResult result, int beforeLine = int.MaxValue)
        where TNode : AstNode
    {
        int line = 0;
        foreach ( AstNode element in result.Tree.Root.Elements )
        {
            if ( element is TNode && element.Range.Start.Line < beforeLine )
            {
                line = element.Range.Start.Line + 1;
            }
        }

        return new Position(line, 0);
    }

    private static string StripExtension(string path)
    {
        // Scripts are reached by #using, which names them without extension. Strip the server
        // or client extension; headers keep theirs (#insert names them in full). Two checks
        // rather than a loop over an array literal built fresh per call — GameProfile.Active can
        // change mid-session, so the two extensions cannot be cached, and there are only ever two.
        string serverExtension = GameProfile.Active.ServerScriptExtension;
        if ( path.EndsWith(serverExtension, StringComparison.Ordinal) )
        {
            return path[..^serverExtension.Length];
        }

        string clientExtension = GameProfile.Active.ClientScriptExtension;
        if ( path.EndsWith(clientExtension, StringComparison.Ordinal) )
        {
            return path[..^clientExtension.Length];
        }

        return path;
    }
}
