using System.Collections.Immutable;
using System.Text;
using GSCode.Core.Symbols;

namespace GSCode.Core.Docs;

/// <summary>
/// Renders an EMPTY ScriptDoc block for a function that has none: the tags the stock scripts use,
/// with everything the signature already states filled in and everything it cannot left as a
/// placeholder to overwrite.
///
/// The counterpart to <see cref="ScriptDocComment.Parse"/>, and deliberately its inverse: what this
/// writes, that reads back. Both dialects' spellings are produced here rather than at the call site
/// so the difference is stated once — and it is not only the delimiters. A pre-BO3 block is an
/// ORDINARY block comment, so the <c>///ScriptDocBegin</c>/<c>///ScriptDocEnd</c> fence is the only
/// thing marking it as documentation at all: without it the extractor treats the result as a
/// comment that happens to sit above a function (see <c>ScriptDocComment.HasTripleSlashFence</c>)
/// and the hover stays empty.
///
/// Every line is quoted, in both dialects. That is the stock convention — 15,226 of the shipped
/// functions' blocks are written that way, which is why <see cref="ScriptDocComment.Parse"/> has an
/// unquote step at all.
/// </summary>
public static class ScriptDocTemplate
{
    /// <summary>
    /// The block for <paramref name="functionName"/>, ending in a newline so it can be inserted
    /// directly above the declaration line.
    /// </summary>
    /// <param name="indent">
    /// The declaration's own leading whitespace, repeated on every line. A method sits inside a
    /// class body, so a block rendered flush left above it would be the only thing in the file at
    /// column zero.
    /// </param>
    public static string Render(
        string functionName,
        ImmutableArray<ParameterSymbol> parameters,
        bool hasVarargs,
        ScriptDocStyle style,
        string indent = "")
    {
        StringBuilder block = new();

        block.Append(indent).Append(style == ScriptDocStyle.AtSign ? "/@" : "/*").Append('\n');
        if ( style == ScriptDocStyle.TripleSlash )
        {
            block.Append(indent).Append("///ScriptDocBegin").Append('\n');
        }

        block.Append(indent).Append("\"Name: ").Append(functionName).Append('(');
        AppendParameterList(block, parameters, hasVarargs);
        block.Append(")\"").Append('\n');

        block.Append(indent).Append("\"Summary: <summary>\"").Append('\n');

        foreach ( ParameterSymbol parameter in parameters )
        {
            // A parameter with a default value is one a caller may leave out, which is exactly what
            // OptionalArg means. Nothing else in the signature distinguishes the two.
            bool optional = parameter.DefaultValueText.Length > 0;
            block.Append(indent)
                .Append(optional ? "\"OptionalArg: " : "\"MandatoryArg: ")
                .Append(Bracket(parameter.Name, optional))
                .Append(" : <description>\"")
                .Append('\n');
        }

        if ( style == ScriptDocStyle.TripleSlash )
        {
            block.Append(indent).Append("///ScriptDocEnd").Append('\n');
        }

        block.Append(indent).Append(style == ScriptDocStyle.AtSign ? "@/" : "*/").Append('\n');
        return block.ToString();
    }

    private static void AppendParameterList(
        StringBuilder block, ImmutableArray<ParameterSymbol> parameters, bool hasVarargs)
    {
        for ( int index = 0; index < parameters.Length; index++ )
        {
            if ( index > 0 )
            {
                block.Append(',');
            }

            block.Append(' ').Append(Bracket(parameters[index].Name, parameters[index].DefaultValueText.Length > 0));
        }

        if ( hasVarargs )
        {
            if ( parameters.Length > 0 )
            {
                block.Append(',');
            }

            block.Append(" ...");
        }

        if ( parameters.Length > 0 || hasVarargs )
        {
            block.Append(' ');
        }
    }

    /// <summary>
    /// The stock spelling of an argument name in a doc line: angle brackets for one that must be
    /// passed, square for one that may be left out. <see cref="ScriptDocComment.Parse"/> accepts
    /// either and strips both, so this is for the reader.
    /// </summary>
    private static string Bracket(string name, bool optional)
    {
        return optional ? "[" + name + "]" : "<" + name + ">";
    }
}
