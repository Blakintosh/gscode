using System.Diagnostics.CodeAnalysis;
using GSCode.Data;
using GSCode.Parser.Data;
using GSCode.Parser.Lexical;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace GSCode.Parser.AST;

/// <summary>
/// An implementation of an LL(1) recursive descent parser for the GSC & CSC languages.
/// </summary>
internal ref struct Parser(Token startToken, ParserIntelliSense sense)
{
    public Token PreviousToken { get; private set; } = startToken;
    public Token CurrentToken { get; private set; } = startToken;

    public readonly TokenType CurrentTokenType => CurrentToken.Type;
    public readonly Range CurrentTokenRange => CurrentToken.Range;
    
    [Flags]
    private enum ParserContextFlags {
        None = 0,
        InFunctionBody = 1,
        InSwitchBody = 2,
        InLoopBody = 4,
    }
    
    private ParserContextFlags ContextFlags { get; set; } = ParserContextFlags.None;

    public ParserIntelliSense Sense { get; } = sense;

    /// <summary>
    /// Used by fault recovery strategies to allow them to attempt parsing in a fault state.
    /// </summary>
    public Token SnapshotToken { get; private set; } = startToken;

    /// <summary>
    /// Suppresses all error messages issued when active, which aids with error recovery.
    /// </summary>
    private bool Silent { get; set; } = false;

    public ScriptNode Parse()
    {
        // Advance past the first SOF token.
        Advance();

        return Script();
    }

    /// <summary>
    /// Parses and outputs a script node.
    /// </summary>
    /// <remarks>
    /// Script := DependenciesList ScriptDefnList
    /// </remarks>
    /// <returns></returns>
    private ScriptNode Script()
    {
        List<DependencyNode> dependencies = DependenciesList();
        List<AstNode> scriptDefns = ScriptList();

        return new ScriptNode
        {
            Dependencies = dependencies,
            ScriptDefns = scriptDefns
        };
    }

    /// <summary>
    /// Parses and outputs a dependencies list.
    /// </summary>
    /// <remarks>
    /// Adaptation of: DependenciesList := Dependency DependenciesList | ε
    /// </remarks>
    /// <returns></returns>
    private List<DependencyNode> DependenciesList()
    {
        List<DependencyNode> dependencies = new List<DependencyNode>();

        while(CurrentTokenType == TokenType.Using)
        {
            DependencyNode? next = Dependency();

            // Success
            if(next is not null)
            {
                dependencies.Add(next);
                continue;
            }

            // Unsuccessful parse - attempt to recover
            EnterRecovery();

            // While we're not in the first set of ScriptList (or at EOF), keep advancing to try and recover.
            while(
                CurrentTokenType != TokenType.Precache && 
                CurrentTokenType != TokenType.UsingAnimTree &&
                CurrentTokenType != TokenType.Function &&
                CurrentTokenType != TokenType.Class &&
                CurrentTokenType != TokenType.Namespace &&
                CurrentTokenType != TokenType.Eof
                )
            {
                Advance();

                // We've recovered, so we can try to parse the next dependency.
                if(CurrentTokenType == TokenType.Using)
                {
                    ExitRecovery();
                    break;
                }
            }
        }

        return dependencies;
    }

    /// <summary>
    /// Parses and outputs a dependency node.
    /// </summary>
    /// <remarks>
    /// Dependency := USING Path SEMICOLON
    /// </remarks>
    /// <returns></returns>
    private DependencyNode? Dependency()
    {
        // Pass USING
        Advance();

        // Parse the path
        PathNode? path = Path();
        if(path is null)
        {
            return null;
        }

        // Check for SEMICOLON
        if (!AdvanceIfType(TokenType.Semicolon))
        {
            AddErrorAtEndOfPrevious(GSCErrorCodes.ExpectedSemiColon, "using directive");
        }

        return new DependencyNode(path);
    }

    /// <summary>
    /// Parses and outputs a path node.
    /// </summary>
    /// <remarks>
    /// Path := IDENTIFIER PathSub
    /// </remarks>
    /// <returns></returns>
    private PathNode? Path()
    {
        Token segmentToken = CurrentToken;
        if(CurrentTokenType != TokenType.Identifier)
        {
            // Expected a path segment
            AddError(GSCErrorCodes.ExpectedPathSegment, CurrentToken.Lexeme);
            return null;
        }

        // Path is whitespace-sensitive, so we'll advance manually.
        CurrentToken = CurrentToken.Next;

        PathNode? partial = PathPartial();

        if(partial is null)
        {
            return null;
        }

        partial.PrependSegment(segmentToken);
        return partial;
    }

    /// <summary>
    /// Parses and outputs a path partial node.
    /// </summary>
    /// <remarks>
    /// PathPartial := BACKSLASH IDENTIFIER PathPartial | ε
    /// </remarks>
    /// <returns></returns>
    private PathNode? PathPartial()
    {
        // Empty case
        if(CurrentTokenType != TokenType.Backslash)
        {
            return new PathNode();
        }

        // Path is whitespace-sensitive, so we'll advance manually.
        CurrentToken = CurrentToken.Next;

        Token segmentToken = CurrentToken;
        if (CurrentTokenType != TokenType.Identifier)
        {
            // Expected a path segment
            AddError(GSCErrorCodes.ExpectedPathSegment, CurrentToken.Lexeme);

            return null;
        }

        // Path is whitespace-sensitive, so we'll advance manually.
        CurrentToken = CurrentToken.Next;

        // Get any further segments, then we'll prepend the current one.
        PathNode? partial = PathPartial();

        // Failed to parse the rest of the path 
        if(partial is null)
        {
            return null;
        }
        partial.PrependSegment(segmentToken);

        return partial;
    }

    /// <summary>
    /// Parses and outputs a script definition list.
    /// </summary>
    /// <remarks>
    /// Adaptation of: ScriptList := ScriptDefn ScriptList | ε
    /// </remarks>
    /// <returns></returns>
    private List<AstNode> ScriptList()
    {
        List<AstNode> scriptDefns = new List<AstNode>();

        // Keep parsing script definitions until we reach the end of the file, as this is our last production.
        while(CurrentTokenType != TokenType.Eof)
        {
            AstNode? next = ScriptDefn();

            // Success
            if(next is not null)
            {
                scriptDefns.Add(next);

                // We're not in recovery mode, so we can continue parsing as normal.
                if(!InRecovery())
                {
                    continue;
                }
            }

            // Unsuccessful parse - attempt to recover
            EnterRecovery();

            // While we're not in the first set of ScriptList (or at EOF), keep advancing to try and recover.
            while(
                CurrentTokenType != TokenType.Precache && 
                CurrentTokenType != TokenType.UsingAnimTree &&
                CurrentTokenType != TokenType.Function &&
                CurrentTokenType != TokenType.Class &&
                CurrentTokenType != TokenType.Namespace &&
                CurrentTokenType != TokenType.Eof
                )
            {
                Advance();

                // We've recovered, so we can try to parse the next script definition.
                if(CurrentTokenType == TokenType.Precache || CurrentTokenType == TokenType.UsingAnimTree || CurrentTokenType == TokenType.Function || CurrentTokenType == TokenType.Class || CurrentTokenType == TokenType.Namespace)
                {
                    ExitRecovery();
                    break;
                }
            }
        }

        return scriptDefns;
    }

    /// <summary>
    /// Parses and outputs a script definition node.
    /// </summary>
    /// <remarks>
    /// ScriptDefn := PrecacheDir | UsingAnimTreeDir | NamespaceDir | FunDefn | ClassDefn
    /// </remarks>
    private AstNode? ScriptDefn()
    {
        switch(CurrentTokenType)
        {
            case TokenType.Precache:
                return PrecacheDir();
            case TokenType.UsingAnimTree:
                return UsingAnimTreeDir();
            case TokenType.Namespace:
                return NamespaceDir();
            case TokenType.Function:
                return FunDefn();
            case TokenType.Class:
                return ClassDefn();
            case TokenType.Using:
                // The GSC compiler doesn't allow this, but we'll still attempt to parse it to get dependency info.
                AddError(GSCErrorCodes.UnexpectedUsing);
                return Dependency();
            case TokenType.Private:
            case TokenType.Autoexec:
                // They may be attempting to define a function with its modifiers in front, which is incorrect.
                AddError(GSCErrorCodes.UnexpectedFunctionModifier, CurrentToken.Lexeme);
                return null;
            default:
                // Expected a directive or definition
                AddError(GSCErrorCodes.ExpectedScriptDefn, CurrentToken.Lexeme);
                return null;
        }
    }

    /// <summary>
    /// Parses and outputs a script precache node.
    /// </summary>
    /// <remarks>
    /// PrecacheDir := PRECACHE OPENPAREN STRING COMMA STRING CLOSEPAREN SEMICOLON
    /// </remarks>
    /// <returns></returns>
    private PrecacheNode? PrecacheDir()
    {
        // Pass PRECACHE
        Advance();

        // Check for OPENPAREN
        if(!AdvanceIfType(TokenType.OpenParen))
        {
            AddError(GSCErrorCodes.ExpectedToken, '(', CurrentToken.Lexeme);
            return null;
        }

        // Parse the asset's type
        Token typeToken = CurrentToken;
        if (!AdvanceIfType(TokenType.String))
        {
            AddError(GSCErrorCodes.ExpectedPrecacheType, CurrentToken.Lexeme);
            return null;
        }

        // Check for COMMA
        if (!AdvanceIfType(TokenType.Comma))
        {
            AddError(GSCErrorCodes.ExpectedToken, ',', CurrentToken.Lexeme);
            return null;
        }

        // Parse the asset's path
        Token pathToken = CurrentToken;
        if (!AdvanceIfType(TokenType.String))
        {
            AddError(GSCErrorCodes.ExpectedPrecachePath, CurrentToken.Lexeme);
            return null;
        }

        // Check for CLOSEPAREN
        if (!AdvanceIfType(TokenType.CloseParen))
        {
            AddError(GSCErrorCodes.ExpectedToken, ')', CurrentToken.Lexeme);

            // We got enough information to create a node even if parsing failed.
            return new PrecacheNode()
            {
                Type = typeToken.Lexeme,
                TypeRange = typeToken.Range,
                Path = pathToken.Lexeme,
                PathRange = pathToken.Range
            };
        }

        // Check for SEMICOLON
        if (!AdvanceIfType(TokenType.Semicolon))
        {
            AddErrorAtEndOfPrevious(GSCErrorCodes.ExpectedSemiColon, "precache directive");
        }

        // TODO: strip the quotes from the strings
        return new PrecacheNode()
        {
            Type = typeToken.Lexeme,
            TypeRange = typeToken.Range,
            Path = pathToken.Lexeme,
            PathRange = pathToken.Range
        };
    }

    /// <summary>
    /// Parses and outputs a using animtree node.
    /// </summary>
    /// <remarks>
    /// UsingAnimTreeDir := USINGANIMTREE OPENPAREN STRING CLOSEPAREN SEMICOLON
    /// </remarks>
    /// <returns></returns>
    private UsingAnimTreeNode? UsingAnimTreeDir()
    {
        // Pass USINGANIMTREE
        Advance();

        // Check for OPENPAREN
        if (!AdvanceIfType(TokenType.OpenParen))
        {
            AddError(GSCErrorCodes.ExpectedToken, '(', CurrentToken.Lexeme);
            return null;
        }

        // Parse the animtree's name
        Token nameToken = CurrentToken;
        if (!AdvanceIfType(TokenType.String))
        {
            AddError(GSCErrorCodes.ExpectedAnimTreeName, CurrentToken.Lexeme);
            return null;
        }

        // Check for CLOSEPAREN
        if (!AdvanceIfType(TokenType.CloseParen))
        {
            AddError(GSCErrorCodes.ExpectedToken, ')', CurrentToken.Lexeme);

            // We got enough information to create a node even if parsing failed.
            return new UsingAnimTreeNode(nameToken);
        }

        // Check for SEMICOLON
        if (!AdvanceIfType(TokenType.Semicolon))
        {
            AddErrorAtEndOfPrevious(GSCErrorCodes.ExpectedSemiColon, "using animation tree directive");
        }

        return new UsingAnimTreeNode(nameToken);
    }

    /// <summary>
    /// Parses and outputs a namespace node.
    /// </summary>
    /// <remarks>
    /// NamespaceDir := NAMESPACE IDENTIFIER SEMICOLON
    /// </remarks>
    /// <returns></returns>
    private NamespaceNode? NamespaceDir()
    {
        // Pass NAMESPACE
        Advance();

        // Parse the namespace's identifier
        Token namespaceToken = CurrentToken;
        if (!AdvanceIfType(TokenType.Identifier))
        {
            AddError(GSCErrorCodes.ExpectedNamespaceIdentifier, CurrentToken.Lexeme);
            return null;
        }

        // Check for SEMICOLON
        if (!AdvanceIfType(TokenType.Semicolon))
        {
            AddErrorAtEndOfPrevious(GSCErrorCodes.ExpectedSemiColon, "namespace directive");
        }

        return new NamespaceNode(namespaceToken);
    }

    /// <summary>
    /// Parses and outputs a class definition.
    /// </summary>
    /// <remarks>
    /// ClassDefn := CLASS IDENTIFIER InheritsFrom LBRACE ClassDefnList RBRACE
    /// </remarks>
    /// <returns></returns>
    private ClassDefnNode ClassDefn()
    {
        // Pass CLASS
        Advance();

        // Parse the class's identifier
        if(!ConsumeIfType(TokenType.Identifier, out Token? identifierToken))
        {
            AddError(GSCErrorCodes.ExpectedClassIdentifier, CurrentToken.Lexeme);
            EnterRecovery();
        }

        // Check if we've got an inheritance clause
        Token? inheritedClassToken = InheritsFrom();

        // Check for LBRACE
        if (!AdvanceIfType(TokenType.OpenBrace))
        {
            AddError(GSCErrorCodes.ExpectedToken, '{', CurrentToken.Lexeme);

            return new ClassDefnNode(identifierToken, inheritedClassToken, new ClassBodyListNode());
        }
        ExitRecovery();

        // Parse the class's body
        ClassBodyListNode classBody = ClassBodyDefnList();

        // Check for RBRACE
        if (!AdvanceIfType(TokenType.CloseBrace))
        {
            AddError(GSCErrorCodes.ExpectedToken, '}', CurrentToken.Lexeme);
            EnterRecovery();
        }

        return new ClassDefnNode(identifierToken, inheritedClassToken, classBody);
    }

    /// <summary>
    /// Parses and outputs an inheritance clause, if present.
    /// </summary>
    /// <remarks>
    /// InheritsFrom := COLON IDENTIFIER | ε
    /// </remarks>
    /// <returns></returns>
    private Token? InheritsFrom()
    {
        if(!AdvanceIfType(TokenType.Colon))
        {
            return null;
        }

        ExitRecovery();

        if(!ConsumeIfType(TokenType.Identifier, out Token? identifierToken))
        {
            AddError(GSCErrorCodes.ExpectedClassIdentifier, CurrentToken.Lexeme);
            EnterRecovery();
        }
        return identifierToken;
    }

    /// <summary>
    /// Parses and outputs zero or more class body definitions.
    /// </summary>
    /// <remarks>
    /// ClassDefnList := ClassDefn ClassDefnList | ε
    /// </remarks>
    /// <returns></returns>
    private ClassBodyListNode ClassBodyDefnList()
    {
        // Empty case
        if(CurrentTokenType != TokenType.Var && CurrentTokenType != TokenType.Constructor && CurrentTokenType != TokenType.Destructor && CurrentTokenType != TokenType.Function)
        {
            return new();
        }

        AstNode? classDefn = ClassBodyDefn();

        if(classDefn is null && 
            // No chance of recovery
            CurrentTokenType != TokenType.Var && CurrentTokenType != TokenType.Constructor && CurrentTokenType != TokenType.Destructor && CurrentTokenType != TokenType.Function)
        {
            return new();
        }

        ClassBodyListNode rest = ClassBodyDefnList();

        // Only add our first definition if it was successfully parsed
        if(classDefn is not null)
        {
            rest.Definitions.AddFirst(classDefn);
        }

        return rest;
    }

    /// <summary>
    /// Parses and outputs a class body definition.
    /// </summary>
    /// <remarks>
    /// ClassBodyDefn := MemberDecl | CONSTRUCTOR StructorDefn | DESTRUCTOR StructorDefn | FunDefn
    /// </remarks>
    /// <returns></returns>
    private AstNode? ClassBodyDefn()
    {
        switch(CurrentTokenType)
        {
            case TokenType.Var:
                return MemberDecl();
            case TokenType.Constructor:
            case TokenType.Destructor:
                Token keywordToken = Consume();

                return StructorDefn(keywordToken);
            case TokenType.Function:
                return FunDefn(isMethod: true);
            default:
                AddError(GSCErrorCodes.ExpectedClassBodyDefinition, CurrentToken.Lexeme);
                return null;
        }
    }

    /// <summary>
    /// Parses a class field declaration.
    /// </summary>
    /// <remarks>
    /// MemberDecl := VAR IDENTIFIER SEMICOLON
    /// </remarks>
    /// <returns></returns>
    private MemberDeclNode? MemberDecl()
    {
        // Pass VAR
        Advance();

        // Get the field name
        if(!ConsumeIfType(TokenType.Identifier, out Token? identifierToken))
        {
            AddError(GSCErrorCodes.ExpectedMemberIdentifier, CurrentToken.Lexeme);
            EnterRecovery();
            return null;
        }

        // Check for SEMICOLON
        if (!AdvanceIfType(TokenType.Semicolon))
        {
            AddErrorAtEndOfPrevious(GSCErrorCodes.ExpectedSemiColon, "member declaration");
        }

        return new MemberDeclNode(identifierToken);
    }

    /// <summary>
    /// Parses a constructor or destructor definition.
    /// </summary>
    /// <remarks>
    /// StructorDefn := OPENPAREN CLOSEPAREN OPENBRACE FunBraceBlock CLOSEBRACE
    /// </remarks>
    /// <param name="keywordToken"></param>
    /// <returns></returns>
    private StructorDefnNode? StructorDefn(Token keywordToken)
    {
        // Keyword has already been consumed

        // Check for OPENPAREN
        if (!AdvanceIfType(TokenType.OpenParen))
        {
            AddError(GSCErrorCodes.ExpectedToken, '(', CurrentToken.Lexeme);
            EnterRecovery();
        }

        // Check for CLOSEPAREN
        if (!AdvanceIfType(TokenType.CloseParen))
        {
            // Were they trying to define a constructor with parameters?
            if(CurrentTokenType == TokenType.Identifier)
            {
                AddError(GSCErrorCodes.UnexpectedConstructorParameter, CurrentToken.Lexeme);
            }
            else
            {
                AddError(GSCErrorCodes.ExpectedConstructorParenthesis, CurrentToken.Lexeme);
            }
            EnterRecovery();
        }
        else
        {
            ExitRecovery();
        }

        // Check for OPENBRACE
        if (CurrentTokenType != TokenType.OpenBrace)
        {
            AddError(GSCErrorCodes.ExpectedToken, '{', CurrentToken.Lexeme);
            return null;
        }

        ExitRecovery();

        StmtListNode block = FunBraceBlock();

        // FunBraceBlock should have consumed the closing brace

        return new StructorDefnNode(keywordToken, block);
    }

    /// <summary>
    /// Parses and outputs a function definition node.
    /// </summary>
    /// <remarks>
    /// FunDefn := FUNCTION FunKeywords IDENTIFIER OPENPAREN ParamList CLOSEPAREN FunBraceBlock
    /// </remarks>
    /// <returns></returns>
    private FunDefnNode? FunDefn(bool isMethod = false)
    {
        // Pass FUNCTION
        Advance();

        FunKeywordsNode keywords = FunKeywords();

        // Parse the function's identifier
        Token? identifierToken = null;
        if(CurrentTokenType == TokenType.Identifier)
        {
            identifierToken = CurrentToken;
            Advance();
        }
        else
        {
            AddError(isMethod ? GSCErrorCodes.ExpectedMethodIdentifier : GSCErrorCodes.ExpectedFunctionIdentifier, CurrentToken.Lexeme);
            EnterRecovery();
        }

        // Check for OPENPAREN
        ParamListNode? parameters = null;
        if (!AdvanceIfType(TokenType.OpenParen))
        {
            AddError(GSCErrorCodes.ExpectedToken, '(', CurrentToken.Lexeme);
        }
        else
        {
            ExitRecovery();

            // Parse the argument list.
            parameters = ParamList();
        }
        parameters ??= new ParamListNode();

        // Check for CLOSEPAREN
        if (!AdvanceIfType(TokenType.CloseParen))
        {
            AddError(GSCErrorCodes.ExpectedToken, ')', CurrentToken.Lexeme);
        }
        else
        {
            ExitRecovery();
        }

        // Check for the brace block, then parse it.
        if(CurrentTokenType != TokenType.OpenBrace)
        {
            AddError(GSCErrorCodes.ExpectedToken, '{', CurrentToken.Lexeme);
            ExitRecovery();

            // No use in doing a placeholder without a body, unless it's got a name.
            if(identifierToken is null)
            {
                return null;
            }

            // @next: Look at the FIRST set of Stmt and make a decision there on whether to enter the body despite the open brace not being there.
            return new FunDefnNode
            {
                Name = identifierToken,
                Keywords = keywords,
                Parameters = parameters,
                Body = new StmtListNode()
            };
        }

        ExitRecovery();
        StmtListNode block = FunBraceBlock();

        return new FunDefnNode
        {
            Name = identifierToken,
            Keywords = keywords,
            Parameters = parameters,
            Body = block
        };
    }

    /// <summary>
    /// Parses and outputs a brace block in a function.
    /// </summary>
    /// <remarks>
    /// FunBraceBlock := OPENBRACE StmtList CLOSEBRACE
    /// </remarks>
    /// <returns></returns>
    private StmtListNode FunBraceBlock()
    {
        // Pass over OPENBRACE
        Advance();

        // Parse the statements in the block
        StmtListNode stmtListNode = StmtList();

        if(!AdvanceIfType(TokenType.CloseBrace))
        {
            AddError(GSCErrorCodes.ExpectedToken, '}', CurrentToken.Lexeme);
        }
        return stmtListNode;
    }

    /// <summary>
    /// Parses a (possibly empty) list of statements in a function brace block.
    /// </summary>
    /// <remarks>
    /// StmtList := Stmt StmtList | ε
    /// </remarks>
    /// <returns></returns>
    private StmtListNode StmtList(ParserContextFlags newContext = ParserContextFlags.None)
    {
        bool isNewContext = false;
        if(newContext != ParserContextFlags.None)
        {
            isNewContext = EnterContextIfNewly(newContext);
        }
        
        switch(CurrentTokenType)
        {
            // Control flow
            case TokenType.If:
            case TokenType.Do:
            case TokenType.While:
            case TokenType.For:
            case TokenType.Foreach:
            case TokenType.Switch:
            case TokenType.Return:
            // Special functions
            case TokenType.WaittillFrameEnd:
            case TokenType.Wait:
            case TokenType.WaitRealTime:
            // Misc
            case TokenType.Const:
            case TokenType.OpenDevBlock:
            case TokenType.OpenBrace:
            case TokenType.Semicolon:
            // Contextual
            case TokenType.Break when InLoopOrSwitch():
            case TokenType.Continue when InLoop():
            // Expressions
            case TokenType.Identifier:
            case TokenType.OpenBracket:
            case TokenType.Thread:
                AstNode? statement = Stmt();

                StmtListNode rest = StmtList();
                if(statement is not null)
                {
                    rest.Statements.AddFirst(statement);
                }

                ExitContextIfWasNewly(newContext, isNewContext);
                return rest;
            // Everything else - empty case
            default:
                ExitContextIfWasNewly(newContext, isNewContext);
                return new();
        }
    }

    /// <summary>
    /// Parses a single statement in a function.
    /// </summary>
    /// <remarks>
    /// Stmt := IfElseStmt | DoWhileStmt | WhileStmt | ForStmt | ForeachStmt | SwitchStmt | ReturnStmt | WaittillFrameEndStmt | WaitStmt | WaitRealTimeStmt | ConstStmt | DevBlock | BraceBlock | ExprStmt
    /// </remarks>
    /// <returns></returns>
    private AstNode? Stmt(ParserContextFlags newContext = ParserContextFlags.None)
    {
        bool isNewContext = false;
        if(newContext != ParserContextFlags.None)
        {
            isNewContext = EnterContextIfNewly(newContext);
        }
        
        AstNode? result = CurrentTokenType switch
        {
            TokenType.If => IfElseStmt(),
            TokenType.Do => DoWhileStmt(),
            TokenType.While => WhileStmt(),
            TokenType.For => ForStmt(),
            TokenType.Foreach => ForeachStmt(),
            TokenType.Switch => SwitchStmt(),
            TokenType.Return => ReturnStmt(),
            TokenType.WaittillFrameEnd => ControlFlowActionStmt(AstNodeType.WaitTillFrameEndStmt),
            TokenType.Wait => ReservedFuncStmt(AstNodeType.WaitStmt),
            TokenType.WaitRealTime => ReservedFuncStmt(AstNodeType.WaitRealTimeStmt),
            TokenType.Const => ConstStmt(),
            TokenType.OpenDevBlock => DevBlock(),
            TokenType.OpenBrace => FunBraceBlock(),
            TokenType.Break when InLoopOrSwitch() => ControlFlowActionStmt(AstNodeType.BreakStmt),
            TokenType.Continue when InLoop() => ControlFlowActionStmt(AstNodeType.ContinueStmt),
            TokenType.Semicolon => EmptyStmt(),
            TokenType.Identifier or TokenType.Thread or TokenType.OpenBracket => ExprStmt(),
            _ => null
        };
        
        ExitContextIfWasNewly(newContext, isNewContext);
        return result;
    }

    /// <summary>
    /// Parses an if-else statement in a function block.
    /// </summary>
    /// <remarks>
    /// IfElseStmt := IfClause ElseOrEndClause
    /// </remarks>
    /// <returns></returns>
    private IfStmtNode IfElseStmt()
    {
        IfStmtNode firstClause = IfClause();
            
        // May go into another clause
        firstClause.Else = ElseOrEndClause();

        return firstClause;
    }

    /// <summary>
    /// Parses a single if-clause and its then statement.
    /// </summary>
    /// <remarks>
    /// IfClause := IF OPENPAREN Expr CLOSEPAREN Stmt
    /// </remarks>
    /// <returns></returns>
    private IfStmtNode IfClause()
    {
        // TODO: Fault tolerant logic
        // Pass IF
        Advance();

        // Check for OPENPAREN
        if (!AdvanceIfType(TokenType.OpenParen))
        {
            AddError(GSCErrorCodes.ExpectedToken, '(', CurrentToken.Lexeme);
        }

        // Parse the condition
        ExprNode? condition = Expr();

        // Check for CLOSEPAREN
        if (!AdvanceIfType(TokenType.CloseParen))
        {
            AddError(GSCErrorCodes.ExpectedToken, ')', CurrentToken.Lexeme);
        }

        // Parse the then branch
        AstNode? then = Stmt();
        
        return new()
        {
            Condition = condition,
            Then = then
        };
    }

    /// <summary>
    /// Parses an else or else-if clause if provided, or otherwise ends the if statement.
    /// </summary>
    /// <remarks>
    /// ElseOrEndClause := ELSE ElseClause | ε
    /// </remarks>
    /// <returns></returns>
    private IfStmtNode? ElseOrEndClause()
    {
        // Empty production
        if (!AdvanceIfType(TokenType.Else))
        {
            return null;
        }
        
        // Otherwise, seek an else or else-if
        return ElseClause();
    }

    /// <summary>
    /// Parses an else or else-if clause, including its then statement and any further clauses.
    /// </summary>
    /// <remarks>
    /// ElseClause := IfClause ElseOrEndClause | Stmt
    /// </remarks>
    /// <returns></returns>
    private IfStmtNode ElseClause()
    {
        // Case 1: another if-clause
        if (CurrentTokenType == TokenType.If)
        {
            IfStmtNode clause = IfClause();
            
            // May go into another clause
            clause.Else = ElseOrEndClause();

            return clause;
        }
        
        // Case 2: just an else clause
        AstNode? then = Stmt();

        return new()
        {
            Then = then
        };
    }

    /// <summary>
    /// Parses a do-while statement, including its then clause and condition.
    /// </summary>
    /// <remarks>
    /// DoWhileStmt := DO Stmt WHILE OPENPAREN Expr CLOSEPAREN SEMICOLON
    /// </remarks>
    /// <returns></returns>
    private DoWhileStmtNode DoWhileStmt()
    {
        // TODO: Fault tolerant logic
        // Pass over DO
        Advance();
        
        // Parse the loop's body
        AstNode? then = Stmt(ParserContextFlags.InLoopBody);
        
        // Check for WHILE
        if (!AdvanceIfType(TokenType.While))
        {
            AddError(GSCErrorCodes.ExpectedToken, "while", CurrentToken.Lexeme);
            return new DoWhileStmtNode(null, then);
        }
        
        // Check for OPENPAREN
        if (!AdvanceIfType(TokenType.OpenParen))
        {
            AddError(GSCErrorCodes.ExpectedToken, '(', CurrentToken.Lexeme);
            EnterRecovery();
        }
        
        ExitRecovery();
        // Parse the loop's condition
        ExprNode? condition = Expr();
        
        // Check for CLOSEPAREN
        if (!AdvanceIfType(TokenType.CloseParen))
        {
            AddError(GSCErrorCodes.ExpectedToken, ')', CurrentToken.Lexeme);
            EnterRecovery();
        }
        
        // Check for SEMICOLON
        if (!AdvanceIfType(TokenType.Semicolon))
        {
            AddErrorAtEndOfPrevious(GSCErrorCodes.ExpectedSemiColon, "do-while loop");
            EnterRecovery();
        }
        
        ExitRecovery();
        return new DoWhileStmtNode(condition, then);
    }

    /// <summary>
    /// Parses a while statement, including its condition and then clause.
    /// </summary>
    /// <remarks>
    /// WhileStmt := WHILE OPENPAREN Expr CLOSEPAREN Stmt
    /// </remarks>
    /// <returns></returns>
    private WhileStmtNode WhileStmt()
    {
        // TODO: Fault tolerant logic
        // Pass over WHILE
        Advance();
        
        // Check for OPENPAREN
        if (!AdvanceIfType(TokenType.OpenParen))
        {
            AddError(GSCErrorCodes.ExpectedToken, '(', CurrentToken.Lexeme);
        }
        
        // Parse the loop's condition
        ExprNode? condition = Expr();
        
        // Check for CLOSEPAREN
        if (!AdvanceIfType(TokenType.CloseParen))
        {
            AddError(GSCErrorCodes.ExpectedToken, ')', CurrentToken.Lexeme);
        }
        
        // Parse the loop's body, update context.
        AstNode? then = Stmt(ParserContextFlags.InLoopBody);
        
        return new WhileStmtNode(condition, then);
    }

    /// <summary>
    /// Parses a for statement, including its then clause and any initialization, condition, and increment clauses.
    /// </summary>
    /// <remarks>
    /// ForStmt := FOR OPENPAREN AssignableExpr SEMICOLON Expr SEMICOLON AssignableExpr CLOSEPAREN Stmt
    /// </remarks>
    /// <returns></returns>
    private ForStmtNode ForStmt()
    {
        // TODO: Fault tolerant logic
        // Pass over FOR
        Advance();
        
        // Check for OPENPAREN
        if (!AdvanceIfType(TokenType.OpenParen))
        {
            AddError(GSCErrorCodes.ExpectedToken, '(', CurrentToken.Lexeme);
        }

        // Parse the loop's initialization
        ExprNode? init = null;
        if (CurrentTokenType != TokenType.Semicolon)
        {
            init = AssignableExpr();
        }

        // Check for SEMICOLON
        if (!AdvanceIfType(TokenType.Semicolon))
        {
            AddErrorAtEndOfPrevious(GSCErrorCodes.ExpectedSemiColon, "for loop");
        }

        // Parse the loop's condition
        ExprNode? condition = null;
        if (CurrentTokenType != TokenType.Semicolon)
        {
            condition = Expr();
        }

        // Check for SEMICOLON
        if (!AdvanceIfType(TokenType.Semicolon))
        {
            AddErrorAtEndOfPrevious(GSCErrorCodes.ExpectedSemiColon, "for loop");
        }

        // Parse the loop's increment
        ExprNode? increment = null;
        if (CurrentTokenType != TokenType.CloseParen)
        {
            increment = AssignableExpr();
        }

        // Check for CLOSEPAREN
        if (!AdvanceIfType(TokenType.CloseParen))
        {
            AddError(GSCErrorCodes.ExpectedToken, ')', CurrentToken.Lexeme);
        }
        
        // Parse the loop's body, update context.
        AstNode? then = Stmt(ParserContextFlags.InLoopBody);
        
        return new(init, condition, increment, then);
    }

    /// <summary>
    /// Parses a foreach statement, including its then clause and collection.
    /// </summary>
    /// <remarks>
    /// ForeachStmt := FOREACH OPENPAREN IDENTIFIER ForeachValueIdentifier IN Expr CLOSEPAREN Stmt
    /// </remarks>
    /// <returns></returns>
    private ForeachStmtNode ForeachStmt()
    {
        // TODO: Fault tolerant logic
        // Pass over FOREACH
        Advance();
        
        // Check for OPENPAREN
        if (!AdvanceIfType(TokenType.OpenParen))
        {
            AddError(GSCErrorCodes.ExpectedToken, '(', CurrentToken.Lexeme);
        }
        
        // Parse the loop's identifier
        Token firstIdentifierToken = CurrentToken;
        if (!AdvanceIfType(TokenType.Identifier))
        {
            AddError(GSCErrorCodes.ExpectedForeachIdentifier, CurrentToken.Lexeme);
        }

        // Check for COMMA and another identifier, which would indicate that we're iterating over key-value pairs.
        Token? valueIdentifierToken = ForeachValueIdentifier();
        
        // Check for IN
        if (!AdvanceIfType(TokenType.In))
        {
            AddError(GSCErrorCodes.ExpectedToken, "in", CurrentToken.Lexeme);
        }
        
        // Parse the loop's collection
        ExprNode? collection = Expr();
        
        // Check for CLOSEPAREN
        if (!AdvanceIfType(TokenType.CloseParen))
        {
            AddError(GSCErrorCodes.ExpectedToken, ')', CurrentToken.Lexeme);
        }
        
        // Parse the loop's body, update context.
        AstNode? then = Stmt(ParserContextFlags.InLoopBody);

        if(valueIdentifierToken is not null)
        {
            return new ForeachStmtNode(valueIdentifierToken, firstIdentifierToken, collection, then);
        }
        return new ForeachStmtNode(firstIdentifierToken, null, collection, then);
    }

    /// <summary>
    /// Parses and outputs a foreach value token, if given. For key, value pairs.
    /// </summary>
    /// <remarks>
    /// ForeachValueIdentifier := COMMA IDENTIFIER | ε
    /// </remarks>
    /// <returns></returns>
    private Token? ForeachValueIdentifier()
    {
        if (!AdvanceIfType(TokenType.Comma))
        {
            return null;
        }

        if (!ConsumeIfType(TokenType.Identifier, out Token? valueToken))
        {
            AddError(GSCErrorCodes.ExpectedForeachIdentifier, CurrentToken.Lexeme);
            EnterRecovery();
            return null;
        }
        return valueToken;
    }

    /// <summary>
    /// Parses and outputs a full switch statement.
    /// </summary>
    /// <remarks>
    /// SwitchStmt := SWITCH OPENPAREN Expr CLOSEPAREN OPENBRACE CaseList CLOSEBRACE
    /// </remarks>
    /// <returns></returns>
    private SwitchStmtNode SwitchStmt()
    {
        // TODO: Fault tolerant logic
        // Pass over SWITCH
        Advance();
        
        // Check for OPENPAREN
        if (!AdvanceIfType(TokenType.OpenParen))
        {
            AddError(GSCErrorCodes.ExpectedToken, '(', CurrentToken.Lexeme);
        }
        
        // Parse the switch's expression
        ExprNode? expression = Expr();
        
        // Check for CLOSEPAREN
        if (!AdvanceIfType(TokenType.CloseParen))
        {
            AddError(GSCErrorCodes.ExpectedToken, ')', CurrentToken.Lexeme);
        }
        
        // Check for OPENBRACE
        if (!AdvanceIfType(TokenType.OpenBrace))
        {
            AddError(GSCErrorCodes.ExpectedToken, '{', CurrentToken.Lexeme);
        }
        
        // Parse the cases
        CaseListNode cases = CaseList();
        
        // Check for CLOSEBRACE
        if (!AdvanceIfType(TokenType.CloseBrace))
        {
            AddError(GSCErrorCodes.ExpectedToken, '}', CurrentToken.Lexeme);
        }
        
        return new SwitchStmtNode()
        {
            Expression = expression,
            Cases = cases
        };
    }

    /// <summary>
    /// Parses and outputs a series of case labels and their associated statements.
    /// </summary>
    /// <remarks>
    /// CaseList := CaseStmt CaseList | ε
    /// </remarks>
    /// <returns></returns>
    private CaseListNode CaseList()
    {
        // Empty case
        if (CurrentTokenType != TokenType.Case && CurrentTokenType != TokenType.Default)
        {
            return new();
        }
        
        // Parse the current case, then prepend it to the rest of the cases.
        CaseStmtNode caseStmt = CaseStmt();
        CaseListNode rest = CaseList();
        
        rest.Cases.AddFirst(caseStmt);
        
        return rest;
    }

    /// <summary>
    /// Parses and outputs a case statement, which includes one or more case labels and their associated statements.
    /// </summary>
    /// <remarks>
    /// CaseStmt := CaseOrDefaultLabel CaseStmtRhs
    /// </remarks>
    /// <returns></returns>
    private CaseStmtNode CaseStmt()
    {
        CaseLabelNode label = CaseOrDefaultLabel();
        
        // Now go to the RHS
        CaseStmtNode rhs = CaseStmtRhs();
        rhs.Labels.AddFirst(label);

        return rhs;
    }

    /// <summary>
    /// Parses and outputs the right-hand result of a case statement, which includes more labels or the case's
    /// statement list.
    /// </summary>
    /// <remarks>
    /// CaseStmtRhs := CaseOrDefaultLabel CaseStmtRhs | StmtList
    /// </remarks>
    /// <returns></returns>
    private CaseStmtNode CaseStmtRhs()
    {
        if(CurrentTokenType == TokenType.Case || CurrentTokenType == TokenType.Default)
        {
            CaseLabelNode label = CaseOrDefaultLabel();
            
            // Self-recurse to exhaust all cases
            CaseStmtNode rhs = CaseStmtRhs();
            rhs.Labels.AddFirst(label);
            return rhs;
        }

        StmtListNode production = StmtList(ParserContextFlags.InSwitchBody);
        
        return new()
        {
            Body = production
        };
    }

    /// <summary>
    /// Parses and outputs a case label or default label.
    /// </summary>
    /// <remarks>
    /// CaseOrDefaultLabel := CASE Expr COLON | DEFAULT COLON
    /// </remarks>
    /// <returns></returns>
    private CaseLabelNode CaseOrDefaultLabel()
    {
        // Default label
        if (AdvanceIfType(TokenType.Default))
        {
            // Check for COLON
            if (!AdvanceIfType(TokenType.Colon))
            {
                AddError(GSCErrorCodes.ExpectedToken, ':', CurrentToken.Lexeme);
            }
            return new(AstNodeType.DefaultLabel);
        }
        
        // Case label
        if (!AdvanceIfType(TokenType.Case))
        {
            AddError(GSCErrorCodes.ExpectedToken, "case", CurrentToken.Lexeme);
        }
        
        // Parse the case's expression
        ExprNode? expression = Expr();
        
        // Check for COLON
        if (!AdvanceIfType(TokenType.Colon))
        {
            AddError(GSCErrorCodes.ExpectedToken, ':', CurrentToken.Lexeme);
        }
        
        return new(AstNodeType.CaseLabel, expression);
    }
    
    /// <summary>
    /// Parses a return statement.
    /// </summary>
    /// <remarks>
    /// Adaptation of: ReturnStmt := RETURN ReturnValue SEMICOLON
    /// where ReturnValue := Expr | ε
    /// </remarks>
    /// <returns></returns>
    private ReturnStmtNode ReturnStmt()
    {
        // Pass over RETURN
        Advance();

        // No return value
        if (AdvanceIfType(TokenType.Semicolon))
        {
            return new();
        }
        
        // Parse the return value
        ExprNode? value = Expr();
        
        // Check for SEMICOLON
        if (!AdvanceIfType(TokenType.Semicolon))
        {
            AddErrorAtEndOfPrevious(GSCErrorCodes.ExpectedSemiColon, "return statement");
        }
        
        return new(value);
    }

    /// <summary>
    /// Parses and outputs any control-flow specific action using the same method.
    /// </summary>
    /// <param name="type"></param>
    /// <remarks>
    /// WaittillFrameEndStmt := WAITTILLFRAMEEND SEMICOLON
    /// BreakStmt := BREAK SEMICOLON
    /// ContinueStmt := CONTINUE SEMICOLON
    /// </remarks>
    /// <returns></returns>
    private ControlFlowActionNode ControlFlowActionStmt(AstNodeType type)
    {
        // Pass over the control flow keyword
        Token actionToken = CurrentToken;
        Advance();
        
        // Check for SEMICOLON
        if (AdvanceIfType(TokenType.Semicolon))
        {
            return new(type, actionToken);
        }

        string statementName = type switch
        {
            AstNodeType.WaitTillFrameEndStmt => "waittillframeend statement",
            AstNodeType.BreakStmt => "break statement",
            AstNodeType.ContinueStmt => "continue statement",
            _ => throw new ArgumentOutOfRangeException(nameof(type), "Invalid control flow action type")
        };
        AddErrorAtEndOfPrevious(GSCErrorCodes.ExpectedSemiColon, statementName);

        return new(type, actionToken);
    }

    /// <summary>
    /// Parses and outputs any reserved function using the same method.
    /// </summary>
    /// <param name="type"></param>
    /// <remarks>
    /// WaitStmt := WAIT Expr SEMICOLON
    /// WaitRealTimeStmt := WAITREALTIME Expr SEMICOLON
    /// </remarks>
    /// <returns></returns>
    private ReservedFuncStmtNode ReservedFuncStmt(AstNodeType type)
    {
        // Pass over WAIT, WAITREALTIME, etc.
        Advance();
        
        // Get the function's expression
        ExprNode? expr = Expr();
        
        // Check for SEMICOLON
        if (AdvanceIfType(TokenType.Semicolon))
        {
            return new(type, expr);
        }

        string statementName = type switch
        {
            AstNodeType.WaitStmt => "wait statement",
            AstNodeType.WaitRealTimeStmt => "waitrealtime statement",
            _ => throw new ArgumentOutOfRangeException(nameof(type), "Invalid reserved function type")
        };
        AddErrorAtEndOfPrevious(GSCErrorCodes.ExpectedSemiColon, statementName);
        
        return new(type, expr);
    }
    
    /// <summary>
    /// Parses and outputs a constant declaration statement.
    /// </summary>
    /// <remarks>
    /// ConstStmt := CONST IDENTIFIER ASSIGN Expr SEMICOLON
    /// </remarks>
    /// <returns></returns>
    private ConstStmtNode ConstStmt()
    {
        // TODO: Fault tolerant logic
        // Pass over CONST
        Advance();
        
        // Parse the constant's identifier
        Token identifierToken = CurrentToken;
        if (!AdvanceIfType(TokenType.Identifier))
        {
            AddError(GSCErrorCodes.ExpectedConstantIdentifier, CurrentToken.Lexeme);
        }
        
        // Check for ASSIGN
        if (!AdvanceIfType(TokenType.Assign))
        {
            AddError(GSCErrorCodes.ExpectedToken, '=', CurrentToken.Lexeme);
        }
        
        // Parse the constant's value
        ExprNode? value = Expr();
        
        // Check for SEMICOLON
        if (!AdvanceIfType(TokenType.Semicolon))
        {
            AddErrorAtEndOfPrevious(GSCErrorCodes.ExpectedSemiColon, "constant declaration");
        }

        return new ConstStmtNode(identifierToken, value);
    }

    /// <summary>
    /// Parses and outputs a dev block.
    /// </summary>
    /// <remarks>
    /// DevBlock := OPENDEVBLOCK StmtList CLOSEDEVBLOCK
    /// </remarks>
    /// <returns></returns>
    private DevBlockNode DevBlock()
    {
        // Pass over OPENDEVBLOCK
        Advance();
        
        // Parse the statements in the block
        StmtListNode stmtListNode = StmtList();
        
        // Check for CLOSEDEVBLOCK
        if (!AdvanceIfType(TokenType.CloseDevBlock))
        {
            AddError(GSCErrorCodes.ExpectedToken, "#/", CurrentToken.Lexeme);
        }
        
        return new DevBlockNode(stmtListNode);
    }

    /// <summary>
    /// Parses an expression statement.
    /// </summary>
    /// <remarks>
    /// ExprStmt := AssignableExpr SEMICOLON
    /// </remarks>
    /// <returns></returns>
    private ExprStmtNode ExprStmt()
    {
        // Parse the expression
        ExprNode? expr = AssignableExpr();

        // The expression failed to parse - try to recover from this.
        if(expr is null)
        {
            // @next - make Stmt more fault-tolerant / better at recovery
            EnterRecovery();
        }
        
        // Check for SEMICOLON
        if (!AdvanceIfType(TokenType.Semicolon))
        {
            AddErrorAtEndOfPrevious(GSCErrorCodes.ExpectedSemiColon, "expression statement");
        }
        
        return new ExprStmtNode(expr);
    }

    /// <summary>
    /// Parses an empty statement (ie just a semicolon.)
    /// </summary>
    /// <remarks>
    /// EmptyStmt := SEMICOLON
    /// </remarks>
    /// <returns></returns>
    private EmptyStmtNode EmptyStmt()
    {
        // Pass over SEMICOLON
        Advance();
        
        return new();
    }

    /// <summary>
    /// Parses and outputs a function keywords list node.
    /// </summary>
    /// <remarks>
    /// FunKeywords := PRIVATE FunKeywords | AUTOEXEC FunKeywords | ε
    /// </remarks>
    /// <returns></returns>
    private FunKeywordsNode FunKeywords()
    {
        // Empty production - base case
        if (CurrentTokenType != TokenType.Private && CurrentTokenType != TokenType.Autoexec)
        {
            return new();
        }
        
        // Got a keyword, prepend it to our keyword list
        Token keywordToken = CurrentToken;
        Advance();

        FunKeywordsNode node = FunKeywords();
        node.Keywords.AddFirst(keywordToken);

        return node;
    }

    /// <summary>
    /// Parses and outputs a function parameter list node.
    /// </summary>
    /// <remarks>
    /// ParamList := Param ParamListRhs | VARARGDOTS | ε
    /// </remarks>
    /// <returns></returns>
    private ParamListNode ParamList()
    {
        // varargdots production
        if(CurrentTokenType == TokenType.VarargDots)
        {
            Advance();
            return new([], true);
        }

        // empty production
        if(CurrentTokenType == TokenType.CloseParen)
        {
           return new();
        }

        // Try to parse a parameter
        ParamNode first = Param();

        // Seek the rest of them.
        ParamListNode rest = ParamListRhs();
        rest.Parameters.AddFirst(first);

        return rest;
    }

    /// <summary>
    /// Parses and outputs the right-hand side of a function parameter list.
    /// </summary>
    /// <remarks>
    /// ParamListRhs := COMMA Param | COMMA VARARGDOTS | ε
    /// </remarks>
    /// <returns></returns>
    private ParamListNode ParamListRhs()
    {
        // Nothing to add, base case.
        if(!AdvanceIfType(TokenType.Comma))
        {
            return new();
        }

        // varargdots production
        if(AdvanceIfType(TokenType.VarargDots))
        {
            return new([], true);
        }

        // Try to parse a parameter
        ParamNode next = Param();

        // Seek the rest of them.
        ParamListNode rest = ParamListRhs();
        rest.Parameters.AddFirst(next);

        return rest;
    }

    /// <summary>
    /// Parses and outputs a function parameter node.
    /// </summary>
    /// <remarks>
    /// Adaptation of: Param := BITAND IDENTIFIER ParamRhs | IDENTIFIER ParamRhs
    /// </remarks>
    /// <returns></returns>
    private ParamNode Param()
    {
        // Get whether we're passing by reference
        bool byRef = AdvanceIfType(TokenType.BitAnd);

        Token? nameToken = CurrentToken;

        // and get the parameter name.
        if(CurrentTokenType != TokenType.Identifier)
        {
            nameToken = null;
            AddError(GSCErrorCodes.ExpectedParameterIdentifier, CurrentToken.Lexeme);

            // Attempt error recovery
            if(CurrentTokenType is TokenType.Comma or TokenType.CloseParen)
            {
                return new(null, byRef);
            }
        }

        AdvanceIfType(TokenType.Identifier);

        ExprNode? defaultNode = ParamRhs();

        return new(nameToken, byRef, defaultNode);
    }

    /// <summary>
    /// Parses and outputs the default value of a function parameter, if given.
    /// </summary>
    /// <remarks>
    /// ParamRhs := ASSIGN Expr | ε
    /// </remarks>
    /// <returns></returns>
    private ExprNode? ParamRhs()
    {
        if(!AdvanceIfType(TokenType.Assign))
        {
            return null;
        }

        return Expr();
    }

    /// <summary>
    /// Parses and outputs a full expression that allows assignment.
    /// </summary>
    /// <remarks>
    /// AssignmentExpr := Expr AssignOp
    /// </remarks>
    /// <returns></returns>
    private ExprNode? AssignableExpr()
    {
        // TODO: Fault tolerant logic
        // Parse the left-hand side of the assignment
        // TODO: in practice, this could return null.
        ExprNode? left = Expr();
        if(left is null)
        {
            return null;
        }
        
        // Parse the assignment operator
        return AssignOp(left);
    }

    /// <summary>
    /// Parses and outputs an assignment operator.
    /// </summary>
    /// <remarks>
    /// AssignOp := (ASSIGN | PLUSASSIGN | MULTIPLYASSIGN | MODULOASSIGN | MINUSASSIGN | DIVIDEASSIGN | BITORASSIGN |
    ///             BITXORASSIGN | BITANDASSIGN | BITLEFTSHIFTASSIGN | BITRIGHTSHIFTASSIGN) Expr | INCREMENT | DECREMENT
    /// </remarks>
    /// <returns></returns>
    private ExprNode AssignOp(ExprNode left)
    {
        switch (CurrentTokenType)
        {
            case TokenType.Increment:
            case TokenType.Decrement:
                return new PostfixExprNode(left, Consume());
            case TokenType.Assign:
            case TokenType.PlusAssign:
            case TokenType.MultiplyAssign:
            case TokenType.ModuloAssign:
            case TokenType.MinusAssign:
            case TokenType.DivideAssign:
            case TokenType.BitOrAssign:
            case TokenType.BitXorAssign:
            case TokenType.BitAndAssign:
            case TokenType.BitLeftShiftAssign:
            case TokenType.BitRightShiftAssign:
                Token operatorToken = Consume();
                ExprNode? right = Expr();
                return new BinaryExprNode(left, operatorToken, right);
            // No assignment - go with whatever LHS yielded
            default:
                return left;
        }
    }

    /// <summary>
    /// Parses and outputs a full arithmetic or logical expression.
    /// </summary>
    /// <remarks>
    /// Expr := LogAnd LogOrRhs
    /// </remarks>
    /// <returns></returns>
    private ExprNode? Expr()
    {
        ExprNode? left = LogAnd();
        if (left is null)
        {
            return null;
        }

        return LogOrRhs(left);
    }

    /// <summary>
    /// Parses and outputs a logical OR expression, if present.
    /// </summary>
    /// <remarks>
    /// LogOrRhs := OR LogAnd LogOrRhs | ε
    /// </remarks>
    /// <param name="left"></param>
    /// <returns></returns>
    private ExprNode LogOrRhs(ExprNode left)
    {
        if (!ConsumeIfType(TokenType.Or, out Token? orToken))
        {
            return left;
        }
        
        // Parse the right-hand side of the OR expression
        ExprNode? right = LogAnd();

        // TODO: maybe we check for OR lookahead, then try construct with unknown RHS/LHS
        if (right is null)
        {
            return left;
        }
        
        // Recurse to the next OR expression
        return LogOrRhs(new BinaryExprNode(left, orToken, right));
    }

    /// <summary>
    /// Parses and outputs logical AND expressions and higher in precedence, if present.
    /// </summary>
    /// <remarks>
    /// LogAnd := BitOr LogAndRhs
    /// </remarks>
    /// <returns></returns>
    private ExprNode? LogAnd()
    {
        ExprNode? left = BitOr();
        if (left is null)
        {
            return null;
        }

        return LogAndRhs(left);
    }

    /// <summary>
    /// Parses and outputs a logical AND expression, if present.
    /// </summary>
    /// <remarks>
    /// LogAndRhs := AND BitOr LogAndRhs | ε
    /// </remarks>
    /// <param name="left"></param>
    /// <returns></returns>
    private ExprNode LogAndRhs(ExprNode left)
    {
        if (!ConsumeIfType(TokenType.And, out Token? andToken))
        {
            return left;
        }

        // Parse the right-hand side of the AND expression
        ExprNode? right = BitOr();

        // TODO: as above, maybe we check for AND lookahead, then try construct with unknown RHS/LHS
        if (right is null)
        {
            return left;
        }
        
        // Recurse to the next AND expression
        return LogAndRhs(new BinaryExprNode(left, andToken, right));
    }
    
    /// <summary>
    /// Parses and outputs bitwise OR expressions and higher in precedence, if present.
    /// </summary>
    /// <remarks>
    /// BitOr := BitXor BitOrRhs
    /// </remarks>
    /// <returns></returns>
    private ExprNode? BitOr()
    {
        ExprNode? left = BitXor();
        if (left is null)
        {
            return null;
        }

        return BitOrRhs(left);
    }

    /// <summary>
    /// Parses and outputs a bitwise OR expression, if present.
    /// </summary>
    /// <remarks>
    /// BitOrRhs := BITOR BitXor BitOrRhs | ε
    /// </remarks>
    /// <param name="left"></param>
    /// <returns></returns>
    private ExprNode BitOrRhs(ExprNode left)
    {
        if(!ConsumeIfType(TokenType.BitOr, out Token? bitOrToken))
        {
            return left;
        }
        
        // Parse the right-hand side of the BITOR expression
        ExprNode? right = BitXor();
        
        // TODO: as above, maybe we check for BITOR lookahead, then try construct with unknown RHS/LHS
        if (right is null)
        {
            return left;
        }
        
        // Recurse to the next BITOR expression
        return BitOrRhs(new BinaryExprNode(left, bitOrToken, right));
    }

    /// <summary>
    /// Parses and outputs bitwise XOR expressions and higher in precedence, if present.
    /// </summary>
    /// <remarks>
    /// BitXor := BitAnd BitXorRhs
    /// </remarks>
    /// <returns></returns>
    private ExprNode? BitXor()
    {
        ExprNode? left = BitAnd();
        if (left is null)
        {
            return null;
        }

        return BitXorRhs(left);
    }
    
    /// <summary>
    /// Parses and outputs a bitwise XOR expression, if present.
    /// </summary>
    /// <remarks>
    /// BitXorRhs := BITXOR BitAnd BitXorRhs | ε
    /// </remarks>
    /// <param name="left"></param>
    /// <returns></returns>
    private ExprNode BitXorRhs(ExprNode left)
    {
        if(!ConsumeIfType(TokenType.BitXor, out Token? bitXorToken))
        {
            return left;
        }
        
        // Parse the right-hand side of the BITXOR expression
        ExprNode? right = BitAnd();
        
        // TODO: as above, maybe we check for BITXOR lookahead, then try construct with unknown RHS/LHS
        if (right is null)
        {
            return left;
        }
        
        // Recurse to the next BITXOR expression
        return BitXorRhs(new BinaryExprNode(left, bitXorToken, right));
    }
    
    /// <summary>
    /// Parses and outputs bitwise AND expressions and higher in precedence, if present.
    /// </summary>
    /// <remarks>
    /// BitAnd := EqOp BitAndRhs
    /// </remarks>
    /// <returns></returns>
    private ExprNode? BitAnd()
    {
        ExprNode? left = EqOp();
        if (left is null)
        {
            return null;
        }

        return BitAndRhs(left);
    }
    
    /// <summary>
    /// Parses and outputs a bitwise AND expression, if present.
    /// </summary>
    /// <remarks>
    /// BitAndRhs := BITAND EqOp BitAndRhs | ε
    /// </remarks>
    /// <param name="left"></param>
    /// <returns></returns>
    private ExprNode BitAndRhs(ExprNode left)
    {
        if(!ConsumeIfType(TokenType.BitAnd, out Token? bitAndToken))
        {
            return left;
        }
        
        // Parse the right-hand side of the BITAND expression
        ExprNode? right = EqOp();
        
        // TODO: as above, maybe we check for BITAND lookahead, then try construct with unknown RHS/LHS
        if (right is null)
        {
            return left;
        }
        
        // Recurse to the next BITAND expression
        return BitAndRhs(new BinaryExprNode(left, bitAndToken, right));
    }

    /// <summary>
    /// Parses and outputs equality expressions and higher in precedence, if present.
    /// </summary>
    /// <remarks>
    /// EqOp := RelOp EqOpRhs
    /// </remarks>
    /// <returns></returns>
    private ExprNode? EqOp()
    {
        ExprNode? left = RelOp();
        if(left is null)
        {
            return null;
        }
        
        return EqOpRhs(left);
    }
    
    /// <summary>
    /// Parses and outputs an equality expression, if present.
    /// </summary>
    /// <remarks>
    /// EqOpRhs := (EQUALS | NOTEQUALS | IDENTITYEQUALS | IDENTITYNOTEQUALS) RelOp EqOpRhs | ε
    /// </remarks>
    /// <param name="left"></param>
    /// <returns></returns>
    private ExprNode EqOpRhs(ExprNode left)
    {
        if (CurrentTokenType != TokenType.Equals && CurrentTokenType != TokenType.NotEquals &&
            CurrentTokenType != TokenType.IdentityEquals && CurrentTokenType != TokenType.IdentityNotEquals)
        {
            return left;
        }
        
        Token operatorToken = Consume();
        
        // Parse the right-hand side of the equality expression
        ExprNode? right = RelOp();
        
        // TODO: as above, maybe we check for EqOp lookahead, then try construct with unknown RHS/LHS
        if (right is null)
        {
            return left;
        }
        
        // Recurse to the next equality expression
        return EqOpRhs(new BinaryExprNode(left, operatorToken, right));
    }

    /// <summary>
    /// Parses and outputs relational expressions and higher in precedence, if present.
    /// </summary>
    /// <remarks>
    /// RelOp := BitShiftOp RelOpRhs
    /// </remarks>
    /// <returns></returns>
    private ExprNode? RelOp()
    {
        ExprNode? left = BitShiftOp();
        if (left is null)
        {
            return null;
        }

        return RelOpRhs(left);
    }

    /// <summary>
    /// Parses and outputs a relational expression, if present.
    /// </summary>
    /// <remarks>
    /// RelOpRhs := (LESSTHAN | LESSTHANEQUALS | GREATERTHAN | GREATERTHANEQUALS) BitShiftOp RelOpRhs | ε
    /// </remarks>
    /// <param name="left"></param>
    /// <returns></returns>
    private ExprNode RelOpRhs(ExprNode left)
    {
        if (CurrentTokenType != TokenType.LessThan && CurrentTokenType != TokenType.LessThanEquals &&
            CurrentTokenType != TokenType.GreaterThan && CurrentTokenType != TokenType.GreaterThanEquals)
        {
            return left;
        }

        Token operatorToken = Consume();

        // Parse the right-hand side of the relational expression
        ExprNode? right = BitShiftOp();

        // TODO: as above, maybe we check for RelOp lookahead, then try construct with unknown RHS/LHS
        if (right is null)
        {
            return left;
        }

        // Recurse to the next relational expression
        return RelOpRhs(new BinaryExprNode(left, operatorToken, right));
    }

    /// <summary>
    /// Parses and outputs bit shift expressions and higher in precedence, if present.
    /// </summary>
    /// <remarks>
    /// BitShiftOp := AddiOp BitShiftOpRhs
    /// </remarks>
    /// <returns></returns>
    private ExprNode? BitShiftOp()
    {
        ExprNode? left = AddiOp();
        if (left is null)
        {
            return null;
        }

        return BitShiftOpRhs(left);
    }

    /// <summary>
    /// Parses and outputs a bit shift expression, if present.
    /// </summary>
    /// <remarks>
    /// BitShiftOpRhs := (BITLEFTSHIFT | BITRIGHTSHIFT) AddiOp BitShiftOpRhs | ε
    /// </remarks>
    /// <param name="left"></param>
    /// <returns></returns>
    private ExprNode BitShiftOpRhs(ExprNode left)
    {
        if(CurrentTokenType != TokenType.BitLeftShift && CurrentTokenType != TokenType.BitRightShift)
        {
            return left;
        }
        
        Token operatorToken = Consume();
        
        // Parse the right-hand side of the bit shift expression
        ExprNode? right = AddiOp();
        
        // TODO: as above, maybe we check for BitShiftOp lookahead, then try construct with unknown RHS/LHS
        if (right is null)
        {
            return left;
        }
        
        // Recurse to the next bit shift expression
        return BitShiftOpRhs(new BinaryExprNode(left, operatorToken, right));
    }

    /// <summary>
    /// Parses and outputs additive expressions and higher in precedence, if present.
    /// </summary>
    /// <remarks>
    /// AddiOp := MulOp AddiOpRhs
    /// </remarks>
    /// <returns></returns>
    private ExprNode? AddiOp()
    {
        ExprNode? left = MulOp();
        if (left is null)
        {
            return null;
        }

        return AddiOpRhs(left);
    }
    
    /// <summary>
    /// Parses and outputs an additive expression, if present.
    /// </summary>
    /// <remarks>
    /// AddiOpRhs := (PLUS | MINUS) MulOp AddiOpRhs | ε
    /// </remarks>
    /// <param name="left"></param>
    /// <returns></returns>
    private ExprNode AddiOpRhs(ExprNode left)
    {
        if(CurrentTokenType != TokenType.Plus && CurrentTokenType != TokenType.Minus)
        {
            return left;
        }
        
        Token operatorToken = Consume();
        
        // Parse the right-hand side of the additive expression
        ExprNode? right = MulOp();
        
        // TODO: as above, maybe we check for AddiOp lookahead, then try construct with unknown RHS/LHS
        if (right is null)
        {
            return left;
        }
        
        // Recurse to the next additive expression
        return AddiOpRhs(new BinaryExprNode(left, operatorToken, right));
    }

    /// <summary>
    /// Parses and outputs multiplicative expressions and higher in precedence, if present.
    /// </summary>
    /// <remarks>
    /// MulOp := PrefixOp MulOpRhs
    /// </remarks>
    /// <returns></returns>
    private ExprNode? MulOp()
    {
        ExprNode? left = PrefixOp();
        if (left is null)
        {
            return null;
        }

        return MulOpRhs(left);
    }
    
    /// <summary>
    /// Parses and outputs a multiplicative expression, if present.
    /// </summary>
    /// <remarks>
    /// MulOpRhs := (MULTIPLY | DIVIDE | MODULO) PrefixOp MulOpRhs | ε
    /// </remarks>
    /// <param name="left"></param>
    /// <returns></returns>
    private ExprNode MulOpRhs(ExprNode left)
    {
        if(CurrentTokenType != TokenType.Multiply && CurrentTokenType != TokenType.Divide && CurrentTokenType != TokenType.Modulo)
        {
            return left;
        }
        
        Token operatorToken = Consume();
        
        // Parse the right-hand side of the multiplicative expression
        ExprNode? right = PrefixOp();
        
        // TODO: as above, maybe we check for MulOp lookahead, then try construct with unknown RHS/LHS
        if (right is null)
        {
            return left;
        }
        
        // Recurse to the next multiplicative expression
        return MulOpRhs(new BinaryExprNode(left, operatorToken, right));
    }

    /// <summary>
    /// Parses and outputs prefix operators and higher in precedence, if present.
    /// </summary>
    /// <remarks>
    /// PrefixOp := (PLUS | MINUS | BITNOT | NOT | BITAND) PrefixOp | CallOrAccessOp |
    ///             NEW Identifier LPAR RPAR
    /// </remarks>
    /// <returns></returns>
    private ExprNode? PrefixOp()
    {
        switch (CurrentTokenType)
        {
            case TokenType.Plus:
            case TokenType.Minus:
            case TokenType.BitNot:
            case TokenType.Not:
            case TokenType.BitAnd:
                Token operatorToken = Consume();
                
                // Parse the right-hand side of the prefix expression
                ExprNode? operand = PrefixOp();
                if (operand is null)
                {
                    return null;
                }

                return new PrefixExprNode(operatorToken, operand);
            case TokenType.New:
                Advance();
                
                // No need to do scope res, etc. as GSC strictly only looks for an identifier.
                
                if(!ConsumeIfType(TokenType.Identifier, out Token? identifierToken))
                {
                    AddError(GSCErrorCodes.ExpectedClassIdentifier, "identifier", CurrentToken.Lexeme);
                    return null;
                }
                
                // GSC doesn't let you pass arguments to constructors, which is hilarious
                
                // Check for LPAR - TODO: handle these more elegantly
                if (!AdvanceIfType(TokenType.OpenParen))
                {
                    AddError(GSCErrorCodes.ExpectedToken, '(', CurrentToken.Lexeme);
                }
                // Check for RPAR
                if (!AdvanceIfType(TokenType.CloseParen))
                {
                    AddError(GSCErrorCodes.ExpectedToken, ')', CurrentToken.Lexeme);
                }
                
                return new ConstructorExprNode(identifierToken);
            default:
                return CallOrAccessOp();
        }
    }

    /// <summary>
    /// Parses and outputs function call, accessor and higher precedence operations.
    /// </summary>
    /// <remarks>
    /// CallOrAccessOp := OPENBRACKET DerefOrArrayOp | Operand CallOrAccessOpRhs | THREAD DerefOrOperand CallOpRhs
    /// </remarks>
    /// <returns></returns>
    private ExprNode? CallOrAccessOp()
    {
        // Dereferenced operation or an array declaration
        if (ConsumeIfType(TokenType.OpenBracket, out Token? openBracket))
        {
            return DerefOrArrayOp(openBracket);
        }

        // Threaded call without called-on
        if (ConsumeIfType(TokenType.Thread, out Token? threadToken))
        {
            ExprNode? call = DerefOpOrOperandFunCall();
            if(call is null)
            {
                return null;
            }
            return new PrefixExprNode(threadToken, call);
        }
        
        // Could be a function call, operand, accessor, etc.
        ExprNode? left = Operand();
        if (left is null)
        {
            return null;
        }

        return CallOrAccessOpRhs(left);
    }

    /// <summary>
    /// Parses and outputs a dereference function call or a standard function call.
    /// </summary>
    /// <returns></returns>
    private ExprNode? DerefOpOrOperandFunCall()
    {
        if(ConsumeIfType(TokenType.OpenBracket, out Token? openBracket))
        {
            return DerefOp(openBracket);
        }

        ExprNode? left = Operand();
        if(left is null)
        {
            return null;
        }
        return CallOpRhs(left);
    }

    /// <summary>
    /// Parses a dereference-related operation or an array declaration.
    /// </summary>
    /// <remarks>
    /// DerefOrArrayOp := CLOSEBRACKET | DerefOp
    /// </remarks>
    /// <returns></returns>
    private ExprNode? DerefOrArrayOp(Token openBracket)
    {
        // Empty array
        if (ConsumeIfType(TokenType.CloseBracket, out Token? closeBracket))
        {
            return DataExprNode.EmptyArray(openBracket, closeBracket);
        }
        
        // Must be dereferencing
        return DerefOp(openBracket);
    }

    /// <summary>
    /// Parses a called-on dereference call operation or an array indexer.
    /// </summary>
    /// <remarks>
    /// CalledOnDerefOrIndexerOp := Expr CLOSEBRACKET CallOrAccessOpRhs | DerefOp
    /// </remarks>
    /// <param name="left"></param>
    /// <param name="openBracket"></param>
    /// <returns></returns>
    private ExprNode? CalledOnDerefOrIndexerOp(ExprNode left, Token openBracket)
    {
        if (CurrentTokenType == TokenType.OpenBracket)
        {
            ExprNode? derefCall = DerefOp(openBracket);

            if(derefCall is null)
            {
                return null;
            }

            return new CalledOnNode(left, derefCall);
        }

        ExprNode? index = Expr();

        // TODO: we should probably attempt a recovery mechanism if close bracket is encountered here, even if we can't provide info on what the array index is.

        // Check for CLOSEBRACKET
        if (!ConsumeIfType(TokenType.CloseBracket, out Token? closeBracket))
        {
            AddError(GSCErrorCodes.ExpectedToken, ']', CurrentToken.Lexeme);
        }

        return CallOrAccessOpRhs(new ArrayIndexNode(RangeHelper.From(left.Range.Start, closeBracket?.Range.End ?? index?.Range.End ?? left.Range.End), left, index));
    }

    /// <summary>
    /// Parses a dereference call operation.
    /// </summary>
    /// <remarks>
    /// DerefOp := OPENBRACKET Expr CLOSEBRACKET CLOSEBRACKET DerefCallOp
    /// </remarks>
    /// <param name="openBracket"></param>
    /// <returns></returns>
    private ExprNode? DerefOp(Token openBracket)
    {
        // TODO: fault tolerance
        if(!AdvanceIfType(TokenType.OpenBracket))
        {
            AddError(GSCErrorCodes.ExpectedToken, '[', CurrentToken.Lexeme);
        }
        
        // Parse the dereference expression
        ExprNode? derefExpr = Expr();

        if (derefExpr is null)
        {
            EnterRecovery();
        }
            
        // Check for CLOSEBRACKET, twice
        if (!AdvanceIfType(TokenType.CloseBracket) || !AdvanceIfType(TokenType.CloseBracket))
        {
            AddError(GSCErrorCodes.ExpectedToken, ']', CurrentToken.Lexeme);
        }

        ExprNode? result = DerefCallOp(openBracket, derefExpr);
        ExitRecovery();

        return result;
    }

    /// <summary>
    /// Parses the end of a dereference call operation.
    /// </summary>
    /// <remarks>
    /// DerefCallOp := ARROW IDENTIFIER FunCall | FunCall
    /// </remarks>
    /// <param name="openBracket"></param>
    /// <param name="derefExpr"></param>
    /// <returns></returns>
    private ExprNode? DerefCallOp(Token openBracket, ExprNode? derefExpr)
    {
        if (AdvanceIfType(TokenType.Arrow))
        {
            ExitRecovery();
            if (!ConsumeIfType(TokenType.Identifier, out Token? methodToken))
            {
                AddError(GSCErrorCodes.ExpectedMethodIdentifier, CurrentToken.Lexeme);
                return null;
            }
            
            ArgsListNode? methodArgs = FunCall();
            if (methodArgs is null)
            {
                return null;
            }

            MethodCallNode methodCall = new(openBracket.Range.Start, derefExpr, methodToken, methodArgs);
            return CallOrAccessOpRhs(methodCall);
        }
            
        ArgsListNode? funArgs = FunCall();
        if (funArgs is null)
        {
            return null;
        }

        FunCallNode call = new FunCallNode(openBracket.Range.Start, derefExpr, funArgs);
        return CallOrAccessOpRhs(call);
    }

    /// <summary>
    /// Parses the right-hand side of a function call, accessor operation, or called-on threaded/function calls.
    /// </summary>
    /// <remarks>
    /// CallOrAccessOpRhs := CallOpRhs | THREAD CalledOnCallOpRhs | CalledOnCallOpRhs | AccessOpRhs
    /// 
    /// TODO TODO TODO: not sure CallOrAccessOp here (for parsing patterns like self foo::bar()) is correct - this might be a left-recursive loop
    /// </remarks>
    /// <param name="left"></param>
    /// <returns></returns>
    private ExprNode? CallOrAccessOpRhs(ExprNode left)
    {
        // TODO: current grammar won't handle self thread ... but we can bolt this on later
        if(CurrentTokenType == TokenType.ScopeResolution || CurrentTokenType == TokenType.OpenParen)
        {
            return CallOpRhs(left);
        }

        // Threaded called-on ent call
        if(ConsumeIfType(TokenType.Thread, out Token? threadToken))
        {
            ExprNode? call = ThreadedCalledOnRhs(threadToken);
            if(call is null)
            {
                return null;
            }

            return new CalledOnNode(left, call);
        }
        
        // Called-on ent call
        if(CurrentTokenType == TokenType.Identifier || CurrentTokenType == TokenType.OpenBracket)
        {
            return CalledOnRhs(left);
        }

        // Else, array index or accessor
        ExprNode? newLeft = AccessOpRhs(left);

        if(newLeft is null)
        {
            return null;
        }

        // Which itself could still be a called-on target
        if (CurrentTokenType == TokenType.Identifier || CurrentTokenType == TokenType.OpenBracket)
        {
            return CalledOnRhs(newLeft);
        }
        return newLeft;
    }

    /// <summary>
    /// Parses the right-hand side of a called-on function call or an array indexer.
    /// </summary>
    /// <remarks>
    /// CalledOnRhs := IDENTIFIER CallOpRhs | OPENBRACKET CalledOnDerefOrIndexerOp
    /// </remarks>
    /// <param name="left"></param>
    /// <returns></returns>
    private ExprNode? CalledOnRhs(ExprNode left)
    {
        // Called-on with identifier, so it's self foo::bar() or self foo()
        if (ConsumeIfType(TokenType.Identifier, out Token? functionQualifier))
        {
            IdentifierExprNode identifierExprNode = new(functionQualifier);
            if (CurrentTokenType != TokenType.ScopeResolution && CurrentTokenType != TokenType.OpenParen)
            {
                AddError(GSCErrorCodes.ExpectedFunctionQualification, CurrentToken.Lexeme);
                return null;
            }

            ExprNode? call = CallOpRhs(identifierExprNode);
            if(call is null)
            {
                return null;
            }
            return new CalledOnNode(left, call);
        }

        // Called on with dereference e.g. self [[ foo.bar ]](); OR an array indexer self[foo]
        if (ConsumeIfType(TokenType.OpenBracket, out Token? openBracket))
        {
            return CalledOnDerefOrIndexerOp(left, openBracket);
        }

        AddError(GSCErrorCodes.ExpectedFunctionIdentifier, CurrentToken.Lexeme);
        return null;
    }

    /// <summary>
    /// Parses the right-hand side of a called-on threaded/function call operation.
    /// </summary>
    /// <remarks>
    /// ThreadedCalledOnRhs := IDENTIFIER CallOpRhs | OPENBRACKET DerefOp
    /// </remarks>
    /// <returns></returns>
    private ExprNode? ThreadedCalledOnRhs(Token threadToken)
    {
        // Neither matches
        if(CurrentTokenType != TokenType.Identifier && CurrentTokenType != TokenType.OpenBracket)
        {
            AddError(GSCErrorCodes.ExpectedFunctionIdentifier, CurrentToken.Lexeme);
            return null;
        }

        ExprNode? call = null;

        // Called-on with identifier, so it's self foo::bar() or self foo()
        if (ConsumeIfType(TokenType.Identifier, out Token? functionQualifier))
        {
            IdentifierExprNode identifierExprNode = new(functionQualifier);
            if (CurrentTokenType != TokenType.ScopeResolution && CurrentTokenType != TokenType.OpenParen)
            {
                AddError(GSCErrorCodes.ExpectedFunctionQualification, CurrentToken.Lexeme);
                return null;
            }

            call = CallOpRhs(identifierExprNode);
        }

        // Called on with dereference e.g. self [[ foo.bar ]]();
        else if (ConsumeIfType(TokenType.OpenBracket, out Token? openBracket))
        {
            call = DerefOp(openBracket);
        }

        if (call is null)
        {
            return null;
        }

        return new PrefixExprNode(threadToken, call);
    }

    /// <summary>
    /// Parses the right-hand side of a function call operation.
    /// </summary>
    /// <remarks>
    /// CallOpRhs := SCOPERESOLUTION Operand NamespacedFunctionRefOrCall CallOrAccessOpRhs | FunCall CallOrAccessOpRhs
    /// </remarks>
    /// <param name="left"></param>
    /// <returns></returns>
    private ExprNode? CallOpRhs(ExprNode left)
    {
        if (AdvanceIfType(TokenType.ScopeResolution))
        {
            ExprNode? memberOfNamespace = Operand();
            if (memberOfNamespace is null)
            {
                return null;
            }

            NamespacedMemberNode namespacedMember = new(left, memberOfNamespace);

            // Could be a reference or a function call
            ExprNode? refOrCall = NamespacedFunctionRefOrCall(namespacedMember);
            if(refOrCall is null)
            {
                return null;
            }

            return CallOrAccessOpRhs(refOrCall);
        }

        // No namespace, just a function call
        if (CurrentTokenType == TokenType.OpenParen)
        {
            // TODO: fault tolerance
            ArgsListNode? functionArgs = FunCall();
            if (functionArgs is null)
            {
                return null;
            }
            FunCallNode call = new FunCallNode(left, functionArgs);
            return CallOrAccessOpRhs(call);
        }

        AddError(GSCErrorCodes.ExpectedFunctionQualification, CurrentToken.Lexeme);
        return null;
    }

    /// <summary>
    /// Parses and outputs either a referenced to a namespaced function, or otherwise a call to that function.
    /// </summary>
    /// <remarks>
    /// NamespacedFunctionRefOrCall := FunCall | ε
    /// </remarks>
    /// <param name="left"></param>
    /// <returns></returns>
    private ExprNode? NamespacedFunctionRefOrCall(NamespacedMemberNode left)
    {
        // Not a call, just a reference.
        if (CurrentTokenType != TokenType.OpenParen)
        {
            return left;
        }

        // Call
        ArgsListNode? functionArgs = FunCall();
        if (functionArgs is null)
        {
            return null;
        }

        return new FunCallNode(left, functionArgs);
    }

    /// <summary>
    /// Parses and outputs a function call argument list node.
    /// </summary>
    /// <remarks>
    /// FunCall := OPENPAREN ArgsList CLOSEPAREN | OPENPAREN CLOSEPAREN
    /// </remarks>
    /// <returns></returns>
    private ArgsListNode? FunCall()
    {
        if(!ConsumeIfType(TokenType.OpenParen, out Token? openParen))
        {
            AddError(GSCErrorCodes.ExpectedToken, '(', CurrentToken.Lexeme);
            return null;
        }

        // Empty argument list
        if (ConsumeIfType(TokenType.CloseParen, out Token? closeParen))
        {
            return new ArgsListNode
            {
                Range = RangeHelper.From(openParen.Range.Start, closeParen.Range.End)
            };
        }

        // Parse the argument list
        ArgsListNode argsList = ArgsList();

        // Check for CLOSEPAREN
        if (!ConsumeIfType(TokenType.CloseParen, out Token? lastToken))
        {
            AddError(GSCErrorCodes.ExpectedToken, ')', CurrentToken.Lexeme);
            lastToken = CurrentToken.Previous;
        }

        argsList.Range = RangeHelper.From(openParen.Range.Start, lastToken.Range.End);
        return argsList;
    }

    /// <summary>
    /// Parses and outputs a 1-or-more argument list node.
    /// </summary>
    /// <remarks>
    /// ArgsList := Expr ArgsListRhs
    /// </remarks>
    /// <returns></returns>
    private ArgsListNode ArgsList()
    {
        ExprNode? firstArgument = Expr();

        ArgsListNode rest = ArgsListRhs();
        rest.Arguments.AddFirst(firstArgument);

        return rest;
    }

    /// <summary>
    /// Parses and outputs additional arguments in an argument list node.
    /// </summary>
    /// <remarks>
    /// ArgsListRhs := COMMA Expr ArgsListRhs | ε
    /// </remarks>
    /// <returns></returns>
    private ArgsListNode ArgsListRhs()
    {
        // No more arguments
        if (!AdvanceIfType(TokenType.Comma))
        {
            return new();
        }

        ExprNode? nextArgument = Expr();

        ArgsListNode rest = ArgsListRhs();
        rest.Arguments.AddFirst(nextArgument);

        return rest;
    }

    /// <summary>
    /// Parses and outputs the right-hand side of an accessor operation.
    /// </summary>
    /// <remarks>
    /// AccessOpRhs := DOT Operand CallOrAccessOpRhs | OPENBRACKET Expr CLOSEBRACKET CallOrAccessOpRhs | ε
    /// </remarks>
    /// <param name="left"></param>
    /// <returns></returns>
    private ExprNode? AccessOpRhs(ExprNode left)
    {
        // Accessor
        if (ConsumeIfType(TokenType.Dot, out Token? dotToken))
        {
            ExprNode? right = Operand();

            return CallOrAccessOpRhs(new BinaryExprNode(left, dotToken, right));
        }
        
        // Array index
        if (ConsumeIfType(TokenType.OpenBracket, out Token? openBracket))
        {
            ExprNode? index = Expr();
            
            // Check for CLOSEBRACKET
            if (!ConsumeIfType(TokenType.CloseBracket, out Token? closeBracket))
            {
                AddError(GSCErrorCodes.ExpectedToken, ']', CurrentToken.Lexeme);
            }

            return CallOrAccessOpRhs(new ArrayIndexNode(RangeHelper.From(left.Range.Start, closeBracket?.Range.End ?? index?.Range.End ?? openBracket.Range.End), left, index));
        }

        // Empty - just an operand further up
        return left;
    }

    /// <summary>
    /// Parses and outputs an operand within the context of an expression.
    /// </summary>
    /// <remarks>
    /// Operand :=  Number | String | Bool | OPENPAREN ParenExpr CLOSEPAREN | IDENTIFIER |
    ///             COMPILERHASH | ANIMIDENTIFIER | ANIMTREE
    /// </remarks>
    /// <returns></returns>
    private ExprNode? Operand()
    {
        switch (CurrentTokenType)
        {
            // All the primitives
            case TokenType.Integer:
            case TokenType.Float:
            case TokenType.Hex:
            case TokenType.String:
            case TokenType.IString:
            case TokenType.True:
            case TokenType.False:
            case TokenType.Undefined:
            case TokenType.CompilerHash:
            case TokenType.AnimTree:
                Token primitiveToken = CurrentToken;
                Advance();
                return DataExprNode.From(primitiveToken);
            // Could be a ternary expression, parenthesised expression, or a vector.
            case TokenType.OpenParen:
                Advance();
                ExprNode? parenExpr = ParenExpr();
                
                // Check for CLOSEPAREN
                if (!AdvanceIfType(TokenType.CloseParen))
                {
                    AddError(GSCErrorCodes.ExpectedToken, ')', CurrentToken.Lexeme);
                }

                return parenExpr;
            // Identifier & anim identifier
            case TokenType.Identifier:
            case TokenType.AnimIdentifier:
                Token identifierToken = CurrentToken;
                Advance();
                return new IdentifierExprNode(identifierToken);
        }

        // Expected an operand
        AddError(GSCErrorCodes.ExpectedExpressionTerm, CurrentToken.Lexeme);
        return null;
    }

    /// <summary>
    /// Parses and outputs a parenthesised sub-expression, which could also be a ternary or vector expression.
    /// </summary>
    /// <remarks>
    /// ParenExpr := Expr ConditionalOrVector
    /// </remarks>
    /// <returns></returns>
    private ExprNode? ParenExpr()
    {
        ExprNode? expr = Expr();

        if (expr is null)
        {
            return null;
        }
        
        // Could be a ternary expression or a vector
        return ConditionalOrVector(expr);
    }

    /// <summary>
    /// Parses and outputs a ternary or vector expression if present.
    /// </summary>
    /// <param name="leftmostExpr">The leftmost subexpression of this node.</param>
    /// <remarks>
    /// ConditionalOrVector := QUESTIONMARK Expr COLON Expr | COMMA Expr COMMA Expr | ε
    /// </remarks>
    /// <returns></returns>
    private ExprNode? ConditionalOrVector(ExprNode leftmostExpr)
    {
        // Ternary expression
        if (AdvanceIfType(TokenType.QuestionMark))
        {
            ExprNode? trueExpr = Expr();

            if(trueExpr is null)
            {
                EnterRecovery();
            }
            
            // Check for COLON
            if (!AdvanceIfType(TokenType.Colon))
            {
                AddError(GSCErrorCodes.ExpectedToken, ':', CurrentToken.Lexeme);
                ExitRecovery();
                return null;
            }
            ExitRecovery();
            
            ExprNode? falseExpr = Expr();
            
            return new TernaryExprNode(leftmostExpr, trueExpr, falseExpr);
        }
        
        // Not a vector expression, just a parenthesised sub-expression
        if (!AdvanceIfType(TokenType.Comma))
        {
            return leftmostExpr;
        }
        
        // Vector expression
        
        ExprNode? secondExpr = Expr();

        if (secondExpr is null)
        {
            EnterRecovery();
        }
            
        // Check for COMMA
        if (!AdvanceIfType(TokenType.Comma))
        {
            AddError(GSCErrorCodes.ExpectedToken, ',', CurrentToken.Lexeme);
            EnterRecovery();
        }
            
        ExprNode? thirdExpr = Expr();
            
        ExitRecovery();
        return new VectorExprNode(leftmostExpr, secondExpr, thirdExpr);
    }

    private bool InLoopOrSwitch()
    {
        return (ContextFlags & ParserContextFlags.InLoopBody) != 0 || (ContextFlags & ParserContextFlags.InSwitchBody) != 0;
    }

    private bool InLoop()
    {
        return (ContextFlags & ParserContextFlags.InLoopBody) != 0;
    }
    
    private bool EnterContextIfNewly(ParserContextFlags context)
    {
        // Already in this context further down.
        if ((ContextFlags & context) != 0)
        {
            return false;
        }
        
        ContextFlags |= context;
        return true;
    }
    
    private bool ExitContextIfWasNewly(ParserContextFlags context, bool wasNewly)
    {
        if (wasNewly)
        {
            ContextFlags ^= context;
        }
        return wasNewly;
    }
    

    private void EnterRecovery()
    {
        Silent = true;
        SnapshotToken = CurrentToken;
    }

    private void ExitRecovery()
    {
        Silent = false;
    }

    private bool InRecovery() => Silent;

    private void Advance()
    {
        PreviousToken = CurrentToken;
        do
        {
            CurrentToken = CurrentToken.Next;
        }
        // Ignore all whitespace and comments.
        while (
            CurrentTokenType == TokenType.Whitespace || 
            CurrentTokenType == TokenType.LineComment ||
            CurrentTokenType == TokenType.MultilineComment ||
            CurrentTokenType == TokenType.DocComment ||
            CurrentTokenType == TokenType.LineBreak);
    }

    private Token Consume()
    {
        Token consumed = CurrentToken;
        Advance();
        
        return consumed;
    }

    private bool AdvanceIfType(TokenType type)
    {
        if(CurrentTokenType == type)
        {
            Advance();
            return true;
        }

        return false;
    }

    private bool ConsumeIfType(TokenType type, [NotNullWhen(true)] out Token? consumed)
    {
        Token current = CurrentToken;
        if(AdvanceIfType(type))
        {
            consumed = current;
            return true;
        }

        consumed = default;
        return false;
    }
    
    private void AddError(GSCErrorCodes errorCode, params object?[] args)
    {
        // We're in a fault recovery state
        if(Silent)
        {
            return;
        }

        Sense.AddAstDiagnostic(CurrentTokenRange, errorCode, args);
    }

    private void AddErrorAtEndOfPrevious(GSCErrorCodes errorCode, params object?[] args)
    {
        // We're in a fault recovery state
        if (Silent)
        {
            return;
        }

        Sense.AddAstDiagnostic(RangeHelper.From(new Position(PreviousToken.Range.End.Line, Math.Max(0, PreviousToken.Range.End.Character - 1)), PreviousToken.Range.End), errorCode, args);
    }
}
