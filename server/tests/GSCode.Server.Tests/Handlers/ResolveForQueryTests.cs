using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Paths;
using GSCode.Core.Text;
using GSCode.Parser.Preprocessing;
using GSCode.Server.Configuration;
using GSCode.Server.Handlers;
using GSCode.Workspace.Database;
using GSCode.Workspace.Documents;
using GSCode.Workspace.Resolution;
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
    private static readonly string IndexedPath =
        PathUtil.NormalizeAbsolute(@"C:\bo3\share\raw\scripts\shared\util_shared.gsc");

    private static readonly TextRange SomeRange = new(new Position(1, 1), new Position(1, 5));

    private static NavigationSupport SupportOver(ScriptDatabase database)
    {
        DocumentStore documents = new(static _ => NullInsertProvider.Instance, new NameTable());
        return new NavigationSupport(documents, database, new ResolverHolder(new PhysicalFileSystem()));
    }

    private static ScriptRecord IndexedRecord()
    {
        return new ScriptRecord
        {
            Path = IndexedPath,
            ContextId = "raw",
            ContentHash = 0,
            Language = ScriptLanguage.Gsc,
            RelativePath = @"scripts\shared\util_shared.gsc",
            DeclaredNamespaces = ImmutableArray.Create("util_shared"),
            Functions =
            [
                new FunctionSymbol
                {
                    Name = "helper",
                    KeyName = "helper",
                    Namespace = "util_shared",
                    NameRange = SomeRange,
                    FullRange = SomeRange,
                },
            ],
        };
    }

    [Fact]
    public void AFileThatIsIndexedButNotOpenStillResolves()
    {
        ScriptDatabase database = new();
        database.Gsc.Upsert(IndexedRecord());

        SymbolQueryContext? context = SupportOver(database).ResolveForQuery(
            DocumentUri.FromFileSystemPath(IndexedPath), CancellationToken.None);

        Assert.NotNull(context);
        Assert.Equal(IndexedPath, context.Path);
        Assert.Equal("raw", context.ContextId);
        Assert.Equal(ScriptLanguage.Gsc, context.Language);
        Assert.Contains("util_shared", context.Namespaces);
    }

    [Fact]
    public void AFileNothingKnowsAboutStillResolvesToNothing()
    {
        // The control: the fallback answers from the INDEX, not from the path existing.
        SymbolQueryContext? context = SupportOver(new ScriptDatabase()).ResolveForQuery(
            DocumentUri.FromFileSystemPath(IndexedPath), CancellationToken.None);

        Assert.Null(context);
    }

    [Fact]
    public void TheContextIdComesFromTheRecordRatherThanTheResolver()
    {
        // A closed file's record already states its resolution world, so the fallback needs no
        // resolver call and no parse — nothing a hierarchy asks needs a syntax tree.
        ScriptDatabase database = new();
        database.Gsc.Upsert(IndexedRecord() with { ContextId = "mod:my_mod" });

        SymbolQueryContext? context = SupportOver(database).ResolveForQuery(
            DocumentUri.FromFileSystemPath(IndexedPath), CancellationToken.None);

        Assert.NotNull(context);
        Assert.Equal("mod:my_mod", context.ContextId);
    }
}
