namespace GSCode.Server.Formatting;

/// <summary>
/// The spellings <see cref="FormatOptions.FixCasing"/> writes names with, answered by something that
/// can see the workspace, which the formatter cannot. Every method answers null to leave a name as
/// written: when it resolves to nothing, or to more than one spelling.
/// </summary>
public interface ICasingLookup
{
    /// <summary>
    /// The spelling for a call to, or reference to, a function.
    /// </summary>
    /// <param name="qualifier">The namespace or class before <c>::</c>, or null for a bare name.</param>
    /// <param name="name">The name as written.</param>
    /// <param name="preferScript">
    /// Whether a script function wins over a builtin of the same name. A bare call resolves to the
    /// builtin first; a threaded call or a function reference cannot mean a builtin, so it resolves
    /// to the script function.
    /// </param>
    string? Function(string? qualifier, string name, bool preferScript);

    /// <summary>The spelling for the namespace or class named before a <c>::</c>.</summary>
    string? Qualifier(string name);

    /// <summary>The spelling for a class named after <c>new</c> or as a base class.</summary>
    string? Class(string name);
}
