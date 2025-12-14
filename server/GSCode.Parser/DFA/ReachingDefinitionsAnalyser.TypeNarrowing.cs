using System;
using System.Collections.Generic;
using GSCode.Data;
using GSCode.Parser.AST;
using GSCode.Parser.Lexical;
using GSCode.Parser.SPA;
using GSCode.Parser.SPA.Logic.Components;

namespace GSCode.Parser.DFA;

internal ref partial struct ReachingDefinitionsAnalyser
{
    private static bool IsLogicalBinaryOperator(TokenType op) => op is TokenType.And or TokenType.Or;

    /// <summary>
    /// Describes how an expression being true/false narrows types.
    /// We keep this separate from <see cref="ScrData"/> so we don't have to refactor all expression analysis at once.
    /// </summary>
    private readonly record struct ConditionResult(
        ScrData Value,
        Dictionary<string, TypeNarrowing> WhenTrue,
        Dictionary<string, TypeNarrowing> WhenFalse);

    /// <summary>
    /// Per-symbol narrowing operation represented as bitmasks over <see cref="ScrDataTypes"/>.
    /// Intended meaning: newType = (oldType &amp; KeepMask) &amp; ~RemoveMask (clamped to valid bits).
    /// </summary>
    private readonly record struct TypeNarrowing(ScrDataTypes KeepMask, ScrDataTypes RemoveMask);

    private ScrData AnalyseLogicalBinaryExpr(BinaryExprNode binary, SymbolTable symbolTable)
    {
        // Logical operators are treated as condition expressions.
        // This enables short-circuit-aware narrowing for RHS analysis without having to refactor
        // AnalyseExpr(...) to return facts for all expressions.
        return AnalyseCondition(binary, symbolTable).Value;
    }

    private enum PredicateKind
    {
        // Future expansion points:
        // - IsFunctionPtr
        // - IsString / IsInt / etc.
        // - IsDefined on fields / paths
        IsDefined
    }

    private readonly record struct PredicateCall(PredicateKind Kind, ExprNode? Subject);

    private ConditionResult AnalyseCondition(ExprNode expr, SymbolTable symbolTable)
    {
        switch (expr)
        {
            case PrefixExprNode prefix when prefix.Operation == TokenType.Not:
                {
                    ConditionResult operand = AnalyseCondition(prefix.Operand, symbolTable);
                    bool? truthy = operand.Value.IsTruthy();

                    ScrData notValue = truthy is null
                        ? new ScrData(ScrDataTypes.Bool)
                        : new ScrData(ScrDataTypes.Bool, !truthy.Value);

                    return new ConditionResult(
                        notValue,
                        WhenTrue: operand.WhenFalse,
                        WhenFalse: operand.WhenTrue);
                }

            case BinaryExprNode binary when binary.Operation == TokenType.And:
                return AnalyseConditionAnd(binary, symbolTable);

            case BinaryExprNode binary when binary.Operation == TokenType.Or:
                return AnalyseConditionOr(binary, symbolTable);

            case FunCallNode call when TryExtractPredicateCall(call, out PredicateCall predicate):
                return AnalysePredicateCall(call, predicate, symbolTable);

            default:
                {
                    // Not a condition we can infer from yet. Still analyze it normally.
                    ScrData value = AnalyseExpr(expr, symbolTable, Sense);
                    return new ConditionResult(
                        value,
                        WhenTrue: CreateEmptyNarrowings(),
                        WhenFalse: CreateEmptyNarrowings());
                }
        }
    }

    private ConditionResult AnalyseConditionAnd(BinaryExprNode andExpr, SymbolTable symbolTable)
    {
        ConditionResult left = AnalyseCondition(andExpr.Left!, symbolTable);

        // Short-circuit: RHS is only evaluated if LHS is true.
        SymbolTable rhsTable = CreateRefinedSymbolTable(symbolTable, left.WhenTrue);
        ConditionResult right = AnalyseCondition(andExpr.Right!, rhsTable);

        ScrData value = AnalyseAndOp(andExpr, left.Value, right.Value);

        // Definite facts:
        // - if (A && B) is true => A is true and B is true
        // - if (A && B) is false => ambiguous (could be A false OR A true and B false), so keep empty for now
        Dictionary<string, TypeNarrowing> whenTrue = MergeNarrowings(left.WhenTrue, right.WhenTrue);

        return new ConditionResult(
            value,
            WhenTrue: whenTrue,
            WhenFalse: CreateEmptyNarrowings());
    }

    private ConditionResult AnalyseConditionOr(BinaryExprNode orExpr, SymbolTable symbolTable)
    {
        ConditionResult left = AnalyseCondition(orExpr.Left!, symbolTable);

        // Short-circuit: RHS is only evaluated if LHS is false.
        SymbolTable rhsTable = CreateRefinedSymbolTable(symbolTable, left.WhenFalse);
        ConditionResult right = AnalyseCondition(orExpr.Right!, rhsTable);

        ScrData value = AnalyseOrOp(orExpr, left.Value, right.Value);

        // Definite facts:
        // - if (A || B) is false => A is false and B is false
        // - if (A || B) is true => ambiguous, so keep empty for now
        Dictionary<string, TypeNarrowing> whenFalse = MergeNarrowings(left.WhenFalse, right.WhenFalse);

        return new ConditionResult(
            value,
            WhenTrue: CreateEmptyNarrowings(),
            WhenFalse: whenFalse);
    }

    private static Dictionary<string, TypeNarrowing> CreateEmptyNarrowings()
    {
        return new Dictionary<string, TypeNarrowing>(StringComparer.OrdinalIgnoreCase);
    }

    private static TypeNarrowing Compose(TypeNarrowing a, TypeNarrowing b)
    {
        return new TypeNarrowing(a.KeepMask & b.KeepMask, a.RemoveMask | b.RemoveMask);
    }

    private static Dictionary<string, TypeNarrowing> MergeNarrowings(
        Dictionary<string, TypeNarrowing> a,
        Dictionary<string, TypeNarrowing> b)
    {
        if (a.Count == 0 && b.Count == 0)
        {
            return CreateEmptyNarrowings();
        }
        if (a.Count == 0)
        {
            return new Dictionary<string, TypeNarrowing>(b, StringComparer.OrdinalIgnoreCase);
        }
        if (b.Count == 0)
        {
            return new Dictionary<string, TypeNarrowing>(a, StringComparer.OrdinalIgnoreCase);
        }

        Dictionary<string, TypeNarrowing> merged = new Dictionary<string, TypeNarrowing>(a, StringComparer.OrdinalIgnoreCase);
        foreach ((string key, TypeNarrowing narrowing) in b)
        {
            if (merged.TryGetValue(key, out TypeNarrowing existing))
            {
                merged[key] = Compose(existing, narrowing);
            }
            else
            {
                merged[key] = narrowing;
            }
        }
        return merged;
    }

    private static ScrDataTypes Apply(TypeNarrowing narrowing, ScrDataTypes originalType)
    {
        // Clamp ~ to the valid type universe (Any == all valid bits except Error).
        ScrDataTypes universe = ScrDataTypes.Any;
        ScrDataTypes keep = narrowing.KeepMask & universe;
        ScrDataTypes remove = narrowing.RemoveMask & universe;

        return (originalType & keep) & (universe & ~remove);
    }

    private SymbolTable CreateRefinedSymbolTable(SymbolTable baseTable, Dictionary<string, TypeNarrowing> narrowings)
    {
        if (narrowings.Count == 0)
        {
            return baseTable;
        }

        Dictionary<string, ScrVariable> refinedEnv = new(baseTable.VariableSymbols, StringComparer.OrdinalIgnoreCase);

        foreach ((string symbol, TypeNarrowing narrowing) in narrowings)
        {
            if (!refinedEnv.TryGetValue(symbol, out ScrVariable? existing) || existing is null)
            {
                continue;
            }

            ScrDataTypes newType = Apply(narrowing, existing.Data.Type);
            refinedEnv[symbol] = existing with { Data = existing.Data with { Type = newType } };
        }

        return new SymbolTable(
            baseTable.GlobalSymbolTable,
            refinedEnv,
            baseTable.LexicalScope,
            ApiData,
            baseTable.CurrentClass,
            baseTable.CurrentNamespace,
            baseTable.KnownNamespaces);
    }

    private static bool TryExtractPredicateCall(FunCallNode call, out PredicateCall predicate)
    {
        predicate = default;

        if (call.Function is not IdentifierExprNode identifier)
        {
            return false;
        }

        // NOTE: Keep this shape extensible. Add more predicate functions here over time.
        PredicateKind? kind = identifier.Identifier.Equals("isdefined", StringComparison.OrdinalIgnoreCase)
            ? PredicateKind.IsDefined
            : null;

        if (kind is null)
        {
            return false;
        }

        // For now we only support single-subject predicates.
        if (call.Arguments.Arguments.Count != 1)
        {
            return false;
        }

        predicate = new PredicateCall(kind.Value, call.Arguments.Arguments.First?.Value);
        return true;
    }

    private ConditionResult AnalysePredicateCall(FunCallNode call, PredicateCall predicate, SymbolTable symbolTable)
    {
        // Preserve side effects / symbol usage analysis of arguments.
        foreach (ExprNode? argument in call.Arguments.Arguments)
        {
            if (argument is null)
            {
                continue;
            }
            AnalyseExpr(argument, symbolTable, Sense);
        }

        // IsDefined(...) always produces a boolean.
        ScrData value = new(ScrDataTypes.Bool);

        Dictionary<string, TypeNarrowing> whenTrue = CreateEmptyNarrowings();
        Dictionary<string, TypeNarrowing> whenFalse = CreateEmptyNarrowings();

        switch (predicate.Kind)
        {
            case PredicateKind.IsDefined:
                {
                    // Currently only support IsDefined(<identifier>) for narrowing.
                    // Future: allow member paths (e.g. IsDefined(foo.bar)) by introducing a path key type.
                    if (predicate.Subject is IdentifierExprNode id)
                    {
                        // True: subject is not undefined
                        whenTrue[id.Identifier] = new TypeNarrowing(ScrDataTypes.Any, ScrDataTypes.Undefined);
                        // False: subject is undefined
                        whenFalse[id.Identifier] = new TypeNarrowing(ScrDataTypes.Undefined, 0);
                    }
                    break;
                }
        }

        return new ConditionResult(value, whenTrue, whenFalse);
    }
}

