using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Extraction;
using GSCode.Parser.Preprocessing;
using Xunit;

namespace GSCode.Workspace.Tests.Analysis;

/// <summary>
/// In-source suppression, carried inside comments.
///
/// A comment is not a style choice here but the only place these can live: GSC's linker reads the
/// file itself, so a pragma outside a comment would have to be real syntax the language does not
/// have, and every script carrying one would stop compiling.
///
/// `disable`/`restore` is C#'s pair, kept for the reason it was chosen there: each word says which
/// way it goes, which `on`/`off` stops doing as soon as two are nested. C#'s `warning` is not kept
/// — suppression here is keyed on the code alone and reaches every severity, so the word would
/// claim a narrowness the code does not have. It is still ACCEPTED, and one test below pins that.
/// </summary>
public class PragmaDirectiveTests
{
    private static ImmutableArray<PragmaDirective> Scan(string source)
    {
        ParseResult result = ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc",
            ScriptLanguage.Gsc,
            SourceText.From(source),
            NullInsertProvider.Instance,
            new NameTable());

        return PragmaDirectives.Scan(result.Lexed.Tokens, result.Text);
    }

    [Fact]
    public void ACodeIsSuppressedBetweenDisableAndRestore()
    {
        ImmutableArray<PragmaDirective> directives = Scan(
            "// #pragma disable 5014\nfunction f()\n{\n}\n// #pragma restore 5014\nfunction g()\n{\n}\n");

        Assert.True(PragmaDirectives.IsSuppressed(directives, GscDiagnosticCode.BuiltinFunctionNotFound, 2));
        Assert.False(PragmaDirectives.IsSuppressed(directives, GscDiagnosticCode.BuiltinFunctionNotFound, 6));
    }

    [Fact]
    public void OnlyTheNamedCodeIsSuppressed()
    {
        // Suppressing one rule must not quietly suppress the rest, which is the whole reason the
        // pragma names a code at all.
        ImmutableArray<PragmaDirective> directives = Scan("// #pragma disable 5014\nfunction f()\n{\n}\n");

        Assert.True(PragmaDirectives.IsSuppressed(directives, GscDiagnosticCode.BuiltinFunctionNotFound, 2));
        Assert.False(PragmaDirectives.IsSuppressed(directives, GscDiagnosticCode.ScriptFunctionNotFound, 2));
    }

    [Theory]
    [InlineData("5014")]
    [InlineData("gscode-5014")]
    [InlineData("GSCODE-5014")]
    public void ACodeMayBeWrittenTheWayTheEditorShowsIt(string written)
    {
        // "gscode-5014" is what is on screen when someone decides to suppress one, so it is what
        // gets copied.
        ImmutableArray<PragmaDirective> directives = Scan($"// #pragma disable {written}\nfunction f()\n{{\n}}\n");

        Assert.True(PragmaDirectives.IsSuppressed(directives, GscDiagnosticCode.BuiltinFunctionNotFound, 2));
    }

    [Fact]
    public void AllSuppressesEveryCode()
    {
        ImmutableArray<PragmaDirective> directives = Scan("// #pragma disable all\nfunction f()\n{\n}\n");

        Assert.True(PragmaDirectives.IsSuppressed(directives, GscDiagnosticCode.BuiltinFunctionNotFound, 2));
        Assert.True(PragmaDirectives.IsSuppressed(directives, GscDiagnosticCode.UnusedLocal, 2));
    }

    [Fact]
    public void NothingAboveTheDisableIsSuppressed()
    {
        ImmutableArray<PragmaDirective> directives = Scan(
            "function f()\n{\n}\n// #pragma disable 5014\n");

        Assert.False(PragmaDirectives.IsSuppressed(directives, GscDiagnosticCode.BuiltinFunctionNotFound, 1));
    }

    [Fact]
    public void ABlockCommentCarriesOneToo()
    {
        // The rule is "inside a comment", not "inside a line comment".
        ImmutableArray<PragmaDirective> directives = Scan("/* #pragma disable 5014 */\nfunction f()\n{\n}\n");

        Assert.True(PragmaDirectives.IsSuppressed(directives, GscDiagnosticCode.BuiltinFunctionNotFound, 2));
    }

    [Fact]
    public void FormatIsItsOwnTargetAndNotADiagnosticCode()
    {
        // The two must not bleed into each other: switching the formatter off is not a licence to
        // stop reporting problems in that region.
        ImmutableArray<PragmaDirective> directives = Scan(
            "// #pragma disable format\nfunction f()\n{\n}\n// #pragma restore format\n");

        Assert.True(PragmaDirectives.IsFormatDisabled(directives, 2));
        Assert.False(PragmaDirectives.IsFormatDisabled(directives, 6));
        Assert.False(PragmaDirectives.IsSuppressed(directives, GscDiagnosticCode.BuiltinFunctionNotFound, 2));
    }

    [Fact]
    public void AnUnmatchedDisableRunsToTheEndOfTheFile()
    {
        // Read in source order rather than as nested scopes, so this needs no special case and
        // gives the answer anyone would expect.
        ImmutableArray<PragmaDirective> directives = Scan("// #pragma disable 5014\n");

        Assert.True(PragmaDirectives.IsSuppressed(directives, GscDiagnosticCode.BuiltinFunctionNotFound, 9999));
    }

    [Fact]
    public void SomethingThatIsNotAPragmaIsIgnored()
    {
        // No '#', no action word, an action with no target, and ordinary prose. `#pragma disable
        // 5014` used to sit in this list as the no-`warning` case; it is the documented spelling
        // now, which is why the negatives here are shapes that cannot become one.
        Assert.Empty(Scan(
            "// pragma disable 5014\n// #pragma 5014\n// #pragma disable\n// just a comment\n"));
    }

    [Theory]
    [InlineData("// #pragma warning disable 5014\n")]
    [InlineData("// #pragma WARNING disable gscode-5014\n")]
    public void TheEarlierWarningSpellingStillWorks(string source)
    {
        // Accepted, not taught. `warning` was the published word for the two weeks between the
        // pragma landing and being renamed off it, and a suppression that silently stops
        // suppressing is the regression this repo has already been bitten by once — the
        // diagnostics come back and nothing on screen says why. It costs one optional regex group.
        Assert.True(PragmaDirectives.IsSuppressed(
            Scan(source + "function f()\n{\n}\n"), GscDiagnosticCode.BuiltinFunctionNotFound, 2));
    }

    [Fact]
    public void EverySeverityIsSuppressible()
    {
        // The reason `warning` was dropped from the spelling, pinned rather than left to the
        // summary: IsSuppressed never sees a severity, so naming a code turns it off whatever it
        // carries. BuiltinFunctionNotFound is an Error and UnusedLocal is a Hint.
        Assert.True(PragmaDirectives.IsSuppressed(
            Scan("// #pragma disable 5014\nfunction f()\n{\n}\n"),
            GscDiagnosticCode.BuiltinFunctionNotFound,
            2));

        Assert.True(PragmaDirectives.IsSuppressed(
            Scan("// #pragma disable 5008\nfunction f()\n{\n}\n"),
            GscDiagnosticCode.UnusedLocal,
            2));
    }
}
