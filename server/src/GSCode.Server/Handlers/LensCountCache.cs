using System.Collections.Concurrent;
using System.Text;
using GSCode.Core.Paths;
using GSCode.Core.Symbols;
using GSCode.Parser.Preprocessing;
using GSCode.Workspace.Database;

namespace GSCode.Server.Handlers;

/// <summary>
/// The part of each CodeLens count that comes from OTHER files, kept per open file while nothing but
/// that file is written.
///
/// A lens counts every reference the shared query finds, and for a declaration the whole workspace
/// calls that means reading every file that reaches it: at 50,000 heavy BO3 files a copy of
/// scene_shared.gsc carries 125 method lenses, each counting the untyped arrow calls of its name in
/// every copy, and the lenses took about 90 ms of a 107 ms edit. Typing writes only the open file,
/// so the other files' part of every count is unchanged until something else is written; only the
/// open file's own part is counted again, by the same query narrowed to it.
///
/// What else the other files' part depends on is the open file's SHAPE, not its body: what it
/// declares, imports and path-calls decides which declaration a key means and which files reach
/// it. That is <see cref="SignatureOf"/>, and a change to it starts the file's counts over. Not
/// cached at all: a mod or workspace file overriding a raw script, since its own references decide
/// whether the raw copy's count (see <see cref="ShadowsRaw"/>), and a file the store does not hold.
/// </summary>
public sealed class LensCountCache
{
    /// <summary>Open files remembered at once; past it, all are forgotten and refilled as asked.</summary>
    private const int MaximumFiles = 64;

    private readonly ConcurrentDictionary<string, Snapshot> _byPath = new(StringComparer.Ordinal);

    private readonly bool _enabled;

    public LensCountCache()
        : this(enabled: true)
    {
    }

    private LensCountCache(bool enabled)
    {
        _enabled = enabled;
    }

    /// <summary>
    /// A cache that keeps nothing, so every lens runs the full query: for a measurement of what
    /// that query costs, which a cache filled by its own warm-up would otherwise report as free.
    /// </summary>
    public static LensCountCache Disabled { get; } = new(enabled: false);

    /// <summary>Forgets every file's counts.</summary>
    public void Clear()
    {
        _byPath.Clear();
    }

    /// <summary>One open file's counts, valid from <see cref="Tick"/> while only it is written.</summary>
    public sealed class Snapshot
    {
        public Snapshot(long tick, string signature)
        {
            Tick = tick;
            Signature = signature;
        }

        public long Tick { get; }

        public string Signature { get; }

        /// <summary>Each key's references outside the open file, declarations excluded.</summary>
        public ConcurrentDictionary<SymbolKey, int> Outside { get; } = new();
    }

    /// <summary>
    /// The counts for this file to read and fill, or null when they cannot be kept — see the class
    /// summary for which files those are.
    /// </summary>
    public Snapshot? For(NavigationTarget target)
    {
        if ( !_enabled )
        {
            return null;
        }

        string path = PathUtil.NormalizeAbsolute(target.Path);
        CommitClock clock = target.Store.Clock;

        // Read before anything is counted: a write landing while the counts are being filled then
        // makes the next request start over rather than trust a half-old answer.
        long now = clock.Now;

        string? signature = SignatureOf(target, path);
        if ( signature is null )
        {
            _byPath.TryRemove(path, out _);
            return null;
        }

        if ( _byPath.TryGetValue(path, out Snapshot? kept)
            && clock.OnlyWrittenSince(kept.Tick, path)
            && string.Equals(kept.Signature, signature, StringComparison.Ordinal) )
        {
            return kept;
        }

        if ( _byPath.Count >= MaximumFiles )
        {
            _byPath.Clear();
        }

        Snapshot fresh = new(now, signature);
        _byPath[path] = fresh;
        return fresh;
    }

    /// <summary>
    /// What the other files' part of a count can depend on in this file, as one string; null when
    /// the file is not one whose counts can be kept.
    /// </summary>
    private static string? SignatureOf(NavigationTarget target, string path)
    {
        if ( !target.Store.TryGet(path, out ScriptRecord record) )
        {
            return null;
        }

        if ( ShadowsRaw(target.Store, record) )
        {
            return null;
        }

        StringBuilder signature = new();
        signature.Append(record.ContextId).Append('|').Append(record.RelativePath).Append('|').Append(record.Language).Append('\n');

        foreach ( string declared in record.DeclaredNamespaces )
        {
            signature.Append("n ").Append(declared).Append('\n');
        }

        foreach ( FunctionSymbol function in record.Functions )
        {
            AppendFunction(signature, function);
        }

        foreach ( ClassSymbol classSymbol in record.Classes )
        {
            signature.Append("c ").Append(classSymbol.Namespace).Append("::").Append(classSymbol.KeyName)
                .Append(" : ").Append(classSymbol.ParentKeyName).Append('\n');

            foreach ( MemberSymbol member in classSymbol.Members )
            {
                signature.Append("  v ").Append(member.KeyName).Append('\n');
            }

            foreach ( FunctionSymbol method in classSymbol.Methods )
            {
                signature.Append("  ");
                AppendFunction(signature, method);
            }
        }

        foreach ( DependencyEdge edge in record.Dependencies )
        {
            signature.Append("d ").Append(edge.IsInsert).Append(' ').Append(edge.RawPath).Append(" > ").Append(edge.ResolvedPath).Append('\n');
        }

        foreach ( GSCode.Parser.Extraction.PathCallReference pathCall in record.PathCallTargets )
        {
            signature.Append("p ").Append(pathCall.Path).Append('\n');
        }

        foreach ( MacroDefinition macro in target.Result.Preprocessed.Macros.All )
        {
            signature.Append("m ").Append(macro.Name).Append(" from ").Append(macro.SourceFile).Append('\n');
        }

        return signature.ToString();
    }

    /// <summary>
    /// Whether this file is a mod or workspace copy of a raw script. The reference query drops a raw
    /// file's references when an overlay the asker sees at the same path references the key at all,
    /// so this file's own body decides part of the OTHER files' count.
    ///
    /// Only that direction. A raw file with a mod's copy beside it was refused too at first, and
    /// those are the stock scripts every workspace calls into, where the counts cost most: the
    /// scale run found every one refused, because the installed mods carry copies of them. A raw
    /// asker sees no overlay, so its body never decides another file's count.
    ///
    /// Not the same walk as <c>WorkspaceSymbolHandler.ShadowedOut</c>, which asks the opposite
    /// direction: whether an overlay beside a raw match declares the same name.
    /// </summary>
    private static bool ShadowsRaw(LanguageStore store, ScriptRecord record)
    {
        if ( record.ContextId == "raw" || record.RelativePath.Length == 0 )
        {
            return false;
        }

        foreach ( string twin in store.FilesAt(RelativePathIndex.Normalize(record.RelativePath)) )
        {
            if ( store.TryGet(twin, out ScriptRecord other) && other.ContextId == "raw" )
            {
                return true;
            }
        }

        return false;
    }

    private static void AppendFunction(StringBuilder signature, FunctionSymbol function)
    {
        signature.Append("f ").Append(function.Namespace).Append("::").Append(function.KeyName)
            .Append(" of ").Append(function.OwnerClassKeyName).Append(function.IsPrivate ? " private" : "").Append('\n');
    }
}
