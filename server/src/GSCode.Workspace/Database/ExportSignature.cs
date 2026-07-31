using System.IO.Hashing;
using System.Text;
using GSCode.Core.Symbols;

namespace GSCode.Workspace.Database;

/// <summary>
/// A hash of everything about a file that ANOTHER file's diagnostics can observe.
///
/// The cross-file lints read their neighbours: whether a <c>#using</c> contributes a namespace,
/// whether a called function exists, is private, takes that many arguments, or lives in a dev
/// block. So editing one file can invalidate the diagnostics of every file that reaches it — but
/// re-linting every open document on every keystroke is far too expensive, and almost every
/// keystroke changes nothing another file could see.
///
/// This is what separates the two. Typing inside a function body, renaming a local, adding a
/// comment: the signature is unchanged and nothing else needs revisiting. Renaming a function,
/// changing a <c>#namespace</c>, adding a parameter, marking something private: the signature
/// moves, and the files that depend on it are stale.
///
/// Deliberately EXCLUDES ranges, bodies, assignments, references and diagnostics. Those churn
/// constantly while typing and no other file's lints read them — including them would make the
/// signature change on every keystroke and defeat the whole point.
/// </summary>
public static class ExportSignature
{
    /// <summary>
    /// The signature of a record's cross-file surface. Stable across processes (xxHash over a
    /// canonical rendering, not <see cref="System.HashCode"/>), so a record restored from the
    /// persistent cache compares equal to the same file re-analysed.
    /// </summary>
    public static ulong Of(ScriptRecord record)
    {
        StringBuilder rendered = new();

        // Identity: a file that moves changes what a path call or import resolves to.
        rendered.Append(record.RelativePath).Append('\n');

        foreach ( string declared in record.DeclaredNamespaces )
        {
            rendered.Append("ns:").Append(declared).Append('\n');
        }

        foreach ( FunctionSymbol function in record.Functions )
        {
            // Everything a caller's lints ask about a callee, and nothing else. Arity and varargs
            // are what ArgumentCountLint compares; the flags are what PrivateAccessLint and
            // DevBlockCallLint gate on.
            rendered.Append("fn:").Append(function.Namespace).Append("::").Append(function.KeyName)
                .Append('/').Append(function.Parameters.Length)
                .Append(function.HasVarargs ? "+" : "")
                .Append(function.IsPrivate ? "p" : "")
                .Append(function.IsAutoexec ? "a" : "")
                .Append(function.IsDevOnly ? "d" : "");

            // Default values decide how few arguments a call may legally pass.
            foreach ( ParameterSymbol parameter in function.Parameters )
            {
                rendered.Append(parameter.DefaultValueText.Length > 0 ? "=" : "-");
            }

            rendered.Append('\n');
        }

        foreach ( ClassSymbol classSymbol in record.Classes )
        {
            rendered.Append("cl:").Append(classSymbol.Namespace).Append("::").Append(classSymbol.KeyName)
                .Append(':').Append(classSymbol.ParentKeyName ?? "").Append('\n');
        }

        // A header's macros are visible to every file that inserts it.
        foreach ( MacroRecord macro in record.Macros )
        {
            rendered.Append("mc:").Append(macro.Name).Append('/').Append(macro.Parameters.Length).Append('\n');
        }

        // Imports, because reference scoping on the merge dialects asks which files a record can
        // reach — a #include added here changes what an unqualified call over there resolves to.
        foreach ( DependencyEdge edge in record.Dependencies )
        {
            rendered.Append(edge.IsInsert ? "in:" : "us:").Append(edge.RawPath).Append('\n');
        }

        return XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(rendered.ToString()));
    }
}
