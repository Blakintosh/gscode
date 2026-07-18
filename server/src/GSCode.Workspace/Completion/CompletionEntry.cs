namespace GSCode.Workspace.Completion;

/// <summary>The kind of a completion item (maps onto LSP CompletionItemKind in the handler).</summary>
public enum CompletionKind
{
    Function,
    Class,
    Keyword,
    Variable,
    Field,
    Macro,
    Namespace,
    AssetType,
    PathSegment,
    Snippet,
}

/// <summary>
/// One completion suggestion, LSP-free. InsertText defaults to Label when empty; snippet
/// insertion is signalled by CompletionKind.Snippet with placeholders in InsertText.
/// </summary>
public sealed record CompletionEntry(
    string Label,
    CompletionKind Kind,
    string Detail = "",
    string InsertText = "",
    string Documentation = "");
