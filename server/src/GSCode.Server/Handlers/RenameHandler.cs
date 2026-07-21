using GSCode.Core.Symbols;
using GSCode.Workspace.Database;
using GSCode.Server.Mapping;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using SymbolKind = GSCode.Core.Symbols.SymbolKind;

namespace GSCode.Server.Handlers;

/// <summary>
/// Renames functions, classes, and macros across every reference in the visible context.
/// Because mods never see each other, a rename in one mod can never touch another. Builtins,
/// keywords, and literals are not renameable (prepareRename rejects them).
/// </summary>
public sealed class RenameHandler : RenameHandlerBase
{
    private readonly NavigationSupport _support;
    private readonly TextDocumentSelector _selector;

    public RenameHandler(NavigationSupport support, TextDocumentSelector selector)
    {
        _support = support;
        _selector = selector;
    }

    protected override RenameRegistrationOptions CreateRegistrationOptions(RenameCapability capability, ClientCapabilities clientCapabilities)
    {
        return new RenameRegistrationOptions { DocumentSelector = _selector, PrepareProvider = true };
    }

    public override Task<WorkspaceEdit?> Handle(RenameParams request, CancellationToken cancellationToken)
    {
        NavigationTarget? target = _support.Resolve(request.TextDocument.Uri);
        if ( target is null )
        {
            return Task.FromResult<WorkspaceEdit?>(null);
        }

        PositionHit hit = SymbolAtPosition.Resolve(target.Result, request.Position.ToCore());
        if ( !IsRenameable(hit) )
        {
            return Task.FromResult<WorkspaceEdit?>(null);
        }

        Dictionary<DocumentUri, List<TextEdit>> edits = new();
        // The full visible set: a header macro renamed in GSC alone would leave CSC broken.
        foreach ( (ScriptRecord record, ReferenceEntry entry) in _support.FindAllReferences(target, hit.Key) )
        {
            DocumentUri uri = DocumentUri.FromFileSystemPath(record.Path);
            if ( !edits.TryGetValue(uri, out List<TextEdit>? list) )
            {
                list = [];
                edits[uri] = list;
            }

            list.Add(new TextEdit { Range = entry.Range.ToLsp(), NewText = request.NewName });
        }

        if ( edits.Count == 0 )
        {
            return Task.FromResult<WorkspaceEdit?>(null);
        }

        Dictionary<DocumentUri, IEnumerable<TextEdit>> changes = edits.ToDictionary(
            static pair => pair.Key,
            static pair => (IEnumerable<TextEdit>)pair.Value);

        return Task.FromResult<WorkspaceEdit?>(new WorkspaceEdit { Changes = changes });
    }

    /// <summary>Only script-defined symbols are renameable; builtins/fields/literals are not.</summary>
    internal static bool IsRenameable(PositionHit hit)
    {
        if ( hit.Kind != HitKind.Reference )
        {
            return false;
        }

        return hit.Key.Kind is SymbolKind.Function or SymbolKind.Class or SymbolKind.Macro;
    }
}
