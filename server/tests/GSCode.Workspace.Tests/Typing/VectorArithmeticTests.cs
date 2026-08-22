using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Workspace.Api;
using GSCode.Workspace.Typing;
using Xunit;

namespace GSCode.Workspace.Tests.Typing;

/// <summary>
/// Vector arithmetic through the whole pass, which is where the fix is actually visible.
///
/// `NumericResult` took no operator and knew only Int/Float/Unknown, so `vector * 0.5` typed as
/// float and `vector - vector` as unknown. That was a wrong hover and a wrong inlay hint on shipped
/// code — `self.velocity = self.origin * 0.5` is the shape it bit — and one of the two causes that
/// got PredefinedFieldTypeMismatch withdrawn after 46 unreal findings on Black Ops III.
/// </summary>
public class VectorArithmeticTests
{
    private static string ApiDirectory => Path.Combine(AppContext.BaseDirectory, "Api");

    private static ScrType TypeOfFirst(string body)
    {
        string source = "function f( a )\n{\n" + body + "\n}\n";

        ParseResult result = ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc", ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable());

        Assert.DoesNotContain(result.AllDiagnostics, d => (int)d.Code is >= 3000 and < 4000);

        FlowTyper typer = new(ApiLoader.Load(ApiDirectory, ScriptLanguage.Gsc), ObjectFields.Load(ApiDirectory));
        ImmutableArray<InferredAssignment> inferred = typer.InferAssignments(result);

        return inferred.Length == 0 ? ScrType.Unknown : inferred[0].Type;
    }

    [Theory]
    [InlineData("    v = ( 0, 0, 1 ) * 0.5;")]
    [InlineData("    v = ( 0, 0, 1 ) * 2;")]
    [InlineData("    v = 0.5 * ( 0, 0, 1 );")]
    [InlineData("    v = ( 0, 0, 1 ) / 2;")]
    public void AScaledVectorIsAVector(string body)
    {
        Assert.Equal(ScrType.Vector, TypeOfFirst(body));
    }

    [Theory]
    [InlineData("    v = ( 1, 0, 0 ) + ( 0, 1, 0 );")]
    [InlineData("    v = ( 1, 0, 0 ) - ( 0, 1, 0 );")]
    public void VectorsAddAndSubtractToAVector(string body)
    {
        Assert.Equal(ScrType.Vector, TypeOfFirst(body));
    }

    [Fact]
    public void TheShippedShapeThatMotivatedThisTypesCorrectly()
    {
        // scripts\mp\killstreaks\_supplydrop.gsc and others write this. It typed as float before.
        Assert.Equal(ScrType.Vector, TypeOfFirst("    origin = ( 0, 0, 1 );\n    v = origin * 0.5;"));
    }

    [Fact]
    public void ScalarArithmeticIsUnaffected()
    {
        // The control: fixing vectors must not disturb the ordinary numeric path.
        Assert.Equal(ScrType.Int, TypeOfFirst("    n = 1 + 2;"));
        Assert.Equal(ScrType.Float, TypeOfFirst("    n = 1 + 2.0;"));
        Assert.Equal(ScrType.Float, TypeOfFirst("    n = 3 / 2;"));
    }

    [Fact]
    public void NegatingAVectorIsAVector()
    {
        Assert.Equal(ScrType.Vector, TypeOfFirst("    v = -( 1, 2, 3 );"));
    }
}
