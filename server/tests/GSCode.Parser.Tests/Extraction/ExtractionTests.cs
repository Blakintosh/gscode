using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser.Preprocessing;
using Xunit;

namespace GSCode.Parser.Tests.Extraction;

public class ExtractionTests
{
    private static ParseResult Analyze(string source, string path = @"c:\work\scripts\test.gsc", GameProfile? profile = null)
    {
        return ScriptAnalysis.Analyze(
            path,
            ScriptAnalysis.LanguageFromPath(path),
            SourceText.From(source),
            NullInsertProvider.Instance,
            new NameTable(),
            profile);
    }

    [Fact]
    public void Function_BasicSurface()
    {
        ParseResult result = Analyze("function private DoThing( a, b = 5, &c, ... )\n{\n}");

        FunctionSymbol function = Assert.Single(result.Extraction.Functions);
        Assert.Equal("DoThing", function.Name);
        Assert.Equal("dothing", function.KeyName);
        Assert.Equal("test", function.Namespace);
        Assert.True(function.IsPrivate);
        Assert.False(function.IsAutoexec);
        Assert.True(function.HasVarargs);
        Assert.Equal(3, function.Parameters.Length);
        Assert.True(function.Parameters[2].ByRef);
        Assert.Equal("5", function.Parameters[1].DefaultValueText);
        Assert.Equal("", function.SourceFile);
    }

    [Fact]
    public void Function_ParameterDefault_IsShownAsWritten_NotAsAnSExpression()
    {
        // Signature help, hover and export signatures show DefaultValueText verbatim. It must read
        // like the source the author wrote, not AstPrinter's debug format — `(vector 0 0 1)` and
        // `(prefix- 1)` are meaningless to a reader looking at a function signature.
        ParseResult result = Analyze(
            "#define MAX_HEALTH 100\nfunction f( v = ( 0, 0, 1 ), n = -1, s = \"hi\", m = MAX_HEALTH )\n{\n}");

        FunctionSymbol function = Assert.Single(result.Extraction.Functions);
        Assert.Equal("( 0, 0, 1 )", function.Parameters[0].DefaultValueText);
        Assert.Equal("-1", function.Parameters[1].DefaultValueText);
        Assert.Equal("\"hi\"", function.Parameters[2].DefaultValueText);

        // A macro-expanded default is shown as the INVOCATION the author wrote, not its expansion —
        // the default's range covers "MAX_HEALTH" in the root file regardless of what it expands to.
        Assert.Equal("MAX_HEALTH", function.Parameters[3].DefaultValueText);
    }

    [Fact]
    public void FileScopeConstant_ExtractsReferencesFromItsValue()
    {
        // An Infinity Ward file-scope constant's value was invisible to WalkDeclarations entirely,
        // so a call, a field read or an address-of inside it never reached the reference list —
        // go-to-definition and find-all-references had nothing to say about names used only there.
        GameProfile mw2 = GameProfile.ByName("mw2")!;
        ParseResult result = Analyze("MAX_HEALTH = get_default();\nrun()\n{\n}\n", profile: mw2);

        // MW2 does not resolve calls by namespace, so an unqualified call keys with none.
        SymbolKey calleeKey = new(null, "get_default", SymbolKind.Function);
        Assert.Contains(
            result.Extraction.References, entry => entry.Key == calleeKey && entry.Kind == ReferenceKind.Call);
    }

    [Fact]
    public void Namespace_DefaultIsFileStem_AndDirectiveSwitches()
    {
        ParseResult result = Analyze("function a()\n{\n}\n#namespace util;\nfunction b()\n{\n}");

        Assert.Equal("test", result.Extraction.Functions[0].Namespace);
        Assert.Equal("util", result.Extraction.Functions[1].Namespace);
        Assert.Equal(2, result.Extraction.Namespaces.Length);
    }

    [Fact]
    public void Assignments_LocalsFieldsForeachAndConst()
    {
        string source = """
            function work()
            {
                score = 0;
                self.health = 100;
                level.round_number = 1;
                const MAX = 5;
                foreach ( key, value in things )
                {
                }
            }
            """;

        ParseResult result = Analyze(source);
        FunctionSymbol function = Assert.Single(result.Extraction.Functions);

        List<(string Owner, string Name)> assignments = [];
        foreach ( AssignmentSymbol assignment in function.Assignments )
        {
            assignments.Add((assignment.OwnerName, assignment.Name));
        }

        Assert.Contains(("", "score"), assignments);
        Assert.Contains(("self", "health"), assignments);
        Assert.Contains(("level", "round_number"), assignments);
        Assert.Contains(("", "MAX"), assignments);
        Assert.Contains(("", "key"), assignments);
        Assert.Contains(("", "value"), assignments);
    }

    [Fact]
    public void Class_SurfaceAndConstructorParameterRule()
    {
        string source = """
            class Faz : Boo
            {
                var far2;

                constructor( illegal )
                {
                }

                function method_one()
                {
                }
            }
            """;

        ParseResult result = Analyze(source);
        ClassSymbol classSymbol = Assert.Single(result.Extraction.Classes);

        Assert.Equal("faz", classSymbol.KeyName);
        Assert.Equal("boo", classSymbol.ParentKeyName);
        Assert.Single(classSymbol.Members);
        Assert.Single(classSymbol.Methods);
        Assert.True(classSymbol.HasConstructor);
        Assert.False(classSymbol.HasDestructor);

        Assert.Contains(result.AllDiagnostics, diagnostic => diagnostic.Code == GscDiagnosticCode.ConstructorHasParameters);
    }

    [Fact]
    public void Precache_ValidAndInvalid()
    {
        Assert.DoesNotContain(
            Analyze("#precache( \"string\", \"HINT\" );").AllDiagnostics,
            diagnostic => diagnostic.Code is GscDiagnosticCode.UnknownPrecacheType or GscDiagnosticCode.WrongPrecacheArgumentCount);

        Assert.Contains(
            Analyze("#precache( \"nonsense_type\", \"X\" );").AllDiagnostics,
            diagnostic => diagnostic.Code == GscDiagnosticCode.UnknownPrecacheType);

        // Animation types, added by report rather than from the stock scripts.
        Assert.DoesNotContain(
            Analyze("#precache( \"xanim\", \"ai_zombie_walk\" );").AllDiagnostics,
            diagnostic => diagnostic.Code is GscDiagnosticCode.UnknownPrecacheType or GscDiagnosticCode.WrongPrecacheArgumentCount);
        Assert.DoesNotContain(
            Analyze("#precache( \"anim\", \"ai_zombie_walk\" );").AllDiagnostics,
            diagnostic => diagnostic.Code is GscDiagnosticCode.UnknownPrecacheType or GscDiagnosticCode.WrongPrecacheArgumentCount);

        Assert.Contains(
            Analyze("#precache( \"model\", \"a\", \"b\" );").AllDiagnostics,
            diagnostic => diagnostic.Code == GscDiagnosticCode.WrongPrecacheArgumentCount);

        // The string family accepts extra values.
        Assert.DoesNotContain(
            Analyze("#precache( \"string\", \"A\", \"B\", \"C\" );").AllDiagnostics,
            diagnostic => diagnostic.Code == GscDiagnosticCode.WrongPrecacheArgumentCount);
    }

    [Theory]
    [InlineData("true = false;")]
    [InlineData("false = 1;")]
    [InlineData("undefined = 1;")]
    [InlineData("5 = x;")]
    [InlineData("\"name\" = x;")]
    [InlineData("foo() = 1;")]
    [InlineData("a + b = 1;")]
    public void Assignment_ToSomethingThatIsNotAPlace(string statement)
    {
        // These parse cleanly - a literal IS an expression and `=` follows it - so nothing objected
        // and the complaint came from whatever mis-parsed afterwards, surfacing as a bare
        // "unexpected TOKEN_EQUALS" that named a token rather than the mistake.
        Assert.Contains(
            Analyze("function f()\n{\n    " + statement + "\n}\n").AllDiagnostics,
            diagnostic => diagnostic.Code == GscDiagnosticCode.InvalidAssignmentTarget);
    }

    [Theory]
    [InlineData("x = 1;")]
    [InlineData("self.field = 1;")]
    [InlineData("level.a.b = 1;")]
    [InlineData("arr[ 0 ] = 1;")]
    [InlineData("arr[ \"key\" ].field = 1;")]
    [InlineData("x += 1;")]
    [InlineData("( x ) = 1;")]
    public void Assignment_ToARealPlaceIsFine(string statement)
    {
        // Everything a value can actually be stored into. `( x ) = 1` stores into x exactly as
        // `x = 1` does, so a parenthesised target is judged by its contents.
        Assert.DoesNotContain(
            Analyze("function f()\n{\n    " + statement + "\n}\n").AllDiagnostics,
            diagnostic => diagnostic.Code == GscDiagnosticCode.InvalidAssignmentTarget);
    }

    [Theory]
    [InlineData("client_fx")]
    [InlineData("client_model")]
    [InlineData("client_string")]
    [InlineData("client_tagfxset")]
    public void Precache_ClientTypesBelongToClientScripts(string typeName)
    {
        string source = $"#precache( \"{typeName}\", \"asset\" );";

        // In a .csc it is ordinary.
        Assert.DoesNotContain(
            Analyze(source, @"c:\work\scripts\test.csc").AllDiagnostics,
            diagnostic => diagnostic.Code is GscDiagnosticCode.UnknownPrecacheType
                or GscDiagnosticCode.ClientOnlyPrecacheType);

        // In a .gsc it is a real type in the wrong world, and reported apart from "unknown"
        // because the two call for opposite responses: an unknown type is probably a typo, while
        // this one is spelled correctly and simply belongs in the other file.
        Assert.Contains(
            Analyze(source, @"c:\work\scripts\test.gsc").AllDiagnostics,
            diagnostic => diagnostic.Code == GscDiagnosticCode.ClientOnlyPrecacheType);
    }

    [Fact]
    public void Precache_AHeaderMayUseEitherWorldsTypes()
    {
        // A .gsh is inserted into whichever world includes it, so the language it ends up in is not
        // knowable from the header. Reporting here would blame a correct file for its caller.
        Assert.DoesNotContain(
            Analyze("#precache( \"client_fx\", \"asset\" );", @"c:\work\scripts\shared.gsh").AllDiagnostics,
            diagnostic => diagnostic.Code == GscDiagnosticCode.ClientOnlyPrecacheType);
    }

    [Theory]
    [InlineData("xmodel")]
    [InlineData("model")]
    [InlineData("xmodelalias")]
    public void Precache_AcceptsEveryAssetTypeStockScriptsUse(string assetType)
    {
        // xmodel is absent from the language PDF but appears 38 times in the shipped scripts;
        // model and xmodelalias are documented and distinct from it. All three must pass, so a
        // future edit cannot "tidy" one into another.
        Assert.DoesNotContain(
            Analyze($"#precache( \"{assetType}\", \"p7_perk_t7_hud_perk_engineer\" );").AllDiagnostics,
            diagnostic => diagnostic.Code == GscDiagnosticCode.UnknownPrecacheType);
    }

    [Theory]
    [InlineData("a = 1, b = -2.5, c = (0,0,0), d = \"x\"")]   // literals and vectors
    [InlineData("reqs = []")]                                // system_shared's register()
    [InlineData("give_fn = &default_give")]                  // a function pointer
    [InlineData("slot = self.piece.inventory_slot")]         // a member read
    [InlineData("weapon = undefined")]
    [InlineData("v = get_default()")]                        // even a call
    public void AnyDefaultParameterValueIsAccepted(string parameters)
    {
        // A default is evaluated in the function BODY when the argument arrives undefined, so
        // anything the body could contain is legal there. The old "literals and vectors only"
        // rule reported 21 errors across 8 shipped scripts, every one of them wrong.
        Assert.Empty(Analyze($"function f( {parameters} )\n{{\n}}").AllDiagnostics);
    }

    [Fact]
    public void References_CallKindsAndKeys()
    {
        string source = """
            function caller()
            {
                helper();
                util::assist();
                sys::print("x");
                f = &util::assist;
                b = new Boo();
            }
            """;

        ParseResult result = Analyze(source);
        List<ReferenceEntry> references = [.. result.Extraction.References];

        // Unqualified calls key under the current namespace state.
        Assert.Contains(references, entry =>
            entry.Key == new SymbolKey("test", "helper", SymbolKind.Function) && entry.Kind == ReferenceKind.Call);

        Assert.Contains(references, entry =>
            entry.Key == new SymbolKey("util", "assist", SymbolKind.Function) && entry.Kind == ReferenceKind.Call);

        // sys:: is the builtin qualifier — builtins are namespace-less.
        Assert.Contains(references, entry =>
            entry.Key == new SymbolKey(null, "print", SymbolKind.Function) && entry.Kind == ReferenceKind.Call);

        Assert.Contains(references, entry =>
            entry.Key == new SymbolKey("util", "assist", SymbolKind.Function) && entry.Kind == ReferenceKind.AddressOf);

        Assert.Contains(references, entry =>
            entry.Key == new SymbolKey(null, "boo", SymbolKind.Class) && entry.Kind == ReferenceKind.ClassUse);
    }

    [Fact]
    public void References_SysIsOnlyTheBuiltinQualifierWhereResolutionIsByNamespace()
    {
        // `sys::` belongs to the namespace dialects: no shipped CoD4 or BO1 script writes it, and on
        // a merge dialect a key with no namespace is every unqualified call's key, so reading `sys`
        // as the builtin form there would claim a meaning the engine never gave it. It keys like any
        // other written qualifier instead, which is what `util::` does beside it.
        GameProfile cod4 = GameProfile.ByName("cod4")!;
        ParseResult result = Analyze("caller()\n{\n    sys::print( \"x\" );\n    util::assist();\n}\n", profile: cod4);
        List<ReferenceEntry> references = [.. result.Extraction.References];

        Assert.Contains(references, entry =>
            entry.Key == new SymbolKey("sys", "print", SymbolKind.Function) && entry.Kind == ReferenceKind.Call);

        Assert.Contains(references, entry =>
            entry.Key == new SymbolKey("util", "assist", SymbolKind.Function) && entry.Kind == ReferenceKind.Call);
    }

    [Fact]
    public void References_ClassDefinitionAndUseShareOneKey()
    {
        // A class name is GLOBAL in T7 — `new Throttle()` names it bare, and the language has no
        // `ns::Throttle` to qualify one with — so its key carries no namespace on either side.
        //
        // The definition used to be keyed under the file's namespace while every use was keyed under
        // none, so the two could never meet: go-to-definition on `new Throttle()` found nothing, and
        // the CodeLens over `class Throttle` counted no references. Asserted together here because
        // the bug was not in either key on its own, it was in the pair disagreeing.
        ParseResult result = Analyze(
            "#namespace throttle_shared;\n\nclass Throttle\n{\n}\n\nfunction f()\n{\n\tlevel.t = new Throttle();\n}\n");

        SymbolKey key = new(null, "throttle", SymbolKind.Class);
        List<ReferenceEntry> references = [.. result.Extraction.References];

        Assert.Contains(references, entry => entry.Key == key && entry.Kind == ReferenceKind.Definition);
        Assert.Contains(references, entry => entry.Key == key && entry.Kind == ReferenceKind.ClassUse);
    }

    [Fact]
    public void References_AParentClassSharesThatSameKey()
    {
        // `class Derived : Throttle` is the other way a class is named, and it has to resolve to the
        // same declaration `new Throttle()` does.
        ParseResult result = Analyze(
            "#namespace a;\n\nclass Throttle\n{\n}\n\nclass Derived : Throttle\n{\n}\n");

        SymbolKey key = new(null, "throttle", SymbolKind.Class);
        List<ReferenceEntry> references = [.. result.Extraction.References];

        Assert.Contains(references, entry => entry.Key == key && entry.Kind == ReferenceKind.Definition);
        Assert.Contains(references, entry => entry.Key == key && entry.Kind == ReferenceKind.ClassUse);
    }

    [Fact]
    public void References_LiteralsWithCaseRules()
    {
        ParseResult result = Analyze("function f()\n{\nself notify(\"Death_Event\");\nx = #\"Hash_Val\";\ny = &\"MENU_LABEL\";\n}");
        List<ReferenceEntry> references = [.. result.Extraction.References];

        // Strings are content-exact (case preserved)...
        Assert.Contains(references, entry =>
            entry.Key == new SymbolKey(null, "Death_Event", SymbolKind.StringLiteral) && entry.Kind == ReferenceKind.Literal);

        // ...and so are hash strings and istrings, which used to be lowercased.
        //
        // The keys are shown verbatim in completion, so lowercasing turned
        // KILLSTREAK_COMBAT_ROBOT_CRATE into killstreak_combat_robot_crate. Storing the written
        // form costs only the linking of two DIFFERENTLY cased writings of one name, and across
        // the stock scripts that never happens: of 539 distinct istrings and 48 hash strings, not
        // one is written with two casings. Literal references are resolved by range containment
        // rather than by re-deriving a key from the text, so both sides of a find-all-references
        // use the same extraction-produced key either way.
        Assert.Contains(references, entry => entry.Key == new SymbolKey(null, "Hash_Val", SymbolKind.HashString));
        Assert.Contains(references, entry => entry.Key == new SymbolKey(null, "MENU_LABEL", SymbolKind.LocalizedString));
    }

    [Fact]
    public void References_ConcatenationFlagDoesNotLeakIntoNestedExpressions()
    {
        // "a" + foo( "name" ) — the OUTER string is a genuine message fragment, but "name" is an
        // ordinary argument sitting inside a call the concatenation merely passes through. Only the
        // outer literal should be flagged; a call argument, an index expression and a nested `+`
        // chain of their own must each be judged on their own terms.
        ParseResult result = Analyze(
            "function f()\n{\nx = \"a\" + foo( \"name\" );\ny = \"b\" + level.flags[ \"key\" ];\nz = \"c\" + (\"d\" + bar( \"e\" ));\n}");

        List<ReferenceEntry> references = [.. result.Extraction.References];

        SymbolKey Key(string text)
        {
            return new SymbolKey(null, text, SymbolKind.StringLiteral);
        }

        Assert.Contains(references, entry => entry.Key == Key("a") && entry.Kind == ReferenceKind.ConcatenatedLiteral);
        Assert.Contains(references, entry => entry.Key == Key("b") && entry.Kind == ReferenceKind.ConcatenatedLiteral);
        Assert.Contains(references, entry => entry.Key == Key("c") && entry.Kind == ReferenceKind.ConcatenatedLiteral);
        Assert.Contains(references, entry => entry.Key == Key("d") && entry.Kind == ReferenceKind.ConcatenatedLiteral);

        Assert.Contains(references, entry => entry.Key == Key("name") && entry.Kind == ReferenceKind.Literal);
        Assert.Contains(references, entry => entry.Key == Key("key") && entry.Kind == ReferenceKind.Literal);
        Assert.Contains(references, entry => entry.Key == Key("e") && entry.Kind == ReferenceKind.Literal);
    }

    [Fact]
    public void References_SpacedAnimReferenceKeysTheSameAsAJoinedOne()
    {
        // `%run` and `% run` name one animation, so find-all-references has to see one symbol with
        // two sites rather than 'run' and ' run'.
        ParseResult result = Analyze("function f()\n{\nx = %run;\ny = % run;\n}");

        List<ReferenceEntry> animations =
            [.. result.Extraction.References.Where(entry => entry.Key.Kind == SymbolKind.AnimReference)];

        Assert.Equal(2, animations.Count);
        Assert.All(animations, entry => Assert.Equal(new SymbolKey(null, "run", SymbolKind.AnimReference), entry.Key));
    }

    [Fact]
    public void References_FieldsAndDefinitions()
    {
        ParseResult result = Analyze("function f()\n{\nx = self.owner;\n}");
        List<ReferenceEntry> references = [.. result.Extraction.References];

        Assert.Contains(references, entry =>
            entry.Key == new SymbolKey(null, "owner", SymbolKind.Field) && entry.Kind == ReferenceKind.FieldAccess);

        Assert.Contains(references, entry =>
            entry.Key == new SymbolKey("test", "f", SymbolKind.Function) && entry.Kind == ReferenceKind.Definition);
    }

    [Fact]
    public void References_MacroDefinitionAndUse()
    {
        ParseResult result = Analyze("#define MAX_HEALTH 100\nfunction f()\n{\nx = MAX_HEALTH;\n}");
        List<ReferenceEntry> references = [.. result.Extraction.References];

        Assert.Contains(references, entry =>
            entry.Key == new SymbolKey(null, "MAX_HEALTH", SymbolKind.Macro) && entry.Kind == ReferenceKind.Definition);

        Assert.Contains(references, entry =>
            entry.Key == new SymbolKey(null, "MAX_HEALTH", SymbolKind.Macro) && entry.Kind == ReferenceKind.MacroUse);
    }

    [Fact]
    public void References_AShadowedMacroDefinitionStillGetsADefinitionReference()
    {
        // MAX is defined twice; the table keeps only the second, but the reader looking AT the
        // first #define line — the one DuplicateMacroDefinition already flags as the one being
        // replaced — deserves go-to-definition and rename recognizing it as a definition too.
        ParseResult result = Analyze("#define MAX 4\n#define MAX 8\nx = MAX;");
        List<ReferenceEntry> definitions = [.. result.Extraction.References
            .Where(entry => entry.Key == new SymbolKey(null, "MAX", SymbolKind.Macro) && entry.Kind == ReferenceKind.Definition)];

        Assert.Equal(2, definitions.Count);
        Assert.Equal(0, definitions[0].Range.Start.Line);
        Assert.Equal(1, definitions[1].Range.Start.Line);
    }

    [Fact]
    public void DocComment_AssociatesWithFunctionBelow()
    {
        string source = """
            /@
            Summary: Does the thing carefully.
            MandatoryArg: <target>: who to affect
            @/
            function do_thing( target )
            {
            }
            """;

        ParseResult result = Analyze(source);
        FunctionSymbol function = Assert.Single(result.Extraction.Functions);

        Assert.Equal("Does the thing carefully.", function.Doc.Summary);
        Assert.Single(function.Doc.Arguments);
        Assert.Equal("target", function.Doc.Arguments[0].Name);
    }

    [Fact]
    public void DocComment_ReadsTheQuotedStockFormat()
    {
        // How every shipped script actually writes it: each line wrapped in double quotes. The
        // key regex starts at \w, so the leading quote made the whole block parse to nothing
        // while the unquoted test above kept passing — 15,226 of 15,231 corpus functions ended
        // up with an empty doc.
        string source = """
            /@
            "Name: do_thing( <target> )"
            "Summary: Does the thing carefully."
            "Module: Utility"
            "MandatoryArg: <target> : who to affect"
            "OptionalArg: <force> : skip the check"
            "Example: do_thing( self );"
            "SPMP: both"
            @/
            function do_thing( target, force )
            {
            }
            """;

        ParseResult result = Analyze(source);
        FunctionSymbol function = Assert.Single(result.Extraction.Functions);

        Assert.Equal("Does the thing carefully.", function.Doc.Summary);
        Assert.Equal("Utility", function.Doc.Module);
        Assert.Equal("both", function.Doc.Spmp);
        Assert.Equal("do_thing( <target> )", function.Doc.Name);
        Assert.Equal("do_thing( self );", Assert.Single(function.Doc.Examples));

        Assert.Equal(2, function.Doc.Arguments.Length);
        Assert.Equal("target", function.Doc.Arguments[0].Name);
        Assert.Equal("who to affect", function.Doc.Arguments[0].Description);
        Assert.False(function.Doc.Arguments[0].Optional);
        Assert.True(function.Doc.Arguments[1].Optional);
    }

    [Fact]
    public void Gsh_LenientMode_SuppressesParseDiagnosticsKeepsMacros()
    {
        // A GSH fragment: bare statements, no enclosing function — invalid as a script.
        ParseResult result = Analyze("#define FLAG 1\nfoo = FLAG;", @"c:\work\scripts\shared\shared.gsh");

        Assert.Equal(GSCode.Core.Symbols.ScriptLanguage.Gsh, result.Language);
        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => (int)diagnostic.Code >= 3000 && (int)diagnostic.Code < 4000);
        Assert.True(result.Preprocessed.Macros.TryGet("FLAG", out _));
    }
}
