using System.Collections.Immutable;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Syntax.Ast;
using GSCode.Workspace.Database;
using GSCode.Workspace.Documents;
using GSCode.Server.Mapping;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Position = GSCode.Core.Text.Position;

namespace GSCode.Server.Handlers;

/// <summary>
/// Quick fixes over the open document: "Remove duplicate #using" for a redundant import, and
/// "Add #using ..." for a qualified call whose defining file the document does not yet import
/// (the natural fix for the NamespaceNotImported lint).
/// </summary>
public sealed class CodeActionHandler : CodeActionHandlerBase
{
    private readonly DocumentStore _documents;
    private readonly NavigationSupport _support;
    private readonly TextDocumentSelector _selector;

    public CodeActionHandler(DocumentStore documents, NavigationSupport support, TextDocumentSelector selector)
    {
        _documents = documents;
        _support = support;
        _selector = selector;
    }

    protected override CodeActionRegistrationOptions CreateRegistrationOptions(
        CodeActionCapability capability, ClientCapabilities clientCapabilities)
    {
        return new CodeActionRegistrationOptions
        {
            DocumentSelector = _selector,
            CodeActionKinds = new Container<CodeActionKind>(CodeActionKind.QuickFix),
        };
    }

    // No lazy resolution — actions carry their edit up front.
    public override Task<CodeAction> Handle(CodeAction request, CancellationToken cancellationToken)
    {
        return Task.FromResult(request);
    }

    public override Task<CommandOrCodeActionContainer?> Handle(CodeActionParams request, CancellationToken cancellationToken)
    {
        if ( !_documents.TryGet(request.TextDocument.Uri.GetFileSystemPath(), out OpenDocument document)
            || document.LatestResult is null )
        {
            return Task.FromResult<CommandOrCodeActionContainer?>(null);
        }

        ParseResult result = document.LatestResult;
        TextRange selection = request.Range.ToCore();
        List<CommandOrCodeAction> actions = [];

        foreach ( UsingNode duplicate in FindRemovableDuplicates(result, selection) )
        {
            actions.Add(new CommandOrCodeAction(BuildRemoveAction(request.TextDocument.Uri, duplicate)));
        }

        NavigationTarget? target = _support.Resolve(request.TextDocument.Uri);
        if ( target is not null )
        {
            Position insertAt = UsingInsertionPoint(result);
            foreach ( string usingPath in FindMissingUsings(result, target.Store, target.ContextId, target.Path, selection) )
            {
                actions.Add(new CommandOrCodeAction(BuildAddUsingAction(request.TextDocument.Uri, usingPath, insertAt)));
            }
        }

        return Task.FromResult<CommandOrCodeActionContainer?>(new CommandOrCodeActionContainer(actions));
    }

    /// <summary>
    /// The #using directives whose path was already imported earlier in the file AND whose
    /// line overlaps the selection — i.e. the redundant ones offered for removal.
    /// </summary>
    internal static List<UsingNode> FindRemovableDuplicates(ParseResult result, TextRange selection)
    {
        List<UsingNode> duplicates = [];
        HashSet<string> seenPaths = new(StringComparer.Ordinal);

        foreach ( AstNode element in result.Tree.Root.Elements )
        {
            if ( element is not UsingNode usingNode )
            {
                continue;
            }

            string normalized = NormalizePath(usingNode.Path);

            // Only the second-and-later occurrences of a path are redundant.
            if ( seenPaths.Add(normalized) )
            {
                continue;
            }

            if ( Overlaps(usingNode.Range, selection) )
            {
                duplicates.Add(usingNode);
            }
        }

        return duplicates;
    }

    /// <summary>
    /// Distinct #using paths that would make a qualified call in the selection resolvable but
    /// aren't imported yet: for each qualified call, the script-relative path of a visible file
    /// defining that function, minus the extension. Own-namespace calls and already-imported
    /// files are skipped.
    /// </summary>
    internal static List<string> FindMissingUsings(
        ParseResult result, LanguageStore store, string contextId, string askingPath, TextRange selection)
    {
        HashSet<string> ownNamespaces = new(StringComparer.Ordinal);
        foreach ( GSCode.Core.Symbols.NamespaceSpan span in result.Extraction.Namespaces )
        {
            ownNamespaces.Add(span.KeyName);
        }

        HashSet<string> existingUsings = new(StringComparer.Ordinal);
        foreach ( AstNode element in result.Tree.Root.Elements )
        {
            if ( element is UsingNode usingNode )
            {
                existingUsings.Add(StripExtension(NormalizePath(usingNode.Path)));
            }
        }

        List<string> missing = [];
        HashSet<string> offered = new(StringComparer.Ordinal);

        foreach ( GSCode.Core.Symbols.ReferenceEntry entry in result.Extraction.References )
        {
            if ( entry.Kind != GSCode.Core.Symbols.ReferenceKind.Call
                || entry.Key.Kind != GSCode.Core.Symbols.SymbolKind.Function )
            {
                continue;
            }

            string? namespaceName = entry.Key.Namespace;
            if ( namespaceName is null || ownNamespaces.Contains(namespaceName) )
            {
                continue;
            }

            if ( !Overlaps(entry.Range, selection) )
            {
                continue;
            }

            ImmutableArray<ResolvedFunction> defined = DatabaseQueries.LookupFunctions(
                store, contextId, askingPath, namespaceName, entry.Key.Name);
            foreach ( ResolvedFunction resolved in defined )
            {
                if ( resolved.Record.RelativePath.Length == 0 )
                {
                    continue;
                }

                string usingPath = StripExtension(NormalizePath(resolved.Record.RelativePath));
                if ( existingUsings.Contains(usingPath) || !offered.Add(usingPath) )
                {
                    continue;
                }

                missing.Add(usingPath);
            }
        }

        return missing;
    }

    /// <summary>Where a new #using should be inserted: after the last one, else the file top.</summary>
    private static Position UsingInsertionPoint(ParseResult result)
    {
        int line = 0;
        foreach ( AstNode element in result.Tree.Root.Elements )
        {
            if ( element is UsingNode usingNode )
            {
                line = usingNode.Range.Start.Line + 1;
            }
        }

        return new Position(line, 0);
    }

    private static CodeAction BuildRemoveAction(DocumentUri uri, UsingNode usingNode)
    {
        // Delete the whole line the directive sits on, including its trailing newline.
        int line = usingNode.Range.Start.Line;
        TextRange lineRange = new(new Position(line, 0), new Position(line + 1, 0));
        TextEdit edit = new() { Range = lineRange.ToLsp(), NewText = "" };

        return new CodeAction
        {
            Title = "Remove duplicate #using " + usingNode.Path,
            Kind = CodeActionKind.QuickFix,
            Edit = SingleEdit(uri, edit),
        };
    }

    private static CodeAction BuildAddUsingAction(DocumentUri uri, string usingPath, Position insertAt)
    {
        TextRange insertRange = new(insertAt, insertAt);
        TextEdit edit = new() { Range = insertRange.ToLsp(), NewText = "#using " + usingPath + ";\n" };

        return new CodeAction
        {
            Title = "Add #using " + usingPath,
            Kind = CodeActionKind.QuickFix,
            Edit = SingleEdit(uri, edit),
        };
    }

    private static WorkspaceEdit SingleEdit(DocumentUri uri, TextEdit edit)
    {
        Dictionary<DocumentUri, IEnumerable<TextEdit>> changes = new()
        {
            [uri] = new[] { edit },
        };

        return new WorkspaceEdit { Changes = changes };
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('/', '\\').ToLowerInvariant();
    }

    private static string StripExtension(string path)
    {
        if ( path.EndsWith(".gsc", StringComparison.Ordinal) || path.EndsWith(".csc", StringComparison.Ordinal) )
        {
            return path[..^4];
        }

        return path;
    }

    private static bool Overlaps(TextRange node, TextRange selection)
    {
        return node.Start <= selection.End && selection.Start <= node.End;
    }
}
