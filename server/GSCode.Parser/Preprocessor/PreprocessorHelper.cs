/**
	GSCode.NET Language Server
    Copyright (C) 2022 Blakintosh

    This program is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */

using GSCode.Data;
using GSCode.Data.Models;
using GSCode.Parser.Data;
using GSCode.Parser.Util;
using Microsoft.VisualStudio.LanguageServer.Protocol;

namespace GSCode.Parser.Preprocessor;

/// <summary>
/// Records script defines for semantics & usage
/// </summary>
/// <param name="Source">The source name token</param>
/// <param name="RawDefineTokens">The tokens #define to the last token of the expansion, excluding comments</param>
/// <param name="ExpansionTokens">When used, the key expands to these tokens</param>
/// <param name="Parameters">List of parameters</param>
/// <param name="Documentation">Documentation for the define if it ends in a comment</param>
public record ScriptDefine(Token Source, List<Token> RawDefineTokens, List<Token> ExpansionTokens,
    List<Token> Parameters, string? Documentation = null) : ISenseToken
{
    public Range Range { get; } = Source.TextRange;

    public string SemanticTokenType { get; } = "macro";
    public string[] SemanticTokenModifiers { get; } = Array.Empty<string>();

    public Hover GetHover()
    {
        return new()
        {
            Range = Range,
            Contents = new MarkupContent()
            {
                Kind = MarkupKind.Markdown,
                Value = string.Format("```gsc\n{0}\n```\n{1}",
                    ParserUtil.ProduceSnippetString(RawDefineTokens), GetFormattedDocumentation())
            }
        };
    }

    private string GetFormattedDocumentation()
    {
        if (!string.IsNullOrEmpty(Documentation))
        {
            return string.Format("---\n{0}", Documentation);
        }
        return string.Empty;
    }
}

/// <summary>
/// Records usages of a macro for semantics & hovers
/// </summary>
/// <param name="Source">The macro token source</param>
/// <param name="DefineSource">The define this macro is from</param>
/// <param name="ExpansionTokens">The expansion of this macro</param>
public record ScriptMacro(Token Source, ScriptDefine DefineSource, List<Token> ExpansionTokens) : ISenseToken
{
    public Range Range { get; } = Source.TextRange;

    public string SemanticTokenType { get; } = SemanticTokenTypes.Macro;

    public string[] SemanticTokenModifiers { get; } = Array.Empty<string>();

    public Hover GetHover()
    {
        string defineSnippet = ParserUtil.ProduceSnippetString(DefineSource.RawDefineTokens);
        string expansionSnippet = ParserUtil.ProduceSnippetString(ExpansionTokens);

        Hover hover = new()
        {
            Contents = new MarkupContent()
            {
                Kind = MarkupKind.Markdown,
                Value = string.Format("```gsc\n{0}\n```\n{1}\n\n---\n{2}\n```gsc\n{3}\n```", 
                    defineSnippet, GetFormattedDocumentation(), "Expands to:", expansionSnippet)
            },
            Range = Source.TextRange,
        };

        return hover;
    }

    private string GetFormattedDocumentation()
    {
        if (!string.IsNullOrEmpty(DefineSource.Documentation))
        {
            return string.Format("---\n{0}", DefineSource.Documentation);
        }
        return string.Empty;
    }
}

internal sealed class PreprocessorHelper
{
    public Dictionary<string, ScriptDefine> Defines { get; } = new();
    public List<ScriptMacro> MacroUses { get; } = new();
    public ParserIntelliSense Sense { get; }
    public string ScriptFile { get; }

    public PreprocessorHelper(string scriptFile, ParserIntelliSense sense)
    {
        ScriptFile = scriptFile;
        Sense = sense;
    }

    public void AddSenseDiagnostic(Range range, GSCErrorCodes code, params object?[] arguments)
    {
        Sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(range, DiagnosticSources.Preprocessor, code, arguments));
    }
}
