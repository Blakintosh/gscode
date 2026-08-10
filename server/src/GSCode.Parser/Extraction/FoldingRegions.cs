using System.Collections.Immutable;
using System.Text.RegularExpressions;
using GSCode.Core.Text;
using GSCode.Parser.Lexing;
using GSCode.Parser.Syntax.Ast;

namespace GSCode.Parser.Extraction;

/// <summary>What produced a folding region (maps onto LSP folding kinds).</summary>
public enum FoldingRegionKind
{
    Code,
    Comment,
    UserRegion,
}

/// <summary>One foldable region, line-based and inclusive.</summary>
public readonly record struct FoldingRegion(int StartLine, int EndLine, FoldingRegionKind Kind);

/// <summary>
/// Computes foldable regions from a parse result: declarations/blocks from the AST,
/// multi-line comments and dev blocks from the raw tokens, and user-marked
/// /* region Name */ ... /* endregion */ pairs (case-insensitive, nestable).
/// </summary>
public static partial class FoldingRegions
{
    [GeneratedRegex(@"^/\*\s*region\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RegionStartRegex();

    [GeneratedRegex(@"^/\*\s*endregion\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RegionEndRegex();

    /// <summary>Computes all folding regions for a file.</summary>
    public static ImmutableArray<FoldingRegion> Compute(ParseResult result)
    {
        ImmutableArray<FoldingRegion>.Builder regions = ImmutableArray.CreateBuilder<FoldingRegion>();

        CollectAstRegions(result.Tree.Root, regions);
        CollectTokenRegions(result, regions);

        return regions.ToImmutable();
    }

    private static void CollectAstRegions(AstNode node, ImmutableArray<FoldingRegion>.Builder regions)
    {
        switch ( node )
        {
            case ScriptNode script:
            {
                foreach ( AstNode element in script.Elements )
                {
                    CollectAstRegions(element, regions);
                }

                return;
            }
            case FunctionNode function:
                AddIfMultiline(function.Range, regions);
                CollectAstRegions(function.Body, regions);
                return;
            case ClassNode classNode:
            {
                AddIfMultiline(classNode.Range, regions);
                foreach ( AstNode member in classNode.Members )
                {
                    CollectAstRegions(member, regions);
                }

                return;
            }
            case ConstructorNode constructor:
                AddIfMultiline(constructor.Range, regions);
                CollectAstRegions(constructor.Body, regions);
                return;
            case DestructorNode destructor:
                AddIfMultiline(destructor.Range, regions);
                CollectAstRegions(destructor.Body, regions);
                return;
            case BlockNode block:
            {
                AddIfMultiline(block.Range, regions);
                foreach ( AstNode statement in block.Statements )
                {
                    CollectAstRegions(statement, regions);
                }

                return;
            }
            case IfNode ifNode:
                CollectAstRegions(ifNode.Then, regions);
                if ( ifNode.Else is not null )
                {
                    CollectAstRegions(ifNode.Else, regions);
                }

                return;
            case WhileNode whileNode:
                CollectAstRegions(whileNode.Body, regions);
                return;
            case DoWhileNode doWhile:
                CollectAstRegions(doWhile.Body, regions);
                return;
            case ForNode forNode:
                CollectAstRegions(forNode.Body, regions);
                return;
            case ForeachNode foreachNode:
                CollectAstRegions(foreachNode.Body, regions);
                return;
            case SwitchNode switchNode:
            {
                AddIfMultiline(switchNode.Range, regions);
                foreach ( CaseGroupNode caseGroup in switchNode.Cases )
                {
                    foreach ( AstNode statement in caseGroup.Statements )
                    {
                        CollectAstRegions(statement, regions);
                    }
                }

                return;
            }
            case DevBlockDeclNode devDecl:
            {
                AddIfMultiline(devDecl.Range, regions);
                foreach ( AstNode declaration in devDecl.Declarations )
                {
                    CollectAstRegions(declaration, regions);
                }

                return;
            }
            case DevBlockStmtNode devStmt:
            {
                AddIfMultiline(devStmt.Range, regions);
                foreach ( AstNode statement in devStmt.Statements )
                {
                    CollectAstRegions(statement, regions);
                }

                return;
            }
            default:
                return;
        }
    }

    private static void CollectTokenRegions(ParseResult result, ImmutableArray<FoldingRegion>.Builder regions)
    {
        // Multi-line comments/doc blocks fold as comments; /* region */ markers pair up
        // into user regions via a stack (nesting supported).
        Stack<int> regionStarts = new();

        foreach ( Token token in result.Lexed.Tokens )
        {
            if ( token.Kind == TokenKind.BlockComment )
            {
                string text = token.GetText(result.Text).ToString();

                if ( RegionStartRegex().IsMatch(text) )
                {
                    regionStarts.Push(token.Range.Start.Line);
                    continue;
                }

                if ( RegionEndRegex().IsMatch(text) )
                {
                    if ( regionStarts.Count > 0 )
                    {
                        int startLine = regionStarts.Pop();
                        int endLine = token.Range.End.Line;
                        if ( endLine > startLine )
                        {
                            regions.Add(new FoldingRegion(startLine, endLine, FoldingRegionKind.UserRegion));
                        }
                    }

                    continue;
                }
            }

            if ( token.Kind is TokenKind.BlockComment or TokenKind.DocComment
                && token.Range.End.Line > token.Range.Start.Line )
            {
                regions.Add(new FoldingRegion(token.Range.Start.Line, token.Range.End.Line, FoldingRegionKind.Comment));
            }
        }
    }

    private static void AddIfMultiline(TextRange range, ImmutableArray<FoldingRegion>.Builder regions)
    {
        if ( range.End.Line > range.Start.Line )
        {
            regions.Add(new FoldingRegion(range.Start.Line, range.End.Line, FoldingRegionKind.Code));
        }
    }
}
