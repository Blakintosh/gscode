using System.IO.Hashing;
using System.Text;
using GSCode.Core;
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

        // A header is pasted into every file that #inserts it, so every BYTE of it is observable —
        // a macro's body decides the dependent's tokens, and with them its parse, its diagnostics
        // and its hover. The macro rendering below carries names and arities only, so
        // `#define CAP 5` becoming `#define CAP 99` would leave it identical and every open
        // dependent showing 5. Content is the honest answer for a header and costs nothing: headers
        // are not what anyone types into all day, and the hash is already stored.
        if ( record.Language == ScriptLanguage.Gsh )
        {
            rendered.Append("gsh:").Append(record.ContentHash).Append('\n');
        }

        foreach ( string declared in record.DeclaredNamespaces )
        {
            rendered.Append("ns:").Append(declared).Append('\n');
        }

        foreach ( FunctionSymbol function in record.Functions )
        {
            AppendFunction(rendered, function);
        }

        foreach ( ClassSymbol classSymbol in record.Classes )
        {
            rendered.Append("cl:").Append(classSymbol.Namespace).Append("::").Append(classSymbol.KeyName)
                .Append(':').Append(classSymbol.ParentKeyName ?? "").Append('\n');

            // The METHOD SET, not just the class name and its parent. A derived class inherits every
            // method its ancestors declare, so adding one to a base class changes what resolves in
            // every file that subclasses it — and while only the name and parent were hashed, that
            // edit left those files' signatures identical and their diagnostics never revisited.
            //
            // Rendered like a function so it carries everything a caller's lints ask: arity for the
            // argument count rule, the flags for the privacy and dev-block rules, and the defaults
            // marker for how few arguments a call may legally pass. Constructors and destructors are
            // left out — no caller can name one, so no caller's diagnostics can depend on it.
            foreach ( FunctionSymbol method in classSymbol.Methods )
            {
                rendered.Append("me:").Append(classSymbol.KeyName).Append("::").Append(method.KeyName)
                    .Append('/').Append(method.Parameters.Length)
                    .Append(method.HasVarargs ? "+" : "")
                    .Append(method.IsPrivate ? "p" : "")
                    .Append(method.IsDevOnly ? "d" : "");

                foreach ( ParameterSymbol parameter in method.Parameters )
                {
                    rendered.Append(parameter.DefaultValueText.Length > 0 ? "=" : "-");
                }

                rendered.Append('\n');
            }
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

    /// <summary>
    /// Everything a caller's lints ask about a callee, and nothing else. Arity and varargs are what
    /// ArgumentCountLint compares; the flags are what PrivateAccessLint and DevBlockCallLint gate on;
    /// default values decide how few arguments a call may legally pass. One spelling for
    /// <see cref="Of"/> and <see cref="ChangedFunctionKeys"/>, so a change the signature sees is a
    /// change the narrowed re-lint sees.
    /// </summary>
    private static void AppendFunction(StringBuilder rendered, FunctionSymbol function)
    {
        rendered.Append("fn:").Append(function.Namespace).Append("::").Append(function.KeyName)
            .Append('/').Append(function.Parameters.Length)
            .Append(function.HasVarargs ? "+" : "")
            .Append(function.IsPrivate ? "p" : "")
            .Append(function.IsAutoexec ? "a" : "")
            .Append(function.IsDevOnly ? "d" : "");
        foreach ( ParameterSymbol parameter in function.Parameters )
        {
            rendered.Append(parameter.DefaultValueText.Length > 0 ? "=" : "-");
        }

        rendered.Append('\n');
    }

    /// <summary>
    /// WHICH of a file's top-level functions an edit added, removed or re-signed, under the key their
    /// callers' references were indexed with (<see cref="GameProfile.KeyNamespace"/>) — so a
    /// full-mode re-lint can reach only the files calling those, rather than every file calling
    /// anything the edited one declares.
    ///
    /// Null when the change cannot be put down to functions: no previous record, or the file moved,
    /// or its <c>#namespace</c>s or imports changed. Each of those moves what other files resolve to
    /// in ways no one function names, so the caller re-lints every caller of the file, as before.
    /// Classes and macros are not looked at; the closed re-lint does not cover them either.
    /// </summary>
    public static IReadOnlySet<SymbolKey>? ChangedFunctionKeys(ScriptRecord? before, ScriptRecord after, GameProfile game)
    {
        if ( before is null
            || !string.Equals(before.RelativePath, after.RelativePath, StringComparison.Ordinal)
            || !before.DeclaredNamespaces.SequenceEqual(after.DeclaredNamespaces)
            || !SameImports(before, after) )
        {
            return null;
        }

        Dictionary<SymbolKey, string> was = RenderedFunctions(before, game);
        Dictionary<SymbolKey, string> now = RenderedFunctions(after, game);
        HashSet<SymbolKey> changed = [];
        foreach ( KeyValuePair<SymbolKey, string> function in was )
        {
            if ( !now.TryGetValue(function.Key, out string? rendered) || !string.Equals(rendered, function.Value, StringComparison.Ordinal) )
            {
                changed.Add(function.Key);
            }
        }

        foreach ( SymbolKey key in now.Keys )
        {
            if ( !was.ContainsKey(key) )
            {
                changed.Add(key);
            }
        }

        return changed;
    }

    /// <summary>Each top-level function's rendering, by its call key; two declarations of one key render together.</summary>
    private static Dictionary<SymbolKey, string> RenderedFunctions(ScriptRecord record, GameProfile game)
    {
        Dictionary<SymbolKey, StringBuilder> rendered = [];
        foreach ( FunctionSymbol function in record.Functions )
        {
            SymbolKey key = new(game.KeyNamespace(function.Namespace), function.KeyName, SymbolKind.Function);
            if ( !rendered.TryGetValue(key, out StringBuilder? text) )
            {
                text = new StringBuilder();
                rendered[key] = text;
            }

            AppendFunction(text, function);
        }

        Dictionary<SymbolKey, string> done = [];
        foreach ( KeyValuePair<SymbolKey, StringBuilder> function in rendered )
        {
            done[function.Key] = function.Value.ToString();
        }

        return done;
    }

    private static bool SameImports(ScriptRecord before, ScriptRecord after)
    {
        if ( before.Dependencies.Length != after.Dependencies.Length )
        {
            return false;
        }

        for ( int index = 0; index < before.Dependencies.Length; index++ )
        {
            DependencyEdge was = before.Dependencies[index];
            DependencyEdge now = after.Dependencies[index];
            if ( was.IsInsert != now.IsInsert || !string.Equals(was.RawPath, now.RawPath, StringComparison.Ordinal) )
            {
                return false;
            }
        }

        return true;
    }
}
