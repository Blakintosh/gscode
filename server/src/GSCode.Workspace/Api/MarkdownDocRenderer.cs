using System.Text;
using GSCode.Core.Docs;
using GSCode.Core.Symbols;
using GSCode.Workspace.Database;

namespace GSCode.Workspace.Api;

/// <summary>
/// Renders hover markdown for every symbol kind in one place (shared by hover, completion
/// detail, and signature help): a fenced signature block, then the doc body. Script docs
/// come from the parsed ScriptDoc block; builtins from the bundled API library.
/// </summary>
public static class MarkdownDocRenderer
{
    /// <summary>
    /// Full-suite markdown for a script function: prototype, summary, region, params, examples.
    ///
    /// <paramref name="ownerClass"/> names the declaring class when this is a method. Worth showing
    /// because for an inherited method it is the answer the reader does not have: the call is
    /// written inside the subclass, and which ancestor it lands on is exactly what is not visible
    /// from the call site.
    /// </summary>
    public static string RenderFunction(FunctionSymbol function, ClassSymbol? ownerClass = null)
    {
        StringBuilder markdown = new();

        markdown.Append("```gsc\n");

        if ( ownerClass is not null )
        {
            markdown.Append("class ").Append(ownerClass.Name).Append('\n');
        }

        markdown.Append(FunctionSignature(function));
        markdown.Append("\n```");

        ScriptDocComment doc = function.Doc;
        if ( !doc.IsNone )
        {
            if ( doc.Summary.Length > 0 )
            {
                markdown.Append("\n\n---\n\n").Append(doc.Summary);
            }

            AppendRegion(markdown, doc);
            AppendParameters(markdown, doc);
            AppendExamples(markdown, doc);
        }

        return markdown.ToString();
    }

    /// <summary>
    /// Markdown for a builtin, matching what a script function's hover shows: prototype,
    /// dev-only warning, description, per-parameter types and descriptions, the return, extra
    /// overloads, and the example.
    ///
    /// The bundled library is far richer than the signature line suggests — 3,240 of the 3,289
    /// GSC parameters carry their own description and 965 overloads name a return type — so
    /// rendering names alone left most of the documentation on the floor.
    /// </summary>
    public static string RenderBuiltin(BuiltinFunction builtin)
    {
        StringBuilder markdown = new();
        BuiltinOverload? primary = builtin.Overloads.FirstOrDefault();

        markdown.Append("```gsc\n");
        markdown.Append(BuiltinSignature(builtin, primary));
        markdown.Append("\n```");

        // Above the description: calling this outside a /# #/ block breaks a shipped mod, which
        // matters more than anything else the hover has to say.
        if ( builtin.IsDevOnly )
        {
            markdown.Append("\n\n**Development only** — calling this outside a `/# #/` block will not work in a release build.");
        }

        if ( builtin.Description.Length > 0 )
        {
            markdown.Append("\n\n---\n\n").Append(builtin.Description);
        }

        AppendBuiltinParameters(markdown, primary);
        AppendBuiltinReturn(markdown, primary);

        // Additional overloads listed compactly under the primary prototype.
        if ( builtin.Overloads.Length > 1 )
        {
            markdown.Append("\n\nOverloads:\n");
            foreach ( BuiltinOverload overload in builtin.Overloads )
            {
                markdown.Append("* `").Append(BuiltinSignature(builtin, overload)).Append("`\n");
            }
        }

        if ( builtin.Example.Length > 0 )
        {
            markdown.Append("\n\nExample:\n```gsc\n").Append(builtin.Example).Append("\n```");
        }

        return markdown.ToString();
    }

    private static void AppendBuiltinParameters(StringBuilder markdown, BuiltinOverload? overload)
    {
        if ( overload is null || overload.Parameters.Length == 0 )
        {
            return;
        }

        markdown.Append("\n\n---\n\nParameters:\n");
        foreach ( BuiltinParameter parameter in overload.Parameters )
        {
            markdown.Append("* `").Append(parameter.Name).Append('`');

            if ( parameter.TypeText.Length > 0 )
            {
                markdown.Append(": `").Append(parameter.TypeText).Append('`');
            }

            if ( !parameter.Mandatory )
            {
                markdown.Append(" *(optional)*");
            }

            if ( parameter.Description.Length > 0 )
            {
                markdown.Append(" — ").Append(parameter.Description);
            }

            markdown.Append('\n');
        }
    }

    private static void AppendBuiltinReturn(StringBuilder markdown, BuiltinOverload? overload)
    {
        // Void is the common case and says nothing worth a line of its own.
        if ( overload is null || overload.ReturnsVoid || overload.ReturnTypeText.Length == 0 )
        {
            return;
        }

        markdown.Append("\n\nReturns: `").Append(overload.ReturnTypeText).Append('`');
    }

    /// <summary>Markdown for a class: its declaration line, with the parent when it has one.</summary>
    public static string RenderClass(ClassSymbol classSymbol)
    {
        string header = classSymbol.ParentKeyName is null
            ? $"class {classSymbol.Name}"
            : $"class {classSymbol.Name} : {classSymbol.ParentKeyName}";

        return "```gsc\n" + header + "\n```";
    }

    /// <summary>
    /// Markdown for a macro: its define form, what it expands to, and any trailing-comment
    /// documentation. The expansion is passed in rather than read off the record, because
    /// macro bodies are deliberately not retained per record — a header inserted by hundreds
    /// of files would otherwise store its bodies hundreds of times over.
    /// </summary>
    public static string RenderMacro(MacroRecord macro, string expansion = "")
    {
        StringBuilder markdown = new();

        markdown.Append("```gsc\n#define ").Append(macro.Name);
        if ( macro.IsFunctionLike )
        {
            markdown.Append('(').Append(string.Join(", ", macro.Parameters)).Append(')');
        }

        // The expansion is what a caller actually wants to see, so it shares the code block
        // with the define rather than sitting below the documentation.
        if ( expansion.Length > 0 )
        {
            markdown.Append('\n').Append(expansion);
        }

        markdown.Append("\n```");

        if ( macro.Documentation.Length > 0 )
        {
            markdown.Append("\n\n---\n\n").Append(CleanComment(macro.Documentation));
        }

        return markdown.ToString();
    }

    /// <summary>
    /// Markdown for a macro whose DEFINE FORM is already on screen: the expansion and the
    /// trailing-comment documentation, without the `#define` line.
    ///
    /// Signature help is the caller. Its label IS the define form — rendered by the client, above
    /// the documentation and with the active argument highlighted — so repeating it here printed
    /// the parameter list twice in a widget the reader is looking at mid-keystroke. Hover has no
    /// label above it and keeps the full form.
    /// </summary>
    public static string RenderMacroExpansion(string expansion, string documentation)
    {
        StringBuilder markdown = new();

        if ( expansion.Length > 0 )
        {
            markdown.Append("```gsc\n").Append(expansion).Append("\n```");
        }

        if ( documentation.Length > 0 )
        {
            // The rule is separating two things that are both there. A body-less #define with a
            // comment has only the comment, and a bare line above it reads as a missing expansion.
            if ( markdown.Length > 0 )
            {
                markdown.Append("\n\n---\n\n");
            }

            markdown.Append(CleanComment(documentation));
        }

        return markdown.ToString();
    }

    private static string FunctionSignature(FunctionSymbol function)
    {
        StringBuilder signature = new();
        if ( function.Namespace.Length > 0 )
        {
            signature.Append(function.Namespace).Append("::");
        }

        signature.Append(function.Name).Append('(');

        for ( int index = 0; index < function.Parameters.Length; index++ )
        {
            ParameterSymbol parameter = function.Parameters[index];
            if ( index > 0 )
            {
                signature.Append(", ");
            }

            if ( parameter.ByRef )
            {
                signature.Append('&');
            }

            signature.Append(parameter.Name);
            if ( parameter.DefaultValueText.Length > 0 )
            {
                signature.Append(" = ").Append(parameter.DefaultValueText);
            }
        }

        if ( function.HasVarargs )
        {
            if ( function.Parameters.Length > 0 )
            {
                signature.Append(", ");
            }

            signature.Append("...");
        }

        signature.Append(')');
        return signature.ToString();
    }

    private static string BuiltinSignature(BuiltinFunction builtin, BuiltinOverload? overload)
    {
        StringBuilder signature = new();
        if ( overload?.CalledOn is not null )
        {
            signature.Append('<').Append(overload.CalledOn).Append("> ");
        }

        signature.Append(builtin.Name).Append('(');
        if ( overload is not null )
        {
            for ( int index = 0; index < overload.Parameters.Length; index++ )
            {
                BuiltinParameter parameter = overload.Parameters[index];
                if ( index > 0 )
                {
                    signature.Append(", ");
                }

                signature.Append(parameter.Name);
                if ( !parameter.Mandatory )
                {
                    signature.Append('?');
                }
            }
        }

        signature.Append(')');
        return signature.ToString();
    }

    private static void AppendRegion(StringBuilder markdown, ScriptDocComment doc)
    {
        if ( doc.CallOn.Length == 0 && doc.Spmp.Length == 0 && doc.Module.Length == 0 )
        {
            return;
        }

        markdown.Append("\n\n---\n");
        if ( doc.CallOn.Length > 0 )
        {
            markdown.Append("\n* Called on: `<").Append(doc.CallOn).Append(">`");
        }

        if ( doc.Spmp.Length > 0 )
        {
            markdown.Append("\n* SPMP: `").Append(doc.Spmp).Append('`');
        }

        if ( doc.Module.Length > 0 )
        {
            markdown.Append("\n* Module: `").Append(doc.Module).Append('`');
        }
    }

    private static void AppendParameters(StringBuilder markdown, ScriptDocComment doc)
    {
        if ( doc.Arguments.Length == 0 )
        {
            return;
        }

        markdown.Append("\n\n---\n\nParameters:\n");
        foreach ( ScriptDocArgument argument in doc.Arguments )
        {
            markdown.Append("* `").Append(argument.Name).Append('`');
            if ( argument.Optional )
            {
                markdown.Append(" *(optional)*");
            }

            if ( argument.Description.Length > 0 )
            {
                markdown.Append(" — ").Append(argument.Description);
            }

            markdown.Append('\n');
        }
    }

    private static void AppendExamples(StringBuilder markdown, ScriptDocComment doc)
    {
        foreach ( string example in doc.Examples )
        {
            markdown.Append("\n\nExample:\n```gsc\n").Append(example).Append("\n```");
        }
    }

    private static string CleanComment(string comment)
    {
        string cleaned = comment.Trim();
        if ( cleaned.StartsWith("//", StringComparison.Ordinal) )
        {
            cleaned = cleaned[2..].Trim();
        }

        return cleaned;
    }
}
