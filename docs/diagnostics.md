# Diagnostics — every code, who raises it, and what fixes it

Every problem GSCode reports has one stable code, `gscode-NNNN`, defined in
`server/src/GSCode.Core/Diagnostics/GscDiagnosticCode.cs`, with its message template in
`DiagnosticMessages.cs` beside it. The thousands digit is the pipeline stage that raises it
(`GscDiagnosticStages`), and the stage tells you which project to open.

| Range | Stage | Project | Needs |
|---|---|---|---|
| 1xxx | Lexing | `GSCode.Parser/Lexing` | the file's text |
| 2xxx | Preprocessing | `GSCode.Parser/Preprocessing` | the file + the headers it `#insert`s |
| 3xxx | Parsing | `GSCode.Parser/Syntax` | the token stream |
| 4xxx | Per-file semantics | `GSCode.Parser/Extraction` | the tree |
| 5xxx | Cross-file lints | `GSCode.Workspace/Analysis` | the tree + the indexed workspace |

## How a diagnostic reaches the editor

1. Stages 1xxx–4xxx are produced by `ScriptAnalysis.Analyze` and land in
   `ParseResult.AllDiagnostics`.
2. `WorkspaceLints.Analyze` adds the 5xxx lints for `.gsc` and `.csc` files (a `.gsh` header gets
   none: it has no language store of its own).
3. `WorkspaceLints.ApplyPragmas` drops anything an in-source pragma suppresses —
   `// #pragma disable 5016` / `#pragma restore`, `gscode-5016`, `all`, or `format` for the formatter
   (`Parser/Extraction/PragmaDirectives.cs`). Suppression is by code, never by severity, so an Error
   can be suppressed like a Hint. `// gscode ignore` from version 1.5 still works as a one-line alias. The user-facing
   description is `client/README.md`, "In-source pragmas".
4. `LspMapping` maps severity and tags (a cast — the enums match the LSP wire values) and
   `DiagnosticsPublisher` sends them. The `Unnecessary` tag is what greys out an unused import or an
   inactive `#if` branch.

Open files are linted from the live buffer on every analysis. Closed files report what was stored on
their record at index time; in `gscode.workspaceIndexingMode: full` that includes the cross-file
lints (`WorkspaceLintSweep`). Which closed files report at all is `gscode.diagnostics.scope`.

## Gates — why a rule sometimes says nothing

A lint that cannot be certain stands down rather than guess. When a code you expect does not appear,
check these first:

- **The index is not finished.** 5013, 5014, 5025 and 5026 need `ScriptDatabase.HasCompletedIndex`:
  "this function does not exist" is not a claim a half-built index can make. Open tabs are re-linted
  when indexing completes.
- **An import did not resolve.** `ImportGate`: an unresolved `#insert` (any of the six ways the
  preprocessor abandons a splice) or `#using` makes the set of legal names unknowable, so rules about
  "this matches nothing" stand down. 5009 is what tells the user why.
- **The game's data cannot carry the claim.** `GameProfile.HasTrustedEngineNames` and
  `HasReliableBuiltinSignatures` gate the rules that say a name is not an engine function or that a
  builtin got the wrong argument count. `server/GAME_PROFILES.md` says which games qualify.
- **The dialect lacks the construct.** 5000 runs only where `ResolvesByNamespace` (BO3); 5026 only
  on the `#include` dialects; 5003 only where `private` exists; 2016 only where there is no
  preprocessor.

## Severity policy

Severity is decided by measurement, not taste. Before a rule ships, it runs over the stock scripts of
every supported game (`scripts\sweep.bat`, `CorpusDiagnosticSweepTests`). Those scripts shipped and
work, so anything an Error reports there is a bug in the rule. Rules that would flood the Problems
panel on working code are Hints, where the fade is the whole output (5008, 5020). The reasoning for
each rule is in its section of `server/src/GSCode.Workspace/FOLDER.md`; the procedure for adding one
is the `add-diagnostic` skill (`.claude/skills/add-diagnostic/SKILL.md`).

## The codes

Built from `GscDiagnosticCode.cs` and `DiagnosticMessages.cs`. When adding a code, add its row
here. To check nothing is missing, compare the number of rows below with
`grep -cE '^\s+[A-Z][A-Za-z0-9]+ = [0-9]+,' server/src/GSCode.Core/Diagnostics/GscDiagnosticCode.cs`
(84 today). "Owner" is the file that raises it. Severity is what the owner passes at the report site. Quick fixes are in `CodeActionHandler`.
In a message, `{0}`, `{1}` and `{2}` are the arguments the owner fills in.

## Lexing (1xxx)

Raised by `server/src/GSCode.Parser/Lexing/Lexer.cs`. Always Error. The formatter refuses a file with any of these.

| Code | Name | Severity | Message | Owner | Quick fix |
|---|---|---|---|---|---|
| 1000 | `UnterminatedString` | Error | String literal is not terminated before the end of the line. | `Lexer.cs` |  |
| 1001 | `UnterminatedBlockComment` | Error | Block comment is missing its closing '*/'. | `Lexer.cs` |  |
| 1002 | `UnterminatedDocComment` | Error | Documentation block is missing its closing '@/'. | `Lexer.cs` |  |
| 1003 | `UnexpectedCharacter` | Error | Unexpected character '{0}'. | `Lexer.cs` |  |
| 1004 | `UnknownDirective` | Error | Unknown preprocessor directive '{0}'. | `Lexer.cs` |  |

## Preprocessing (2xxx)

Raised by `server/src/GSCode.Parser/Preprocessing/Preprocessor.cs` while it expands macros, splices `#insert`s and evaluates `#if`. Error except 2013, which is the grey-out of an inactive `#if` branch. A diagnostic from inside an inserted header is anchored at the `#insert` line of the root file.

| Code | Name | Severity | Message | Owner | Quick fix |
|---|---|---|---|---|---|
| 2000 | `ExpectedMacroName` | Error | Expected a macro name after '#define'. | `Preprocessor.cs` |  |
| 2001 | `UnterminatedMacroParameters` | Error | The parameter list of macro '{0}' is missing its closing ')'. | `Preprocessor.cs` |  |
| 2002 | `InvalidLineContinuation` | Error | A line continuation '\' must be the last token on its line. | `Preprocessor.cs` |  |
| 2003 | `MissingInsertPath` | Error | Expected a file path after '#insert'. | `Preprocessor.cs` |  |
| 2004 | `InsertMissingSemicolon` | Error | '#insert' directive must end with ';'. | `Preprocessor.cs` |  |
| 2005 | `InvalidInsertPath` | Error | '{0}' is not a valid insert path: paths must be relative and cannot contain '..'. | `Preprocessor.cs` |  |
| 2006 | `InsertNotFound` | Error | Cannot find insert file '{0}'. | `Preprocessor.cs` |  |
| 2007 | `InsertTooDeep` | Error | '#insert' nesting is too deep at '{0}'. | `Preprocessor.cs` |  |
| 2008 | `InsertCycle` | Error | '#insert' cycle detected: '{0}' is already being inserted. | `Preprocessor.cs` |  |
| 2009 | `UnterminatedConditionalDirective` | Error | '{0}' directive is missing its closing '#endif'. | `Preprocessor.cs` |  |
| 2010 | `UnexpectedConditionalDirective` | Error | '{0}' without a matching '#if'. | `Preprocessor.cs` |  |
| 2011 | `MissingMacroArguments` | Error | Macro '{0}' expects an argument list. | `Preprocessor.cs` |  |
| 2012 | `UnterminatedMacroArguments` | Error | The argument list for macro '{0}' is missing its closing ')'. | `Preprocessor.cs` |  |
| 2013 | `InactiveConditionalBranch` | Hint | Inactive preprocessor branch; this code is excluded from the build. | `ParseResult.cs` |  |
| 2014 | `InsertNotAHeader` | Error | '{0}' is not a header; '#insert' expects a '{1}' file. | `Preprocessor.cs` |  |
| 2015 | `WrongMacroArgumentCount` | Error | Macro '{0}' takes {1} argument(s) but {2} were passed. | `Preprocessor.cs` |  |
| 2016 | `MacrosNotInDialect` | Error | '{0}' is not available in {1}, which has no preprocessor — macros and conditional compilation arrive in Black Ops III. | `Preprocessor.cs` |  |
| 2017 | `DuplicateMacroDefinition` | Error | '{0}' is already defined in {1}; this definition is seen later and replaces it. | `Preprocessor.cs` |  |
| 2018 | `DuplicateMacroParameter` | Error | Macro '{1}' already has a parameter named '{0}'; arguments passed for this one are discarded. | `Preprocessor.cs` |  |

## Parsing (3xxx)

Raised by `server/src/GSCode.Parser/Syntax/Parser*.cs`. Always Error. Recovery is panic-mode: one diagnostic, then a silent skip to the next `;`, `}` or declaration keyword. The formatter refuses a file with any of these.

| Code | Name | Severity | Message | Owner | Quick fix |
|---|---|---|---|---|---|
| 3000 | `ExpectedToken` | Error | Expected '{0}' but found '{1}'. | `Parser.Declarations.cs, Parser.cs` |  |
| 3001 | `ExpectedDeclaration` | Error | Expected a function, class, or directive but found '{0}'. | `Parser.Declarations.cs` |  |
| 3002 | `ExpectedExpression` | Error | Expected an expression but found '{0}'. | `Parser.Expressions.cs` |  |
| 3003 | `ExpectedStatement` | Error | Expected a statement but found '{0}'. | `Parser.Statements.cs` |  |
| 3004 | `ExpectedParameterName` | Error | Expected a parameter name but found '{0}'. | `Parser.Declarations.cs` |  |
| 3005 | `ExpectedClassMember` | Error | Expected 'var', 'constructor', 'destructor', or 'function' but found '{0}'. | `Parser.Declarations.cs` |  |
| 3006 | `ExpectedCaseLabel` | Error | Expected 'case' or 'default' but found '{0}'. | `Parser.Statements.cs` |  |
| 3007 | `UnterminatedBlock` | Error | Block is missing its closing '}'. | `Parser.Declarations.cs, Parser.Statements.cs` |  |
| 3008 | `UnterminatedDevBlock` | Error | Dev block is missing its closing '#/'. | `Parser.Declarations.cs, Parser.Statements.cs` |  |
| 3009 | `UsingAfterDeclaration` | Error | '#using' directives must appear before the first function or class declaration. | `Parser.Declarations.cs` | Move the `#using` up |
| 3010 | `ExpectedScriptPath` | Error | Expected a script path after '{0}'. | `Parser.Declarations.cs` |  |
| 3011 | `ExpectedNamespaceName` | Error | Expected a namespace name after '#namespace'. | `Parser.Declarations.cs` |  |
| 3012 | `InvalidAssignmentTarget` | Error | Cannot assign to {0} — assignment needs a variable, field or array element on the left. | `Parser.Expressions.cs` |  |
| 3013 | `AssignmentUsedAsCondition` | Error | This assigns to '{0}' and tests the assigned value; '==' compares. Wrap it in parentheses if the assignment is deliberate. | `Parser.Statements.cs` |  |
| 3014 | `MissingSemicolon` | Error | Expected ';' at the end of this statement. | `Parser.cs` |  |
| 3015 | `NestingTooDeep` | Error | Nested too deeply to analyse; the rest of this statement was skipped. | `Parser.cs` |  |

## Per-file semantics (4xxx)

Raised during extraction (`SymbolExtractor.cs`) or by the parser for declaration rules. Always Error. Need only the one file.

| Code | Name | Severity | Message | Owner | Quick fix |
|---|---|---|---|---|---|
| 4000 | `UnknownPrecacheType` | Error | '{0}' is not a known #precache asset type. | `SymbolExtractor.cs` |  |
| 4001 | `WrongPrecacheArgumentCount` | Error | #precache type '{0}' expects {1} value(s) after the type but got {2}. | `SymbolExtractor.cs` |  |
| 4002 | `ConstructorHasParameters` | Error | Constructors cannot declare parameters. | `SymbolExtractor.cs` |  |
| 4003 | `DestructorHasParameters` | Error | Destructors cannot declare parameters. | `SymbolExtractor.cs` |  |
| 4005 | `DuplicateFunction` | Error | Function '{0}' is already defined in namespace '{1}'. | `SymbolExtractor.cs` |  |
| 4006 | `ClientOnlyPrecacheType` | Error | '{0}' is a client-side asset type and can only be precached from a client script. | `SymbolExtractor.cs` |  |
| 4007 | `DuplicateParameter` | Error | Parameter '{0}' is declared more than once. | `Parser.Declarations.cs` |  |
| 4008 | `VarargNotLastParameter` | Error | '...' must be the last entry in the parameter list. | `Parser.Declarations.cs` |  |

## Cross-file / workspace (5xxx)

One rule per file in `server/src/GSCode.Workspace/Analysis/`, all run by `WorkspaceLints.Analyze`. Severity is chosen by measurement over the stock game scripts (see the `add-diagnostic` skill): an Error must never fire on code that ships and works. Some rules stand down on a game whose builtin data is incomplete, or until the index is finished.

| Code | Name | Severity | Message | Owner | Quick fix |
|---|---|---|---|---|---|
| 5000 | `NamespaceNotImported` | Error | Namespace '{0}' is called but no '#using' imports a file that declares it. | `NamespaceUsageLint.cs` | Add the `#using` |
| 5001 | `UnusedUsing` | Hint | '{0}' is imported but nothing from it is used. | `UnusedUsingLint.cs` | Remove (one, or all unused) |
| 5002 | `PreferBooleanLiteral` | Hint | {0} '{1}' is a bool; prefer '{2}' over the integer literal. | `PreferBooleanLiteralLint.cs` | Use `true`/`false` |
| 5003 | `PrivateFunctionNotVisible` | Error | '{0}' is private to namespace '{1}'; only files declaring that namespace can call it. | `PrivateAccessLint.cs` |  |
| 5004 | `ReadOnlyFieldWrite` | Warning | Engine field '{0}' is read-only; assigning to it has no effect. | `ReadOnlyWriteLint.cs` |  |
| 5005 | `SizeIsReadOnly` | Error | '.size' is read-only and cannot be assigned. | `ReadOnlyWriteLint.cs` |  |
| 5006 | `DevOnlyFunctionCalledOutsideDevBlock` | Error | '{0}' is dev-only. A '/# #/' dev block is skipped unless developer script is enabled on the server, so call it from inside one. | `DevBlockCallLint.cs` |  |
| 5007 | `AmbiguousFunction` | Warning | '{0}' is declared in {2} of the files this script imports, all in namespace '{1}' — which one this call reaches is undefined. | `AmbiguousFunctionLint.cs` |  |
| 5008 | `UnusedLocal` | Hint | '{0}' is assigned but never used. | `UnusedLocalLint.cs` |  |
| 5009 | `UsingNotFound` | Error | Cannot find script '{0}'. | `UsingNotFoundLint.cs` (imports), `FunctionResolutionLint.cs` (path calls to a missing file) |  |
| 5010 | `CaseUndefined` | Warning | 'case undefined' never matches — a switch compares values, so this branch is unreachable. | `CaseLabelLint.cs` |  |
| 5011 | `NonConstantCaseLabel` | Warning | A case label must be a constant value. | `CaseLabelLint.cs` |  |
| 5012 | `UnusedInclude` | Hint | '{0}' is included but nothing from it is used. | `UnusedIncludeLint.cs` | Remove (one, or all unused) |
| 5013 | `ScriptFunctionNotFound` | Error | The script function '{0}' could not be resolved; this call names a script location, so no engine function could have matched. | `FunctionResolutionLint.cs` | Create the function; or add `#using` and qualify |
| 5014 | `BuiltinFunctionNotFound` | Error | '{0}' matches no script function or known engine function. | `FunctionResolutionLint.cs` | Create the function; or add `#using` and qualify |
| 5015 | `UnreachableCode` | Information | Unreachable: the preceding '{0}' always leaves this block. | `UnreachableCodeLint.cs` |  |
| 5016 | `VariableNeverAssigned` | Warning | '{0}' is read but never assigned in this function. | `UnassignedVariableLint.cs` |  |
| 5017 | `DuplicateCaseLabel` | Warning | '{0}' is already a case label in this switch; only the first can ever match. | `CaseLabelLint.cs` |  |
| 5018 | `DuplicateImport` | Warning | '{0}' is already imported by an earlier '{1}'. | `DuplicateImportLint.cs` | Remove the duplicate line |
| 5019 | `VoidResultAssigned` | Warning | '{0}' returns nothing, so this assigns undefined. | `VoidResultLint.cs` |  |
| 5020 | `UnusedBinding` | Hint | {0} '{1}' is never used. | `UnusedBindingLint.cs` |  |
| 5021 | `ClassInheritanceCycle` | Error | '{0}' inherits from itself through {1}. | `ClassCycleLint.cs` |  |
| 5022 | `TooManyArguments` | Error | '{0}' declares {1} parameter(s) but {2} were passed. | `ArgumentCountLint.cs` |  |
| 5023 | `WrongBuiltinArgumentCount` | Error | '{0}' needs at least {1} argument(s) but {2} were passed. | `ArgumentCountLint.cs` |  |
| 5024 | `VarargOutsideVarargFunction` | Warning | '{0}' is only bound in a function declaring '...'; add it to the parameter list to use the pack here. | `UnassignedVariableLint.cs` |  |
| 5025 | `KeywordNotInDialect` | Error | '{0}' is not part of the {1} dialect; it arrives in {2}. Here it reads as an ordinary function name. | `FunctionResolutionLint.cs` |  |
| 5026 | `FunctionNotIncluded` | Error | '{0}' is declared in '{1}', but this file has no '#include' bringing it into scope. | `IncludeUsageLint.cs` | Add the `#include` |
| 5027 | `MultipleDefaultLabels` | Warning | This switch already has a 'default' label; only the first one can ever be reached. | `CaseLabelLint.cs` |  |
| 5028 | `ConsumedThreadedCallResult` | Warning | A 'thread' call returns at the function's first 'wait', not at its 'return' — so this reads 'undefined' as soon as the thread waits. | `ThreadedResultLint.cs` |  |
| 5029 | `ExpectedConstantExpression` | Warning | '{0}' is declared 'const', so its value must be known at compile time — a literal, or arithmetic over literals. | `ConstDeclarationLint.cs` |  |
| 5030 | `CannotAssignToConstant` | Warning | '{0}' is declared 'const' and cannot be assigned to. | `ConstDeclarationLint.cs` |  |
| 5031 | `DivisionByZero` | Warning | The divisor here is zero. | `ArithmeticLint.cs` |  |
| 5032 | `InvalidExpressionStatement` | Warning | This statement computes a value and discards it, so it has no effect — a missing '=' or a call missing its '()'. | `ExpressionStatementLint.cs` |  |
| 5033 | `CannotEnumerateType` | Warning | 'foreach' needs an array or a struct, but this is {0}. | `TypeMismatchLint.cs` |  |
| 5034 | `InvalidVectorComponent` | Warning | A vector component must be a number, but this is {0}. | `TypeMismatchLint.cs` |  |
| 5035 | `CannotAssignToGlobalObject` | Error | '{0}' is an engine global and cannot be assigned to; write to a field on it instead, as in '{0}.field = value'. | `GlobalObjectWriteLint.cs` |  |
