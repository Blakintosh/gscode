using GSCode.Core.Symbols;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Server.Configuration;
using GSCode.Server.Mapping;
using GSCode.Workspace.Documents;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;
using SymbolKind = OmniSharp.Extensions.LanguageServer.Protocol.Models.SymbolKind;
using TextRange = GSCode.Core.Text.TextRange;

namespace GSCode.Server.Handlers;

/// <summary>
/// The hierarchical outline: explicit namespaces → classes → functions → assignments
/// (behind outline.showAssignments), plus macros literally #defined in THIS file
/// (insert-provided ones are excluded via provenance).
/// </summary>
public sealed class DocumentSymbolHandler : DocumentSymbolHandlerBase
{
    private readonly DocumentStore _documents;
    private readonly ServerSettings _settings;
    private readonly TextDocumentSelector _selector;

    public DocumentSymbolHandler(DocumentStore documents, ServerSettings settings, TextDocumentSelector selector)
    {
        _documents = documents;
        _settings = settings;
        _selector = selector;
    }

    protected override DocumentSymbolRegistrationOptions CreateRegistrationOptions(
        DocumentSymbolCapability capability, ClientCapabilities clientCapabilities)
    {
        return new DocumentSymbolRegistrationOptions
        {
            DocumentSelector = _selector,
            Label = "GSCode",
        };
    }

    public override Task<SymbolInformationOrDocumentSymbolContainer?> Handle(
        DocumentSymbolParams request, CancellationToken cancellationToken)
    {
        if ( !_documents.TryGetAnalyzed(
            request.TextDocument.Uri.GetFileSystemPath(), out OpenDocument _, out ParseResult result) )
        {
            return Task.FromResult<SymbolInformationOrDocumentSymbolContainer?>(null);
        }

        List<SymbolInformationOrDocumentSymbol> roots = [];

        // File-local #define macros first (never ones arriving via #insert).
        foreach ( MacroDefinition macro in result.Preprocessed.Macros.All )
        {
            if ( macro.SourceFile is null )
            {
                AddIfNamed(roots, MakeSymbol(macro.Name, SymbolKind.Constant, macro.NameRange.ToLsp(), macro.NameRange.ToLsp(), []));
            }
        }

        // Group top-level declarations by namespace span; only EXPLICIT #namespace
        // directives become container nodes (the file-default span stays flat).
        foreach ( NamespaceSpan namespaceSpan in result.Extraction.Namespaces )
        {
            bool isExplicit = namespaceSpan.NameRange != TextRange.Empty;
            List<SymbolInformationOrDocumentSymbol> children = [];

            foreach ( ClassSymbol classSymbol in result.Extraction.Classes )
            {
                if ( classSymbol.SourceFile.Length == 0
                    && classSymbol.Namespace == namespaceSpan.KeyName
                    && namespaceSpan.GovernedRange.Contains(classSymbol.FullRange.Start) )
                {
                    AddIfNamed(children, MakeClassSymbol(classSymbol));
                }
            }

            foreach ( FunctionSymbol function in result.Extraction.Functions )
            {
                if ( function.SourceFile.Length == 0
                    && function.Namespace == namespaceSpan.KeyName
                    && namespaceSpan.GovernedRange.Contains(function.FullRange.Start) )
                {
                    AddIfNamed(children, MakeFunctionSymbol(function));
                }
            }

            if ( children.Count == 0 )
            {
                continue;
            }

            // An implicit namespace is still a namespace: a file with no #namespace directive
            // belongs to the one named after it, and its functions really do live there. Treating
            // "no directive" as "no namespace" flattened those files to the root, so struct.gsc
            // showed a bare list of functions while its neighbours were grouped.
            //
            // The node needs a selection range that exists in the file, and there is no name to
            // point at, so it selects the first thing it contains.
            LspRange selectionRange = isExplicit
                ? namespaceSpan.NameRange.ToLsp()
                : FirstSelectionRange(children);

            AddIfNamed(roots, MakeSymbol(
                namespaceSpan.Name,
                SymbolKind.Namespace,
                namespaceSpan.GovernedRange.ToLsp(),
                selectionRange,
                children));
        }

        return Task.FromResult<SymbolInformationOrDocumentSymbolContainer?>(
            new SymbolInformationOrDocumentSymbolContainer(roots));
    }

    private SymbolInformationOrDocumentSymbol? MakeFunctionSymbol(FunctionSymbol function)
    {
        List<SymbolInformationOrDocumentSymbol> children = [];

        if ( _settings.OutlineShowAssignments )
        {
            // Show each name once, at its first assignment.
            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach ( AssignmentSymbol assignment in function.Assignments )
            {
                // A loop's own counter is not a symbol anyone navigates to. Every `for` and
                // `foreach` in the file would otherwise contribute an `i`, `key` or `value`,
                // which is what made the outline look like it was listing the loops themselves.
                if ( assignment.IsLoopVariable )
                {
                    continue;
                }

                string display = assignment.OwnerName.Length == 0
                    ? assignment.Name
                    : assignment.OwnerName + "." + assignment.Name;

                if ( seen.Add(display) )
                {
                    AddIfNamed(children, MakeSymbol(display, SymbolKind.Variable, assignment.Range.ToLsp(), assignment.Range.ToLsp(), []));
                }
            }
        }

        return MakeSymbol(
            function.Name,
            SymbolKind.Function,
            function.FullRange.ToLsp(),
            function.NameRange.ToLsp(),
            children);
    }

    private SymbolInformationOrDocumentSymbol? MakeClassSymbol(ClassSymbol classSymbol)
    {
        List<SymbolInformationOrDocumentSymbol> children = [];

        foreach ( MemberSymbol member in classSymbol.Members )
        {
            AddIfNamed(children, MakeSymbol(member.Name, SymbolKind.Field, member.Range.ToLsp(), member.Range.ToLsp(), []));
        }

        foreach ( FunctionSymbol method in classSymbol.Methods )
        {
            AddIfNamed(children, MakeFunctionSymbol(method));
        }

        return MakeSymbol(
            classSymbol.Name,
            SymbolKind.Class,
            classSymbol.FullRange.ToLsp(),
            classSymbol.NameRange.ToLsp(),
            children);
    }

    /// <summary>
    /// A selection range for a node with no name of its own to point at: the first child's.
    /// Clients require the selection range to lie inside the node's full range, so it cannot
    /// simply be left empty.
    /// </summary>
    private static LspRange FirstSelectionRange(
        List<SymbolInformationOrDocumentSymbol> children)
    {
        foreach ( SymbolInformationOrDocumentSymbol child in children )
        {
            if ( child.DocumentSymbol is not null )
            {
                return child.DocumentSymbol.SelectionRange;
            }
        }

        return new LspRange();
    }

    /// <summary>
    /// Builds one outline node, or NULL when it has no name yet.
    ///
    /// The outline is rebuilt on every keystroke, so it sees the file mid-declaration constantly:
    /// the instant `function` is typed there is a function whose name is still empty, and the same
    /// goes for `class` and a bare `#define`. LSP forbids an empty DocumentSymbol name, so those
    /// reached the client as "Request textDocument/documentSymbol failed. Error: name must not be
    /// falsy" — an error toast raised by ordinary typing, and raised for the WHOLE request, so one
    /// half-written declaration took the entire outline with it.
    ///
    /// Nameless is the normal intermediate state of a file being written, not a fault, so it is
    /// filtered here at the single point every node is constructed rather than at each of the six
    /// places nodes are collected — a new collection site would otherwise reintroduce it.
    /// </summary>
    private static SymbolInformationOrDocumentSymbol? MakeSymbol(
        string name,
        SymbolKind kind,
        LspRange fullRange,
        LspRange selectionRange,
        List<SymbolInformationOrDocumentSymbol> children)
    {
        if ( string.IsNullOrWhiteSpace(name) )
        {
            return null;
        }

        DocumentSymbol symbol = new()
        {
            Name = name,
            Kind = kind,
            Range = fullRange,
            SelectionRange = selectionRange,
            Children = new Container<DocumentSymbol>(children.Select(child => child.DocumentSymbol!)),
        };

        return new SymbolInformationOrDocumentSymbol(symbol);
    }

    /// <summary>Adds a node when it exists. A nameless one is dropped, taking nothing else with it.</summary>
    private static void AddIfNamed(
        List<SymbolInformationOrDocumentSymbol> list, SymbolInformationOrDocumentSymbol? symbol)
    {
        if ( symbol is not null )
        {
            list.Add(symbol);
        }
    }
}
