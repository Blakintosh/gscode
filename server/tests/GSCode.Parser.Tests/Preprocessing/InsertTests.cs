using GSCode.Core.Diagnostics;
using GSCode.Parser.Lexing;
using GSCode.Parser.Preprocessing;
using Xunit;

namespace GSCode.Parser.Tests.Preprocessing;

public class InsertTests
{
    private const string GshPath = @"scripts\shared\shared.gsh";

    [Fact]
    public void Insert_SplicesTokens_WithGshProvenance()
    {
        FakeInsertProvider provider = new FakeInsertProvider()
            .AddInsert(GshPath, "#define FLAG 1\nvalue = 7;");

        PreprocessResult result = PreprocessTestHelper.Run($"#insert {GshPath};\nx = FLAG;", provider);

        // The gsh's plain code splices in with SourceFile set and its OWN gsh-local range.
        PToken spliced = Assert.Single(result.Tokens, token => token.Text == "value");
        Assert.Equal(GshPath.ToLowerInvariant(), spliced.Provenance.SourceFile);
        Assert.Equal(1, spliced.Range.Start.Line);
        Assert.NotNull(spliced.Provenance.RootSite);
        Assert.Equal(0, spliced.Provenance.RootSite!.Value.Start.Line);

        // The macro defined in the gsh expands in the root file.
        Assert.Contains(result.Tokens, token => token.Text == "1" && token.Kind == TokenKind.Integer);
    }

    [Fact]
    public void Insert_MacroFromGsh_DefinitionSitePointsIntoGsh()
    {
        FakeInsertProvider provider = new FakeInsertProvider()
            .AddInsert(GshPath, "#define VERSION 3");

        PreprocessResult result = PreprocessTestHelper.Run($"#insert {GshPath};\nx = VERSION;", provider);

        Assert.True(result.Macros.TryGet("VERSION", out MacroDefinition definition));
        Assert.Equal(GshPath.ToLowerInvariant(), definition.SourceFile);
        Assert.Equal(0, definition.NameRange.Start.Line);

        PToken expanded = Assert.Single(result.Tokens, token => token.Text == "3");
        Assert.Equal(GshPath.ToLowerInvariant(), expanded.Provenance.SourceFile);
    }

    [Fact]
    public void Insert_Edges_AreRecorded()
    {
        FakeInsertProvider provider = new FakeInsertProvider().AddInsert(GshPath, "// empty");

        PreprocessResult result = PreprocessTestHelper.Run($"#insert {GshPath};", provider);

        InsertEdge edge = Assert.Single(result.Inserts);
        Assert.Equal(GshPath, edge.RawPath);
        Assert.Equal(GshPath.ToLowerInvariant(), edge.ResolvedPath);
        Assert.Null(edge.ContainingFile);
    }

    [Fact]
    public void Insert_NotFound_DiagnosticAndEdgeWithNullResolved()
    {
        PreprocessResult result = PreprocessTestHelper.Run(@"#insert scripts\missing.gsh;");

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == GscDiagnosticCode.InsertNotFound);
        InsertEdge edge = Assert.Single(result.Inserts);
        Assert.Null(edge.ResolvedPath);
    }

    [Fact]
    public void Insert_MissingSemicolon_Diagnostic()
    {
        FakeInsertProvider provider = new FakeInsertProvider().AddInsert(GshPath, "a = 1;");

        PreprocessResult result = PreprocessTestHelper.Run($"#insert {GshPath}\nnext = 2;", provider);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == GscDiagnosticCode.InsertMissingSemicolon);
        // The insert still happens.
        Assert.Contains(result.Tokens, token => token.Text == "a");
    }

    [Theory]
    [InlineData(@"#insert \rooted\path.gsh;")]
    [InlineData(@"#insert c:\abs\path.gsh;")]
    [InlineData(@"#insert scripts\..\up.gsh;")]
    public void Insert_IllegalPath_Diagnostic(string source)
    {
        PreprocessResult result = PreprocessTestHelper.Run(source);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == GscDiagnosticCode.InvalidInsertPath);
    }

    [Fact]
    public void Insert_MissingPath_Diagnostic()
    {
        PreprocessResult result = PreprocessTestHelper.Run("#insert ;");

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == GscDiagnosticCode.MissingInsertPath);
    }

    [Fact]
    public void Insert_Nested_InnerGshResolves()
    {
        FakeInsertProvider provider = new FakeInsertProvider()
            .AddInsert(@"scripts\outer.gsh", @"#insert scripts\inner.gsh;")
            .AddInsert(@"scripts\inner.gsh", "#define DEEP 9");

        PreprocessResult result = PreprocessTestHelper.Run(@"#insert scripts\outer.gsh;
x = DEEP;", provider);

        Assert.Equal(2, result.Inserts.Length);
        Assert.Contains(result.Tokens, token => token.Text == "9");

        // The nested edge records which file contains it.
        InsertEdge nestedEdge = Assert.Single(result.Inserts, edge => edge.RawPath == @"scripts\inner.gsh");
        Assert.Equal(@"scripts\outer.gsh", nestedEdge.ContainingFile);
    }

    [Fact]
    public void Insert_Cycle_DiagnosticNoHang()
    {
        FakeInsertProvider provider = new FakeInsertProvider()
            .AddInsert(@"scripts\a.gsh", @"#insert scripts\b.gsh;")
            .AddInsert(@"scripts\b.gsh", @"#insert scripts\a.gsh;");

        PreprocessResult result = PreprocessTestHelper.Run(@"#insert scripts\a.gsh;", provider);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == GscDiagnosticCode.InsertCycle);
    }

    [Fact]
    public void Insert_DiagnosticInsideGsh_ReportsAtRootInsertSite()
    {
        // The gsh contains a broken #define; the diagnostic must anchor at the root's
        // #insert line (line 0), not inside the invisible gsh.
        FakeInsertProvider provider = new FakeInsertProvider()
            .AddInsert(GshPath, "#define\n");

        PreprocessResult result = PreprocessTestHelper.Run($"#insert {GshPath};", provider);

        Diagnostic diagnostic = Assert.Single(result.Diagnostics, d => d.Code == GscDiagnosticCode.ExpectedMacroName);
        Assert.Equal(0, diagnostic.Range.Start.Line);
    }
}
