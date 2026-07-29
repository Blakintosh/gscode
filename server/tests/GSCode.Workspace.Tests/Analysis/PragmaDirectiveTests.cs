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
/// The spelling matches C#'s deliberately. Anyone reaching for this already knows what
/// `#pragma warning disable` means, and `disable`/`restore` say which way they go — which
/// `on`/`off` stops doing as soon as two are nested.
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
            "// #pragma warning disable 5014\nfunction f()\n{\n}\n// #pragma warning restore 5014\nfunction g()\n{\n}\n");

        Assert.True(PragmaDirectives.IsSuppressed(directives, GscDiagnosticCode.BuiltinFunctionNotFound, 2));
        Assert.False(PragmaDirectives.IsSuppressed(directives, GscDiagnosticCode.BuiltinFunctionNotFound, 6));
    }

    [Fact]
    public void OnlyTheNamedCodeIsSuppressed()
    {
        // Suppressing one rule must not quietly suppress the rest, which is the whole reason the
        // pragma names a code at all.
        ImmutableArray<PragmaDirective> directives = Scan("// #pragma warning disable 5014\nfunction f()\n{\n}\n");

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
        ImmutableArray<PragmaDirective> directives = Scan($"// #pragma warning disable {written}\nfunction f()\n{{\n}}\n");

        Assert.True(PragmaDirectives.IsSuppressed(directives, GscDiagnosticCode.BuiltinFunctionNotFound, 2));
    }

    [Fact]
    public void AllSuppressesEveryCode()
    {
        ImmutableArray<PragmaDirective> directives = Scan("// #pragma warning disable all\nfunction f()\n{\n}\n");

        Assert.True(PragmaDirectives.IsSuppressed(directives, GscDiagnosticCode.BuiltinFunctionNotFound, 2));
        Assert.True(PragmaDirectives.IsSuppressed(directives, GscDiagnosticCode.UnusedLocal, 2));
    }

    [Fact]
    public void NothingAboveTheDisableIsSuppressed()
    {
        ImmutableArray<PragmaDirective> directives = Scan(
            "function f()\n{\n}\n// #pragma warning disable 5014\n");

        Assert.False(PragmaDirectives.IsSuppressed(directives, GscDiagnosticCode.BuiltinFunctionNotFound, 1));
    }

    [Fact]
    public void ABlockCommentCarriesOneToo()
    {
        // The rule is "inside a comment", not "inside a line comment".
        ImmutableArray<PragmaDirective> directives = Scan("/* #pragma warning disable 5014 */\nfunction f()\n{\n}\n");

        Assert.True(PragmaDirectives.IsSuppressed(directives, GscDiagnosticCode.BuiltinFunctionNotFound, 2));
    }

    [Fact]
    public void FormatIsItsOwnTargetAndNotADiagnosticCode()
    {
        // The two must not bleed into each other: switching the formatter off is not a licence to
        // stop reporting problems in that region.
        ImmutableArray<PragmaDirective> directives = Scan(
            "// #pragma warning disable format\nfunction f()\n{\n}\n// #pragma warning restore format\n");

        Assert.True(PragmaDirectives.IsFormatDisabled(directives, 2));
        Assert.False(PragmaDirectives.IsFormatDisabled(directives, 6));
        Assert.False(PragmaDirectives.IsSuppressed(directives, GscDiagnosticCode.BuiltinFunctionNotFound, 2));
    }

    [Fact]
    public void AnUnmatchedDisableRunsToTheEndOfTheFile()
    {
        // Read in source order rather than as nested scopes, so this needs no special case and
        // gives the answer anyone would expect.
        ImmutableArray<PragmaDirective> directives = Scan("// #pragma warning disable 5014\n");

        Assert.True(PragmaDirectives.IsSuppressed(directives, GscDiagnosticCode.BuiltinFunctionNotFound, 9999));
    }

    [Fact]
    public void SomethingThatIsNotAPragmaIsIgnored()
    {
        Assert.Empty(Scan("// pragma warning disable 5014\n// #pragma disable 5014\n// just a comment\n"));
    }
}
