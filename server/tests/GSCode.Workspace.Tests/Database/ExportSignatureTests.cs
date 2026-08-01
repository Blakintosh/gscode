using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Workspace.Database;
using GSCode.Workspace.Resolution;
using Xunit;

namespace GSCode.Workspace.Tests.Database;

/// <summary>
/// The signature that decides whether editing one file can invalidate another's diagnostics.
///
/// Its whole value is in what it does NOT react to. Cross-file lints have to be re-run on the open
/// documents around an edit, and doing that per keystroke would be far too expensive — so the
/// signature must stay put for the edits that dominate typing (inside a body, a comment, a local)
/// and move for the ones another file can actually observe.
/// </summary>
public class ExportSignatureTests
{
    private static ulong SignatureOf(string source, string relativePath = @"scripts\util.gsc")
    {
        ParseResult result = ScriptAnalysis.Analyze(
            @"c:\ws\scripts\util.gsc",
            ScriptLanguage.Gsc,
            SourceText.From(source),
            NullInsertProvider.Instance,
            new NameTable());

        ScriptDatabase database = new();
        return ExportSignature.Of(database.Commit(result, ResolutionContext.RawContext, false, relativePath));
    }

    private const string Original = "#namespace util;\n#using scripts\\other;\n\nfunction get_players( team )\n{\n    x = 1;\n    return x;\n}\n";

    [Fact]
    public void TheSameFileHashesTheSame()
    {
        Assert.Equal(SignatureOf(Original), SignatureOf(Original));
    }

    // --- Unchanged: the edits that dominate typing ---

    [Theory]
    // A statement inside the body — nothing outside the file can see it.
    [InlineData("#namespace util;\n#using scripts\\other;\n\nfunction get_players( team )\n{\n    x = 2;\n    y = 3;\n    return y;\n}\n")]
    // A comment.
    [InlineData("#namespace util;\n#using scripts\\other;\n\n// explains itself\nfunction get_players( team )\n{\n    x = 1;\n    return x;\n}\n")]
    // A local renamed.
    [InlineData("#namespace util;\n#using scripts\\other;\n\nfunction get_players( team )\n{\n    count = 1;\n    return count;\n}\n")]
    public void EditsInsideABodyDoNotMoveIt(string edited)
    {
        // The property the whole feature rests on: these are almost every keystroke, and each one
        // that moved the signature would re-lint every open tab for nothing.
        Assert.Equal(SignatureOf(Original), SignatureOf(edited));
    }

    // --- Changed: what a neighbour's lints actually read ---

    [Theory]
    // Renamed — every caller's resolution changes.
    [InlineData("#namespace util;\n#using scripts\\other;\n\nfunction get_player( team )\n{\n    x = 1;\n    return x;\n}\n")]
    // A different namespace — what NamespaceUsageLint and UnusedUsingLint ask about an import.
    [InlineData("#namespace utility;\n#using scripts\\other;\n\nfunction get_players( team )\n{\n    x = 1;\n    return x;\n}\n")]
    // An extra parameter — ArgumentCountLint compares arity at every call site.
    [InlineData("#namespace util;\n#using scripts\\other;\n\nfunction get_players( team, alive )\n{\n    x = 1;\n    return x;\n}\n")]
    // Now private — PrivateAccessLint gates on it.
    [InlineData("#namespace util;\n#using scripts\\other;\n\nfunction private get_players( team )\n{\n    x = 1;\n    return x;\n}\n")]
    // A default value, which changes how few arguments a call may pass.
    [InlineData("#namespace util;\n#using scripts\\other;\n\nfunction get_players( team = 1 )\n{\n    x = 1;\n    return x;\n}\n")]
    // A second function appears.
    [InlineData("#namespace util;\n#using scripts\\other;\n\nfunction get_players( team )\n{\n    x = 1;\n    return x;\n}\nfunction get_bots()\n{\n}\n")]
    // An import added, which changes what this file reaches.
    [InlineData("#namespace util;\n#using scripts\\other;\n#using scripts\\third;\n\nfunction get_players( team )\n{\n    x = 1;\n    return x;\n}\n")]
    public void ChangesANeighbourCanSeeMoveIt(string edited)
    {
        Assert.NotEqual(SignatureOf(Original), SignatureOf(edited));
    }

    [Fact]
    public void MovingTheFileMovesIt()
    {
        // Identity: a path call or an import resolves by relative path, so the same text at a
        // different path is a different thing to everyone reaching it.
        Assert.NotEqual(SignatureOf(Original), SignatureOf(Original, @"scripts\moved.gsc"));
    }

    [Fact]
    public void ItIsAStableHashRatherThanAProcessSeededOne()
    {
        // Pinned to a literal on purpose. Records are restored from a persistent cache written by
        // an EARLIER PROCESS, so a randomly-seeded hash (System.HashCode) would make every cached
        // file look changed on the first edit of every session and fan out for nothing. Only a
        // deliberate change to what the signature covers should move this number.
        Assert.Equal(SignatureOf(Original), SignatureOf(Original));
        Assert.Equal(ExpectedOriginalSignature, SignatureOf(Original));
    }

    /// <summary>Recorded from the implementation; see the test above for why it is pinned.</summary>
    private const ulong ExpectedOriginalSignature = 3734125606613226397;

    // --- Class methods ---
    //
    // A derived class inherits every method its ancestors declare, so a base class's method set is
    // part of what another file can observe. While only the class name and its parent were hashed,
    // adding a method to cScriptBundleBase left scene_shared.gsc's signature identical and its
    // diagnostics never revisited.

    private const string WithClass =
        "#namespace util;\nclass cBase\n{\n    function play( a )\n    {\n        x = 1;\n    }\n}\n";

    [Fact]
    public void AddingAMethodToAClass_ChangesTheSignature()
    {
        string extended =
            "#namespace util;\nclass cBase\n{\n    function play( a )\n    {\n        x = 1;\n    }\n    function stop()\n    {\n    }\n}\n";

        Assert.NotEqual(SignatureOf(WithClass), SignatureOf(extended));
    }

    [Fact]
    public void ChangingAMethodArity_ChangesTheSignature()
    {
        string widened =
            "#namespace util;\nclass cBase\n{\n    function play( a, b )\n    {\n        x = 1;\n    }\n}\n";

        Assert.NotEqual(SignatureOf(WithClass), SignatureOf(widened));
    }

    [Fact]
    public void MarkingAMethodPrivate_ChangesTheSignature()
    {
        string madePrivate =
            "#namespace util;\nclass cBase\n{\n    function private play( a )\n    {\n        x = 1;\n    }\n}\n";

        Assert.NotEqual(SignatureOf(WithClass), SignatureOf(madePrivate));
    }

    [Fact]
    public void GivingAMethodParameterADefault_ChangesTheSignature()
    {
        // It decides how few arguments a call may legally pass, which is what the argument-count
        // rule in another file compares against.
        string defaulted =
            "#namespace util;\nclass cBase\n{\n    function play( a = 1 )\n    {\n        x = 1;\n    }\n}\n";

        Assert.NotEqual(SignatureOf(WithClass), SignatureOf(defaulted));
    }

    [Fact]
    public void EditingAMethodBody_DoesNotChangeTheSignature()
    {
        // The other half of the contract: this is the edit that dominates typing, and reacting to it
        // would re-lint every dependent document on every keystroke.
        string rebodied =
            "#namespace util;\nclass cBase\n{\n    function play( a )\n    {\n        x = 2;\n        y = 3;\n    }\n}\n";

        Assert.Equal(SignatureOf(WithClass), SignatureOf(rebodied));
    }

    [Fact]
    public void AddingAConstructor_DoesNotChangeTheSignature()
    {
        // No caller can name one, so no caller's diagnostics can depend on it.
        string constructed =
            "#namespace util;\nclass cBase\n{\n    constructor()\n    {\n    }\n    function play( a )\n    {\n        x = 1;\n    }\n}\n";

        Assert.Equal(SignatureOf(WithClass), SignatureOf(constructed));
    }
}
