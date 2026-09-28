using System.Collections.Immutable;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Workspace.Database;
using Xunit;

namespace GSCode.Workspace.Tests.Database;

/// <summary>
/// The declaration index answers "which files declare this name" and, since the scale sweep,
/// "which files declare this name IN this namespace". The second exists because a namespaced lookup
/// used to walk every file declaring the bare name — every <c>__init__</c> in the workspace — to keep
/// the one in the namespace asked for, which at 50,000 files made one file's lint pass cost more
/// than the keystroke debounce. These pin that the narrower answer stays exactly as correct as the
/// filter it replaces, across edits and removals.
/// </summary>
public class DeclarationIndexTests
{
    private static readonly TextRange s_someRange = TextRange.FromCoordinates(1, 0, 1, 4);

    private static ScriptRecord Declaring(string path, params (string Namespace, string KeyName)[] functions)
    {
        ImmutableArray<FunctionSymbol>.Builder symbols = ImmutableArray.CreateBuilder<FunctionSymbol>();
        foreach ( (string Namespace, string KeyName) function in functions )
        {
            symbols.Add(new FunctionSymbol
            {
                Name = function.KeyName,
                KeyName = function.KeyName,
                Namespace = function.Namespace,
                NameRange = s_someRange,
                FullRange = s_someRange,
            });
        }

        return new ScriptRecord
        {
            Path = path,
            Language = ScriptLanguage.Gsc,
            ContextId = "raw",
            ContentHash = 0,
            Functions = symbols.ToImmutable(),
        };
    }

    [Fact]
    public void ANamespacedQuery_NamesOnlyTheFilesDeclaringIntoThatNamespace()
    {
        LanguageStore store = new();
        store.Upsert(Declaring(@"c:\raw\a.gsc", ("alpha", "__init__")));
        store.Upsert(Declaring(@"c:\raw\b.gsc", ("beta", "__init__")));
        store.Upsert(Declaring(@"c:\raw\c.gsc", ("alpha", "__init__"), ("alpha", "other")));

        Assert.Equal([@"c:\raw\a.gsc", @"c:\raw\c.gsc"], store.FilesDeclaring("alpha", "__init__").Order());
        Assert.Equal(@"c:\raw\b.gsc", Assert.Single(store.FilesDeclaring("beta", "__init__")));
        Assert.Empty(store.FilesDeclaring("gamma", "__init__"));

        // The bare-name answer is unchanged.
        Assert.Equal(3, store.FilesDeclaring("__init__").Length);
    }

    [Fact]
    public void AnEditThatMovesAFunctionToAnotherNamespace_MovesItInTheIndex()
    {
        LanguageStore store = new();
        store.Upsert(Declaring(@"c:\raw\a.gsc", ("alpha", "__init__")));

        store.Upsert(Declaring(@"c:\raw\a.gsc", ("gamma", "__init__")));

        Assert.Empty(store.FilesDeclaring("alpha", "__init__"));
        Assert.Equal(@"c:\raw\a.gsc", Assert.Single(store.FilesDeclaring("gamma", "__init__")));
    }

    [Fact]
    public void ARemovedFile_LeavesTheNamespacedIndex()
    {
        LanguageStore store = new();
        store.Upsert(Declaring(@"c:\raw\a.gsc", ("alpha", "__init__")));

        store.Remove(@"c:\raw\a.gsc");

        Assert.Empty(store.FilesDeclaring("alpha", "__init__"));
    }

    [Fact]
    public void ADevOnlyFunctionOrMethod_MakesItsNameMaybeDevOnly_UntilAnEditTakesItOut()
    {
        LanguageStore store = new();
        ScriptRecord record = Declaring(@"c:\raw\a.gsc", ("alpha", "debug_draw"), ("alpha", "normal"));
        FunctionSymbol devFunction = record.Functions[0] with { IsDevOnly = true };
        FunctionSymbol devMethod = record.Functions[1] with { Name = "trace", KeyName = "trace", IsDevOnly = true };
        store.Upsert(record with
        {
            Functions = [devFunction, record.Functions[1]],
            Classes =
            [
                new ClassSymbol
                {
                    Name = "cthing",
                    KeyName = "cthing",
                    Namespace = "alpha",
                    Methods = [devMethod],
                    NameRange = s_someRange,
                    FullRange = s_someRange,
                },
            ],
        });

        Assert.True(store.MayBeDevOnly("debug_draw"));
        Assert.True(store.MayBeDevOnly("DEBUG_DRAW"));
        Assert.True(store.MayBeDevOnly("trace"));
        Assert.False(store.MayBeDevOnly("normal"));

        store.Upsert(record);
        Assert.False(store.MayBeDevOnly("debug_draw"));
        Assert.False(store.MayBeDevOnly("trace"));
    }

    [Fact]
    public void LookupFunctions_WithANamespace_FindsTheSameFunctionsItAlwaysDid()
    {
        LanguageStore store = new();
        store.Upsert(Declaring(@"c:\raw\a.gsc", ("alpha", "__init__")));
        store.Upsert(Declaring(@"c:\raw\b.gsc", ("beta", "__init__")));

        ImmutableArray<ResolvedFunction> alpha = DatabaseQueries.LookupFunctions(store, "raw", "", "alpha", "__init__");
        ImmutableArray<ResolvedFunction> any = DatabaseQueries.LookupFunctions(store, "raw", "", null, "__init__");

        Assert.Equal(@"c:\raw\a.gsc", Assert.Single(alpha).Record.Path);
        Assert.Equal(2, any.Length);
    }
}
