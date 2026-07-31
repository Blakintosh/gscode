using System.Collections.Immutable;
using GSCode.Core.Diagnostics;
using GSCode.Core.Symbols;
using GSCode.Core.Text;

namespace GSCode.Parser.Extraction;

/// <summary>
/// A path-qualified call/reference site — <c>maps\mp\_utility::foo()</c>. The name is keyed
/// <c>(null, name)</c> like any merge-dialect call so it unions for find-references, but the PATH it
/// explicitly names is kept here so go-to-definition can resolve it to that ONE file rather than the
/// whole include scope. <see cref="NameRange"/> matches the reference's range, which is how the two
/// are paired. The leading <c>::foo</c> local form has an empty path and is not recorded.
/// </summary>
public sealed record PathCallReference(string Path, TextRange NameRange);

/// <summary>
/// The extracted symbol surface of one file: namespaces, declarations with their
/// contained assignments, the flat classified reference list, and semantic diagnostics.
/// This is the payload the Workspace layer builds ScriptRecords from.
/// </summary>
public sealed record ExtractionResult(
    ImmutableArray<NamespaceSpan> Namespaces,
    ImmutableArray<FunctionSymbol> Functions,
    ImmutableArray<ClassSymbol> Classes,
    ImmutableArray<ReferenceEntry> References,
    ImmutableArray<Diagnostic> Diagnostics,
    ImmutableArray<PathCallReference> PathCalls)
{
    /// <summary>
    /// The namespaces this file declares into — the SET question, as opposed to the positional one
    /// <see cref="Namespaces"/> answers. See <see cref="NamespaceSpan"/> for why the two differ and
    /// why reading the spans as a set yields a phantom named after the file.
    /// </summary>
    public ImmutableArray<string> DeclaredNamespaces => DeclaredNamespaceSet.From(Functions, Classes);
}
