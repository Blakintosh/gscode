using System.Collections.Immutable;
using GSCode.Core.Diagnostics;
using GSCode.Core.Symbols;

namespace GSCode.Parser.Extraction;

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
    ImmutableArray<Diagnostic> Diagnostics);
