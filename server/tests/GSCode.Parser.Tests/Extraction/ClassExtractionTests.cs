using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using Xunit;

namespace GSCode.Parser.Tests.Extraction;

/// <summary>
/// The written keys extraction produces for classes and their methods.
///
/// These are the contract every later stage rests on: the class graph, method resolution, the
/// reference union behind go-to-definition and CodeLens, and the lints. Before the owner-class slot
/// existed, a method DEFINITION was keyed <c>("", name)</c>, a bare call inside the class under the
/// FILE's namespace, and an arrow call under none — three shapes that could never meet, which is
/// why none of those features worked on a class.
/// </summary>
public class ClassExtractionTests
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

    /// <summary>The single reference whose name matches, of the given kind.</summary>
    private static SymbolKey KeyOf(ParseResult result, string name, ReferenceKind kind)
    {
        ImmutableArray<ReferenceEntry> matches =
        [
            .. result.Extraction.References.Where(entry => entry.Key.Name == name && entry.Kind == kind)
        ];

        return Assert.Single(matches).Key;
    }

    [Fact]
    public void MethodDefinition_IsKeyedWithItsOwnerClassAndNoNamespace()
    {
        ParseResult result = Analyze("#namespace scene;\nclass cScene\n{\n    function add_object()\n    {\n    }\n}\n");

        SymbolKey key = KeyOf(result, "add_object", ReferenceKind.Definition);

        Assert.Equal("cscene", key.OwnerClass);
        Assert.Null(key.Namespace);
        Assert.Equal(SymbolKind.Function, key.Kind);
        Assert.True(key.IsMethod);
    }

    [Fact]
    public void MethodSymbol_CarriesItsOwnerClass()
    {
        ParseResult result = Analyze("class cScene\n{\n    function add_object()\n    {\n    }\n}\n");

        ClassSymbol classSymbol = Assert.Single(result.Extraction.Classes);
        FunctionSymbol method = Assert.Single(classSymbol.Methods);
        Assert.Equal("cscene", method.OwnerClassKeyName);
    }

    [Fact]
    public void BareCallInsideAClassBody_IsKeyedWithTheEnclosingClass()
    {
        // The key the definition above uses, so the two meet on identity. Under the file's namespace
        // — which is what this used to be — they never could.
        ParseResult result = Analyze(
            "#namespace scene;\nclass cScene\n{\n    function play()\n    {\n        add_object();\n    }\n}\n");

        SymbolKey key = KeyOf(result, "add_object", ReferenceKind.Call);

        Assert.Equal("cscene", key.OwnerClass);
        Assert.Null(key.Namespace);
    }

    [Fact]
    public void BareCallInsideAConstructorBody_IsKeyedWithTheEnclosingClass()
    {
        ParseResult result = Analyze(
            "class cScene\n{\n    constructor()\n    {\n        add_object();\n    }\n}\n");

        Assert.Equal("cscene", KeyOf(result, "add_object", ReferenceKind.Call).OwnerClass);
    }

    [Fact]
    public void BareCallInsideADestructorBody_IsKeyedWithTheEnclosingClass()
    {
        ParseResult result = Analyze(
            "class cScene\n{\n    destructor()\n    {\n        cleanup();\n    }\n}\n");

        Assert.Equal("cscene", KeyOf(result, "cleanup", ReferenceKind.Call).OwnerClass);
    }

    [Fact]
    public void ConstructorAndDestructor_AreSurfacedOnTheClass()
    {
        ParseResult result = Analyze(
            "class cScene\n{\n    constructor()\n    {\n        a = 1;\n    }\n    destructor()\n    {\n    }\n}\n");

        ClassSymbol classSymbol = Assert.Single(result.Extraction.Classes);
        Assert.True(classSymbol.HasConstructor);
        Assert.True(classSymbol.HasDestructor);
        Assert.NotNull(classSymbol.Constructor);
        Assert.NotNull(classSymbol.Destructor);
        Assert.Equal("cscene", classSymbol.Constructor!.OwnerClassKeyName);

        // The whole point of surfacing them: the assignments inside were built and thrown away.
        AssignmentSymbol assignment = Assert.Single(classSymbol.Constructor.Assignments);
        Assert.Equal("a", assignment.Name);
    }

    [Fact]
    public void ConstructorAndDestructor_AreNotListedAsMethods()
    {
        // Neither is callable by name, so listing them would offer them in method completion and
        // hash them into the export signature as though a caller could depend on one.
        ParseResult result = Analyze(
            "class cScene\n{\n    constructor()\n    {\n    }\n    destructor()\n    {\n    }\n    function play()\n    {\n    }\n}\n");

        ClassSymbol classSymbol = Assert.Single(result.Extraction.Classes);
        FunctionSymbol method = Assert.Single(classSymbol.Methods);
        Assert.Equal("play", method.Name);
    }

    [Fact]
    public void ArrowCallOnSelf_IsKeyedWithTheEnclosingClass()
    {
        ParseResult result = Analyze(
            "class cScene\n{\n    function play()\n    {\n        [[self]]->new_object();\n    }\n}\n");

        SymbolKey key = KeyOf(result, "new_object", ReferenceKind.MethodCall);

        Assert.Equal("cscene", key.OwnerClass);
        Assert.Null(key.Namespace);
    }

    [Fact]
    public void ArrowCallOnAnUnknownReceiver_HasNoOwnerClass()
    {
        // 155 of the 159 arrow calls in the stock BO3 scripts are this shape. The receiver's class
        // is not knowable without typing the local, so the key stays open and resolution answers it
        // by method name instead.
        ParseResult result = Analyze(
            "class cScene\n{\n    function play()\n    {\n        [[o_scene]]->stop();\n    }\n}\n");

        SymbolKey key = KeyOf(result, "stop", ReferenceKind.MethodCall);

        Assert.Null(key.OwnerClass);
        Assert.Null(key.Namespace);
    }

    [Fact]
    public void ArrowCallOutsideAClass_HasNoOwnerClass()
    {
        ParseResult result = Analyze("function run()\n{\n    [[self]]->stop();\n}\n");

        Assert.Null(KeyOf(result, "stop", ReferenceKind.MethodCall).OwnerClass);
    }

    [Fact]
    public void ArrowCall_IsDistinguishableFromAPlainCall()
    {
        // The kind is the only thing separating an untyped arrow call from sys::foo() and from an
        // unqualified call, all three of which key with no namespace and no owner.
        ParseResult result = Analyze("function run()\n{\n    [[o_scene]]->stop();\n}\n");

        ImmutableArray<ReferenceEntry> arrows =
        [
            .. result.Extraction.References.Where(entry => entry.Kind == ReferenceKind.MethodCall)
        ];

        Assert.Equal("stop", Assert.Single(arrows).Key.Name);
    }

    [Fact]
    public void NestedArrowCall_KeysBothMethodsIndependently()
    {
        // scene_shared.gsc:1926 writes exactly this. The inner receiver is self, the outer is a call
        // expression whose class is unknown, so the two must not be given the same owner.
        ParseResult result = Analyze(
            "class cScene\n{\n    function play()\n    {\n        [[ [[self]]->new_object() ]]->first_init();\n    }\n}\n");

        Assert.Equal("cscene", KeyOf(result, "new_object", ReferenceKind.MethodCall).OwnerClass);
        Assert.Null(KeyOf(result, "first_init", ReferenceKind.MethodCall).OwnerClass);
    }

    [Fact]
    public void QualifiedCallInsideAClass_DoesNotTakeTheEnclosingClassAsOwner()
    {
        // The written qualifier IS the identity. A dialect may declare a namespace and a class with
        // the same name and mean the namespace — BO3's phalanx.gsc does — so a qualified call has to
        // key exactly as it would outside a class, or it stops matching its own definition.
        ParseResult result = Analyze(
            "#namespace scene;\nclass cScene\n{\n    function play()\n    {\n        flagsys::clear( \"ready\" );\n    }\n}\n");

        SymbolKey key = KeyOf(result, "clear", ReferenceKind.Call);

        Assert.Null(key.OwnerClass);
        Assert.Equal("flagsys", key.Namespace);
    }

    [Fact]
    public void BaseQualifiedCallInsideAClass_KeysUnderTheWrittenQualifierOnly()
    {
        // scene_shared.gsc:106 — a derived class calling its base's method by name.
        ParseResult result = Analyze(
            "class cSceneObject : cScriptBundleObjectBase\n{\n    function first_init()\n    {\n        cScriptBundleObjectBase::init();\n    }\n}\n");

        SymbolKey key = KeyOf(result, "init", ReferenceKind.Call);

        Assert.Null(key.OwnerClass);
        Assert.Equal("cscriptbundleobjectbase", key.Namespace);
    }

    [Fact]
    public void TopLevelFunctionAndCall_AreUnchangedByTheClassWork()
    {
        ParseResult result = Analyze(
            "#namespace scene;\nfunction run()\n{\n    helper();\n}\nfunction helper()\n{\n}\n");

        SymbolKey definition = KeyOf(result, "helper", ReferenceKind.Definition);
        SymbolKey call = KeyOf(result, "helper", ReferenceKind.Call);

        Assert.Null(definition.OwnerClass);
        Assert.Equal("scene", definition.Namespace);
        Assert.Equal(definition, call);
    }

    [Fact]
    public void CallAfterAClassCloses_ReturnsToTheFileNamespace()
    {
        // The guard against the owner-class state leaking past the closing brace, which would key
        // every remaining call in the file to a class it is not in.
        ParseResult result = Analyze(
            "#namespace scene;\nclass cScene\n{\n    function play()\n    {\n    }\n}\nfunction run()\n{\n    helper();\n}\n");

        SymbolKey key = KeyOf(result, "helper", ReferenceKind.Call);

        Assert.Null(key.OwnerClass);
        Assert.Equal("scene", key.Namespace);
    }

    [Fact]
    public void TwoClassesInOneFile_KeepTheirOwnMethods()
    {
        ParseResult result = Analyze(
            "class cScene\n{\n    function play()\n    {\n    }\n}\nclass cOther\n{\n    function play()\n    {\n    }\n}\n");

        ImmutableArray<ReferenceEntry> definitions =
        [
            .. result.Extraction.References.Where(
                entry => entry.Key.Name == "play" && entry.Kind == ReferenceKind.Definition)
        ];

        Assert.Equal(2, definitions.Length);
        Assert.Contains(definitions, entry => entry.Key.OwnerClass == "cscene");
        Assert.Contains(definitions, entry => entry.Key.OwnerClass == "cother");
    }

    [Fact]
    public void AddressOfAMethodInsideAClass_IsKeyedWithTheEnclosingClass()
    {
        ParseResult result = Analyze(
            "class cScene\n{\n    function play()\n    {\n        f = &stop;\n    }\n    function stop()\n    {\n    }\n}\n");

        Assert.Equal("cscene", KeyOf(result, "stop", ReferenceKind.AddressOf).OwnerClass);
    }
}
