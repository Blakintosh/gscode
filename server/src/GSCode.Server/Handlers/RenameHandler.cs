using GSCode.Core.Symbols;
using GSCode.Workspace.Api;
using GSCode.Workspace.Database;
using GSCode.Server.Mapping;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using SymbolKind = GSCode.Core.Symbols.SymbolKind;

namespace GSCode.Server.Handlers;

/// <summary>
/// Renames anything the SCRIPTS define, across every reference in the visible context: functions,
/// classes, macros, their own fields, and the string/hash/anim literals they coin. Because mods
/// never see each other, a rename in one mod can never touch another. What the ENGINE defines -
/// builtins and engine fields - and the keywords are rejected by prepareRename.
/// </summary>
public sealed class RenameHandler : RenameHandlerBase
{
    private readonly NavigationSupport _support;
    private readonly BuiltinApiSet _builtins;
    private readonly ObjectFields _objectFields;
    private readonly TextDocumentSelector _selector;

    public RenameHandler(
        NavigationSupport support, BuiltinApiSet builtins, ObjectFields objectFields, TextDocumentSelector selector)
    {
        _support = support;
        _builtins = builtins;
        _objectFields = objectFields;
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
        if ( !IsRenameable(hit, _builtins.For(target.Language), _objectFields) )
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

    /// <summary>
    /// Whether the thing under the cursor is the SCRIPT'S to rename.
    ///
    /// The line is ownership, not kind. Anything the scripts define — functions, classes, macros,
    /// their own fields, and the string/hash/anim literals they coin — can be renamed, because
    /// every occurrence is in the workspace and the edit is complete. Anything the ENGINE defines
    /// cannot: renaming <c>GetTime</c> or <c>.origin</c> would rewrite the call sites while the
    /// engine kept the old name, turning working code into code that silently resolves to nothing.
    ///
    /// Restricting it to Function/Class/Macro was a cruder version of the same idea — it excluded
    /// the engine, but took the scripts' own fields and literals with it, and a notify string is
    /// exactly the kind of name worth renaming everywhere at once.
    /// </summary>
    internal static bool IsRenameable(PositionHit hit, BuiltinApi builtins, ObjectFields objectFields)
    {
        if ( hit.Kind != HitKind.Reference )
        {
            return false;
        }

        switch ( hit.Key.Kind )
        {
            case SymbolKind.Function:
                // A builtin call is keyed as a Function like any other, so the library is what
                // tells them apart.
                return builtins.Find(hit.Key.Name) is null;

            case SymbolKind.Field:
                // An engine field is the engine's name in the same way a builtin is.
                return objectFields.FindField(hit.Key.Name).Length == 0;

            case SymbolKind.Class:
            case SymbolKind.Macro:
            case SymbolKind.StringLiteral:
            case SymbolKind.HashString:
            case SymbolKind.LocalizedString:
            case SymbolKind.AnimReference:
                return true;

            default:
                return false;
        }
    }
}
