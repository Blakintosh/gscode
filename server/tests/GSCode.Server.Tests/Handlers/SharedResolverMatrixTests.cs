using GSCode.Core;
using GSCode.Server.Configuration;
using GSCode.Server.Handlers;
using GSCode.Workspace.Completion;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// Every request that turns a call into a declaration goes through the shared resolvers
/// (<c>DatabaseQueries.FunctionsTheCallReaches</c> and what builds on it), and each one is asked the
/// same questions here, so a change that fixes one request cannot leave another behind. That is how
/// go-to-definition stopped finding a path call into a file nothing includes while hover still did.
///
/// On an include dialect (cod4) a function is keyed by the file that declares it: a bare call sees
/// the file's own functions and its DIRECT includes' own, a path call sees the named file's own.
/// Four calls, with a same-stem decoy beside every target:
/// <list type="bullet">
/// <item>a bare call into a direct include — reaches it;</item>
/// <item>a bare call to a function only a nested include declares — reaches nothing;</item>
/// <item>a path call into a file nothing includes, from a file declaring its own function of the
/// same name — reaches the named file's;</item>
/// <item>a path call to a function the named file only includes — reaches nothing.</item>
/// </list>
/// On a namespace dialect (BO3) <c>util::f</c> has an mp, a zm and a cp copy, and the mp script that
/// <c>#using</c>s the mp copy means that one.
///
/// Each answer is reduced to a TAG naming the declaration it describes — by the declaring file for a
/// location, by the parameter name for text — so every request is held to the same expectation.
/// </summary>
public sealed class SharedResolverMatrixTests
{
    // ---- cod4 ----------------------------------------------------------------------------------

    private const string CallerPath = @"maps\caller.gsc";

    private const string CallerSource =
        "#include maps\\_direct;\n"                    // 0
        + "\n"                                         // 1
        + "main()\n"                                   // 2
        + "{\n"                                        // 3
        + "\tdirect_fn( 1 );\n"                        // 4  name 1, inside 12
        + "\tnested_fn( 1 );\n"                        // 5  name 1, inside 12
        + "\tmaps\\_pathed::pathed_fn( 1 );\n"         // 6  name 15, inside 26
        + "\tmaps\\_pathed::inner_fn( 1 );\n"          // 7  name 15, inside 25
        + "\tspawn( \"script_origin\" );\n"            // 8  name 1
        + "}\n"                                        // 9
        + "\n"                                         // 10
        + "pathed_fn( own_p )\n"                       // 11 this file's own pathed_fn
        + "{\n"
        + "}\n";

    private const int OwnDeclarationLine = 11;

    private static readonly TestFile[] s_cod4 =
    [
        new TestFile(@"maps\_direct.gsc", "#include maps\\_nested;\n\ndirect_fn( direct_p )\n{\n}\n"),
        new TestFile(@"maps\_nested.gsc", "nested_fn( nested_p )\n{\n}\n\nspawn( nested_spawn )\n{\n}\n"),
        new TestFile(@"maps\_pathed.gsc", "#include maps\\_pathed_inc;\n\npathed_fn( pathed_p )\n{\n}\n"),
        new TestFile(@"maps\_pathed_inc.gsc", "inner_fn( inner_p )\n{\n}\n"),
        new TestFile(@"maps\other\_direct.gsc", "direct_fn( decoy_p )\n{\n}\n"),
        new TestFile(@"maps\mp\_pathed.gsc", "pathed_fn( decoy_p )\n{\n}\n"),
        new TestFile(@"maps\other\decoys.gsc", "nested_fn( decoy_p )\n{\n}\n\ninner_fn( decoy_p )\n{\n}\n"),
        new TestFile(CallerPath, CallerSource),
    ];

    private static readonly Dictionary<string, string> s_cod4Files = new(StringComparer.OrdinalIgnoreCase)
    {
        [@"maps\_direct.gsc"] = "direct",
        [@"maps\_nested.gsc"] = "nested",
        [@"maps\_pathed.gsc"] = "pathed",
        [@"maps\_pathed_inc.gsc"] = "inner",
        [@"maps\other\_direct.gsc"] = "decoy",
        [@"maps\mp\_pathed.gsc"] = "decoy",
        [@"maps\other\decoys.gsc"] = "decoy",
    };

    private static readonly string[] s_cod4Parameters = ["direct", "nested", "pathed", "inner", "own", "decoy"];

    /// <summary>One call in the caller: where its name is, just inside its parentheses, and what it reaches.</summary>
    private sealed record Call(int Line, int Name, int Inside, string Reaches);

    private static readonly Dictionary<string, Call> s_calls = new()
    {
        ["bare call into a direct include"] = new Call(4, 1, 12, "direct"),
        ["bare call into a nested include"] = new Call(5, 1, 12, ""),
        ["path call into a file nothing includes"] = new Call(6, 15, 26, "pathed"),
        ["path call to what the named file includes"] = new Call(7, 15, 25, ""),
    };

    public static TheoryData<string> Calls()
    {
        return [.. s_calls.Keys];
    }

    private static async Task<HandlerWorkspace> Cod4Async()
    {
        HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync(s_cod4, GameProfile.Cod4);
        workspace.Open(CallerPath);
        return workspace;
    }

    private static TextDocumentIdentifier Caller()
    {
        return HandlerWorkspace.Identify(CallerPath);
    }

    /// <summary>The tag of each location: its file's, or "own" for the caller's own declaration.</summary>
    private static string TagLocations(IEnumerable<(string Path, int Line)> locations, Dictionary<string, string> files)
    {
        SortedSet<string> tags = new(StringComparer.Ordinal);
        foreach ( (string path, int line) in locations )
        {
            string relative = Path.GetRelativePath(TestPaths.RawRoot, path);
            if ( files.TryGetValue(relative, out string? tag) )
            {
                tags.Add(tag);
            }
            else if ( relative.Equals(CallerPath, StringComparison.OrdinalIgnoreCase) && line == OwnDeclarationLine )
            {
                tags.Add("own");
            }
        }

        return string.Join(",", tags);
    }

    /// <summary>The tags of the parameters a text names.</summary>
    private static string TagText(string? text, string[] parameters)
    {
        SortedSet<string> tags = new(StringComparer.Ordinal);
        foreach ( string parameter in parameters )
        {
            if ( text is not null && text.Contains(parameter + "_", StringComparison.Ordinal) )
            {
                tags.Add(parameter);
            }
        }

        return string.Join(",", tags);
    }

    private static IEnumerable<(string Path, int Line)> Of(LocationOrLocationLinks? found)
    {
        if ( found is null )
        {
            return [];
        }

        return found.Where(static link => link.Location is not null)
            .Select(static link => (link.Location!.Uri.GetFileSystemPath(), link.Location.Range.Start.Line));
    }

    [Theory]
    [MemberData(nameof(Calls))]
    public async Task Hover(string call)
    {
        Call at = s_calls[call];
        using HandlerWorkspace workspace = await Cod4Async();

        Hover? hover = await new HoverHandler(workspace.Navigation, workspace.Builtins, workspace.ObjectFields, HandlerWorkspace.Selector)
            .Handle(new HoverParams { TextDocument = Caller(), Position = new Position(at.Line, at.Name) }, CancellationToken.None);

        Assert.Equal(at.Reaches, TagText(hover?.Contents.MarkupContent?.Value, s_cod4Parameters));
    }

    [Theory]
    [MemberData(nameof(Calls))]
    public async Task Definition(string call)
    {
        Call at = s_calls[call];
        using HandlerWorkspace workspace = await Cod4Async();

        LocationOrLocationLinks? found = await new DefinitionHandler(workspace.Navigation, HandlerWorkspace.Selector)
            .Handle(new DefinitionParams { TextDocument = Caller(), Position = new Position(at.Line, at.Name) }, CancellationToken.None);

        Assert.Equal(at.Reaches, TagLocations(Of(found), s_cod4Files));
    }

    [Theory]
    [MemberData(nameof(Calls))]
    public async Task Implementation_OnATopLevelCall_AnswersNothing(string call)
    {
        // By design: a top-level function has no overrides, and go-to-definition is bound beside
        // this. Implementation reaches the resolvers through a field holding a callback, below.
        Call at = s_calls[call];
        using HandlerWorkspace workspace = await Cod4Async();

        LocationOrLocationLinks? found = await new ImplementationHandler(workspace.Navigation, HandlerWorkspace.Selector)
            .Handle(new ImplementationParams { TextDocument = Caller(), Position = new Position(at.Line, at.Name) }, CancellationToken.None);

        Assert.Equal("", TagLocations(Of(found), s_cod4Files));
    }

    // A store file binding three callbacks: one the file's include reaches, one by a path into a file
    // nothing includes, one only a nested include declares. "\tlevel.cb = ..." — the field at column 7.
    private const string StorePath = @"maps\_store.gsc";

    private const string StoreSource =
        "#include maps\\_direct;\n"                     // 0
        + "\n"                                          // 1
        + "init()\n"                                    // 2
        + "{\n"                                         // 3
        + "\tlevel.cb_direct = ::direct_fn;\n"          // 4
        + "\tlevel.cb_pathed = maps\\_pathed::pathed_fn;\n" // 5
        + "\tlevel.cb_nested = ::nested_fn;\n"          // 6
        + "}\n";

    public static TheoryData<int, string> Fields()
    {
        return new TheoryData<int, string> { { 4, "direct" }, { 5, "pathed" }, { 6, "" } };
    }

    private static async Task<HandlerWorkspace> Cod4WithStoreAsync()
    {
        HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync([.. s_cod4, new TestFile(StorePath, StoreSource)], GameProfile.Cod4);
        workspace.Open(StorePath);
        return workspace;
    }

    [Theory]
    [MemberData(nameof(Fields))]
    public async Task Implementation_OnACallbackField(int line, string reaches)
    {
        using HandlerWorkspace workspace = await Cod4WithStoreAsync();

        LocationOrLocationLinks? found = await new ImplementationHandler(workspace.Navigation, HandlerWorkspace.Selector).Handle(
            new ImplementationParams { TextDocument = HandlerWorkspace.Identify(StorePath), Position = new Position(line, 7) },
            CancellationToken.None);

        Assert.Equal(reaches, TagLocations(Of(found), s_cod4Files));
    }

    [Theory]
    [MemberData(nameof(Fields))]
    public async Task TypeDefinition_OnACallbackField(int line, string reaches)
    {
        using HandlerWorkspace workspace = await Cod4WithStoreAsync();

        LocationOrLocationLinks? found = await new TypeDefinitionHandler(
                workspace.Navigation, workspace.Builtins, workspace.ObjectFields, HandlerWorkspace.Selector)
            .Handle(new TypeDefinitionParams { TextDocument = HandlerWorkspace.Identify(StorePath), Position = new Position(line, 7) },
                CancellationToken.None);

        Assert.Equal(reaches, TagLocations(Of(found), s_cod4Files));
    }

    [Theory]
    [MemberData(nameof(Fields))]
    public async Task CallHierarchyPrepare_OnACallbackField(int line, string reaches)
    {
        using HandlerWorkspace workspace = await Cod4WithStoreAsync();

        Container<CallHierarchyItem>? items = await new CallHierarchyHandler(workspace.Navigation, HandlerWorkspace.Selector).Handle(
            new CallHierarchyPrepareParams { TextDocument = HandlerWorkspace.Identify(StorePath), Position = new Position(line, 7) },
            CancellationToken.None);

        Assert.Equal(reaches, TagLocations(
            (items ?? []).Select(static item => (item.Uri.GetFileSystemPath(), item.SelectionRange.Start.Line)), s_cod4Files));
    }

    [Theory]
    [MemberData(nameof(Calls))]
    public async Task SignatureHelp(string call)
    {
        Call at = s_calls[call];
        using HandlerWorkspace workspace = await Cod4Async();

        SignatureHelp? help = await new SignatureHelpHandler(
                workspace.Navigation, new SignatureEngine(workspace.Database, workspace.Builtins), HandlerWorkspace.Selector)
            .Handle(new SignatureHelpParams { TextDocument = Caller(), Position = new Position(at.Line, at.Inside) }, CancellationToken.None);

        Assert.Equal(at.Reaches, TagText(help?.Signatures.FirstOrDefault()?.Label, s_cod4Parameters));
    }

    [Theory]
    [MemberData(nameof(Calls))]
    public async Task InlayHints(string call)
    {
        Call at = s_calls[call];
        using HandlerWorkspace workspace = await Cod4Async();
        ServerSettings settings = new() { InlayParameterNames = true, InlayInferredTypes = false, InlayMacroParameterNames = false };

        InlayHintContainer? hints = await new InlayHintHandler(
                workspace.Navigation, workspace.Builtins, workspace.ObjectFields, settings, HandlerWorkspace.Selector)
            .Handle(new InlayHintParams { TextDocument = Caller(), Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(0, 0, 14, 0) },
                CancellationToken.None);

        string labels = string.Join(" ", (hints ?? []).Where(hint => hint.Position.Line == at.Line).Select(static hint => hint.Label.ToString()));
        Assert.Equal(at.Reaches, TagText(labels, s_cod4Parameters));
    }

    [Theory]
    [MemberData(nameof(Calls))]
    public async Task References(string call)
    {
        Call at = s_calls[call];
        using HandlerWorkspace workspace = await Cod4Async();

        LocationContainer? found = await new ReferencesHandler(workspace.Navigation, HandlerWorkspace.Selector).Handle(
            new ReferenceParams
            {
                TextDocument = Caller(),
                Position = new Position(at.Line, at.Name),
                Context = new ReferenceContext { IncludeDeclaration = true },
            },
            CancellationToken.None);

        Assert.Equal(at.Reaches, TagLocations(
            (found ?? []).Select(static location => (location.Uri.GetFileSystemPath(), location.Range.Start.Line)), s_cod4Files));
    }

    [Theory]
    [MemberData(nameof(Calls))]
    public async Task Rename(string call)
    {
        Call at = s_calls[call];
        using HandlerWorkspace workspace = await Cod4Async();

        WorkspaceEdit? edit = await new RenameHandler(workspace.Navigation, workspace.Builtins, workspace.ObjectFields, HandlerWorkspace.Selector)
            .Handle(new RenameParams { TextDocument = Caller(), Position = new Position(at.Line, at.Name), NewName = "renamed_fn" },
                CancellationToken.None);

        IEnumerable<(string Path, int Line)> edited = edit?.Changes is null
            ? []
            : edit.Changes.SelectMany(static change => change.Value.Select(textEdit => (change.Key.GetFileSystemPath(), textEdit.Range.Start.Line)));
        Assert.Equal(at.Reaches, TagLocations(edited, s_cod4Files));
    }

    [Theory]
    [MemberData(nameof(Calls))]
    public async Task Highlight(string call)
    {
        // In-file only, so what it must get right is not claiming this file's own pathed_fn for the
        // path call that means _pathed's.
        Call at = s_calls[call];
        using HandlerWorkspace workspace = await Cod4Async();

        DocumentHighlightContainer? found = await new DocumentHighlightHandler(workspace.Navigation, HandlerWorkspace.Selector)
            .Handle(new DocumentHighlightParams { TextDocument = Caller(), Position = new Position(at.Line, at.Name) }, CancellationToken.None);

        List<int> lines = [.. (found ?? []).Select(static highlight => highlight.Range.Start.Line)];
        Assert.Contains(at.Line, lines);
        Assert.DoesNotContain(OwnDeclarationLine, lines);
    }

    [Theory]
    [MemberData(nameof(Calls))]
    public async Task CallHierarchyPrepare(string call)
    {
        Call at = s_calls[call];
        using HandlerWorkspace workspace = await Cod4Async();

        Container<CallHierarchyItem>? items = await new CallHierarchyHandler(workspace.Navigation, HandlerWorkspace.Selector)
            .Handle(new CallHierarchyPrepareParams { TextDocument = Caller(), Position = new Position(at.Line, at.Name) }, CancellationToken.None);

        Assert.Equal(at.Reaches, TagLocations(
            (items ?? []).Select(static item => (item.Uri.GetFileSystemPath(), item.SelectionRange.Start.Line)), s_cod4Files));
    }

    [Fact]
    public async Task CallHierarchyOutgoing_ListsWhatEachCallReaches()
    {
        using HandlerWorkspace workspace = await Cod4Async();
        CallHierarchyHandler handler = new(workspace.Navigation, HandlerWorkspace.Selector);

        Container<CallHierarchyItem>? main = await handler.Handle(
            new CallHierarchyPrepareParams { TextDocument = Caller(), Position = new Position(2, 0) }, CancellationToken.None);
        Container<CallHierarchyOutgoingCall>? outgoing = await handler.Handle(
            new CallHierarchyOutgoingCallsParams { Item = Assert.Single(main!) }, CancellationToken.None);

        List<string> callees = [.. (outgoing ?? []).Select(static call => call.To.Name + ":" + TagLocations(
            [(call.To.Uri.GetFileSystemPath(), call.To.SelectionRange.Start.Line)], s_cod4Files))];
        Assert.Contains("direct_fn:direct", callees);
        Assert.Contains("pathed_fn:pathed", callees);
        Assert.DoesNotContain(callees, static callee => callee.StartsWith("nested_fn", StringComparison.Ordinal)
            || callee.StartsWith("inner_fn", StringComparison.Ordinal)
            || callee.EndsWith(":decoy", StringComparison.Ordinal)
            || callee.EndsWith(":own", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(CallerPath, OwnDeclarationLine, "0 references")]
    [InlineData(@"maps\_pathed.gsc", 2, "1 reference")]
    [InlineData(@"maps\_direct.gsc", 2, "1 reference")]
    [InlineData(@"maps\other\_direct.gsc", 0, "0 references")]
    public async Task CodeLens_CountsOnlyTheCallsThatReachTheDeclaration(string file, int line, string title)
    {
        // The caller's own pathed_fn is not what its path call means, and the decoy direct_fn is not
        // what its bare call reaches.
        using HandlerWorkspace workspace = await Cod4Async();
        workspace.Open(file);
        CodeLensHandler handler = new(
            workspace.Navigation, new ServerSettings { CodeLensEnabled = true }, HandlerWorkspace.Selector, new LensCountCache());

        CodeLensContainer? lenses = await handler.HandleResolvedAsync(
            new CodeLensParams { TextDocument = HandlerWorkspace.Identify(file) }, CancellationToken.None);

        CodeLens lens = Assert.Single(lenses!, candidate => candidate.Range.Start.Line == line);
        Assert.StartsWith(title, lens.Command!.Title, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("direct_fn", "_direct", "", "direct")]
    [InlineData("nested_fn", "_nested", "", "")]
    [InlineData("nested_fn", "_nested", @"maps\_nested", "nested")]
    public async Task CompletionResolve(string name, string ns, string importPath, string reaches)
    {
        // A row's docs are the declaration the name reaches from here; a row offering the #include
        // describes the file it would include.
        using HandlerWorkspace workspace = await Cod4Async();
        CompletionHandler handler = new(
            workspace.Navigation, workspace.Completion, new ServerSettings(), HandlerWorkspace.Selector, workspace.Builtins);
        CompletionItem row = new()
        {
            Label = name,
            Data = CompletionHandler.ResolveData(
                new CompletionEntry(name, CompletionKind.Function, ns, name + "($0)", Namespace: ns, ImportPath: importPath),
                DocumentUri.FromFileSystemPath(TestPaths.Raw(CallerPath))),
        };

        CompletionItem resolved = await handler.Handle(row, CancellationToken.None);

        Assert.Equal(reaches, TagText(resolved.Documentation?.MarkupContent?.Value, s_cod4Parameters));
    }

    [Fact]
    public async Task BuiltinAt_IsNotHiddenByAScriptFunctionTheCallCannotReach()
    {
        // _nested declares its own spawn(), one include too deep for the caller.
        using HandlerWorkspace workspace = await Cod4Async();

        BuiltinAtResponse response = await new BuiltinAtHandler(workspace.Navigation, workspace.Builtins).Handle(
            new BuiltinAtParams { Uri = Caller().Uri.ToString(), Line = 8, Character = 1 }, CancellationToken.None);

        Assert.Equal("spawn", response.Name, ignoreCase: true);
    }

    // ---- BO3 -----------------------------------------------------------------------------------

    private const string Bo3CallerPath = @"scripts\mp\caller.gsc";

    private const string Bo3CallerSource =
        "#using scripts\\mp\\gametypes\\_util;\n"     // 0
        + "\n"                                        // 1
        + "#namespace caller;\n"                      // 2
        + "\n"                                        // 3
        + "function main()\n"                         // 4
        + "{\n"                                       // 5
        + "\tutil::f( 1 );\n"                         // 6  name 7, inside 10
        + "\tg = &util::f;\n"                         // 7
        + "\t[[ g ]]( 1 );\n"                         // 8  g at 4
        + "}\n";

    private static readonly TestFile[] s_bo3 =
    [
        new TestFile(@"scripts\zm\gametypes\_util.gsc", "#namespace util;\nfunction f( zm_p, zm_q )\n{\n}\n"),
        new TestFile(@"scripts\cp\gametypes\_util.gsc", "#namespace util;\nfunction f( cp_p, cp_q, cp_r )\n{\n}\n"),
        new TestFile(@"scripts\mp\gametypes\_util.gsc", "#namespace util;\nfunction f( mp_p )\n{\n}\n"),
        new TestFile(Bo3CallerPath, Bo3CallerSource),
    ];

    private static readonly Dictionary<string, string> s_bo3Files = new(StringComparer.OrdinalIgnoreCase)
    {
        [@"scripts\mp\gametypes\_util.gsc"] = "mp",
        [@"scripts\zm\gametypes\_util.gsc"] = "zm",
        [@"scripts\cp\gametypes\_util.gsc"] = "cp",
    };

    private static readonly string[] s_bo3Parameters = ["mp", "zm", "cp"];

    private static async Task<HandlerWorkspace> Bo3Async()
    {
        HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync(s_bo3);
        workspace.Open(Bo3CallerPath);
        return workspace;
    }

    [Fact]
    public async Task Bo3_EveryRequestDescribesTheCopyTheFileUses()
    {
        using HandlerWorkspace workspace = await Bo3Async();
        TextDocumentIdentifier id = HandlerWorkspace.Identify(Bo3CallerPath);
        Position call = new(6, 7);

        Hover? hover = await new HoverHandler(workspace.Navigation, workspace.Builtins, workspace.ObjectFields, HandlerWorkspace.Selector)
            .Handle(new HoverParams { TextDocument = id, Position = call }, CancellationToken.None);
        Assert.Equal("mp", TagText(hover?.Contents.MarkupContent?.Value, s_bo3Parameters));

        LocationOrLocationLinks? definition = await new DefinitionHandler(workspace.Navigation, HandlerWorkspace.Selector)
            .Handle(new DefinitionParams { TextDocument = id, Position = call }, CancellationToken.None);
        Assert.Equal("mp", TagLocations(Of(definition), s_bo3Files));


        SignatureHelp? help = await new SignatureHelpHandler(
                workspace.Navigation, new SignatureEngine(workspace.Database, workspace.Builtins), HandlerWorkspace.Selector)
            .Handle(new SignatureHelpParams { TextDocument = id, Position = new Position(6, 10) }, CancellationToken.None);
        Assert.Equal("mp", TagText(help?.Signatures.FirstOrDefault()?.Label, s_bo3Parameters));

        ServerSettings settings = new() { InlayParameterNames = true, InlayInferredTypes = false, InlayMacroParameterNames = false };
        InlayHintContainer? hints = await new InlayHintHandler(
                workspace.Navigation, workspace.Builtins, workspace.ObjectFields, settings, HandlerWorkspace.Selector)
            .Handle(new InlayHintParams { TextDocument = id, Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(6, 0, 7, 0) },
                CancellationToken.None);
        Assert.Equal("mp", TagText(string.Join(" ", (hints ?? []).Select(static hint => hint.Label.ToString())), s_bo3Parameters));

        LocationOrLocationLinks? typeDefinition = await new TypeDefinitionHandler(
                workspace.Navigation, workspace.Builtins, workspace.ObjectFields, HandlerWorkspace.Selector)
            .Handle(new TypeDefinitionParams { TextDocument = id, Position = new Position(8, 4) }, CancellationToken.None);
        Assert.Equal("mp", TagLocations(Of(typeDefinition), s_bo3Files));

        Container<CallHierarchyItem>? items = await new CallHierarchyHandler(workspace.Navigation, HandlerWorkspace.Selector)
            .Handle(new CallHierarchyPrepareParams { TextDocument = id, Position = call }, CancellationToken.None);
        Assert.Equal("mp", TagLocations((items ?? []).Select(static item => (item.Uri.GetFileSystemPath(), item.SelectionRange.Start.Line)), s_bo3Files));

        LocationContainer? references = await new ReferencesHandler(workspace.Navigation, HandlerWorkspace.Selector).Handle(
            new ReferenceParams { TextDocument = id, Position = call, Context = new ReferenceContext { IncludeDeclaration = true } },
            CancellationToken.None);
        Assert.Equal("mp", TagLocations((references ?? []).Select(static location => (location.Uri.GetFileSystemPath(), location.Range.Start.Line)), s_bo3Files));

        CompletionHandler completion = new(
            workspace.Navigation, workspace.Completion, new ServerSettings(), HandlerWorkspace.Selector, workspace.Builtins);
        CompletionItem resolved = await completion.Handle(
            new CompletionItem
            {
                Label = "f",
                Data = CompletionHandler.ResolveData(
                    new CompletionEntry("f", CompletionKind.Function, "util", "util::f($0)", Namespace: "util"), id.Uri),
            },
            CancellationToken.None);
        Assert.Equal("mp", TagText(resolved.Documentation?.MarkupContent?.Value, s_bo3Parameters));
    }

    [Fact]
    public async Task Bo3_ACallbackField_ResolvesToTheCopyTheStoringFileUses()
    {
        // "\tlevel.cb = &util::f;" — the field at column 7.
        TestFile store = new(@"scripts\mp\store.gsc",
            "#using scripts\\mp\\gametypes\\_util;\n\n#namespace store;\n\nfunction init()\n{\n\tlevel.cb = &util::f;\n}\n");
        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync([.. s_bo3, store]);
        workspace.Open(store.RelativePath);
        TextDocumentIdentifier id = HandlerWorkspace.Identify(store.RelativePath);
        Position field = new(6, 7);

        LocationOrLocationLinks? implementation = await new ImplementationHandler(workspace.Navigation, HandlerWorkspace.Selector)
            .Handle(new ImplementationParams { TextDocument = id, Position = field }, CancellationToken.None);
        Assert.Equal("mp", TagLocations(Of(implementation), s_bo3Files));

        LocationOrLocationLinks? typeDefinition = await new TypeDefinitionHandler(
                workspace.Navigation, workspace.Builtins, workspace.ObjectFields, HandlerWorkspace.Selector)
            .Handle(new TypeDefinitionParams { TextDocument = id, Position = field }, CancellationToken.None);
        Assert.Equal("mp", TagLocations(Of(typeDefinition), s_bo3Files));
    }
}
