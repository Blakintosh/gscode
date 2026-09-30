using System.Collections.Immutable;
using GSCode.Core.Symbols;
using GSCode.Core.Paths;
using GSCode.Core.Text;
using GSCode.Server.Handlers;
using GSCode.Workspace.Database;
using OmniSharp.Extensions.LanguageServer.Protocol;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// The hierarchies answer for files that are not open.
///
/// Call and type hierarchy resolved every item through the document store, which requires the file
/// to be OPEN. Expanding an incoming call or a supertype names another file, and that file is
/// normally one the user does not have open — so the handler got null back and returned it, which
/// the protocol reads as "there are no incoming calls" rather than "ask again".
/// </summary>
public class ResolveForQueryTests
{
    private const string IndexedRelativePath = @"scripts\shared\util_shared.gsc";

    private static readonly string s_indexedPath = PathUtil.NormalizeAbsolute(TestPaths.Raw(IndexedRelativePath));

    private static DocumentUri IndexedUri => HandlerWorkspace.Identify(IndexedRelativePath).Uri;

    private static readonly TextRange s_someRange = new(new Position(1, 1), new Position(1, 5));

    private static ScriptRecord IndexedRecord()
    {
        return new ScriptRecord
        {
            Path = s_indexedPath,
            ContextId = "raw",
            ContentHash = 0,
            Language = ScriptLanguage.Gsc,
            RelativePath = IndexedRelativePath,
            DeclaredNamespaces = ImmutableArray.Create("util_shared"),
            Functions =
            [
                new FunctionSymbol
                {
                    Name = "helper",
                    KeyName = "helper",
                    Namespace = "util_shared",
                    NameRange = s_someRange,
                    FullRange = s_someRange,
                },
            ],
        };
    }

    [Fact]
    public async Task AFileThatIsIndexedButNotOpenStillResolves()
    {
        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync([]);
        workspace.Database.Gsc.Upsert(IndexedRecord());

        SymbolQueryContext? context = workspace.Navigation.ResolveForQuery(IndexedUri, CancellationToken.None);

        Assert.NotNull(context);
        Assert.Equal(s_indexedPath, context.Path);
        Assert.Equal("raw", context.ContextId);
        Assert.Equal(ScriptLanguage.Gsc, context.Language);
        Assert.Contains("util_shared", context.Namespaces);
    }

    [Fact]
    public async Task AFileNothingKnowsAboutStillResolvesToNothing()
    {
        // The control: the fallback answers from the INDEX, not from the path existing.
        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync([]);

        SymbolQueryContext? context = workspace.Navigation.ResolveForQuery(IndexedUri, CancellationToken.None);

        Assert.Null(context);
    }

    [Fact]
    public async Task TheContextIdComesFromTheRecordRatherThanTheResolver()
    {
        // A closed file's record already states its resolution world, so the fallback needs no
        // resolver call and no parse — nothing a hierarchy asks needs a syntax tree.
        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync([]);
        workspace.Database.Gsc.Upsert(IndexedRecord() with { ContextId = "mod:my_mod" });

        SymbolQueryContext? context = workspace.Navigation.ResolveForQuery(IndexedUri, CancellationToken.None);

        Assert.NotNull(context);
        Assert.Equal("mod:my_mod", context.ContextId);
    }
}
