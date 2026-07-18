using System.Collections.Immutable;
using GSCode.Core.Diagnostics;
using GSCode.Parser.Syntax.Ast;

namespace GSCode.Parser.Syntax;

/// <summary>The parser's output: a tree that always covers the whole file, plus syntax diagnostics.</summary>
public sealed record ParseTree(ScriptNode Root, ImmutableArray<Diagnostic> Diagnostics);
