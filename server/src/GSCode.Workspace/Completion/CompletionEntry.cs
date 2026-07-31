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

/// <summary>
/// How much punctuation a completed call brings with it (the gscode.completion.callPunctuation
/// setting).
/// </summary>
public enum CallPunctuation
{
    /// <summary>Insert the bare name; the user types everything else.</summary>
    Off,

    /// <summary>Insert `name()` with the cursor between the parentheses.</summary>
    Parens,

    /// <summary>Also close a STATEMENT with a semicolon: `name(&lt;cursor&gt;);`.</summary>
    ParensAndSemicolon,
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

    /// <summary>A folder in a #using/#insert path — inserts a trailing '\' and reopens the list.</summary>
    PathSegment,

    /// <summary>A script or header file: the end of a #using/#insert path.</summary>
    PathFile,
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
/// <param name="Namespace">
/// The declaring namespace, carried so completionItem/resolve can find the symbol again and
/// render its full documentation. Empty for anything without one (builtins, macros, keywords).
/// </param>
/// <param name="RetriggerCompletion">
/// Whether accepting this entry should immediately reopen the suggestion list.
///
/// For entries that insert a snippet landing the cursor somewhere with its own vocabulary —
/// `#precache( "&lt;here&gt;", … )` wants asset types, `#using &lt;here&gt;;` wants path segments. Nothing
/// otherwise reopens the list, so the user had to delete the inserted quotes and retype one just
/// to fire the '"' trigger character again.
/// </param>
public sealed record CompletionEntry(
    string Label,
    CompletionKind Kind,
    string Detail = "",
    string InsertText = "",
    string Documentation = "",
    string FilterText = "",
    string Namespace = "",
    bool RetriggerCompletion = false);
