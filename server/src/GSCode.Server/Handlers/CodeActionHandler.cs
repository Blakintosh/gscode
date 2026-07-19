using GSCode.Core.Text;
using GSCode.Parser.Syntax.Ast;
using GSCode.Workspace.Documents;
using GSCode.Server.Mapping;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Position = GSCode.Core.Text.Position;

namespace GSCode.Server.Handlers;

/// <summary>
/// Quick fixes over the open document. Currently offers "Remove duplicate #using" for any
/// #using directive whose path was already imported earlier in the file (the redundant line
/// is deleted). More actions (auto-add #using) build on the same shape.
/// </summary>
public sealed class CodeActionHandler : CodeActionHandlerBase
{
    private readonly DocumentStore _documents;
    private readonly TextDocumentSelector _selector;

    public CodeActionHandler(DocumentStore documents, TextDocumentSelector selector)
    {
        _documents = documents;
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

        TextRange selection = request.Range.ToCore();
        List<CommandOrCodeAction> actions = [];
        foreach ( UsingNode duplicate in FindRemovableDuplicates(document.LatestResult, selection) )
        {
            actions.Add(new CommandOrCodeAction(BuildRemoveAction(request.TextDocument.Uri, duplicate)));
        }

        return Task.FromResult<CommandOrCodeActionContainer?>(new CommandOrCodeActionContainer(actions));
    }

    /// <summary>
    /// The #using directives whose path was already imported earlier in the file AND whose
    /// line overlaps the selection — i.e. the redundant ones offered for removal.
    /// </summary>
    internal static List<UsingNode> FindRemovableDuplicates(GSCode.Parser.ParseResult result, TextRange selection)
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

    private static CodeAction BuildRemoveAction(DocumentUri uri, UsingNode usingNode)
    {
        // Delete the whole line the directive sits on, including its trailing newline.
        int line = usingNode.Range.Start.Line;
        TextRange lineRange = new(new Position(line, 0), new Position(line + 1, 0));
        TextEdit edit = new() { Range = lineRange.ToLsp(), NewText = "" };

        Dictionary<DocumentUri, IEnumerable<TextEdit>> changes = new()
        {
            [uri] = new[] { edit },
        };

        return new CodeAction
        {
            Title = "Remove duplicate #using " + usingNode.Path,
            Kind = CodeActionKind.QuickFix,
            Edit = new WorkspaceEdit { Changes = changes },
        };
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('/', '\\').ToLowerInvariant();
    }

    private static bool Overlaps(TextRange node, TextRange selection)
    {
        return node.Start <= selection.End && selection.Start <= node.End;
    }
}
