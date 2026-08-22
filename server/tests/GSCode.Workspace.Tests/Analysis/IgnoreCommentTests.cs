using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Extraction;
using GSCode.Parser.Preprocessing;
using GSCode.Workspace.Analysis;
using GSCode.Workspace.Api;
using GSCode.Workspace.Database;
using GSCode.Workspace.Resolution;
using GSCode.Workspace.Tests.Resolution;
using Xunit;

namespace GSCode.Workspace.Tests.Analysis;

/// <summary>
/// <c>// gscode ignore</c>, the suppression 1.5 shipped, kept as an alias of
/// <c>#pragma disable all</c> over one line.
///
/// It is a compatibility surface, so the tests pin 1.5's semantics rather than what would be
/// tidier: the comment suppresses the line BELOW it and nothing else, <c>gsc</c> is accepted for
/// <c>gscode</c>, and a block comment counts from the line it ENDS on.
/// </summary>
public class IgnoreCommentTests
{
    private const string Raw = @"C:\bo3\raw";
    private static string ApiDirectory => Path.Combine(AppContext.BaseDirectory, "Api");

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

    [Theory]
    [InlineData("// gscode ignore")]
    [InlineData("// gsc ignore")]
    [InlineData("//gscode ignore")]
    [InlineData("//\tgsc\tignore")]
    [InlineData("// gscode ignore - the engine build we use has this one")]
    [InlineData("/* gsc ignore */")]
    public void AnIgnoreCommentSuppressesTheLineBelowIt(string comment)
    {
        ImmutableArray<PragmaDirective> directives = Scan($"{comment}\nfoo();\n");

        Assert.True(PragmaDirectives.IsSuppressed(directives, GscDiagnosticCode.BuiltinFunctionNotFound, 1));
    }

    [Fact]
    public void ABlockCommentCountsFromTheLineItEndsOn()
    {
        // 1.5 keyed off the comment's END line, which is the only reading that puts the suppression
        // on the line the reader sees underneath it.
        ImmutableArray<PragmaDirective> directives = Scan("/* gscode ignore\n   see the changelog */\nfoo();\n");

        Assert.False(PragmaDirectives.IsSuppressed(directives, GscDiagnosticCode.BuiltinFunctionNotFound, 1));
        Assert.True(PragmaDirectives.IsSuppressed(directives, GscDiagnosticCode.BuiltinFunctionNotFound, 2));
    }

    [Fact]
    public void ItSuppressesItsOneLineAndNoOther()
    {
        // The whole difference from a pragma: this does not open a region. Its own line is not
        // covered either — the comment is written above the code it is about.
        ImmutableArray<PragmaDirective> directives = Scan("// gscode ignore\nfoo();\nbar();\n");

        Assert.False(PragmaDirectives.IsSuppressed(directives, GscDiagnosticCode.BuiltinFunctionNotFound, 0));
        Assert.True(PragmaDirectives.IsSuppressed(directives, GscDiagnosticCode.BuiltinFunctionNotFound, 1));
        Assert.False(PragmaDirectives.IsSuppressed(directives, GscDiagnosticCode.BuiltinFunctionNotFound, 2));
    }

    [Fact]
    public void ItSuppressesEveryCode()
    {
        // 1.5 had no way to name a code, so the alias can only mean "all of them".
        ImmutableArray<PragmaDirective> directives = Scan("// gscode ignore\nfoo();\n");

        Assert.True(PragmaDirectives.IsSuppressed(directives, GscDiagnosticCode.BuiltinFunctionNotFound, 1));
        Assert.True(PragmaDirectives.IsSuppressed(directives, GscDiagnosticCode.UnusedLocal, 1));
        Assert.True(PragmaDirectives.IsSuppressed(directives, GscDiagnosticCode.NamespaceNotImported, 1));
    }

    [Fact]
    public void ARestoreAboveItDoesNotUndoIt()
    {
        // It carries its own answer rather than reading the running disable/restore state, so it
        // still works inside a region a restore has switched back on.
        ImmutableArray<PragmaDirective> directives = Scan(
            "// #pragma disable all\n// #pragma restore all\n// gscode ignore\nfoo();\n");

        Assert.True(PragmaDirectives.IsSuppressed(directives, GscDiagnosticCode.BuiltinFunctionNotFound, 3));
    }

    [Fact]
    public void ItDoesNotDisturbTheDisableRestoreState()
    {
        // The other half of the same rule: it writes no state either, so the restore below still
        // switches diagnostics back on for the rest of the file.
        ImmutableArray<PragmaDirective> directives = Scan(
            "// #pragma disable 5014\n// gscode ignore\nfoo();\n// #pragma restore 5014\nbar();\n");

        Assert.True(PragmaDirectives.IsSuppressed(directives, GscDiagnosticCode.BuiltinFunctionNotFound, 2));
        Assert.False(PragmaDirectives.IsSuppressed(directives, GscDiagnosticCode.BuiltinFunctionNotFound, 4));
    }

    [Fact]
    public void ItDoesNotSwitchTheFormatterOff()
    {
        // Suppressing diagnostics is not a licence to stop formatting, the same separation the
        // `format` target keeps in the other direction.
        ImmutableArray<PragmaDirective> directives = Scan("// gscode ignore\nfoo();\n");

        Assert.False(PragmaDirectives.IsFormatDisabled(directives, 1));
    }

    [Theory]
    [InlineData("// this one we gscode ignore")]
    [InlineData("// gscode ignores the return value")]
    [InlineData("// gscodeignore")]
    [InlineData("// gscode")]
    [InlineData("// ignore")]
    public void ProseThatMerelyContainsTheWordsIsNotOne(string comment)
    {
        // Anchored at the comment's opener and boundaried at the end of `ignore`, as 1.5's pattern
        // was: an English sentence must not silently switch a file's diagnostics off.
        Assert.Empty(Scan($"{comment}\nfoo();\n"));
    }

    [Fact]
    public void AnIgnoredDiagnosticIsDroppedFromTheWholeAnalysis()
    {
        // End to end through the layer that applies suppression, which is what both the open-file
        // and the indexed path go through.
        const string Source = "function f()\n{\n    // gscode ignore\n    unused = 1;\n}\n";

        Assert.Contains(
            Analyze("function f()\n{\n    unused = 1;\n}\n"),
            d => d.Code == GscDiagnosticCode.UnusedLocal);
        Assert.DoesNotContain(Analyze(Source), d => d.Code == GscDiagnosticCode.UnusedLocal);
    }

    private static ImmutableArray<Diagnostic> Analyze(string source)
    {
        string path = @$"{Raw}\scripts\t.gsc";
        TestWorkspace.Built workspace = TestWorkspace.Build(GameProfile.Active, Raw, (path, source));

        ParseResult result = ScriptAnalysis.Analyze(
            path, ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable());

        return WorkspaceLints.Analyze(
            result,
            ScriptLanguage.Gsc,
            path,
            workspace.Database,
            workspace.Resolver,
            BuiltinApiSet.Load(ApiDirectory),
            ObjectFields.Load(ApiDirectory));
    }
}
