using System.Linq;
using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using Xunit;

namespace GSCode.Parser.Tests.Extraction;

/// <summary>
/// Include-merge resolution. Under the Infinity Ward dialects <c>#include</c> MERGES a file's
/// functions into the including scope, so a function is reached by NAME, not by a namespace. Go-to-
/// definition, find-references, and rename all run on reference-key equality, so the fix is to key a
/// function's definition and every call to it identically — with no namespace — so a call anywhere
/// in the merged scope resolves to the definition wherever it lives. BO3 keeps the namespace as part
/// of a function's identity, so its keys are unchanged.
/// </summary>
public class DialectResolutionTests
{
    private static readonly GameProfile Cod4 = GameProfile.ByName("cod4")!;
    private static readonly GameProfile Bo3 = GameProfile.BlackOps3;

    private static ParseResult Analyze(string source, GameProfile profile)
    {
        return ScriptAnalysis.Analyze(
            @"c:\work\scripts\maps\mp\_utility.gsc",
            ScriptLanguage.Gsc,
            SourceText.From(source),
            NullInsertProvider.Instance,
            new NameTable(),
            profile);
    }

    private static SymbolKey KeyOf(ParseResult result, ReferenceKind kind, string name)
    {
        return result.Extraction.References
            .Single(entry => entry.Kind == kind && entry.Key.Name == name && entry.Key.Kind == SymbolKind.Function)
            .Key;
    }

    [Fact]
    public void AnIncludeDialectKeysAFunctionDefinitionWithoutANamespace()
    {
        // The file stem is "_utility"; under #include that is NOT the function's namespace.
        ParseResult result = Analyze("helper()\n{\n}\n", Cod4);

        SymbolKey definition = KeyOf(result, ReferenceKind.Definition, "helper");
        Assert.Null(definition.Namespace);
    }

    [Fact]
    public void AnUnqualifiedCallResolvesToTheDefinitionByName()
    {
        // Definition and call must share one key for go-to-definition (which is key equality).
        ParseResult result = Analyze("helper()\n{\n}\nrun()\n{\n\thelper();\n}\n", Cod4);

        SymbolKey definition = KeyOf(result, ReferenceKind.Definition, "helper");
        SymbolKey call = KeyOf(result, ReferenceKind.Call, "helper");

        Assert.Null(call.Namespace);
        Assert.Equal(definition, call);
    }

    [Fact]
    public void APathCallSharesTheDefinitionKeyForTheSameName()
    {
        // maps\mp\_utility::foo() and a bare foo definition reduce to the same (null, foo) key, so a
        // path call resolves to the merged function just like an unqualified one.
        ParseResult result = Analyze("foo()\n{\n}\nrun()\n{\n\tmaps\\mp\\_utility::foo();\n}\n", Cod4);

        SymbolKey definition = KeyOf(result, ReferenceKind.Definition, "foo");
        SymbolKey call = KeyOf(result, ReferenceKind.Call, "foo");

        Assert.Null(call.Namespace);
        Assert.Equal(definition, call);
    }

    [Fact]
    public void APathCallRecordsItsTargetFile()
    {
        // The explicit path is kept so go-to-definition can pin the call to that one file.
        ParseResult result = Analyze("run()\n{\n\tmaps\\mp\\_utility::foo();\n}\n", Cod4);

        GSCode.Parser.Extraction.PathCallReference pathCall = Assert.Single(result.Extraction.PathCalls);
        Assert.Equal("maps\\mp\\_utility", pathCall.Path);
    }

    [Fact]
    public void ALeadingScopeResolutionRecordsNoTargetFile()
    {
        // ::foo is a local pointer with no explicit file target.
        ParseResult result = Analyze("run()\n{\n\t::foo();\n}\n", Cod4);

        Assert.Empty(result.Extraction.PathCalls);
    }

    [Fact]
    public void BlackOps3StillKeysFunctionsByNamespace()
    {
        // BO3 identity includes the namespace (the file stem here, "_utility"), unchanged.
        ParseResult result = Analyze("function helper()\n{\n}\nfunction run()\n{\n\thelper();\n}\n", Bo3);

        SymbolKey definition = KeyOf(result, ReferenceKind.Definition, "helper");
        SymbolKey call = KeyOf(result, ReferenceKind.Call, "helper");

        Assert.Equal("_utility", definition.Namespace);
        Assert.Equal(definition, call);
    }
}
