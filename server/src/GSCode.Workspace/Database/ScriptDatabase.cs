using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.IO.Hashing;
using System.Text;
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

    public void UpsertGsh(ScriptRecord record)
    {
        _gshRecords[record.Path] = record;
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
        }

        return new ScriptRecord
        {
            Path = result.FilePath,
            Language = result.Language,
            ContextId = ContextIdOf(context),
            RelativePath = relativePath,
            ContentHash = ComputeContentHash(result.Text.Text),
            Namespaces = result.Extraction.Namespaces,
            Functions = result.Extraction.Functions,
            Classes = result.Extraction.Classes,
            Macros = macros.ToImmutable(),
            Dependencies = dependencies.ToImmutable(),
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
