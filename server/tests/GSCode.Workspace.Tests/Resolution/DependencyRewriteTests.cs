using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Workspace.Database;
using GSCode.Workspace.Indexing;
using GSCode.Workspace.Resolution;
using GSCode.Workspace.Tests.Resolution;
using Xunit;

namespace GSCode.Workspace.Tests.Resolution;

/// <summary>
/// Renaming a script must carry its importers with it, or the rename silently breaks them and
/// only surfaces later as unresolved-path diagnostics far from the cause.
/// </summary>
public class DependencyRewriteTests
{
    private const string Raw = @"C:\bo3\share\raw";

    private static ScriptDatabase BuildWorkspace()
    {
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(@$"{Raw}\scripts\shared\util.gsc", "#namespace util;\nfunction helper()\n{\n}\n")
            .AddFile(@$"{Raw}\scripts\shared\shared.gsh", "#define IS_TRUE(__a) (isdefined(__a) && __a)\n")
            .AddFile(
                @$"{Raw}\scripts\a.gsc",
                "#using scripts\\shared\\util;\n#insert scripts\\shared\\shared.gsh;\nfunction run()\n{\n}\n")
            .AddFile(@$"{Raw}\scripts\b.gsc", "#using scripts\\shared\\util;\nfunction other()\n{\n}\n")
            .AddFile(@$"{Raw}\scripts\c.gsc", "#using scripts\\shared\\other_thing;\nfunction third()\n{\n}\n");

        RootConfig config = RootConfig.Create(true, null, null, @"C:\bo3", [], files);
        PathResolver resolver = new(config, files);
        ScriptDatabase database = new();
        WorkspaceIndexer indexer = new(database, () => resolver, files, new NameTable());
        indexer.IndexAsync(IndexingMode.Partial, NullIndexProgressListener.Instance, CancellationToken.None).GetAwaiter().GetResult();

        return database;
    }

    [Fact]
    public void RenamingAScript_RewritesEveryUsingThatNamesIt()
    {
        ImmutableArray<DependencyEdit> edits = DependencyRewrite.PlanRename(
            BuildWorkspace(), @"scripts\shared\util", @"scripts\shared\utility", isInsert: false);

        // a.gsc and b.gsc both import it; c.gsc imports something else.
        Assert.Equal(2, edits.Length);
        Assert.All(edits, edit => Assert.Equal(@"scripts\shared\utility", edit.NewText));
        Assert.DoesNotContain(edits, edit => edit.FilePath.EndsWith("c.gsc", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RenamingAHeader_RewritesInsertsNotUsings()
    {
        ImmutableArray<DependencyEdit> edits = DependencyRewrite.PlanRename(
            BuildWorkspace(), @"scripts\shared\shared.gsh", @"scripts\shared\common.gsh", isInsert: true);

        DependencyEdit edit = Assert.Single(edits);
        Assert.EndsWith("a.gsc", edit.FilePath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(@"scripts\shared\common.gsh", edit.NewText);
    }

    [Fact]
    public void UsingAndInsertDoNotCrossOver()
    {
        // Asking for insert edits on a script path must not touch the #using directives.
        ImmutableArray<DependencyEdit> edits = DependencyRewrite.PlanRename(
            BuildWorkspace(), @"scripts\shared\util", @"scripts\shared\utility", isInsert: true);

        Assert.Empty(edits);
    }

    [Fact]
    public void SlashStyleAndCasing_StillMatch()
    {
        // Directives are written either way; the plan has to find them regardless.
        ImmutableArray<DependencyEdit> edits = DependencyRewrite.PlanRename(
            BuildWorkspace(), @"Scripts/Shared/Util", @"scripts\shared\utility", isInsert: false);

        Assert.Equal(2, edits.Length);
    }

    [Fact]
    public void RenameToTheSamePath_PlansNothing()
    {
        ImmutableArray<DependencyEdit> edits = DependencyRewrite.PlanRename(
            BuildWorkspace(), @"scripts\shared\util", @"scripts/shared/util", isInsert: false);

        Assert.Empty(edits);
    }

    [Fact]
    public void UnreferencedScript_PlansNothing()
    {
        ImmutableArray<DependencyEdit> edits = DependencyRewrite.PlanRename(
            BuildWorkspace(), @"scripts\nobody\imports_me", @"scripts\nobody\renamed", isInsert: false);

        Assert.Empty(edits);
    }

    [Theory]
    [InlineData(@"scripts\shared\util.gsc", false, @"scripts\shared\util")]
    [InlineData(@"scripts/shared/util.csc", false, @"scripts\shared\util")]
    [InlineData(@"scripts\shared\shared.gsh", true, @"scripts\shared\shared.gsh")]
    [InlineData(@"scripts\no_extension", false, @"scripts\no_extension")]
    public void DirectiveForm_DropsTheExtensionOnlyForUsing(string relativePath, bool isInsert, string expected)
    {
        Assert.Equal(expected, DependencyRewrite.ToDirectivePath(relativePath, isInsert));
    }
}
