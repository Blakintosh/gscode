using System.Threading;
using GSCode.Core;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Server.Configuration;
using GSCode.Server.Handlers;
using GSCode.Workspace.Api;
using GSCode.Workspace.Database;
using GSCode.Workspace.Documents;
using GSCode.Workspace.Resolution;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// Hovering __FUNCTION__ / __FILE__ where they are WRITTEN. By the time extraction runs, the token
/// there is an ordinary String literal — the macro already expanded before parsing — and a string
/// literal's own hover is a deliberate no-op, so without a separate path these two showed nothing at
/// all: the reader looking at "__FUNCTION__" on screen had no way to see what it resolved to.
/// </summary>
public class BuiltinMacroHoverTests
{
    private static async Task<Hover?> HoverAtAsync(string source, int line, int character)
    {
        string path = @"c:\bo3\share\raw\scripts\main.gsc";

        DocumentStore documents = new(static _ => NullInsertProvider.Instance, new NameTable());
        OpenDocument document = documents.Open(path, source, 1);
        documents.AnalyzeIfStale(document);

        ScriptDatabase database = new();
        ResolverHolder holder = new(new PhysicalFileSystem());
        NavigationSupport support = new(documents, database, holder);

        string api = Path.Combine(AppContext.BaseDirectory, "Api");
        HoverHandler handler = new(
            support,
            BuiltinApiSet.Load(api, GameProfile.BlackOps3),
            ObjectFields.Load(api),
            TextDocumentSelector.ForLanguage("gsc"));

        HoverParams request = new()
        {
            TextDocument = new TextDocumentIdentifier { Uri = DocumentUri.FromFileSystemPath(path) },
            Position = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Position(line, character),
        };

        return await handler.Handle(request, CancellationToken.None);
    }

    private static string TextOf(Hover hover)
    {
        return hover.Contents.MarkupContent!.Value;
    }

    [Fact]
    public async Task FunctionBuiltin_HoversWithTheQualifiedName()
    {
        string source = "#namespace spawner;\nfunction spawn_think()\n{\n    x = __FUNCTION__;\n}\n";

        Hover? hover = await HoverAtAsync(source, 3, 9);

        Assert.NotNull(hover);
        Assert.Contains("spawner::spawn_think", TextOf(hover!), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FileBuiltin_HoversWithTheResolvedPath()
    {
        string source = "function run()\n{\n    x = __FILE__;\n}\n";

        Hover? hover = await HoverAtAsync(source, 2, 9);

        Assert.NotNull(hover);
        Assert.Contains(@"c:\bo3\share\raw\scripts\main.gsc", TextOf(hover!), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnOrdinaryStringLiteralStillHasNoHover()
    {
        // The new path must not start claiming every string in the file.
        string source = "function run()\n{\n    x = \"hello\";\n}\n";

        Hover? hover = await HoverAtAsync(source, 2, 9);

        Assert.Null(hover);
    }
}
