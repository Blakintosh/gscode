using System.Collections.Generic;
using System.Linq;
using System.Threading;
using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Server.Configuration;
using GSCode.Server.Handlers;
using GSCode.Workspace.Database;
using GSCode.Workspace.Documents;
using GSCode.Workspace.Resolution;
using OmniSharp.Extensions.LanguageServer.Protocol;
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
    private const string AskingPath = @"C:\bo3\share\raw\scripts\main.gsc";
    private const string UtilPath = @"C:\bo3\share\raw\scripts\util.gsc";

    private static ParseResult AnalyzeAt(string source, string path)
    {
        return ScriptAnalysis.Analyze(
            path, ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable());
    }

    private static CodeActionHandler BuildHandler(string askingSource, out DocumentStore documents, bool utilIsPrivate = false)
    {
        ScriptDatabase database = new();
        string modifier = utilIsPrivate ? "private " : "";
        ParseResult util = AnalyzeAt("#namespace util;\nfunction " + modifier + "helper()\n{\n}\n", UtilPath);
        database.Commit(util, ResolutionContext.RawContext, false, "scripts\\util.gsc");

        return BuildHandlerWith(database, askingSource, out documents);
    }

    private static CodeActionHandler BuildHandlerWith(ScriptDatabase database, string askingSource)
    {
        return BuildHandlerWith(database, askingSource, out DocumentStore _);
    }

    private static CodeActionHandler BuildHandlerWith(
        ScriptDatabase database, string askingSource, out DocumentStore documents)
    {
        documents = new DocumentStore(static _ => NullInsertProvider.Instance, new NameTable());
        OpenDocument document = documents.Open(AskingPath, askingSource, 1);
        documents.AnalyzeIfStale(document);

        ResolverHolder holder = new(new PhysicalFileSystem());
        NavigationSupport support = new(documents, database, holder);

        return new CodeActionHandler(documents, support, TextDocumentSelector.ForLanguage("gsc"));
    }

    private static async Task<List<CodeAction>> ActionsAtAsync(CodeActionHandler handler, int line, int start, int end)
    {
        CodeActionParams request = new()
        {
            TextDocument = new TextDocumentIdentifier { Uri = DocumentUri.FromFileSystemPath(AskingPath) },
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

    [Fact]
    public async Task AskingOnTheDiagnostic_OffersTheImport()
    {
        // `util::helper()` — the name token sits at characters 10..16 on line 3, which is both the
        // reference's range and the range 5000 is reported over.
        CodeActionHandler handler = BuildHandler(
            "#namespace game;\nfunction run()\n{\n    util::helper();\n}\n", out DocumentStore _);

        List<CodeAction> fixes = await ActionsAtAsync(handler, 3, 10, 16);

        CodeAction fix = Assert.Single(fixes, f => f.Title!.StartsWith("Add #using", StringComparison.Ordinal));
        Assert.Equal("Add #using scripts\\util", fix.Title);
    }

    [Fact]
    public async Task TheImportFixIsAttachedToTheDiagnosticItFixes()
    {
        // Without this the action is a general lightbulb entry rather than the fix FOR the error,
        // so it is absent from every flow keyed to a diagnostic and never marked preferred.
        CodeActionHandler handler = BuildHandler(
            "#namespace game;\nfunction run()\n{\n    util::helper();\n}\n", out DocumentStore _);

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
        ScriptDatabase database = new();
        foreach ( string path in new[] { "scripts\\util.gsc", "scripts\\util_extra.gsc" } )
        {
            ParseResult contributor = AnalyzeAt(
                "#namespace util;\nfunction helper()\n{\n}\n", @"C:\bo3\share\raw\" + path);

            database.Commit(contributor, ResolutionContext.RawContext, false, path);
        }

        CodeActionHandler handler = BuildHandlerWith(
            database, "#namespace game;\nfunction run()\n{\n    util::helper();\n}\n");

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
        CodeActionHandler handler = BuildHandlerWith(
            new ScriptDatabase(),
            "#using scripts\\util;\n#using scripts\\util;\n#namespace game;\nfunction run()\n{\n}\n");

        CodeActionParams request = new()
        {
            TextDocument = new TextDocumentIdentifier { Uri = DocumentUri.FromFileSystemPath(AskingPath) },
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
        CodeActionHandler handler = BuildHandler(
            "#namespace game;\nfunction run()\n{\n    util::helper();\n}\n", out DocumentStore _, utilIsPrivate: true);

        Assert.DoesNotContain(
            await ActionsAtAsync(handler, 3, 10, 16), f => f.Title!.StartsWith("Add #using", StringComparison.Ordinal));
    }
}
