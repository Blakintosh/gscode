using GSCode.Core.Diagnostics;
using GSCode.Server.Handlers;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;
using LspDiagnostic = OmniSharp.Extensions.LanguageServer.Protocol.Models.Diagnostic;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// The add-#using fix, driven the way the EDITOR drives it: through CodeActionHandler.Handle with a
/// real document store and navigation support, rather than by calling FindMissingUsings directly.
///
/// The direct tests on FindMissingUsings pass and always have, which is exactly why these exist —
/// the reported fault was that asking for the fix on a `gscode-5000` produced nothing, and a unit
/// test of the finder cannot see anything that goes wrong between the request and that call.
/// </summary>
public class NamespaceImportFixTests
{
    private const string AskingRelativePath = @"scripts\main.gsc";

    /// <summary>A workspace whose one file, scripts\util.gsc, declares util::helper.</summary>
    private static Task<HandlerWorkspace> WorkspaceWithUtilAsync(bool utilIsPrivate = false)
    {
        string modifier = utilIsPrivate ? "private " : "";
        return HandlerWorkspace.BuildAsync(
            [new TestFile(@"scripts\util.gsc", "#namespace util;\nfunction " + modifier + "helper()\n{\n}\n")]);
    }

    /// <summary>
    /// The code-action handler with <paramref name="askingSource"/> open as scripts\main.gsc. The
    /// asking file is opened, not indexed: the fix is for a buffer being written.
    /// </summary>
    private static CodeActionHandler HandlerOver(HandlerWorkspace workspace, string askingSource)
    {
        workspace.Open(AskingRelativePath, askingSource);
        DocumentLinter linter = new(
            workspace.Database, workspace.ResolverHolder, workspace.Builtins, workspace.ObjectFields);

        return new CodeActionHandler(workspace.Documents, workspace.Navigation, linter, HandlerWorkspace.Selector);
    }

    private static async Task<List<CodeAction>> ActionsAtAsync(CodeActionHandler handler, int line, int start, int end)
    {
        CodeActionParams request = new()
        {
            TextDocument = HandlerWorkspace.Identify(AskingRelativePath),
            Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(line, start, line, end),
            Context = new CodeActionContext
            {
                Diagnostics = new Container<LspDiagnostic>(new LspDiagnostic
                {
                    Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(line, start, line, end),
                    Code = new DiagnosticCode((int)GscDiagnosticCode.NamespaceNotImported),
                    Message = "reported",
                }),
            },
        };

        CommandOrCodeActionContainer? container = await handler.Handle(request, CancellationToken.None);

        List<CodeAction> fixes = [];
        foreach ( CommandOrCodeAction action in container ?? [] )
        {
            if ( action.IsCodeAction && action.CodeAction is not null )
            {
                fixes.Add(action.CodeAction);
            }
        }

        return fixes;
    }

    // --- "Quick Fix..." from the right-click menu: no reported diagnostics, just a cursor ---
    //
    // VS Code's context-menu "Quick Fix..." sends whatever the CURSOR overlaps, which can be an
    // empty Context.Diagnostics even though a diagnostic sits elsewhere on the same line — unlike
    // the lightbulb or hover Quick Fix, both anchored ON the marker. The reported symptom was
    // "right click -> fix doesn't show".

    // UsingAfterDeclaration (a parser-level diagnostic, always in ParseResult.AllDiagnostics — no
    // database or physical filesystem resolution needed) on line 2: `#using scripts\late;`.
    private const string UsingAfterDeclarationSource = "#using scripts\\a;\nfunction f(){}\n#using scripts\\late;\n";

    [Fact]
    public async Task EmptyContextDiagnostics_StillFindsAFixReportedElsewhereOnTheLine()
    {
        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync([]);
        CodeActionHandler handler = HandlerOver(workspace, UsingAfterDeclarationSource);

        CodeActionParams request = new()
        {
            TextDocument = HandlerWorkspace.Identify(AskingRelativePath),
            // A zero-width cursor at the very START of line 2, left of where the diagnostic itself
            // is reported (over the `#using scripts\late;` text starting at character 0 too, but a
            // real cursor position need not land inside a squiggle's exact bounds to be "on the
            // line") — with no diagnostics reported for it, the way a context-menu Quick Fix
            // arrives when the client decided the cursor was not on the marker.
            Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(2, 0, 2, 0),
            Context = new CodeActionContext { Diagnostics = new Container<LspDiagnostic>() },
        };

        CommandOrCodeActionContainer? container = await handler.Handle(request, CancellationToken.None);

        CodeAction fix = Assert.Single(
            [.. (container ?? []).Where(a => a.IsCodeAction).Select(a => a.CodeAction!)],
            f => f.Title!.Contains("Move", StringComparison.Ordinal));
        Assert.Equal((int)GscDiagnosticCode.UsingAfterDeclaration, Assert.Single(fix.Diagnostics!).Code!.Value.Long);
    }

    [Fact]
    public async Task EmptyContextDiagnostics_OffersNothingFromADifferentLine()
    {
        // The recomputed fallback is scoped to the requested LINE, not the whole file — otherwise
        // every Quick Fix request would offer every fix in the document regardless of where it was
        // asked.
        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync([]);
        CodeActionHandler handler = HandlerOver(workspace, UsingAfterDeclarationSource);

        CodeActionParams request = new()
        {
            TextDocument = HandlerWorkspace.Identify(AskingRelativePath),
            Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(1, 0, 1, 0),
            Context = new CodeActionContext { Diagnostics = new Container<LspDiagnostic>() },
        };

        CommandOrCodeActionContainer? container = await handler.Handle(request, CancellationToken.None);

        Assert.DoesNotContain(
            (container ?? []).Where(a => a.IsCodeAction).Select(a => a.CodeAction!),
            f => f.Title!.Contains("Move", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AskingOnTheDiagnostic_OffersTheImport()
    {
        // `util::helper()` — the name token sits at characters 10..16 on line 3, which is both the
        // reference's range and the range 5000 is reported over.
        using HandlerWorkspace workspace = await WorkspaceWithUtilAsync();
        CodeActionHandler handler = HandlerOver(workspace, "#namespace game;\nfunction run()\n{\n    util::helper();\n}\n");

        List<CodeAction> fixes = await ActionsAtAsync(handler, 3, 10, 16);

        CodeAction fix = Assert.Single(fixes, f => f.Title!.StartsWith("Add #using", StringComparison.Ordinal));
        Assert.Equal("Add #using scripts\\util", fix.Title);
    }

    [Fact]
    public async Task TheImportFixIsAttachedToTheDiagnosticItFixes()
    {
        // Without this the action is a general lightbulb entry rather than the fix FOR the error,
        // so it is absent from every flow keyed to a diagnostic and never marked preferred.
        using HandlerWorkspace workspace = await WorkspaceWithUtilAsync();
        CodeActionHandler handler = HandlerOver(workspace, "#namespace game;\nfunction run()\n{\n    util::helper();\n}\n");

        CodeAction fix = Assert.Single(
            await ActionsAtAsync(handler, 3, 10, 16), f => f.Title!.StartsWith("Add #using", StringComparison.Ordinal));

        LspDiagnostic attached = Assert.Single(fix.Diagnostics!);
        Assert.Equal((int)GscDiagnosticCode.NamespaceNotImported, attached.Code!.Value.Long);
        Assert.True(fix.IsPreferred);
    }

    [Fact]
    public async Task WithTwoFilesSupplyingTheName_NeitherIsPreferred()
    {
        // Auto Fix runs preferred actions without asking, so preferring one of two files would pick
        // an import for the user and not say so. Both are still bound to the diagnostic.
        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync(
        [
            new TestFile(@"scripts\util.gsc", "#namespace util;\nfunction helper()\n{\n}\n"),
            new TestFile(@"scripts\util_extra.gsc", "#namespace util;\nfunction helper()\n{\n}\n"),
        ]);
        CodeActionHandler handler = HandlerOver(workspace, "#namespace game;\nfunction run()\n{\n    util::helper();\n}\n");

        List<CodeAction> imports = [];
        foreach ( CodeAction fix in await ActionsAtAsync(handler, 3, 10, 16) )
        {
            if ( fix.Title!.StartsWith("Add #using", StringComparison.Ordinal) )
            {
                imports.Add(fix);
            }
        }

        Assert.Equal(2, imports.Count);
        Assert.All(imports, f => Assert.NotNull(f.Diagnostics));
        Assert.All(imports, f => Assert.False(f.IsPreferred));
    }

    [Fact]
    public async Task TheDuplicateImportFixIsAttachedToItsDiagnostic()
    {
        // 5018 is reported over the duplicate's PATH range, which sits inside the directive's own
        // range — so the action matches the diagnostic without either having to know about the
        // other's bounds.
        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync([]);
        CodeActionHandler handler = HandlerOver(workspace, "#using scripts\\util;\n#using scripts\\util;\n#namespace game;\nfunction run()\n{\n}\n");

        CodeActionParams request = new()
        {
            TextDocument = HandlerWorkspace.Identify(AskingRelativePath),
            Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(1, 0, 1, 20),
            Context = new CodeActionContext
            {
                Diagnostics = new Container<LspDiagnostic>(new LspDiagnostic
                {
                    Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(1, 7, 1, 19),
                    Code = new DiagnosticCode((int)GscDiagnosticCode.DuplicateImport),
                    Message = "reported",
                }),
            },
        };

        CommandOrCodeActionContainer? container = await handler.Handle(request, CancellationToken.None);
        CodeAction fix = Assert.Single(
            [.. (container ?? []).Where(a => a.IsCodeAction).Select(a => a.CodeAction!)],
            f => f.Title!.StartsWith("Remove duplicate", StringComparison.Ordinal));

        Assert.Equal(
            (int)GscDiagnosticCode.DuplicateImport, Assert.Single(fix.Diagnostics!).Code!.Value.Long);

        // The line is provably dead — the same file is imported above — so Auto Fix may take it.
        Assert.True(fix.IsPreferred);
    }

    [Fact]
    public async Task APrivateTargetIsNotOffered()
    {
        // Importing the file would not make the call legal, so there is no fix to offer here; 5003
        // is the diagnostic that has the right story for it.
        using HandlerWorkspace workspace = await WorkspaceWithUtilAsync(utilIsPrivate: true);
        CodeActionHandler handler = HandlerOver(workspace, "#namespace game;\nfunction run()\n{\n    util::helper();\n}\n");

        Assert.DoesNotContain(
            await ActionsAtAsync(handler, 3, 10, 16), f => f.Title!.StartsWith("Add #using", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AskingOnAMacroInvocation_OffersTheImportItsExpansionNeeds()
    {
        // The lint reports 5000 here, so the fix has to be offered here too — a diagnostic with no
        // fix behind it is worse than either half alone. Nothing on this line spells `util::`; the
        // #define does, and the expanded reference is keyed to the invocation.
        using HandlerWorkspace workspace = await WorkspaceWithUtilAsync();
        CodeActionHandler handler = HandlerOver(workspace, "#define HELP() util::helper()\n#namespace game;\nfunction run()\n{\n    HELP();\n}\n");

        List<CodeAction> fixes = await ActionsAtAsync(handler, 4, 4, 8);

        CodeAction fix = Assert.Single(fixes, f => f.Title!.StartsWith("Add #using", StringComparison.Ordinal));
        Assert.Equal("Add #using scripts\\util", fix.Title);
    }
}
