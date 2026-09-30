using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Server.Handlers;
using GSCode.Workspace.Database;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// Who the incoming half of the call hierarchy says is calling.
///
/// The grouping used to be by FILE, with the whole group named after whichever function contained
/// the first call range. A file where two functions both call the target therefore reported one
/// caller, and the ranges belonging to the other function were listed underneath it — so clicking
/// one jumped into a body that does not contain it.
/// </summary>
public class CallHierarchyGroupingTests
{
    private static readonly string s_callerPath = TestPaths.Raw(@"scripts\caller.gsc");

    private static FunctionSymbol Function(string name, int firstLine, int lastLine)
    {
        return new FunctionSymbol
        {
            Name = name,
            KeyName = name,
            Namespace = "caller",
            NameRange = new TextRange(new Position(firstLine, 9), new Position(firstLine, 9 + name.Length)),
            FullRange = new TextRange(new Position(firstLine, 0), new Position(lastLine, 1)),
        };
    }

    private static ScriptRecord Record(params FunctionSymbol[] functions)
    {
        return TestRecords.At(s_callerPath) with { ContentHash = 1, Functions = [.. functions] };
    }

    private static ReferenceEntry CallAt(int line)
    {
        return new ReferenceEntry(
            new SymbolKey("target", "run", SymbolKind.Function),
            new TextRange(new Position(line, 1), new Position(line, 4)),
            ReferenceKind.Call);
    }

    [Fact]
    public void TwoFunctionsInOneFileAreTwoCallers()
    {
        FunctionSymbol first = Function("alpha", firstLine: 0, lastLine: 4);
        FunctionSymbol second = Function("beta", firstLine: 6, lastLine: 10);
        ScriptRecord record = Record(first, second);

        List<CallHierarchyHandler.IncomingGroup> groups = CallHierarchyHandler.GroupIncomingCalls(
            [(record, CallAt(2)), (record, CallAt(8))]);

        Assert.Equal(2, groups.Count);
        Assert.Contains(groups, group => group.Caller?.Name == "alpha" && group.Ranges.Count == 1);
        Assert.Contains(groups, group => group.Caller?.Name == "beta" && group.Ranges.Count == 1);
    }

    [Fact]
    public void SeveralCallsFromOneFunctionStayOneCaller()
    {
        FunctionSymbol only = Function("alpha", firstLine: 0, lastLine: 9);
        ScriptRecord record = Record(only);

        List<CallHierarchyHandler.IncomingGroup> groups = CallHierarchyHandler.GroupIncomingCalls(
            [(record, CallAt(2)), (record, CallAt(3)), (record, CallAt(7))]);

        CallHierarchyGroup(groups, "alpha", expectedRanges: 3);
        Assert.Single(groups);
    }

    [Fact]
    public void TheDeclarationItselfIsNotACaller()
    {
        FunctionSymbol only = Function("alpha", firstLine: 0, lastLine: 4);
        ScriptRecord record = Record(only);

        ReferenceEntry definition = new(
            new SymbolKey("target", "run", SymbolKind.Function),
            new TextRange(new Position(1, 1), new Position(1, 4)),
            ReferenceKind.Definition);

        Assert.Empty(CallHierarchyHandler.GroupIncomingCalls([(record, definition)]));
    }

    [Fact]
    public void ACallOutsideEveryFunctionBodyHasNoCallingFunction()
    {
        // A file-scope initialiser, say. The file stands in for the caller rather than the call
        // being attributed to whichever function happens to be nearby.
        ScriptRecord record = Record(Function("alpha", firstLine: 4, lastLine: 8));

        List<CallHierarchyHandler.IncomingGroup> groups = CallHierarchyHandler.GroupIncomingCalls(
            [(record, CallAt(0))]);

        CallHierarchyHandler.IncomingGroup group = Assert.Single(groups);
        Assert.Null(group.Caller);
    }

    private static void CallHierarchyGroup(
        List<CallHierarchyHandler.IncomingGroup> groups, string name, int expectedRanges)
    {
        CallHierarchyHandler.IncomingGroup group = Assert.Single(groups, candidate => candidate.Caller?.Name == name);
        Assert.Equal(expectedRanges, group.Ranges.Count);
    }
}
