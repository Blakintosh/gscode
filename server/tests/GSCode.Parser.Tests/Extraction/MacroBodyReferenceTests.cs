using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser.Preprocessing;
using Xunit;

namespace GSCode.Parser.Tests.Extraction;

/// <summary>
/// A macro NAME used inside another macro's #define body — <c>#define BAR FOO</c> with
/// <c>#define FOO "something"</c> in scope — must be recorded as a MacroUse reference at its own
/// position, so hover/go-to-definition/semantic-tokens work on it directly.
///
/// Before this, a body name was only ever recorded when the OUTER macro (BAR) was actually
/// expanded somewhere in the file: <c>TryExpandBodyToken</c> walks the body live at that moment
/// and records the nested use then. A body name inside a macro that is never invoked got no
/// reference at all — hovering FOO on the #define BAR line showed nothing, and F12 went nowhere,
/// which was the reported symptom ("highlighting over FOO on the #define line would hint show
/// 'something'").
/// </summary>
public class MacroBodyReferenceTests
{
    private static ParseResult Analyze(string source)
    {
        return ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc", ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable());
    }

    [Fact]
    public void UnusedMacro_StillRecordsItsBodyNameAsAMacroUse()
    {
        // BAR is never invoked anywhere in the file — the nested-invocation path in the
        // preprocessor never runs, so this reference has to come from somewhere else.
        ParseResult result = Analyze("#define FOO \"something\"\n#define BAR FOO\n");

        Assert.Contains(
            result.Extraction.References,
            entry => entry.Key.Kind == SymbolKind.Macro
                && entry.Key.Name == "FOO"
                && entry.Kind == ReferenceKind.MacroUse
                && entry.Range.Start.Line == 1);
    }

    [Fact]
    public void MacroParameterName_IsNotMistakenForAMacroUse()
    {
        // __a is a PARAMETER of IS_TRUE, not a reference to some macro named __a — even if one
        // happens to exist in scope. Parameter substitution takes priority.
        ParseResult result = Analyze(
            "#define __a 999\n#define IS_TRUE(__a) (isdefined(__a) && __a)\n");

        Assert.DoesNotContain(
            result.Extraction.References,
            entry => entry.Key.Kind == SymbolKind.Macro
                && entry.Key.Name == "__a"
                && entry.Kind == ReferenceKind.MacroUse);
    }

    [Fact]
    public void UndefinedNameInABody_RecordsNothing()
    {
        // NOT_A_MACRO names nothing in this file, so there is nothing to reference — this must not
        // throw or fabricate a use of an undefined name.
        ParseResult result = Analyze("#define BAR NOT_A_MACRO\n");

        Assert.DoesNotContain(
            result.Extraction.References,
            entry => entry.Key.Kind == SymbolKind.Macro && entry.Key.Name == "NOT_A_MACRO");
    }

    [Fact]
    public void InvokedMacro_DoesNotDoubleUpItsBodyReference()
    {
        // BAR IS invoked here, so the preprocessor's own nested-invocation path already records
        // FOO once at this exact position. The body scan must not add a second entry there.
        ParseResult result = Analyze(
            "#define FOO \"something\"\n#define BAR FOO\nfunction f()\n{\n    x = BAR;\n}\n");

        int atDefineLine = 0;
        foreach ( ReferenceEntry entry in result.Extraction.References )
        {
            if ( entry.Key.Kind == SymbolKind.Macro && entry.Key.Name == "FOO" && entry.Range.Start.Line == 1 )
            {
                atDefineLine++;
            }
        }

        Assert.Equal(1, atDefineLine);
    }
}
