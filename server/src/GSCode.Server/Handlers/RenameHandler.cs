using System.Collections.Immutable;
using GSCode.Parser.Lexing;
using System.Buffers;
using GSCode.Core.Symbols;
using GSCode.Workspace.Api;
using GSCode.Workspace.Database;
using GSCode.Server.Mapping;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Position = GSCode.Core.Text.Position;
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
        NavigationTarget? target = _support.Resolve(request.TextDocument.Uri, cancellationToken);
        if ( target is null )
        {
            return Task.FromResult<WorkspaceEdit?>(null);
        }

        PositionHit hit = _support.ResolveHit(target, request.Position.ToCore());
        if ( !IsRenameable(hit, _builtins.For(target.Language), _objectFields) )
        {
            // A local is always the script's to rename — the engine defines no locals — but it is
            // absent from the reference index, so IsRenameable cannot see it and the AST answers
            // instead. PrepareRenameHandler takes the same fallthrough, so the preview and the
            // rename still cannot disagree about what is allowed.
            return Task.FromResult(RenameLocal(target, request));
        }

        if ( !IsLegalNewName(hit.Key.Kind, request.NewName) )
        {
            return Task.FromResult<WorkspaceEdit?>(null);
        }

        Dictionary<DocumentUri, List<TextEdit>> edits = new();
        // The full visible set: a header macro renamed in GSC alone would leave CSC broken.
        foreach ( (ScriptRecord record, ReferenceEntry entry) in _support.FindAllReferences(target, hit.Key, hit.ReferenceKind) )
        {
            cancellationToken.ThrowIfCancellationRequested();

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
    /// Renames a local across the function that scopes it — one file, and never beyond that body.
    ///
    /// Refused when the function already binds the new name. That case does not fail loudly: it
    /// MERGES two variables into one, and the script keeps running while meaning something else.
    /// In a language where reading an undefined variable is not an error, a silent merge is the
    /// worst shape a refactor can take, so no edit at all is the better answer.
    /// </summary>
    private WorkspaceEdit? RenameLocal(NavigationTarget target, RenameParams request)
    {
        Position position = request.Position.ToCore();

        ImmutableArray<LocalOccurrence> occurrences = _support.LocalOccurrencesAt(target, position);
        if ( occurrences.Length == 0 )
        {
            return null;
        }

        // A local has no SymbolKey and so no kind to ask about — it is always an identifier, so the
        // lexer's rule is asked directly. See IsLegalNewName for why this is refused silently.
        if ( !GscIdentifier.IsIdentifier(request.NewName) )
        {
            return null;
        }

        if ( LocalReferences.BindsName(target.Result, position, request.NewName) )
        {
            return null;
        }

        List<TextEdit> edits = [];
        foreach ( LocalOccurrence occurrence in occurrences )
        {
            edits.Add(new TextEdit { Range = occurrence.Range.ToLsp(), NewText = request.NewName });
        }

        DocumentUri uri = DocumentUri.FromFileSystemPath(target.Path);
        Dictionary<DocumentUri, IEnumerable<TextEdit>> changes = new() { [uri] = edits };

        return new WorkspaceEdit { Changes = changes };
    }

    /// <summary>
    /// Whether the new name is one the scripts can actually carry for a symbol of this kind.
    ///
    /// Nothing checked it. The name arrives from a text box and was written straight into every
    /// reference range, so renaming a function to <c>my func</c> or <c>2fast</c> rewrote the whole
    /// workspace into text that no longer lexes as one token — across as many files as the symbol
    /// reaches, in one undo-less edit. <c>PrepareRenameHandler</c> cannot help: prepare runs before
    /// the name is typed.
    ///
    /// Two rules, because the renameable kinds are not all identifiers. A function, class, macro,
    /// field or local is one, judged by the LEXER's rule so this cannot drift from what would
    /// actually parse. The literals the scripts coin — notify strings, hashes, localized strings,
    /// anim references — are string CONTENT and may hold spaces and punctuation; what they may not
    /// hold is a character that ends the literal early.
    ///
    /// Refused silently, with no edit, for the same reason the name-collision check below is: the
    /// editor asked for a rename and got none, which is recoverable, where half a rename is not.
    /// </summary>
    internal static bool IsLegalNewName(SymbolKind kind, string newName)
    {
        switch ( kind )
        {
            case SymbolKind.StringLiteral:
            case SymbolKind.HashString:
            case SymbolKind.LocalizedString:
            case SymbolKind.AnimReference:
                return newName.Length > 0 && !newName.AsSpan().ContainsAny(s_literalStoppers);

            default:
                return GscIdentifier.IsIdentifier(newName);
        }
    }

    /// <summary>
    /// What a literal's replacement text may not contain: the closing quote, the escape that would
    /// consume it, and the line breaks no literal survives.
    /// </summary>
    private static readonly SearchValues<char> s_literalStoppers = SearchValues.Create("\"\\\r\n");

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

            // A class `var` is declared by the scripts outright, so there is no engine name to
            // collide with the way a field can carry one. Every use of it is in the index even
            // when the hierarchy spans files - see SymbolExtractor's _currentClassHasUnseenAncestor
            // - so there is nothing here to hold a rename back.
            case SymbolKind.Member:
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
