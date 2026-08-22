namespace GSCode.Core.Diagnostics;

/// <summary>
/// Every diagnostic the server can produce, one stable code per condition.
/// Message templates live in <see cref="DiagnosticMessages"/>. Grows phase by phase.
/// </summary>
public enum GscDiagnosticCode
{
    // Lexing (1xxx)
    UnterminatedString = 1000,
    UnterminatedBlockComment = 1001,
    UnterminatedDocComment = 1002,
    UnexpectedCharacter = 1003,
    UnknownDirective = 1004,

    // Preprocessing (2xxx)
    ExpectedMacroName = 2000,
    UnterminatedMacroParameters = 2001,
    InvalidLineContinuation = 2002,
    MissingInsertPath = 2003,
    InsertMissingSemicolon = 2004,
    InvalidInsertPath = 2005,
    InsertNotFound = 2006,
    InsertTooDeep = 2007,
    InsertCycle = 2008,
    UnterminatedConditionalDirective = 2009,
    UnexpectedConditionalDirective = 2010,
    MissingMacroArguments = 2011,
    UnterminatedMacroArguments = 2012,
    InactiveConditionalBranch = 2013,
    InsertNotAHeader = 2014,
    WrongMacroArgumentCount = 2015,

    /// <summary>
    /// A <c>#define</c> or a member of the <c>#if</c> chain in a dialect with no preprocessor —
    /// everything before BO3. See <see cref="GameProfile.HasMacros"/> for the measurement.
    ///
    /// The directive is still PROCESSED after being reported, which is the one thing about this
    /// rule worth knowing. Skipping it would model the game's compiler more faithfully, and would
    /// punish the case this is most likely to be wrong about: a custom compiler that does accept
    /// macros. Reporting-and-expanding leaves suppression a complete answer — <c>#pragma disable
    /// 2016</c> and everything still resolves — where reporting-and-skipping would leave a
    /// suppressed file with its macros silently unexpanded.
    /// </summary>
    MacrosNotInDialect = 2016,

    /// <summary>
    /// A <c>#define</c> of a name something has already defined — twice in one file, or once in an
    /// inserted header and again in the script that inserts it.
    ///
    /// Reported at the LATER definition, because that is the one that takes effect: the macro table
    /// is order-based and the last definition seen wins. Which body a call site expands to therefore
    /// depends on insert order, and nothing at the call site shows it, so the message names the file
    /// holding the definition being replaced.
    /// </summary>
    DuplicateMacroDefinition = 2017,

    /// <summary>
    /// A parameter name repeated on one <c>#define</c>. The counterpart of
    /// <see cref="DuplicateParameter"/> for macros, and unambiguous for the same reason: only one of
    /// the two can ever be substituted, so every argument passed for the other is discarded.
    /// </summary>
    DuplicateMacroParameter = 2018,

    // Parsing (3xxx)
    ExpectedToken = 3000,
    ExpectedDeclaration = 3001,
    ExpectedExpression = 3002,
    ExpectedStatement = 3003,
    ExpectedParameterName = 3004,
    ExpectedClassMember = 3005,
    ExpectedCaseLabel = 3006,
    UnterminatedBlock = 3007,
    UnterminatedDevBlock = 3008,
    UsingAfterDeclaration = 3009,
    ExpectedScriptPath = 3010,
    ExpectedNamespaceName = 3011,
    InvalidAssignmentTarget = 3012,
    AssignmentUsedAsCondition = 3013,

    /// <summary>
    /// A statement with no terminating <c>;</c>. Split out from <see cref="ExpectedToken"/> because
    /// it is the one missing token whose report belongs somewhere OTHER than the token that revealed
    /// it — see Parser.ReportMissingSemicolon — and because a distinct code lets it be recognised
    /// without matching on message text.
    /// </summary>
    MissingSemicolon = 3014,

    /// <summary>
    /// A construct nested past the parser's ceiling — see Parser.MaxNestingDepth. Reported so the
    /// truncated tree is explained rather than silently short: everything from that point to the
    /// next statement boundary is missing from the outline, from references, and from every lint.
    ///
    /// It exists because the alternative is a StackOverflowException, which .NET cannot catch and
    /// which takes the whole server process down with every open document's state.
    /// </summary>
    NestingTooDeep = 3015,

    // Extraction / per-file semantics (4xxx)
    UnknownPrecacheType = 4000,
    WrongPrecacheArgumentCount = 4001,
    ConstructorHasParameters = 4002,
    DestructorHasParameters = 4003,
    NonValueDefaultParameter = 4004,
    DuplicateFunction = 4005,
    ClientOnlyPrecacheType = 4006,
    DuplicateParameter = 4007,

    /// <summary>
    /// <c>...</c> written anywhere but last in a parameter list — <c>f( ..., a )</c>, or a second
    /// <c>...</c>. It marks the point where the named parameters stop and the pack begins, so a
    /// parameter after it can never be bound by position: the pack has already swallowed everything
    /// the caller passed.
    /// </summary>
    VarargNotLastParameter = 4008,

    // Cross-file / workspace semantics (5xxx)
    NamespaceNotImported = 5000,
    UnusedUsing = 5001,
    PreferBooleanLiteral = 5002,
    PrivateFunctionNotVisible = 5003,
    ReadOnlyFieldWrite = 5004,
    SizeIsReadOnly = 5005,
    DevOnlyFunctionCalledFromRelease = 5006,

    /// <summary>
    /// The same namespace::name is declared in two files this one links against, so which
    /// definition a call reaches is not decided by the source.
    /// </summary>
    AmbiguousFunction = 5007,

    /// <summary>A local that is assigned and never read. Hint + Unnecessary: dead, not broken.</summary>
    UnusedLocal = 5008,

    /// <summary>A #using whose target does not exist. The script will not link.</summary>
    UsingNotFound = 5009,

    /// <summary>`case undefined:` — nothing equals undefined in a switch, so the branch is dead.</summary>
    CaseUndefined = 5010,

    /// <summary>A case label that is not a compile-time constant.</summary>
    NonConstantCaseLabel = 5011,

    /// <summary>An #include whose target contributes nothing this file uses. Hint, not an error.</summary>
    UnusedInclude = 5012,

    /// <summary>
    /// A call that names a SCRIPT function which does not exist — a qualified <c>ns::foo()</c> or a
    /// path-qualified <c>maps\mp\_util::foo()</c>. Both name a script location explicitly, so the
    /// call cannot have meant a builtin and the failure is unambiguous.
    ///
    /// v1 reported this and the builtin case as one code (<c>FunctionDoesNotExist = 3035</c>),
    /// because its <c>SymbolTable.TryGetFunction</c> fell back from script functions to the API
    /// inside a single lookup and returned one verdict. Splitting them keeps each failure to the
    /// one domain that can explain it.
    /// </summary>
    ScriptFunctionNotFound = 5013,

    /// <summary>
    /// A call that resolves to no script function and no entry in the builtin API — an unqualified
    /// <c>foo()</c> or an explicit <c>sys::foo()</c>. Either the name is a typo, or it is a real
    /// engine builtin missing from our API data, which is why this code is reported separately:
    /// swept over a corpus it yields the candidate list for curating the builtin library.
    ///
    /// Only meaningful where the game HAS builtin data (<see cref="GameProfile.DataFilePrefix"/>);
    /// without it every builtin call would look unresolved.
    /// </summary>
    BuiltinFunctionNotFound = 5014,
    UnreachableCode = 5015,
    VariableNeverAssigned = 5016,
    DuplicateCaseLabel = 5017,
    DuplicateImport = 5018,
    VoidResultAssigned = 5019,
    UnusedBinding = 5020,
    ClassInheritanceCycle = 5021,
    TooManyArguments = 5022,
    WrongBuiltinArgumentCount = 5023,

    /// <summary>
    /// The parameter-pack name (BO3's <c>vararg</c>) read in a function that does not declare
    /// <c>...</c>, so nothing ever binds it. Reported separately from
    /// <see cref="VariableNeverAssigned"/>, which would say the same thing far less usefully: the
    /// fix is not to assign the name but to add <c>...</c> to the declaration, and only a message
    /// that knows what the name IS can say so.
    /// </summary>
    VarargOutsideVarargFunction = 5024,

    /// <summary>
    /// A word that IS a keyword in some other game of the lineage, written call-shaped in a dialect
    /// that does not have it — <c>foreach ( x in a )</c> in CoD4 being the case that prompted this.
    ///
    /// Reported instead of <see cref="BuiltinFunctionNotFound"/>, which is where such a call lands
    /// otherwise and which describes it wrongly. The lexer gates keywords per profile, so a word the
    /// dialect lacks stays an ordinary identifier; the parser then sees identifier-then-'(' and
    /// builds a call; the resolution lint finds no function and no builtin of that name and says so.
    /// Every step is correct and the verdict — "matches no script function or known engine function"
    /// — sends the reader looking for a missing definition instead of telling them the loop does not
    /// exist in the game they are targeting.
    ///
    /// The message names the earliest game that HAS the word, because "not in this dialect" without
    /// it leaves the obvious next question unanswered.
    /// </summary>
    KeywordNotInDialect = 5025,

    /// <summary>
    /// The Infinity Ward counterpart to <see cref="NamespaceNotImported"/>: an unqualified call to a
    /// function that EXISTS in the workspace but is not merged into this file's scope, because
    /// neither this file nor any file it <c>#include</c>s declares it.
    ///
    /// Distinct from <see cref="BuiltinFunctionNotFound"/>, which is the "no such name anywhere"
    /// verdict. Here the name is known and the fix is an import, so a message telling the reader the
    /// function does not exist would send them to write one that is already there.
    ///
    /// Only meaningful on an <c>#include</c> dialect. Under <c>#using</c> the same mistake is a
    /// qualified call into an unimported namespace, which <see cref="NamespaceNotImported"/> already
    /// reports.
    /// </summary>
    FunctionNotIncluded = 5026,

    /// <summary>
    /// A second <c>default:</c> in one switch. Only the first can be reached, so the later one is
    /// dead in exactly the way <see cref="DuplicateCaseLabel"/> describes for a value label — and
    /// this rule sits beside it for that reason.
    ///
    /// Raised by 1.5 from its CFG builder, which is why it went out with the type-derived family it
    /// has nothing to do with. It needs no types: a count per switch answers it.
    /// </summary>
    MultipleDefaultLabels = 5027,

    /// <summary>
    /// The value of a <c>thread</c> call used for something. A threaded call returns at the callee's
    /// first <c>wait</c>, not at its <c>return</c>, so what the caller receives is whatever had been
    /// reached by then — <c>undefined</c> for anything that waits at all.
    ///
    /// Worth a Warning rather than an Error precisely because it is not always wrong today: a
    /// threaded function containing no wait runs to completion first, so the value is correct until
    /// someone adds a wait to it and every caller starts reading undefined at once.
    ///
    /// 1.5 raised this and <c>AssignOnThreadedFunction</c> as two codes for one mistake; an
    /// assignment satisfied both. This is the general question — an argument, a condition and a
    /// return value all consume a value without being an assignment.
    /// </summary>
    ConsumedThreadedCallResult = 5028,

    /// <summary>
    /// A <c>const</c> whose value is not known at compile time. What counts as known was measured
    /// over Black Ops III's 117 stock declarations: literals, and arithmetic over them.
    /// </summary>
    ExpectedConstantExpression = 5029,

    /// <summary>
    /// An assignment to a name bound by <c>const</c>. Black Ops III only — the Infinity Ward
    /// dialects' file-scope constants are our modelling of a bare assignment between two functions,
    /// and nothing establishes the engine refuses a later write to one.
    /// </summary>
    CannotAssignToConstant = 5030,

    /// <summary>
    /// Division, or modulo, by a divisor written as literal zero. Narrower than 1.5's, which tracked
    /// constant VALUES and so caught <c>d = 0; x = n / d;</c> — this tree has no constant
    /// propagation, and the literal case needs none.
    /// </summary>
    DivisionByZero = 5031,

    /// <summary>
    /// A statement whose expression cannot do anything, so the line has no effect: <c>a + b;</c>,
    /// <c>x == 1;</c>. Usually a missing <c>=</c> or a call that lost its parentheses.
    /// </summary>
    InvalidExpressionStatement = 5032,

    /// <summary>
    /// <c>foreach</c> over a value that is certainly a scalar. Structs are excluded — the engine
    /// enumerates one — so this is only the int/float/bool/string/vector case.
    /// </summary>
    CannotEnumerateType = 5033,

    /// <summary>A vector component that cannot be a number.</summary>
    InvalidVectorComponent = 5034,

    /// <summary>
    /// An assignment to one of the engine's global objects — <c>level</c>, <c>self</c>,
    /// <c>game</c>, <c>anim</c>, <c>world</c>. The names come from the active
    /// <c>GameProfile</c>, so a dialect without <c>world</c> keeps <c>world</c> as an ordinary
    /// local and is not reported.
    /// </summary>
    CannotAssignToGlobalObject = 5035,
}
