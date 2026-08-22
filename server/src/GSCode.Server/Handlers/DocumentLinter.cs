using System.Collections.Immutable;
using GSCode.Core.Diagnostics;
using GSCode.Parser;
using GSCode.Server.Configuration;
using GSCode.Workspace.Analysis;
using GSCode.Workspace.Api;
using GSCode.Workspace.Database;
using GSCode.Workspace.Documents;

namespace GSCode.Server.Handlers;

/// <summary>
/// One open document's full diagnostic set: its parse diagnostics plus the cross-file lints.
///
/// A thin thing on purpose — it holds no state and decides nothing. What it owns is the ARGUMENT
/// LIST. <see cref="WorkspaceLints.Analyze"/> takes seven arguments, four of which are workspace
/// singletons that never vary within a session, and it was called from two handlers that each
/// injected all four for that one line and nothing else. Two copies of a seven-argument call is two
/// places to update when the pipeline gains an input, and one of them is easy to miss.
/// </summary>
public sealed class DocumentLinter
{
    private readonly ScriptDatabase _database;
    private readonly ResolverHolder _resolver;
    private readonly BuiltinApiSet _builtins;
    private readonly ObjectFields _objectFields;

    public DocumentLinter(
        ScriptDatabase database, ResolverHolder resolver, BuiltinApiSet builtins, ObjectFields objectFields)
    {
        _database = database;
        _resolver = resolver;
        _builtins = builtins;
        _objectFields = objectFields;
    }

    /// <summary>
    /// The document's diagnostics, from the shared pipeline so the editor and any offline sweep
    /// report the same thing.
    /// </summary>
    /// <param name="result">
    /// The analysis to lint. Passed in rather than read off the document: the callers have just
    /// produced it, and re-reading <c>LatestResult</c> here would let a concurrent analysis swap it
    /// for a different parse than the one they published a version number for.
    /// </param>
    public ImmutableArray<Diagnostic> Analyze(OpenDocument document, ParseResult result)
    {
        return WorkspaceLints.Analyze(
            result, document.Language, document.Path, _database, _resolver.Current, _builtins, _objectFields);
    }
}
