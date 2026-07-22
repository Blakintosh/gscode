using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using Xunit;

namespace GSCode.Parser.Tests.Extraction;

/// <summary>
/// Macro-expanded tokens report the INVOCATION's range, so a macro's whole body stacks onto the
/// one call site. The reported symptom was go-to-definition on a multi-statement macro landing on
/// the first call in its body.
///
/// The parser-level contract is that those uses are recorded under ReferenceKind.ExpandedFromMacro
/// and never as an ordinary Call — the fact is kept, so the unused-import lint can see that
/// REGISTER_SYSTEM really does call into the `system` namespace, while navigation (which skips
/// that kind) still resolves the cursor to the macro. SymbolAtPositionTests covers that half.
/// </summary>
public class MacroExpansionReferenceTests
{
    private static ParseResult Analyze(string source)
    {
        return ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc", ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable());
    }

    /// <summary>The reported shape: a multi-statement macro whose body opens with calls.</summary>
    private const string NewStateSource = """
        #define NEW_STATE(__state) flagsys::clear( "ready" ); \
            flagsys::clear( "done" ); \
            _str_state = __state; \
            self notify( __state );

        function play( str_alert_state )
        {
            NEW_STATE( "play" );
        }
        """;

    private static ImmutableArray<ReferenceEntry> ReferencesAt(ParseResult result, int line)
    {
        return result.Extraction.References
            .Where(entry => entry.Range.Start.Line == line)
            .ToImmutableArray();
    }

    [Fact]
    public void MacroInvocation_CarriesTheMacroUse_AndNoOrdinaryCall()
    {
        ParseResult result = Analyze(NewStateSource);

        // The invocation sits on the NEW_STATE( "play" ); line.
        int invocationLine = NewStateSource.Split('\n').ToList().FindIndex(line => line.Contains("NEW_STATE( \"play\" )"));
        ImmutableArray<ReferenceEntry> atCall = ReferencesAt(result, invocationLine);

        // The macro use must be here, and nothing from its body may claim the position as an
        // ordinary CALL — that is what let go-to-definition land on flagsys::clear.
        // (The "play" string literal also lives here legitimately — the caller wrote it.)
        Assert.Contains(atCall, entry => entry.Kind == ReferenceKind.MacroUse);
        Assert.DoesNotContain(atCall, entry => entry.Kind == ReferenceKind.Call);
        Assert.DoesNotContain(atCall, entry => entry.Kind == ReferenceKind.ClassUse);
    }

    [Fact]
    public void MacroBodyCalls_AreRecordedAsExpansions_NotAsCalls()
    {
        ParseResult result = Analyze(NewStateSource);

        // `clear` lives in the macro body. The USE is kept, so a lint can tell this file reaches
        // into the flagsys namespace — REGISTER_SYSTEM's expansion into system::register is the
        // case that matters — but never as a Call, which is the kind navigation resolves.
        ImmutableArray<ReferenceEntry> toClear = result.Extraction.References
            .Where(entry => string.Equals(entry.Key.Name, "clear", StringComparison.OrdinalIgnoreCase))
            .ToImmutableArray();

        Assert.NotEmpty(toClear);
        Assert.All(toClear, entry => Assert.Equal(ReferenceKind.ExpandedFromMacro, entry.Kind));
    }

    [Fact]
    public void ArgumentsWrittenAtTheCallSite_AreStillRecorded()
    {
        // The "play" literal is written by the caller, so it keeps its own provenance and
        // must survive the filter — otherwise literal find-all-references would regress.
        ParseResult result = Analyze(NewStateSource);

        Assert.Contains(
            result.Extraction.References,
            entry => entry.Key.Kind == SymbolKind.StringLiteral && entry.Key.Name == "play");
    }

    [Fact]
    public void OrdinaryCalls_AreUnaffected()
    {
        ParseResult result = Analyze("function f()\n{\n    flagsys::clear( \"ready\" );\n}\n");

        Assert.Contains(
            result.Extraction.References,
            entry => string.Equals(entry.Key.Name, "clear", StringComparison.OrdinalIgnoreCase));
    }
}
