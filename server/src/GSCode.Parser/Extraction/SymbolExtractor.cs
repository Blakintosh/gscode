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
    private readonly ImmutableArray<PathCallReference>.Builder _pathCalls = ImmutableArray.CreateBuilder<PathCallReference>();
    private readonly ImmutableArray<Diagnostic>.Builder _diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

    // Namespace state while walking (default = the file name stem).
    private string _currentNamespace;

    // Ranges in THIS file where a macro was invoked. An expansion's AST nodes report the
    // invocation's range, so containment identifies macro-supplied syntax.
    private readonly List<TextRange> _macroInvocations = [];

    // How many dev blocks enclose the walk right now; > 0 means release builds drop this code.
    private int _devBlockDepth;

    // Whether the walk is inside a `+` chain, where a string literal is a message fragment
    // rather than a name. Nested so an inner expression does not clear an outer concatenation.
    private bool _inStringConcatenation;

    private readonly GameProfile _profile;

    private SymbolExtractor(string rootFilePath, NameTable names, SourceText text, ImmutableArray<Token> rawTokens, GameProfile profile)
    {
        _rootFilePath = rootFilePath;
        _names = names;
        _text = text;
        _rawTokens = rawTokens;
        _profile = profile;
        _currentNamespace = names.InternLower(Path.GetFileNameWithoutExtension(rootFilePath));
    }

    /// <summary>Extracts the symbol surface from a parsed file.</summary>
    public static ExtractionResult Extract(
        string rootFilePath,
        ParseTree tree,
        PreprocessResult preprocessed,
        ImmutableArray<Token> rawTokens,
        SourceText text,
        NameTable names,
        GameProfile profile)
    {
        SymbolExtractor extractor = new(rootFilePath, names, text, rawTokens, profile);
        extractor.Run(tree, preprocessed);
        extractor.ReportDuplicateFunctions();

        return new ExtractionResult(
            extractor._namespaces.ToImmutable(),
            extractor._functions.ToImmutable(),
            extractor._classes.ToImmutable(),
            extractor._references.ToImmutable(),
            extractor._diagnostics.ToImmutable(),
            extractor._pathCalls.ToImmutable());
    }

    private void Run(ParseTree tree, PreprocessResult preprocessed)
    {
        // Collected BEFORE the walk, because default-parameter validation consults them.
        foreach ( MacroInvocation invocation in preprocessed.MacroInvocations )
        {
            if ( invocation.SourceFile is null )
            {
                _macroInvocations.Add(invocation.Range);
            }
        }

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
                    // Everything declared in here is stripped from a release build.
                    _devBlockDepth++;
                    WalkDeclarations(devBlock.Declarations, devBlock.Range);
                    _devBlockDepth--;
                    continue;
                default:
                    continue;
            }
        }
    }

    /// <summary>
    /// The namespace component of a function's reference key. On a merge dialect a function is
    /// reached by NAME across the merged scope — the file it lives in is not a namespace — so its key
    /// carries none, and a call keyed the same way resolves to it wherever it lives. Where resolution
    /// is namespace-driven (BO3) the namespace is part of the function's identity and is kept.
    /// </summary>
    private string? FunctionKeyNamespace(string namespaceName)
    {
        return _profile.ResolvesByNamespace ? namespaceName : null;
    }

    private FunctionSymbol ExtractFunction(FunctionNode function, string namespaceName)
    {
        SymbolKey key = new(FunctionKeyNamespace(namespaceName), _names.InternLower(function.NameToken.Text), SymbolKind.Function);
        AddReference(key, function.NameToken, ReferenceKind.Definition);

        ImmutableArray<AssignmentSymbol>.Builder assignments = ImmutableArray.CreateBuilder<AssignmentSymbol>();
        WalkStatement(function.Body, assignments);

        ImmutableArray<ParameterSymbol>.Builder parameters = ImmutableArray.CreateBuilder<ParameterSymbol>();
        foreach ( ParameterNode parameter in function.Parameters )
        {
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
            IsDevOnly = _devBlockDepth > 0,
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
        // NO namespace, unlike a function. A class name is global in T7: it is reached as a bare
        // `new Throttle()` or `class Derived : Throttle`, and the language has no `ns::Throttle` form
        // to qualify one with. Keying the DEFINITION under its file's namespace while every USE is
        // keyed under none — see the parent below, and NewNode in WalkExpression — meant the two
        // could never meet, so go-to-definition on `new Throttle()` found nothing and the CodeLens
        // over `class Throttle` counted no references.
        SymbolKey classKey = new(null, _names.InternLower(classNode.NameToken.Text), SymbolKind.Class);
        AddReference(classKey, classNode.NameToken, ReferenceKind.Definition);

        if ( classNode.ParentToken is not null )
        {
            SymbolKey parentKey = new(null, _names.InternLower(classNode.ParentToken.Value.Text), SymbolKind.Class);
            AddReference(parentKey, classNode.ParentToken.Value, ReferenceKind.ClassUse);
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

    /// <summary>
    /// True when the expression occupies a macro invocation's range — i.e. the preprocessor put
    /// it there. Checked by containment rather than token provenance so it holds for every node
    /// shape an expansion can produce, not just the ones that carry a token directly.
    /// </summary>
    private bool IsMacroSupplied(ExprNode expression)
    {
        foreach ( TextRange invocation in _macroInvocations )
        {
            if ( invocation.Contains(expression.Range.Start) )
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The spec allows only plain values as parameter defaults: literals, vectors, negated literals.</summary>

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

        // A real type used in the wrong world. Reported apart from "unknown" because the two call
        // for opposite responses: an unknown type is probably a typo, while this one is spelled
        // correctly and simply belongs in the other file.
        if ( !PrecacheAssetTypes.IsAvailableIn(assetType, _profile.LanguageFromPath(_rootFilePath)) )
        {
            AddDiagnostic(GscDiagnosticCode.ClientOnlyPrecacheType, groups[0][0].RootRange, typeName);
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
                    int beforeInitializer = assignments.Count;
                    WalkStatement(forNode.Initializer, assignments);
                    MarkAsLoopVariables(assignments, beforeInitializer);
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
                    AddLocalAssignment(foreachNode.KeyToken.Value, assignments, isLoopVariable: true);
                }

                AddLocalAssignment(foreachNode.ValueToken, assignments, isLoopVariable: true);
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
            {
                // A string literal spliced into a `+` chain is a message fragment, not a name, so
                // it is recorded under a kind literal completion skips. Marked here because the
                // AST is the only place the relationship is visible: by the time the reference
                // reaches the database it is just text.
                bool concatenation = binary.Operator == TokenKind.Plus;
                bool wasInConcatenation = _inStringConcatenation;
                _inStringConcatenation = wasInConcatenation || concatenation;

                WalkExpression(binary.Left, assignments);
                WalkExpression(binary.Right, assignments);

                _inStringConcatenation = wasInConcatenation;
                return;
            }
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
                AddReference(methodKey, arrow.MethodToken, ReferenceKind.Call);

                foreach ( ExprNode argument in arrow.Arguments )
                {
                    WalkExpression(argument, assignments);
                }

                return;
            }
            case NewNode newNode:
            {
                SymbolKey classKey = new(null, _names.InternLower(newNode.ClassToken.Text), SymbolKind.Class);
                AddReference(classKey, newNode.ClassToken, ReferenceKind.ClassUse);

                foreach ( ExprNode argument in newNode.Arguments )
                {
                    WalkExpression(argument, assignments);
                }

                return;
            }
            case PathQualifiedNode:
                // A bare maps\x::foo with no call — a function pointer passed as a value.
                RecordCalleeReference(expression, ReferenceKind.AddressOf);
                return;
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

    private void AddLocalAssignment(
        PToken nameToken, ImmutableArray<AssignmentSymbol>.Builder assignments, bool isLoopVariable = false)
    {
        assignments.Add(new AssignmentSymbol(
            "",
            nameToken.Text,
            _names.InternLower(nameToken.Text),
            nameToken.RootRange,
            isLoopVariable));
    }

    /// <summary>
    /// Marks everything a `for` initializer just added as a loop variable.
    ///
    /// The initializer is an ordinary statement — `i = 0` is indistinguishable from any other
    /// assignment by the time it is walked — so rather than thread a flag through the whole
    /// expression walk, the entries it contributed are rewritten afterwards. The range is known
    /// because a builder only ever appends.
    /// </summary>
    private static void MarkAsLoopVariables(ImmutableArray<AssignmentSymbol>.Builder assignments, int fromIndex)
    {
        for ( int index = fromIndex; index < assignments.Count; index++ )
        {
            assignments[index] = assignments[index] with { IsLoopVariable = true };
        }
    }

    private void RecordCalleeReference(ExprNode callee, ReferenceKind kind)
    {
        switch ( callee )
        {
            case IdentifierNode identifier when identifier.Token.Kind == TokenKind.Identifier:
            {
                // Unqualified: keyed under the current namespace state (its primary
                // resolution target; builtin fallback is a query-time concern). Under a merge
                // dialect there is no namespace, so the key drops it and the call resolves to the
                // matching definition wherever the merged scope pulled it in from.
                SymbolKey key = new(FunctionKeyNamespace(_currentNamespace), _names.InternLower(identifier.Token.Text), SymbolKind.Function);
                AddReference(key, identifier.Token, kind);
                return;
            }
            case QualifiedNode qualified:
            {
                // sys:: is the explicit builtin qualifier — builtins are namespace-less.
                string namespaceText = _names.InternLower(qualified.NamespaceToken.Text);
                string? namespaceKey = namespaceText == "sys" ? null : namespaceText;

                SymbolKey key = new(namespaceKey, _names.InternLower(qualified.NameToken.Text), SymbolKind.Function);
                AddReference(key, qualified.NameToken, kind);
                return;
            }
            case PathQualifiedNode path:
            {
                // maps\mp\_utility::foo — the Infinity Ward path form. #include MERGES the file's
                // functions into this scope, so the call resolves by NAME; the path names the
                // source file, not a namespace. Keyed like an unqualified call (null namespace) so
                // it unions for find-references; the explicit path is kept alongside so
                // go-to-definition can pin it to that one file.
                SymbolKey key = new(null, _names.InternLower(path.NameToken.Text), SymbolKind.Function);
                AddReference(key, path.NameToken, kind);

                // The leading ::foo local form has an empty path and needs no file pinning.
                if ( path.Path.Length > 0 )
                {
                    _pathCalls.Add(new PathCallReference(path.Path, path.NameToken.Range));
                }

                return;
            }
            default:
                return;
        }
    }

    private void RecordFieldReference(PToken nameToken)
    {
        SymbolKey key = new(null, _names.InternLower(nameToken.Text), SymbolKind.Field);
        AddReference(key, nameToken, ReferenceKind.FieldAccess);
    }

    private void RecordLiteralReference(LiteralNode literal)
    {
        switch ( literal.Token.Kind )
        {
            case TokenKind.String:
            {
                // Strings are content-exact (case-sensitive).
                SymbolKey key = new(null, _names.Intern(Unquote(literal.Token.Text)), SymbolKind.StringLiteral);
                AddReference(
                    key,
                    literal.Token,
                    _inStringConcatenation ? ReferenceKind.ConcatenatedLiteral : ReferenceKind.Literal);
                return;
            }
            case TokenKind.HashString:
            {
                // Case-preserving: the name is shown verbatim in completion, and lowercasing it
                // turned KILLSTREAK_COMBAT_ROBOT_CRATE into killstreak_combat_robot_crate. Safe
                // to match case-sensitively too — across the stock scripts no hash string or
                // localized string is ever written with two different casings.
                SymbolKey key = new(null, _names.Intern(Unquote(literal.Token.Text[1..])), SymbolKind.HashString);
                AddReference(key, literal.Token, ReferenceKind.Literal);
                return;
            }
            case TokenKind.LocalizedString:
            {
                SymbolKey key = new(null, _names.Intern(Unquote(literal.Token.Text[1..])), SymbolKind.LocalizedString);
                AddReference(key, literal.Token, ReferenceKind.Literal);
                return;
            }
            case TokenKind.AnimReference:
            {
                SymbolKey key = new(null, _names.InternLower(literal.Token.Text.AsSpan(1)), SymbolKind.AnimReference);
                AddReference(key, literal.Token, ReferenceKind.Literal);
                return;
            }
            default:
                return;
        }
    }

    // --- Doc comments ---

    /// <summary>
    /// Finds the doc block that ends within two lines above a root-file declaration.
    /// Inserted declarations get no doc association (their text is another file's).
    ///
    /// Which token counts depends on the dialect. BO3 has a doc comment of its own
    /// (<c>/@ … @/</c>, its own token kind), while every earlier game writes the block inside an
    /// ordinary <c>/* … */</c> comment fenced by <c>///ScriptDocBegin</c>. Only the first was ever
    /// looked for, so on CoD4, WaW, MW2 and BO1 no function had documentation at all — the profile
    /// recorded which style each game uses and nothing read it.
    /// </summary>
    private ScriptDocComment FindDocComment(int declarationStartLine, string? sourceFile)
    {
        if ( sourceFile is not null )
        {
            return ScriptDocComment.None;
        }

        foreach ( Token token in _rawTokens )
        {
            if ( !IsDocCommentToken(token) )
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

    /// <summary>
    /// Whether a token is this dialect's doc block.
    ///
    /// Under <see cref="ScriptDocStyle.TripleSlash"/> the fence is the only thing that separates
    /// documentation from an ordinary comment above a function, so it is required — without it
    /// every banner and copyright header would become the function's hover text.
    /// </summary>
    private bool IsDocCommentToken(Token token)
    {
        if ( _profile.ScriptDocStyle == ScriptDocStyle.AtSign )
        {
            return token.Kind == TokenKind.DocComment;
        }

        return token.Kind == TokenKind.BlockComment
            && ScriptDocComment.HasTripleSlashFence(token.GetText(_text));
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

    /// <summary>
    /// Records a reference at the token's root-file range, unless the token came out of a
    /// macro body.
    ///
    /// Expanded tokens report the INVOCATION's range, so recording them would stack a macro's
    /// whole body onto the one call site: go-to-definition would land on whatever the body
    /// mentions first, and every expanded call would contribute its own parameter hints there.
    /// Arguments passed at the call site keep their own provenance and so are still recorded,
    /// as is the MacroUse reference for the invocation itself.
    ///
    /// The cost is that a function named only inside a macro body gets no reference anywhere,
    /// since the body is never parsed as code at its definition site either.
    /// </summary>
    private void AddReference(SymbolKey key, PToken token, ReferenceKind kind)
    {
        // Text from a macro body still USES what it names — a file invoking REGISTER_SYSTEM
        // really does call system::register — but the cursor can never sit on it, because the
        // characters on screen spell the macro's name. Recording it under a separate kind keeps
        // the fact while leaving navigation to resolve the macro instead. RootRange is already
        // the invocation site in this file, so the range is meaningful either way.
        if ( token.Provenance.DefinitionSite is not null )
        {
            _references.Add(new ReferenceEntry(key, token.RootRange, ReferenceKind.ExpandedFromMacro));
            return;
        }

        _references.Add(new ReferenceEntry(key, token.RootRange, kind));
    }

    private void AddDiagnostic(GscDiagnosticCode code, TextRange range, params object[] arguments)
    {
        _diagnostics.Add(Diagnostic.Create(range, DiagnosticSeverity.Error, code, arguments));
    }

    /// <summary>
    /// Reports a redeclaration of the same namespace::function within one file, carrying a
    /// related location that points back at the first declaration. Only file-local functions
    /// take part: one spliced in by #insert keeps its GSH-local name coordinates, which would
    /// put the marker in the wrong place in the including file.
    /// </summary>
    private void ReportDuplicateFunctions()
    {
        Dictionary<string, FunctionSymbol> firstByKey = new(StringComparer.Ordinal);

        foreach ( FunctionSymbol function in _functions )
        {
            if ( function.SourceFile.Length > 0 )
            {
                continue;
            }

            string key = function.Namespace + "::" + function.KeyName;
            if ( !firstByKey.TryGetValue(key, out FunctionSymbol? first) )
            {
                firstByKey[key] = function;
                continue;
            }

            // The collision is a NAMESPACE one, not a file one: the key above is already
            // namespace-qualified, and two files may share a namespace, so saying "in this file"
            // described the search rather than the rule. Files that share the namespace without
            // being linked together are the cross-file lint's business, since only the importer
            // knows which of them meet.
            DiagnosticRelation relation = new(_rootFilePath, first.NameRange, "First defined here.");
            Diagnostic duplicate = Diagnostic.Create(
                function.NameRange,
                DiagnosticSeverity.Error,
                GscDiagnosticCode.DuplicateFunction,
                function.Name,
                function.Namespace);

            _diagnostics.Add(duplicate with { RelatedInformation = [relation] });
        }
    }
}
