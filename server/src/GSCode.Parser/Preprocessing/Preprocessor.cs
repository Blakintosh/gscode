using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Core.Text;
using GSCode.Parser.Lexing;

namespace GSCode.Parser.Preprocessing;

/// <summary>
/// Turns the raw lexed stream into the trivia-free parse stream: registers #defines,
/// splices #inserts (with provenance so GSH definitions keep real locations), evaluates
/// #if chains, and expands macros. One linear pass per file; inserts recurse.
/// </summary>
public sealed class Preprocessor
{
    private const int MaxInsertDepth = 16;

    private readonly string _rootFilePath;
    private readonly IInsertProvider _insertProvider;
    private readonly NameTable _names;

    /// <summary>Only for the header-extension rule; everything else here is dialect-independent.</summary>
    private readonly GameProfile _profile;

    private readonly MacroTable _macros = new();
    private readonly List<PToken> _output = [];
    private readonly ImmutableArray<Diagnostic>.Builder _diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
    private readonly ImmutableArray<InsertEdge>.Builder _inserts = ImmutableArray.CreateBuilder<InsertEdge>();
    private readonly ImmutableArray<MacroInvocation>.Builder _invocations = ImmutableArray.CreateBuilder<MacroInvocation>();
    private readonly ImmutableArray<TextRange>.Builder _disabledRegions = ImmutableArray.CreateBuilder<TextRange>();

    // Guards: inserts currently on the splice stack (cycle detection), and macros
    // currently being expanded (self-recursion is left unexpanded, like C).
    private readonly HashSet<string> _activeInsertPaths = new(StringComparer.Ordinal);
    private readonly HashSet<string> _expansionStack = new(StringComparer.Ordinal);

    /// <summary>One file being walked: its tokens, its text, and how it anchors to the root file.</summary>
    private sealed record FileFrame(ImmutableArray<Token> Tokens, SourceText Text, string? SourceFile, TextRange? RootSite, int Depth);

    private Preprocessor(
        string rootFilePath, IInsertProvider insertProvider, NameTable names, GameProfile profile)
    {
        _rootFilePath = rootFilePath;
        _insertProvider = insertProvider;
        _names = names;
        _profile = profile;
    }

    /// <summary>Preprocesses a lexed file into the parse stream + macro/insert knowledge.</summary>
    public static PreprocessResult Process(
        string rootFilePath,
        ImmutableArray<Token> tokens,
        SourceText text,
        IInsertProvider insertProvider,
        NameTable names,
        GameProfile? profile = null)
    {
        Preprocessor preprocessor = new(rootFilePath, insertProvider, names, profile ?? GameProfile.Active);

        FileFrame rootFrame = new(tokens, text, SourceFile: null, RootSite: null, Depth: 0);
        preprocessor.ProcessRange(rootFrame, 0, tokens.Length, preprocessor._output);

        TextRange endRange = tokens.Length > 0 ? tokens[^1].Range : TextRange.Empty;
        preprocessor._output.Add(new PToken(TokenKind.EndOfFile, "", endRange, Provenance.Root));

        return new PreprocessResult(
            [.. preprocessor._output],
            preprocessor._macros,
            preprocessor._invocations.ToImmutable(),
            preprocessor._inserts.ToImmutable(),
            preprocessor._disabledRegions.ToImmutable(),
            preprocessor._diagnostics.ToImmutable());
    }

    // --- Main walk ---

    private void ProcessRange(FileFrame frame, int start, int endExclusive, List<PToken> sink)
    {
        int index = start;
        while ( index < endExclusive )
        {
            Token token = frame.Tokens[index];

            if ( token.IsTrivia || token.Kind == TokenKind.EndOfFile )
            {
                index++;
                continue;
            }

            switch ( token.Kind )
            {
                case TokenKind.DefineDirective:
                    index = ParseDefine(frame, index);
                    continue;
                case TokenKind.InsertDirective:
                    index = HandleInsert(frame, index, sink);
                    continue;
                case TokenKind.IfDirective:
                    index = HandleConditionalChain(frame, index, endExclusive, sink);
                    continue;
                case TokenKind.ElifDirective:
                case TokenKind.ElseDirective:
                case TokenKind.EndifDirective:
                    AddDiagnostic(frame, token.Range, GscDiagnosticCode.UnexpectedConditionalDirective, KindText(frame, token));
                    index = SkipToEndOfLine(frame, index);
                    continue;
                default:
                    break;
            }

            if ( IsMacroCandidate(token.Kind) && TryExpandAt(frame, ref index, sink) )
            {
                continue;
            }

            sink.Add(MakePToken(frame, token));
            index++;
        }
    }

    // --- #define ---

    private int ParseDefine(FileFrame frame, int index)
    {
        Token directive = frame.Tokens[index];
        index++;

        int nameIndex = NextSignificantOnLine(frame, index);
        if ( nameIndex < 0 || !IsMacroCandidate(frame.Tokens[nameIndex].Kind) )
        {
            AddDiagnostic(frame, directive.Range, GscDiagnosticCode.ExpectedMacroName);
            return SkipToEndOfLine(frame, index);
        }

        Token nameToken = frame.Tokens[nameIndex];
        string name = _names.Intern(nameToken.GetText(frame.Text));
        index = nameIndex + 1;

        // A parameter list only exists when '(' is ADJACENT to the name (C rule);
        // "#define A (x)" is an object-like macro whose body starts with '('.
        ImmutableArray<string>? parameters = null;
        if ( index < frame.Tokens.Length
            && frame.Tokens[index].Kind == TokenKind.OpenParen
            && frame.Tokens[index].Start == nameToken.End )
        {
            index = ParseMacroParameters(frame, index, name, out parameters);
        }

        // Body: significant tokens to end of line; '\' immediately before the line
        // break continues onto the next line. A trailing comment becomes documentation.
        List<PToken> body = [];
        string? documentation = null;

        while ( index < frame.Tokens.Length )
        {
            Token current = frame.Tokens[index];

            if ( current.Kind == TokenKind.Newline || current.Kind == TokenKind.EndOfFile )
            {
                break;
            }

            if ( current.Kind is TokenKind.LineComment or TokenKind.BlockComment or TokenKind.DocComment )
            {
                documentation = _names.Intern(current.GetText(frame.Text));
                index++;
                continue;
            }

            if ( current.IsTrivia )
            {
                index++;
                continue;
            }

            if ( current.Kind == TokenKind.Backslash )
            {
                int afterBackslash = index + 1;
                while ( afterBackslash < frame.Tokens.Length && frame.Tokens[afterBackslash].Kind == TokenKind.Whitespace )
                {
                    afterBackslash++;
                }

                if ( afterBackslash < frame.Tokens.Length && frame.Tokens[afterBackslash].Kind == TokenKind.Newline )
                {
                    index = afterBackslash + 1;
                    continue;
                }

                // A stray backslash is excluded from the body so call sites don't all break.
                AddDiagnostic(frame, current.Range, GscDiagnosticCode.InvalidLineContinuation);
                index++;
                continue;
            }

            PToken bodyToken = MakePToken(frame, current) with
            {
                Provenance = new Provenance(frame.SourceFile, RootSite: null, DefinitionSite: nameToken.Range),
            };
            body.Add(bodyToken);
            index++;
        }

        _macros.Define(new MacroDefinition(name, frame.SourceFile, nameToken.Range, parameters, [.. body], documentation));
        return index;
    }

    private int ParseMacroParameters(FileFrame frame, int openParenIndex, string macroName, out ImmutableArray<string>? parameters)
    {
        List<string> names = [];
        int index = openParenIndex + 1;

        while ( index < frame.Tokens.Length )
        {
            Token current = frame.Tokens[index];

            if ( current.Kind == TokenKind.CloseParen )
            {
                parameters = [.. names];
                return index + 1;
            }

            if ( current.Kind == TokenKind.Newline || current.Kind == TokenKind.EndOfFile )
            {
                break;
            }

            if ( IsMacroCandidate(current.Kind) )
            {
                names.Add(_names.Intern(current.GetText(frame.Text)));
            }

            // Commas, whitespace, and anything unexpected are simply stepped over.
            index++;
        }

        AddDiagnostic(frame, frame.Tokens[openParenIndex].Range, GscDiagnosticCode.UnterminatedMacroParameters, macroName);
        parameters = [.. names];
        return index;
    }

    // --- #insert ---

    private int HandleInsert(FileFrame frame, int index, List<PToken> sink)
    {
        Token directive = frame.Tokens[index];
        index++;

        // Path tokens run until the terminating ';' (or, erroneously, the line break).
        int firstPathIndex = -1;
        int lastPathIndex = -1;
        bool sawSemicolon = false;

        while ( index < frame.Tokens.Length )
        {
            Token current = frame.Tokens[index];

            if ( current.Kind == TokenKind.Semicolon )
            {
                sawSemicolon = true;
                index++;
                break;
            }

            if ( current.Kind == TokenKind.Newline || current.Kind == TokenKind.EndOfFile )
            {
                break;
            }

            if ( !current.IsTrivia )
            {
                if ( firstPathIndex < 0 )
                {
                    firstPathIndex = index;
                }

                lastPathIndex = index;
            }

            index++;
        }

        if ( firstPathIndex < 0 )
        {
            AddDiagnostic(frame, directive.Range, GscDiagnosticCode.MissingInsertPath);
            return index;
        }

        Token firstPath = frame.Tokens[firstPathIndex];
        Token lastPath = frame.Tokens[lastPathIndex];
        TextRange pathRange = new(firstPath.Range.Start, lastPath.Range.End);
        string rawPath = frame.Text.Slice(firstPath.Start, lastPath.End - firstPath.Start).ToString().Trim();

        if ( !sawSemicolon )
        {
            AddDiagnostic(frame, pathRange, GscDiagnosticCode.InsertMissingSemicolon);
        }

        if ( IsIllegalInsertPath(rawPath) )
        {
            AddDiagnostic(frame, pathRange, GscDiagnosticCode.InvalidInsertPath, rawPath);
            return index;
        }

        // `#insert` takes a HEADER. Naming a script instead resolves to a real file and then pastes
        // its function declarations into the middle of this one, so the errors surface far from the
        // directive and look nothing like the cause. Reported and skipped rather than reported and
        // inserted, because inserting it is what produces the confusing wreckage.
        if ( _profile.HeaderExtension.Length > 0
            && !rawPath.EndsWith(_profile.HeaderExtension, StringComparison.OrdinalIgnoreCase) )
        {
            AddDiagnostic(
                frame, pathRange, GscDiagnosticCode.InsertNotAHeader, rawPath, _profile.HeaderExtension);
            return index;
        }

        TextRange rootSite = frame.RootSite ?? new TextRange(directive.Range.Start, lastPath.Range.End);

        if ( frame.Depth + 1 > MaxInsertDepth )
        {
            AddDiagnostic(frame, pathRange, GscDiagnosticCode.InsertTooDeep, rawPath);
            return index;
        }

        if ( !_insertProvider.TryGetInsert(rawPath, out InsertedFile inserted) )
        {
            AddDiagnostic(frame, pathRange, GscDiagnosticCode.InsertNotFound, rawPath);
            _inserts.Add(new InsertEdge(rawPath, null, pathRange, frame.SourceFile));
            return index;
        }

        if ( _activeInsertPaths.Contains(inserted.Path) )
        {
            AddDiagnostic(frame, pathRange, GscDiagnosticCode.InsertCycle, rawPath);
            _inserts.Add(new InsertEdge(rawPath, inserted.Path, pathRange, frame.SourceFile));
            return index;
        }

        _inserts.Add(new InsertEdge(rawPath, inserted.Path, pathRange, frame.SourceFile));

        _activeInsertPaths.Add(inserted.Path);
        FileFrame insertedFrame = new(inserted.Tokens, inserted.Text, inserted.Path, rootSite, frame.Depth + 1);
        ProcessRange(insertedFrame, 0, inserted.Tokens.Length, sink);
        _activeInsertPaths.Remove(inserted.Path);

        return index;
    }

    private static bool IsIllegalInsertPath(string rawPath)
    {
        if ( rawPath.Length == 0 )
        {
            return true;
        }

        if ( rawPath[0] == '\\' || rawPath[0] == '/' )
        {
            return true;
        }

        if ( rawPath.Length >= 2 && rawPath[1] == ':' )
        {
            return true;
        }

        return rawPath.Contains("..", StringComparison.Ordinal);
    }

    // --- #if / #elif / #else / #endif ---

    private int HandleConditionalChain(FileFrame frame, int index, int endExclusive, List<PToken> sink)
    {
        Token chainStart = frame.Tokens[index];
        bool branchTaken = false;

        while ( index < endExclusive )
        {
            Token directive = frame.Tokens[index];
            TokenKind directiveKind = directive.Kind;
            index++;

            bool active;
            if ( directiveKind == TokenKind.IfDirective || directiveKind == TokenKind.ElifDirective )
            {
                List<PToken> condition = CollectConditionLine(frame, ref index);
                int? value = ConditionalEvaluator.Evaluate(condition);
                active = !branchTaken && value is int result && result != 0;
            }
            else
            {
                // #else takes the branch when nothing before it did.
                index = SkipToEndOfLine(frame, index);
                active = !branchTaken;
            }

            int bodyStart = index;
            int bodyEnd = FindBranchEnd(frame, bodyStart, endExclusive);

            if ( bodyEnd >= endExclusive )
            {
                AddDiagnostic(frame, chainStart.Range, GscDiagnosticCode.UnterminatedConditionalDirective, KindText(frame, chainStart));
                if ( active )
                {
                    branchTaken = true;
                    ProcessRange(frame, bodyStart, endExclusive, sink);
                }
                else
                {
                    RecordDisabledRegion(frame, bodyStart, endExclusive);
                }

                return endExclusive;
            }

            if ( active )
            {
                branchTaken = true;
                ProcessRange(frame, bodyStart, bodyEnd, sink);
            }
            else
            {
                RecordDisabledRegion(frame, bodyStart, bodyEnd);
            }

            index = bodyEnd;
            if ( frame.Tokens[index].Kind == TokenKind.EndifDirective )
            {
                return SkipToEndOfLine(frame, index + 1);
            }

            // Loop continues with the #elif/#else now at index.
        }

        AddDiagnostic(frame, chainStart.Range, GscDiagnosticCode.UnterminatedConditionalDirective, KindText(frame, chainStart));
        return endExclusive;
    }

    /// <summary>Finds the index of the #elif/#else/#endif that closes the branch starting at <paramref name="start"/>.</summary>
    private static int FindBranchEnd(FileFrame frame, int start, int endExclusive)
    {
        int nesting = 0;
        int index = start;

        while ( index < endExclusive )
        {
            TokenKind kind = frame.Tokens[index].Kind;

            if ( kind == TokenKind.IfDirective )
            {
                nesting++;
            }
            else if ( kind == TokenKind.EndifDirective )
            {
                if ( nesting == 0 )
                {
                    return index;
                }

                nesting--;
            }
            else if ( (kind == TokenKind.ElifDirective || kind == TokenKind.ElseDirective) && nesting == 0 )
            {
                return index;
            }

            index++;
        }

        return endExclusive;
    }

    /// <summary>Collects the condition tokens on the directive's line, macro-expanding as it goes.</summary>
    private List<PToken> CollectConditionLine(FileFrame frame, ref int index)
    {
        List<PToken> condition = [];

        while ( index < frame.Tokens.Length )
        {
            Token current = frame.Tokens[index];

            if ( current.Kind == TokenKind.Newline || current.Kind == TokenKind.EndOfFile )
            {
                index++;
                return condition;
            }

            if ( current.IsTrivia )
            {
                index++;
                continue;
            }

            if ( IsMacroCandidate(current.Kind) && TryExpandAt(frame, ref index, condition) )
            {
                continue;
            }

            condition.Add(MakePToken(frame, current));
            index++;
        }

        return condition;
    }

    private void RecordDisabledRegion(FileFrame frame, int start, int endExclusive)
    {
        // Grey-out only applies to the root document; inactive code in inserts is invisible.
        if ( frame.SourceFile is not null )
        {
            return;
        }

        int firstSignificant = -1;
        int lastSignificant = -1;
        for ( int index = start; index < endExclusive; index++ )
        {
            if ( !frame.Tokens[index].IsTrivia )
            {
                if ( firstSignificant < 0 )
                {
                    firstSignificant = index;
                }

                lastSignificant = index;
            }
        }

        if ( firstSignificant < 0 )
        {
            return;
        }

        _disabledRegions.Add(new TextRange(frame.Tokens[firstSignificant].Range.Start, frame.Tokens[lastSignificant].Range.End));
    }

    // --- Macro expansion ---

    /// <summary>
    /// Attempts to expand the macro-candidate token at <paramref name="index"/>. Returns
    /// false when it is not a macro (the caller emits it as an ordinary token).
    /// </summary>
    private bool TryExpandAt(FileFrame frame, ref int index, List<PToken> sink)
    {
        Token nameToken = frame.Tokens[index];
        string name = _names.Intern(nameToken.GetText(frame.Text));

        if ( TryExpandBuiltin(frame, name, nameToken.Range, sink) )
        {
            index++;
            return true;
        }

        if ( !_macros.TryGet(name, out MacroDefinition definition) || _expansionStack.Contains(name) )
        {
            return false;
        }

        TextRange rootSite = frame.RootSite ?? nameToken.Range;

        if ( !definition.IsFunctionLike )
        {
            _invocations.Add(new MacroInvocation(name, frame.SourceFile, nameToken.Range, definition));
            index++;
            ExpandBody(definition, arguments: null, rootSite, sink);
            return true;
        }

        // Function-like: '(' must follow, otherwise the identifier stays as-is.
        int openParenIndex = NextSignificant(frame, index + 1);
        if ( openParenIndex < 0 || frame.Tokens[openParenIndex].Kind != TokenKind.OpenParen )
        {
            AddDiagnostic(frame, nameToken.Range, GscDiagnosticCode.MissingMacroArguments, name);
            return false;
        }

        if ( !TryCollectArguments(frame, openParenIndex, definition, out Dictionary<string, List<PToken>> arguments, out int afterArguments) )
        {
            index = afterArguments;
            return true;
        }

        _invocations.Add(new MacroInvocation(name, frame.SourceFile, nameToken.Range, definition));
        index = afterArguments;
        ExpandBody(definition, arguments, rootSite, sink);
        return true;
    }

    private bool TryExpandBuiltin(FileFrame frame, string name, TextRange range, List<PToken> sink)
    {
        switch ( name )
        {
            case "__LINE__":
            {
                // 1-based, matching how compilers report line numbers to users.
                string line = (range.Start.Line + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                sink.Add(new PToken(TokenKind.Integer, _names.Intern(line), range, ProvenanceFor(frame)));
                return true;
            }
            case "__FILE__":
            {
                string path = frame.SourceFile ?? _rootFilePath;
                sink.Add(new PToken(TokenKind.String, _names.Intern("\"" + path + "\""), range, ProvenanceFor(frame)));
                return true;
            }
            case "FASTFILE":
            {
                // The fastfile name only exists at link time; a placeholder keeps parsing sane.
                sink.Add(new PToken(TokenKind.Identifier, "__fastfile__", range, ProvenanceFor(frame)));
                return true;
            }
            default:
                return false;
        }

        Provenance ProvenanceFor(FileFrame currentFrame)
        {
            if ( currentFrame.SourceFile is null )
            {
                return Provenance.Root;
            }

            return new Provenance(currentFrame.SourceFile, currentFrame.RootSite, null);
        }
    }

    /// <summary>
    /// Collects a function-like macro's arguments: balanced parens, split on top-level
    /// commas, each argument macro-expanded at collection time, keyed by parameter name.
    /// </summary>
    private bool TryCollectArguments(FileFrame frame, int openParenIndex, MacroDefinition definition, out Dictionary<string, List<PToken>> arguments, out int afterArguments)
    {
        List<List<PToken>> collected = [[]];
        bool closed = false;

        int depth = 1;
        int index = openParenIndex + 1;

        while ( index < frame.Tokens.Length )
        {
            Token current = frame.Tokens[index];

            if ( current.Kind == TokenKind.EndOfFile )
            {
                break;
            }

            if ( current.IsTrivia )
            {
                index++;
                continue;
            }

            if ( current.Kind == TokenKind.OpenParen )
            {
                depth++;
            }
            else if ( current.Kind == TokenKind.CloseParen )
            {
                depth--;
                if ( depth == 0 )
                {
                    closed = true;
                    index++;
                    break;
                }
            }
            else if ( current.Kind == TokenKind.Comma && depth == 1 )
            {
                collected.Add([]);
                index++;
                continue;
            }

            if ( IsMacroCandidate(current.Kind) && TryExpandAt(frame, ref index, collected[^1]) )
            {
                continue;
            }

            collected[^1].Add(MakePToken(frame, current));
            index++;
        }

        if ( !closed )
        {
            AddDiagnostic(frame, frame.Tokens[openParenIndex].Range, GscDiagnosticCode.UnterminatedMacroArguments, definition.Name);
        }

        afterArguments = index;

        // Map by parameter name; missing arguments expand to nothing (engine behavior).
        arguments = new Dictionary<string, List<PToken>>(StringComparer.Ordinal);
        ImmutableArray<string> parameters = definition.Parameters ?? [];

        // A macro's parameter list is EXACT, unlike a script function's. A function called with
        // fewer arguments than it declares leaves the rest undefined, which is idiomatic; a macro
        // invoked with fewer substitutes NOTHING for the missing one, so the expansion is silently
        // malformed — `IS_TRUE( )` becomes `isdefined(  ) &&  `. An extra argument is simply
        // dropped. Neither is ever intended, and the definition is right there to compare against,
        // so unlike the builtin case there is no data-quality question.
        //
        // `collected` always holds at least one group, so `FOO()` arrives as one EMPTY group rather
        // than as zero groups — counting the groups alone would read that as one argument.
        int supplied = collected.Count == 1 && collected[0].Count == 0 ? 0 : collected.Count;

        // Only when the list actually closed: an unterminated one is already reported, and its
        // count is whatever the scan happened to reach.
        if ( closed && supplied != parameters.Length )
        {
            AddDiagnostic(
                frame,
                frame.Tokens[openParenIndex].Range,
                GscDiagnosticCode.WrongMacroArgumentCount,
                definition.Name,
                parameters.Length,
                supplied);
        }
        for ( int position = 0; position < parameters.Length; position++ )
        {
            arguments[parameters[position]] = position < collected.Count ? collected[position] : [];
        }

        return closed;
    }

    /// <summary>
    /// Emits a macro body into the sink: parameters splice their argument tokens,
    /// nested macros expand recursively, everything else is re-stamped with the
    /// invocation's root site so diagnostics land where the user wrote the call.
    /// </summary>
    private void ExpandBody(MacroDefinition definition, Dictionary<string, List<PToken>>? arguments, TextRange rootSite, List<PToken> sink)
    {
        _expansionStack.Add(definition.Name);

        IReadOnlyList<PToken> body = definition.Body;
        int index = 0;

        while ( index < body.Count )
        {
            PToken current = body[index];

            if ( IsMacroCandidate(current.Kind) )
            {
                // Parameter reference → splice the (already expanded) argument tokens.
                if ( arguments is not null && arguments.TryGetValue(current.Text, out List<PToken>? argumentTokens) )
                {
                    sink.AddRange(argumentTokens);
                    index++;
                    continue;
                }

                // Nested macro use inside the body.
                if ( _macros.TryGet(current.Text, out MacroDefinition nested) && !_expansionStack.Contains(current.Text) )
                {
                    if ( !nested.IsFunctionLike )
                    {
                        index++;
                        ExpandBody(nested, arguments: null, rootSite, sink);
                        continue;
                    }

                    if ( TryCollectBodyArguments(body, index + 1, nested, arguments, rootSite, out Dictionary<string, List<PToken>> nestedArguments, out int afterNested) )
                    {
                        index = afterNested;
                        ExpandBody(nested, nestedArguments, rootSite, sink);
                        continue;
                    }
                }
            }

            sink.Add(current with { Provenance = new Provenance(current.Provenance.SourceFile, rootSite, current.Provenance.DefinitionSite) });
            index++;
        }

        _expansionStack.Remove(definition.Name);
    }

    /// <summary>Collects a nested invocation's arguments from the remaining BODY tokens.</summary>
    private bool TryCollectBodyArguments(
        IReadOnlyList<PToken> body,
        int startIndex,
        MacroDefinition nested,
        Dictionary<string, List<PToken>>? outerArguments,
        TextRange rootSite,
        out Dictionary<string, List<PToken>> nestedArguments,
        out int afterArguments)
    {
        nestedArguments = new Dictionary<string, List<PToken>>(StringComparer.Ordinal);
        afterArguments = startIndex;

        if ( startIndex >= body.Count || body[startIndex].Kind != TokenKind.OpenParen )
        {
            return false;
        }

        List<List<PToken>> collected = [[]];
        int depth = 1;
        int index = startIndex + 1;

        while ( index < body.Count )
        {
            PToken current = body[index];

            if ( current.Kind == TokenKind.OpenParen )
            {
                depth++;
            }
            else if ( current.Kind == TokenKind.CloseParen )
            {
                depth--;
                if ( depth == 0 )
                {
                    afterArguments = index + 1;
                    ImmutableArray<string> parameters = nested.Parameters ?? [];
                    for ( int position = 0; position < parameters.Length; position++ )
                    {
                        nestedArguments[parameters[position]] = position < collected.Count ? collected[position] : [];
                    }

                    return true;
                }
            }
            else if ( current.Kind == TokenKind.Comma && depth == 1 )
            {
                collected.Add([]);
                index++;
                continue;
            }

            // Outer parameters referenced inside nested arguments splice through.
            if ( outerArguments is not null && IsMacroCandidate(current.Kind) && outerArguments.TryGetValue(current.Text, out List<PToken>? outerTokens) )
            {
                collected[^1].AddRange(outerTokens);
                index++;
                continue;
            }

            collected[^1].Add(current with { Provenance = new Provenance(current.Provenance.SourceFile, rootSite, current.Provenance.DefinitionSite) });
            index++;
        }

        return false;
    }

    // --- Shared plumbing ---

    private PToken MakePToken(FileFrame frame, Token token)
    {
        string text = TokenFacts.GetStaticText(token.Kind) ?? _names.Intern(token.GetText(frame.Text));

        Provenance provenance;
        if ( frame.SourceFile is null )
        {
            provenance = Provenance.Root;
        }
        else
        {
            provenance = new Provenance(frame.SourceFile, frame.RootSite, null);
        }

        return new PToken(token.Kind, text, token.Range, provenance);
    }

    private void AddDiagnostic(FileFrame frame, TextRange range, GscDiagnosticCode code, params object[] arguments)
    {
        // Problems inside inserted files report at the root file's #insert site,
        // matching how the engine attributes errors from inserted content.
        TextRange reportRange = frame.SourceFile is null ? range : frame.RootSite ?? range;
        _diagnostics.Add(Diagnostic.Create(reportRange, DiagnosticSeverity.Error, code, arguments));
    }

    private static bool IsMacroCandidate(TokenKind kind)
    {
        return kind == TokenKind.Identifier || TokenFacts.IsKeyword(kind);
    }

    private string KindText(FileFrame frame, Token token)
    {
        return TokenFacts.GetStaticText(token.Kind) ?? token.GetText(frame.Text).ToString();
    }

    /// <summary>Index of the next non-trivia token at or after <paramref name="start"/>, or -1.</summary>
    private static int NextSignificant(FileFrame frame, int start)
    {
        for ( int index = start; index < frame.Tokens.Length; index++ )
        {
            if ( !frame.Tokens[index].IsTrivia )
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>Like NextSignificant but stops at the end of the current line.</summary>
    private static int NextSignificantOnLine(FileFrame frame, int start)
    {
        for ( int index = start; index < frame.Tokens.Length; index++ )
        {
            Token token = frame.Tokens[index];
            if ( token.Kind == TokenKind.Newline || token.Kind == TokenKind.EndOfFile )
            {
                return -1;
            }

            if ( !token.IsTrivia )
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>Advances past the next line break and returns the index after it.</summary>
    private static int SkipToEndOfLine(FileFrame frame, int start)
    {
        int index = start;
        while ( index < frame.Tokens.Length )
        {
            if ( frame.Tokens[index].Kind == TokenKind.Newline )
            {
                return index + 1;
            }

            if ( frame.Tokens[index].Kind == TokenKind.EndOfFile )
            {
                return index;
            }

            index++;
        }

        return index;
    }
}
