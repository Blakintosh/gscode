using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Parser.Preprocessing;
using GSCode.Server.Configuration;
using GSCode.Server.Handlers;
using GSCode.Workspace.Api;
using GSCode.Workspace.Database;
using GSCode.Workspace.Documents;
using GSCode.Workspace.Indexing;
using GSCode.Workspace.Resolution;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// F7: <c>workspaceIndexingMode: full</c> — the cross-file lints, run over a CLOSED file's
/// stored record rather than only over open documents. Uses a real temporary directory and
/// <see cref="PhysicalFileSystem"/> rather than an in-memory fake: this project has no
/// <c>FakeFileSystem</c> of its own (that lives in <c>GSCode.Workspace.Tests</c>), and a handful
/// of small real files is cheap enough that duplicating one is not worth it.
/// </summary>
public sealed class WorkspaceLintSweepTests : IDisposable
{
    private readonly string _root;

    public WorkspaceLintSweepTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"gscode-lint-sweep-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_root, "scripts"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch ( IOException )
        {
            // Best-effort cleanup; a locked file left behind costs nothing a later run cannot fix.
        }
    }

    private void WriteFile(string relativePath, string content)
    {
        File.WriteAllText(Path.Combine(_root, relativePath), content);
    }

    private sealed record Harness(
        ScriptDatabase Database, DocumentStore Documents, WorkspaceIndexer Indexer, WorkspaceLintSweep Sweep);

    private async Task<Harness> BuildAsync()
    {
        GameProfile previous = GameProfile.Active;
        GameProfile.Select("bo3");
        try
        {
            PhysicalFileSystem fileSystem = new();
            RootConfig config = RootConfig.Create(
                rawEnabled: true, rawPath: _root, modsPath: null, workspaceFolders: [], fileSystem: fileSystem);
            PathResolver resolver = new(config, fileSystem);

            NameTable names = new();
            ScriptDatabase database = new();
            WorkspaceIndexer indexer = new(database, () => resolver, fileSystem, names);
            await indexer.IndexAsync(IndexingMode.Partial, NullIndexProgressListener.Instance, CancellationToken.None);

            ResolverHolder resolverHolder = new(fileSystem) { Current = resolver };
            BuiltinApiSet builtins = BuiltinApiSet.Load(Path.Combine(AppContext.BaseDirectory, "Api"));
            ObjectFields objectFields = ObjectFields.Load(Path.Combine(AppContext.BaseDirectory, "Api"));
            DocumentStore documents = new(_ => NullInsertProvider.Instance, names);
            DocumentLinter linter = new(database, resolverHolder, builtins, objectFields);
            WorkspaceLintSweep sweep = new(database, documents, indexer, linter);

            return new Harness(database, documents, indexer, sweep);
        }
        finally
        {
            GameProfile.Select(previous.ShortName);
        }
    }

    [Fact]
    public async Task BeforeTheSweep_AClosedFilesStoredDiagnosticsAreParseLevelOnly()
    {
        WriteFile(@"scripts\lib.gsc", "#namespace lib;\nfunction helper()\n{\n}\n");
        WriteFile(
            @"scripts\caller.gsc",
            "#using scripts\\lib;\n#namespace game;\nfunction run()\n{\n}\n");

        Harness harness = await BuildAsync();

        string callerPath = Path.Combine(_root, "scripts", "caller.gsc");
        Assert.True(harness.Database.TryGetAnyRecord(callerPath, out ScriptRecord record));
        Assert.DoesNotContain(record.Diagnostics, d => d.Code == GscDiagnosticCode.UnusedUsing);
    }

    [Fact]
    public async Task RunFullSweepAsync_AddsTheCrossFileLintToTheClosedRecord()
    {
        WriteFile(@"scripts\lib.gsc", "#namespace lib;\nfunction helper()\n{\n}\n");
        WriteFile(
            @"scripts\caller.gsc",
            "#using scripts\\lib;\n#namespace game;\nfunction run()\n{\n}\n");

        Harness harness = await BuildAsync();
        string callerPath = Path.Combine(_root, "scripts", "caller.gsc");

        LintSweepOutcome outcome = await harness.Sweep.RunFullSweepAsync(CancellationToken.None);

        Assert.True(outcome.Linted > 0);
        Assert.True(harness.Database.TryGetAnyRecord(callerPath, out ScriptRecord record));
        Assert.Contains(record.Diagnostics, d => d.Code == GscDiagnosticCode.UnusedUsing);
    }

    [Fact]
    public async Task RunFullSweepAsync_SkipsAnOpenDocument_LeavingItsRecordUntouched()
    {
        WriteFile(@"scripts\lib.gsc", "#namespace lib;\nfunction helper()\n{\n}\n");
        WriteFile(
            @"scripts\caller.gsc",
            "#using scripts\\lib;\n#namespace game;\nfunction run()\n{\n}\n");

        Harness harness = await BuildAsync();
        string callerPath = Path.Combine(_root, "scripts", "caller.gsc");

        harness.Documents.Open(callerPath, File.ReadAllText(callerPath), version: 1);

        await harness.Sweep.RunFullSweepAsync(CancellationToken.None);

        // Untouched, not merely lacking the lint: the sweep must not have written to it at all,
        // since the open document's own live-analysis path is what owns its diagnostics now.
        Assert.True(harness.Database.TryGetAnyRecord(callerPath, out ScriptRecord record));
        Assert.DoesNotContain(record.Diagnostics, d => d.Code == GscDiagnosticCode.UnusedUsing);
    }

    [Fact]
    public async Task RelintClosedFilesAsync_UpgradesOnlyTheNamedFile()
    {
        WriteFile(@"scripts\lib.gsc", "#namespace lib;\nfunction helper()\n{\n}\n");
        WriteFile(
            @"scripts\caller.gsc",
            "#using scripts\\lib;\n#namespace game;\nfunction run()\n{\n}\n");
        WriteFile(
            @"scripts\other_caller.gsc",
            "#using scripts\\lib;\n#namespace game2;\nfunction run2()\n{\n}\n");

        Harness harness = await BuildAsync();
        string callerPath = Path.Combine(_root, "scripts", "caller.gsc");
        string otherPath = Path.Combine(_root, "scripts", "other_caller.gsc");

        LintSweepOutcome outcome = await harness.Sweep.RelintClosedFilesAsync([callerPath], CancellationToken.None);

        Assert.Equal(1, outcome.Linted);
        Assert.True(harness.Database.TryGetAnyRecord(callerPath, out ScriptRecord relinted));
        Assert.Contains(relinted.Diagnostics, d => d.Code == GscDiagnosticCode.UnusedUsing);

        Assert.True(harness.Database.TryGetAnyRecord(otherPath, out ScriptRecord untouched));
        Assert.DoesNotContain(untouched.Diagnostics, d => d.Code == GscDiagnosticCode.UnusedUsing);
    }
}
