using System.Collections.Immutable;
using GSCode.Core.Text;
using GSCode.Workspace.Database;

namespace GSCode.Workspace.Resolution;

/// <summary>One directive path to rewrite: where the file is, which range, and the replacement.</summary>
public readonly record struct DependencyEdit(string FilePath, TextRange Range, string NewText);

/// <summary>
/// Plans the <c>#using</c>/<c>#insert</c> path edits a file rename implies. Renaming a script
/// otherwise breaks every importer silently, because the directives keep pointing at the old
/// name and only surface as unresolved-path diagnostics later.
///
/// Matching is on the path AS WRITTEN in the directive rather than on a resolved absolute
/// path, because <c>#using</c> edges carry no resolved path — they are resolved lazily per
/// asking context, so the same directive text can mean different files in different contexts.
/// </summary>
public static class DependencyRewrite
{
    /// <summary>
    /// Every edit needed so directives naming <paramref name="oldDirectivePath"/> name
    /// <paramref name="newDirectivePath"/> instead. Both are script-relative and in directive
    /// form: no extension for <c>#using</c>, the <c>.gsh</c> extension kept for <c>#insert</c>.
    /// </summary>
    public static ImmutableArray<DependencyEdit> PlanRename(
        ScriptDatabase database,
        string oldDirectivePath,
        string newDirectivePath,
        bool isInsert)
    {
        if ( oldDirectivePath.Length == 0 || newDirectivePath.Length == 0 )
        {
            return [];
        }

        string wanted = Canonical(oldDirectivePath);
        if ( wanted == Canonical(newDirectivePath) )
        {
            return [];
        }

        ImmutableArray<DependencyEdit>.Builder edits = ImmutableArray.CreateBuilder<DependencyEdit>();

        foreach ( ScriptRecord record in database.AllRecords )
        {
            foreach ( DependencyEdge edge in record.Dependencies )
            {
                if ( edge.IsInsert != isInsert )
                {
                    continue;
                }

                if ( Canonical(edge.RawPath) != wanted )
                {
                    continue;
                }

                // The stored range covers the path argument only, so the directive keyword and
                // its trailing semicolon are untouched.
                edits.Add(new DependencyEdit(record.Path, edge.Range, newDirectivePath));
            }
        }

        return edits.ToImmutable();
    }

    /// <summary>
    /// The directive form of a script-relative path: backslashes, and the extension dropped for
    /// <c>#using</c> (which names a script without one) but kept for <c>#insert</c>.
    /// </summary>
    public static string ToDirectivePath(string relativePath, bool isInsert)
    {
        if ( relativePath.Length == 0 )
        {
            return "";
        }

        string withBackslashes = relativePath.Replace('/', '\\').TrimStart('\\');
        if ( isInsert )
        {
            return withBackslashes;
        }

        int lastDot = withBackslashes.LastIndexOf('.');
        int lastSeparator = withBackslashes.LastIndexOf('\\');

        return lastDot > lastSeparator ? withBackslashes[..lastDot] : withBackslashes;
    }

    /// <summary>Paths in directives are case-insensitive and may use either slash style.</summary>
    private static string Canonical(string directivePath)
    {
        return directivePath.Replace('/', '\\').TrimStart('\\').ToLowerInvariant();
    }

    /// <summary>Both language worlds plus the shared headers — a .gsh can insert another .gsh.</summary>
}
