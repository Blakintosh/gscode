using GSCode.Parser.Lexer;
using System;
using System.Collections.Generic;
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
    /// <param name="currentToken"></param>
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
    /// FunBraceBlock := OPENBRACE FunStmtList CLOSEBRACE
    /// </remarks>
    /// <returns></returns>
    private StmtListNode FunBraceBlock()
    {
        // Pass over OPENBRACE
        Advance();

        // Parse the statements in the block
        StmtListNode stmtListNode = FunStmtList();

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
    /// FunStmtList := FunStmt FunStmtList | ε
    /// </remarks>
    /// <returns></returns>
    private StmtListNode FunStmtList()
    {
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
            // Expressions
            case TokenType.Identifier:
                ASTNode? statement = FunStmt();

                StmtListNode rest = FunStmtList();
                rest.Statements.AddFirst(statement);

                return rest;
            // Everything else - empty case
            default:
                return new();
        }
    }

    /// <summary>
    /// Parses a single statement in a function brace block.
    /// </summary>
    /// <remarks>
    /// FunStmt := IfElseStmt | DoWhileStmt | WhileStmt | ForStmt | ForeachStmt | SwitchStmt | ReturnStmt | WaittillFrameEndStmt | WaitStmt | WaitRealTimeStmt | ConstStmt | DevBlock | BraceBlock | ExprStmt
    /// </remarks>
    /// <returns></returns>
    private ASTNode? FunStmt()
    {
        switch (CurrentTokenType)
        {
            case TokenType.If:
                return IfElseStmt();
            case TokenType.Do:
                return DoWhileStmt();
            case TokenType.While:
                return WhileStmt();
            case TokenType.For:
                return ForStmt();
            case TokenType.Foreach:
                return ForeachStmt();
            case TokenType.Switch:
                return SwitchStmt();
            case TokenType.Return:
                return ReturnStmt();
            case TokenType.WaittillFrameEnd:
                return WaittillFrameEndStmt();
            case TokenType.Wait:
                return WaitStmt();
            case TokenType.WaitRealTime:
                return WaitRealTimeStmt();
            case TokenType.Const:
                return ConstStmt();
            case TokenType.OpenDevBlock:
                return DevBlock();
            case TokenType.OpenBrace:
                return BraceBlock();
            case TokenType.Semicolon:
                Advance();
                return new EmptyStmtNode();
            case TokenType.Identifier:
                return ExprStmt();
        }

        return null;
    }

    private IfElseStmtNode IfElseStmt()
    {
        IfElseStmtNode firstBranch = IfStmt();
    }

    private IfElseStmtNode IfStmt()
    {
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
        ASTNode? then = FunStmt();

        // Parse the false branch, if present
        // TODO: ElseStmt production

        return new IfStmtNode(condition, then, falseBranch);
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
        // Got a keyword, prepend it to our keyword list
        if (CurrentTokenType == TokenType.Private || CurrentTokenType == TokenType.Autoexec)
        {
            Token keywordToken = CurrentToken;
            Advance();

            FunKeywordsNode node = FunKeywords();
            node.Keywords.AddFirst(keywordToken);

            return node;
        }


        // Empty production - base case
        return new();
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

    private bool AdvanceIfType(TokenType type)
    {
        if(CurrentTokenType == type)
        {
            Advance();
            return true;
        }

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
