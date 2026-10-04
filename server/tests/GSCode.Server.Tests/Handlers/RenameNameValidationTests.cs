using GSCode.Core.Symbols;
using GSCode.Parser.Lexing;
using GSCode.Server.Handlers;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// What a rename may be renamed TO.
///
/// Nothing checked it. The new name arrives from a text box and was written straight into every
/// reference range, so renaming a function to "my func" or "2fast" rewrote the whole workspace into
/// text that no longer lexes as one token — across every file the symbol reaches, in one edit.
/// PrepareRenameHandler cannot cover this: prepare runs before the name is typed.
/// </summary>
public class RenameNameValidationTests
{
    [Theory]
    [InlineData(SymbolKind.Function)]
    [InlineData(SymbolKind.Class)]
    [InlineData(SymbolKind.Macro)]
    [InlineData(SymbolKind.Field)]
    public void AnIdentifierKindTakesOnlyAnIdentifier(SymbolKind kind)
    {
        Assert.True(RenameHandler.IsLegalNewName(kind, "good_name2"));
        Assert.True(RenameHandler.IsLegalNewName(kind, "_leading_underscore"));

        Assert.False(RenameHandler.IsLegalNewName(kind, "my func"));
        Assert.False(RenameHandler.IsLegalNewName(kind, "2fast"));
        Assert.False(RenameHandler.IsLegalNewName(kind, "has-a-dash"));
        Assert.False(RenameHandler.IsLegalNewName(kind, "ns::qualified"));
        Assert.False(RenameHandler.IsLegalNewName(kind, ""));
    }

    [Theory]
    [InlineData(SymbolKind.StringLiteral)]
    [InlineData(SymbolKind.HashString)]
    [InlineData(SymbolKind.LocalizedString)]
    [InlineData(SymbolKind.AnimReference)]
    public void ALiteralMayHoldAnythingThatDoesNotEndTheLiteral(SymbolKind kind)
    {
        // These are string CONTENT, not identifiers: a notify string with a space in it is ordinary.
        Assert.True(RenameHandler.IsLegalNewName(kind, "player killed"));
        Assert.True(RenameHandler.IsLegalNewName(kind, "weapon.fire-2"));

        // What it may not hold is a character that closes the literal early.
        Assert.False(RenameHandler.IsLegalNewName(kind, "unbalanced\" quote"));
        Assert.False(RenameHandler.IsLegalNewName(kind, "trailing escape\\"));
        Assert.False(RenameHandler.IsLegalNewName(kind, "two\nlines"));
        Assert.False(RenameHandler.IsLegalNewName(kind, ""));
    }

    [Fact]
    public void TheIdentifierRuleIsTheLexersOwn()
    {
        // The rule used to be a local copy testing char.IsLetterOrDigit, which is Unicode-wide and
        // so accepted names the lexer splits into two tokens.
        Assert.False(GscIdentifier.IsIdentifier("caf\u00e9"));
        Assert.True(GscIdentifier.IsIdentifier("cafe"));
    }
}
