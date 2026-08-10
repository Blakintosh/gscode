using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Workspace.Database;
using Xunit;

namespace GSCode.Workspace.Tests.Database;

/// <summary>
/// A macro's whole body reports the INVOCATION's range, so every use inside it lands on the one
/// call site. Those uses are now recorded — the unused-import lint has to see that
/// `REGISTER_SYSTEM(...)` calls into the `system` namespace — which puts several references on
/// exactly the characters the user is pointing at.
///
/// The cursor must still resolve to the MACRO. The text under it spells the macro's name, and
/// the reported bug was go-to-definition on a multi-statement macro landing on the first call in
/// its body instead.
/// </summary>
public class MacroNavigationTests
{
    private const string Source = """
        #define NEW_STATE(__state) flagsys::clear( "ready" ); \
            flagsys::clear( "done" ); \
            self notify( __state );

        function play( str_alert_state )
        {
            NEW_STATE( "play" );
        }
        """;

    private static ParseResult Analyze()
    {
        return ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc", ScriptLanguage.Gsc, SourceText.From(Source), NullInsertProvider.Instance, new NameTable());
    }

    private static Position InvocationPosition(ParseResult result)
    {
        // On the NEW_STATE identifier itself.
        int line = Source.Split('\n').ToList().FindIndex(l => l.Contains("NEW_STATE( \"play\" )"));
        return new Position(line, Source.Split('\n')[line].IndexOf("NEW_STATE", StringComparison.Ordinal) + 2);
    }

    [Fact]
    public void TheCursorOnAMacroInvocationResolvesToTheMacro()
    {
        ParseResult result = Analyze();

        PositionHit hit = SymbolAtPosition.Resolve(result, InvocationPosition(result));

        Assert.Equal(HitKind.Reference, hit.Kind);
        Assert.Equal(SymbolKind.Macro, hit.Key.Kind);
        Assert.Equal("NEW_STATE", hit.Key.Name);
    }

    [Fact]
    public void ItNeverResolvesToSomethingTheMacroExpandedInto()
    {
        // The reported bug: landing on flagsys::clear, the first call in the body.
        ParseResult result = Analyze();

        PositionHit hit = SymbolAtPosition.Resolve(result, InvocationPosition(result));

        Assert.NotEqual("clear", hit.Key.Name);
        Assert.NotEqual(ReferenceKind.ExpandedFromMacro, hit.ReferenceKind);
    }

    [Fact]
    public void TheExpandedUsesAreStillRecorded()
    {
        // The other half of the contract: navigation ignores them, but they exist, because the
        // file really does call flagsys::clear and a lint has to be able to tell.
        ParseResult result = Analyze();

        Assert.Contains(
            result.Extraction.References,
            entry => entry.Kind == ReferenceKind.ExpandedFromMacro
                && string.Equals(entry.Key.Name, "clear", StringComparison.OrdinalIgnoreCase));
    }
}
