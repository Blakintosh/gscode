using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Extraction;
using GSCode.Parser.Preprocessing;
using Xunit;

namespace GSCode.Parser.Tests.Extraction;

public class ExtractionTests
{
    private static ParseResult Analyze(string source, string path = @"c:\work\scripts\test.gsc")
    {
        return ScriptAnalysis.Analyze(
            path,
            ScriptAnalysis.LanguageFromPath(path),
            SourceText.From(source),
            NullInsertProvider.Instance,
            new NameTable());
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

        Assert.Contains(
            Analyze("#precache( \"model\", \"a\", \"b\" );").AllDiagnostics,
            diagnostic => diagnostic.Code == GscDiagnosticCode.WrongPrecacheArgumentCount);

        // The string family accepts extra values.
        Assert.DoesNotContain(
            Analyze("#precache( \"string\", \"A\", \"B\", \"C\" );").AllDiagnostics,
            diagnostic => diagnostic.Code == GscDiagnosticCode.WrongPrecacheArgumentCount);
    }

    [Fact]
    public void DefaultParameter_MustBePlainValue()
    {
        Assert.DoesNotContain(
            Analyze("function f( a = 1, b = -2.5, c = (0,0,0), d = \"x\" )\n{\n}").AllDiagnostics,
            diagnostic => diagnostic.Code == GscDiagnosticCode.NonValueDefaultParameter);

        Assert.Contains(
            Analyze("function f( a = get_default() )\n{\n}").AllDiagnostics,
            diagnostic => diagnostic.Code == GscDiagnosticCode.NonValueDefaultParameter);
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
    public void References_LiteralsWithCaseRules()
    {
        ParseResult result = Analyze("function f()\n{\nself notify(\"Death_Event\");\nx = #\"Hash_Val\";\ny = &\"MENU_LABEL\";\n}");
        List<ReferenceEntry> references = [.. result.Extraction.References];

        // Strings are content-exact (case preserved)...
        Assert.Contains(references, entry =>
            entry.Key == new SymbolKey(null, "Death_Event", SymbolKind.StringLiteral) && entry.Kind == ReferenceKind.Literal);

        // ...hash strings and istrings are case-insensitive (lowercase keys).
        Assert.Contains(references, entry => entry.Key == new SymbolKey(null, "hash_val", SymbolKind.HashString));
        Assert.Contains(references, entry => entry.Key == new SymbolKey(null, "menu_label", SymbolKind.LocalizedString));
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
    public void Gsh_LenientMode_SuppressesParseDiagnosticsKeepsMacros()
    {
        // A GSH fragment: bare statements, no enclosing function — invalid as a script.
        ParseResult result = Analyze("#define FLAG 1\nfoo = FLAG;", @"c:\work\scripts\shared\shared.gsh");

        Assert.Equal(GSCode.Core.Symbols.ScriptLanguage.Gsh, result.Language);
        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => (int)diagnostic.Code >= 3000 && (int)diagnostic.Code < 4000);
        Assert.True(result.Preprocessed.Macros.TryGet("FLAG", out _));
    }
}
