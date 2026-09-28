using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using Xunit;

namespace GSCode.Parser.Tests.Extraction;

/// <summary>
/// The two halves of what extraction records about a field write: WHERE it happens
/// (<see cref="ReferenceKind.FieldWrite"/>) and WHAT was put there
/// (<see cref="FieldBinding"/>).
///
/// Both exist because a field is declared nowhere. Every navigation request that looked for a
/// declaration found none and answered nothing, and the answer is not one thing: go-to-definition
/// wants the write, while type-definition, implementation and the two hierarchies want the symbol
/// the write named. Keeping them apart here is what keeps those answers apart.
///
/// The negative cases carry as much weight as the positives. A binding recorded for a right-hand
/// side that does NOT name one thing is a confidently wrong jump, which is worse than the silence
/// it replaced.
/// </summary>
public class FieldBindingTests
{
    private static ParseResult Analyze(string source, string path = @"c:\work\scripts\test.gsc")
    {
        return ScriptAnalysis.Analyze(
            path,
            ScriptAnalysis.LanguageFromPath(path),
            SourceText.From(source),
            NullInsertProvider.Instance,
            new NameTable());
    }

    private static ImmutableArray<ReferenceEntry> FieldReferences(ParseResult result, string name)
    {
        return [.. result.Extraction.References.Where(
            entry => entry.Key.Kind == SymbolKind.Field && entry.Key.Name == name)];
    }

    private static FieldBinding SingleBinding(ParseResult result)
    {
        return Assert.Single(result.Extraction.FieldBindings);
    }

    [Fact]
    public void TheLeftOfAnAssignmentIsAWriteAndEverythingElseIsARead()
    {
        ParseResult result = Analyze(
            "function f()\n"
            + "{\n"
            + "    level.count = 1;\n"
            + "    x = level.count;\n"
            + "}\n");

        ImmutableArray<ReferenceEntry> references = FieldReferences(result, "count");
        Assert.Equal(2, references.Length);
        Assert.Single(references, entry => entry.Kind == ReferenceKind.FieldWrite);
        Assert.Single(references, entry => entry.Kind == ReferenceKind.FieldAccess);
    }

    [Fact]
    public void OnlyTheOutermostMemberOfATargetIsWritten()
    {
        // `level.a[ level.b ].c = 1` writes `c`. The other two are lookups that happen to sit
        // inside the target expression, and the walk reaches all three the same way.
        ParseResult result = Analyze(
            "function f()\n"
            + "{\n"
            + "    level.a[ level.b ].c = 1;\n"
            + "}\n");

        Assert.Equal(ReferenceKind.FieldWrite, Assert.Single(FieldReferences(result, "c")).Kind);
        Assert.Equal(ReferenceKind.FieldAccess, Assert.Single(FieldReferences(result, "a")).Kind);
        Assert.Equal(ReferenceKind.FieldAccess, Assert.Single(FieldReferences(result, "b")).Kind);
    }

    [Fact]
    public void ACompoundAssignmentIsAnUpdateRatherThanAWrite()
    {
        // Still a write for everything that asks where the field is SET — go-to-definition and
        // document highlight both take it. Separate so go-to-implementation can leave it out:
        // `+= 1` adjusts a value some plain assignment already decided, so it is a step rather
        // than an answer to "what is in this field".
        ParseResult result = Analyze(
            "function f()\n"
            + "{\n"
            + "    level.count = 0;\n"
            + "    level.count += 1;\n"
            + "    level.count |= 2;\n"
            + "}\n");

        ImmutableArray<ReferenceEntry> references = FieldReferences(result, "count");
        Assert.Equal(3, references.Length);
        Assert.Single(references, entry => entry.Kind == ReferenceKind.FieldWrite);
        Assert.Equal(2, references.Count(entry => entry.Kind == ReferenceKind.FieldUpdate));
        Assert.Empty(result.Extraction.FieldBindings);
    }

    [Fact]
    public void AnAddressOfBindsTheFunctionItNames()
    {
        ParseResult result = Analyze(
            "#namespace util;\n"
            + "function f()\n"
            + "{\n"
            + "    level.callback = &on_damage;\n"
            + "}\n"
            + "function on_damage()\n"
            + "{\n"
            + "}\n");

        FieldBinding binding = SingleBinding(result);
        Assert.Equal(new SymbolKey(null, "callback", SymbolKind.Field), binding.Field);
        Assert.Equal(SymbolKind.Function, binding.Target.Kind);
        Assert.Equal("on_damage", binding.Target.Name);

        // Keyed through the same path an ordinary call takes, so the binding and the call meet at
        // one key — the whole reason TryCalleeKey is shared rather than copied.
        Assert.Equal("util", binding.Target.Namespace);
    }

    [Fact]
    public void AQualifiedNameBindsWithoutAnAmpersand()
    {
        ParseResult result = Analyze(
            "function f()\n"
            + "{\n"
            + "    level.callback = damage::on_hit;\n"
            + "}\n");

        FieldBinding binding = SingleBinding(result);
        Assert.Equal("damage", binding.Target.Namespace);
        Assert.Equal("on_hit", binding.Target.Name);
    }

    [Fact]
    public void ANewExpressionBindsTheClass()
    {
        ParseResult result = Analyze(
            "function f()\n"
            + "{\n"
            + "    level.scene = new cAwarenessScene();\n"
            + "}\n");

        FieldBinding binding = SingleBinding(result);
        Assert.Equal(new SymbolKey(null, "scene", SymbolKind.Field), binding.Field);
        Assert.Equal(SymbolKind.Class, binding.Target.Kind);
        Assert.Equal("cawarenessscene", binding.Target.Name);
    }

    [Fact]
    public void ABareIdentifierIsALocalReadAndBindsNothing()
    {
        // `level.callback = handler` reads a LOCAL called handler. A qualified name is a function
        // reference and an unqualified one is not, which is the same line FlowTyper draws when it
        // decides what carries a FunctionTarget. Binding this would point at whatever function
        // happened to share the local's name.
        ParseResult result = Analyze(
            "function f( handler )\n"
            + "{\n"
            + "    level.callback = handler;\n"
            + "}\n");

        Assert.Empty(result.Extraction.FieldBindings);
    }

    [Fact]
    public void AValueWithNoSingleIdentityBindsNothing()
    {
        ParseResult result = Analyze(
            "function f()\n"
            + "{\n"
            + "    level.count = 1 + 2;\n"
            + "    level.name = \"scene\";\n"
            + "    level.result = compute();\n"
            + "}\n");

        Assert.Empty(result.Extraction.FieldBindings);
    }

    [Fact]
    public void TheValueSideOfAnAssignmentStillReadsItsOwnFields()
    {
        // The write flag is set across the TARGET only. `level.a = level.b` writes `a` and reads
        // `b`, and a bug that leaked the flag into the value would mark both as writes — which
        // go-to-definition would then offer as places `b` is set.
        ParseResult result = Analyze(
            "function f()\n"
            + "{\n"
            + "    level.a = level.b;\n"
            + "}\n");

        Assert.Equal(ReferenceKind.FieldWrite, Assert.Single(FieldReferences(result, "a")).Kind);
        Assert.Equal(ReferenceKind.FieldAccess, Assert.Single(FieldReferences(result, "b")).Kind);
    }

    [Fact]
    public void ANestedAssignmentKeepsItsOwnTarget()
    {
        ParseResult result = Analyze(
            "function f()\n"
            + "{\n"
            + "    level.a = ( level.b = 1 );\n"
            + "}\n");

        Assert.Equal(ReferenceKind.FieldWrite, Assert.Single(FieldReferences(result, "a")).Kind);
        Assert.Equal(ReferenceKind.FieldWrite, Assert.Single(FieldReferences(result, "b")).Kind);
    }
}
