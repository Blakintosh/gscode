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
    private AssignmentExprNode? AssignmentExpr()
    {
        // TODO: Fault tolerant logic
        // Parse the left-hand side of the assignment
        ExprNode left = Operand();
        
        // Parse the assignment operator
        AssignOpNode? op = AssignOp();
    }

    private ExprNode Expr()
    {
        
    }

    /// <summary>
    /// Parses and outputs an operand within the context of an expression.
    /// </summary>
    /// <remarks>
    /// Operand :=  Number | String | Bool | OPENPAREN ParenExpr CLOSEPAREN | OPENBRACKET ArrayOrDeref | IDENTIFIER |
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
            // Could be an array definition, or a dereference over an object or function ptr.
            case TokenType.OpenBracket:
                return ArrayOrDeref();
            // Identifier
            case TokenType.Identifier:
                Token identifierToken = CurrentToken;
                Advance();
                return new IdentifierExprNode(identifierToken);
        }
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
    
    /// <summary>
    /// Parses and outputs either an array definition or a dereference expression.
    /// </summary>
    /// <remarks>
    /// ArrayOrDeref := CLOSEBRACKET | OPENBRACKET Expr CLOSEBRACKET CLOSEBRACKET
    /// </remarks>
    /// <returns></returns>
    private ExprNode ArrayOrDeref()
    {
        // Advance over OPENBRACKET
        Token openBracket = Consume();
        
        // This is an empty array
        if (ConsumeIfType(TokenType.CloseBracket, out Token? closeBracket))
        {
            return DataExprNode.EmptyArray(openBracket, closeBracket);
        }
        
        // This is a dereference expression
        if (AdvanceIfType(TokenType.OpenBracket))
        {
            // Parse the dereference expression
            ExprNode derefExpr = Expr();
            
            // Check for CLOSEBRACKET, twice
            if (!AdvanceIfType(TokenType.CloseBracket) && !AdvanceIfType(TokenType.CloseBracket))
            {
                AddError(GSCErrorCodes.ExpectedToken, ']', CurrentToken.Lexeme);
            }

            return DereferenceExprNode(derefExpr);
        }
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
