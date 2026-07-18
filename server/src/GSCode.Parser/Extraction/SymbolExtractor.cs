using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Core.Docs;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser.Lexing;
using GSCode.Parser.Preprocessing;
using GSCode.Parser.Syntax;
using GSCode.Parser.Syntax.Ast;

namespace GSCode.Parser.Extraction;

/// <summary>
/// One walk over the syntax tree producing the file's symbol surface: namespace spans,
/// functions/classes with their contained assignments, the classified reference list,
/// and the per-file semantic diagnostics (#precache rules, ctor/dtor rules, defaults).
/// </summary>
public sealed class SymbolExtractor
{
    private readonly string _rootFilePath;
    private readonly NameTable _names;
    private readonly SourceText _text;
    private readonly ImmutableArray<Token> _rawTokens;

    private readonly ImmutableArray<NamespaceSpan>.Builder _namespaces = ImmutableArray.CreateBuilder<NamespaceSpan>();
    private readonly ImmutableArray<FunctionSymbol>.Builder _functions = ImmutableArray.CreateBuilder<FunctionSymbol>();
    private readonly ImmutableArray<ClassSymbol>.Builder _classes = ImmutableArray.CreateBuilder<ClassSymbol>();
    private readonly ImmutableArray<ReferenceEntry>.Builder _references = ImmutableArray.CreateBuilder<ReferenceEntry>();
    private readonly ImmutableArray<Diagnostic>.Builder _diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

    // Namespace state while walking (default = the file name stem).
    private string _currentNamespace;

    private SymbolExtractor(string rootFilePath, NameTable names, SourceText text, ImmutableArray<Token> rawTokens)
    {
        _rootFilePath = rootFilePath;
        _names = names;
        _text = text;
        _rawTokens = rawTokens;
        _currentNamespace = names.InternLower(Path.GetFileNameWithoutExtension(rootFilePath));
    }

    /// <summary>Extracts the symbol surface from a parsed file.</summary>
    public static ExtractionResult Extract(
        string rootFilePath,
        ParseTree tree,
        PreprocessResult preprocessed,
        ImmutableArray<Token> rawTokens,
        SourceText text,
        NameTable names)
    {
        SymbolExtractor extractor = new(rootFilePath, names, text, rawTokens);
        extractor.Run(tree, preprocessed);

        return new ExtractionResult(
            extractor._namespaces.ToImmutable(),
            extractor._functions.ToImmutable(),
            extractor._classes.ToImmutable(),
            extractor._references.ToImmutable(),
            extractor._diagnostics.ToImmutable());
    }

    private void Run(ParseTree tree, PreprocessResult preprocessed)
    {
        WalkDeclarations(tree.Root.Elements, tree.Root.Range);
        CloseNamespaceSpan(tree.Root.Range.End);

        // Macro definitions in THIS file are definitions; every invocation is a use.
        foreach ( MacroDefinition macro in preprocessed.Macros.All )
        {
            if ( macro.SourceFile is null )
            {
                SymbolKey key = new(null, _names.Intern(macro.Name), SymbolKind.Macro);
                _references.Add(new ReferenceEntry(key, macro.NameRange, ReferenceKind.Definition));
            }
        }

        foreach ( MacroInvocation invocation in preprocessed.MacroInvocations )
        {
            if ( invocation.SourceFile is null )
            {
                SymbolKey key = new(null, _names.Intern(invocation.Name), SymbolKind.Macro);
                _references.Add(new ReferenceEntry(key, invocation.Range, ReferenceKind.MacroUse));
            }
        }
    }

    // --- Namespace span bookkeeping ---

    private TextRange _currentNamespaceNameRange = TextRange.Empty;
    private Position _currentNamespaceStart = Position.Zero;

    private void CloseNamespaceSpan(Position end)
    {
        TextRange governed = new(_currentNamespaceStart, end);
        _namespaces.Add(new NamespaceSpan(_currentNamespace, _currentNamespace, _currentNamespaceNameRange, governed));
    }

    // --- Declaration walk ---

    private void WalkDeclarations(ImmutableArray<AstNode> elements, TextRange containerRange)
    {
        foreach ( AstNode element in elements )
        {
            switch ( element )
            {
                case NamespaceNode namespaceNode:
                {
                    CloseNamespaceSpan(namespaceNode.Range.Start);
                    _currentNamespace = _names.InternLower(namespaceNode.NameToken.Text);
                    _currentNamespaceNameRange = namespaceNode.NameToken.RootRange;
                    _currentNamespaceStart = namespaceNode.Range.Start;
                    continue;
                }
                case FunctionNode function:
                    _functions.Add(ExtractFunction(function, _currentNamespace));
                    continue;
                case ClassNode classNode:
                    ExtractClass(classNode);
                    continue;
                case PrecacheNode precache:
                    ValidatePrecache(precache);
                    continue;
                case DevBlockDeclNode devBlock:
                    WalkDeclarations(devBlock.Declarations, devBlock.Range);
                    continue;
                default:
                    continue;
            }
        }
    }

    private FunctionSymbol ExtractFunction(FunctionNode function, string namespaceName)
    {
        SymbolKey key = new(namespaceName, _names.InternLower(function.NameToken.Text), SymbolKind.Function);
        _references.Add(new ReferenceEntry(key, function.NameToken.RootRange, ReferenceKind.Definition));

        ImmutableArray<AssignmentSymbol>.Builder assignments = ImmutableArray.CreateBuilder<AssignmentSymbol>();
        WalkStatement(function.Body, assignments);

        ImmutableArray<ParameterSymbol>.Builder parameters = ImmutableArray.CreateBuilder<ParameterSymbol>();
        foreach ( ParameterNode parameter in function.Parameters )
        {
            ValidateDefaultValue(parameter);
            string defaultText = parameter.DefaultValue is null ? "" : AstPrinter.Print(parameter.DefaultValue);
            parameters.Add(new ParameterSymbol(parameter.NameToken.Text, parameter.ByRef, defaultText));
        }

        return new FunctionSymbol
        {
            Name = function.NameToken.Text,
            KeyName = _names.InternLower(function.NameToken.Text),
            Namespace = namespaceName,
            IsPrivate = function.IsPrivate,
            IsAutoexec = function.IsAutoexec,
            Parameters = parameters.ToImmutable(),
            HasVarargs = function.HasVarargs,
            NameRange = function.NameToken.Range,
            FullRange = function.Range,
            SourceFile = function.NameToken.Provenance.SourceFile ?? "",
            Doc = FindDocComment(function.Range.Start.Line, function.NameToken.Provenance.SourceFile),
            Assignments = assignments.ToImmutable(),
        };
    }

    private void ExtractClass(ClassNode classNode)
    {
        SymbolKey classKey = new(_currentNamespace, _names.InternLower(classNode.NameToken.Text), SymbolKind.Class);
        _references.Add(new ReferenceEntry(classKey, classNode.NameToken.RootRange, ReferenceKind.Definition));

        if ( classNode.ParentToken is not null )
        {
            SymbolKey parentKey = new(null, _names.InternLower(classNode.ParentToken.Value.Text), SymbolKind.Class);
            _references.Add(new ReferenceEntry(parentKey, classNode.ParentToken.Value.RootRange, ReferenceKind.ClassUse));
        }

        ImmutableArray<MemberSymbol>.Builder members = ImmutableArray.CreateBuilder<MemberSymbol>();
        ImmutableArray<FunctionSymbol>.Builder methods = ImmutableArray.CreateBuilder<FunctionSymbol>();
        bool hasConstructor = false;
        bool hasDestructor = false;

        foreach ( AstNode member in classNode.Members )
        {
            switch ( member )
            {
                case VarDeclNode varDecl:
                    members.Add(new MemberSymbol(varDecl.NameToken.Text, _names.InternLower(varDecl.NameToken.Text), varDecl.NameToken.RootRange));
                    continue;
                case FunctionNode method:
                    // Class methods carry no namespace; the class scopes them.
                    methods.Add(ExtractFunction(method, ""));
                    continue;
                case ConstructorNode constructor:
                {
                    hasConstructor = true;
                    if ( constructor.Parameters.Length > 0 )
                    {
                        AddDiagnostic(GscDiagnosticCode.ConstructorHasParameters, constructor.KeywordToken.RootRange);
                    }

                    ImmutableArray<AssignmentSymbol>.Builder constructorAssignments = ImmutableArray.CreateBuilder<AssignmentSymbol>();
                    WalkStatement(constructor.Body, constructorAssignments);
                    continue;
                }
                case DestructorNode destructor:
                {
                    hasDestructor = true;
                    if ( destructor.Parameters.Length > 0 )
                    {
                        AddDiagnostic(GscDiagnosticCode.DestructorHasParameters, destructor.KeywordToken.RootRange);
                    }

                    WalkStatement(destructor.Body, ImmutableArray.CreateBuilder<AssignmentSymbol>());
                    continue;
                }
                default:
                    continue;
            }
        }

        _classes.Add(new ClassSymbol
        {
            Name = classNode.NameToken.Text,
            KeyName = _names.InternLower(classNode.NameToken.Text),
            Namespace = _currentNamespace,
            ParentKeyName = classNode.ParentToken is null ? null : _names.InternLower(classNode.ParentToken.Value.Text),
            Members = members.ToImmutable(),
            Methods = methods.ToImmutable(),
            HasConstructor = hasConstructor,
            HasDestructor = hasDestructor,
            NameRange = classNode.NameToken.Range,
            FullRange = classNode.Range,
            SourceFile = classNode.NameToken.Provenance.SourceFile ?? "",
        });
    }

    private void ValidateDefaultValue(ParameterNode parameter)
    {
        if ( parameter.DefaultValue is null )
        {
            return;
        }

        if ( !IsPlainValue(parameter.DefaultValue) )
        {
            AddDiagnostic(GscDiagnosticCode.NonValueDefaultParameter, parameter.DefaultValue.Range, parameter.NameToken.Text);
        }
    }

    /// <summary>The spec allows only plain values as parameter defaults: literals, vectors, negated literals.</summary>
    private static bool IsPlainValue(ExprNode expression)
    {
        switch ( expression )
        {
            case LiteralNode:
                return true;
            case VectorNode vector:
                return IsPlainValue(vector.X) && IsPlainValue(vector.Y) && IsPlainValue(vector.Z);
            case PrefixNode prefix when prefix.Operator == TokenKind.Minus:
                return IsPlainValue(prefix.Operand);
            case ParenNode paren:
                return IsPlainValue(paren.Inner);
            default:
                return false;
        }
    }

    private void ValidatePrecache(PrecacheNode precache)
    {
        // Split the raw tokens on top-level commas into value groups.
        List<List<PToken>> groups = [[]];
        foreach ( PToken token in precache.Arguments )
        {
            if ( token.Kind == TokenKind.Comma )
            {
                groups.Add([]);
                continue;
            }

            groups[^1].Add(token);
        }

        if ( groups[0].Count == 0 || groups[0][0].Kind != TokenKind.String )
        {
            AddDiagnostic(GscDiagnosticCode.UnknownPrecacheType, precache.Range, groups[0].Count == 0 ? "(missing)" : groups[0][0].Text);
            return;
        }

        string typeName = Unquote(groups[0][0].Text);
        if ( !PrecacheAssetTypes.TryGet(typeName, out PrecacheAssetType assetType) )
        {
            AddDiagnostic(GscDiagnosticCode.UnknownPrecacheType, groups[0][0].RootRange, typeName);
            return;
        }

        int valueCount = groups.Count - 1;
        if ( valueCount < assetType.MinValues || valueCount > assetType.MaxValues )
        {
            string expected = assetType.MinValues == assetType.MaxValues
                ? assetType.MinValues.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : $"{assetType.MinValues}-{assetType.MaxValues}";
            AddDiagnostic(GscDiagnosticCode.WrongPrecacheArgumentCount, precache.Range, typeName, expected, valueCount);
        }
    }

    // --- Statement/expression walk: assignments + references ---

    private void WalkStatement(AstNode statement, ImmutableArray<AssignmentSymbol>.Builder assignments)
    {
        switch ( statement )
        {
            case BlockNode block:
            {
                foreach ( AstNode child in block.Statements )
                {
                    WalkStatement(child, assignments);
                }

                return;
            }
            case IfNode ifNode:
                WalkExpression(ifNode.Condition, assignments);
                WalkStatement(ifNode.Then, assignments);
                if ( ifNode.Else is not null )
                {
                    WalkStatement(ifNode.Else, assignments);
                }

                return;
            case WhileNode whileNode:
                WalkExpression(whileNode.Condition, assignments);
                WalkStatement(whileNode.Body, assignments);
                return;
            case DoWhileNode doWhile:
                WalkStatement(doWhile.Body, assignments);
                WalkExpression(doWhile.Condition, assignments);
                return;
            case ForNode forNode:
                if ( forNode.Initializer is not null )
                {
                    WalkStatement(forNode.Initializer, assignments);
                }

                if ( forNode.Condition is not null )
                {
                    WalkExpression(forNode.Condition, assignments);
                }

                if ( forNode.Increment is not null )
                {
                    WalkStatement(forNode.Increment, assignments);
                }

                WalkStatement(forNode.Body, assignments);
                return;
            case ForeachNode foreachNode:
            {
                // Loop variables are local assignments by definition.
                if ( foreachNode.KeyToken is not null )
                {
                    AddLocalAssignment(foreachNode.KeyToken.Value, assignments);
                }

                AddLocalAssignment(foreachNode.ValueToken, assignments);
                WalkExpression(foreachNode.Collection, assignments);
                WalkStatement(foreachNode.Body, assignments);
                return;
            }
            case SwitchNode switchNode:
            {
                WalkExpression(switchNode.Subject, assignments);
                foreach ( CaseGroupNode caseGroup in switchNode.Cases )
                {
                    foreach ( ExprNode? label in caseGroup.Labels )
                    {
                        if ( label is not null )
                        {
                            WalkExpression(label, assignments);
                        }
                    }

                    foreach ( AstNode child in caseGroup.Statements )
                    {
                        WalkStatement(child, assignments);
                    }
                }

                return;
            }
            case ReturnNode returnNode:
                if ( returnNode.Value is not null )
                {
                    WalkExpression(returnNode.Value, assignments);
                }

                return;
            case WaitNode wait:
                WalkExpression(wait.Duration, assignments);
                return;
            case ConstDeclNode constDecl:
                AddLocalAssignment(constDecl.NameToken, assignments);
                WalkExpression(constDecl.Value, assignments);
                return;
            case ExprStatementNode exprStatement:
                WalkExpression(exprStatement.Expression, assignments);
                return;
            case DevBlockStmtNode devBlock:
            {
                foreach ( AstNode child in devBlock.Statements )
                {
                    WalkStatement(child, assignments);
                }

                return;
            }
            default:
                return;
        }
    }

    private void WalkExpression(ExprNode expression, ImmutableArray<AssignmentSymbol>.Builder assignments)
    {
        switch ( expression )
        {
            case AssignmentNode assignment:
                RecordAssignmentTarget(assignment.Target, assignments);
                WalkExpression(assignment.Target, assignments);
                WalkExpression(assignment.Value, assignments);
                return;
            case BinaryNode binary:
                WalkExpression(binary.Left, assignments);
                WalkExpression(binary.Right, assignments);
                return;
            case TernaryNode ternary:
                WalkExpression(ternary.Condition, assignments);
                WalkExpression(ternary.WhenTrue, assignments);
                WalkExpression(ternary.WhenFalse, assignments);
                return;
            case PrefixNode prefix:
            {
                // &foo / &ns::foo — a function reference.
                if ( prefix.Operator == TokenKind.Ampersand )
                {
                    RecordCalleeReference(prefix.Operand, ReferenceKind.AddressOf);
                    return;
                }

                WalkExpression(prefix.Operand, assignments);
                return;
            }
            case PostfixNode postfix:
                WalkExpression(postfix.Operand, assignments);
                return;
            case ParenNode paren:
                WalkExpression(paren.Inner, assignments);
                return;
            case VectorNode vector:
                WalkExpression(vector.X, assignments);
                WalkExpression(vector.Y, assignments);
                WalkExpression(vector.Z, assignments);
                return;
            case MemberNode member:
                RecordFieldReference(member.NameToken);
                WalkExpression(member.Object, assignments);
                return;
            case IndexNode index:
                WalkExpression(index.Object, assignments);
                WalkExpression(index.Index, assignments);
                return;
            case PointerDerefNode pointer:
                WalkExpression(pointer.Pointer, assignments);
                return;
            case CallNode call:
            {
                if ( call.Target is not null )
                {
                    WalkExpression(call.Target, assignments);
                }

                RecordCalleeReference(call.Callee, ReferenceKind.Call);
                if ( call.Callee is PointerDerefNode derefCallee )
                {
                    WalkExpression(derefCallee.Pointer, assignments);
                }

                foreach ( ExprNode argument in call.Arguments )
                {
                    WalkExpression(argument, assignments);
                }

                return;
            }
            case ArrowCallNode arrow:
            {
                WalkExpression(arrow.Object.Pointer, assignments);
                SymbolKey methodKey = new(null, _names.InternLower(arrow.MethodToken.Text), SymbolKind.Function);
                _references.Add(new ReferenceEntry(methodKey, arrow.MethodToken.RootRange, ReferenceKind.Call));

                foreach ( ExprNode argument in arrow.Arguments )
                {
                    WalkExpression(argument, assignments);
                }

                return;
            }
            case NewNode newNode:
            {
                SymbolKey classKey = new(null, _names.InternLower(newNode.ClassToken.Text), SymbolKind.Class);
                _references.Add(new ReferenceEntry(classKey, newNode.ClassToken.RootRange, ReferenceKind.ClassUse));

                foreach ( ExprNode argument in newNode.Arguments )
                {
                    WalkExpression(argument, assignments);
                }

                return;
            }
            case LiteralNode literal:
                RecordLiteralReference(literal);
                return;
            default:
                return;
        }
    }

    private void RecordAssignmentTarget(ExprNode target, ImmutableArray<AssignmentSymbol>.Builder assignments)
    {
        switch ( target )
        {
            case IdentifierNode identifier:
                AddLocalAssignment(identifier.Token, assignments);
                return;
            case MemberNode { Object: IdentifierNode owner } member:
            {
                // self.foo = / level.x = / anyvar.field = — a tracked field write.
                string ownerName = _names.InternLower(owner.Token.Text);
                assignments.Add(new AssignmentSymbol(
                    ownerName,
                    member.NameToken.Text,
                    _names.InternLower(member.NameToken.Text),
                    member.NameToken.RootRange));
                return;
            }
            default:
                return;
        }
    }

    private void AddLocalAssignment(PToken nameToken, ImmutableArray<AssignmentSymbol>.Builder assignments)
    {
        assignments.Add(new AssignmentSymbol(
            "",
            nameToken.Text,
            _names.InternLower(nameToken.Text),
            nameToken.RootRange));
    }

    private void RecordCalleeReference(ExprNode callee, ReferenceKind kind)
    {
        switch ( callee )
        {
            case IdentifierNode identifier when identifier.Token.Kind == TokenKind.Identifier:
            {
                // Unqualified: keyed under the current namespace state (its primary
                // resolution target; builtin fallback is a query-time concern).
                SymbolKey key = new(_currentNamespace, _names.InternLower(identifier.Token.Text), SymbolKind.Function);
                _references.Add(new ReferenceEntry(key, identifier.Token.RootRange, kind));
                return;
            }
            case QualifiedNode qualified:
            {
                // sys:: is the explicit builtin qualifier — builtins are namespace-less.
                string namespaceText = _names.InternLower(qualified.NamespaceToken.Text);
                string? namespaceKey = namespaceText == "sys" ? null : namespaceText;

                SymbolKey key = new(namespaceKey, _names.InternLower(qualified.NameToken.Text), SymbolKind.Function);
                _references.Add(new ReferenceEntry(key, qualified.NameToken.RootRange, kind));
                return;
            }
            default:
                return;
        }
    }

    private void RecordFieldReference(PToken nameToken)
    {
        SymbolKey key = new(null, _names.InternLower(nameToken.Text), SymbolKind.Field);
        _references.Add(new ReferenceEntry(key, nameToken.RootRange, ReferenceKind.FieldAccess));
    }

    private void RecordLiteralReference(LiteralNode literal)
    {
        switch ( literal.Token.Kind )
        {
            case TokenKind.String:
            {
                // Strings are content-exact (case-sensitive).
                SymbolKey key = new(null, _names.Intern(Unquote(literal.Token.Text)), SymbolKind.StringLiteral);
                _references.Add(new ReferenceEntry(key, literal.Token.RootRange, ReferenceKind.Literal));
                return;
            }
            case TokenKind.HashString:
            {
                SymbolKey key = new(null, _names.InternLower(Unquote(literal.Token.Text[1..])), SymbolKind.HashString);
                _references.Add(new ReferenceEntry(key, literal.Token.RootRange, ReferenceKind.Literal));
                return;
            }
            case TokenKind.LocalizedString:
            {
                SymbolKey key = new(null, _names.InternLower(Unquote(literal.Token.Text[1..])), SymbolKind.LocalizedString);
                _references.Add(new ReferenceEntry(key, literal.Token.RootRange, ReferenceKind.Literal));
                return;
            }
            case TokenKind.AnimReference:
            {
                SymbolKey key = new(null, _names.InternLower(literal.Token.Text.AsSpan(1)), SymbolKind.AnimReference);
                _references.Add(new ReferenceEntry(key, literal.Token.RootRange, ReferenceKind.Literal));
                return;
            }
            default:
                return;
        }
    }

    // --- Doc comments ---

    /// <summary>
    /// Finds the /@ @/ block that ends within two lines above a root-file declaration.
    /// Inserted declarations get no doc association (their text is another file's).
    /// </summary>
    private ScriptDocComment FindDocComment(int declarationStartLine, string? sourceFile)
    {
        if ( sourceFile is not null )
        {
            return ScriptDocComment.None;
        }

        foreach ( Token token in _rawTokens )
        {
            if ( token.Kind != TokenKind.DocComment )
            {
                continue;
            }

            int endLine = token.Range.End.Line;
            if ( endLine >= declarationStartLine - 2 && endLine <= declarationStartLine )
            {
                return ScriptDocComment.Parse(token.GetText(_text).ToString());
            }

            if ( endLine > declarationStartLine )
            {
                break;
            }
        }

        return ScriptDocComment.None;
    }

    private static string Unquote(string text)
    {
        string trimmed = text;
        if ( trimmed.StartsWith('"') )
        {
            trimmed = trimmed[1..];
        }

        if ( trimmed.EndsWith('"') )
        {
            trimmed = trimmed[..^1];
        }

        return trimmed;
    }

    private void AddDiagnostic(GscDiagnosticCode code, TextRange range, params object[] arguments)
    {
        _diagnostics.Add(Diagnostic.Create(range, DiagnosticSeverity.Error, code, arguments));
    }
}
