using GSCode.Data;
using GSCode.Parser.AST;
using GSCode.Parser.Data;
using GSCode.Parser.Lexical;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace GSCode.Parser.Pre;

internal ref partial struct Preprocessor(Token startToken, ParserIntelliSense sense)
{
    private Token CurrentToken { get; set; } = startToken;

    private readonly TokenType CurrentTokenType => CurrentToken.Type;
    private readonly Range CurrentTokenRange => CurrentToken.Range;

    private ParserIntelliSense Sense { get; } = sense;

    private Dictionary<string, MacroDefinition> Defines { get; } = new();

    public void Process()
    {
        Token startToken = CurrentToken;

        // pass 1 - expand all system-defined macros, track all defines and apply inserts.
        // pass 2 - evaluate all #if/#elif directives.
        // pass 3 - expand all user-defined macros and system-defined macros as a result of those (if necessary).
        FirstPass();
        CurrentToken = startToken;

        SecondPass();
        CurrentToken = startToken;

        ThirdPass();
    }

    /// <summary>
    /// Performs preprocessor transformations on the script token sequence.
    /// </summary>
    public void FirstPass()
    {
        while(CurrentTokenType != TokenType.Eof)
        {
            switch(CurrentTokenType)
            {
                case TokenType.Define:
                    Define();
                    break;
                case TokenType.Insert:
                    Insert();
                    break;
                default:
                    // is it a system macro reference?
                    if((CurrentTokenType == TokenType.Identifier || CurrentToken.IsKeyword()) &&
                        TryGetSystemDefinedMacroDefinition(CurrentToken, out MacroDefinition? macro))
                    {
                        Macro(macro);
                        break;
                    }
                    Advance();
                    break;
            }
        }
    }

    public void SecondPass()
    {
        while (CurrentTokenType != TokenType.Eof)
        {
            switch (CurrentTokenType)
            {
                case TokenType.PreIf:
                    IfDirective();
                    break;
                case TokenType.PreElIf:
                    AddError(GSCErrorCodes.MisplacedPreprocessorDirective, CurrentToken.Lexeme);
                    // Consume the directive so it doesn't cause further issues
                    SkipMisplacedDirective();

                    break;
                case TokenType.PreElse:
                case TokenType.PreEndIf:
                    AddError(GSCErrorCodes.MisplacedPreprocessorDirective, CurrentToken.Lexeme);

                    // Delete the directive so it doesn't cause further issues
                    ConnectTokens(CurrentToken.Previous, CurrentToken.Next);
                    Advance();
                    break;
                default:
                    Advance();
                    break;
            }
        }
    }

    public void ThirdPass()
    {
        // Now expand user-defined macros and any system-defined macros that weren't expanded in the first pass.
        while(CurrentTokenType != TokenType.Eof)
        {
            if((CurrentTokenType == TokenType.Identifier || CurrentToken.IsKeyword()) &&
                TryGetMacroDefinition(CurrentToken, out MacroDefinition? macro))
            {
                Macro(macro);
                continue;
            }
            Advance();
        }
    }

    /// <summary>
    /// Transforms a macro definition into a script define.
    /// </summary>
    private void Define()
    {
        // Pass over DEFINE
        Token defineToken = Consume();

        // Get the macro name
        Token nameToken = CurrentToken;

        // Macros can be either keywords or identifiers
        if (CurrentTokenType != TokenType.Identifier && !CurrentToken.IsKeyword())
        {
            AddError(GSCErrorCodes.ExpectedMacroIdentifier, CurrentToken.Lexeme);
            return;
        }
        
        CurrentToken = CurrentToken.Next;

        string macroName = nameToken.Lexeme;

        // Get its parameter list, if it has one.
        LinkedList<Token>? parameters = ParamList();

        // In order to exclude backslashes/linebreaks, we'll have to create a copy of the token list for the expansion
        Token? firstExpansionToken = null;
        Token? lastExpansionToken = null;
        Token? previousToken = null;
        Token current = CurrentToken;

        while (current.Type != TokenType.LineBreak && current.Type != TokenType.Eof)
        {
            // Handle backslash here, which must immediately precede a line break if encountered
            if (current.Type == TokenType.Backslash)
            {
                if (current.Next.Type != TokenType.LineBreak)
                {
                    AddError(GSCErrorCodes.InvalidLineContinuation, "\\");
                }
                else
                {
                    // Skip both the backslash and linebreak
                    current = current.Next.Next;  // Skip past backslash and linebreak
                    continue;  // Continue to next token without adding these to expansion
                }
            }

            // Clone the current token and link it
            var newToken = current with { };

            firstExpansionToken ??= newToken;
            newToken.Previous = previousToken ?? newToken;
            if (previousToken is not null)
            {
                previousToken.Next = newToken;
            }

            previousToken = newToken;
            lastExpansionToken = newToken;
            current = current.Next;
        }
        CurrentToken = defineToken.Previous;

        if (firstExpansionToken is not null)
        {
            lastExpansionToken = current;
        }

        // Consume skips comments, so if there is one at the end of this expansion we can get it directly from working backwards from CurrentToken
        Token documentationToken = current.Previous;
        string? documentation = null;

        // TODO: this currently doesn't remove the //, etc.
        if (documentationToken.IsComment())
        {
            documentation = documentationToken.Lexeme;
        }

        // Remove the define directive from the script.
        ConnectTokens(defineToken.Previous, current.Next);

        // Create the macro
        MacroDefinition definition = new(
            nameToken,
            DefineTokens: new TokenList(defineToken, current),
            ExpansionTokens: new TokenList(firstExpansionToken, lastExpansionToken),
            Parameters: parameters,
            Documentation: documentation
            );

        // GSC doesn't allow redefinitions of existing macros.
        if(TryGetMacroDefinition(nameToken, out _))
        {
            Sense.AddPreDiagnostic(nameToken.Range, GSCErrorCodes.DuplicateMacroDefinition, macroName);
        }
        else
        {
            // Fine to add
            Defines.Add(macroName, definition);
            // Determine source display for macro (use nearest insert region if any)
            string? srcDisplay = null;
            foreach (var region in Sense.InsertRegions)
            {
                // If the define tokens fall after an insert range on the same line, prefer that region
                if (region.Range.Start.Line <= nameToken.Range.Start.Line && region.ResolvedPath is not null)
                {
                    string rel = GetRelativeDisplay(region.ResolvedPath);
                    srcDisplay = rel;
                }
            }
            Sense.AddMacroOutline(macroName, nameToken.Range, srcDisplay);
        }
        Sense.AddSenseToken(nameToken, definition);

        static string GetRelativeDisplay(string fullPath)
        {
            try
            {
                // Show trailing two segments if possible, e.g., shared/shared.gsh
                string dir = System.IO.Path.GetDirectoryName(fullPath) ?? string.Empty;
                string file = System.IO.Path.GetFileName(fullPath);
                string lastDir = string.IsNullOrEmpty(dir) ? string.Empty : System.IO.Path.GetFileName(dir);
                return string.IsNullOrEmpty(lastDir) ? file : System.IO.Path.Combine(lastDir, file).Replace('\\', '/');
            }
            catch { return fullPath; }
        }
    }

    /// <summary>
    /// Consumes an #elif directive (including its condition) in the case that it's misplaced, so it doesn't cause
    /// AST errors.
    /// </summary>
    private void SkipMisplacedDirective()
    {
        Token startToken = CurrentToken;
        Token current = CurrentToken;

        // Scan until newline or EOF
        while (current.Next is not null && current.Next.Type != TokenType.LineBreak && current.Next.Type != TokenType.Eof)
        {
            current = current.Next;
        }

        // Remove the directive and condition tokens but preserve the newline/EOF
        Token endToken = current;
        ConnectTokens(startToken.Previous, endToken.Next!);

        // Update current token to point after the removed section
        CurrentToken = endToken.Next!;
    }

    /// <summary>
    /// Parses a macro definition's parameter list, if it exists.
    /// </summary>
    /// <returns></returns>
    private LinkedList<Token>? ParamList()
    {
        // No parameters
        if(!AdvanceIfType(TokenType.OpenParen))
        {
            return null;
        }

        LinkedList<Token> result = Params();

        // Check for CLOSEPAREN
        if(!AdvanceIfType(TokenType.CloseParen))
        {
            AddError(GSCErrorCodes.ExpectedPreprocessorToken, ")", CurrentToken.Lexeme);
        }

        return result;
    }

    /// <summary>
    /// Parses zero or more macro definition parameters.
    /// </summary>
    /// <returns></returns>
    private LinkedList<Token> Params()
    {
        // Used to duplicate check
        HashSet<string> set = [];

        // Zero parameters
        if (!ConsumeIfType(TokenType.Identifier, out Token? parameterToken))
        {
            return [];
        }

        set.Add(parameterToken.Lexeme);

        LinkedList<Token> rest = ParamsRhs(set);
        rest.AddFirst(parameterToken);

        return rest;
    }

    /// <summary>
    /// Parses the right-hand side of a macro definition's parameters.
    /// </summary>
    /// <returns></returns>
    private LinkedList<Token> ParamsRhs(HashSet<string> set)
    {
        // End of parameter list
        if (!ConsumeIfType(TokenType.Comma, out Token? commaToken))
        {
            return [];
        }

        // Get the next parameter's name
        if (CurrentTokenType != TokenType.Identifier && !CurrentToken.IsKeyword())
        {
            AddError(GSCErrorCodes.ExpectedMacroParameter, CurrentToken.Lexeme);
            return [];
        }
        Token parameterToken = Consume();

        // Duplicate parameter
        bool isDuplicate = !set.Add(parameterToken.Lexeme);
        if(isDuplicate)
        {
            AddErrorAtToken(GSCErrorCodes.DuplicateMacroParameter, parameterToken, parameterToken.Lexeme);
        }

        // Recurse
        LinkedList<Token> rest = ParamsRhs(set);
        if(!isDuplicate)
        {
            rest.AddFirst(parameterToken);
        }

        return rest;
    }

    [GeneratedRegex(@"(^[\\/])|(^[a-zA-Z]:)|(\.\.)")]
    private static partial Regex InvalidPathRegex();

    /// <summary>
    /// Transforms an insert directive into the file's contents.
    /// </summary>
    private void Insert()
    {
        // Pass over INSERT
        Token insertToken = Consume();

        // Continously consume tokens until we get to a semicolon or line break.
        TokenList path = Path(out Token? terminatorToken);

        if(path.IsEmpty)
        {
            // Pass over semicolon so we can remove it too - if it's the others, we don't want to remove those.
            AdvanceIfType(TokenType.Semicolon);

            ConnectTokens(insertToken.Previous, CurrentToken);
            return;
        }

        // Check the path is relative and doesn't exit the project directory
        string filePath = path.ToRawString();
        if(InvalidPathRegex().IsMatch(filePath))
        {
            AddErrorAtRange(GSCErrorCodes.InvalidInsertPath, path.Range!, filePath);

            if(terminatorToken!.Type == TokenType.Semicolon)
            {
                ConnectTokens(insertToken.Previous, terminatorToken.Next);
                return;
            }
            ConnectTokens(insertToken.Previous, terminatorToken!.Previous);
            return;
        }

        // Record hover on the #insert path text for navigation
        Sense.HoverLibrary.Add(new InsertDirectiveHover(filePath, path.Range!));
        // Track insert region and its resolved path to later attribute definitions
        string? resolvedInsertPath = Sense.ResolveInsertPath(filePath, path.Range!);
        Sense.AddInsertRegion(path.Range!, filePath, resolvedInsertPath);

        // If the insert path could not be resolved, a diagnostic was already added in ResolveInsertPath.
        // Remove the directive to avoid AST errors and return early to prevent a duplicate diagnostic.
        if (resolvedInsertPath is null)
        {
            if (terminatorToken!.Type == TokenType.Semicolon)
            {
                ConnectTokens(insertToken.Previous, terminatorToken.Next);
            }
            else
            {
                ConnectTokens(insertToken.Previous, terminatorToken!.Previous);
            }
            return;
        }

        // Get the file contents
        TokenList? insertTokensResult;
        try
        {
            insertTokensResult = Sense.GetFileTokens(filePath, path.Range);
        }
        catch(Exception ex)
        {
            Sense.AddIdeDiagnostic(path.Range!, GSCErrorCodes.FailedToReadInsertFile, filePath, ex.GetType().Name);
            return;
        }

        // If we got null back then the file doesn't exist
        if(insertTokensResult is not TokenList insertTokens)
        {
            AddErrorAtRange(GSCErrorCodes.MissingInsertFile, path.Range!, filePath);
            // Remove the insert directive from the token stream to prevent AST errors
            if (terminatorToken!.Type == TokenType.Semicolon)
            {
                ConnectTokens(insertToken.Previous, terminatorToken.Next);
            }
            else
            {
                ConnectTokens(insertToken.Previous, terminatorToken!.Previous);
            }
            return;
        }

        // If the inserted file has no content (only SOF/EOF), remove the directive entirely
        if (insertTokens.Start!.Next == insertTokens.End!.Previous)
        {
            if (terminatorToken!.Type == TokenType.Semicolon)
            {
                ConnectTokens(insertToken.Previous, terminatorToken.Next);
            }
            else
            {
                ConnectTokens(insertToken.Previous, terminatorToken!.Previous);
            }
            return;
        }
        
        // Otherwise, it's a unique instance so connect its boundaries (exc. the SOF and EOF) to the insert directive
        ConnectTokens(insertToken.Previous, insertTokens.Start!.Next);
        CurrentToken = insertTokens.Start.Next;

        // Connect past semicolon - otherwise directly to the terminator
        if (terminatorToken!.Type == TokenType.Semicolon)
        {
            ConnectTokens(insertTokens.End!.Previous, terminatorToken.Next);
            return;
        }
        ConnectTokens(insertTokens.End!.Previous, terminatorToken);
    }

    private TokenList Path(out Token? terminatorIfMatched)
    {
        // Empty path
        if(CurrentTokenType == TokenType.Semicolon || CurrentTokenType == TokenType.LineBreak || CurrentTokenType == TokenType.Eof)
        {
            terminatorIfMatched = null;
            AddError(GSCErrorCodes.ExpectedInsertPath);
            return TokenList.Empty;
        }

        Token start = Consume();
        Token current = start;

        while(CurrentTokenType != TokenType.Semicolon && CurrentTokenType != TokenType.LineBreak && CurrentTokenType != TokenType.Eof)
        {
            current = Consume();
        }

        // Consume one more time so we can get the terminator and go back one from it for the full, whitespace-included path.
        terminatorIfMatched = Consume();

        if(terminatorIfMatched.Type == TokenType.LineBreak || terminatorIfMatched.Type == TokenType.Eof)
        {
            AddErrorAtToken(GSCErrorCodes.ExpectedPreprocessorToken, terminatorIfMatched, ';', terminatorIfMatched.Lexeme);
        }

        return new TokenList(start, terminatorIfMatched.Previous);
    }

    private void Macro(MacroDefinition macroDefinition)
    {
        if(macroDefinition.Parameters is null)
        {
            MacroWithoutArgs(macroDefinition);
            return;
        }
        MacroWithArgs(macroDefinition);
    }

    private void MacroWithoutArgs(MacroDefinition macroDefinition)
    {
        Token macroToken = Consume();

        // Clone the expansion, adding them with the macro token's range
        TokenList expansion = macroDefinition.ExpansionTokens.CloneList(macroToken.Range);

        // Connect them to the surrounding tokens
        expansion.ConnectToTokens(macroToken.Previous, macroToken.Next);

        // Make sure we're at the beginning, as macros can contain macros.
        CurrentToken = macroToken.Previous;

        // Finally, add the macro reference to IntelliSense
        Sense.AddSenseToken(macroToken, new ScriptMacro(macroToken, macroDefinition, expansion));
    }

    private void MacroWithArgs(MacroDefinition macroDefinition)
    {
        // Get the macro token
        Token macroToken = Consume();

        // The macro should have an arguments list but doesn't, so it'll be ignored.
        if (!AdvanceIfType(TokenType.OpenParen))
        {
            AddErrorAtToken(GSCErrorCodes.MissingMacroParameterList, macroToken, macroToken.Lexeme);
            return;
        }

        // Get the arguments
        LinkedList<TokenList?> arguments = MacroArgs(macroToken, macroDefinition.Parameters!);

        // Check for )
        if(!AdvanceIfType(TokenType.CloseParen))
        {
            AddError(GSCErrorCodes.ExpectedPreprocessorToken, ")", CurrentToken.Lexeme);
        }
        Token endAnchorToken = CurrentToken;

        // Start with a cloned expansion, then replace references to parameters with the argument expansions.
        TokenList expansion = macroDefinition.ExpansionTokens.CloneList(macroToken.Range);

        // Before doing anything to it, connect it to the macro token
        expansion.ConnectToTokens(macroToken.Previous, endAnchorToken);

        // To do this, we'll need to map identifier names to their argument expansions.
        Dictionary<string, TokenList?> argumentMappings = new();

        LinkedListNode<Token>? parameter = macroDefinition.Parameters!.First;
        LinkedListNode<TokenList?>? argument = arguments.First;

        while(parameter is not null && argument is not null)
        {
            argumentMappings[parameter.Value.Lexeme] = argument.Value;

            parameter = parameter.Next;
            argument = argument.Next;
        }

        // Replace parameter references with their argument expansions.
        Token? current = expansion.Start;
        while(current is not null)
        {
            // Curren token is a ref
            if((current.Type == TokenType.Identifier || current.IsKeyword()) && argumentMappings.TryGetValue(current.Lexeme, out TokenList? parameterExpansionResult))
            {
                // It's left blank, so just remove the identifier
                if(parameterExpansionResult is not TokenList parameterExpansion)
                {
                    Token next = current.Next;
                    ConnectTokens(current.Previous, next);

                    // Update expansion.Start if needed
                    if (current == expansion.Start)
                    {
                        expansion.Start = next;
                    }
                    // Update expansion.End if needed
                    if (current == expansion.End)
                    {
                        expansion.End = current.Previous;
                        break;
                    }

                    current = next;
                    continue;
                }

                // Otherwise, clone the expansion then connect it to where the identifier formerly was
                TokenList clonedExpansion = parameterExpansion.CloneList();

                clonedExpansion.ConnectToTokens(current.Previous, current.Next);

                // Going to current.Next is fine as it's still one-way connected to beyond the end of the expansion
                if (current == expansion.Start)
                {
                    expansion.Start = clonedExpansion.Start;
                }
                if (current == expansion.End)
                {
                    expansion.End = clonedExpansion.End;
                    break;
                }
            }

            if (current == expansion.End)
            {
                break;
            }
            current = current.Next;
        }

        // Then finally, connect the macro token to the expansion
        expansion.ConnectToTokens(macroToken.Previous, endAnchorToken);

        // Make sure we're at the beginning, as macros can contain macros.
        CurrentToken = macroToken.Previous;

        // Job done (who knew with args would be so much more complex!)
        // Finally, add the macro reference to IntelliSense
        Sense.AddSenseToken(macroToken, new ScriptMacro(macroToken, macroDefinition, expansion));
    }

    /// <summary>
    /// Parses one or more macro expansion arguments.
    /// </summary>
    /// <param name="macroToken"></param>
    /// <param name="parameters"></param>
    /// <returns></returns>
    private LinkedList<TokenList?> MacroArgs(Token macroToken, IEnumerable<Token> parameters)
    {
        // Get the first argument
        TokenList? argument = MacroArgExpansion();

        // Get the rest of the arguments
        LinkedList<TokenList?> rest = MacroArgsRhs(macroToken, parameters, 1);
        rest.AddFirst(argument);

        return rest;
    }

    /// <summary>
    /// Parses the right-hand side of a macro expansion's arguments.
    /// </summary>
    /// <param name="macroToken"></param>
    /// <param name="parameters"></param>
    /// <param name="index"></param>
    /// <param name="alreadyErroredAboutArgumentCount"></param>
    /// <returns></returns>
    private LinkedList<TokenList?> MacroArgsRhs(Token macroToken, IEnumerable<Token> parameters, int index, bool alreadyErroredAboutArgumentCount = false)
    {
        int expectedParameterCount = parameters.Count();

        // At the end of the argument list
        if (!ConsumeIfType(TokenType.Comma, out Token? commaToken))
        {
            // But we didn't get enough arguments
            if(expectedParameterCount != index)
            {
                AddError(GSCErrorCodes.TooFewMacroArguments, macroToken.Lexeme, expectedParameterCount);
            }
            return [];
        }

        // Check we're not about to go above the expected parameter count, continue parsing even if we are though.
        if(index + 1 > expectedParameterCount && !alreadyErroredAboutArgumentCount)
        {
            alreadyErroredAboutArgumentCount = true;
            AddErrorAtToken(GSCErrorCodes.TooManyMacroArguments, commaToken!, macroToken.Lexeme, expectedParameterCount);
        }

        // Get the next argument
        TokenList? argument = MacroArgExpansion();

        // Get the rest of the arguments
        LinkedList<TokenList?> rest = MacroArgsRhs(macroToken, parameters, index + 1, alreadyErroredAboutArgumentCount);
        rest.AddFirst(argument);

        return rest;
    }

    /// <summary>
    /// Parses a macro argument's expansion.
    /// </summary>
    /// <returns></returns>
    private TokenList? MacroArgExpansion()
    {
        ExpansionState state = new();

        Token start = CurrentToken;
        Token? current = null;

        while(!state.ShouldEndExpansion(CurrentTokenType))
        {
            // Maintain the indexes looking ahead at the token we're about to consume
            state.TrackToken(CurrentTokenType);

            // Go to the next token, consuming our current one
            current = Consume();
        }

        // Nothing to do, it was an empty expansion
        if (current is null)
        {
            return null;
        }

        return new TokenList(start, current);
    }
    
    private record struct ExpansionState(int ParenIndex = 0, int BraceIndex = 0, int BracketIndex = 0)
    {
        private bool InPunctuation => ParenIndex > 0 || BraceIndex > 0 || BracketIndex > 0;

        public void TrackToken(TokenType currentTokenType)
        {
            switch (currentTokenType)
            {
                case TokenType.OpenParen:
                    ParenIndex++;
                    break;
                case TokenType.CloseParen when ParenIndex > 0:
                    ParenIndex--;
                    break;
                case TokenType.OpenBracket:
                    BracketIndex++;
                    break;
                case TokenType.CloseBracket when BracketIndex > 0:
                    BracketIndex--;
                    break;
                case TokenType.OpenBrace:
                    BraceIndex++;
                    break;
                case TokenType.CloseBrace when BraceIndex > 0:
                    BraceIndex--;
                    break;
            }
        }

        public bool ShouldEndExpansion(TokenType currentTokenType)
        {
            if (InPunctuation)
            {
                return false;
            }
        
            return currentTokenType == TokenType.Comma || currentTokenType == TokenType.CloseParen || currentTokenType == TokenType.Eof;
        }
    }

    /// <summary>
    /// Transforms a preprocessor if directive, emitting the tokens that meet the condition or any else tokens.
    /// </summary>
    private void IfDirective()
    {
        // The #if, expand to the right of it
        Token startAnchorToken = CurrentToken;
        Advance();

        // The condition
        int? condition = IfCondition();

        // For this branch to be taken, the condition must have parsed and resolved to a non-zero integer.
        bool conditionMet = condition is { } conditionInt && conditionInt != 0;

        // Go till we've definitely reached the end of the directive line
        while(CurrentTokenType != TokenType.LineBreak && CurrentTokenType != TokenType.Eof)
        {
            Advance();
        }

        Token endAnchorToken = CurrentToken;

        // We can now "delete" the if directive entirely.
        ConnectTokens(startAnchorToken.Previous, endAnchorToken);

        // Track nesting level of #if directives
        int nestingLevel = 1;

        // Now get the whole branch.
        while(CurrentTokenType != TokenType.Eof)
        {
            // Entering nested if
            if(CurrentTokenType == TokenType.PreIf)
            {
                nestingLevel++;
            }
            // Exiting nested if
            else if(CurrentTokenType == TokenType.PreEndIf)
            {
                nestingLevel--;
                if(nestingLevel == 0)
                {
                    break;
                }
            }
            // If we're at the top level, we're done
            else if(nestingLevel == 1)
            {
                if(CurrentTokenType == TokenType.PreElIf || CurrentTokenType == TokenType.PreElse)
                {
                    break;
                }
            }
            Advance();
        }

        // If we're at the end of the file, we've got an error.
        if(CurrentTokenType == TokenType.Eof)
        {
            AddErrorAtToken(GSCErrorCodes.UnterminatedPreprocessorDirective, CurrentToken.Previous, "#if");
            return;
        }

        // If the condition was not met, delete the whole branch.
        if(!conditionMet)
        {
            AddErrorAtRange(GSCErrorCodes.InactivePreprocessorBranch, RangeHelper.From(endAnchorToken.Next.Range.Start, CurrentToken.Previous.Range.End));
            ConnectTokens(startAnchorToken.Previous, CurrentToken);
        }

        // If we're at #endif, we're done and need to delete it.
        if(CurrentTokenType == TokenType.PreEndIf)
        {
            ConnectTokens(startAnchorToken.Previous, CurrentToken.Next);
            Advance();
            return;
        }

        // Otherwise, we're at an #elif or #else, so we need to continue parsing.
        if(CurrentTokenType == TokenType.PreElse)
        {
            ElseDirective(conditionMet);
            return;
        }
        ElifDirective(conditionMet);

        // Finally, go back to the start of the directive so nested ones can be processed.
        CurrentToken = startAnchorToken.Previous;
    }

    private void ElifDirective(bool conditionAlreadyMet)
    {
        // Store the elif token to delete it later.
        Token startAnchorToken = CurrentToken;
        Advance();

        // Parse the condition.
        int? conditionResult = IfCondition();
        bool conditionMet = !conditionAlreadyMet && conditionResult is int result && result != 0;

        // Go till we've definitely reached the end of the directive line
        while(CurrentTokenType != TokenType.LineBreak && CurrentTokenType != TokenType.Eof)
        {
            Advance();
        }

        Token endAnchorToken = CurrentToken;

        // We can now "delete" the elif directive entirely.
        ConnectTokens(startAnchorToken.Previous, endAnchorToken);

        // Track nesting level of #if directives
        int nestingLevel = 1;

        // Now get the whole branch.
        while(CurrentTokenType != TokenType.Eof)
        {
            // Entering nested if
            if(CurrentTokenType == TokenType.PreIf)
            {
                nestingLevel++;
            }
            // Exiting nested if
            else if(CurrentTokenType == TokenType.PreEndIf)
            {
                nestingLevel--;
                if(nestingLevel == 0)
                {
                    break;
                }
            }
            // If we're at the top level, we're done
            else if(nestingLevel == 1)
            {
                if(CurrentTokenType == TokenType.PreElIf || CurrentTokenType == TokenType.PreElse)
                {
                    break;
                }
            }
            Advance();
        }

        // If we're at the end of the file, we've got an error.
        if(CurrentTokenType == TokenType.Eof)
        {
            AddErrorAtToken(GSCErrorCodes.UnterminatedPreprocessorDirective, CurrentToken.Previous, "#elif");
            return;
        }

        // If the condition was not met, delete the whole branch.
        if(!conditionMet)
        {
            AddErrorAtRange(GSCErrorCodes.InactivePreprocessorBranch, RangeHelper.From(endAnchorToken.Next.Range.Start, CurrentToken.Previous.Range.End));
            ConnectTokens(startAnchorToken.Previous, CurrentToken);
        }

        // If we're at #endif, we're done and need to delete it.
        if(CurrentTokenType == TokenType.PreEndIf)
        {
            ConnectTokens(startAnchorToken.Previous, CurrentToken.Next);
            Advance();
            return;
        }

        // Otherwise, we're at another #elif or #else, so we need to continue parsing.
        if(CurrentTokenType == TokenType.PreElse)
        {
            ElseDirective(conditionAlreadyMet || conditionMet);
            return;
        }
        ElifDirective(conditionAlreadyMet || conditionMet);
    }

    private void ElseDirective(bool conditionAlreadyMet)
    {
        // Store the else token to delete it later.
        Token startAnchorToken = CurrentToken;
        Advance();

        Token endAnchorToken = CurrentToken;

        // Track nesting level of #if directives
        int nestingLevel = 1;

        // Now get the whole branch.
        while(CurrentTokenType != TokenType.Eof)
        {
            // Entering nested if
            if(CurrentTokenType == TokenType.PreIf)
            {
                nestingLevel++;
            }
            // Exiting nested if
            else if(CurrentTokenType == TokenType.PreEndIf)
            {
                nestingLevel--;
                if(nestingLevel == 0)
                {
                    break;
                }
            }
            Advance();
        }

        // If we're at the end of the file, we've got an error.
        if(CurrentTokenType == TokenType.Eof)
        {
            AddErrorAtToken(GSCErrorCodes.UnterminatedPreprocessorDirective, CurrentToken.Previous, "#else");
        }

        // Otherwise we're at the #endif

        // Delete the whole branch if a previous condition was met
        if(conditionAlreadyMet)
        {
            AddErrorAtRange(GSCErrorCodes.InactivePreprocessorBranch, RangeHelper.From(endAnchorToken.Next.Range.Start, CurrentToken.Previous.Range.End));
            ConnectTokens(startAnchorToken.Previous, CurrentToken.Next);
            Advance();
            return;
        }

        // Otherwise, delete the #else and the #endif but keep the rest.
        ConnectTokens(startAnchorToken.Previous, startAnchorToken.Next);

        if(CurrentTokenType == TokenType.PreEndIf)
        {
            ConnectTokens(CurrentToken.Previous, CurrentToken.Next);
            Advance();
        }
    }

    /// <summary>
    /// Parses an if directive's condition, starting with a logical OR operation.
    /// </summary>
    /// <returns></returns>
    private int? IfCondition()
    {
        int? leftResult = LogicalAndOp();
        if(leftResult is not int left)
        {
            return null;
        }

        return LogicalOrRhsOp(left);
    }

    /// <summary>
    /// Parses the right-hand side of an if directive's condition, starting with a logical OR operation.
    /// </summary>
    /// <param name="left"></param>
    /// <returns></returns>
    private int? LogicalOrRhsOp(int left)
    {
        if(!AdvanceIfType(TokenType.Or))
        {
            return left;
        }

        int? rightResult = LogicalAndOp();
        if(rightResult is not int right)
        {
            return null;
        }

        int localResult = (left != 0 || right != 0) ? 1 : 0;
        return LogicalOrRhsOp(localResult);
    }

    /// <summary>
    /// Parses the left-hand side of a logical AND operation in an if directive's condition.
    /// </summary>
    /// <returns></returns>
    private int? LogicalAndOp()
    {
        int? leftResult = EqualityOp();
        if(leftResult is not int left)
        {
            return null;
        }

        return LogicalAndRhsOp(left);
    }

    /// <summary>
    /// Parses the right-hand side of a logical AND operation in an if directive's condition.
    /// </summary>
    /// <param name="left"></param>
    /// <returns></returns>
    private int? LogicalAndRhsOp(int left)
    {
        if(!AdvanceIfType(TokenType.And))
        {
            return left;
        }

        int? rightResult = EqualityOp();
        if(rightResult is not int right)
        {
            return null;
        }

        int localResult = (left != 0 && right != 0) ? 1 : 0;
        return LogicalAndRhsOp(localResult);
    }

    /// <summary>
    /// Parses a logical equality operation in an if directive's condition.
    /// </summary>
    /// <returns></returns>
    private int? EqualityOp()
    {
        int? leftResult = RelationalOp();
        if(leftResult is not int left)
        {
            return null;
        }

        return EqualityRhsOp(left);
    }

    /// <summary>
    /// Parses the right-hand side of a logical equality operation in an if directive's condition.
    /// </summary>
    /// <param name="left"></param>
    /// <returns></returns>
    private int? EqualityRhsOp(int left)
    {
        TokenType operatorType = CurrentTokenType;
        if(operatorType != TokenType.Equals && operatorType != TokenType.NotEquals)
        {
            return left;
        }
        Advance();

        int? rightResult = RelationalOp();
        if(rightResult is not int right)
        {
            return null;
        }

        // Equality
        if(operatorType == TokenType.Equals)
        {
            return (left == right) ? 1 : 0;
        }
        // Inequality
        return (left != right) ? 1 : 0;
    }

    /// <summary>
    /// Parses a relational operation in an if directive's condition.
    /// </summary>
    /// <returns></returns>
    private int? RelationalOp()
    {
        int? targetResult = OpTarget();
        if(targetResult is not int target)
        {
            return null;
        }

        return RelationalRhsOp(target);
    }

    /// <summary>
    /// Parses the right-hand side of a relational operation in an if directive's condition.
    /// </summary>
    /// <param name="target"></param>
    /// <returns></returns>
    private int? RelationalRhsOp(int target)
    {
        TokenType operatorType = CurrentTokenType;
        if(operatorType != TokenType.LessThan && operatorType != TokenType.LessThanEquals && operatorType != TokenType.GreaterThan && operatorType != TokenType.GreaterThanEquals)
        {
            return target;
        }
        Advance();

        int? rightResult = OpTarget();
        if(rightResult is not int right)
        {
            return null;
        }

        return operatorType switch
        {
            TokenType.LessThan => (target < right) ? 1 : 0,
            TokenType.LessThanEquals => (target <= right) ? 1 : 0,
            TokenType.GreaterThan => (target > right) ? 1 : 0,
            TokenType.GreaterThanEquals => (target >= right) ? 1 : 0,
            _ => null
        };
    }

    /// <summary>
    /// Parses a target of an operation in an if directive's condition.
    /// </summary>
    /// <returns></returns>
    private int? OpTarget()
    {
        // Parenthesised sub-expression
        if(AdvanceIfType(TokenType.OpenParen))
        {
            int? result = IfCondition();
            if(result is not int resultInt)
            {
                return null;
            }

            if(!AdvanceIfType(TokenType.CloseParen))
            {
                return null;
            }
            return resultInt;
        }

        // Got an integer we can work with
        if(ConsumeIfType(TokenType.Integer, out Token? integerToken))
        {
            return int.Parse(integerToken.Lexeme);
        }

        // Token isn't supported - whole expression fails.
        return null;
    }

    private void ConnectTokens(Token left, Token right)
    {
        left.Next = right;
        right.Previous = left;
    }


    private void AddError(GSCErrorCodes errorCode, params object?[] args)
    {
        Sense.AddPreDiagnostic(CurrentTokenRange, errorCode, args);
    }

    private void AddErrorAtToken(GSCErrorCodes errorCode, Token token, params object?[] args)
    {
        Sense.AddPreDiagnostic(token.Range, errorCode, args);
    }
    private void AddErrorAtRange(GSCErrorCodes errorCode, Range range, params object?[] args)
    {
        Sense.AddPreDiagnostic(range, errorCode, args);
    }

    private void Advance()
    {
        do
        {
            CurrentToken = CurrentToken.Next;
        }
        // Ignore all whitespace and comments, but don't ignore line breaks.
        while (
            CurrentTokenType == TokenType.Whitespace ||
            CurrentTokenType == TokenType.LineComment ||
            CurrentTokenType == TokenType.MultilineComment ||
            CurrentTokenType == TokenType.DocComment);
    }

    private Token Consume()
    {
        Token consumed = CurrentToken;
        Advance();

        return consumed;
    }

    private bool AdvanceIfType(TokenType type)
    {
        if (CurrentTokenType == type)
        {
            Advance();
            return true;
        }

        return false;
    }

    private bool ConsumeIfType(TokenType type, [NotNullWhen(true)] out Token? consumed)
    {
        Token current = CurrentToken;
        if (AdvanceIfType(type))
        {
            consumed = current;
            return true;
        }

        consumed = default;
        return false;
    }

    private readonly bool TryGetMacroDefinition(Token token, [NotNullWhen(true)] out MacroDefinition? definition)
    {
        // Only expand user-defined macros in the third pass.
        // Lookup built-in macros first, because they exist in user-defined space only as placeholders.
        return TryGetSystemDefinedMacroDefinition(token, out definition) || Defines.TryGetValue(token.Lexeme, out definition);
    }

    private readonly bool TryGetSystemDefinedMacroDefinition(Token token, [NotNullWhen(true)] out MacroDefinition? definition)
    {
        // Built-in macros are expanded first and third passes.
        switch (token.Lexeme)
        {
            case "__LINE__":
                // account for zero-index
                definition = LineMacro(token.Range.Start.Line + 1);
                return true;
            case "XFILE_VERSION":
                definition = XFileVersionMacro;
                return true;
            case "__FILE__":
                definition = FileMacro;
                return true;
            case "FASTFILE":
                definition = FastFileMacro;
                return true;
        }
        definition = default;
        return false;
    }

    private static MacroDefinition LineMacro(int line)
    {
        return MacroDefinition.BuiltInMacroDefinition("__LINE__", new Token(TokenType.Integer, RangeHelper.Empty, line.ToString()));
    }

    private static MacroDefinition XFileVersionMacro { get; } = MacroDefinition.BuiltInMacroDefinition("XFILE_VERSION", new Token(TokenType.Integer, RangeHelper.Empty, "593"));

    private static MacroDefinition FileMacro { get; } = MacroDefinition.BuiltInMacroDefinition("__FILE__", new Token(TokenType.Identifier, RangeHelper.Empty, "these_wont_work_how_you_hope_them_to_sad_face"));
    private static MacroDefinition FastFileMacro { get; } = MacroDefinition.BuiltInMacroDefinition("FASTFILE", new Token(TokenType.Identifier, RangeHelper.Empty, "these_wont_work_how_you_hope_them_to_sad_face"));
}
