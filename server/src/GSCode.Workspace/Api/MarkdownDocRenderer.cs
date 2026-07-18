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
    /// <summary>Full-suite markdown for a script function: prototype, summary, region, params, examples.</summary>
    public static string RenderFunction(FunctionSymbol function)
    {
        StringBuilder markdown = new();

        markdown.Append("```gsc\n");
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

    /// <summary>Markdown for a builtin: prototype (first overload), description, extra overloads, example.</summary>
    public static string RenderBuiltin(BuiltinFunction builtin)
    {
        StringBuilder markdown = new();

        markdown.Append("```gsc\n");
        markdown.Append(BuiltinSignature(builtin, builtin.Overloads.FirstOrDefault()));
        markdown.Append("\n```");

        if ( builtin.Description.Length > 0 )
        {
            markdown.Append("\n\n---\n\n").Append(builtin.Description);
        }

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

    /// <summary>Markdown for a macro: its define form plus any trailing-comment documentation.</summary>
    public static string RenderMacro(MacroRecord macro)
    {
        StringBuilder markdown = new();

        markdown.Append("```gsc\n#define ").Append(macro.Name);
        if ( macro.IsFunctionLike )
        {
            markdown.Append('(').Append(string.Join(", ", macro.Parameters)).Append(')');
        }

        markdown.Append("\n```");

        if ( macro.Documentation.Length > 0 )
        {
            markdown.Append("\n\n---\n\n").Append(CleanComment(macro.Documentation));
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
