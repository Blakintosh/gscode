using GSCode.Core;
using GSCode.Core.Paths;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Server.Handlers;
using GSCode.Server.Tests.Corpus;
using GSCode.Workspace.Database;
using GSCode.Workspace.Documents;
using GSCode.Workspace.Resolution;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// The closed files a full-mode re-lint reaches after an edit changes a file's exports, on both
/// dialect families. On a merge dialect a function still has a declared namespace — the file stem
/// — but calls to it are keyed with none, so the dependents have to be found under the key the
/// references were actually indexed with, not one rebuilt from the declaration.
/// </summary>
[Collection(GameProfileCollection.Name)]
public class ClosedDependentsDialectTests
{
    private const string LibPath = @"C:\ws\maps\lib.gsc";
    private const string CallerPath = @"C:\ws\maps\caller.gsc";

    private static HashSet<string> DependentsOn(GameProfile profile, string libSource, string callerSource)
    {
        GameProfile previous = GameProfile.Active;
        try
        {
            GameProfile.Select(profile.ShortName);

            NameTable names = new();
            ScriptDatabase database = new();

            ParseResult lib = ScriptAnalysis.Analyze(
                LibPath, ScriptLanguage.Gsc, SourceText.From(libSource), NullInsertProvider.Instance, names, profile);
            ScriptRecord origin = database.Commit(lib, ResolutionContext.RawContext, isDirty: false, @"maps\lib.gsc");

            ParseResult caller = ScriptAnalysis.Analyze(
                CallerPath, ScriptLanguage.Gsc, SourceText.From(callerSource), NullInsertProvider.Instance, names, profile);
            database.Commit(caller, ResolutionContext.RawContext, isDirty: false, @"maps\caller.gsc");

            DocumentStore noOpenDocuments = new(static _ => NullInsertProvider.Instance, new NameTable());
            return DependentDiagnosticsRefresher.ClosedDependentsOf(origin, database.Gsc, noOpenDocuments);
        }
        finally
        {
            GameProfile.Select(previous.ShortName);
        }
    }

    [Fact]
    public void AMergeDialectCaller_IsADependent()
    {
        HashSet<string> dependents = DependentsOn(
            GameProfile.Cod4,
            "helper()\n{\n}\n",
            "#include maps\\lib;\n\nrun()\n{\n    helper();\n}\n");

        Assert.Contains(PathUtil.NormalizeAbsolute(CallerPath), dependents);
    }

    [Fact]
    public void AMergeDialectPathCall_IsADependent()
    {
        HashSet<string> dependents = DependentsOn(
            GameProfile.Cod4,
            "helper()\n{\n}\n",
            "run()\n{\n    maps\\lib::helper();\n}\n");

        Assert.Contains(PathUtil.NormalizeAbsolute(CallerPath), dependents);
    }

    [Fact]
    public void ANamespaceDialectCaller_IsStillADependent()
    {
        HashSet<string> dependents = DependentsOn(
            GameProfile.BlackOps3,
            "#namespace lib;\nfunction helper()\n{\n}\n",
            "#using maps\\lib;\n#namespace game;\nfunction run()\n{\n    lib::helper();\n}\n");

        Assert.Contains(PathUtil.NormalizeAbsolute(CallerPath), dependents);
    }
}
