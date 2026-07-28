using System.Linq;
using GSCode.Core;
using GSCode.Parser.Syntax;
using GSCode.Parser.Syntax.Ast;
using Xunit;

namespace GSCode.Parser.Tests.Syntax;

/// <summary>
/// The inline-path-call fork. The Infinity Ward games qualify a function by its file PATH —
/// <c>maps\mp\_utility::foo()</c> — where BO3 uses a namespace (<c>ns::foo()</c>) and takes an
/// address with <c>&amp;foo</c>. Gated on <see cref="GameProfile.HasInlinePathCalls"/>, so BO3 is
/// unchanged and only the pre-BO3 dialects accept the backslash-path form.
/// </summary>
public class DialectExpressionTests
{
    private static readonly GameProfile Cod4 = GameProfile.ByName("cod4")!;
    private static readonly GameProfile Mw2 = GameProfile.ByName("mw2")!;
    private static readonly GameProfile Bo3 = GameProfile.BlackOps3;

    /// <summary>Parses a snippet as the body of a function and returns its first expression.</summary>
    private static ExprNode FirstExpression(string statement, GameProfile profile)
    {
        string keyword = profile.HasFunctionKeyword ? "function " : "";
        ParseTree tree = ParserTestHelper.Parse(keyword + "run()\n{\n\t" + statement + "\n}\n", profile);
        FunctionNode function = Assert.IsType<FunctionNode>(Assert.Single(tree.Root.Elements));
        ExprStatementNode expressionStatement = function.Body.Statements.OfType<ExprStatementNode>().First();
        return expressionStatement.Expression;
    }

    [Fact]
    public void APathQualifiedCallParses()
    {
        CallNode call = Assert.IsType<CallNode>(FirstExpression("maps\\mp\\_utility::foo();", Cod4));

        PathQualifiedNode callee = Assert.IsType<PathQualifiedNode>(call.Callee);
        Assert.Equal("maps\\mp\\_utility", callee.Path);
        Assert.Equal("foo", callee.NameToken.Text);
        Assert.Null(call.Target);
    }

    [Fact]
    public void APathQualifiedCallKeepsItsArguments()
    {
        CallNode call = Assert.IsType<CallNode>(FirstExpression("maps\\mp\\_utility::foo( a, 1 );", Cod4));

        Assert.IsType<PathQualifiedNode>(call.Callee);
        Assert.Equal(2, call.Arguments.Length);
    }

    [Fact]
    public void MethodNotationTakesAPathQualifiedCallee()
    {
        // self maps\mp\_utility::foo() — the path callee applies to a target object.
        CallNode call = Assert.IsType<CallNode>(FirstExpression("self maps\\mp\\_utility::foo();", Cod4));

        Assert.IsType<PathQualifiedNode>(call.Callee);
        Assert.NotNull(call.Target);
    }

    [Fact]
    public void ThreadTakesAPathQualifiedCallee()
    {
        CallNode call = Assert.IsType<CallNode>(FirstExpression("thread maps\\mp\\_utility::foo();", Cod4));

        Assert.IsType<PathQualifiedNode>(call.Callee);
        Assert.True(call.IsThread);
    }

    [Fact]
    public void APathQualifiedPointerParsesWithoutParens()
    {
        // array_thread( guys, maps\mp\_utility::foo ) — the second argument is a bare pointer,
        // not a call, so it stays a PathQualifiedNode.
        CallNode outer = Assert.IsType<CallNode>(FirstExpression("array_thread( guys, maps\\mp\\_utility::foo );", Cod4));

        ExprNode pointer = outer.Arguments[1];
        PathQualifiedNode path = Assert.IsType<PathQualifiedNode>(pointer);
        Assert.Equal("maps\\mp\\_utility", path.Path);
        Assert.Equal("foo", path.NameToken.Text);
    }

    [Fact]
    public void APathQualifiedCallHasNoDiagnostics()
    {
        ParseTree tree = ParserTestHelper.Parse("run()\n{\n\tmaps\\mp\\_utility::foo();\n}\n", Cod4);

        Assert.Empty(tree.Diagnostics);
    }

    [Fact]
    public void ALeadingScopeResolutionIsALocalPointer()
    {
        // array_thread( guys, ::foo ) — ::foo is a local function pointer (empty path).
        CallNode outer = Assert.IsType<CallNode>(FirstExpression("array_thread( guys, ::foo );", Cod4));

        PathQualifiedNode path = Assert.IsType<PathQualifiedNode>(outer.Arguments[1]);
        Assert.Equal("", path.Path);
        Assert.Equal("foo", path.NameToken.Text);
    }

    [Fact]
    public void ALeadingScopeResolutionCanBeCalled()
    {
        CallNode call = Assert.IsType<CallNode>(FirstExpression("::foo();", Cod4));

        PathQualifiedNode callee = Assert.IsType<PathQualifiedNode>(call.Callee);
        Assert.Equal("", callee.Path);
        Assert.Empty(call.Arguments);
    }

    [Fact]
    public void BlackOps3RejectsALeadingScopeResolution()
    {
        // BO3 needs a namespace before :: -- a bare ::foo does not parse.
        ParseTree tree = ParserTestHelper.Parse("function run()\n{\n\tx = ::foo;\n}\n", Bo3);

        Assert.NotEmpty(tree.Diagnostics);
    }

    [Fact]
    public void BlackOps3RejectsThePathForm()
    {
        // A backslash is not part of any BO3 expression, so the path form does not parse -- the
        // fork leaves BO3 untouched.
        ParseTree tree = ParserTestHelper.Parse("function run()\n{\n\tmaps\\mp\\_utility::foo();\n}\n", Bo3);

        Assert.NotEmpty(tree.Diagnostics);
    }

    [Fact]
    public void BlackOps3StillParsesNamespaceQualifiedCalls()
    {
        // The T7 ns::foo() form is unchanged -- a QualifiedNode, never a PathQualifiedNode.
        CallNode call = Assert.IsType<CallNode>(FirstExpression("_utility::foo();", Bo3));

        Assert.IsType<QualifiedNode>(call.Callee);
    }

    [Fact]
    public void ChildThreadIsAThreadedCall()
    {
        // childthread foo() runs on a child thread, so it parses like thread — a threaded call.
        CallNode call = Assert.IsType<CallNode>(FirstExpression("childthread foo();", Mw2));

        Assert.True(call.IsThread);
        Assert.Null(call.Target);
    }

    [Fact]
    public void CallInvokesAFunctionPointerSynchronously()
    {
        // call [[ level.func ]]( a ) — a synchronous (non-thread) pointer-deref call.
        CallNode call = Assert.IsType<CallNode>(FirstExpression("call [[ level.func ]]( a );", Mw2));

        Assert.False(call.IsThread);
        Assert.IsType<PointerDerefNode>(call.Callee);
        Assert.Single(call.Arguments);
    }

    [Fact]
    public void CallTakesAMethodNotationTarget()
    {
        // self call [[ func ]]() — call applies to a target object, like thread does.
        CallNode call = Assert.IsType<CallNode>(FirstExpression("self call [[ func ]]();", Mw2));

        Assert.False(call.IsThread);
        Assert.NotNull(call.Target);
        Assert.IsType<PointerDerefNode>(call.Callee);
    }

    [Fact]
    public void BlackOps3TreatsCallAsAnOrdinaryIdentifier()
    {
        // BO3's keyword set omits call (its corpus uses it as a variable), so `call = 1;` is a plain
        // assignment there — the word never becomes a keyword.
        ExprNode expression = FirstExpression("call = 1;", Bo3);

        AssignmentNode assignment = Assert.IsType<AssignmentNode>(expression);
        IdentifierNode target = Assert.IsType<IdentifierNode>(assignment.Target);
        Assert.Equal("call", target.Token.Text);
    }
}
