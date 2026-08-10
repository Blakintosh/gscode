using System.Collections.Immutable;
using GSCode.Core.Diagnostics;
using GSCode.Core.Text;
using GSCode.Server.Mapping;
using Xunit;
using LspDiagnostic = OmniSharp.Extensions.LanguageServer.Protocol.Models.Diagnostic;
using LspDiagnosticTag = OmniSharp.Extensions.LanguageServer.Protocol.Models.DiagnosticTag;

namespace GSCode.Server.Tests.Mapping;

/// <summary>
/// Tags and related information are the two fields the editor needs for grey-out and
/// "see also" navigation, so their wire mapping is pinned here.
/// </summary>
public class DiagnosticMappingTests
{
    private static Diagnostic Plain()
    {
        return Diagnostic.Create(
            TextRange.FromCoordinates(1, 0, 1, 5),
            DiagnosticSeverity.Hint,
            GscDiagnosticCode.InactiveConditionalBranch);
    }

    [Fact]
    public void OrdinaryDiagnostic_OmitsTagsAndRelatedInformation()
    {
        // Null rather than empty containers, so the common case stays off the wire entirely.
        LspDiagnostic mapped = Plain().ToLsp();

        Assert.Null(mapped.Tags);
        Assert.Null(mapped.RelatedInformation);
    }

    [Fact]
    public void UnnecessaryTag_MapsToTheLspTag()
    {
        Diagnostic tagged = Plain() with { Tags = [DiagnosticTag.Unnecessary] };

        LspDiagnostic mapped = tagged.ToLsp();

        Assert.NotNull(mapped.Tags);
        Assert.Equal(LspDiagnosticTag.Unnecessary, Assert.Single(mapped.Tags!));
    }

    [Fact]
    public void RelatedInformation_CarriesPathAsUriAndKeepsMessage()
    {
        DiagnosticRelation relation = new(
            @"C:\bo3\share\raw\scripts\util.gsc", TextRange.FromCoordinates(3, 2, 3, 8), "First defined here.");
        Diagnostic related = Plain() with { RelatedInformation = [relation] };

        LspDiagnostic mapped = related.ToLsp();

        Assert.NotNull(mapped.RelatedInformation);
        OmniSharp.Extensions.LanguageServer.Protocol.Models.DiagnosticRelatedInformation single =
            Assert.Single(mapped.RelatedInformation!);

        Assert.Equal("First defined here.", single.Message);
        Assert.Equal(3, single.Location.Range.Start.Line);
        Assert.Contains("util.gsc", single.Location.Uri.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}
