using GSCode.Parser.Lexer;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using GSCode.Data;
using GSCode.Parser.Data;
using System.Xml.XPath;

namespace GSCode.Parser.AST;

/// <summary>
/// An implementation of an LL(1) recursive descent parser for the GSC & CSC languages.
/// </summary>
internal class Parser(Token startToken, ParserIntelliSense sense)
{
    public Token CurrentToken { get; private set; } = startToken;

    public TokenType CurrentTokenType => CurrentToken.Type;
    public Range CurrentTokenRange => CurrentToken.Range;
    
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
        List<ScriptDefnNode> scriptDefns = ScriptList();
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
        if (CurrentTokenType != TokenType.Semicolon)
        {
            AddError(GSCErrorCodes.ExpectedSemiColon, "using directive");
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

        Advance();

        Token segmentToken = CurrentToken;
        if (CurrentTokenType != TokenType.Identifier)
        {
            // Expected a path segment
            AddError(GSCErrorCodes.ExpectedPathSegment, CurrentToken.Lexeme);

            return null;
        }

        Advance();

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
    private List<ASTNode> ScriptList()
    {
        List<ASTNode> scriptDefns = new List<ASTNode>();

        // Keep parsing script definitions until we reach the end of the file, as this is our last production.
        while(CurrentTokenType != TokenType.Eof)
        {
            ASTNode? next = ScriptDefn();

            // Success
            if(next is not null)
            {
                scriptDefns.Add(next);
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
    private ASTNode? ScriptDefn()
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
            AddError(GSCErrorCodes.ExpectedSemiColon, "precache directive");
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
            AddError(GSCErrorCodes.ExpectedSemiColon, "using animation tree directive");
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
            AddError(GSCErrorCodes.ExpectedSemiColon, "namespace directive");
        }

        return new NamespaceNode(namespaceToken);
    }

    /// <summary>
    /// Parses and outputs a function definition node.
    /// </summary>
    /// <remarks>
    /// FunDefn := FUNCTION FunKeywords IDENTIFIER OPENPAREN ParamList CLOSEPAREN FunBraceBlock
    /// </remarks>
    /// <returns></returns>
    private FunDefnNode? FunDefn()
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
            AddError(GSCErrorCodes.ExpectedFunctionIdentifier, CurrentToken.Lexeme);
            EnterRecovery();
        }

        // Check for OPENPAREN
        if (!AdvanceIfType(TokenType.OpenParen))
        {
            AddError(GSCErrorCodes.ExpectedToken, '(', CurrentToken.Lexeme);
        }
        else
        {
            ExitRecovery();

            // Parse the argument list.
            ParamListNode parameters = ParamList();
        }

        // Check for CLOSEPAREN
        if (!AdvanceIfType(TokenType.CloseParen))
        {
            AddError(GSCErrorCodes.ExpectedToken, ')', CurrentToken.Lexeme);
        }

        // Check for the brace block, then parse it.
        if(CurrentTokenType != TokenType.OpenBrace)
        {
            AddError(GSCErrorCodes.ExpectedToken, '{', CurrentToken.Lexeme);
        }

        ExitRecovery();
        StmtListNode block = FunBraceBlock();
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
                ASTNode? statement = Stmt();

                StmtListNode rest = StmtList();
                rest.Statements.AddFirst(statement);

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
    private ASTNode? Stmt(ParserContextFlags newContext = ParserContextFlags.None)
    {
        bool isNewContext = false;
        if(newContext != ParserContextFlags.None)
        {
            isNewContext = EnterContextIfNewly(newContext);
        }
        
        ASTNode? result = CurrentTokenType switch
        {
            TokenType.If => IfElseStmt(),
            TokenType.Do => DoWhileStmt(),
            TokenType.While => WhileStmt(),
            TokenType.For => ForStmt(),
            TokenType.Foreach => ForeachStmt(),
            TokenType.Switch => SwitchStmt(),
            TokenType.Return => ReturnStmt(),
            TokenType.WaittillFrameEnd => ControlFlowActionStmt(ASTNodeType.WaittillFrameEndStmt),
            TokenType.Wait => ReservedFuncStmt(ASTNodeType.WaitStmt),
            TokenType.WaitRealTime => ReservedFuncStmt(ASTNodeType.WaitRealTimeStmt),
            TokenType.Const => ConstStmt(),
            TokenType.OpenDevBlock => DevBlock(),
            TokenType.OpenBrace => FunBraceBlock(),
            TokenType.Break when InLoopOrSwitch() => ControlFlowActionStmt(ASTNodeType.BreakStmt),
            TokenType.Continue when InLoop() => ControlFlowActionStmt(ASTNodeType.ContinueStmt),
            TokenType.Semicolon => EmptyStmt(),
            TokenType.Identifier => ExprStmt(),
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
        ExprNode condition = Expr();

        // Check for CLOSEPAREN
        if (!AdvanceIfType(TokenType.CloseParen))
        {
            AddError(GSCErrorCodes.ExpectedToken, ')', CurrentToken.Lexeme);
        }

        // Parse the then branch
        ASTNode? then = Stmt();
        
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
    private IfStmtNode? ElseClause()
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
        ASTNode? then = Stmt();

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
        ASTNode then = Stmt(ParserContextFlags.InLoopBody);
        
        // Check for WHILE
        if (!AdvanceIfType(TokenType.While))
        {
            AddError(GSCErrorCodes.ExpectedToken, "while", CurrentToken.Lexeme);
        }
        
        // Check for OPENPAREN
        if (!AdvanceIfType(TokenType.OpenParen))
        {
            AddError(GSCErrorCodes.ExpectedToken, '(', CurrentToken.Lexeme);
        }
        
        // Parse the loop's condition
        ExprNode condition = Expr();
        
        // Check for CLOSEPAREN
        if (!AdvanceIfType(TokenType.CloseParen))
        {
            AddError(GSCErrorCodes.ExpectedToken, ')', CurrentToken.Lexeme);
        }
        
        // Check for SEMICOLON
        if (!AdvanceIfType(TokenType.Semicolon))
        {
            AddError(GSCErrorCodes.ExpectedSemiColon, "do-while loop");
        }
        
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
        ExprNode condition = Expr();
        
        // Check for CLOSEPAREN
        if (!AdvanceIfType(TokenType.CloseParen))
        {
            AddError(GSCErrorCodes.ExpectedToken, ')', CurrentToken.Lexeme);
        }
        
        // Parse the loop's body, update context.
        ASTNode then = Stmt(ParserContextFlags.InLoopBody);
        
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
        AssignmentExprNode? init = AssignmentExpr();
        
        // Check for SEMICOLON
        if (!AdvanceIfType(TokenType.Semicolon))
        {
            AddError(GSCErrorCodes.ExpectedSemiColon, "for loop");
        }
        
        // Parse the loop's condition
        ExprNode? condition = Expr();
        
        // Check for SEMICOLON
        if (!AdvanceIfType(TokenType.Semicolon))
        {
            AddError(GSCErrorCodes.ExpectedSemiColon, "for loop");
        }
        
        // Parse the loop's increment
        AssignmentExprNode? increment = AssignmentExpr();
        
        // Check for CLOSEPAREN
        if (!AdvanceIfType(TokenType.CloseParen))
        {
            AddError(GSCErrorCodes.ExpectedToken, ')', CurrentToken.Lexeme);
        }
        
        // Parse the loop's body, update context.
        ASTNode then = Stmt(ParserContextFlags.InLoopBody);
        
        return new(init, condition, increment, then);
    }

    /// <summary>
    /// Parses a foreach statement, including its then clause and collection.
    /// </summary>
    /// <remarks>
    /// ForeachStmt := FOREACH OPENPAREN IDENTIFIER IN Expr CLOSEPAREN Stmt
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
        Token identifierToken = CurrentToken;
        if (!AdvanceIfType(TokenType.Identifier))
        {
            AddError(GSCErrorCodes.ExpectedForeachIdentifier, CurrentToken.Lexeme);
        }
        
        // Check for IN
        if (!AdvanceIfType(TokenType.In))
        {
            AddError(GSCErrorCodes.ExpectedToken, "in", CurrentToken.Lexeme);
        }
        
        // Parse the loop's collection
        ExprNode collection = Expr();
        
        // Check for CLOSEPAREN
        if (!AdvanceIfType(TokenType.CloseParen))
        {
            AddError(GSCErrorCodes.ExpectedToken, ')', CurrentToken.Lexeme);
        }
        
        // Parse the loop's body, update context.
        ASTNode then = Stmt(ParserContextFlags.InLoopBody);
        
        return new ForeachStmtNode(identifierToken, collection, then);
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
        ExprNode expression = Expr();
        
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
            return new(ASTNodeType.DefaultLabel);
        }
        
        // Case label
        if (!AdvanceIfType(TokenType.Case))
        {
            AddError(GSCErrorCodes.ExpectedToken, "case", CurrentToken.Lexeme);
        }
        
        // Parse the case's expression
        ExprNode expression = Expr();
        
        // Check for COLON
        if (!AdvanceIfType(TokenType.Colon))
        {
            AddError(GSCErrorCodes.ExpectedToken, ':', CurrentToken.Lexeme);
        }
        
        return new(ASTNodeType.CaseLabel, expression);
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
            AddError(GSCErrorCodes.ExpectedSemiColon, "return statement");
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
    private ControlFlowActionNode ControlFlowActionStmt(ASTNodeType type)
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
            ASTNodeType.WaittillFrameEndStmt => "waittillframeend statement",
            ASTNodeType.BreakStmt => "break statement",
            ASTNodeType.ContinueStmt => "continue statement",
            _ => throw new ArgumentOutOfRangeException(nameof(type), "Invalid control flow action type")
        };
        AddError(GSCErrorCodes.ExpectedSemiColon, statementName);

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
    private ReservedFuncStmtNode ReservedFuncStmt(ASTNodeType type)
    {
        // Pass over WAIT, WAITREALTIME, etc.
        Advance();
        
        // Get the function's expression
        ExprNode expr = Expr();
        
        // Check for SEMICOLON
        if (AdvanceIfType(TokenType.Semicolon))
        {
            return new(type, expr);
        }

        string statementName = type switch
        {
            ASTNodeType.WaitStmt => "wait statement",
            ASTNodeType.WaitRealTimeStmt => "waitrealtime statement",
            _ => throw new ArgumentOutOfRangeException(nameof(type), "Invalid reserved function type")
        };
        AddError(GSCErrorCodes.ExpectedSemiColon, statementName);
        
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
        ExprNode value = Expr();
        
        // Check for SEMICOLON
        if (!AdvanceIfType(TokenType.Semicolon))
        {
            AddError(GSCErrorCodes.ExpectedSemiColon, "constant declaration");
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
        AssignmentExprNode expr = AssignmentExpr();
        
        // Check for SEMICOLON
        if (!AdvanceIfType(TokenType.Semicolon))
        {
            AddError(GSCErrorCodes.ExpectedSemiColon, "expression statement");
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
        ParamNode? first = Param();
        if(first is null)
        {
            // Failed
            return new();
        }

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
        ParamNode? next = Param();
        if(next is null)
        {
            // Failed
            return new();
        }

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
    private ParamNode? Param()
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
            if(CurrentTokenType == TokenType.Comma || CurrentTokenType == TokenType.CloseParen)
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
    /// Parses and outputs a full assignment expression.
    /// </summary>
    /// <remarks>
    /// AssignmentExpr := Operand AssignOp
    /// </remarks>
    /// <returns></returns>
    private ExprNode? AssignmentExpr()
    {
        // TODO: Fault tolerant logic
        // Parse the left-hand side of the assignment
        // TODO: in practice, this could return null.
        ExprNode left = Operand();
        
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
    private ExprNode? AssignOp(ExprNode left)
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
                
                // TODO: in practice, this could return null.
                ExprNode right = Expr();
                return new BinaryExprNode(left, operatorToken, right);
            default:
                // ERROR: Expected an assignment operator
                AddError(GSCErrorCodes.ExpectedAssignmentOperator, CurrentToken.Lexeme);
                return null;
        };
    }

    /// <summary>
    /// Parses and outputs a full arithmetic or logical expression.
    /// </summary>
    /// <remarks>
    /// Expr := LogAnd LogOrRhs
    /// </remarks>
    /// <returns></returns>
    private ExprNode Expr()
    {
        ExprNode? left = LogAnd();

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
    /// PrefixOp := (PLUS | MINUS | BITNOT | NOT | BITAND) PrefixOp | CallOrAccessOp | THREAD ThreadedCallOp |
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
                
                return new PrefixExprNode(operatorToken, operand);
            case TokenType.Thread:
                // TODO: this might need to be moved further down into CallOrAccessOp
                Advance();
                
                return ThreadedCallOp();
            case TokenType.New:
                Advance();
                
                // No need to do scope res, etc. as GSC strictly only looks for an identifier.
                
                if(!ConsumeIfType(TokenType.Identifier, out Token? identifierToken))
                {
                    AddError(GSCErrorCodes.ExpectedClassIdentifier, "identifier", CurrentToken.Lexeme);
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
    /// CallOrAccessOp := OPENBRACKET DerefOrArrayOp | Operand CallOrAccessOpRhs | THREAD Operand CallOpRhs
    /// </remarks>
    /// <returns></returns>
    private ExprNode? CallOrAccessOp()
    {
        // Dereferenced operation or an array declaration
        if (ConsumeIfType(TokenType.OpenBracket, out Token? openBracket))
        {
            return DerefOrArrayOp(openBracket);
        }

        // Threaded call
        if (ConsumeIfType(TokenType.Thread, out Token? threadToken))
        {
            ExprNode? leftQualifier = Operand();
            return new PrefixExprNode(threadToken, CallOpRhs(leftQualifier));
        }
        
        // Could be a function call, operand, accessor, etc.
        ExprNode? left = Operand();

        return CallOrAccessOpRhs(left);
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
        
        // Must be a dereference
        return DerefOp(openBracket);
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
        ExprNode derefExpr = Expr();
            
        // Check for CLOSEBRACKET, twice
        if (!AdvanceIfType(TokenType.CloseBracket) && !AdvanceIfType(TokenType.CloseBracket))
        {
            AddError(GSCErrorCodes.ExpectedToken, ']', CurrentToken.Lexeme);
        }

        return DerefCallOp(openBracket, derefExpr);
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
    private ExprNode? DerefCallOp(Token openBracket, ExprNode derefExpr)
    {
        if (AdvanceIfType(TokenType.Arrow))
        {
            if (!ConsumeIfType(TokenType.Identifier, out Token? methodToken))
            {
                AddError(GSCErrorCodes.ExpectedMethodIdentifier, CurrentToken.Lexeme);
            }
            
            ArgsListNode? methodArgs = FunCall();
            
            return new MethodCallNode(openBracket.Range.Start, derefExpr, methodToken, methodArgs);
        }
            
        ArgsListNode? funArgs = FunCall();
        return new FunCallNode(openBracket.Range.Start, derefExpr, funArgs);
    }

    /// <summary>
    /// Parses the right-hand side of a function call, accessor operation, or called-on threaded/function calls.
    /// </summary>
    /// <remarks>
    /// CallOrAccessOpRhs := CallOpRhs | AccessOpRhs | CallOrAccessOp
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

        // TODO: self foo::bar() is a thing, but we don't handle it yet
        // TODO TODO TODO: not sure CallOrAccessOp here (for parsing patterns like self foo::bar()) is correct - this is a left-recursive loop
        // we can take advantage of the fact that self foo.bar() is not valid syntax - it's self [[foo.bar]]() so just discriminate based on whether
        // the next token is openbracket (for deref) or otherwise go for an Operand and then CallOpRhs

        // Otherwise - accessor or array index
        return AccessOpRhs(left);
    }

    /// <summary>
    /// Parses the right-hand side of a function call operation.
    /// </summary>
    /// <remarks>
    /// CallOpRhs := SCOPERESOLUTION Operand FunCall | FunCall
    /// </remarks>
    /// <param name="left"></param>
    /// <returns></returns>
    private ExprNode? CallOpRhs(ExprNode left)
    {
        if (AdvanceIfType(TokenType.ScopeResolution))
        {
            // TODO: fault tolerance
            ExprNode memberOfNamespace = Operand();

            NamespacedMemberRefNode namespacedMember = new(left, memberOfNamespace);
            ArgsListNode? functionArgs = FunCall();

            return new FunCallNode(namespacedMember, functionArgs);
        }

        // No namespace, just a function call
        if (CurrentTokenType == TokenType.OpenParen)
        {
            // TODO: fault tolerance
            ArgsListNode? functionArgs = FunCall();
            return new FunCallNode(left, functionArgs);
        }
    }

    /// <summary>
    /// Parses and outputs the right-hand side of an accessor operation.
    /// </summary>
    /// <remarks>
    /// AccessOpRhs := DOT Operand AccessOpRhs | OPENBRACKET Expr CLOSEBRACKET AccessOpRhs | ε
    /// </remarks>
    /// <param name="left"></param>
    /// <returns></returns>
    private ExprNode? AccessOpRhs(ExprNode left)
    {
        // Accessor
        if (ConsumeIfType(TokenType.Dot, out Token? dotToken))
        {
            ExprNode? right = Operand();

            return AccessOpRhs(new BinaryExprNode(left, dotToken, right));
        }
        
        // Array index
        if (ConsumeIfType(TokenType.OpenBracket, out Token? openBracket))
        {
            ExprNode index = Expr();
            
            // Check for CLOSEBRACKET
            if (!ConsumeIfType(TokenType.CloseBracket, out Token? closeBracket))
            {
                AddError(GSCErrorCodes.ExpectedToken, ']', CurrentToken.Lexeme);
            }

            return AccessOpRhs(new ArrayIndexNode(RangeHelper.From(left.Range.Start, closeBracket.Range.End), left, index));
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
    private ExprNode Operand()
    {
        // TODO: Fault tolerant logic
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
                ExprNode parenExpr = ParenExpr();
                
                // Check for CLOSEPAREN
                if (!AdvanceIfType(TokenType.CloseParen))
                {
                    AddError(GSCErrorCodes.ExpectedToken, ')', CurrentToken.Lexeme);
                }

                return parenExpr;
            // Identifier
            case TokenType.Identifier:
                Token identifierToken = CurrentToken;
                Advance();
                return new IdentifierExprNode(identifierToken);
        }
        
        // ERROR
    }

    /// <summary>
    /// Parses and outputs a parenthesised sub-expression, which could also be a ternary or vector expression.
    /// </summary>
    /// <remarks>
    /// ParenExpr := Expr ConditionalOrVector
    /// </remarks>
    /// <returns></returns>
    private ExprNode ParenExpr()
    {
        ExprNode expr = Expr();
        
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
    private ExprNode ConditionalOrVector(ExprNode leftmostExpr)
    {
        // Ternary expression
        if (AdvanceIfType(TokenType.QuestionMark))
        {
            ExprNode trueExpr = Expr();
            
            // Check for COLON
            if (!AdvanceIfType(TokenType.Colon))
            {
                AddError(GSCErrorCodes.ExpectedToken, ':', CurrentToken.Lexeme);
            }
            
            ExprNode falseExpr = Expr();
            
            return new TernaryExprNode(leftmostExpr, trueExpr, falseExpr);
        }
        
        // Not a vector expression, just a parenthesised sub-expression
        if (!AdvanceIfType(TokenType.Comma))
        {
            return leftmostExpr;
        }
        
        // Vector expression
        
        ExprNode secondExpr = Expr();
            
        // Check for COMMA
        if (!AdvanceIfType(TokenType.Comma))
        {
            AddError(GSCErrorCodes.ExpectedToken, ',', CurrentToken.Lexeme);
        }
            
        ExprNode thirdExpr = Expr();
            
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

    private void Advance()
    {
        CurrentToken = CurrentToken.Next;
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
    
    private void AddError(GSCErrorCodes errorCode, params object[]? args)
    {
        // We're in a fault recovery state
        if(Silent)
        {
            return;
        }

        Sense.AddAstDiagnostic(CurrentTokenRange, errorCode, args);
    }
}
