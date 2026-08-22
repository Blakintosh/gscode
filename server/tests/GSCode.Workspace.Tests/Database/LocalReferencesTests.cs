using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Workspace.Database;
using Xunit;

namespace GSCode.Workspace.Tests.Database;

/// <summary>
/// Find-all-references on a LOCAL. Locals are absent from the reference index by design — it is
/// keyed by SymbolKey and shared workspace-wide, so an `i` in one function would collide with the
/// `i` in every other — which left find-references, highlight and rename with nothing to find on a
/// variable.
///
/// They are walked from the AST instead, per function, which is the scope a local really has. What
/// counts as a WRITE is the part worth pinning down: it is the same set of language facts that made
/// the unassigned-variable lint report thousands of names in code that ships and works.
/// </summary>
public class LocalReferencesTests
{
    private static ParseResult Analyze(string source, GameProfile? profile = null)
    {
        return ScriptAnalysis.Analyze(
            @"C:\bo3\share\raw\scripts\main.gsc",
            ScriptLanguage.Gsc,
            SourceText.From(source),
            NullInsertProvider.Instance,
            new NameTable(),
            profile);
    }

    [Fact]
    public void AVariableIsFoundAtEveryOccurrence()
    {
        //                     0         1
        //                     0123456789012345
        string source = "function f()\n{\n\tcount = 1;\n\tuse( count );\n}\n";

        // The `count` inside use(), on line 3.
        ImmutableArray<LocalOccurrence> found = LocalReferences.Find(Analyze(source), new Position(3, 6));

        Assert.Equal(2, found.Length);
        Assert.Equal(2, found[0].Range.Start.Line);
        Assert.True(found[0].IsWrite);
        Assert.Equal(3, found[1].Range.Start.Line);
        Assert.False(found[1].IsWrite);
    }

    [Fact]
    public void ALocalInAnotherFunctionIsNotFound()
    {
        // The whole reason locals stay out of the shared index.
        string source = "function a()\n{\n\tcount = 1;\n}\nfunction b()\n{\n\tuse( count );\n}\n";

        ImmutableArray<LocalOccurrence> found = LocalReferences.Find(Analyze(source), new Position(6, 6));

        Assert.Single(found);
        Assert.Equal(6, found[0].Range.Start.Line);
    }

    [Fact]
    public void AFieldWriteIsNotABareLocal()
    {
        // `self.count` is a field on an entity that outlives the function, so it is neither a write
        // to the bare `count` nor an occurrence of it.
        string source = "function f()\n{\n\tself.count = 1;\n\tuse( count );\n}\n";

        ImmutableArray<LocalOccurrence> found = LocalReferences.Find(Analyze(source), new Position(3, 6));

        Assert.Single(found);
        Assert.Equal(3, found[0].Range.Start.Line);
        Assert.False(found[0].IsWrite);
    }

    [Fact]
    public void SubscriptingAnArrayWritesTheBase()
    {
        // `a[ 0 ] = x` CREATES `a` when it does not exist — that is how every array in the stock
        // scripts is built, and `quotes[ quotes.size ] = "…"` appears all through them.
        //                     0         1
        //                     0123456789012
        string source = "function f()\n{\n\ta[ i ] = x;\n}\n";

        ImmutableArray<LocalOccurrence> found = LocalReferences.Find(Analyze(source), new Position(2, 1));

        Assert.Single(found);
        Assert.True(found[0].IsWrite);
    }

    [Fact]
    public void TheSubscriptItselfIsStillARead()
    {
        // `a[ i ] = x` genuinely reads `i`.
        string source = "function f()\n{\n\ta[ i ] = x;\n}\n";

        ImmutableArray<LocalOccurrence> found = LocalReferences.Find(Analyze(source), new Position(2, 4));

        Assert.Single(found);
        Assert.False(found[0].IsWrite);
    }

    [Fact]
    public void WaittillBindsItsTrailingArguments()
    {
        // They are OUTPUTS the engine fills in, not values being read, and this is the only place
        // the name comes into existence. Missing it was the single largest source of false
        // positives the unassigned-variable lint ever produced.
        //                     0         1         2         3
        //                     0123456789012345678901234567890123456
        string source = "function f()\n{\n\tself waittill( \"damage\", attacker );\n\tuse( attacker );\n}\n";

        ImmutableArray<LocalOccurrence> found = LocalReferences.Find(Analyze(source), new Position(2, 26));

        Assert.Equal(2, found.Length);
        Assert.True(found[0].IsWrite);
        Assert.True(found[0].IsDeclaration);
        Assert.False(found[1].IsWrite);
    }

    [Fact]
    public void TheFirstWaittillArgumentIsAGenuineRead()
    {
        // It is the event NAME. Only what follows it is bound.
        //                     0         1         2         3
        //                     0123456789012345678901234567890
        string source = "function f( evt )\n{\n\tself waittill( evt, attacker );\n}\n";

        ImmutableArray<LocalOccurrence> found = LocalReferences.Find(Analyze(source), new Position(2, 16));

        Assert.Equal(2, found.Length);
        Assert.Equal(0, found[0].Range.Start.Line);
        Assert.False(found[1].IsWrite);
    }

    [Fact]
    public void AFunctionPointerIsNotAVariableUse()
    {
        // `&foo` names a FUNCTION. The local spelled foo has nothing to do with it.
        string source = "function f()\n{\n\tfoo = 1;\n\thandler = &foo;\n}\n";

        ImmutableArray<LocalOccurrence> found = LocalReferences.Find(Analyze(source), new Position(2, 1));

        Assert.Single(found);
        Assert.Equal(2, found[0].Range.Start.Line);
    }

    [Fact]
    public void ACallToAFunctionIsNotAUseOfALocalSpelledTheSame()
    {
        // The callee of `foo()` names a function, not the variable.
        string source = "function f()\n{\n\tfoo = 1;\n\tfoo();\n}\n";

        ImmutableArray<LocalOccurrence> found = LocalReferences.Find(Analyze(source), new Position(2, 1));

        Assert.Single(found);
        Assert.Equal(2, found[0].Range.Start.Line);
    }

    [Fact]
    public void AParameterIsIncludedAndIsTheDeclaration()
    {
        // The signature is where the name is introduced; the assignment writes to something that
        // already exists. Same precedence LocalDefinition.Find applies.
        //                     0         1
        //                     0123456789012345678
        string source = "function f( count )\n{\n\tcount = 1;\n}\n";

        ImmutableArray<LocalOccurrence> found = LocalReferences.Find(Analyze(source), new Position(2, 1));

        Assert.Equal(2, found.Length);
        Assert.Equal(0, found[0].Range.Start.Line);
        Assert.True(found[0].IsDeclaration);
        Assert.True(found[1].IsWrite);
        Assert.False(found[1].IsDeclaration);
    }

    [Fact]
    public void TheFirstWriteIsTheDeclarationWhenThereIsNoParameter()
    {
        // A later write is only a write because the first one introduced the name.
        string source = "function f()\n{\n\tx = 1;\n\tx = 2;\n}\n";

        ImmutableArray<LocalOccurrence> found = LocalReferences.Find(Analyze(source), new Position(2, 1));

        Assert.Equal(2, found.Length);
        Assert.True(found[0].IsDeclaration);
        Assert.True(found[1].IsWrite);
        Assert.False(found[1].IsDeclaration);
    }

    [Fact]
    public void AForeachBindingIsAWrite()
    {
        // The loop writes it each pass; the author never assigns it.
        //                     0         1
        //                     0123456789012345678901234
        string source = "function f( list )\n{\n\tforeach ( item in list )\n\t{\n\t\tuse( item );\n\t}\n}\n";

        ImmutableArray<LocalOccurrence> found = LocalReferences.Find(Analyze(source), new Position(2, 11));

        Assert.Equal(2, found.Length);
        Assert.True(found[0].IsWrite);
        Assert.Equal(4, found[1].Range.Start.Line);
        Assert.False(found[1].IsWrite);
    }

    [Fact]
    public void ClickingTheBindingItselfWorks()
    {
        // A `foreach` binding is a bare token hanging off the loop, not an IdentifierNode, so the
        // shared AstSearch.TryFindLocalContext finds nothing under a cursor on it — on the one
        // occurrence a user is most likely to click, since it is where the name is introduced.
        string source = "function f( list )\n{\n\tforeach ( item in list )\n\t{\n\t\tuse( item );\n\t}\n}\n";

        ImmutableArray<LocalOccurrence> found = LocalReferences.Find(Analyze(source), new Position(2, 12));

        Assert.Equal(2, found.Length);
    }

    [Fact]
    public void ClickingAParameterInTheSignatureWorks()
    {
        // A parameter name is a bare token too.
        //                     0         1
        //                     0123456789012345678
        string source = "function f( count )\n{\n\tuse( count );\n}\n";

        ImmutableArray<LocalOccurrence> found = LocalReferences.Find(Analyze(source), new Position(0, 13));

        Assert.Equal(2, found.Length);
        Assert.True(found[0].IsDeclaration);
        Assert.Equal(2, found[1].Range.Start.Line);
    }

    [Fact]
    public void AGlobalIsNotALocal()
    {
        // `level` comes from the profile, and its readers are every script in the workspace.
        string source = "function f()\n{\n\tlevel.x = 1;\n\tuse( level );\n}\n";

        Assert.Empty(LocalReferences.Find(Analyze(source), new Position(3, 6)));
    }

    [Fact]
    public void AClassMemberIsNotALocal()
    {
        // A bare name in a method may be a `var` member, whose readers are the class's other
        // methods and potentially other files. Answering with this body's occurrences would hide
        // them.
        string source =
            "class Foo\n{\n\tvar id;\n\n\tfunction play()\n\t{\n\t\tid = 1;\n\t\tuse( id );\n\t}\n}\n";

        Assert.Empty(LocalReferences.Find(Analyze(source), new Position(6, 2)));
    }

    [Fact]
    public void AnInheritedMemberInTheSameFileIsNotALocalEither()
    {
        // The parent chain is walked, so a member declared one class up is recognised too.
        string source =
            "class Base\n{\n\tvar id;\n}\n\nclass Foo : Base\n{\n\tfunction play()\n\t{\n\t\tid = 1;\n\t}\n}\n";

        Assert.Empty(LocalReferences.Find(Analyze(source), new Position(9, 2)));
    }

    [Fact]
    public void AnInfinityWardFileScopeConstantIsNotALocal()
    {
        // `SPEED = 1.0;` between two functions is readable from ALL of them, so a per-function
        // answer would hide every other reader. Their ALL_CAPS naming makes them look convincingly
        // like macros, and MW2's scripts alone hold 755.
        //                     0         1
        //                     0123456789012
        string source = "SPEED = 1.0;\nrun()\n{\n\tx = SPEED;\n}\n";

        Assert.Empty(LocalReferences.Find(
            Analyze(source, GameProfile.ModernWarfare2), new Position(3, 5), GameProfile.ModernWarfare2));
    }

    [Fact]
    public void APositionOnNothingResolvesToNothing()
    {
        string source = "function f()\n{\n\tcount = 1;\n}\n";

        Assert.Empty(LocalReferences.Find(Analyze(source), new Position(1, 0)));
    }

    [Fact]
    public void BindsNameSeesANameTheFunctionAlreadyWrites()
    {
        // The collision a rename has to refuse: renaming `x` to `total` here would MERGE the two,
        // and the script would keep running while meaning something else.
        string source = "function f()\n{\n\tx = 1;\n\ttotal = 2;\n}\n";

        Assert.True(LocalReferences.BindsName(Analyze(source), new Position(2, 1), "total"));
    }

    [Fact]
    public void BindsNameSeesAParameter()
    {
        string source = "function f( total )\n{\n\tx = 1;\n}\n";

        Assert.True(LocalReferences.BindsName(Analyze(source), new Position(2, 1), "total"));
    }

    [Fact]
    public void BindsNameIgnoresANameNothingWrites()
    {
        // Only a BINDING collides. A name the function merely reads was already undefined.
        string source = "function f()\n{\n\tx = 1;\n\tuse( total );\n}\n";

        Assert.False(LocalReferences.BindsName(Analyze(source), new Position(2, 1), "total"));
    }

    [Fact]
    public void BindsNameSeesAClassMemberTheMethodCanReach()
    {
        // The collision from OUTSIDE the function. Renaming `x` to `id` here would capture the
        // method's `use( id )` — a read of the member — into the local, exactly the silent merge
        // BindsName exists to refuse, just arriving from the enclosing class instead of the body.
        string source =
            "class Foo\n{\n\tvar id;\n\n\tfunction play()\n\t{\n\t\tx = 1;\n\t\tuse( id );\n\t}\n}\n";

        Assert.True(LocalReferences.BindsName(Analyze(source), new Position(6, 2), "id"));
    }

    [Fact]
    public void BindsNameSeesAnInfinityWardFileScopeConstant()
    {
        string source = "SPEED = 1.0;\nrun()\n{\n\tx = 1;\n\tuse( SPEED );\n}\n";

        Assert.True(LocalReferences.BindsName(
            Analyze(source, GameProfile.ModernWarfare2), new Position(3, 1), "SPEED", GameProfile.ModernWarfare2));
    }

    [Fact]
    public void BindsNameSeesAGlobalObjectName()
    {
        // `level` is not the function's to bind, but renaming a local onto it would still capture
        // every `level.` read in the body.
        string source = "function f()\n{\n\tx = 1;\n\tlevel.time = x;\n}\n";

        Assert.True(LocalReferences.BindsName(Analyze(source), new Position(2, 1), "level"));
    }
}
