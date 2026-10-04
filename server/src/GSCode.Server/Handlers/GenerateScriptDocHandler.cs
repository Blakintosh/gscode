using GSCode.Core;
using GSCode.Core.Docs;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Workspace.Documents;
using MediatR;
using OmniSharp.Extensions.JsonRpc;
using OmniSharp.Extensions.LanguageServer.Protocol;

namespace GSCode.Server.Handlers;

/// <summary>Request for gscode/generateScriptDoc: document the function at this position.</summary>
[Method("gscode/generateScriptDoc", Direction.ClientToServer)]
public sealed class GenerateScriptDocParams : IRequest<GenerateScriptDocResponse>
{
    public string Uri { get; set; } = "";
    public int Line { get; set; }
    public int Character { get; set; }
}

/// <summary>
/// Response for gscode/generateScriptDoc. Primitives only, so no serializer can rename a key on
/// the way to the client.
/// </summary>
public sealed class GenerateScriptDocResponse
{
    /// <summary>
    /// <c>generated</c> (insert <see cref="Text"/> at the start of <see cref="Line"/>),
    /// <c>documented</c> (the function already has a block), or <c>none</c> (no function here).
    /// </summary>
    public string Status { get; set; } = GenerateScriptDocHandler.NoFunction;

    /// <summary>The function's name, for the client's message; "" when there is none.</summary>
    public string Function { get; set; } = "";

    /// <summary>The line the block goes above: the declaration's first line.</summary>
    public int Line { get; set; }

    /// <summary>The block, ending in a line break, indented to the declaration.</summary>
    public string Text { get; set; } = "";
}

/// <summary>
/// "Generate ScriptDoc block", run from the editor's right-click menu: find the function or method
/// the cursor is in — anywhere in it, declaration or body — and write the doc block it is missing.
///
/// A request rather than a code action, on purpose. As a code action it was offered on every
/// undocumented function, which in the stock scripts is most of them, so a lightbulb sat on nearly
/// every function in a file. An undocumented function is not a fault, so nothing should announce
/// it; the block is written when someone asks for it.
///
/// The block is <see cref="ScriptDocTemplate"/> in the dialect's own style, which is the inverse of
/// <see cref="ScriptDocComment.Parse"/>: what this writes, the extractor reads back as the
/// function's documentation.
/// </summary>
public sealed class GenerateScriptDocHandler : IJsonRpcRequestHandler<GenerateScriptDocParams, GenerateScriptDocResponse>
{
    internal const string Generated = "generated";
    internal const string AlreadyDocumented = "documented";
    internal const string NoFunction = "none";

    private readonly DocumentStore _documents;

    public GenerateScriptDocHandler(DocumentStore documents)
    {
        _documents = documents;
    }

    public Task<GenerateScriptDocResponse> Handle(GenerateScriptDocParams request, CancellationToken cancellationToken)
    {
        // Fresh, because the answer is a position: a parse from before the last keystroke would put
        // the block on the wrong line.
        if ( !_documents.TryAnalyzeFresh(
            DocumentUri.Parse(request.Uri).GetFileSystemPath(), cancellationToken, out _, out ParseResult result) )
        {
            return Task.FromResult(new GenerateScriptDocResponse());
        }

        return Task.FromResult(For(result, new Position(request.Line, request.Character)));
    }

    /// <summary>The answer for one parse and position. Static so tests need no protocol.</summary>
    internal static GenerateScriptDocResponse For(ParseResult result, Position position)
    {
        FunctionSymbol? declaration = DeclarationAt(result, position);
        if ( declaration is null )
        {
            return new GenerateScriptDocResponse();
        }

        if ( !declaration.Doc.IsNone )
        {
            return new GenerateScriptDocResponse { Status = AlreadyDocumented, Function = declaration.Name };
        }

        int line = declaration.FullRange.Start.Line;
        return new GenerateScriptDocResponse
        {
            Status = Generated,
            Function = declaration.Name,
            Line = line,
            Text = ScriptDocTemplate.Render(
                declaration.Name,
                declaration.Parameters,
                declaration.HasVarargs,
                GameProfile.Active.ScriptDocStyle,
                IndentOf(result, line)),
        };
    }

    /// <summary>
    /// The function or method whose range contains <paramref name="position"/>, or null. Both lists
    /// are walked, since <c>Extraction.Functions</c> holds top-level functions only.
    /// </summary>
    private static FunctionSymbol? DeclarationAt(ParseResult result, Position position)
    {
        foreach ( FunctionSymbol function in result.Extraction.Functions )
        {
            if ( IsDocumentableAt(function, position) )
            {
                return function;
            }
        }

        foreach ( ClassSymbol classSymbol in result.Extraction.Classes )
        {
            foreach ( FunctionSymbol method in classSymbol.Methods )
            {
                if ( IsDocumentableAt(method, position) )
                {
                    return method;
                }
            }
        }

        return null;
    }

    private static bool IsDocumentableAt(FunctionSymbol function, Position position)
    {
        // SourceFile names the header an #insert brought the declaration in from, where the ranges
        // are true in the header and the edit would land in the wrong file. A nameless declaration
        // is half-typed code, which is the normal state of an editor.
        return function.SourceFile.Length == 0
            && function.Name.Length > 0
            && function.FullRange.Contains(position);
    }

    /// <summary>The leading whitespace of a line, which a generated block has to repeat — a method
    /// sits inside a class body and a block flush left above it would be the only thing in the file
    /// at column zero.</summary>
    private static string IndentOf(ParseResult result, int line)
    {
        if ( line < 0 || line >= result.Text.LineCount )
        {
            return "";
        }

        int start = result.Text.GetOffset(new Position(line, 0));
        int cursor = start;
        while ( cursor < result.Text.Length && (result.Text.Text[cursor] == ' ' || result.Text.Text[cursor] == '\t') )
        {
            cursor++;
        }

        return result.Text.Text[start..cursor];
    }
}
