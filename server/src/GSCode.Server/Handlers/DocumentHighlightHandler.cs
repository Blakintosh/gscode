using GSCode.Core.Symbols;
using GSCode.Workspace.Database;
using GSCode.Server.Mapping;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace GSCode.Server.Handlers;

/// <summary>Highlights every occurrence of the symbol under the cursor within the current file.</summary>
public sealed class DocumentHighlightHandler : DocumentHighlightHandlerBase
{
    private readonly NavigationSupport _support;
    private readonly TextDocumentSelector _selector;

    public DocumentHighlightHandler(NavigationSupport support, TextDocumentSelector selector)
    {
        _support = support;
        _selector = selector;
    }

    protected override DocumentHighlightRegistrationOptions CreateRegistrationOptions(DocumentHighlightCapability capability, ClientCapabilities clientCapabilities)
    {
        return new DocumentHighlightRegistrationOptions { DocumentSelector = _selector };
    }

    public override Task<DocumentHighlightContainer?> Handle(DocumentHighlightParams request, CancellationToken cancellationToken)
    {
        NavigationTarget? target = _support.Resolve(request.TextDocument.Uri, cancellationToken);
        if ( target is null )
        {
            return Task.FromResult<DocumentHighlightContainer?>(null);
        }

        PositionHit hit = _support.ResolveHit(target, request.Position.ToCore());
        if ( hit.Kind != HitKind.Reference )
        {
            // Every LOCAL lands here: the reference index is keyed by SymbolKey and shared
            // workspace-wide, so locals are deliberately absent from it and are walked from the AST
            // per function instead. Same fallthrough DefinitionHandler and ReferencesHandler take.
            return Task.FromResult(LocalHighlightsAt(target, request.Position.ToCore()));
        }

        // The shared query, not a raw scan of this file's own Extraction.References: a method hit
        // is keyed by its OWNER at the cursor, and only MethodResolution.Canonicalize (inside the
        // query) widens that to the declaring class, so a raw key comparison misses an inherited
        // method's call sites.
        //
        // Asked for THIS FILE, not asked wide and filtered afterwards: asked wide it builds every
        // location in the workspace — 406,326 in bo3's sampled requests at 50,000 files (PERF.md,
        // the scale section) — on every cursor move. The narrowing is a parameter on the same query,
        // so the key derivation, the method union, the scoping and the shadow rule are the same code.
        List<DocumentHighlight> highlights = [];
        foreach ( (ScriptRecord Record, ReferenceEntry Entry) reference in
            _support.FindReferencesInFile(target, hit.Key, hit.ReferenceKind) )
        {
            cancellationToken.ThrowIfCancellationRequested();

            if ( reference.Record.Path != target.Path )
            {
                continue;
            }

            // A field has no Definition entry — it is declared nowhere — so before the field-write
            // kinds existed every occurrence of `level.x` highlighted as a read, including the
            // `= 1` that set it. A compound update counts too: the editor colours `x += 1` as a
            // write, and the distinction that separates the two kinds is go-to-implementation's,
            // not this one's.
            bool isWrite = reference.Entry.Kind == ReferenceKind.Definition || reference.Entry.IsFieldWrite;

            highlights.Add(new DocumentHighlight
            {
                Range = reference.Entry.Range.ToLsp(),
                Kind = isWrite ? DocumentHighlightKind.Write : DocumentHighlightKind.Read,
            });
        }

        return Task.FromResult<DocumentHighlightContainer?>(new DocumentHighlightContainer(highlights));
    }

    /// <summary>
    /// The local under the cursor, highlighted across its function. Writes are marked as such —
    /// an assignment, a loop binding, a <c>waittill</c> output and the parameter itself all place
    /// a value in the name, and the editor colours those differently from a read.
    /// </summary>
    private DocumentHighlightContainer? LocalHighlightsAt(
        NavigationTarget target, GSCode.Core.Text.Position position)
    {
        List<DocumentHighlight> highlights = [];
        foreach ( LocalOccurrence occurrence in _support.LocalOccurrencesAt(target, position) )
        {
            highlights.Add(new DocumentHighlight
            {
                Range = occurrence.Range.ToLsp(),
                Kind = occurrence.IsWrite ? DocumentHighlightKind.Write : DocumentHighlightKind.Read,
            });
        }

        if ( highlights.Count == 0 )
        {
            return null;
        }

        return new DocumentHighlightContainer(highlights);
    }
}
