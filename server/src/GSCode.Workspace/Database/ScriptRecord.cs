using System.Collections.Immutable;
using GSCode.Core.Diagnostics;
using GSCode.Core.Symbols;
using GSCode.Core.Text;

namespace GSCode.Workspace.Database;

/// <summary>A macro surfaced from a file (definitions only; bodies stay parser-side).</summary>
/// <param name="Name">Exact-case macro name (the case-sensitive space).</param>
/// <param name="IsFunctionLike">Whether it declares parameters.</param>
/// <param name="Parameters">Parameter names in order (empty for object-like).</param>
/// <param name="NameRange">Range of the name at its definition (go-to-def target).</param>
/// <param name="Documentation">Trailing same-line comment, or "".</param>
public sealed record MacroRecord(
    string Name,
    bool IsFunctionLike,
    ImmutableArray<string> Parameters,
    TextRange NameRange,
    string Documentation);

/// <summary>One #using or #insert dependency of a file.</summary>
/// <param name="RawPath">The path as written.</param>
/// <param name="ResolvedPath">Normalized absolute target, or "" when unresolved.</param>
/// <param name="IsInsert">True for #insert edges (GSH), false for #using.</param>
/// <param name="Range">Directive range in the root file.</param>
public sealed record DependencyEdge(string RawPath, string ResolvedPath, bool IsInsert, TextRange Range);

/// <summary>
/// The complete, immutable knowledge about one script file. Updates build a whole new
/// record off-thread and swap it in atomically — readers always see a consistent file.
/// Closed files keep ONLY this record (no tokens/AST), keeping indexed memory flat.
/// </summary>
public sealed record ScriptRecord
{
    /// <summary>Normalized absolute path — the database key.</summary>
    public required string Path { get; init; }

    public required ScriptLanguage Language { get; init; }

    /// <summary>The file's resolution world, stringified: "raw", "mod:name", or "workspace:folder".</summary>
    public required string ContextId { get; init; }

    /// <summary>
    /// Script-relative identity under its root (e.g. scripts\shared\util.gsc), or ""
    /// when outside every root. Same RelativePath in overlay and raw = shadowing pair.
    /// </summary>
    public string RelativePath { get; init; } = "";

    /// <summary>xxHash-style content hash of the analysed text (cache invalidation key).</summary>
    public required ulong ContentHash { get; init; }

    /// <summary>
    /// The namespace REGIONS of the file, for the positional question only. For "which namespaces
    /// does this file declare into", use <see cref="DeclaredNamespaces"/> — see
    /// <see cref="NamespaceSpan"/> for why the two are not the same list.
    /// </summary>
    public ImmutableArray<NamespaceSpan> Namespaces { get; init; } = [];

    /// <summary>
    /// The namespaces this file declares into. Computed once at build time rather than per query:
    /// the lints ask it for every <c>#using</c> target and completion asks it for every imported
    /// file, on every keystroke.
    /// </summary>
    public ImmutableArray<string> DeclaredNamespaces { get; init; } = [];

    public ImmutableArray<FunctionSymbol> Functions { get; init; } = [];
    public ImmutableArray<ClassSymbol> Classes { get; init; } = [];
    public ImmutableArray<MacroRecord> Macros { get; init; } = [];
    public ImmutableArray<DependencyEdge> Dependencies { get; init; } = [];

    /// <summary>
    /// The path calls this script makes, each with the file it names and the range of the called
    /// name. The RANGE is what matters: scoping has to decide per REFERENCE, not per file. A file
    /// that path-calls combat also declares its own main() and may call cover_behavior::main(), and
    /// those are three different functions sharing one key.
    ///
    /// The files this script reaches by PATH — <c>maps\mp\_utility::foo()</c> — as script-relative
    /// paths without extension, deduplicated.
    ///
    /// Kept separate from <see cref="Dependencies"/> on purpose: a path call is not an import. The
    /// Infinity Ward games reach a function by naming its file inline, with no <c>#include</c> at
    /// all, so folding these into the dependency edges would make an unused import look used and
    /// would misdirect the dependency rewrite. But they ARE how one file reaches another's
    /// functions, so reference scoping has to see them.
    /// </summary>
    public ImmutableArray<GSCode.Parser.Extraction.PathCallReference> PathCallTargets { get; init; } = [];
    public ImmutableArray<ReferenceEntry> References { get; init; } = [];
    public ImmutableArray<Diagnostic> Diagnostics { get; init; } = [];

    /// <summary>True while the record reflects unsaved editor text (never persisted).</summary>
    public bool IsDirty { get; init; }
}
