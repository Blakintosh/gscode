using GSCode.Workspace.Api;
using GSCode.Core;
using GSCode.Core.Paths;
using System.Collections.Immutable;
using GSCode.Core.Symbols;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Workspace.Database;
using GSCode.Workspace.Documents;
using GSCode.Workspace.Resolution;
using GSCode.Server.Configuration;
using OmniSharp.Extensions.LanguageServer.Protocol;

namespace GSCode.Server.Handlers;

/// <summary>The resolved context for a navigation request against one open document.</summary>
/// <param name="Namespaces">
/// The namespaces this file declares, carried so every query can apply the namespace-privacy
/// rule without recomputing it: a private function is visible to any file in the same namespace.
/// </param>
/// <param name="Store">
/// The single store for symbol lookups (functions, classes). A <c>.gsh</c> gets the GSC store,
/// which is arbitrary but harmless: headers declare macros, not functions or classes.
/// </param>
/// <param name="Stores">
/// Every store this file may see references in — both worlds for a <c>.gsh</c>, since a header is
/// inserted into GSC and CSC alike. Use with <see cref="DatabaseQueries.FindAllReferences"/>.
/// </param>
public sealed record NavigationTarget(
    ParseResult Result,
    string Path,
    ScriptLanguage Language,
    LanguageStore Store,
    ImmutableArray<LanguageStore> Stores,
    string ContextId,
    ImmutableArray<string> Namespaces);

/// <summary>
/// Everything a symbol query needs about the file doing the asking, with no parse attached.
///
/// <see cref="NavigationTarget"/> is this plus the file's live analysis, and most handlers need
/// both. The hierarchies do not: expanding a caller or a supertype asks the reference index and the
/// class graph, which are record-level facts. Splitting the two is what lets those answers come
/// from a file that is not OPEN, which is the normal case for a caller.
/// </summary>
public sealed record SymbolQueryContext(
    string Path,
    ScriptLanguage Language,
    LanguageStore Store,
    ImmutableArray<LanguageStore> Stores,
    string ContextId,
    ImmutableArray<string> Namespaces);

/// <summary>
/// Shared plumbing for the navigation handlers: turns a document URI into its live
/// analysis plus the language store and resolution context to query against.
/// </summary>
public sealed class NavigationSupport
{
    private readonly DocumentStore _documents;
    private readonly ScriptDatabase _database;
    private readonly ResolverHolder _resolver;

    /// <summary>
    /// The engine's own function library, for the one question the reference query cannot answer
    /// from the database: whether a name nothing declares is a BUILTIN or merely unresolved. See
    /// <see cref="IsBuiltinCall"/>.
    ///
    /// Optional because most fixtures construct this with no library and ask it nothing that needs
    /// one. A null library disables the builtin widening only — every other answer is unchanged —
    /// which is the right behaviour for a caller that has no engine data to compare against, since
    /// widening on "nothing declares it" alone would group a typo in one namespace with the same
    /// typo in another.
    /// </summary>
    private readonly BuiltinApiSet? _builtins;

    public NavigationSupport(
        DocumentStore documents, ScriptDatabase database, ResolverHolder resolver, BuiltinApiSet? builtins = null)
    {
        _documents = documents;
        _database = database;
        _resolver = resolver;
        _builtins = builtins;
    }

    public ScriptDatabase Database
    {
        get { return _database; }
    }

    public PathResolver Resolver
    {
        get { return _resolver.Current; }
    }

    /// <summary>Resolves an open document, or null when it is unknown or not yet analysed.</summary>
    /// <param name="cancellationToken">
    /// Taken even though this path does no slow work itself, so that a handler cannot silently pick
    /// the uncancellable overload when it meant the freshening one. Required rather than defaulted
    /// for the same reason.
    /// </param>
    public NavigationTarget? Resolve(DocumentUri uri, CancellationToken cancellationToken)
    {
        return Resolve(uri, freshen: false, cancellationToken);
    }

    /// <summary>
    /// Resolves an open document, re-analysing first if the text has moved on since the last run.
    ///
    /// For features whose request carries a LIVE cursor position — completion and signature help.
    /// Analysis is debounced by 250 ms, so mid-typing the cursor offset lands in text the user has
    /// already replaced: at <c>#pre</c> the stale text might still read <c>#p</c>, the '#'-context
    /// check reads the wrong characters, and completion falls back to statement scope and offers
    /// `private`. That it worked whenever the user paused is exactly the tell.
    /// </summary>
    /// <param name="cancellationToken">
    /// Reaches <see cref="DocumentStore.AnalyzeIfStale"/>, which is the whole point: this runs a
    /// full lex, preprocess, parse and extract on the REQUEST thread, and every caller of it is a
    /// read path with no debounce in front of it. Without the token a completion, code lens or
    /// inlay-hint request the client had already cancelled was analysed to the end regardless.
    /// </param>
    public NavigationTarget? ResolveFresh(DocumentUri uri, CancellationToken cancellationToken)
    {
        return Resolve(uri, freshen: true, cancellationToken);
    }

    private NavigationTarget? Resolve(DocumentUri uri, bool freshen, CancellationToken cancellationToken)
    {
        string path = uri.GetFileSystemPath();
        if ( !_documents.TryGet(path, out OpenDocument document) )
        {
            return null;
        }

        ParseResult? result = freshen
            ? _documents.AnalyzeIfStale(document, cancellationToken)
            : document.LatestResult;

        if ( result is null )
        {
            return null;
        }

        ResolutionContext context = _resolver.Current.GetContext(document.Path);

        return new NavigationTarget(
            result,
            document.Path,
            document.Language,
            _database.StoreFor(document.Language),
            _database.StoresFor(document.Language),
            ScriptDatabase.ContextIdOf(context),
            result.Extraction.DeclaredNamespaces);
    }

    /// <summary>
    /// The query context for a file, whether or not it is open.
    ///
    /// An open document answers from its live analysis, as <see cref="Resolve(DocumentUri, CancellationToken)"/>
    /// does. A file that is merely INDEXED answers from its record, which carries the context id and
    /// the declared namespaces outright — no resolver call and no parse, because nothing here needs
    /// a syntax tree.
    ///
    /// For the hierarchies. Expanding an incoming call or a supertype names another file, and that
    /// file is usually not one the user has open; resolving through the document store returned null
    /// for it, which the protocol reads as "there are none".
    /// </summary>
    public SymbolQueryContext? ResolveForQuery(DocumentUri uri, CancellationToken cancellationToken)
    {
        NavigationTarget? open = Resolve(uri, cancellationToken);
        if ( open is not null )
        {
            return ContextOf(open);
        }

        string path = PathUtil.NormalizeAbsolute(uri.GetFileSystemPath());
        if ( !_database.TryGetAnyRecord(path, out ScriptRecord record) )
        {
            return null;
        }

        return new SymbolQueryContext(
            record.Path,
            record.Language,
            _database.StoreFor(record.Language),
            _database.StoresFor(record.Language),
            record.ContextId,
            record.DeclaredNamespaces);
    }

    /// <summary>The query half of a resolved open document.</summary>
    public static SymbolQueryContext ContextOf(NavigationTarget target)
    {
        return new SymbolQueryContext(
            target.Path, target.Language, target.Store, target.Stores, target.ContextId, target.Namespaces);
    }

    /// <summary>
    /// The file a <c>#using</c> or <c>#include</c> path names, or null when nothing resolves.
    ///
    /// The extension comes from the ASKING document, not the path: a <c>.csc</c> writing
    /// <c>#using maps\mp\x</c> means the client script, whose server twin may not exist at all.
    /// Written once because go-to-definition and ctrl-click ask the identical question — with a
    /// copy each, a new directive form or a change to how the extension is chosen had to be found
    /// twice, and finding one of the two is silent.
    /// </summary>
    public string? ResolveDirectivePath(NavigationTarget target, string directivePath)
    {
        string extension = target.Language == ScriptLanguage.Csc
            ? GameProfile.Active.ClientScriptExtension
            : GameProfile.Active.ServerScriptExtension;

        PathResolver resolver = _resolver.Current;
        return resolver.Resolve(resolver.GetContext(target.Path), directivePath + extension);
    }

    /// <summary>
    /// What the reference index knows about the symbol under a cursor — a function, class, macro,
    /// field, or one of the literal kinds — or <see cref="PositionHit.None"/> when nothing does.
    ///
    /// The one entry point every position-based handler calls — hover, definition, references,
    /// rename, prepare-rename, highlight, both hierarchies and <c>gscode/builtinAt</c>. "Nothing
    /// does" is ALSO the answer for a
    /// LOCAL, since the index is keyed by <c>SymbolKey</c> and shared workspace-wide — every caller
    /// here falls through to <see cref="LocalOccurrencesAt"/> for that case, which is why the two
    /// methods are written as companions rather than merged into one: what a hit and a local each
    /// resolve TO is different enough per feature (a single range, a list of locations honouring
    /// <c>includeDeclaration</c>, Read/Write highlight kinds, rename edits) that unifying the
    /// fallthrough itself would cost more than the one shared resolve call is worth.
    /// </summary>
    public PositionHit ResolveHit(NavigationTarget target, GSCode.Core.Text.Position position)
    {
        PositionHit hit = SymbolAtPosition.Resolve(target.Result, position);

        // A MEMBER hit is the one kind extraction may have guessed at. Inside a class whose
        // ancestors are not all in the file, it records every bare name as a member, because it
        // cannot tell an inherited one from a local and the alternative is that the inherited ones
        // stay invisible. The class graph settles it here, where the whole workspace is available:
        // a name no ancestor declares is a local after all, and answering None sends it down the
        // local path exactly as before.
        if ( hit.Kind == HitKind.Reference && hit.Key.Kind == SymbolKind.Member )
        {
            return IsRealMember(target, hit.Key) ? hit : PositionHit.None;
        }

        return hit;
    }

    /// <summary>
    /// Whether a member key names a <c>var</c> the class really has, own or inherited.
    ///
    /// Bounded by the hierarchy rather than the workspace — three classes at BO3's deepest — and
    /// reached only for a cursor inside a class body, which is rare enough that document highlight
    /// firing on every cursor move does not care.
    /// </summary>
    private static bool IsRealMember(NavigationTarget target, SymbolKey key)
    {
        if ( key.OwnerClass is null )
        {
            return false;
        }

        return MethodResolution.FindDeclaringClassForMember(
            target.Store, target.ContextId, key.OwnerClass, key.Name) is not null;
    }

    /// <summary>
    /// Every occurrence of the LOCAL under a position, within the function that scopes it.
    ///
    /// The local counterpart of <c>FindAllReferences</c>, and shared for the same reason:
    /// find-references, highlight and rename must agree about what a variable's occurrences are,
    /// and three copies of the walk is how they stop agreeing.
    ///
    /// Empty when the position is not on a local — which is also the answer for everything the
    /// reference index DOES know, so a caller can reach here unconditionally after a failed
    /// <see cref="ResolveHit"/>.
    /// </summary>
    public ImmutableArray<LocalOccurrence> LocalOccurrencesAt(
        NavigationTarget target, GSCode.Core.Text.Position position)
    {
        return LocalReferences.Find(target.Result, position);
    }

    /// <summary>
    /// Every reference to a key visible from this document, across both language worlds when the
    /// document is a header. The single entry point, so the count a CodeLens shows and the list a
    /// peek opens are computed the same way.
    /// </summary>
    /// <param name="referenceKind">
    /// How the SITE under the cursor used the name. Load-bearing for the arrow form: a key with no
    /// namespace and no owner is written the same way by an untyped <c>[[x]]-&gt;m()</c>, a
    /// <c>sys::m()</c> builtin call and a plain unqualified call, and only the kind separates them.
    /// </param>
    public ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> FindAllReferences(
        NavigationTarget target, SymbolKey key, ReferenceKind referenceKind = ReferenceKind.Call)
    {
        return FindAllReferences(ContextOf(target), key, referenceKind, MacroSpansLanguages(target, key));
    }

    /// <summary>
    /// The same query, answered for ONE FILE — for a question that is same-file by definition.
    ///
    /// Document highlight is the caller. It has to run this query rather than read the document's
    /// own <c>Extraction.References</c>, because a method hit is keyed by its OWNER at the cursor
    /// and only <c>MethodResolution.Canonicalize</c> (inside the query) knows to widen that to the
    /// declaring class — but everything the query then reads outside this file is thrown away. On
    /// bo3 at 50,000 files the sampled reference requests return 406,326 locations (PERF.md, the
    /// scale section), and highlight fires on every cursor move.
    ///
    /// Not a second implementation: the narrowing is a parameter on the one query, so the key
    /// derivation, the method union, the reachability scoping and the shadow rule are literally the
    /// same code. <c>SameFileReferenceTests</c> requires the two to agree.
    /// </summary>
    public ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> FindReferencesInFile(
        NavigationTarget target, SymbolKey key, ReferenceKind referenceKind = ReferenceKind.Call)
    {
        return FindAllReferences(
            ContextOf(target), key, referenceKind, MacroSpansLanguages(target, key), target.Path);
    }

    /// <summary>
    /// False when <paramref name="key"/> is a macro this file defines LOCALLY — no <c>#insert</c>
    /// involved — so <see cref="GSCode.Workspace.Database.DatabaseQueries.FindAllReferences"/> can
    /// be told not to widen it to both language stores. True for everything else, including a
    /// macro this file has no answer for (defensive — a real <c>MacroUse</c>/<c>Definition</c> key
    /// should always resolve here).
    ///
    /// <c>DatabaseQueries.FindAllReferences</c> widens every macro key to both language stores
    /// unconditionally by default, which is right for a macro actually reached through a shared
    /// <c>.gsh</c> (a rename started in either world has to reach the other — see
    /// <c>MacroRenameAcrossLanguagesTests</c>) but wrong for two macros that merely share a NAME —
    /// <c>CF_CRACKS_ALL</c> independently <c>#define</c>d in <c>animation_shared.gsc</c> and its
    /// sibling <c>.csc</c>, with no header between them. Left unanswered, the CodeLens count and
    /// the peek list on the <c>.gsc</c>'s own definition both counted the <c>.csc</c>'s unrelated
    /// one — the same conflation
    /// <see cref="GSCode.Server.Handlers.DefinitionHandler.MacroDefinitionAt"/> answers for
    /// go-to-definition, here for every OTHER caller of <see cref="FindAllReferences(NavigationTarget, SymbolKey, ReferenceKind)"/>
    /// (CodeLens, Find References, Document Highlight, rename).
    ///
    /// Answered from THIS FILE's own preprocessor result, matching go-to-definition exactly: a
    /// macro whose entry here has no <c>SourceFile</c> was written directly in this file, and a
    /// same-named local <c>#define</c> in the other language world is a different macro. One with
    /// a <c>SourceFile</c> came from an <c>#insert</c>, so it keeps the full cross-language width —
    /// that IS the shared case the wide search exists for.
    /// </summary>
    private static bool MacroSpansLanguages(NavigationTarget target, SymbolKey key)
    {
        if ( key.Kind != SymbolKind.Macro )
        {
            return true;
        }

        return !(target.Result.Preprocessed.Macros.TryGet(key.Name, out MacroDefinition definition)
            && definition.SourceFile is null);
    }

    /// <summary>
    /// The same query for a file that need not be open. See <see cref="SymbolQueryContext"/>.
    /// </summary>
    /// <param name="macroSpansLanguages">
    /// See <see cref="GSCode.Workspace.Database.DatabaseQueries.FindAllReferences"/>'s own
    /// parameter of the same name. Defaults true — this overload has no open document's own parse
    /// to answer <see cref="MacroSpansLanguages"/> from, so it keeps the wide, always-correct-if-
    /// imprecise answer rather than guessing.
    /// </param>
    /// <param name="onlyPath">
    /// See <see cref="GSCode.Workspace.Database.DatabaseQueries.FindAllReferences"/>'s parameter of
    /// the same name, and <see cref="FindReferencesInFile"/> for the caller it exists for.
    /// </param>
    public ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> FindAllReferences(
        SymbolQueryContext target, SymbolKey key, ReferenceKind referenceKind = ReferenceKind.Call,
        bool macroSpansLanguages = true,
        string onlyPath = "")
    {
        // A class MEMBER has the same problem in both directions: the `var` is on one class and
        // the bare uses are keyed by whichever class's body each sits in, so the key canonicalizes
        // DOWN to the declarer and the union comes back UP across its descendants. First, because
        // nothing below knows about the class graph — and here rather than per handler, so
        // find-references, rename, highlight and the CodeLens count cannot disagree about it.
        if ( key.Kind == SymbolKind.Member )
        {
            return MethodResolution.FindMemberReferences(
                _database, target.Stores, target.Store, target.ContextId, key, onlyPath);
        }

        // A method is not reachable under one key the way a function is — inheritance, the
        // Class::method form and untyped arrow calls each name it differently — so it resolves to
        // its declaration's key first and then unions the ways a call site can spell it. Done HERE
        // for the same reason the narrowing below is: the CodeLens count and the peek list run this
        // one query, so they cannot disagree.
        ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> methodReferences =
            MethodResolution.FindReferencesForCall(
                _database, target.Stores, target.Store, target.ContextId, key, referenceKind, onlyPath);

        if ( methodReferences.Length > 0 )
        {
            return methodReferences;
        }

        // The branch above is chosen on whether the METHOD UNION found anything AT ALL, so a
        // narrowed union that found nothing in this file cannot decide it: falling through here
        // would answer a method's cursor from the function query, which is a different answer
        // rather than a smaller one. Ask the union the question it actually decides on — and only
        // here, which a cursor sitting on a reference inside this file rarely reaches, since the
        // site under it is itself one of the entries the union collects.
        if ( onlyPath.Length > 0
            && MethodResolution.FindReferencesForCall(
                _database, target.Stores, target.Store, target.ContextId, key, referenceKind).Length > 0 )
        {
            return [];
        }

        if ( key.Kind == SymbolKind.Function )
        {
            key = MethodResolution.Canonicalize(
                target.Store, target.ContextId, key, referenceKind, key.Namespace ?? "");
        }

        // A BUILTIN is not reachable under one key either, and for a different reason than a method:
        // it has no declaration, so extraction keys each call site by the scope it was written in.
        // Asked under the asking file's own namespace alone, the query returns only the sites that
        // share it — usually just this file. DatabaseQueries has the rule and
        // the reason; this is where the decision belongs, in the one query every reference-shaped
        // feature runs, so the list and the CodeLens count cannot disagree about it.
        if ( IsBuiltinCall(target, key) )
        {
            return DatabaseQueries.FindBuiltinReferences(
                _database, target.Stores, target.Store, target.ContextId, key.Name, onlyPath);
        }

        // Narrowing happens HERE, in the one query both the CodeLens count and the peek list run,
        // so the number and the list cannot disagree. Scoping only the lens once produced a count of
        // 0 beside a list of 1,970.
        //
        // When the key names one declaring file, only the files that can reach it are read — the
        // same answer, without first collecting every reference to a key that on a merge dialect
        // every `main` in the workspace shares. See FindReferencesReaching.
        string declaring = DeclaringFile(target, key);
        if ( declaring.Length > 0 )
        {
            return DatabaseQueries.FindReferencesReaching(
                target.Stores, target.ContextId, key, declaring, onlyPath: onlyPath);
        }

        ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> all =
            DatabaseQueries.FindAllReferences(
                _database, target.Stores, target.ContextId, key, macroSpansLanguages, onlyPath);

        return DatabaseQueries.ScopeToIncludeGraph(all, declaring);
    }

    /// <summary>
    /// Whether this key names an engine function rather than a script one — the test that decides
    /// whether the reference query is asked about a KEY or about a NAME.
    ///
    /// Both halves are needed. The library alone is not enough: a script may declare a function that
    /// shares an engine name, and inside that namespace the call means the script's. "Nothing
    /// declares it" alone is not enough either: that is also true of a typo, and widening a typo
    /// across namespaces would group unrelated mistakes as one symbol.
    /// </summary>
    private bool IsBuiltinCall(SymbolQueryContext target, SymbolKey key)
    {
        if ( _builtins is null || key.Kind != SymbolKind.Function )
        {
            return false;
        }

        if ( _builtins.For(target.Language).Find(key.Name) is null )
        {
            return false;
        }

        if ( key.OwnerClass is not null )
        {
            return MethodResolution.FindDeclaringClass(
                target.Store, target.ContextId, key.OwnerClass, key.Name) is null;
        }

        // The explicit `sys::` form: no script declaration can claim it.
        if ( BuiltinQualifier.IsBuiltinKey(key, GameProfile.Active) )
        {
            return true;
        }

        return DatabaseQueries.LookupFunctions(
            target.Store, target.ContextId, target.Path, key.Namespace, key.Name, includePrivate: true).Length == 0;
    }

    /// <summary>
    /// The file whose declaration this key means FROM THIS DOCUMENT, or empty when that is not one
    /// specific file.
    ///
    /// Under a merge dialect a function has no namespace, so the key alone names every same-named
    /// function in the workspace; what disambiguates it is the asking file, which can only reach
    /// declarations it owns, imports or path-calls. Resolving from the asking document therefore
    /// answers both callers correctly with one rule: a CodeLens sits on a declaration and resolves
    /// to the file it is already in, while find-references on a call resolves to the declaration
    /// that call actually reaches.
    ///
    /// A namespace-driven dialect needs the same rule for a smaller reason: the namespace is in the
    /// key, but it does not pin a FILE. Both <c>scripts\mp\gametypes\_globallogic_utils.gsc</c> and
    /// <c>scripts\zm\gametypes\_globallogic_utils.gsc</c> declare <c>#namespace globallogic_utils</c>,
    /// so without this a reference count on either merged both game modes' callers. The reachability
    /// question is identical — a <c>#using</c> edge is a dependency edge like an <c>#include</c> — so
    /// the same walk answers it.
    ///
    /// Empty on ambiguity — several reachable declarations, or none — because a wide answer is
    /// recoverable and a confidently wrong narrow one is not.
    /// </summary>
    private string DeclaringFile(SymbolQueryContext target, SymbolKey key)
    {
        if ( key.Kind != SymbolKind.Function )
        {
            return "";
        }

        if ( !target.Store.TryGet(PathUtil.NormalizeAbsolute(target.Path), out ScriptRecord asking) )
        {
            return "";
        }

        // A declaration in the ASKING FILE wins outright, and this is the common case: a lens sits
        // on one. Reaching another file that happens to declare the same name does not make the
        // local symbol ambiguous — a bare main() inside combat.gsc means combat.gsc's main, even
        // though combat.gsc also path-calls cover_prone and _mgturret, which each declare their own.
        // Without this, any animscript that path-calls another animscript loses all narrowing,
        // which is every one of them.
        //
        // Matched on the KEY, namespace included, not on the name: on BO3 a file routinely declares
        // a name that some other namespace also declares, and claiming those would hand every such
        // lens its own file's declaration instead of the one it names.
        foreach ( FunctionSymbol declared in asking.Functions )
        {
            if ( string.Equals(declared.KeyName, key.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    GameProfile.Active.KeyNamespace(declared.Namespace), key.Namespace, StringComparison.Ordinal) )
            {
                return asking.RelativePath;
            }
        }

        string only = "";
        foreach ( ResolvedFunction candidate in DatabaseQueries.LookupFunctions(
            target.Store, target.ContextId, target.Path, key.Namespace, key.Name, includePrivate: true) )
        {
            if ( !DatabaseQueries.Reaches(asking, candidate.Record.RelativePath) )
            {
                continue;
            }

            if ( only.Length > 0 && only != candidate.Record.RelativePath )
            {
                return "";
            }

            only = candidate.Record.RelativePath;
        }

        return only;
    }
}
