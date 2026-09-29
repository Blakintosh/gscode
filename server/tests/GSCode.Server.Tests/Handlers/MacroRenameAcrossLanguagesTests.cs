using GSCode.Server.Handlers;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;
using LspPosition = OmniSharp.Extensions.LanguageServer.Protocol.Models.Position;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// Renaming a macro from a script, driven through RenameHandler the way the editor drives it.
///
/// A macro is declared in a <c>.gsh</c>, which is inserted into <c>.gsc</c> and <c>.csc</c> alike,
/// so the two language worlds are not a scope for it. The reported fault was that the rename query
/// took its scope from the ASKING file's language: started in the <c>.gsc</c> it rewrote the
/// <c>.gsc</c> and the <c>.gsh</c> and left every <c>.csc</c> use spelled the old way, expanding to
/// nothing. Starting the same rename from the <c>.gsh</c> worked, which is what made it easy to
/// miss by hand.
/// </summary>
public class MacroRenameAcrossLanguagesTests
{
    private const string HeaderRelativePath = @"scripts\shared\flags.gsh";
    private const string GscRelativePath = @"scripts\shared\flags_test.gsc";
    private const string CscRelativePath = @"scripts\shared\flags_test.csc";

    private const string HeaderSource = "#define MAX_FLAGS 8\n";

    private const string ScriptSource =
        "#insert scripts\\shared\\flags.gsh;\nfunction f()\n{\n    x = MAX_FLAGS;\n}\n\nfunction g()\n{\n    f();\n}\n";

    /// <summary>The one MAX_FLAGS use in <see cref="ScriptSource"/>: line 3, inside the name.</summary>
    private static LspPosition MacroUse => new(3, 10);

    /// <summary>The one f() call in <see cref="ScriptSource"/>: line 8, inside the name.</summary>
    private static LspPosition FunctionCall => new(8, 4);

    /// <summary>
    /// The header and both scripts, indexed with the real insert provider, so the scripts'
    /// MAX_FLAGS is a macro use and not a variable.
    /// </summary>
    private static async Task<WorkspaceEdit?> RenameAsync(string askingRelativePath, LspPosition position)
    {
        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync(
        [
            new TestFile(HeaderRelativePath, HeaderSource),
            new TestFile(GscRelativePath, ScriptSource),
            new TestFile(CscRelativePath, ScriptSource),
        ]);
        workspace.Open(askingRelativePath);

        RenameHandler handler = new(
            workspace.Navigation, workspace.Builtins, workspace.ObjectFields, HandlerWorkspace.Selector);

        return await handler.Handle(
            new RenameParams
            {
                TextDocument = HandlerWorkspace.Identify(askingRelativePath),
                Position = position,
                NewName = "FLAG_LIMIT",
            },
            CancellationToken.None);
    }

    private static bool Touches(WorkspaceEdit edit, string extension)
    {
        foreach ( KeyValuePair<DocumentUri, IEnumerable<TextEdit>> change in edit.Changes! )
        {
            if ( change.Key.GetFileSystemPath().EndsWith(extension, StringComparison.OrdinalIgnoreCase)
                && change.Value.Any() )
            {
                return true;
            }
        }

        return false;
    }

    [Fact]
    public async Task AMacroRenamedFromTheGsc_EditsTheGshAndTheCscToo()
    {
        WorkspaceEdit? edit = await RenameAsync(GscRelativePath, MacroUse);

        Assert.NotNull(edit);
        Assert.True(Touches(edit, ".gsc"), "the asking file itself");
        Assert.True(Touches(edit, ".gsh"), "the declaration");
        Assert.True(Touches(edit, ".csc"), "the other world's uses, which the old scope dropped");
    }

    [Fact]
    public async Task AMacroRenamedFromTheCsc_EditsTheGshAndTheGscToo()
    {
        // The mirror case, for the same reason: neither world owns the macro.
        WorkspaceEdit? edit = await RenameAsync(CscRelativePath, MacroUse);

        Assert.NotNull(edit);
        Assert.True(Touches(edit, ".csc"));
        Assert.True(Touches(edit, ".gsh"));
        Assert.True(Touches(edit, ".gsc"));
    }

    [Fact]
    public async Task AFunctionRenamedFromTheGsc_LeavesTheCscAlone()
    {
        // The isolation the macro rule must not widen: flags_test.gsc and flags_test.csc each
        // declare their own f(), and they are different functions.
        WorkspaceEdit? edit = await RenameAsync(GscRelativePath, FunctionCall);

        Assert.NotNull(edit);
        Assert.True(Touches(edit, ".gsc"));
        Assert.False(Touches(edit, ".csc"));
    }
}
