using GSCode.Data;
using GSCode.Lexer.Types;
using GSCode.Parser.Data;
using GSCode.Parser.DFA;
using GSCode.Parser.SPA;
using GSCode.Parser.SPA.Logic.Analysers;
using GSCode.Parser.SPA.Logic.Components;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GSCode.Parser.AST.Expressions;

/// <summary>
/// Defines the behaviour of dividing an expression when multiple operators of the same precedence occur within it
/// Left to right: (2 + 3) + 4 (Forward)
/// Right to left: 2 + (3 + 4) (Backward)
/// </summary>
internal enum OperatorAssociativity
{
    LeftToRight,
    RightToLeft,
}

/// <summary>
/// Defines all the possible operation types in GSC expressions.
/// </summary>
internal enum OperatorOps
{
    ScopeResolution,
    CalledOnEntity,
    FunctionCall,
    ThreadedFunctionCall,
    Subscript,
    MethodAccess,
    MemberAccess,
    Comma,
    AddressOf,
    AssignBitLeftShift,
    AssignBitRightShift,
    AssignBitAnd,
    AssignBitOr,
    AssignBitXor,
    AssignDivide,
    AssignSubtract,
    AssignRemainder,
    AssignMultiply,
    AssignPlus,
    BitLeftShift,
    BitRightShift,
    PostDecrement,
    PostIncrement,
    PreDecrement,
    PreIncrement,
    Equal,
    NotEqual,
    GreaterThanOrEqual,
    LessThanOrEqual,
    GreaterThan,
    LessThan,
    NotTypeEquals,
    TypeEquals,
    And,
    Or,
    Assign,
    BitAnd,
    BitNot,
    BitOr,
    Divide,
    Minus,
    Remainder,
    Multiply,
    Not,
    Plus,
    BitXor,
    Ternary,
    TernaryElse,
    Negation,
    UnaryPlus,
    NewObject
}

/// <summary>
/// Maps operation groups to their precedences. A lower number is a higher precedence (and so will be at the bottom of the AST).
/// </summary>
internal enum OperatorPrecedences
{
    ScopeResolution,
    Postfix,
    Prefix,
    MulDivRem,
    Additive,
    BitShifts,
    Relational,
    Equalities,
    Bitwise,
    Logical,
    Ternary,
    Assignment,
    Comma
}

internal record class OperatorCategory(OperatorAssociativity Associativity, OperatorPrecedences Precedence, List<IOperationFactory> OperationFactories);
internal record class OperationDef(OperatorOps Operation, IOperationFactory Factory);

/// <summary>
/// Stores all GSC operator definitions for AST & their associated analysers for SPA.
/// </summary>
internal static class OperatorData
{
    public static List<OperatorCategory> OperationPrecedencesList { get; } = new()
        {
            {
                new(OperatorAssociativity.LeftToRight, OperatorPrecedences.ScopeResolution, new()
                {
                    new BinaryOperationFactory(OperatorTypes.ScopeResolution, OperatorOps.ScopeResolution,
                        (OperationNode node, SymbolTable symbolTable, ParserIntelliSense sense, ScrData? lhsContext) =>
                        {
                            // TODO: Send the LHS as context to the right. Error if LHS is not a token node.
                            // Function call will make check on the namespace, then add a deferred symbol check
                            ScrData left = ExpressionAnalyzer.AnalyseNode(node.Left!, symbolTable, sense);
                            ScrData right = ExpressionAnalyzer.AnalyseNode(node.Right!, symbolTable, sense);

                            return ScrData.Default;
                        }),
                })
            },
            {
                new(OperatorAssociativity.LeftToRight, OperatorPrecedences.Postfix, new()
                {
                    new PostfixOperationFactory(OperatorTypes.Increment, OperatorOps.PostIncrement),
                    new PostfixOperationFactory(OperatorTypes.Decrement, OperatorOps.PostDecrement),
                    new EnclosedAccessFactory(OperatorOps.FunctionCall, EnclosureType.Parenthesis,
                        (OperationNode node, SymbolTable symbolTable, ParserIntelliSense sense, ScrData? lhsContext) =>
                        {
                            ScrData left = ExpressionAnalyzer.AnalyseNode(node.Left!, symbolTable, sense);
                            ScrData right = ExpressionAnalyzer.AnalyseNode(node.Right!, symbolTable, sense);

                            return ScrData.Default;
                        }),
                    new EnclosedAccessFactory(OperatorOps.Subscript, EnclosureType.Bracket,
                        (OperationNode node, SymbolTable symbolTable, ParserIntelliSense sense, ScrData? lhsContext) =>
                        {
                            ScrData left = ExpressionAnalyzer.AnalyseNode(node.Left!, symbolTable, sense, lhsContext);

                            if(left.TypeUnknown())
                            {
                                return ScrData.Default;
                            }

                            // Checks: Left is an array or map, otherwise error for invalid [] usage
                            // Check RHS provides a value of string or int type, otherwise error for invalid [] usage
                            ScrData right = ExpressionAnalyzer.AnalyseNode(node.Right!, symbolTable, sense, left);

                            if(right.TypeUnknown())
                            {
                                return ScrData.Default;
                            }

                            if(right.Type != ScrDataTypes.Int && right.Type != ScrDataTypes.String)
                            {
                                // ERROR: Cannot use ... as an indexer
                                sense.AddSpaDiagnostic(node.Right!.Range, GSCErrorCodes.CannotUseAsIndexer, right.TypeToString());
                                return ScrData.Default;
                            }

                            // Otherwise this is the lowest level so we produce a reference here

                            // Not currently implemented in any capacity
                            return ScrData.Default;
                        }),
                    new BinaryOperationFactory(OperatorTypes.MemberAccess, OperatorOps.MemberAccess,
                        (OperationNode node, SymbolTable symbolTable, ParserIntelliSense sense, ScrData? lhsContext) =>
                        {
                            ScrData left = ExpressionAnalyzer.AnalyseNode(node.Left!, symbolTable, sense, lhsContext);

                            if(left.TypeUnknown())
                            {
                                return ScrData.Default;
                            }

                            // Checks: Left is an object, struct, entity, otherwise error for invalid . usage
                            // Check RHS is a TokenNode, or OperationNode of type function call, subscript.
                            // No method access as methods are called as [[ ]] -> ... (not syntatically valid here)
                            // Function calls of form foo.bar() are not valid in GSC - it's either [[ foo.bar ]] () or [[foo]]->bar()
                            // array has property size, string has property length or siize whatever it is in gsc
                            ScrData right = ScrData.Default;

                            if((lhsContext is null || (node.Left is TokenNode lhsTokenNode && lhsTokenNode.NodeType == ExpressionNodeType.Field)) &&
                                node.Right is TokenNode rhsTokenNode && rhsTokenNode.NodeType == ExpressionNodeType.Field ||
                                node.Right is OperationNode rhsOperationNode && rhsOperationNode.Operation == OperatorOps.Subscript)
                            {
                                // TODO: need to check that it's a type that supports membership
                                // sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(node.Left!.Range, DiagnosticSources.SPA,
                                    //GSCErrorCodes.DoesNotContainMember, node.Right!.SourceToken.Contents, left.TypeToString()));
                                right = ExpressionAnalyzer.AnalyseNode(node.Right!, symbolTable, sense, left);
                            }
                            else
                            {
                                // Identifier expected
                                sense.AddSpaDiagnostic(node.Right!.Range, GSCErrorCodes.IdentifierExpected);
                            }

                            return right;
                        }),
                    new BinaryOperationFactory(OperatorTypes.MethodAccess, OperatorOps.MethodAccess),
                    new KeywordOperationFactory(KeywordTypes.Thread, OperatorOps.ThreadedFunctionCall,
                        (OperationNode node, SymbolTable symbolTable, ParserIntelliSense sense, ScrData? lhsContext) =>
                        {
                            ScrData right = ExpressionAnalyzer.AnalyseNode(node.Right!, symbolTable, sense, lhsContext);

                            return new ScrData(ScrDataTypes.Undefined);
                        }),
                    new KeywordOperationFactory(KeywordTypes.New, OperatorOps.NewObject,
                        (OperationNode node, SymbolTable symbolTable, ParserIntelliSense sense, ScrData? lhsContext) =>
                        {
                            ScrData right = ExpressionAnalyzer.AnalyseNode(node.Right!, symbolTable, sense, lhsContext);

                            // TODO: in a future implementation, this will instantiate the relevant object type.
                            // ... is the right a function call?
                            return new ScrData(ScrDataTypes.Object);
                        }),
                    new CalledOnFactory(
                        (OperationNode node, SymbolTable symbolTable, ParserIntelliSense sense, ScrData? lhsContext) =>
                        {
                            ScrData left = ExpressionAnalyzer.AnalyseNode(node.Left!, symbolTable, sense, lhsContext);

                            ScrData right = ExpressionAnalyzer.AnalyseNode(node.Right!, symbolTable, sense, lhsContext);

                            return right;
                        }),
                })
            },
            {
                new(OperatorAssociativity.RightToLeft, OperatorPrecedences.Prefix, new()
                {
                    new PrefixOperationFactory(OperatorTypes.Increment, OperatorOps.PreIncrement,
                        (OperationNode node, SymbolTable symbolTable, ParserIntelliSense sense, ScrData? lhsContext) =>
                        {
                            ScrData value = ExpressionAnalyzer.AnalyseNode(node.Right!, symbolTable, sense, default);

                            if (!value.TypeUnknown() && (value.Type != ScrDataTypes.Int && value.Type != ScrDataTypes.Float))
                            {
                                // ERROR: Operator '++' cannot be applied on operand of type ...
                                sense.AddSpaDiagnostic(node.Range, GSCErrorCodes.OperatorNotSupportedOn, "++", value.TypeToString());

                                return ScrData.Default;
                            }

                            if (!value.ValueUnknown())
                            {
                                if (value.Type == ScrDataTypes.Int)
                                {
                                    return new ScrData(ScrDataTypes.Int, value.Get<int>() + 1);
                                }
                                else // Float
                                {
                                    return new ScrData(ScrDataTypes.Float, value.Get<double>() + 1.0);
                                }
                            }

                            return new ScrData(value.Type);
                        }),
                    new PrefixOperationFactory(OperatorTypes.Decrement, OperatorOps.PreDecrement,
                        (OperationNode node, SymbolTable symbolTable, ParserIntelliSense sense, ScrData? lhsContext) =>
                        {
                            ScrData value = ExpressionAnalyzer.AnalyseNode(node.Right!, symbolTable, sense, default);

                            if (!value.TypeUnknown() && (value.Type != ScrDataTypes.Int && value.Type != ScrDataTypes.Float))
                            {
                                // ERROR: Operator '--' cannot be applied on operand of type ...
                                sense.AddSpaDiagnostic(node.Range, GSCErrorCodes.OperatorNotSupportedOn, "--", value.TypeToString());
                                return ScrData.Default;
                            }

                            if (!value.ValueUnknown())
                            {
                                if (value.Type == ScrDataTypes.Int)
                                {
                                    return new ScrData(ScrDataTypes.Int, value.Get<int>() - 1);
                                }
                                else // Float
                                {
                                    return new ScrData(ScrDataTypes.Float, value.Get<float>() - 1.0);
                                }
                            }

                            return new ScrData(value.Type);
                        }),
                    new PrefixOperationFactory(OperatorTypes.Not, OperatorOps.Not,
                        (OperationNode node, SymbolTable symbolTable, ParserIntelliSense sense, ScrData? lhsContext) =>
                        {
                            ScrData nodeValue = ExpressionAnalyzer.AnalyseNode(node.Right!, symbolTable, sense, default);

                            if(nodeValue.ValueUnknown())
                            {
                                return new ScrData(ScrDataTypes.Bool);
                            }

                            if(nodeValue.Type != ScrDataTypes.Bool)
                            {
                                return ScrData.Default;
                            }


                            return new ScrData(ScrDataTypes.Bool, !(bool)nodeValue.Value!);
                        }),
                    new PrefixOperationFactory(OperatorTypes.BitwiseNot, OperatorOps.BitNot),
                    new StrictPrefixOperationFactory(OperatorTypes.Ampersand, OperatorOps.AddressOf),
                    new StrictPrefixOperationFactory(OperatorTypes.Minus, OperatorOps.Negation,
                        (OperationNode node, SymbolTable symbolTable, ParserIntelliSense sense, ScrData? lhsContext) =>
                        {
                            ScrData value = ExpressionAnalyzer.AnalyseNode(node.Right!, symbolTable, sense, default);

                            if (!value.TypeUnknown() &&
                                (value.Type != ScrDataTypes.Int && value.Type != ScrDataTypes.Float && value.Type != ScrDataTypes.Vec3))
                            {
                                // ERROR: Operator '-' cannot be applied on operand of type ...
                                sense.AddSpaDiagnostic(node.Range, GSCErrorCodes.OperatorNotSupportedOn, "-", value.TypeToString());
                                return ScrData.Default;
                            }

                            if (!value.ValueUnknown())
                            {
                                if (value.Type == ScrDataTypes.Int)
                                {
                                    return new ScrData(ScrDataTypes.Int, -value.Get<int>());
                                }
                                else if(value.Type == ScrDataTypes.Float) // Float
                                {
                                    return new ScrData(ScrDataTypes.Float, -value.Get<float>());
                                }
                                else if(value.Type == ScrDataTypes.Vec3)
                                {
                                    // todo
                                    return ScrData.Default;
                                }
                            }

                            return new ScrData(value.Type);
                        }),
                    new StrictPrefixOperationFactory(OperatorTypes.Plus, OperatorOps.UnaryPlus,
                        (OperationNode node, SymbolTable symbolTable, ParserIntelliSense sense, ScrData? lhsContext) =>
                        {
                            ScrData value = ExpressionAnalyzer.AnalyseNode(node.Right!, symbolTable, sense, default);

                            if (!value.TypeUnknown() &&
                                (value.Type != ScrDataTypes.Int && value.Type != ScrDataTypes.Float && value.Type != ScrDataTypes.Vec3))
                            {
                                // ERROR: Operator '+' cannot be applied on operand of type ...
                                sense.AddSpaDiagnostic(node.Range, GSCErrorCodes.OperatorNotSupportedOn, "+", value.TypeToString());
                                return ScrData.Default;
                            }

                            if (!value.TypeUnknown())
                            {
                                // For unary plus, we simply return the value as it is because it doesn't change
                                return new ScrData(value.Type, value.Value);
                            }

                            return new ScrData(value.Type);
                        }),
                })
            },
            {
                new(OperatorAssociativity.LeftToRight, OperatorPrecedences.MulDivRem, new()
                {
                    new BinaryOperationFactory(OperatorTypes.Divide, OperatorOps.Divide,
                        (OperationNode node, SymbolTable symbolTable, ParserIntelliSense sense, ScrData? lhsContext) =>
                        {
                            ScrData lhsValue = ExpressionAnalyzer.AnalyseNode(node.Left!, symbolTable, sense, default);
                            ScrData rhsValue = ExpressionAnalyzer.AnalyseNode(node.Right!, symbolTable, sense, default);

                            if (!lhsValue.TypeUnknown() && !rhsValue.TypeUnknown() &&
                                ((lhsValue.Type != ScrDataTypes.Int && lhsValue.Type != ScrDataTypes.Float) ||
                                 (rhsValue.Type != ScrDataTypes.Int && rhsValue.Type != ScrDataTypes.Float)))
                            {
                                // ERROR: Operator '/' cannot be applied on operands of type ...
                                sense.AddSpaDiagnostic(node.Range, GSCErrorCodes.OperatorNotSupportedOnTypes, "/", lhsValue.TypeToString(), rhsValue.TypeToString());
                                return ScrData.Default;
                            }

                            if (!lhsValue.ValueUnknown() && !rhsValue.ValueUnknown())
                            {
                                float lhsDouble = lhsValue.Type == ScrDataTypes.Int ? lhsValue.Get<int>() : lhsValue.Get<float>();
                                float rhsDouble = rhsValue.Type == ScrDataTypes.Int ? rhsValue.Get<int>() : rhsValue.Get<float>();

                                if (rhsDouble == 0)
                                {
                                    // ERROR: Division by zero
                                    sense.AddSpaDiagnostic(node.Range, GSCErrorCodes.DivisionByZero);
                                    return ScrData.Default;
                                }

                                float result = lhsDouble / rhsDouble;

                                // If both are integers and result is a whole number, return as integer
                                if (lhsValue.Type == ScrDataTypes.Int && rhsValue.Type == ScrDataTypes.Int && Math.Abs(result % 1) <= double.Epsilon)
                                {
                                    return new ScrData(ScrDataTypes.Int, (int)result);
                                }

                                return new ScrData(ScrDataTypes.Float, result);
                            }

                            return new ScrData(
                                (lhsValue.Type == ScrDataTypes.Float || rhsValue.Type == ScrDataTypes.Float) ? ScrDataTypes.Float : ScrDataTypes.Any
                            );
                        }),
                    new BinaryOperationFactory(OperatorTypes.Multiply, OperatorOps.Multiply,
                        (OperationNode node, SymbolTable symbolTable, ParserIntelliSense sense, ScrData? lhsContext) =>
                        {
                            ScrData lhsValue = ExpressionAnalyzer.AnalyseNode(node.Left!, symbolTable, sense, default);
                            ScrData rhsValue = ExpressionAnalyzer.AnalyseNode(node.Right!, symbolTable, sense, default);

                            if (!lhsValue.TypeUnknown() && !rhsValue.TypeUnknown() &&
                                ((lhsValue.Type != ScrDataTypes.Int && lhsValue.Type != ScrDataTypes.Float) ||
                                 (rhsValue.Type != ScrDataTypes.Int && rhsValue.Type != ScrDataTypes.Float)))
                            {
                                // ERROR: Operator '*' cannot be applied on operands of type ...
                                sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(node.Range, DiagnosticSources.SPA, GSCErrorCodes.OperatorNotSupportedOnTypes, "*", lhsValue.TypeToString(), rhsValue.TypeToString()));
                                return ScrData.Default;
                            }

                            if (!lhsValue.ValueUnknown() && !rhsValue.ValueUnknown())
                            {
                                if (lhsValue.Type == ScrDataTypes.Float || rhsValue.Type == ScrDataTypes.Float)
                                {
                                    return new ScrData(ScrDataTypes.Float, lhsValue.GetNumericValue() * rhsValue.GetNumericValue());
                                }
                                else if (lhsValue.Type == ScrDataTypes.Int && rhsValue.Type == ScrDataTypes.Int)
                                {
                                    return new ScrData(ScrDataTypes.Int, lhsValue.Get<int>() * rhsValue.Get<int>());
                                }
                            }

                            return new ScrData((lhsValue.Type == ScrDataTypes.Float || rhsValue.Type == ScrDataTypes.Float) ? ScrDataTypes.Float : ScrDataTypes.Any);
                        }),
                    new BinaryOperationFactory(OperatorTypes.Remainder, OperatorOps.Remainder,
                        (OperationNode node, SymbolTable symbolTable, ParserIntelliSense sense, ScrData? lhsContext) =>
                        {
                            ScrData lhsValue = ExpressionAnalyzer.AnalyseNode(node.Left!, symbolTable, sense, default);
                            ScrData rhsValue = ExpressionAnalyzer.AnalyseNode(node.Right!, symbolTable, sense, default);

                            if((!lhsValue.TypeUnknown() && lhsValue.Type != ScrDataTypes.Int) ||
                                (!rhsValue.TypeUnknown() && rhsValue.Type != ScrDataTypes.Int))
                            {
                                // ERROR: Operator '%' cannot be applied on operands of type ...
                                sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(node.Range, DiagnosticSources.SPA, GSCErrorCodes.OperatorNotSupportedOnTypes, "%", lhsValue.TypeToString(), rhsValue.TypeToString()));
                                return ScrData.Default;
                            }

                            if(!lhsValue.ValueUnknown() && !rhsValue.ValueUnknown())
                            {
                                return new ScrData(ScrDataTypes.Int, lhsValue.Get<int>() % rhsValue.Get<int>());
                            }

                            return new ScrData(ScrDataTypes.Int);
                        }),
                })
            },
            {
                new(OperatorAssociativity.LeftToRight, OperatorPrecedences.Additive, new()
                {
                    new BinaryOperationFactory(OperatorTypes.Plus, OperatorOps.Plus,
                        (OperationNode node, SymbolTable symbolTable, ParserIntelliSense sense, ScrData? lhsContext) =>
                        {
                            ScrData lhsValue = ExpressionAnalyzer.AnalyseNode(node.Left!, symbolTable, sense, default);
                            ScrData rhsValue = ExpressionAnalyzer.AnalyseNode(node.Right!, symbolTable, sense, default);

                            if((!lhsValue.TypeUnknown() && (lhsValue.Type != ScrDataTypes.Int && lhsValue.Type != ScrDataTypes.Float && lhsValue.Type != ScrDataTypes.Vec3 && lhsValue.Type != ScrDataTypes.String)) ||
                                (!rhsValue.TypeUnknown() && (rhsValue.Type != ScrDataTypes.Int && rhsValue.Type != ScrDataTypes.Float && rhsValue.Type != ScrDataTypes.Vec3 && rhsValue.Type != ScrDataTypes.String)))
                            {
                                // ERROR: Operator '+' cannot be applied on operands of type ...
                                sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(node.Range, DiagnosticSources.SPA, GSCErrorCodes.OperatorNotSupportedOnTypes, "+", lhsValue.TypeToString(), rhsValue.TypeToString()));
                                return ScrData.Default;
                            }

                            if (!lhsValue.ValueUnknown() && !rhsValue.ValueUnknown())
                            {
                                if (lhsValue.Type == ScrDataTypes.Int && rhsValue.Type == ScrDataTypes.Int)
                                {
                                    return new ScrData (ScrDataTypes.Int, lhsValue.Get<int>() + rhsValue.Get<int>());
                                }
                                else if (lhsValue.Type == ScrDataTypes.String || rhsValue.Type == ScrDataTypes.String)
                                {
                                    return new ScrData(ScrDataTypes.String, lhsValue.ToString() + rhsValue.ToString());
                                }
                                else if (lhsValue.Type == ScrDataTypes.Vec3 || rhsValue.Type == ScrDataTypes.Vec3)
                                {
                                    // TODO: Actual computation when Vector3d is implemented.
                                    return new ScrData (ScrDataTypes.Vec3);
                                }
                                else
                                {
                                    float lhsAsFloat = (lhsValue.Type == ScrDataTypes.Int) ? lhsValue.Get<int>() : lhsValue.Get<float>();
                                    float rhsAsFloat = (rhsValue.Type == ScrDataTypes.Int) ? rhsValue.Get<int>() : rhsValue.Get<float>();
                                    return new ScrData(ScrDataTypes.Float, lhsAsFloat + rhsAsFloat);
                                }
                            }

                            return new ScrData (ScrDataTypes.Float);
                        }),
                    new BinaryOperationFactory(OperatorTypes.Minus, OperatorOps.Minus,
                        (OperationNode node, SymbolTable symbolTable, ParserIntelliSense sense, ScrData? lhsContext) =>
                        {
                            ScrData lhsValue = ExpressionAnalyzer.AnalyseNode(node.Left!, symbolTable, sense, default);
                            ScrData rhsValue = ExpressionAnalyzer.AnalyseNode(node.Right!, symbolTable, sense, default);

                            if((!lhsValue.TypeUnknown() && (lhsValue.Type != ScrDataTypes.Int && lhsValue.Type != ScrDataTypes.Float && lhsValue.Type != ScrDataTypes.Vec3)) ||
                                (!rhsValue.TypeUnknown() && (rhsValue.Type != ScrDataTypes.Int && rhsValue.Type != ScrDataTypes.Float && rhsValue.Type != ScrDataTypes.Vec3)))
                            {
                                // ERROR: Operator '-' cannot be applied on operands of type ...
                                sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(node.Range, DiagnosticSources.SPA, GSCErrorCodes.OperatorNotSupportedOnTypes, "-", lhsValue.TypeToString(), rhsValue.TypeToString()));
                                return ScrData.Default;
                            }

                            if (!lhsValue.ValueUnknown() && !rhsValue.ValueUnknown())
                            {
                                if (lhsValue.Type == ScrDataTypes.Int && rhsValue.Type == ScrDataTypes.Int)
                                {
                                    return new ScrData (ScrDataTypes.Int, lhsValue.Get < int >() - rhsValue.Get < int >());
                                }
                                else if (lhsValue.Type == ScrDataTypes.Vec3 || rhsValue.Type == ScrDataTypes.Vec3)
                                {
                                    // TODO: Actual computation when Vector3d is implemented.
                                    return new ScrData (ScrDataTypes.Vec3);
                                }
                                else
                                {
                                    double lhsAsDouble = (lhsValue.Type == ScrDataTypes.Int) ? lhsValue.Get<int>() : lhsValue.Get<double>();
                                    double rhsAsDouble = (rhsValue.Type == ScrDataTypes.Int) ? rhsValue.Get<int>() : rhsValue.Get<double>();
                                    return new ScrData (ScrDataTypes.Float, lhsAsDouble - rhsAsDouble);
                                }
                            }

                            return new ScrData (ScrDataTypes.Float);
                        }),
                })
            },
            {
                new(OperatorAssociativity.LeftToRight, OperatorPrecedences.BitShifts, new()
                {
                    new BinaryOperationFactory(OperatorTypes.BitLeftShift, OperatorOps.BitLeftShift,
                        (OperationNode node, SymbolTable symbolTable, ParserIntelliSense sense, ScrData? lhsContext) =>
                        {
                            ScrData lhsValue = ExpressionAnalyzer.AnalyseNode(node.Left!, symbolTable, sense, default);
                            ScrData rhsValue = ExpressionAnalyzer.AnalyseNode(node.Right!, symbolTable, sense, default);

                            if((!lhsValue.TypeUnknown() && lhsValue.Type != ScrDataTypes.Int) ||
                                (!rhsValue.TypeUnknown() && rhsValue.Type != ScrDataTypes.Int))
                            {
                                // ERROR: Operator '<<' cannot be applied on operands of type ...
                                sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(node.Range, DiagnosticSources.SPA, GSCErrorCodes.OperatorNotSupportedOnTypes, "<<", lhsValue.TypeToString(), rhsValue.TypeToString()));
                                return ScrData.Default;
                            }

                            if(!lhsValue.ValueUnknown() && !rhsValue.ValueUnknown())
                            {
                                return new ScrData (ScrDataTypes.Int, lhsValue.Get < int >() <<(int) rhsValue.Get < int >());
                            }

                            return new ScrData (ScrDataTypes.Int);
                        }),
                    new BinaryOperationFactory(OperatorTypes.BitRightShift, OperatorOps.BitRightShift,
                        (OperationNode node, SymbolTable symbolTable, ParserIntelliSense sense, ScrData? lhsContext) =>
                        {
                            ScrData lhsValue = ExpressionAnalyzer.AnalyseNode(node.Left!, symbolTable, sense, default);
                            ScrData rhsValue = ExpressionAnalyzer.AnalyseNode(node.Right!, symbolTable, sense, default);

                            if((!lhsValue.TypeUnknown() && lhsValue.Type != ScrDataTypes.Int) ||
                                (!rhsValue.TypeUnknown() && rhsValue.Type != ScrDataTypes.Int))
                            {
                                // ERROR: Operator '>>' cannot be applied on operands of type ...
                                sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(node.Range, DiagnosticSources.SPA, GSCErrorCodes.OperatorNotSupportedOnTypes, ">>", lhsValue.TypeToString(), rhsValue.TypeToString()));
                                return ScrData.Default;
                            }

                            if(!lhsValue.ValueUnknown() && !rhsValue.ValueUnknown())
                            {
                                return new ScrData (ScrDataTypes.Int, lhsValue.Get < int >() >>(int) rhsValue.Get < int >());
                            }

                            return new ScrData (ScrDataTypes.Int);
                        }),
                })
            },
            {
                new(OperatorAssociativity.LeftToRight, OperatorPrecedences.Relational, new()
                {
                    new BinaryOperationFactory(OperatorTypes.GreaterThan, OperatorOps.GreaterThan,
                        (OperationNode node, SymbolTable symbolTable, ParserIntelliSense sense, ScrData? lhsContext) =>
                        {
                            ScrData lhsValue = ExpressionAnalyzer.AnalyseNode(node.Left!, symbolTable, sense, default);
                            ScrData rhsValue = ExpressionAnalyzer.AnalyseNode(node.Right!, symbolTable, sense, default);

                            if((!lhsValue.TypeUnknown() && (lhsValue.Type != ScrDataTypes.Int && lhsValue.Type != ScrDataTypes.Float)) ||
                                (!rhsValue.TypeUnknown() && (rhsValue.Type != ScrDataTypes.Int && rhsValue.Type != ScrDataTypes.Float)))
                            {
                                // ERROR: Operator '>' cannot be applied on operands of type ...
                                sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(node.Range, DiagnosticSources.SPA, GSCErrorCodes.OperatorNotSupportedOnTypes, ">", lhsValue.TypeToString(), rhsValue.TypeToString()));
                                return ScrData.Default;
                            }

                            if(!lhsValue.ValueUnknown() && !rhsValue.ValueUnknown())
                            {
                                double lhsAsDouble = (lhsValue.Type == ScrDataTypes.Int) ? lhsValue.Get<int>() : lhsValue.Get<double>();
                                double rhsAsDouble = (rhsValue.Type == ScrDataTypes.Int) ? rhsValue.Get<int>() : rhsValue.Get<double>();

                                return new ScrData (ScrDataTypes.Bool, lhsAsDouble > rhsAsDouble);
                            }

                            return new ScrData (ScrDataTypes.Bool);
                        }),
                    new BinaryOperationFactory(OperatorTypes.GreaterThanEquals, OperatorOps.GreaterThanOrEqual,
                        (OperationNode node, SymbolTable symbolTable, ParserIntelliSense sense, ScrData? lhsContext) =>
                        {
                            ScrData lhsValue = ExpressionAnalyzer.AnalyseNode(node.Left!, symbolTable, sense, default);
                            ScrData rhsValue = ExpressionAnalyzer.AnalyseNode(node.Right!, symbolTable, sense, default);

                            if((!lhsValue.TypeUnknown() && (lhsValue.Type != ScrDataTypes.Int && lhsValue.Type != ScrDataTypes.Float)) ||
                                (!rhsValue.TypeUnknown() && (rhsValue.Type != ScrDataTypes.Int && rhsValue.Type != ScrDataTypes.Float)))
                            {
                                // ERROR: Operator '>=' cannot be applied on operands of type ...
                                sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(node.Range, DiagnosticSources.SPA, GSCErrorCodes.OperatorNotSupportedOnTypes, ">=", lhsValue.TypeToString(), rhsValue.TypeToString()));
                                return ScrData.Default;
                            }

                            if(!lhsValue.ValueUnknown() && !rhsValue.ValueUnknown())
                            {
                                double lhsAsDouble = (lhsValue.Type == ScrDataTypes.Int) ? lhsValue.Get<int>() : lhsValue.Get<double>();
                                double rhsAsDouble = (rhsValue.Type == ScrDataTypes.Int) ? rhsValue.Get<int>() : rhsValue.Get<double>();

                                return new ScrData (ScrDataTypes.Bool, lhsAsDouble >= rhsAsDouble);
                            }

                            return new ScrData (ScrDataTypes.Bool);
                        }),
                    new BinaryOperationFactory(OperatorTypes.LessThan, OperatorOps.LessThan,
                        (OperationNode node, SymbolTable symbolTable, ParserIntelliSense sense, ScrData? lhsContext) =>
                        {
                            ScrData lhsValue = ExpressionAnalyzer.AnalyseNode(node.Left!, symbolTable, sense, default);
                            ScrData rhsValue = ExpressionAnalyzer.AnalyseNode(node.Right!, symbolTable, sense, default);

                            if((!lhsValue.TypeUnknown() && (lhsValue.Type != ScrDataTypes.Int && lhsValue.Type != ScrDataTypes.Float)) ||
                                (!rhsValue.TypeUnknown() && (rhsValue.Type != ScrDataTypes.Int && rhsValue.Type != ScrDataTypes.Float)))
                            {
                                // ERROR: Operator '<' cannot be applied on operands of type ...
                                sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(node.Range, DiagnosticSources.SPA, GSCErrorCodes.OperatorNotSupportedOnTypes, "<", lhsValue.TypeToString(), rhsValue.TypeToString()));
                                return ScrData.Default;
                            }

                            if(!lhsValue.ValueUnknown() && !rhsValue.ValueUnknown())
                            {
                                double lhsAsDouble = (lhsValue.Type == ScrDataTypes.Int) ? lhsValue.Get<int>() : lhsValue.Get<double>();
                                double rhsAsDouble = (rhsValue.Type == ScrDataTypes.Int) ? rhsValue.Get<int>() : rhsValue.Get<double>();

                                return new ScrData (ScrDataTypes.Bool, lhsAsDouble < rhsAsDouble);
                            }

                            return new ScrData (ScrDataTypes.Bool);
                        }),
                    new BinaryOperationFactory(OperatorTypes.LessThanEquals, OperatorOps.LessThanOrEqual,
                        (OperationNode node, SymbolTable symbolTable, ParserIntelliSense sense, ScrData? lhsContext) =>
                        {
                            ScrData lhsValue = ExpressionAnalyzer.AnalyseNode(node.Left!, symbolTable, sense, default);
                            ScrData rhsValue = ExpressionAnalyzer.AnalyseNode(node.Right!, symbolTable, sense, default);

                            if((!lhsValue.TypeUnknown() && (lhsValue.Type != ScrDataTypes.Int && lhsValue.Type != ScrDataTypes.Float)) ||
                                (!rhsValue.TypeUnknown() && (rhsValue.Type != ScrDataTypes.Int && rhsValue.Type != ScrDataTypes.Float)))
                            {
                                // ERROR: Operator '<=' cannot be applied on operands of type ...
                                sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(node.Range, DiagnosticSources.SPA, GSCErrorCodes.OperatorNotSupportedOnTypes, "<=", lhsValue.TypeToString(), rhsValue.TypeToString()));
                                return ScrData.Default;
                            }

                            if(!lhsValue.ValueUnknown() && !rhsValue.ValueUnknown())
                            {
                                double lhsAsDouble = (lhsValue.Type == ScrDataTypes.Int) ? lhsValue.Get<int>() : lhsValue.Get<double>();
                                double rhsAsDouble = (rhsValue.Type == ScrDataTypes.Int) ? rhsValue.Get<int>() : rhsValue.Get<double>();

                                return new ScrData (ScrDataTypes.Bool, lhsAsDouble <= rhsAsDouble);
                            }

                            return new ScrData (ScrDataTypes.Bool);
                        }),
                })
            },
            {
                new(OperatorAssociativity.LeftToRight, OperatorPrecedences.Equalities, new()
                {
                    new BinaryOperationFactory(OperatorTypes.Equals, OperatorOps.Equal,
                        (OperationNode node, SymbolTable symbolTable, ParserIntelliSense sense, ScrData? lhsContext) =>
                        {
                            ScrData left = ExpressionAnalyzer.AnalyseNode(node.Left!, symbolTable, sense, default);
                            ScrData right = ExpressionAnalyzer.AnalyseNode(node.Right!, symbolTable, sense, default);

                            if(!left.ValueUnknown() && !right.ValueUnknown())
                            {
                                return new ScrData(ScrDataTypes.Bool,
                                    (left.Value is not null && right.Value is not null)
                                        ? left.Value == right.Value : null);
                            }


                            return new ScrData(ScrDataTypes.Bool);
                        }),
                    new BinaryOperationFactory(OperatorTypes.NotEquals, OperatorOps.NotEqual,
                        (OperationNode node, SymbolTable symbolTable, ParserIntelliSense sense, ScrData? lhsContext) =>
                        {
                            ScrData left = ExpressionAnalyzer.AnalyseNode(node.Left!, symbolTable, sense);
                            ScrData right = ExpressionAnalyzer.AnalyseNode(node.Right!, symbolTable, sense);

                            if(!left.ValueUnknown() && !right.ValueUnknown())
                            {
                                return new ScrData(ScrDataTypes.Bool, left.Value != right.Value);
                            }


                            return new ScrData(ScrDataTypes.Bool);
                        }),
                    new BinaryOperationFactory(OperatorTypes.TypeEquals, OperatorOps.TypeEquals),
                    new BinaryOperationFactory(OperatorTypes.NotTypeEquals, OperatorOps.NotTypeEquals),
                })
            },
            {
                new(OperatorAssociativity.LeftToRight, OperatorPrecedences.Bitwise, new()
                {
                    new BinaryOperationFactory(OperatorTypes.Ampersand, OperatorOps.BitAnd,
                        (OperationNode node, SymbolTable symbolTable, ParserIntelliSense sense, ScrData? lhsContext) =>
                        {
                            ScrData left = ExpressionAnalyzer.AnalyseNode(node.Left!, symbolTable, sense);
                            ScrData right = ExpressionAnalyzer.AnalyseNode(node.Right!, symbolTable, sense);

                            if(left.Type != ScrDataTypes.Any && right.Type != ScrDataTypes.Any)
                            {
                                if(left.Type != ScrDataTypes.Int)
                                {
                                    // ERROR: Operator '&' cannot be applied to operand of type '???'.
                                    return ScrData.Default;
                                }
                                if(right.Type != ScrDataTypes.Int)
                                {
                                    // ERROR: Operator '&' cannot be applied to operand of type '???'.
                                    return ScrData.Default;
                                }

                                if(!left.ValueUnknown() && !right.ValueUnknown())
                                {
                                    return new ScrData(ScrDataTypes.Int, left.Get<int>() & right.Get<int>());
                                }

                                return new ScrData(ScrDataTypes.Int);
                            }
                            return ScrData.Default;
                        }),
                    new BinaryOperationFactory(OperatorTypes.Xor, OperatorOps.BitXor,
                        (OperationNode node, SymbolTable symbolTable, ParserIntelliSense sense, ScrData? lhsContext) =>
                        {
                            ScrData left = ExpressionAnalyzer.AnalyseNode(node.Left!, symbolTable, sense);
                            ScrData right = ExpressionAnalyzer.AnalyseNode(node.Right!, symbolTable, sense);

                            if (left.Type != ScrDataTypes.Any && right.Type != ScrDataTypes.Any)
                            {
                                if(left.Type != ScrDataTypes.Int || right.Type != ScrDataTypes.Int)
                                {
                                    // ERROR: Operator ^ cannot be applied to operands of type '??' and '??'
                                    sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(node.Right!.Range, DiagnosticSources.SPA, GSCErrorCodes.OperatorNotSupportedOnTypes, "^", left.TypeToString(), right.TypeToString()));
                                    return ScrData.Default;
                                }
                                // ERROR: The left-hand side of an arithmetic operation must be of type 'int' or 'float'.

                                if(!left.ValueUnknown() && !right.ValueUnknown())
                                {
                                    return new ScrData(ScrDataTypes.Int, left.Get<int>() ^ right.Get<int>());
                                }

                                return new ScrData(ScrDataTypes.Int);
                            }
                            return ScrData.Default;
                        }),
                    new BinaryOperationFactory(OperatorTypes.BitwiseOr, OperatorOps.BitOr,
                        (OperationNode node, SymbolTable symbolTable, ParserIntelliSense sense, ScrData? lhsContext) =>
                        {
                            ScrData left = ExpressionAnalyzer.AnalyseNode(node.Left!, symbolTable, sense);
                            ScrData right = ExpressionAnalyzer.AnalyseNode(node.Right!, symbolTable, sense);

                            if(left.Type != ScrDataTypes.Any && right.Type != ScrDataTypes.Any)
                            {
                                if(left.Type != ScrDataTypes.Int || right.Type != ScrDataTypes.Int)
                                {
                                    // ERROR: Operator | cannot be applied to operands of type '??' and '??'
                                    sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(node.Right!.Range, DiagnosticSources.SPA, GSCErrorCodes.OperatorNotSupportedOnTypes, "|", left.TypeToString(), right.TypeToString()));
                                    return ScrData.Default;
                                }

                                if(!left.ValueUnknown() && !right.ValueUnknown())
                                {
                                    return new ScrData(ScrDataTypes.Int, left.Get<int>() | right.Get<int>());
                                }

                                return new ScrData(ScrDataTypes.Int);
                            }
                            return ScrData.Default;
                        }),
                })
            },
            {
                new(OperatorAssociativity.LeftToRight, OperatorPrecedences.Logical, new()
                {
                    new BinaryOperationFactory(OperatorTypes.And, OperatorOps.And,
                        (OperationNode node, SymbolTable symbolTable, ParserIntelliSense sense, ScrData? lhsContext) =>
                        {
                            ScrData left = ExpressionAnalyzer.AnalyseNode(node.Left!, symbolTable, sense);
                            ScrData right = ExpressionAnalyzer.AnalyseNode(node.Right!, symbolTable, sense);

                            if(left.Type != ScrDataTypes.Any && right.Type != ScrDataTypes.Any)
                            {
                                if(!left.CanEvaluateToBoolean() || !right.CanEvaluateToBoolean())
                                {
                                    // ERROR: Operator '&&' cannot be applied to operands of type '??' and '??'
                                    sense.AddSpaDiagnostic(node.Right!.Range, GSCErrorCodes.OperatorNotSupportedOnTypes, "&&", left.TypeToString(), right.TypeToString());
                                    return ScrData.Default;
                                }

                                bool? leftValue = left.IsTruthy();
                                bool? rightValue = right.IsTruthy();

                                if(leftValue is bool leftBool && rightValue is bool rightBool)
                                {
                                    return new ScrData(ScrDataTypes.Bool, leftBool && rightBool);
                                }

                                return new ScrData(ScrDataTypes.Bool);
                            }
                            return ScrData.Default;
                        }),
                    new BinaryOperationFactory(OperatorTypes.Or, OperatorOps.Or,
                        (OperationNode node, SymbolTable symbolTable, ParserIntelliSense sense, ScrData? lhsContext) =>
                        {
                            ScrData left = ExpressionAnalyzer.AnalyseNode(node.Left!, symbolTable, sense);
                            ScrData right = ExpressionAnalyzer.AnalyseNode(node.Right!, symbolTable, sense);

                            if (left.Type != ScrDataTypes.Any && right.Type != ScrDataTypes.Any)
                            {
                                if (!left.CanEvaluateToBoolean() ||
                                    !right.CanEvaluateToBoolean())
                                {
                                    // ERROR: The operator '||' is not valid on operands of type '??' and '??'.
                                    sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(new Range(), DiagnosticSources.SPA, GSCErrorCodes.OperatorNotSupportedOnTypes, "||", left.TypeToString(), right.TypeToString()));
                                    return ScrData.Default;
                                }

                                bool? leftValue = left.IsTruthy();
                                bool? rightValue = right.IsTruthy();

                                if (leftValue is bool leftBool && rightValue is bool rightBool)
                                {
                                    return new ScrData(ScrDataTypes.Bool, leftBool || rightBool);
                                }

                                return new ScrData(ScrDataTypes.Bool);
                            }
                            return ScrData.Default;
                        }),
                })
            },
            {
                new(OperatorAssociativity.RightToLeft, OperatorPrecedences.Ternary, new()
                {
                    new BinaryOperationFactory(OperatorTypes.TernaryStart, OperatorOps.Ternary, (OperationNode node, SymbolTable symbolTable, ParserIntelliSense sense, ScrData? lhsContext) =>
                        {
                            ScrData left = ExpressionAnalyzer.AnalyseNode(node.Left!, symbolTable, sense);
                            ScrData right = ExpressionAnalyzer.AnalyseNode(node.Right!, symbolTable, sense);

                            if(!left.TypeUnknown() && !left.CanEvaluateToBoolean())
                            {
                                // The LHS of a ternary expression must be a 'bool'
                                //int test = 2 ? 3 : 4;
                            }

                            if(left.Type != ScrDataTypes.Any && right.Type != ScrDataTypes.Any)
                            {
                                if(!left.CanEvaluateToBoolean() || !right.CanEvaluateToBoolean())
                                {
                                    // ERROR: Operator '&&' cannot be applied to operands of type '??' and '??'
                                    sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(node.Right!.Range, DiagnosticSources.SPA, GSCErrorCodes.OperatorNotSupportedOnTypes, "&&", left.TypeToString(), right.TypeToString()));
                                    return ScrData.Default;
                                }

                                bool? leftValue = left.IsTruthy();
                                bool? rightValue = right.IsTruthy();

                                if(leftValue is bool leftBool && rightValue is bool rightBool)
                                {
                                    return new ScrData(ScrDataTypes.Bool, leftBool && rightBool);
                                }

                                return new ScrData(ScrDataTypes.Bool);
                            }
                            return ScrData.Default;
                        }),
                    new BinaryOperationFactory(OperatorTypes.Colon, OperatorOps.TernaryElse),
                })
            },
            {
                new(OperatorAssociativity.RightToLeft, OperatorPrecedences.Assignment, new()
                {
                    new BinaryOperationFactory(OperatorTypes.Assignment, OperatorOps.Assign,
                        (OperationNode node, SymbolTable symbolTable, ParserIntelliSense sense, ScrData? lhsContext) =>
                        {
                            ScrData left = ExpressionAnalyzer.AnalyseNode(node.Left!, symbolTable, sense);
                            ScrData right = ExpressionAnalyzer.AnalyseNode(node.Right!, symbolTable, sense);

                            // Assigning to a local variable
                            if(node.Left is TokenNode variableOperand)
                            {
                                string symbolName = variableOperand.SourceToken.Contents;

                                if(left.ReadOnly)
                                {
                                    sense.AddSpaDiagnostic(variableOperand.Range, GSCErrorCodes.CannotAssignToConstant, symbolName);
                                    return ScrData.Default;
                                }

                                bool isNew = symbolTable.AddOrSetSymbol(symbolName, right);

                                if(isNew && right.Type != ScrDataTypes.Undefined)
                                {
                                    sense.AddSenseToken(ScrVariableSymbol.Declaration(variableOperand, right));
                                }

                                return right;
                            }

                            // Assigning to a property on a struct
                            if(node.Left is OperationNode operationNode && operationNode.Operation == OperatorOps.MemberAccess && left.Owner is ScrStruct destination)
                            {
                                TokenNode leafNode = operationNode.FarRightTokenLeaf;
                                string propertyName = leafNode.SourceToken.Contents;

                                if(left.ReadOnly)
                                {
                                    sense.AddSpaDiagnostic(leafNode.Range, GSCErrorCodes.CannotAssignToReadOnlyProperty, propertyName);
                                    return ScrData.Default;
                                }

                                destination.Set(propertyName, right);
                                return right;
                            }

                            sense.AddSpaDiagnostic(node.Left!.Range, GSCErrorCodes.InvalidAssignmentTarget);
                            return ScrData.Default;
                        }),
                    new BinaryOperationFactory(OperatorTypes.AssignmentPlus, OperatorOps.AssignPlus),
                    new BinaryOperationFactory(OperatorTypes.AssignmentMinus, OperatorOps.AssignSubtract),
                    new BinaryOperationFactory(OperatorTypes.AssignmentMultiply, OperatorOps.AssignMultiply),
                    new BinaryOperationFactory(OperatorTypes.AssignmentDivide, OperatorOps.AssignDivide),
                    new BinaryOperationFactory(OperatorTypes.AssignmentRemainder, OperatorOps.AssignRemainder),
                    new BinaryOperationFactory(OperatorTypes.AssignmentBitwiseAnd, OperatorOps.AssignBitAnd),
                    new BinaryOperationFactory(OperatorTypes.AssignmentBitwiseOr, OperatorOps.AssignBitOr,
                        (OperationNode node, SymbolTable symbolTable, ParserIntelliSense sense, ScrData? lhsContext) =>
                        {
                            ScrData left = ExpressionAnalyzer.AnalyseNode(node.Left!, symbolTable, sense);
                            ScrData right = ExpressionAnalyzer.AnalyseNode(node.Right!, symbolTable, sense);

                            bool isLeftSymbolPathValid = false;

                            if (isLeftSymbolPathValid && left.Type == ScrDataTypes.Int && right.Type == ScrDataTypes.Int)
                            {
                                if(!left.ValueUnknown() && !right.ValueUnknown())
                                {
                                    return new ScrData (ScrDataTypes.Int, left.Get < int >() | right.Get < int >());
                                }

                                return new ScrData (ScrDataTypes.Int);
                            }

                            sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(new Range(), DiagnosticSources.SPA, GSCErrorCodes.OperatorNotSupportedOnTypes,
                                "|=", left.TypeToString(), right.TypeToString()));
                            return ScrData.Default;
                        }),
                    new BinaryOperationFactory(OperatorTypes.AssignmentBitwiseXor, OperatorOps.AssignBitXor),
                    new BinaryOperationFactory(OperatorTypes.AssignmentBitwiseLeftShift, OperatorOps.AssignBitLeftShift),
                    new BinaryOperationFactory(OperatorTypes.AssignmentBitwiseRightShift, OperatorOps.AssignBitRightShift),
                })
            },
            {
                new(OperatorAssociativity.LeftToRight, OperatorPrecedences.Comma, new()
                {
                    new BinaryOperationFactory(OperatorTypes.Comma, OperatorOps.Comma,
                        (OperationNode node, SymbolTable symbolTable, ParserIntelliSense sense, ScrData? lhsContext) =>
                        {
                            ScrData left = ExpressionAnalyzer.AnalyseNode(node.Left!, symbolTable, sense);

                            ScrArguments argumentsNode = (left as ScrArguments) ?? new ScrArguments
                            {
                                Value = left.Value,
                                Type = left.Type,
                                Arguments = new()
                                {
                                    node.Left!
                                }
                            };

                            ScrData right = ExpressionAnalyzer.AnalyseNode(node.Right!, symbolTable, sense);
                            if(right is not ScrArguments)
                            {
                                argumentsNode.Arguments.Add(node.Right!);
                            }

                            return argumentsNode;
                        }),
                })
            },
        };
    public static bool IsOperand(IExpressionNode node)
    {
        switch (node.NodeType)
        {
            case ExpressionNodeType.Enclosure: // May cause undesired behaviour, remains to be seen
            case ExpressionNodeType.Literal:
            case ExpressionNodeType.Field:
            case ExpressionNodeType.Operation:
                return true;
            default:
                return false;
        }
    }
    public static bool IsFunctionalOperand(IExpressionNode node)
    {
        switch (node.NodeType)
        {
            case ExpressionNodeType.Enclosure:
                EnclosureNode enclosureNode = (EnclosureNode)node;
                return enclosureNode.EnclosureType == EnclosureType.Dereference;
            case ExpressionNodeType.Field:
            case ExpressionNodeType.Operation:
                return true;
            default:
                return false;
        }
    }
}