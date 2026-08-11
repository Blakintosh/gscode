---
name: add-game-profile
description: Add support for another Call of Duty GSC dialect, or change how an existing one behaves. Use when touching GameProfile, SupportedProfiles, or anything that differs between games — it covers the portability seam, the verification bar, and the traps that come from BO3-shaped assumptions.
---

# Adding or changing a game profile

## The seam

`GameProfile` (`src/GSCode.Core/GameProfile.cs`) is the one place game-specific knowledge lives.
Nothing else may branch on a game. A new dialect is a new record in
`src/GSCode.Core/Profiles/SupportedProfiles.cs`, not a change to engine logic.

Data, not switches. `Keywords` is a set, and `HasClasses`, `HasFunctionKeyword`, `HasForeach` and
`HasDoWhile` all derive from it, so a game gains a feature by gaining the keyword.

## The two axes that matter most

**`ImportStyle`.** `Namespace` (BO3: `#using`, functions keyed `(namespace, name)`) versus
`Include` (everything earlier: `#include`, functions keyed `(null, name)`). Almost every surprising
bug in this area came from assuming the first.

Under `Include`, every same-named function in the workspace shares one key — CoD4's animscripts
hold 1,230 `main()`s — so anything keyed by name must scope by reachability. `GameProfile.KeyNamespace`
and `DatabaseQueries.ScopeToIncludeGraph` exist for exactly this.

**`HasInlinePathCalls`.** `maps\mp\_util::foo()` reaches a function with **no import at all**. Any
reachability rule that only follows `#include` will be wrong on these — that mistake took a
function's reference count from 1,230 to zero, which reads as "this is dead code".

## Directives are gated one at a time, and the default is NOT "everything has it"

`GscKeywords.IsAvailable` and `Keywords.IsDirectiveEnabled` each name the flag a directive depends
on — `ImportStyle` for the imports, `HasNamespaceDirective`, `HasHeaders`, `HasPrecacheDirective`,
`HasMacros` for `#define` and the `#if` chain. Both used to end in "anything else beginning with
`#` exists everywhere", and that is how CoD4 came to be offered a preprocessor it does not have.
Only the animtree pair is genuinely universal.

A new profile therefore has to answer each flag, and answer it from that game's own scripts. The
trap is the shape of the evidence rather than its absence: file-scope constants (`MAX = 4;`) read
exactly like macros, and `#animtree` is real in every game but never starts a line, so a
line-anchored grep says it is unused. Measure the construct the way it is actually written.

Where a game lacks a capability, the corresponding rule should SAY so rather than go quiet —
`gscode-2016` for the preprocessor, `gscode-5025` for a word another game has as a keyword.

## Install layout

`RawSubfolder` and `ModsSubfolder` say what the folders are CALLED, never where the game is — that
comes from `gscode.rawPath`/`gscode.modsPath`, and is derived from the workspace only as a
fallback. BO3 is `share\raw`; every earlier game is `raw`; all use `mods`.

## Bundled data

`DataFilePrefix` names the files (`<prefix>_api_gsc.json`, `_object_fields.json`,
`_radiant_keys.json`, `_stock_scripts.txt`). Null means the game ships none, which is a valid state —
every CORE is there today. All five supported games now ship a library.

**A game with no source of its own can still get one.** MW2 shipped no mod tools: no wordfile, no
documentation, only a `radiant/keys.txt`. Its library is CoD4's — the LIBRARY, not the wordfile
behind it; the two differ and taking the smaller one produced 215 false include reports — corrected
by sweeping MW2's own scripts for names it lacked. Setting a `DataFilePrefix` also turns on the
stock-script list and the `BundledDataTests` invariant that every promised file actually ships, so
run `StockScriptListTests` in the same pass or that test fails.

**`HasCompleteBuiltinLibrary` is a different claim from `Verified`.** Verified means the DIALECT is
proven against the game's own scripts. Completeness means the FUNCTION LIST is exhaustive enough to
say a name is *not* an engine function. WaW's and BO1's libraries come from a partial wordfile, so
they are used for completion and hover but never to report a name as unknown.

Add the prefix to `client/package.json`'s `gscode.game` enum too, and only when a profile really
exists: offering a game with no profile resolves silently to BO3, which is exactly how
"gscode.game does nothing" happened.

## Earning `Verified = true`

Evidence, not assertion. `GameCorpusTests` enforces it over the game's own scripts:

1. **No crashes** — every script analyses without throwing.
2. **Parse budget** — lex/parse errors under 1% of files.
3. **Formatter round-trip** — over a 250-file sample, reflow changes no token, and a second format
   is a fixed point.

Inspect every remaining failure rather than accepting the budget. All of the current ones are
genuinely malformed files no compiler would take either — missing semicolons, an unterminated
`/*`, NUL padding bytes — and each is listed in `GAME_PROFILES.md`.

## The BO3-shaped assumptions that have bitten

Worth checking against any new work in this area:

- **Data loaded before the profile was selected.** The bundled data resolves during container
  construction, so the game must be chosen from the command line (`--game`), not the initialize
  handshake, which arrives too late.
- **The cache surviving a game change.** A record is dialect-specific; `ServerBuildIdentity`
  includes the game for this reason.
- **ScriptDoc.** BO3 uses `/@ … @/`; every earlier game fences a block inside an ordinary `/* */`
  with `///ScriptDocBegin`. `ScriptDocStyle` records which, and the extractor reads it.
