# Change Log

All notable changes to the GSCode extension are documented in this file.

This project follows [Keep a Changelog](http://keepachangelog.com/).

## 2.2.0

2.2.0 rather than 2.1.0: the Marketplace keeps odd minors for pre-releases, which are published as
2.1.N from the 2.0 line, so the next full release takes the even minor above them.

Upgrading: the workspace cache moves to a new binary format and rebuilds once on the first start.
Format Document will re-indent existing files once (see **Changed**). `gscode.completion.fieldScope`
is gone (see **Removed**). Inlay hints are now off by default; turn on the ones you want.

### Added

**Navigation**
- **Go to Implementations** on a class method lists the subclasses that override it. On a field it
  lists the field's plain assignments, plus the function a callback field is bound to
  (`level.callback = &on_damage`).
- **Go to Type Definition** on a local jumps to the class it holds or the function it points at; on a
  field, to the class or function the scripts put in it.
- **Go to Definition** on a field (`level.foo`) goes to the places it is written. It used to do
  nothing, since a field is declared nowhere.
- A class `var` gets the navigation every other symbol has — definition, references, hover, rename —
  including its uses in subclasses in other files.
- Call hierarchy works for class methods end to end, for files you do not have open, and for callers
  on CoD4, WaW, MW2 and BO1, where expanding a caller used to show nothing.
- Find All References on an engine builtin finds every call site in the workspace, not only the ones
  in the namespace you asked from.
- Hovering a script function, class or macro shows where it is declared, as a link to that file and
  line — the answer a macro's hover could not give before, since an `#insert`ed `#define` lives in a
  header the file never names.

**Macros**
- Signature help on a macro invocation shows what it expands to, and hover follows a chain of macros.
- Macro parameter-name inlay hints (`gscode.inlayHints.macroParameterNames`, off by default).
- `__FUNCTION__` and `__FILE__` hover with the value they resolve to, where they are written.
- Keyword-shaped macro names such as `DEFAULT` get the macro colour.
- The lints see through macros: a private, dev-only, ambiguous or unresolved call that a macro
  expands to is reported at the invocation, and a missing `#using`/`#include` it needs is asked for.

**Completion**
- Completion offers functions from scripts you have not imported and writes the `#using` /
  `#include` for you when you accept one (`gscode.completion.autoImport`, on by default). It waits
  for three characters, so what is already in scope still comes first.
- Outside any function, completion offers what a function body gets — macros such as
  `REGISTER_SYSTEM`, functions, classes — instead of nothing but snippets.
- `sys::` offers the engine's builtin library. A `sys::` call's hover shows the builtin, and its
  argument count is checked against it.
- A field written in more than one casing is one row, labelled with this file's spelling or else
  the most-used one, with a `+N spellings` hint and the spellings listed in the details pane.

**Commands and the editor**
- A **GSCode** submenu in the right-click menu of GSC, CSC and GSH files: Organize Imports, Generate
  ScriptDoc Block, Open Documentation for Symbol, Select Game, Show Server Output, Restart Language
  Server, and Clear Cache and Reindex.
- **GSCode: Generate ScriptDoc Block** writes the doc block for the function the cursor is in,
  anywhere in it: the tags the stock scripts use, with the parameters filled in from the signature
  and marked mandatory or optional. It is a command, not a lightbulb, so undocumented functions are
  not flagged.
- **GSCode: Organize Imports** as a palette command, which says so when there is nothing to do
  instead of VS Code's "No code actions available".
- **GSCode: Select Game** can be opened on purpose from the Command Palette, and lists only the games
  the server has a dialect for, ticking the one actually in force.
- `workspaceIndexingMode: "full"` runs the cross-file lints over every indexed file, not just open
  ones, and re-lints only the closed files an edit reaches.
- A status-bar warning when indexing fails, instead of a spinner that never stops.

**Formatting settings**
- `gscode.format.fixCasing` (on): formatting lowercases keywords and writes functions, namespaces
  and classes the way they are declared — `isDefined()` becomes `isdefined()`, `getplayers()`
  becomes `GetPlayers()`, `FOo()` becomes `foo()`, `Util::` becomes `util::`. A bare call takes the
  builtin's spelling when there is one, since it resolves to the builtin first. Macros match exactly
  and are never recased.
- `gscode.format.indentCaseLabels` (on; off puts `case` in the switch's column),
  `gscode.format.indentCaseBlocks` (off; on indents a braced case body inside its label) and
  `gscode.format.indentDevBlocks` (off; on indents the body of a `/# … #/` block), for the
  indentations the stock scripts split on.
- `gscode.format.alignMaxPadding` (20): consecutive alignment no longer pushes a short name's `=`
  across the screen to match a deeply subscripted neighbour. A line further than this from the rest
  of its run keeps a single space, and the rest still align.

### Changed

**Formatter output.** Each rule follows what the stock scripts do; Format Document re-indents
existing files once.
- A line continuing an open `(` or `[` is indented one level past its statement, rather than pulled
  back flush.
- A control-flow header split across lines keeps its breaks and aligns the continuation under its
  opening parenthesis, the way stock writes long chains of conditions.
- A blank line follows a closed block, and the statement that ends an unbraced body, before the next
  statement (still capped by `maxBlankLines`).
- Ternary and base-class colons are spaced (`a ? b : c`, `class Derived : Base`), and each `case`
  label stands on its own line.
- A braced case body stays in its label's column.
- A function-pointer call is set apart from its caller (`self [[ level.callback ]]()`), and nested
  subscripts are padded (`a[ b[ c ] ]`).
- In a run of aligned assignments, a compound operator's `=` lines up with the others' `=`.
- `vararg[ i ]` is subscripted like the variable it is.

**Organize Imports** removes every unused `#using`/`#include` in the file, not only the one under the
cursor, and then groups and sorts the directive block the same way Format Document does.

**Performance.**
- Large workspaces stay fast. Measured on generated 50,000-file workspaces: a warm start is about 4
  seconds, and completion, one file's lint pass, CodeLens and find-references cost the same as in a
  1,000-file one.
- Ordinary starts are faster too: a Black Ops III workspace indexes in about 0.6 s rather than 2.2 s,
  and a workspace opened at the root of a game install no longer walks its 170,000 tool-output files
  (0.7 s rather than 2.8 s to index).
- Completion inside a string or after a `.` sends only the entries matching what has been typed.

**Diagnostics.** `5006` is renamed `DevOnlyFunctionCalledOutsideDevBlock`, and its message now says
what a dev block is: a runtime switch the game skips unless developer script is enabled, not code
compiled out of a "release build". The number is unchanged, so pragmas keep working.

**Other.**
- `gscode.serverLogLevel` defaults to `warning` rather than `off`.
- Every inlay hint starts off: `gscode.inlayHints.parameterNames` and
  `gscode.inlayHints.inferredTypes` now default to `false`, joining
  `gscode.inlayHints.macroParameterNames` and `gscode.codeLens.enabled`. Each adds text between the
  characters of a line, so they are something to turn on deliberately. A value you have set
  yourself is kept.
- The description of `workspaceIndexingMode: "off"` says what it actually turns off: the checks
  that need the index. A `#using` naming no file and a duplicate import are still reported.
- Diagnostics for closed files are only re-sent when they change.
- GSCode runs in Restricted Mode, reading `gscode.rawPath` and `gscode.modsPath` from user settings
  only until the workspace is trusted. It used to be disabled there entirely.
- The GSCode commands no longer appear in the Command Palette of a workspace with no GSC in it, and
  **Open Documentation for Symbol** only appears in a GSC, CSC or GSH editor.
- Completing `function` inserts the bare keyword rather than a declaration snippet, so `private` and
  `autoexec` can follow it; the modifiers are offered next.
- A function pointer completes without parentheses (`&foo`, not `&foo()`), and macros are offered
  only on Black Ops III, the one game with a preprocessor.
- Parameter-name inlay hints leave out a label that repeats the argument (`give( player )`, not
  `give( player: player )`), and an inlay-hint setting takes effect as soon as it is changed.
- The extension ships as one bundled file, and only what it runs.

### Removed

- `gscode.completion.fieldScope`. Field completion offers every field after any `x.` and narrows by
  what has been typed. Scoping by the variable before the dot hid fields the object has: `self` is
  whatever a function was called on, and after `blah = level;`, `blah.` offered nothing `level` has.

### Fixed

**Formatting**
- Formatting a CRLF file no longer returns an edit covering the whole file every time, and no
  longer does so on every `;` and `}` with format-on-type. Output keeps the file's line endings.
- Format Document keeps the caret where it was on a large file. VS Code applies a thousand edits or
  more as one replacement and put the caret at its end; the extension now puts it back.
- Format-on-type and Format Selection only change the lines they are scoped to. On a large file
  whose formatting changed lines throughout, one `;` used to rewrite the whole file and move the
  caret thousands of lines. Formatting edits are now one per changed line, and computing them is
  about three times faster on the largest stock scripts.
- A `#define` body is never split across lines (`#define WAIT_SERVER_FRAME {wait(0.05);}` used to
  come out as an empty macro followed by a block), and an object-like `#define HALF ( 1 / 2 )` is no
  longer rewritten into a function-like one. Both changed what the macro means.
- The `doc` snippet on the four pre-BO3 games writes the `///ScriptDocBegin`/`///ScriptDocEnd`
  fence, without which what it inserted read back as an ordinary comment and the function it
  documented hovered with no documentation at all.
- A comment on the same line as an `if`, `else if`, `else`, `for`, `foreach` or `while` header no
  longer pushes its braced body one level right with the `}` out of step.
- A split `if` condition's continuation lines are indented under the condition rather than left
  in the `if`'s column, and a comment after `for ( ; x; )` no longer splits the header in two.
- The space after a ternary's `:` is kept: `b ? &foo : &bar` used to come out `b ? &foo: &bar`.

**Diagnostics and analysis**
- With `workspaceIndexingMode: "full"`, a closed file keeps its cross-file problems after it has
  been opened and closed again, after it changes on disk (a branch switch, a checkout, another
  tool), and when a workspace folder is added mid-session. Each used to drop it back to the problems
  parsing alone finds, until the next start.
- Adding or removing a workspace folder republishes the closed files' problems and re-checks the
  open ones. A removed folder's problems used to stay in the Problems panel, and an added folder's
  never appeared.
- Diagnostics from an older analysis no longer replace newer ones, reappear on a closed file, or
  show twice under two spellings of the same path.
- Editing or saving a `.gsh` updates every file that inserts it, including through other headers.
  Hover on a macro used to show the old value until something was typed in the file using it.
- The startup index no longer overwrites an open document, runs twice at once, or survives
  **Clear Cache and Reindex**; clearing the cache now actually deletes it on Windows.
- A cache restored in a new session picks up headers created or deleted in between, files deleted
  in between, and a workspace-folder change.
- The outline, folding and selection ranges answer for a file opened during startup, before its
  first analysis has finished.
- A mod overlay hides the raw file it replaces everywhere: references, completion, workspace
  symbols, and declarations it no longer contains.
- Path calls (`maps\mp\_utility::foo()`) resolve against the file they name, not a same-named
  function elsewhere.
- One unreadable import no longer silences the unused-import hints for the rest of the file.
- Unreachable code directly inside a `switch` case is reported.
- Argument counts, ambiguous-call, unused-local and unused-binding lints: several false positives on
  shipped scripts, including `waittillmatch`'s trailing argument read as an output and class member
  writes in a method read as unused locals.
- Type inference: subscript writes, compound assignments, `++`/`--`, loops, `switch` and `if`
  conditions now update what a variable is known to hold, and `"text" + vector` is a string.

**Editor features**
- Completion no longer pops up inside comments or right after a closed string, opens the directive
  list when typing the `/#` dev-block opener, offers functions from unrelated files sharing a name
  stem, or misses a function just written.
- Parameter-name inlay hints are back on CoD4, WaW, MW2 and BO1; hints no longer stack on one spot,
  and a call that starts above the visible area keeps its labels.
- Signature help on merge dialects (`#include`) and on call-shaped keywords; go-to-definition on
  locals created by a subscript write; call hierarchy lists each calling function separately; a
  rename refuses a name that is not a valid identifier.
- A builtin's hover no longer repeats its main signature in the overload list, or shows `Name()` for
  a builtin with no signature data.
- `__FILE__` inside an `#insert`ed header names the script it ends up in.
- Quick Fix from the right-click menu finds the fixes for the cursor's line.
- ScriptDoc blocks are coloured as the stock headers actually write them.
- Renaming or moving a folder updates the `#using` and `#insert` paths that name the scripts inside
  it, as renaming a single script already did. Moving a `.gsc` and its `.csc` together no longer
  loses the update either.
- Typing `;` in front of another `;` no longer deletes one inside a string or a comment, and undo
  and redo no longer trigger that clean-up.
- Without the .NET runtime, the GSCode commands said "command not found". They now say the server
  is not running and open the log that explains why, and **Open Documentation for Symbol** still
  opens the library index.

## 2.0.2

### Changed
- The "N references" code lens is now off by default. It re-rendered against analysis that runs
  250 ms behind the keystroke, so the lens rows jumped on every keypress and dragged the viewport
  with them — this was the "editor scrolls as I type" complaint, not format-on-type. Opt back in
  with `gscode.codeLens.enabled`.
- The formatter refuses scripts that ship with the game. Format Document, Format Selection and
  format-on-type all return nothing for a stock script, so a stray format (or format-on-save)
  cannot leave the install differing from everyone else's. Your own scripts placed under `raw`
  still format — the test is whether the file is one of the game's, not which folder it is in.

### Fixed
- Code lens and inlay hint requests now re-analyse the document when its text has moved on since
  the last run, so their positions match the buffer instead of trailing one edit behind it.
- `for ( ;; )` was broken across lines, with the `)` pushed onto a line of its own, whatever the
  settings. An empty `for` clause now stays on the header line, and one already broken that way by
  an earlier format is joined back on the next.

### Added
- `xanim` and `anim` are accepted as `#precache` asset types, and offered in completion.
- `gscode.format.spaceBeforeControlParen`: turn off for `if(`, `for(`, `while(` instead of `if (`.
  Independent of `padParens`, so every combination of keyword space and interior padding is
  reachable.

## 2.0.1

### Changed
- On-type formatting is no longer enabled by default. 2.0.0 shipped `editor.formatOnType` on for
  GSC, CSC and GSH, so typing `;` or `}` re-indented and re-aligned the surrounding lines. To opt
  back in, add `"[gsc]": { "editor.formatOnType": true }` (and likewise `[csc]`, `[gsh]`) to your
  settings; Format Document and Format Selection are unaffected. With it off, `}` no longer
  auto-dedents as you type.

### Added
- Two more formatter spacing settings, so the padding is yours to choose per kind:
  `gscode.format.padCallParens` (`foo( a )` vs `foo(a)`, separately from control-flow parens) and
  `gscode.format.padBrackets` (`a[ i ]` vs `a[i]`). Both default to the 2.0.0 behaviour; set
  `padCallParens` and `padBrackets` off, with `padParens` on, for `if ( foo(a[i]) )`.

### Fixed
- The formatter put a space after a unary minus or address-of — `( -150, -1024, 304 )` came out
  `( - 150, - 1024, 304 )` and `&funcname` as `& funcname`. Sign and `&` now hug their operand;
  binary `a - b` and `a & b` are unchanged.

## 2.0.0

A complete ground-up rewrite of the language server and VS Code extension.

### Added
- **Support for five games rather than one.** Call of Duty 4, World at War, Modern Warfare 2,
  Black Ops and Black Ops III, selected with `gscode.game`. Each dialect's keywords, import style
  (`#include` merge vs. `#using` namespaces), function-pointer and ScriptDoc syntax, and bundled
  engine data come from one game profile rather than from branching, and each was checked against
  that game's own shipped scripts. Every later game up to Black Ops 6 is nameable as a *core* over
  the shared base dialect, for a contributor with those tools to fill in.
- New analysis pipeline: span-based lexer, provenance-tracking preprocessor, recursive-descent
  parser with error recovery, and symbol extraction — none of which throw on malformed input.
- **Eight diagnostics 1.5 raised, brought back.** A macro name defined twice, or defined in a header
  and again in the script that inserts it, naming which definition wins (`2017`); a macro parameter
  written twice (`2018`); a switch's second `default:` (`5027`); reading the value of a `thread`
  call, which is `undefined` as soon as the thread waits (`5028`); a `const` whose value is not
  known at compile time, and an assignment to one (`5029`, `5030`); division by a literal zero
  (`5031`); and a statement that computes a value and discards it (`5032`). Each was measured over
  all five games' shipped scripts before being given a severity, and all report nothing there except
  `5028`, whose findings are real.
- Mod-tools support: `share/raw` and each `mods/<name>` indexed in isolation with overlay
  resolution; a first-class workspace-only mode for machines with no game install.
- A single script database with structurally isolated GSC/CSC worlds and a shared GSH store,
  backed by a persistent SQLite cache with two-gate versioning for fast cold starts.
- Full LSP suite: diagnostics, hover, completion, signature help, definition, references
  (including string/hash/localized/anim literals), highlight, semantic tokens, folding,
  selection ranges, document/workspace symbols, code lens, rename, call and type hierarchy,
  inlay hints, document links, formatting (whole/range/on-type), and code actions.
- Inlay-hint families apply the moment they are toggled: a settings push that changes one asks the
  client to re-request its hints, rather than leaving the change invisible (and a family switched
  off still on screen) until the next keystroke or scroll.
- Macro parameter-name inlay hints: the arguments of a `#define` invocation labelled with the
  macro's own parameter names, `IS_TRUE( __a: level.ready )`. A third inlay family, separate from
  the call-site one and off by default (`gscode.inlayHints.macroParameterNames`), because a macro
  parameter is named for the macro's body rather than for its caller.
- Type-flow inference for inferred-type inlay hints and local-variable hovers, seeded with
  engine object-field types.
- Corruption-proof whitespace-only formatter (refuses syntax errors; re-checks its own output).
- Code actions: remove-duplicate-`#using` and add-missing-`#using`, backed by a namespace-usage
  lint.
- Commands: `gscode.showOutput`, `gscode.restartServer`, `gscode.clearCacheAndReindex`,
  `gscode.selectGame`, and `gscode.openApiLibrary` (`shift+f1` in gsc/csc/gsh files).
- **GSCode: Select Game**, a picker for the dialect this workspace targets. The roster comes from
  the server, so it offers only games with an implemented dialect, and it ticks the game actually
  in force rather than the one `gscode.game` names — those differ whenever the setting names
  something the server did not recognise. Previously the picker appeared only if the server
  noticed a file that did not look like the selected game, and only once per session.
- In-source suppression carried inside comments: `#pragma disable|restore <code>|all|format`.
  A named code is suppressed at whatever severity it carries, errors and syntax errors included —
  wider than the C# pragma the spelling comes from, which is why `warning` is not part of it (though
  the C# form is still accepted). 1.5's `// gscode ignore` still works as a legacy alias, suppressing
  every diagnostic on the line below the comment.
- A settings surface for the above, under `gscode.*`: the game and script roots
  (`game`, `raw.enabled`, `rawPath`, `modsPath`, `rawFileWarningMode`), indexing
  (`workspaceIndexingMode`, `enableWorkspaceCache`, `diagnostics.scope`), the editor features
  (`outline.showAssignments`, `codeLens.enabled`, the `inlayHints.*` and `completion.*` keys) and
  formatting (`format.padParens`, `format.maxBlankLines`, `format.sortDirectives`,
  `format.alignConsecutive`).

### Changed
- Requires the .NET 10 runtime.
- Completion outside a function body offers the same names it offers inside one — the macros an
  `#insert`ed header supplies, the functions and classes in scope — rather than keywords and
  snippets alone. A `REGISTER_SYSTEM` line and the function pointers and `undefined`s inside its
  arguments are all completable now, and nothing completed at file scope carries a trailing
  semicolon.
- A function pointer completes as `&foo`, without the parentheses a call gets. Applies to
  `&namespace::foo` too, and only on Black Ops III, where `&` is what makes a pointer.
- Signature help answers on a function-like macro, showing its parameter names, the argument the
  caret is in, what the macro expands to, and its trailing-comment documentation. Previously only a
  script function or a builtin had a signature, so typing the arguments to `REGISTER_SYSTEM(` or
  `IS_TRUE(` showed nothing. The macro is read from the file being edited plus the headers it
  `#insert`s, so a header added a keystroke ago counts.
- A macro's expansion keeps the lines it was written on, in hover as well as signature help. A
  multi-statement macro used to be joined into one long line along with its backslashes; the
  backslashes still go, and a macro that declares a function now reads as a declaration. Indentation
  is redrawn at four spaces a level, so a header indented with tabs reads the same as one indented
  with spaces.
- Dialect-specific snippets moved from the extension to the server. A contributed snippet is
  registered per language id and one id covers five games, so `foreach`, `class`, `new`, the
  function declaration, `#precache`, the import directives and the ScriptDoc forms used to be
  offered while editing games that do not have them. They are now offered only where the selected
  game has the construct; the universal ones still ship with the extension and work before the
  server has started.

### Removed

Named rather than left to be discovered, because a 1.5 user upgrading loses these.

- **The type-derived diagnostics.** 1.5 ran an abstract-interpretation pass over a control-flow
  graph, and thirteen diagnostics that came out of it are still not raised here: the operator and
  conversion checks (`OperatorNotSupportedOnTypes`, `NoImplicitConversionExists`), argument-type
  checking against the builtin library (`ArgumentTypeMismatch` and its `Unverified` twin), the
  member, index, enumeration and vector-component family (`DoesNotContainMember`,
  `CannotUseAsIndexer`, `CannotEnumerateType`, `InvalidVectorComponent`), the engine-field pair
  (`PredefinedFieldTypeMismatch`, `CannotAssignToImmutableEntity`), the function-value pair
  (`StoreFunctionAsPointer`, `ExpectedFunction`), and the type half of `UnreachableCase`.

  Eight others named in an earlier draft of this entry have since been restored, and are listed
  under Added above.

  Worth being exact about what is missing, and about one thing this entry previously got wrong. It
  said most of 1.5's analysis was already switched off, pointing at commented-out analyser and
  operator-data files. Those files are real, but they were an abandoned earlier generation: the pass
  that replaced them ran on every analysis, and 1.5 shipped editor quick fixes for five of these
  codes. Nothing here was switched off. What 2.0 has instead is type inference that feeds hovers,
  inlay hints and four lints; what it does not have is the union lattice with constant tracking and
  entity subtypes that the thirteen above need. Every other diagnostic layer — lexing,
  preprocessing, parsing, extraction, resolution and arity — is at parity or ahead, across five
  dialects rather than one.

- **The headless CLI.** 1.5 shipped a `GSCode.CLI` project. 2.0 does not, though the layering keeps
  it cheap to restore: the LSP dependency is isolated in the server project, so the parser and
  workspace are already a complete engine without it.

## 1.5.0

- Added game script indexing so GSCode can discover namespaces and functions across the workspace
  and shared raw scripts without every file needing to be opened first.
- Added workspace-wide namespace and `namespace::function` completions, including `sys::` API
  completions and automatic `#using` insertion for functions from unimported scripts.
- Added field completions for dot-access on common globals such as `level`, `world`, and `game`,
  with fields learned from indexed scripts.
- Added optional persistent workspace caching for faster startup after scripts have already been
  indexed.
- Improved `#using` quick fixes so they work for more missing namespace/function cases, insert
  alphabetically, skip duplicates, and avoid suggesting scripts from the wrong VM.
- Improved protected raw-folder warnings with `gscode.rawFileWarningMode`, warning for stock
  shared-raw scripts by default while staying quiet for custom scripts kept in `share/raw`.
- Fixed GSH and macro invalidation so changes to inserted files, added/removed macros, and macro
  body edits are picked up after save without restarting VS Code.
- Fixed several diagnostics, navigation, and highlighting edge cases, including string `.size`,
  no-op `break` statements, boolean-literal hints, namespace scope leakage, dev blocks, switch
  expressions, usage detection, and type-flow convergence.
- Updated the extension baseline to VS Code 1.85+ with newer client/server dependencies.

Special thanks go to [iAmThatMichael](https://github.com/iAmThatMichael) who contributed many of the
above changes.

## 1.4

- Added a `gscode ignore` comment directive that suppresses diagnostics on the following line.
- Added context-aware completion suggestions based on editor location.
- Significant API updates & improvements aimed to reduce false-positive diagnostics. Added typing to
  most methods.
- Added type checking against function signatures.
- Added quick fix action capability with action for unused usings.
- Various codebase quality improvements, optimisations, and bug fixes.

Special thanks go to [iAmThatMichael](https://github.com/iAmThatMichael) who contributed many of the
above changes ([#54](https://github.com/Blakintosh/gscode/pull/54),
[#63](https://github.com/Blakintosh/gscode/pull/63)).

## 1.3

- Add capability for more detailed diagnostics by 'emulating' select functions, such as
  `LuiNotifyEvent`.
- Significant memory-focused optimisations.
- Various bug fixes and API updates.

## 1.2

- Re-added indexing support.
- Various optimisations and bug fixes.

Special thanks go to [iAmThatMichael](https://github.com/iAmThatMichael) who contributed many of the
above changes ([#51](https://github.com/Blakintosh/gscode/pull/51)).

## 1.1

- Various type system improvements, including new support for inference on entity fields.
- Added type inference support for built-in functions (via the API).
- Added `vectorscale` analysis.
- Various bug fixes.

## 1.0

- Adds semantic analysis steps & type inference associated validation.
- Various bug fixes.
- End of beta phase.

## 0.10 beta

- Disabled workspace indexing temporarily due to performance concerns.
- Added reference finding (Go to Reference, Find All References).
- Added workspace indexing of scripts.
- Fixed switch case analysis with braced bodies.

Special thanks go to [iAmThatMichael](https://github.com/iAmThatMichael) who contributed all of the
above changes ([#30](https://github.com/Blakintosh/gscode/pull/30),
[#31](https://github.com/Blakintosh/gscode/pull/31)).

## 0.9 beta

- Added Outliner support for classes, functions, and macros.
- Added goto definition support for usings, script functions, and macros.
- Added signature support for script functions & builtins.
- Fixed function & variable names not showing signatures & tooltips due to case-sensitivity.
- Added analyser checks for: unknown namespace, unused using, unused variable, unused parameters,
  switch checks.
- Added comment code region support (`/* region Name */` `/* endregion */` syntax) with folding
  ranges in the editor ([#22](https://github.com/Blakintosh/gscode/issues/22)).

Special thanks go to [iAmThatMichael](https://github.com/iAmThatMichael) who contributed most of the
above changes ([#24](https://github.com/Blakintosh/gscode/pull/24)).

## 0.2 beta

- Added a non-contextual completion handler to suggest function completions.
- Added a non-contextual handler to provide GSCode API hover documentation on built-in functions.
- Added diagnostic for missing scripts from using.
- Added basic signature analysis for highlighting of class, function, method and parameter
  definitions.
- Added using highlight with script path hint.
- Various bug fixes.

## 0.1 beta

- Initial public release. Adds GSC & CSC language support, providing syntax highlighting and
  IntelliSense for preprocessor and syntactic analysis.
