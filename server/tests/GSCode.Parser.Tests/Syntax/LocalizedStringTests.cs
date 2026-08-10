using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using Xunit;

namespace GSCode.Parser.Tests.Syntax;

/// <summary>
/// `&amp;"..."` is a localized string, never address-of. The lexer folds the adjacent form into
/// one token, so these cover the two ways the pieces arrive apart: written with a space, and
/// supplied by a macro — the shape found in stock `_quadtank.gsc`, where
/// <c>#define WEAKSPOT_BONE_NAME "tag_target_lower"</c> is used as <c>&amp;WEAKSPOT_BONE_NAME</c>.
/// </summary>
public class LocalizedStringTests
{
    private static ParseResult Analyze(string source)
    {
        return ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc", ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable());
    }

    private static ImmutableArray<Diagnostic> Errors(ParseResult result)
    {
        return result.AllDiagnostics
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToImmutableArray();
    }

    private static ImmutableArray<ReferenceEntry> IStrings(ParseResult result)
    {
        return result.Extraction.References
            .Where(entry => entry.Key.Kind == SymbolKind.LocalizedString)
            .ToImmutableArray();
    }

    [Fact]
    public void Adjacent_IsAnIString()
    {
        // The pre-existing lexer path, pinned so the parser change cannot regress it.
        ParseResult result = Analyze("function f()\n{\n    x = &\"MENU_ITEM\";\n}\n");

        Assert.Empty(Errors(result));
        Assert.Equal("MENU_ITEM", Assert.Single(IStrings(result)).Key.Name);
    }

    [Fact]
    public void Spaced_IsAlsoAnIString()
    {
        // Spacing is not meaningful here — the parser stream is trivia-free.
        ParseResult result = Analyze("function f()\n{\n    x = & \"MENU_ITEM\";\n}\n");

        Assert.Empty(Errors(result));
        Assert.Equal("MENU_ITEM", Assert.Single(IStrings(result)).Key.Name);
    }

    [Fact]
    public void MacroSuppliedString_IsAnIString()
    {
        // The stock _quadtank.gsc shape: previously "Expected an expression but found ...".
        string source = "#define WEAKSPOT_BONE_NAME \"tag_target_lower\"\n"
            + "function f()\n{\n    self trigger( &WEAKSPOT_BONE_NAME );\n}\n";

        Assert.Empty(Errors(Analyze(source)));
    }

    [Fact]
    public void AddressOfAFunction_StillParses()
    {
        // The other meaning of `&` must be untouched.
        ParseResult result = Analyze("function f()\n{\n    callback = &my_handler;\n}\n");

        Assert.Empty(Errors(result));
        Assert.Empty(IStrings(result));
    }

    [Fact]
    public void QualifiedAddressOf_StillParses()
    {
        ParseResult result = Analyze("function f()\n{\n    callback = &util::my_handler;\n}\n");

        Assert.Empty(Errors(result));
    }

    [Fact]
    public void SpacedIString_CarriesTheSameContentAsTheAdjacentForm()
    {
        // Both spellings must index identically, or find-all-references splits in two.
        ReferenceEntry adjacent = Assert.Single(IStrings(Analyze("function f()\n{\n    x = &\"HUD_TEXT\";\n}\n")));
        ReferenceEntry spaced = Assert.Single(IStrings(Analyze("function f()\n{\n    x = & \"HUD_TEXT\";\n}\n")));

        Assert.Equal(adjacent.Key, spaced.Key);
    }
}
