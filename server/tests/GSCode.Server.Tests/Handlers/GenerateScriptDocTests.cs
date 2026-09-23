using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Docs;
using GSCode.Core.Symbols;
using GSCode.Workspace.Api;
using GSCode.Parser.Preprocessing;
using GSCode.Server.Configuration;
using GSCode.Server.Handlers;
using GSCode.Workspace.Database;
using GSCode.Workspace.Documents;
using GSCode.Workspace.Resolution;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// "Generate ScriptDoc block": the action offered on a function that has none, and the block it
/// writes.
///
/// The round trip is what these pin. A generated block is only worth anything if the extractor
/// reads it back as documentation, and the two dialects disagree about what makes that true — BO3
/// has its own <c>/@ … @/</c> delimiter, while everything earlier is an ordinary block comment that
/// counts as documentation only because of a <c>///ScriptDocBegin</c> fence inside it. A block
/// missing that fence looks right and parses to nothing, which is exactly how the `doc` snippet had
/// been wrong.
/// </summary>
public class GenerateScriptDocTests
{
    private const string ScriptPath = @"c:\bo3\share\raw\scripts\main.gsc";

    private static async Task<ImmutableArray<CodeAction>> ActionsAtAsync(string source, int line, int character)
    {
        DocumentStore documents = new(static _ => NullInsertProvider.Instance, new NameTable());
        OpenDocument document = documents.Open(ScriptPath, source, 1);
        documents.AnalyzeIfStale(document);

        ScriptDatabase database = new();
        ResolverHolder holder = new(new PhysicalFileSystem());
        NavigationSupport support = new(documents, database, holder);

        string api = Path.Combine(AppContext.BaseDirectory, "Api");
        DocumentLinter linter = new(database, holder, BuiltinApiSet.Load(api), ObjectFields.Load(api));

        CodeActionHandler handler = new(documents, support, linter, TextDocumentSelector.ForLanguage("gsc"));

        CodeActionParams request = new()
        {
            TextDocument = new TextDocumentIdentifier { Uri = DocumentUri.FromFileSystemPath(ScriptPath) },
            Range = new LspRange(line, character, line, character),
            Context = new CodeActionContext { Diagnostics = new Container<Diagnostic>() },
        };

        CommandOrCodeActionContainer? result = await handler.Handle(request, CancellationToken.None);
        if ( result is null )
        {
            return [];
        }

        return [.. result.Where(entry => entry.CodeAction is not null).Select(entry => entry.CodeAction!)];
    }

    private static CodeAction? ScriptDocAction(ImmutableArray<CodeAction> actions)
    {
        return actions.FirstOrDefault(action => action.Title.StartsWith("Generate ScriptDoc", StringComparison.Ordinal));
    }

    private static string InsertedText(CodeAction action)
    {
        return action.Edit!.Changes!.Values.Single().Single().NewText;
    }

    [Fact]
    public async Task AnUndocumentedFunctionIsOfferedABlock()
    {
        string source = "#namespace game;\nfunction give( weapon, count = 1 )\n{\n}\n";

        CodeAction? action = ScriptDocAction(await ActionsAtAsync(source, 1, 12));

        Assert.NotNull(action);
        Assert.Equal(CodeActionKind.Refactor, action!.Kind);

        string block = InsertedText(action);
        Assert.Contains("\"Name: give( <weapon>, [count] )\"", block, StringComparison.Ordinal);

        // A parameter with a default is one the caller may leave out, which is what OptionalArg means.
        Assert.Contains("\"MandatoryArg: <weapon> : <description>\"", block, StringComparison.Ordinal);
        Assert.Contains("\"OptionalArg: [count] : <description>\"", block, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFunctionThatAlreadyHasADocBlockIsNotOffered()
    {
        string source =
            "#namespace game;\n/@\n\"Name: give()\"\n\"Summary: Gives.\"\n@/\nfunction give()\n{\n}\n";

        Assert.Null(ScriptDocAction(await ActionsAtAsync(source, 5, 12)));
    }

    [Fact]
    public async Task AMethodsBlockIsIndentedToItsDeclaration()
    {
        string source = "#namespace game;\nclass cScene\n{\n    function play()\n    {\n    }\n}\n";

        CodeAction? action = ScriptDocAction(await ActionsAtAsync(source, 3, 14));

        Assert.NotNull(action);

        // Flush left, a block above a method would be the only thing in the file at column zero.
        string block = InsertedText(action!);
        foreach ( string blockLine in block.TrimEnd('\n').Split('\n') )
        {
            Assert.StartsWith("    ", blockLine, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("bo3")]
    [InlineData("cod4")]
    public void TheGeneratedBlockParsesBackAsDocumentation(string shortName)
    {
        GameProfile previous = GameProfile.Active;
        GameProfile.Select(shortName);
        try
        {
            string block = ScriptDocTemplate.Render(
                "give",
                [new ParameterSymbol("weapon", ByRef: false, DefaultValueText: "")],
                hasVarargs: false,
                GameProfile.Active.ScriptDocStyle);

            // The pre-BO3 games have no doc delimiter of their own, so the fence is what makes this
            // documentation rather than a comment above a function.
            if ( GameProfile.Active.ScriptDocStyle == ScriptDocStyle.TripleSlash )
            {
                Assert.True(ScriptDocComment.HasTripleSlashFence(block));
            }

            ScriptDocComment parsed = ScriptDocComment.Parse(block);

            Assert.False(parsed.IsNone);
            Assert.Equal("give( <weapon> )", parsed.Name);
            Assert.Equal("weapon", Assert.Single(parsed.Arguments).Name);
        }
        finally
        {
            GameProfile.Select(previous.ShortName);
        }
    }
}
