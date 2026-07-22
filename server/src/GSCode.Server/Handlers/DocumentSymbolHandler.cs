using GSCode.Core.Symbols;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Server.Configuration;
using GSCode.Server.Mapping;
using GSCode.Workspace.Documents;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using SymbolKind = OmniSharp.Extensions.LanguageServer.Protocol.Models.SymbolKind;

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
        if ( !_documents.TryGet(request.TextDocument.Uri.GetFileSystemPath(), out OpenDocument document)
            || document.LatestResult is null )
        {
            return Task.FromResult<SymbolInformationOrDocumentSymbolContainer?>(null);
        }

        ParseResult result = document.LatestResult;
        List<SymbolInformationOrDocumentSymbol> roots = [];

        // File-local #define macros first (never ones arriving via #insert).
        foreach ( MacroDefinition macro in result.Preprocessed.Macros.All )
        {
            if ( macro.SourceFile is null )
            {
                roots.Add(MakeSymbol(macro.Name, SymbolKind.Constant, macro.NameRange.ToLsp(), macro.NameRange.ToLsp(), []));
            }
        }

        // Group top-level declarations by namespace span; only EXPLICIT #namespace
        // directives become container nodes (the file-default span stays flat).
        foreach ( NamespaceSpan namespaceSpan in result.Extraction.Namespaces )
        {
            bool isExplicit = namespaceSpan.NameRange != GSCode.Core.Text.TextRange.Empty;
            List<SymbolInformationOrDocumentSymbol> children = [];

            foreach ( ClassSymbol classSymbol in result.Extraction.Classes )
            {
                if ( classSymbol.SourceFile.Length == 0
                    && classSymbol.Namespace == namespaceSpan.KeyName
                    && namespaceSpan.GovernedRange.Contains(classSymbol.FullRange.Start) )
                {
                    children.Add(MakeClassSymbol(classSymbol));
                }
            }

            foreach ( FunctionSymbol function in result.Extraction.Functions )
            {
                if ( function.SourceFile.Length == 0
                    && function.Namespace == namespaceSpan.KeyName
                    && namespaceSpan.GovernedRange.Contains(function.FullRange.Start) )
                {
                    children.Add(MakeFunctionSymbol(function));
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
            OmniSharp.Extensions.LanguageServer.Protocol.Models.Range selectionRange = isExplicit
                ? namespaceSpan.NameRange.ToLsp()
                : FirstSelectionRange(children);

            roots.Add(MakeSymbol(
                namespaceSpan.Name,
                SymbolKind.Namespace,
                namespaceSpan.GovernedRange.ToLsp(),
                selectionRange,
                children));
        }

        return Task.FromResult<SymbolInformationOrDocumentSymbolContainer?>(
            new SymbolInformationOrDocumentSymbolContainer(roots));
    }

    private SymbolInformationOrDocumentSymbol MakeFunctionSymbol(FunctionSymbol function)
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
                    children.Add(MakeSymbol(display, SymbolKind.Variable, assignment.Range.ToLsp(), assignment.Range.ToLsp(), []));
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

    private SymbolInformationOrDocumentSymbol MakeClassSymbol(ClassSymbol classSymbol)
    {
        List<SymbolInformationOrDocumentSymbol> children = [];

        foreach ( MemberSymbol member in classSymbol.Members )
        {
            children.Add(MakeSymbol(member.Name, SymbolKind.Field, member.Range.ToLsp(), member.Range.ToLsp(), []));
        }

        foreach ( FunctionSymbol method in classSymbol.Methods )
        {
            children.Add(MakeFunctionSymbol(method));
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
    private static OmniSharp.Extensions.LanguageServer.Protocol.Models.Range FirstSelectionRange(
        List<SymbolInformationOrDocumentSymbol> children)
    {
        foreach ( SymbolInformationOrDocumentSymbol child in children )
        {
            if ( child.DocumentSymbol is not null )
            {
                return child.DocumentSymbol.SelectionRange;
            }
        }

        return new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range();
    }

    private static SymbolInformationOrDocumentSymbol MakeSymbol(
        string name,
        SymbolKind kind,
        OmniSharp.Extensions.LanguageServer.Protocol.Models.Range fullRange,
        OmniSharp.Extensions.LanguageServer.Protocol.Models.Range selectionRange,
        List<SymbolInformationOrDocumentSymbol> children)
    {
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
}
