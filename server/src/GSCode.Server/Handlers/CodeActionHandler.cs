using GSCode.Core;
using System.Collections.Immutable;
using GSCode.Core.Diagnostics;
using GSCode.Core.Docs;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Lexing;
using GSCode.Parser.Syntax.Ast;
using GSCode.Workspace.Database;
using GSCode.Workspace.Documents;
using GSCode.Server.Mapping;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using LspDiagnostic = OmniSharp.Extensions.LanguageServer.Protocol.Models.Diagnostic;
using Position = GSCode.Core.Text.Position;
using ClassSymbol = GSCode.Core.Symbols.ClassSymbol;
using FunctionSymbol = GSCode.Core.Symbols.FunctionSymbol;
using ReferenceEntry = GSCode.Core.Symbols.ReferenceEntry;
using ReferenceKind = GSCode.Core.Symbols.ReferenceKind;
using SymbolKind = GSCode.Core.Symbols.SymbolKind;
using GSCode.Core.Paths;
using Diagnostic = GSCode.Core.Diagnostics.Diagnostic;

namespace GSCode.Server.Handlers;

/// <summary>
/// Quick fixes over the open document: "Remove duplicate #using" for a redundant import,
/// "Add #using ..." for a qualified call whose defining file the document does not yet import
/// (the natural fix for the NamespaceNotImported lint), and for a call that resolved to nothing
/// (5013/5014) either declaring the function here or importing and qualifying it from wherever it
/// does exist.
/// </summary>
public sealed class CodeActionHandler : CodeActionHandlerBase
{
    private readonly DocumentStore _documents;
    private readonly NavigationSupport _support;
    private readonly DocumentLinter _linter;
    private readonly TextDocumentSelector _selector;

    public CodeActionHandler(DocumentStore documents, NavigationSupport support, DocumentLinter linter, TextDocumentSelector selector)
    {
        _documents = documents;
        _support = support;
        _linter = linter;
        _selector = selector;
    }

    protected override CodeActionRegistrationOptions CreateRegistrationOptions(
        CodeActionCapability capability, ClientCapabilities clientCapabilities)
    {
        return new CodeActionRegistrationOptions
        {
            DocumentSelector = _selector,

            // Every kind this handler can produce has to be named here. VS Code's "Source
            // Action..." and "Refactor..." context-menu entries ask the client's own capability
            // negotiation whether a server offers that KIND before ever sending a request — with
            // only QuickFix registered, both menus showed nothing to pick, not an empty result
            // from an actual request.
            //
            // Refactor is here for "Generate ScriptDoc block", which cannot be a QuickFix: a quick
            // fix with no diagnostic behind it is never presented as the fix FOR anything, and
            // there is no missing-documentation diagnostic — nor should there be, since it would
            // fire on the thousands of undocumented functions the stock scripts ship.
            CodeActionKinds = new Container<CodeActionKind>(
                CodeActionKind.QuickFix, CodeActionKind.Refactor, CodeActionKind.SourceOrganizeImports),
        };
    }

    // No lazy resolution — actions carry their edit up front.
    public override Task<CodeAction> Handle(CodeAction request, CancellationToken cancellationToken)
    {
        return Task.FromResult(request);
    }

    public override Task<CommandOrCodeActionContainer?> Handle(CodeActionParams request, CancellationToken cancellationToken)
    {
        if ( !_documents.TryAnalyzeFresh(
            request.TextDocument.Uri.GetFileSystemPath(), cancellationToken, out OpenDocument document, out ParseResult result) )
        {
            return Task.FromResult<CommandOrCodeActionContainer?>(null);
        }

        TextRange selection = request.Range.ToCore();
        List<CommandOrCodeAction> actions = [];

        // The two consumers of the lint pass below share one run of it. See RequestLints.
        RequestLints lints = new(_linter, document, result);

        foreach ( RedundantImport duplicate in FindRemovableDuplicates(result, selection) )
        {
            actions.Add(new CommandOrCodeAction(BuildRemoveAction(
                request.TextDocument.Uri,
                duplicate,
                ReportedAt(request, GscDiagnosticCode.DuplicateImport, duplicate.Range))));
        }

        NavigationTarget? target = _support.Resolve(request.TextDocument.Uri, cancellationToken);
        if ( target is not null )
        {
            Position insertAt = ImportEdits.InsertionPoint<UsingNode>(result);
            List<MissingUsing> missing = FindMissingUsingSites(result, target.Store, target.ContextId, target.Path, selection);

            // How many imports could serve each call site. One means the fix is unambiguous and can
            // be marked preferred, which is what Auto Fix runs; several means the user has to pick,
            // and preferring an arbitrary one of them would be a guess dressed up as an answer.
            Dictionary<TextRange, int> candidates = [];
            foreach ( MissingUsing site in missing )
            {
                candidates[site.Range] = candidates.GetValueOrDefault(site.Range) + 1;
            }

            foreach ( MissingUsing site in missing )
            {
                actions.Add(new CommandOrCodeAction(BuildAddUsingAction(
                    request.TextDocument.Uri,
                    site.Path,
                    insertAt,
                    ReportedAt(request, GscDiagnosticCode.NamespaceNotImported, site.Range),
                    candidates[site.Range] == 1)));
            }
        }

        AddDiagnosticFixes(request, result, actions, DiagnosticsForFixes(request, lints, cancellationToken), target);

        AddGenerateScriptDocAction(request.TextDocument.Uri, result, selection, actions);

        // Same TriggerKind gate as DiagnosticsForFixes: VS Code never polls a Source Action
        // request the way it polls QuickFix for the lightbulb, so this only ever runs on an
        // explicit ask — but the gate is kept anyway rather than assumed, for the same
        // defend-against-an-unusual-client reason DiagnosticsForFixes keeps its own.
        if ( (request.Context.TriggerKind ?? CodeActionTriggerKind.Invoked) == CodeActionTriggerKind.Invoked )
        {
            AddOrganizeImportsAction(
                request.TextDocument.Uri, AllUnusedImportDiagnostics(lints, cancellationToken), actions);
        }

        return Task.FromResult<CommandOrCodeActionContainer?>(new CommandOrCodeActionContainer(actions));
    }

    /// <summary>
    /// Diagnostics on the request's LINE, so "Quick Fix..." from the right-click context menu
    /// finds a fix even when the CURSOR (what that command anchors on) sits somewhere else on the
    /// same line than the squiggle.
    ///
    /// <c>request.Context.Diagnostics</c> is exactly what VS Code computed overlapped the request's
    /// own range, and is used as-is whenever it is non-empty — this never second-guesses a set the
    /// client actually sent, so the lightbulb, hover "Quick Fix" and the Problems panel (each
    /// already anchored ON a marker) see no change at all. It is empty specifically for the
    /// cursor-not-on-the-squiggle case, and that is the one this exists for: the workspace lints
    /// (unused #using, PreferBooleanLiteral, an unresolved call, …) are not recomputable from the
    /// ParseResult alone — they run in TextSyncHandler/DocumentLinter — so answering the same
    /// question the client would have takes running that same pipeline here, once, and keeping only
    /// what lands on this line.
    /// </summary>
    private static IEnumerable<LspDiagnostic> DiagnosticsForFixes(
        CodeActionParams request, RequestLints lints, CancellationToken cancellationToken)
    {
        if ( request.Context.Diagnostics.Any() )
        {
            return request.Context.Diagnostics;
        }

        // Automatic requests are VS Code polling for the LIGHTBULB, not a user action — it fires
        // on every cursor move/idle pause to decide whether to show the icon at all, far more
        // often than anyone actually opens the menu. Running a full relint for each one turned
        // routine cursor movement into continuous background work for no visible benefit: an
        // automatic request with nothing in Context.Diagnostics already means no lightbulb shows
        // there, and the fallback below exists for the INVOKED case — the context menu's own
        // "Quick Fix..." — where the cost is paid once, on an explicit click. Default to Invoked
        // when the client sends no TriggerKind at all, so an older/other client that never sends
        // one keeps getting the fallback rather than silently losing it.
        if ( (request.Context.TriggerKind ?? CodeActionTriggerKind.Invoked) != CodeActionTriggerKind.Invoked )
        {
            return request.Context.Diagnostics;
        }

        // The pass already returns result.AllDiagnostics plus the cross-file lints layered on top
        // (WorkspaceLints.Analyze's own doc: "the file's own diagnostics plus every cross-file
        // lint"), so this is the one set that needs asking for, not two — a second pass over
        // result.AllDiagnostics on its own duplicated every parser-level diagnostic here.
        int requestedLine = request.Range.Start.Line;
        List<LspDiagnostic> onThisLine = [];

        foreach ( Diagnostic diagnostic in lints.All(cancellationToken) )
        {
            // LINE MEMBERSHIP, not TextRange.Overlaps: Overlaps compares positions inclusively
            // (Start <= other.End), so a range built from LineRangeOf's (line, 0)-(line+1, 0) —
            // meant for a whole-line DELETE edit, where touching the next line's start is exactly
            // the point — touches a diagnostic that starts at column 0 of the FOLLOWING line too.
            // Asking whether the line sits between a diagnostic's own start and end line avoids
            // that boundary entirely.
            if ( diagnostic.Range.Start.Line <= requestedLine && requestedLine <= diagnostic.Range.End.Line )
            {
                onThisLine.Add(diagnostic.ToLsp());
            }
        }

        return onThisLine;
    }

    /// <summary>
    /// Fixes driven by the diagnostics the client reported for the selection, or
    /// <see cref="DiagnosticsForFixes"/>'s line-scoped recomputation of them.
    /// </summary>
    /// <param name="target">
    /// The workspace view, when there is one. Only the unresolved-call fixes need it — everything
    /// else here is answerable from the document alone — so it is optional rather than a
    /// precondition, and those fixes simply go unoffered on a document with no indexed context.
    /// </param>
    internal static void AddDiagnosticFixes(
        CodeActionParams request,
        ParseResult result,
        List<CommandOrCodeAction> actions,
        NavigationTarget? target = null)
    {
        AddDiagnosticFixes(request, result, actions, request.Context.Diagnostics, target);
    }

    /// <summary>
    /// Same as the four-argument overload, but over an EXPLICIT diagnostic list rather than
    /// <c>request.Context.Diagnostics</c> — see <see cref="DiagnosticsForFixes"/>, the only caller
    /// that needs the difference.
    /// </summary>
    internal static void AddDiagnosticFixes(
        CodeActionParams request,
        ParseResult result,
        List<CommandOrCodeAction> actions,
        IEnumerable<LspDiagnostic> diagnostics,
        NavigationTarget? target = null)
    {
        DocumentUri uri = request.TextDocument.Uri;

        // One per request, so the two call fixes below answer the questions that do not vary within
        // it exactly once between them however many diagnostics the selection carries.
        CallFixContext context = new(
            result, target?.Store, target?.ContextId ?? "", target?.Path ?? "");

        // Tracked per directive. No dialect has both — #include is gated by import style — but the
        // bulk action names the directive in its title, and one shared list would let a CoD4 user
        // be offered "Remove all 3 unused #using directives".
        List<LspDiagnostic> unusedUsings = [];
        List<LspDiagnostic> unusedIncludes = [];

        foreach ( LspDiagnostic diagnostic in diagnostics )
        {
            switch ( CodeOf(diagnostic) )
            {
                case GscDiagnosticCode.UnusedUsing:
                    unusedUsings.Add(diagnostic);
                    actions.Add(new CommandOrCodeAction(BuildDeleteLineAction(uri, result, diagnostic)));
                    continue;

                // The merge dialects' counterpart, reported by UnusedIncludeLint. It had no fix at
                // all: the switch knew only about #using, so on CoD4, WaW, MW2 and BO1 an unused
                // import was greyed out with nothing offered to remove it.
                case GscDiagnosticCode.UnusedInclude:
                    unusedIncludes.Add(diagnostic);
                    actions.Add(new CommandOrCodeAction(BuildDeleteLineAction(uri, result, diagnostic)));
                    continue;
                case GscDiagnosticCode.PreferBooleanLiteral:
                    AddBooleanLiteralFix(uri, result, diagnostic, actions);
                    continue;
                case GscDiagnosticCode.UsingAfterDeclaration:
                    AddMoveUsingFix(uri, result, diagnostic, actions);
                    continue;

                // Both halves of an unresolved call get the same two offers, because from the fix's
                // point of view they are one situation: a name with no definition behind it. Which
                // code fired says where we LOOKED, not what the user should do about it.
                case GscDiagnosticCode.ScriptFunctionNotFound:
                case GscDiagnosticCode.BuiltinFunctionNotFound:
                    foreach ( CodeAction fix in UnresolvedCallFixes(uri, context, diagnostic) )
                    {
                        actions.Add(new CommandOrCodeAction(fix));
                    }

                    continue;

                // The merge dialects' missing-import case (5026). Separate from the two above
                // because the situation is the opposite one: the function is KNOWN, and the only
                // sensible offer is the import that brings it into scope. Creating a second copy of
                // a function that already exists is exactly what a user must not be nudged into.
                case GscDiagnosticCode.FunctionNotIncluded:
                    foreach ( CodeAction fix in MissingIncludeFixes(uri, context, diagnostic) )
                    {
                        actions.Add(new CommandOrCodeAction(fix));
                    }

                    continue;
                default:
                    continue;
            }
        }

        // Organize Imports is NOT built here — see BuildOrganizeImportsAction's own call site in
        // Handle(). This method only ever sees whatever unusedUsings/unusedIncludes the REQUEST's
        // own diagnostics carried (the client's selection, or DiagnosticsForFixes' one-line
        // fallback), and "Organize Imports" is a whole-DOCUMENT command by convention — scoping it
        // to wherever the cursor happened to be when the menu opened read as "1 unused" on a file
        // that actually had several.

        // One click for the common cleanup, rather than N separate fixes.
        AddRemoveAllUnusedAction(uri, unusedUsings, "#using", actions);
        AddRemoveAllUnusedAction(uri, unusedIncludes, "#include", actions);
    }

    /// <summary>
    /// The document's lint pass, run at most once per request and only if something asks for it.
    ///
    /// Two things in one request want it, for different slices of the same answer:
    /// <see cref="DiagnosticsForFixes"/> keeps what lands on the request's LINE, and
    /// <see cref="AllUnusedImportDiagnostics"/> keeps the unused imports in the WHOLE document.
    /// Both used to call <see cref="DocumentLinter.Analyze(OpenDocument, ParseResult, CancellationToken)"/> themselves, so an invoked "Quick
    /// Fix..." with nothing in <c>Context.Diagnostics</c> ran twenty-six cross-file rules and a
    /// whole-file flow pass TWICE over one unchanged document — and the second run could not even
    /// disagree with the first, which is what makes sharing it safe rather than a trade.
    ///
    /// Lazy, because the common request asks for neither: an AUTOMATIC request (VS Code polling for
    /// the lightbulb on every cursor move) returns before either consumer runs, and must keep
    /// costing nothing.
    ///
    /// Per request and dropped with it, deliberately — the same rule
    /// <see cref="CallFixContext"/> states for itself. A cache that outlived the request could
    /// answer for a buffer that has since been edited.
    /// </summary>
    internal sealed class RequestLints
    {
        private readonly DocumentLinter _linter;
        private readonly OpenDocument _document;
        private readonly ParseResult _result;

        private ImmutableArray<Diagnostic>? _all;

        public RequestLints(DocumentLinter linter, OpenDocument document, ParseResult result)
        {
            _linter = linter;
            _document = document;
            _result = result;
        }

        /// <summary>Every diagnostic the pass reports, in the order it reports them.</summary>
        public ImmutableArray<Diagnostic> All(CancellationToken cancellationToken)
        {
            if ( _all is ImmutableArray<Diagnostic> already )
            {
                return already;
            }

            ImmutableArray<Diagnostic> analyzed =
                [.. _linter.Analyze(_document, _result, cancellationToken)];

            _all = analyzed;
            return analyzed;
        }
    }

    /// <summary>
    /// The work the two call fixes need that does not vary WITHIN one code-action request, computed
    /// at most once and reused by every diagnostic in it.
    ///
    /// A request carries every diagnostic overlapping the selection, so twenty unresolved calls on a
    /// multi-line selection meant twenty scans of <c>store.AllRecords</c> — thousands of records,
    /// tens of thousands of function symbols — and forty walks of the directive list, to answer
    /// questions whose answers were identical every time.
    ///
    /// The name lookup is cached even when it finds NOTHING, which is the common case here: these
    /// fixes exist for names that did not resolve, and repeating a fruitless full scan is the most
    /// expensive way to learn the same thing twice.
    ///
    /// Deliberately not a cache with a lifetime. It is built per request and dropped with it, so it
    /// cannot go stale against an edited buffer — the failure mode a longer-lived one would invite.
    /// </summary>
    internal sealed class CallFixContext
    {
        private readonly Dictionary<string, ImmutableArray<ResolvedFunction>> _declaring =
            new(StringComparer.OrdinalIgnoreCase);

        private ImmutableArray<string>? _includedPaths;
        private HashSet<string>? _existingUsings;
        private Position? _usingInsertAt;
        private Position? _includeInsertAt;

        public CallFixContext(
            ParseResult result,
            LanguageStore? store,
            string contextId,
            string askingPath,
            GameProfile? profile = null)
        {
            Result = result;
            Store = store;
            ContextId = contextId;
            AskingPath = askingPath;
            Profile = profile ?? GameProfile.Active;
        }

        public ParseResult Result { get; }
        public LanguageStore? Store { get; }
        public string ContextId { get; }
        public string AskingPath { get; }
        public GameProfile Profile { get; }

        /// <summary>
        /// Every visible declaration of a name, searched across all namespaces — which is the point
        /// for both callers, since each already knows the location that was written does not have it.
        /// </summary>
        public ImmutableArray<ResolvedFunction> Declaring(string name)
        {
            if ( Store is null )
            {
                return [];
            }

            if ( !_declaring.TryGetValue(name, out ImmutableArray<ResolvedFunction> found) )
            {
                found = DatabaseQueries.LookupFunctions(
                    Store, ContextId, AskingPath, null, NameTable.Shared.InternLower(name));

                _declaring[name] = found;
            }

            return found;
        }

        public ImmutableArray<string> IncludedPaths
        {
            get { return _includedPaths ??= DatabaseQueries.IncludedScriptPaths(Result); }
        }

        public HashSet<string> ExistingUsings
        {
            get { return _existingUsings ??= GatherUsings(Result); }
        }

        public Position UsingInsertAt
        {
            get { return _usingInsertAt ??= ImportEdits.InsertionPoint<UsingNode>(Result); }
        }

        public Position IncludeInsertAt
        {
            get { return _includeInsertAt ??= ImportEdits.InsertionPoint<IncludeNode>(Result); }
        }

        /// <summary>
        /// The extensionless paths the file already imports with <c>#using</c>.
        /// </summary>
        /// <remarks>
        /// Shared with <see cref="FindMissingUsingSites"/>, which asks the same question of the same
        /// tree. Two answers to "is this path already imported" is two chances to offer an import
        /// that is already there.
        /// </remarks>
        internal static HashSet<string> GatherUsings(ParseResult result)
        {
            HashSet<string> paths = new(StringComparer.Ordinal);
            foreach ( AstNode element in result.Tree.Root.Elements )
            {
                if ( element is UsingNode usingNode )
                {
                    paths.Add(ImportEdits.PathOf(usingNode.Path));
                }
            }

            return paths;
        }
    }

    private static void AddRemoveAllUnusedAction(
        DocumentUri uri,
        List<LspDiagnostic> unused,
        string directive,
        List<CommandOrCodeAction> actions)
    {
        if ( unused.Count > 1 )
        {
            actions.Add(new CommandOrCodeAction(BuildRemoveAllUnusedAction(uri, unused, directive)));
        }
    }

    /// <summary>
    /// The one SourceOrganizeImports action: every unused #using/#include it is GIVEN, in a single
    /// edit, with no scoping of its own — the file-wide-versus-current-line distinction lives
    /// entirely in what <see cref="AllUnusedImportDiagnostics"/> gathers (<c>internal</c> so a test
    /// can pin that). Offered whenever there is at least one, unlike the QuickFix bulk action, which
    /// needs two because a lone unused import already has its own per-line fix: Organize Imports is
    /// the command a user reaches for regardless of count.
    /// </summary>
    internal static void AddOrganizeImportsAction(
        DocumentUri uri,
        IReadOnlyCollection<LspDiagnostic> unused,
        List<CommandOrCodeAction> actions)
    {
        if ( unused.Count == 0 )
        {
            return;
        }

        List<TextEdit> edits = LineDeletions(unused);

        actions.Add(new CommandOrCodeAction(BuildAction(
            "Organize imports (remove " + edits.Count + " unused)",
            uri,
            edits,
            new Container<LspDiagnostic>(unused),
            CodeActionKind.SourceOrganizeImports)));
    }

    /// <summary>
    /// Every unused <c>#using</c>/<c>#include</c> in the WHOLE document, regardless of the
    /// request's own range — "Organize Imports" is a whole-file command by convention, unlike
    /// every other fix in this handler, which only ever needs to answer for the requested
    /// selection. Runs the same DocumentLinter pipeline <see cref="DiagnosticsForFixes"/> falls
    /// back to; the two are not combined into one call because that fallback is deliberately
    /// scoped to one LINE and this deliberately is not.
    /// </summary>
    private static ImmutableArray<LspDiagnostic> AllUnusedImportDiagnostics(
        RequestLints lints, CancellationToken cancellationToken)
    {
        ImmutableArray<LspDiagnostic>.Builder unused = ImmutableArray.CreateBuilder<LspDiagnostic>();

        foreach ( Diagnostic diagnostic in lints.All(cancellationToken) )
        {
            LspDiagnostic converted = diagnostic.ToLsp();
            GscDiagnosticCode? code = CodeOf(converted);
            if ( code is GscDiagnosticCode.UnusedUsing or GscDiagnosticCode.UnusedInclude )
            {
                unused.Add(converted);
            }
        }

        return unused.ToImmutable();
    }

    private static GscDiagnosticCode? CodeOf(LspDiagnostic diagnostic)
    {
        if ( diagnostic.Code is null || !diagnostic.Code.Value.IsLong )
        {
            return null;
        }

        return (GscDiagnosticCode)diagnostic.Code.Value.Long;
    }

    /// <summary>The whole line a range sits on, including its trailing newline.</summary>
    private static TextRange LineRangeOf(TextRange range)
    {
        return new TextRange(new Position(range.Start.Line, 0), new Position(range.Start.Line + 1, 0));
    }

    /// <summary>The trimmed source of a line, used to name an action after what it acts on.</summary>
    private static string LineTextOf(ParseResult result, int line)
    {
        if ( line < 0 || line >= result.Text.LineCount )
        {
            return "";
        }

        int start = result.Text.GetOffset(new Position(line, 0));
        int end = line + 1 < result.Text.LineCount
            ? result.Text.GetOffset(new Position(line + 1, 0))
            : result.Text.Length;

        return result.Text.Text[start..end].Trim().TrimEnd(SemiColon);
    }

    private const char SemiColon = ';';

    private static CodeAction BuildDeleteLineAction(DocumentUri uri, ParseResult result, LspDiagnostic diagnostic)
    {
        TextRange range = diagnostic.Range.ToCore();
        TextEdit edit = new() { Range = LineRangeOf(range).ToLsp(), NewText = "" };

        // Preferred: the answer for the import under the cursor, and there is only one — so this is
        // what Auto Fix should take, not the bulk sweep sitting next to it.
        return QuickFix(
            "Remove unused " + LineTextOf(result, range.Start.Line), uri, [edit], diagnostic, preferred: true);
    }

    /// <summary>
    /// One edit removing every unused import of a kind, bound to ALL of the diagnostics it clears.
    ///
    /// Binding it to all of them is what makes it reachable: an action carrying no diagnostics only
    /// appears in the general lightbulb, so the bulk cleanup was invisible from the very squiggles
    /// it exists to clear. It therefore shows on each of those lines beside that line's own single
    /// removal, which is the same pair TypeScript offers for an unused declaration.
    ///
    /// Never preferred, though. Auto Fix runs preferred actions on the diagnostic under the cursor,
    /// and the answer there is to remove THAT import — deleting the other six as a side effect of
    /// fixing one is not what was asked for. The single-line removal carries the preference.
    /// </summary>
    private static CodeAction BuildRemoveAllUnusedAction(
        DocumentUri uri,
        List<LspDiagnostic> unused,
        string directive)
    {
        List<TextEdit> edits = LineDeletions(unused);

        return QuickFix(
            "Remove all " + edits.Count + " unused " + directive + " directives",
            uri,
            edits,
            new Container<LspDiagnostic>(unused));
    }

    /// <summary>
    /// One whole-line deletion per distinct line the unused-import diagnostics sit on — the edit set
    /// both Organize Imports and "Remove all unused" apply, so the two cannot disagree about what
    /// removing every unused import means. Whole-line deletions on distinct lines never overlap, so
    /// order does not matter.
    /// </summary>
    private static List<TextEdit> LineDeletions(IEnumerable<LspDiagnostic> unused)
    {
        HashSet<int> lines = [];
        List<TextEdit> edits = [];
        foreach ( LspDiagnostic diagnostic in unused )
        {
            TextRange range = diagnostic.Range.ToCore();
            if ( lines.Add(range.Start.Line) )
            {
                edits.Add(new TextEdit { Range = LineRangeOf(range).ToLsp(), NewText = "" });
            }
        }

        return edits;
    }

    /// <summary>
    /// Replaces a literal 0/1 with false/true. The replacement is read from the source at the
    /// diagnostic's range rather than parsed out of its message, so the fix cannot drift if the
    /// wording ever changes.
    /// </summary>
    private static void AddBooleanLiteralFix(DocumentUri uri, ParseResult result, LspDiagnostic diagnostic, List<CommandOrCodeAction> actions)
    {
        TextRange range = diagnostic.Range.ToCore();
        int start = result.Text.GetOffset(range.Start);
        int end = result.Text.GetOffset(range.End);
        if ( start >= end || end > result.Text.Length )
        {
            return;
        }

        string literal = result.Text.Text[start..end].Trim();
        string replacement = "";
        if ( literal == "1" )
        {
            replacement = "true";
        }
        else if ( literal == "0" )
        {
            replacement = "false";
        }

        if ( replacement.Length == 0 )
        {
            return;
        }

        TextEdit edit = new() { Range = range.ToLsp(), NewText = replacement };
        actions.Add(new CommandOrCodeAction(
            QuickFix("Replace " + literal + " with " + replacement, uri, [edit], diagnostic)));
    }

    /// <summary>
    /// Moves a #using that appears after the first declaration up to where imports belong. Two
    /// edits — delete the offending line, insert it at the top — applied as one operation.
    /// </summary>
    private static void AddMoveUsingFix(DocumentUri uri, ParseResult result, LspDiagnostic diagnostic, List<CommandOrCodeAction> actions)
    {
        TextRange range = diagnostic.Range.ToCore();
        string directive = LineTextOf(result, range.Start.Line);
        if ( directive.Length == 0 )
        {
            return;
        }

        Position insertAt = ImportEdits.InsertionPoint<UsingNode>(result, range.Start.Line);
        if ( insertAt.Line >= range.Start.Line )
        {
            // Nowhere earlier to move it to; leave the diagnostic without a fix.
            return;
        }

        List<TextEdit> edits =
        [
            new TextEdit { Range = LineRangeOf(range).ToLsp(), NewText = "" },
            new TextEdit { Range = new TextRange(insertAt, insertAt).ToLsp(), NewText = directive + ";" + "\n" },
        ];

        // Preferred: there is one way to fix a misplaced directive, so Auto Fix can take it.
        actions.Add(new CommandOrCodeAction(QuickFix(
            "Move " + directive + " above the first declaration", uri, edits, diagnostic, preferred: true)));
    }

    /// <summary>
    /// The offers for a call that resolved to nothing (5013/5014): declare the function here, and —
    /// where the name DOES exist somewhere the file cannot currently see — import that file and
    /// qualify the call so it reaches it.
    ///
    /// The diagnostic's range covers the NAME TOKEN only, never the <c>ns::</c> in front of it (see
    /// <c>SymbolExtractor.RecordCalleeReference</c>, which records the reference against
    /// <c>NameToken</c>). That is what makes both edits simple: qualifying is an insert at the range
    /// start, and re-qualifying replaces the qualifier found by scanning back from it.
    /// </summary>
    internal static List<CodeAction> UnresolvedCallFixes(
        DocumentUri uri,
        CallFixContext context,
        LspDiagnostic diagnostic)
    {
        ParseResult result = context.Result;
        List<CodeAction> fixes = [];
        TextRange range = diagnostic.Range.ToCore();
        string name = TextAt(result, range);
        // The lexer's own rule: a stale diagnostic range against an edited buffer is how a fix
        // would otherwise write a declaration out of something that is not a name.
        if ( name.Length == 0 || !GscIdentifier.IsIdentifier(name) )
        {
            return fixes;
        }

        TextRange? qualifier = QualifierRange(result, range);

        // Declaring it HERE only answers a call that named no other location. `other::foo()` says
        // where it expects to find the function, and writing foo into this file would not put it
        // there — that fix would have to edit the other file, which is a different operation and a
        // different set of ways to be wrong.
        if ( qualifier is null )
        {
            fixes.Add(BuildCreateFunctionAction(uri, result, name, diagnostic));
        }

        if ( context.Store is null )
        {
            return fixes;
        }

        // Under a merge dialect an unqualified call already resolves by NAME across everything the
        // include graph pulled in, so a call that reached this diagnostic is not one an import would
        // fix. Only a namespace dialect can have the function present but out of reach.
        if ( !context.Profile.ResolvesByNamespace )
        {
            return fixes;
        }

        HashSet<string> ownNamespaces = new(StringComparer.Ordinal);
        foreach ( string declared in result.Extraction.DeclaredNamespaces )
        {
            ownNamespaces.Add(declared);
        }

        HashSet<string> existingUsings = context.ExistingUsings;
        Position insertAt = context.UsingInsertAt;
        HashSet<string> offered = new(StringComparer.Ordinal);

        // Gathered before any action is built, because IsPreferred is init-only and whether ONE of
        // these is preferred depends on how many there turn out to be.
        List<(string Namespace, string Path)> reachable = [];

        // Namespace left null: every namespace is searched, which is the whole point — the caller
        // already knows the one that was written does not have it.
        foreach ( ResolvedFunction resolved in context.Declaring(name) )
        {
            string? namespaceName = resolved.Function.Namespace;
            if ( namespaceName is null || ownNamespaces.Contains(namespaceName) )
            {
                continue;
            }

            if ( resolved.Record.RelativePath.Length == 0 )
            {
                continue;
            }

            string usingPath = ImportEdits.PathOf(resolved.Record.RelativePath);

            // One offer per namespace+file pair. The same namespace spread over several files is
            // normal, and each file is a genuinely different import.
            if ( !offered.Add(namespaceName + "|" + usingPath) )
            {
                continue;
            }

            reachable.Add((namespaceName, usingPath));
        }

        foreach ( (string Namespace, string Path) candidate in reachable )
        {
            fixes.Add(BuildImportAndQualifyAction(
                uri,
                candidate.Namespace,
                candidate.Path,
                existingUsings.Contains(candidate.Path),
                insertAt,
                range,
                qualifier,
                diagnostic,

                // Preferred only when one place can supply the name. Auto Fix runs preferred actions
                // without asking, and choosing between several namespaces on the user's behalf is a
                // guess dressed up as an answer.
                reachable.Count == 1));
        }

        return fixes;
    }

    /// <summary>
    /// The offers for a call that resolves to a function nothing merges into scope (5026): one
    /// <c>#include</c> per file that declares the name.
    ///
    /// No "create it here" offer, unlike <see cref="UnresolvedCallFixes"/>. There the name matched
    /// nothing and writing a declaration was one of two honest answers; here the function
    /// demonstrably exists, and a second copy of it is a bug rather than a fix.
    ///
    /// The candidate list is rebuilt from the store rather than read off the diagnostic. The message
    /// names ONE file — the first alphabetically — and the others ride along as related information,
    /// which the client is free not to send back; deriving the list again is what keeps the fix
    /// offering every file the lint considered.
    /// </summary>
    /// <param name="context">
    /// The per-request state, which also carries the dialect to answer for. The tests set that
    /// explicitly so they need not mutate the global profile selection, which every other test in
    /// the assembly reads.
    /// </param>
    internal static List<CodeAction> MissingIncludeFixes(
        DocumentUri uri,
        CallFixContext context,
        LspDiagnostic diagnostic)
    {
        List<CodeAction> fixes = [];
        if ( context.Store is null || context.Profile.ResolvesByNamespace )
        {
            return fixes;
        }

        ParseResult result = context.Result;
        TextRange range = diagnostic.Range.ToCore();
        string name = TextAt(result, range);
        // The lexer's own rule: a stale diagnostic range against an edited buffer is how a fix
        // would otherwise write a declaration out of something that is not a name.
        if ( name.Length == 0 || !GscIdentifier.IsIdentifier(name) )
        {
            return fixes;
        }

        // The same normalized form the lint and the resolver use, rather than a second rule for
        // include paths that would have to stay in step with theirs.
        ImmutableArray<string> existingIncludes = context.IncludedPaths;

        // Sorted and deduplicated by construction. Alphabetical matters: it is the order the lint
        // names them in, and two lists of the same files in two orders read as two different
        // answers.
        SortedSet<string> candidates = new(StringComparer.OrdinalIgnoreCase);

        // Namespace left null: a merge dialect keys every function without one, so this is the same
        // lookup the lint made.
        foreach ( ResolvedFunction resolved in context.Declaring(name) )
        {
            if ( resolved.Record.RelativePath.Length == 0 )
            {
                continue;
            }

            string includePath = ImportEdits.PathOf(resolved.Record.RelativePath);
            if ( !existingIncludes.Contains(includePath) )
            {
                candidates.Add(includePath);
            }
        }

        Position insertAt = context.IncludeInsertAt;
        foreach ( string candidate in candidates )
        {
            TextEdit edit = new()
            {
                Range = new TextRange(insertAt, insertAt).ToLsp(),
                NewText = "#include " + candidate + ";\n",
            };

            // Preferred only when one file can supply the name. Several same-named functions is the
            // normal state of a merge dialect, and picking one for Auto Fix would change which
            // function the call means without saying so.
            fixes.Add(QuickFix(
                "Add #include " + candidate, uri, [edit], diagnostic, preferred: candidates.Count == 1));
        }

        return fixes;
    }

    /// <summary>
    /// Declares the missing function at the end of the file, opened the way the dialect declares
    /// one — BO3 writes `function foo()`, the merge games the bare name.
    ///
    /// No parameter list, deliberately: the call site's argument count is not on the diagnostic, and
    /// a guessed list that disagreed with the call would be worse than an empty one the user fills
    /// in with the caret already inside it.
    /// </summary>
    private static CodeAction BuildCreateFunctionAction(
        DocumentUri uri,
        ParseResult result,
        string name,
        LspDiagnostic diagnostic)
    {
        string opening = GameProfile.Active.HasFunctionKeyword ? "function " : "";

        // A file that does not end in a newline would otherwise get the declaration welded onto its
        // last line.
        string separator = result.Text.Length > 0 && result.Text.Text[^1] != '\n' ? "\n\n" : "\n";
        Position end = result.Text.GetPosition(result.Text.Length);
        TextEdit edit = new()
        {
            Range = new TextRange(end, end).ToLsp(),
            NewText = separator + opening + name + "()\n{\n}\n",
        };

        // Never preferred: writing an empty declaration is a reasonable thing to OFFER and a poor
        // thing for Auto Fix to do silently, since it makes the error disappear without the
        // function doing anything.
        return QuickFix("Create function '" + name + "'", uri, [edit], diagnostic);
    }

    /// <summary>
    /// Imports the file that declares the function and points the call at it. Up to two edits, as
    /// one operation: the `#using`, when it is not already there, and the qualifier — inserted when
    /// the call was written bare, replaced when it named a namespace that does not have the name.
    /// </summary>
    private static CodeAction BuildImportAndQualifyAction(
        DocumentUri uri,
        string namespaceName,
        string usingPath,
        bool alreadyImported,
        Position insertAt,
        TextRange nameRange,
        TextRange? qualifier,
        LspDiagnostic diagnostic,
        bool unambiguous)
    {
        List<TextEdit> edits = [];
        if ( !alreadyImported )
        {
            edits.Add(new TextEdit
            {
                Range = new TextRange(insertAt, insertAt).ToLsp(),
                NewText = "#using " + usingPath + ";\n",
            });
        }

        TextRange qualifierRange = qualifier ?? new TextRange(nameRange.Start, nameRange.Start);
        edits.Add(new TextEdit { Range = qualifierRange.ToLsp(), NewText = namespaceName + "::" });

        string title = alreadyImported
            ? "Qualify with '" + namespaceName + "::'"
            : "Add #using " + usingPath + " and qualify with '" + namespaceName + "::'";

        return QuickFix(title, uri, edits, diagnostic, preferred: unambiguous);
    }

    /// <summary>
    /// The `ns::` written immediately before a call's name, as a range covering the namespace and
    /// the colons, or null when the call was written bare. Whitespace between the parts is allowed
    /// for, since nothing forbids `util :: foo()` even though nobody writes it.
    /// </summary>
    private static TextRange? QualifierRange(ParseResult result, TextRange nameRange)
    {
        string text = result.Text.Text;
        int index = result.Text.GetOffset(nameRange.Start);

        int afterColons = SkipWhitespaceBack(text, index);
        if ( afterColons < 2 || text[afterColons - 1] != ':' || text[afterColons - 2] != ':' )
        {
            return null;
        }

        int end = afterColons;
        int cursor = SkipWhitespaceBack(text, afterColons - 2);
        int start = cursor;
        while ( start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_') )
        {
            start--;
        }

        if ( start == cursor )
        {
            // Colons with no name in front of them; not a qualifier this can rewrite.
            return null;
        }

        return new TextRange(result.Text.GetPosition(start), result.Text.GetPosition(end));
    }

    private static int SkipWhitespaceBack(string text, int index)
    {
        while ( index > 0 && char.IsWhiteSpace(text[index - 1]) )
        {
            index--;
        }

        return index;
    }

    /// <summary>The source covered by a range, trimmed.</summary>
    private static string TextAt(ParseResult result, TextRange range)
    {
        int start = result.Text.GetOffset(range.Start);
        int end = result.Text.GetOffset(range.End);
        if ( start >= end || end > result.Text.Length )
        {
            return "";
        }

        return result.Text.Text[start..end].Trim();
    }

    /// <summary>An import already made earlier in the file, and so removable.</summary>
    internal sealed record RedundantImport(string Path, string Directive, TextRange Range);

    /// <summary>
    /// The import directives whose path was already imported earlier in the file AND whose line
    /// overlaps the selection — i.e. the redundant ones offered for removal.
    ///
    /// Covers <c>#include</c> as well as <c>#using</c>. It did not, which left the four merge games
    /// reporting a duplicate import (5018) with no fix behind it at all — the lint is dialect-neutral
    /// and this was not.
    ///
    /// Each directive keeps its own set, mirroring <c>DuplicateImportLint</c>, so the fix and the
    /// diagnostic cannot disagree about what counts as a duplicate.
    /// </summary>
    internal static List<RedundantImport> FindRemovableDuplicates(ParseResult result, TextRange selection)
    {
        List<RedundantImport> duplicates = [];
        HashSet<string> seenUsings = new(StringComparer.Ordinal);
        HashSet<string> seenIncludes = new(StringComparer.Ordinal);

        foreach ( AstNode element in result.Tree.Root.Elements )
        {
            string path;
            string directive;
            HashSet<string> seen;

            switch ( element )
            {
                case UsingNode usingNode:
                    path = usingNode.Path;
                    directive = "#using";
                    seen = seenUsings;
                    break;
                case IncludeNode includeNode:
                    path = includeNode.Path;
                    directive = "#include";
                    seen = seenIncludes;
                    break;
                default:
                    continue;
            }

            // Only the second-and-later occurrences of a path are redundant.
            if ( seen.Add(PathUtil.NormalizeScriptPath(path)) )
            {
                continue;
            }

            if ( element.Range.Overlaps(selection) )
            {
                duplicates.Add(new RedundantImport(path, directive, element.Range));
            }
        }

        return duplicates;
    }

    /// <summary>An import that would make one call site resolvable, and the site it belongs to.</summary>
    /// <param name="Range">
    /// The call's NAME range, which is also the range the NamespaceNotImported lint reports over —
    /// both come from the same <c>ReferenceEntry</c>. That shared origin is what lets the action be
    /// matched back to the diagnostic it fixes.
    /// </param>
    internal sealed record MissingUsing(string Path, TextRange Range);

    /// <summary>
    /// The imports that would make the qualified calls in the selection resolvable but are not
    /// there yet: for each such call, the script-relative path of a visible file defining that
    /// function, minus the extension, paired with the call it answers. Own-namespace calls and
    /// already-imported files are skipped.
    /// </summary>
    internal static List<MissingUsing> FindMissingUsingSites(
        ParseResult result, LanguageStore store, string contextId, string askingPath, TextRange selection)
    {
        // The DECLARED set rather than the namespace spans: the spans include a leading region named
        // after the file whenever its imports sit above its #namespace line, and a phantom in here
        // read as "already own that namespace" and silently withheld the add-#using fix.
        HashSet<string> ownNamespaces = new(StringComparer.Ordinal);
        foreach ( string declared in result.Extraction.DeclaredNamespaces )
        {
            ownNamespaces.Add(declared);
        }

        HashSet<string> existingUsings = CallFixContext.GatherUsings(result);

        List<MissingUsing> missing = [];
        HashSet<string> offered = new(StringComparer.Ordinal);

        foreach ( ReferenceEntry entry in result.Extraction.References )
        {
            // FromMacro entries are included, matching NamespaceUsageLint exactly: it reports the
            // missing import for a call a macro expanded into, and a diagnostic with no fix behind
            // it is worse than either half alone. The range is the invocation site in both, which
            // is what lets the action be matched to the Error the client reported.
            if ( entry.Kind != ReferenceKind.Call
                || entry.Key.Kind != SymbolKind.Function )
            {
                continue;
            }

            string? namespaceName = entry.Key.Namespace;
            if ( namespaceName is null || ownNamespaces.Contains(namespaceName) )
            {
                continue;
            }

            if ( !entry.Range.Overlaps(selection) )
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

                string usingPath = ImportEdits.PathOf(resolved.Record.RelativePath);
                if ( existingUsings.Contains(usingPath) || !offered.Add(usingPath) )
                {
                    continue;
                }

                missing.Add(new MissingUsing(usingPath, entry.Range));
            }
        }

        return missing;
    }

    private static CodeAction BuildRemoveAction(
        DocumentUri uri,
        RedundantImport duplicate,
        LspDiagnostic? reported)
    {
        // Delete the whole line the directive sits on, including its trailing newline.
        TextEdit edit = new() { Range = LineRangeOf(duplicate.Range).ToLsp(), NewText = "" };

        // Preferred: one way to remove a redundant import, and the line is provably dead — the same
        // file is imported above. Safe for Auto Fix to take.
        return QuickFix(
            "Remove duplicate " + duplicate.Directive + " " + duplicate.Path,
            uri,
            [edit],
            reported,
            preferred: reported is not null);
    }

    /// <summary>
    /// The import itself, BOUND to the diagnostic it answers when one was reported.
    ///
    /// The binding is the whole difference between an action a user can find and one they cannot.
    /// Without it this is a general lightbulb entry: it never appears as the fix FOR the error, Auto
    /// Fix skips it (that runs preferred actions only), and Fix All does not see it. The action was
    /// produced correctly the whole time and asking for the fix still did nothing.
    /// </summary>
    private static CodeAction BuildAddUsingAction(
        DocumentUri uri,
        string usingPath,
        Position insertAt,
        LspDiagnostic? reported,
        bool unambiguous)
    {
        TextRange insertRange = new(insertAt, insertAt);
        TextEdit edit = new() { Range = insertRange.ToLsp(), NewText = "#using " + usingPath + ";\n" };

        // Preferred only when one import can serve the call. Preferring one of several would make
        // Auto Fix pick a file for the user without saying so.
        return QuickFix(
            "Add #using " + usingPath, uri, [edit], reported, preferred: reported is not null && unambiguous);
    }

    /// <summary>
    /// The diagnostic of a given code that the client reported over a range, or null when it did
    /// not report one — the action is still offered then, just not bound to anything.
    /// </summary>
    private static LspDiagnostic? ReportedAt(
        CodeActionParams request, GscDiagnosticCode code, TextRange range)
    {
        foreach ( LspDiagnostic diagnostic in request.Context.Diagnostics )
        {
            if ( CodeOf(diagnostic) == code && diagnostic.Range.ToCore().Overlaps(range) )
            {
                return diagnostic;
            }
        }

        return null;
    }

    /// <summary>
    /// One quick fix over this document: its title, the edits it applies, and the diagnostics it
    /// answers.
    ///
    /// The diagnostic binding is the whole difference between an action a user can find and one
    /// they cannot. Unbound, an action is a general-lightbulb entry only — it never appears as the
    /// fix FOR the error, Auto Fix skips it (that runs preferred actions only), and Fix All does
    /// not see it. Every offer here that answers a reported diagnostic must carry it.
    /// </summary>
    /// <param name="preferred">
    /// Whether Auto Fix may take this without asking. True only where the fix is the single
    /// unambiguous answer: preferring one of several candidates decides for the user silently.
    /// Defaults to false, which is what <see cref="CodeAction.IsPreferred"/> already is when left
    /// unset — it is a plain bool, not a nullable one.
    /// </param>
    private static CodeAction QuickFix(
        string title,
        DocumentUri uri,
        IEnumerable<TextEdit> edits,
        Container<LspDiagnostic>? diagnostics,
        bool preferred = false)
    {
        return BuildAction(title, uri, edits, diagnostics, CodeActionKind.QuickFix, preferred);
    }

    /// <summary>
    /// "Generate ScriptDoc block" on a function or method that has none.
    ///
    /// A Refactor rather than a QuickFix, and deliberately without a diagnostic behind it: an
    /// undocumented function is not a fault. The stock scripts ship thousands of them, so a
    /// missing-documentation rule would be noise on code that works, which is the bar
    /// <c>add-diagnostic</c> sets. The action stands on its own.
    ///
    /// Offered on the declaration the selection sits in — both loops, because
    /// <c>Extraction.Functions</c> holds top-level functions only and a class's methods hang off
    /// the class, the same split <see cref="CodeLensHandler"/> walks.
    /// </summary>
    private static void AddGenerateScriptDocAction(
        DocumentUri uri, ParseResult result, TextRange selection, List<CommandOrCodeAction> actions)
    {
        FunctionSymbol? declaration = UndocumentedDeclarationAt(result, selection.Start);
        if ( declaration is null )
        {
            return;
        }

        int line = declaration.FullRange.Start.Line;
        string block = ScriptDocTemplate.Render(
            declaration.Name,
            declaration.Parameters,
            declaration.HasVarargs,
            GameProfile.Active.ScriptDocStyle,
            IndentOf(result, line));

        TextRange insertAt = new(new Position(line, 0), new Position(line, 0));
        actions.Add(new CommandOrCodeAction(BuildAction(
            "Generate ScriptDoc block for '" + declaration.Name + "'",
            uri,
            [new TextEdit { Range = insertAt.ToLsp(), NewText = block }],
            diagnostics: null,
            CodeActionKind.Refactor)));
    }

    /// <summary>
    /// The function or method whose body contains <paramref name="position"/> and which has no doc
    /// block, or null.
    ///
    /// A nameless declaration is skipped rather than offered an empty block: a handler runs on
    /// every keystroke, so a function whose name has not been typed yet is the normal state, not a
    /// fault.
    /// </summary>
    private static FunctionSymbol? UndocumentedDeclarationAt(ParseResult result, Position position)
    {
        foreach ( FunctionSymbol function in result.Extraction.Functions )
        {
            if ( IsUndocumentedAt(function, position) )
            {
                return function;
            }
        }

        foreach ( ClassSymbol classSymbol in result.Extraction.Classes )
        {
            foreach ( FunctionSymbol method in classSymbol.Methods )
            {
                if ( IsUndocumentedAt(method, position) )
                {
                    return method;
                }
            }
        }

        return null;
    }

    private static bool IsUndocumentedAt(FunctionSymbol function, Position position)
    {
        // SourceFile names the header an #insert brought the declaration in from, where the ranges
        // are true and the edit would land in the wrong file entirely.
        return function.SourceFile.Length == 0
            && function.Name.Length > 0
            && function.Doc.IsNone
            && function.FullRange.Contains(position);
    }

    /// <summary>The leading whitespace of a line, which a generated block has to repeat — a method
    /// sits inside a class body and a block flush left above it would be the only thing in the file
    /// at column zero.</summary>
    private static string IndentOf(ParseResult result, int line)
    {
        if ( line < 0 || line >= result.Text.LineCount )
        {
            return "";
        }

        int start = result.Text.GetOffset(new Position(line, 0));
        int cursor = start;
        while ( cursor < result.Text.Length && (result.Text.Text[cursor] == ' ' || result.Text.Text[cursor] == '\t') )
        {
            cursor++;
        }

        return result.Text.Text[start..cursor];
    }

    /// <summary>What <see cref="QuickFix(string, DocumentUri, IEnumerable{TextEdit}, Container{LspDiagnostic}, bool)"/>
    /// builds, generalised over the action's <see cref="CodeActionKind"/> — every quick fix in this
    /// file is one, and <see cref="AddOrganizeImportsAction"/> is the one caller that is not.</summary>
    private static CodeAction BuildAction(
        string title,
        DocumentUri uri,
        IEnumerable<TextEdit> edits,
        Container<LspDiagnostic>? diagnostics,
        CodeActionKind kind,
        bool preferred = false)
    {
        Dictionary<DocumentUri, IEnumerable<TextEdit>> changes = new() { [uri] = edits };

        return new CodeAction
        {
            Title = title,
            Kind = kind,
            Diagnostics = diagnostics,
            IsPreferred = preferred,
            Edit = new WorkspaceEdit { Changes = changes },
        };
    }

    /// <inheritdoc cref="QuickFix(string, DocumentUri, IEnumerable{TextEdit}, Container{LspDiagnostic}, bool)"/>
    /// <param name="diagnostic">
    /// The one diagnostic this answers, or null when the client reported none over the range — the
    /// action is still offered then, just not bound to anything.
    /// </param>
    private static CodeAction QuickFix(
        string title,
        DocumentUri uri,
        IEnumerable<TextEdit> edits,
        LspDiagnostic? diagnostic,
        bool preferred = false)
    {
        return QuickFix(
            title, uri, edits, diagnostic is null ? null : new Container<LspDiagnostic>(diagnostic), preferred);
    }
}
