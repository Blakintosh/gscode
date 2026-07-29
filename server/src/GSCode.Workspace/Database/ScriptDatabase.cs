using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.IO.Hashing;
using System.Text;
using GSCode.Core.Paths;
using GSCode.Core.Symbols;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Workspace.Resolution;

namespace GSCode.Workspace.Database;

/// <summary>
/// THE central store: one dictionary of ScriptRecords per language world (GSC, CSC —
/// structurally isolated) plus a shared GSH store (headers serve both sides). Keyed by
/// normalized absolute on-disk path. Queries pick their store ONCE from the asking
/// file's language; everything below that point is language-blind.
/// </summary>
public sealed class ScriptDatabase
{
    /// <summary>The GSC world.</summary>
    /// <summary>
    /// Whether a workspace index has finished at least once.
    ///
    /// Most analysis degrades gracefully on a partial index — a lookup that finds nothing simply
    /// offers nothing. FUNCTION RESOLUTION does not: it reports a name as nonexistent, and before
    /// the index is populated every script function in the workspace looks nonexistent. So that one
    /// lint has to know, and there is no cheaper signal — unlike a missing FILE, which the resolver
    /// can answer from the filesystem, a missing FUNCTION can only be answered by the index.
    /// </summary>
    public bool HasCompletedIndex { get; private set; }

    /// <summary>Marks the index complete; called by the indexer when a full pass finishes.</summary>
    public void MarkIndexComplete()
    {
        HasCompletedIndex = true;
    }

    public LanguageStore Gsc { get; } = new();

    /// <summary>The CSC world.</summary>
    public LanguageStore Csc { get; } = new();

    /// <summary>GSH records (macros/dependencies), shared by both languages.</summary>
    private readonly ConcurrentDictionary<string, ScriptRecord> _gshRecords = new(StringComparer.Ordinal);

    /// <summary>The store for a language; GSH callers use the dedicated methods below.</summary>
    public LanguageStore StoreFor(ScriptLanguage language)
    {
        if ( language == ScriptLanguage.Csc )
        {
            return Csc;
        }

        return Gsc;
    }

    /// <summary>
    /// Every store a file of this language may see references in.
    ///
    /// A <c>.gsh</c> is inserted into BOTH worlds, so a macro it defines is used from <c>.gsc</c>
    /// and <c>.csc</c> alike and neither store alone is the answer. <see cref="StoreFor"/> hands
    /// GSH the GSC store — fine for picking one store to write into, but as a query scope it made
    /// CSC uses of a header macro invisible from the header itself.
    ///
    /// GSC and CSC stay single-store: their separation is what stops a same-named symbol in the
    /// other world from being conflated with this one.
    /// </summary>
    public ImmutableArray<LanguageStore> StoresFor(ScriptLanguage language)
    {
        if ( language == ScriptLanguage.Gsh )
        {
            return [Gsc, Csc];
        }

        return [StoreFor(language)];
    }

    public void UpsertGsh(ScriptRecord record)
    {
        _gshRecords[record.Path] = record;
    }

    /// <summary>
    /// Finds a record by path without knowing its language, for callers that have only a file
    /// path — a closed document, a file-watcher event. Every store is searched because the path
    /// alone does not say which world it belongs to.
    /// </summary>
    public bool TryGetAnyRecord(string path, out ScriptRecord record)
    {
        string normalized = PathUtil.NormalizeAbsolute(path);

        return Gsc.TryGet(normalized, out record)
            || Csc.TryGet(normalized, out record)
            || TryGetGsh(normalized, out record);
    }

    public bool TryGetGsh(string normalizedPath, out ScriptRecord record)
    {
        return _gshRecords.TryGetValue(normalizedPath, out record!);
    }

    public void RemoveGsh(string normalizedPath)
    {
        _gshRecords.TryRemove(normalizedPath, out _);
    }

    public IEnumerable<ScriptRecord> AllGshRecords
    {
        get { return _gshRecords.Values; }
    }

    /// <summary>Removes a file from whichever store holds it.</summary>
    public void Remove(string normalizedPath, ScriptLanguage language)
    {
        if ( language == ScriptLanguage.Gsh )
        {
            RemoveGsh(normalizedPath);
        }
        else
        {
            StoreFor(language).Remove(normalizedPath);
        }
    }

    /// <summary>Normalized paths of every non-GSH file that #inserts the given GSH.</summary>
    public IEnumerable<string> FilesInserting(string normalizedGshPath)
    {
        foreach ( ScriptRecord record in Gsc.AllRecords.Concat(Csc.AllRecords) )
        {
            foreach ( DependencyEdge edge in record.Dependencies )
            {
                if ( edge.IsInsert && string.Equals(edge.ResolvedPath, normalizedGshPath, StringComparison.Ordinal) )
                {
                    yield return record.Path;
                    break;
                }
            }
        }
    }

    /// <summary>Stores a completed analysis as the file's current record.</summary>
    public ScriptRecord Commit(ParseResult result, ResolutionContext context, bool isDirty, string relativePath = "")
    {
        ScriptRecord record = BuildRecord(result, context, isDirty, relativePath);

        if ( record.Language == ScriptLanguage.Gsh )
        {
            UpsertGsh(record);
        }
        else
        {
            StoreFor(record.Language).Upsert(record);
        }

        return record;
    }

    /// <summary>Stores a pre-built record (from the cache) without re-analysing.</summary>
    public void CommitRecord(ScriptRecord record)
    {
        if ( record.Language == ScriptLanguage.Gsh )
        {
            UpsertGsh(record);
        }
        else
        {
            StoreFor(record.Language).Upsert(record);
        }
    }

    /// <summary>
    /// The distinct files a script reaches by path call. Records do not keep the ParseResult, so
    /// these are lifted out here or they are lost — and reference scoping on the merge dialects
    /// needs them: a path call reaches another file's function without importing it.
    /// </summary>
    private static ImmutableArray<string> PathCallTargetsOf(ParseResult result)
    {
        if ( result.Extraction.PathCalls.Length == 0 )
        {
            return [];
        }

        HashSet<string> distinct = new(StringComparer.OrdinalIgnoreCase);
        foreach ( GSCode.Parser.Extraction.PathCallReference pathCall in result.Extraction.PathCalls )
        {
            if ( pathCall.Path.Length > 0 )
            {
                distinct.Add(pathCall.Path);
            }
        }

        return [.. distinct];
    }

    /// <summary>Builds the immutable record from a pipeline result.</summary>
    public static ScriptRecord BuildRecord(ParseResult result, ResolutionContext context, bool isDirty, string relativePath = "")
    {
        ImmutableArray<MacroRecord>.Builder macros = ImmutableArray.CreateBuilder<MacroRecord>();
        foreach ( MacroDefinition macro in result.Preprocessed.Macros.All )
        {
            // Only macros literally defined in THIS file; inserted ones belong to their GSH.
            if ( macro.SourceFile is null )
            {
                macros.Add(new MacroRecord(
                    macro.Name,
                    macro.IsFunctionLike,
                    macro.Parameters ?? [],
                    macro.NameRange,
                    macro.Documentation ?? ""));
            }
        }

        ImmutableArray<DependencyEdge>.Builder dependencies = ImmutableArray.CreateBuilder<DependencyEdge>();
        foreach ( InsertEdge insert in result.Preprocessed.Inserts )
        {
            if ( insert.ContainingFile is null )
            {
                dependencies.Add(new DependencyEdge(insert.RawPath, insert.ResolvedPath ?? "", IsInsert: true, insert.DirectiveRange));
            }
        }

        foreach ( GSCode.Parser.Syntax.Ast.AstNode element in result.Tree.Root.Elements )
        {
            if ( element is GSCode.Parser.Syntax.Ast.UsingNode usingNode )
            {
                dependencies.Add(new DependencyEdge(usingNode.Path, "", IsInsert: false, usingNode.PathRange));
            }

            // #include is the Infinity Ward import; an edge like #using's (resolved lazily per
            // context), so the include graph exists for navigation, rename and merge scoping. A
            // file is one dialect, so #using and #include never mix in the same record.
            if ( element is GSCode.Parser.Syntax.Ast.IncludeNode includeNode )
            {
                dependencies.Add(new DependencyEdge(includeNode.Path, "", IsInsert: false, includeNode.PathRange));
            }
        }

        return new ScriptRecord
        {
            // The record's path IS the store key, so normalizing here rather than trusting
            // callers keeps lookups and same-file comparisons (e.g. private visibility) sound.
            Path = PathUtil.NormalizeAbsolute(result.FilePath),
            Language = result.Language,
            ContextId = ContextIdOf(context),
            RelativePath = relativePath,
            ContentHash = ComputeContentHash(result.Text.Text),
            Namespaces = result.Extraction.Namespaces,
            Functions = result.Extraction.Functions,
            Classes = result.Extraction.Classes,
            Macros = macros.ToImmutable(),
            Dependencies = dependencies.ToImmutable(),
            PathCallTargets = PathCallTargetsOf(result),
            References = result.Extraction.References,
            Diagnostics = result.AllDiagnostics,
            IsDirty = isDirty,
        };
    }

    /// <summary>The content-hash function used for cache invalidation (xxHash64 of the UTF-8 text).</summary>
    public static ulong ComputeContentHash(string text)
    {
        return XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>The stable string form of a context ("raw", "mod:name", "workspace:folder").</summary>
    public static string ContextIdOf(ResolutionContext context)
    {
        switch ( context.Kind )
        {
            case ResolutionContextKind.Raw:
                return "raw";
            case ResolutionContextKind.Mod:
                return "mod:" + context.ModName;
            default:
                return "workspace:" + context.BaseFolder;
        }
    }

    /// <summary>
    /// Visibility rule between contexts: raw sees raw; mod M sees {mod M, raw};
    /// workspace sees {any workspace, raw}. Mods NEVER see each other or workspaces.
    /// </summary>
    public static bool CanSee(string askingContextId, string recordContextId)
    {
        if ( askingContextId == recordContextId )
        {
            return true;
        }

        if ( recordContextId == "raw" )
        {
            return true;
        }

        bool askingIsWorkspace = askingContextId.StartsWith("workspace:", StringComparison.Ordinal);
        bool recordIsWorkspace = recordContextId.StartsWith("workspace:", StringComparison.Ordinal);

        return askingIsWorkspace && recordIsWorkspace;
    }
}
