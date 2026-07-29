using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Server.Handlers;
using GSCode.Workspace.Api;
using GSCode.Workspace.Database;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// What may be renamed. The line is OWNERSHIP, not kind: anything the scripts define can be
/// renamed because every occurrence is in the workspace and the edit is complete, while anything
/// the engine defines cannot — rewriting the call sites of <c>GetTime</c> or <c>.origin</c> while
/// the engine keeps the old name turns working code into code that resolves to nothing.
///
/// The previous rule (Function/Class/Macro only) got the engine half right and threw away the
/// scripts' own fields and literals with it.
/// </summary>
public class RenameScopeTests
{
    private static string ApiDirectory => Path.Combine(AppContext.BaseDirectory, "Api");
    private static BuiltinApi Builtins => BuiltinApiSet.Load(ApiDirectory).For(ScriptLanguage.Gsc);
    private static ObjectFields Fields => ObjectFields.Load(ApiDirectory);

    private static bool CanRename(string name, SymbolKind kind)
    {
        PositionHit hit = new(
            HitKind.Reference, new SymbolKey(null, name, kind), TextRange.Empty, ReferenceKind.Call, "");

        return RenameHandler.IsRenameable(hit, Builtins, Fields);
    }

    [Theory]
    [InlineData(SymbolKind.Class)]
    [InlineData(SymbolKind.Macro)]
    [InlineData(SymbolKind.StringLiteral)]
    [InlineData(SymbolKind.HashString)]
    [InlineData(SymbolKind.LocalizedString)]
    [InlineData(SymbolKind.AnimReference)]
    public void WhatTheScriptsDefineIsRenameable(SymbolKind kind)
    {
        // A notify string is exactly the kind of name worth renaming everywhere at once, and it
        // used to be rejected outright.
        Assert.True(CanRename("something_the_script_named", kind));
    }

    [Fact]
    public void AScriptFunctionIsRenameable()
    {
        Assert.True(CanRename("a_function_no_engine_has", SymbolKind.Function));
    }

    [Theory]
    [InlineData("GetTime")]
    [InlineData("SetDvar")]
    [InlineData("PlayFX")]
    [InlineData("spawn")]
    public void ABuiltinIsNot(string name)
    {
        // Keyed as a Function like any other call, so the library is what tells them apart.
        //
        // `isdefined` deliberately is not among these: in BO3 it is a KEYWORD, not an API entry, so
        // it never reaches here as a function reference at all. Asserting on it would have been
        // testing a hit the resolver cannot produce.
        Assert.False(CanRename(name, SymbolKind.Function));
    }

    [Fact]
    public void AScriptsOwnFieldIsRenameableButAnEngineFieldIsNot()
    {
        Assert.True(CanRename("my_custom_flag", SymbolKind.Field));
        Assert.False(CanRename("origin", SymbolKind.Field));
    }

    [Fact]
    public void NothingUnderTheCursorIsNotRenameable()
    {
        PositionHit nothing = PositionHit.None;

        Assert.False(RenameHandler.IsRenameable(nothing, Builtins, Fields));
    }
}
