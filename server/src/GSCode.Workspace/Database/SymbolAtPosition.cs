using System.Collections.Immutable;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Parser.Syntax.Ast;

namespace GSCode.Workspace.Database;

/// <summary>What the cursor is sitting on: a classified reference, a dependency path, or nothing.</summary>
public enum HitKind
{
    None,
    Reference,
    DependencyPath,
}

/// <summary>
/// The resolved thing under a cursor position. For a Reference hit the SymbolKey and its
/// range are set; for a DependencyPath hit the resolved target path is set. This single
/// resolver backs hover, definition, references, highlight, and document links.
/// </summary>
public readonly record struct PositionHit(
    HitKind Kind,
    SymbolKey Key,
    TextRange Range,
    ReferenceKind ReferenceKind,
    string DependencyTargetPath)
{
    public static PositionHit None { get; } = new(HitKind.None, default, TextRange.Empty, default, "");
}

/// <summary>Finds what symbol or path a position refers to within a file's analysis.</summary>
public static class SymbolAtPosition
{
    /// <summary>
    /// The reference under <paramref name="position"/>, or <see cref="PositionHit.None"/> when the
    /// position is on none of them. Shared by the two <c>Resolve</c> overloads: an indexed record
    /// and a live parse hold the same reference list, so the rule for reading it — including the
    /// macro guard — is one rule, and writing it twice gave it two places to change.
    /// </summary>
    private static PositionHit ResolveReference(ImmutableArray<ReferenceEntry> references, Position position)
    {
        foreach ( ReferenceEntry entry in references )
        {
            // Never resolve the cursor to something a macro expanded into: the characters under
            // it spell the macro's name, so go-to-definition and hover belong to the macro.
            if ( entry.FromMacro )
            {
                continue;
            }

            if ( entry.Range.Contains(position) )
            {
                return new PositionHit(HitKind.Reference, entry.Key, entry.Range, entry.Kind, "");
            }
        }

        return PositionHit.None;
    }

    /// <summary>
    /// Resolves the position against the record's classified references first (functions,
    /// classes, macros, fields, literals), then its #using/#insert dependency paths.
    /// </summary>
    public static PositionHit Resolve(ScriptRecord record, Position position)
    {
        PositionHit hit = ResolveReference(record.References, position);
        if ( hit.Kind != HitKind.None )
        {
            return hit;
        }

        foreach ( DependencyEdge edge in record.Dependencies )
        {
            if ( edge.Range.Contains(position) && edge.ResolvedPath.Length > 0 )
            {
                return new PositionHit(HitKind.DependencyPath, default, edge.Range, default, edge.ResolvedPath);
            }
        }

        return PositionHit.None;
    }

    /// <summary>Resolves against an open document's live analysis (references come from extraction).</summary>
    public static PositionHit Resolve(ParseResult result, Position position)
    {
        PositionHit hit = ResolveReference(result.Extraction.References, position);
        if ( hit.Kind != HitKind.None )
        {
            return hit;
        }

        foreach ( InsertEdge insert in result.Preprocessed.Inserts )
        {
            if ( insert.ContainingFile is null && insert.ResolvedPath is not null && insert.DirectiveRange.Contains(position) )
            {
                return new PositionHit(HitKind.DependencyPath, default, insert.DirectiveRange, default, insert.ResolvedPath);
            }
        }

        foreach ( AstNode element in result.Tree.Root.Elements )
        {
            if ( element is UsingNode usingNode && usingNode.PathRange.Contains(position) )
            {
                // #using targets resolve at query time (the resolver isn't held here).
                return new PositionHit(HitKind.DependencyPath, default, usingNode.PathRange, default, "");
            }

            if ( element is IncludeNode includeNode && includeNode.PathRange.Contains(position) )
            {
                // #include is the Infinity Ward import; its target resolves the same way.
                return new PositionHit(HitKind.DependencyPath, default, includeNode.PathRange, default, "");
            }
        }

        return PositionHit.None;
    }
}
