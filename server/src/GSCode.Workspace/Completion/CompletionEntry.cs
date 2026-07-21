namespace GSCode.Workspace.Completion;

/// <summary>
/// How widely assignment-derived field names are offered after a `.` (the
/// gscode.completion.fieldScope setting).
/// </summary>
public enum FieldScope
{
    /// <summary>Only fields seen assigned on THIS owner — `level.` offers level's fields.</summary>
    Owner,

    /// <summary>Every field name assigned on any owner: broader, and noisier.</summary>
    All,
}

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
    Literal,
    Snippet,
}

/// <summary>
/// One completion suggestion, LSP-free. InsertText defaults to Label when empty; snippet
/// insertion is signalled by CompletionKind.Snippet with placeholders in InsertText.
/// </summary>
/// <param name="FilterText">
/// What the editor matches the typed prefix against, when that differs from the label. Needed
/// for directives: the language's word pattern excludes '#', so after typing "#p" the editor's
/// current word is "p" and a "#precache" label would be filtered out.
/// </param>
public sealed record CompletionEntry(
    string Label,
    CompletionKind Kind,
    string Detail = "",
    string InsertText = "",
    string Documentation = "",
    string FilterText = "");
