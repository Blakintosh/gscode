using System.Threading;
using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Server.Configuration;
using GSCode.Server.Handlers;
using GSCode.Workspace.Api;
using GSCode.Workspace.Database;
using GSCode.Workspace.Documents;
using GSCode.Workspace.Resolution;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// The macro parameter-name inlay family, driven through the handler the way the editor drives
/// it, because everything that can go wrong here lives between the request and the hint: the
/// setting has to be read, the invocation has to survive preprocessing, and the label has to land
/// on the argument rather than on the parenthesis before it.
/// </summary>
public class InlayHintMacroTests
{
    private const string Path = @"c:\bo3\share\raw\scripts\main.gsc";

    private const string Source =
        "#define IS_TRUE(__a) (isdefined(__a) && __a)\n"
        + "#define MAX_PLAYERS 18\n"
        + "\n"
        + "function s()\n"
        + "{\n"
        + "    if ( !IS_TRUE( level.friendlyContentOutlines ) )\n"
        + "    {\n"
        + "        return false;\n"
        + "    }\n"
        + "\n"
        + "    return MAX_PLAYERS;\n"
        + "}\n";

    private static InlayHintHandler BuildHandler(ServerSettings settings)
    {
        DocumentStore documents = new(static _ => NullInsertProvider.Instance, new NameTable());
        OpenDocument document = documents.Open(Path, Source, 1);
        documents.AnalyzeIfStale(document);

        NavigationSupport support = new(documents, new ScriptDatabase(), new ResolverHolder(new PhysicalFileSystem()));

        return new InlayHintHandler(
            support,
            new BuiltinApiSet(BuiltinApi.Empty, BuiltinApi.Empty),
            ObjectFields.Empty,
            settings,
            TextDocumentSelector.ForLanguage("gsc"));
    }

    private static async Task<List<InlayHint>> HintsAsync(ServerSettings settings)
    {
        InlayHintParams request = new()
        {
            TextDocument = new TextDocumentIdentifier { Uri = DocumentUri.FromFileSystemPath(Path) },
            Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(0, 0, 20, 0),
        };

        InlayHintContainer? container = await BuildHandler(settings).Handle(request, CancellationToken.None);

        return [.. container ?? []];
    }

    /// <summary>Only the macro family on, so nothing else can supply the hint under test.</summary>
    private static ServerSettings MacroOnly()
    {
        return new ServerSettings
        {
            InlayInferredTypes = false,
            InlayParameterNames = false,
            InlayMacroParameterNames = true,
        };
    }

    [Fact]
    public void TheFamilyIsOffByDefault()
    {
        Assert.False(new ServerSettings().InlayMacroParameterNames);
    }

    [Fact]
    public async Task NoMacroHintsWhenTheSettingIsOff()
    {
        ServerSettings settings = MacroOnly();
        settings.InlayMacroParameterNames = false;

        Assert.Empty(await HintsAsync(settings));
    }

    [Fact]
    public async Task TheArgumentIsLabelledWithTheMacrosParameterName()
    {
        // The reported shape: `IS_TRUE( level.friendlyContentOutlines )` should read `__a:` before
        // the argument. The call is gone by the time there is a tree, so this can only come from
        // the preprocessor's invocation list.
        InlayHint hint = Assert.Single(await HintsAsync(MacroOnly()));

        Assert.Equal("__a:", hint.Label.String);
        Assert.Equal(InlayHintKind.Parameter, hint.Kind);
        Assert.True(hint.PaddingRight);

        // Line 5, at `level` — not at the '(' and not at the space after it.
        Assert.Equal(5, hint.Position.Line);
        Assert.Equal(Source.Split('\n')[5].IndexOf("level", StringComparison.Ordinal), hint.Position.Character);
    }

    [Fact]
    public async Task AnObjectLikeMacroIsNotLabelled()
    {
        // `MAX_PLAYERS` is invoked too, and takes no arguments — the single hint above is already
        // the proof, pinned here as the thing that would regress if the parameter check went away.
        List<InlayHint> hints = await HintsAsync(MacroOnly());

        Assert.DoesNotContain(hints, hint => (hint.Label.String ?? "").Contains("MAX_PLAYERS", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnInvocationOutsideTheWindowIsNotHinted()
    {
        InlayHintParams request = new()
        {
            TextDocument = new TextDocumentIdentifier { Uri = DocumentUri.FromFileSystemPath(Path) },
            Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(8, 0, 11, 0),
        };

        InlayHintContainer? container = await BuildHandler(MacroOnly()).Handle(request, CancellationToken.None);

        Assert.Empty(container ?? []);
    }

    [Fact]
    public async Task TheCallSitePassStillSkipsTheExpansion()
    {
        // The macro body expands to `isdefined( … )`, whose tokens all report the INVOCATION's
        // range. With the call-site family on as well, that must still be skipped — otherwise the
        // expansion's own parameter names stack onto the one call site.
        List<InlayHint> hints = await HintsAsync(new ServerSettings
        {
            InlayInferredTypes = false,
            InlayParameterNames = true,
            InlayMacroParameterNames = true,
        });

        Assert.Single(hints);
        Assert.Equal("__a:", hints[0].Label.String);
    }
}
