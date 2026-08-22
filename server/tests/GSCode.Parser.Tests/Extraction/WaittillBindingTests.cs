using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using Xunit;

namespace GSCode.Parser.Tests.Extraction;

/// <summary>
/// <c>self waittill( "damage", attacker, amount );</c> BINDS its trailing arguments — they are
/// outputs the engine fills in, not values being read — and that call is the only place those names
/// ever come into existence.
///
/// Extraction walked them as ordinary expressions and produced no assignment at all, so
/// go-to-definition, hover typing and the outline had nothing to say about `attacker` and its kind.
/// UnassignedVariableLint already knew the rule (it was that lint's largest single source of false
/// positives on shipped code); the two walks now agree about what binds a name.
/// </summary>
public class WaittillBindingTests
{
    private static ImmutableArray<AssignmentSymbol> Assignments(string body)
    {
        ParseResult result = ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc",
            ScriptLanguage.Gsc,
            SourceText.From("function f()\n{\n" + body + "\n}\n"),
            NullInsertProvider.Instance,
            new NameTable());

        return result.Extraction.Functions.Single().Assignments;
    }

    private static bool Binds(string body, string name)
    {
        return Assignments(body).Any(a => string.Equals(a.Name, name, StringComparison.Ordinal));
    }

    [Fact]
    public void TrailingWaittillArgumentsAreBound()
    {
        const string body = "    self waittill( \"damage\", attacker, amount );";

        Assert.True(Binds(body, "attacker"));
        Assert.True(Binds(body, "amount"));
    }

    [Fact]
    public void TheEventNameIsNotBound()
    {
        // The first argument is the event NAME and is a genuine read. Where it is a variable rather
        // than a literal, binding it would invent an assignment the author never wrote.
        Assert.False(Binds("    self waittill( evt, attacker );", "evt"));
    }

    [Fact]
    public void WaittillMatchBindsTheSameWay()
    {
        Assert.True(Binds("    self waittillmatch( \"done\", stage );", "stage"));
    }

    [Fact]
    public void BoundNamesAreLocalsRatherThanFields()
    {
        // OwnerName is "" for a local. A field would be another entity's, with readers elsewhere.
        AssignmentSymbol bound = Assignments("    self waittill( \"damage\", attacker );")
            .Single(a => string.Equals(a.Name, "attacker", StringComparison.Ordinal));

        Assert.Equal("", bound.OwnerName);
        Assert.False(bound.IsLoopVariable);
    }

    [Fact]
    public void AnOrdinaryCallStillReadsItsArguments()
    {
        // Only the waittill family binds. `use( x )` must not invent an assignment to x.
        Assert.False(Binds("    use( \"damage\", attacker );", "attacker"));
    }

    [Fact]
    public void ANonIdentifierArgumentIsNotBound()
    {
        // `self waittill( "x", a.b )` is not a name being introduced.
        Assert.False(Binds("    self waittill( \"x\", holder.field );", "field"));
    }
}
