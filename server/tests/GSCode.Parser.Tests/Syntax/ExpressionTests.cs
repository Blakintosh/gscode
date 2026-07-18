using Xunit;

namespace GSCode.Parser.Tests.Syntax;

public class ExpressionTests
{
    [Fact]
    public void Precedence_MultiplicationBindsTighter()
    {
        Assert.Equal("(= x (+ a (* b c)))", ParserTestHelper.PrintExpr("x = a + b * c"));
    }

    [Fact]
    public void Precedence_ComparisonAndLogical()
    {
        Assert.Equal("(= x (|| (&& (< a b) (> c d)) e))", ParserTestHelper.PrintExpr("x = a < b && c > d || e"));
    }

    [Fact]
    public void Ternary_Parses()
    {
        Assert.Equal("(= x (?: a b c))", ParserTestHelper.PrintExpr("x = a ? b : c"));
    }

    [Fact]
    public void CompoundAssignment_Parses()
    {
        Assert.Equal("(+= x 2)", ParserTestHelper.PrintExpr("x += 2"));
        Assert.Equal("(<<= x 1)", ParserTestHelper.PrintExpr("x <<= 1"));
    }

    [Fact]
    public void MemberAndIndexChains()
    {
        Assert.Equal("(= x (. (. self owner) health))", ParserTestHelper.PrintExpr("x = self.owner.health"));
        Assert.Equal("(= x (index (. level players) 0))", ParserTestHelper.PrintExpr("x = level.players[ 0 ]"));
    }

    [Fact]
    public void NestedIndexers_ParseCorrectly()
    {
        // a[b[1]] — the closing "]]" must read as two separate index closers.
        Assert.Equal("(= x (index a (index b 1)))", ParserTestHelper.PrintExpr("x = a[b[1]]"));
    }

    [Fact]
    public void Calls_PlainQualifiedAndArguments()
    {
        Assert.Equal("(call foo)", ParserTestHelper.PrintExpr("foo()"));
        Assert.Equal("(call util::foo 1 2)", ParserTestHelper.PrintExpr("util::foo(1, 2)"));
    }

    [Fact]
    public void MethodNotation_TargetCall()
    {
        Assert.Equal("(call on:player giveweapon \"weapon_x\")", ParserTestHelper.PrintExpr("player giveweapon(\"weapon_x\")"));
    }

    [Fact]
    public void ThreadCalls_WithAndWithoutTarget()
    {
        Assert.Equal("(call thread go)", ParserTestHelper.PrintExpr("thread go()"));
        Assert.Equal("(call thread on:ent do_stuff)", ParserTestHelper.PrintExpr("ent thread do_stuff()"));
    }

    [Fact]
    public void KeywordCalls_WaittillNotifyEndon()
    {
        Assert.Equal("(call on:level waittill \"spawned\")", ParserTestHelper.PrintExpr("level waittill(\"spawned\")"));
        Assert.Equal("(call on:self notify \"death\" attacker)", ParserTestHelper.PrintExpr("self notify(\"death\", attacker)"));
        Assert.Equal("(call on:self endon \"disconnect\")", ParserTestHelper.PrintExpr("self endon(\"disconnect\")"));
        Assert.Equal("(call isdefined x)", ParserTestHelper.PrintExpr("isdefined(x)"));
    }

    [Fact]
    public void PointerDeref_CallForms()
    {
        Assert.Equal("(call (deref func) a)", ParserTestHelper.PrintExpr("[[func]](a)"));
        Assert.Equal("(-> (deref boo_object) faz)", ParserTestHelper.PrintExpr("[[boo_object]]->faz()"));
        Assert.Equal("(-> (deref faz_object) faz undefined 1)", ParserTestHelper.PrintExpr("[[faz_object]]->faz(undefined, 1)"));
    }

    [Fact]
    public void MethodNotation_PointerCallOnTarget()
    {
        Assert.Equal("(call on:ent (deref ptr) 1)", ParserTestHelper.PrintExpr("ent [[ptr]](1)"));
    }

    [Fact]
    public void New_Parses()
    {
        Assert.Equal("(= boo_object (new Boo))", ParserTestHelper.PrintExpr("boo_object = new Boo()"));
    }

    [Fact]
    public void VectorAndArrayLiterals()
    {
        Assert.Equal("(= v (vector 1.0 (prefix- 0.5) 0))", ParserTestHelper.PrintExpr("v = ( 1.0, -0.5, 0 )"));
        Assert.Equal("(= a (array))", ParserTestHelper.PrintExpr("a = []"));
    }

    [Fact]
    public void FunctionReferences_PlainAndQualified()
    {
        Assert.Equal("(= f (prefix& callback))", ParserTestHelper.PrintExpr("f = &callback"));
        Assert.Equal("(= f (prefix& util::callback))", ParserTestHelper.PrintExpr("f = &util::callback"));
    }

    [Fact]
    public void SpecialLiterals_Parse()
    {
        Assert.Equal("(= x #\"hash_val\")", ParserTestHelper.PrintExpr("x = #\"hash_val\""));
        Assert.Equal("(= x &\"MENU_LABEL\")", ParserTestHelper.PrintExpr("x = &\"MENU_LABEL\""));
        Assert.Equal("(= x %anim_run)", ParserTestHelper.PrintExpr("x = %anim_run"));
        Assert.Equal("(= x undefined)", ParserTestHelper.PrintExpr("x = undefined"));
        Assert.Equal("(= t #animtree)", ParserTestHelper.PrintExpr("t = #animtree"));
    }

    [Fact]
    public void StrictEquality_Parses()
    {
        Assert.Equal("(= x (=== a b))", ParserTestHelper.PrintExpr("x = a === b"));
        Assert.Equal("(= x (!== a b))", ParserTestHelper.PrintExpr("x = a !== b"));
    }

    [Fact]
    public void SizeProperty_IsMemberAccess()
    {
        Assert.Equal("(= n (. arr size))", ParserTestHelper.PrintExpr("n = arr.size"));
    }
}
