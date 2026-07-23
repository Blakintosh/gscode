# Game profile worksheet

Fill in what each game supports. Every row maps to a `GameProfile` shell in
`src/GSCode.Core/GameProfile.cs`. Only **bo3** is verified today; the rest are `?` until someone
confirms them. Replace a `?` with the value, and when a whole row is done say so and it gets
promoted to a real profile with `Verified = true`.

## Legend

- **✓ / ✗** — has it / does not
- **?** — unknown, fill in
- **import** — `ns` = `#using` imports a namespace, calls stay qualified (`ns::foo`) · `inc` =
  `#include` merges functions into the file, calls are unqualified
- **fptr** — how a function pointer is written: `&` = `&foo` / `&ns::foo` (BO3 style) · `::` = a bare
  qualified name is the pointer, `maps\mp\_utility::foo` with no parens (IW / pre-BO3 style)
- **arr-ref** — array parameters passed by reference (✓) or copied by value (✗)
- **csc** = client scripts (`.csc`) · **gsh** = headers (`.gsh` / `#insert`) · **class** =
  `class`/`new`/`->` · **func** = the `function` keyword on declarations · **#ns** = the
  `#namespace` directive

## Matrix

| game   | studio      | year | csc | gsh | class | func | #ns | import | fptr | arr-ref |
|--------|-------------|-----:|:---:|:---:|:-----:|:----:|:---:|:------:|:----:|:-------:|
| cod4   | InfinityWard| 2007 |  ✗  |  ✗  |   ✗   |  ✗   |  ✗  |   inc    |  ::   |    ✗    |
| waw    | Treyarch    | 2008 |  ✓  |  ✗  |   ✗   |  ✗   |  ✗  |   inc    |  ::   |    ✗    |
| mw2    | InfinityWard| 2009 |  ✗  |  ✗  |   ✗   |  ✗   |  ✗  |   inc    |  ::   |    ✗    |
| bo1    | Treyarch    | 2010 |  ✓  |  ✗  |   ✗   |  ✗   |  ✗  |   inc    |  ::   |    ✗    |
| mw3    | InfinityWard| 2011 |  ✗  |  ✗  |   ✗   |  ✗   |  ✗  |   inc    |  ::   |    ✗    |
| bo2    | Treyarch    | 2012 |  ✓  |  ✗  |   ✗   |  ✗   |  ✗  |   inc    |  ::   |    ✗    |
| ghosts | InfinityWard| 2013 |  ✗  |  ✗  |   ✗   |  ✗   |  ✗  |   inc    |  ::   |    ✗    |
| aw     | Sledgehammer| 2014 |  ✗  |  ✗  |   ✗   |  ✗   |  ✗  |   inc    |  ::   |    ✗    |
| **bo3**| Treyarch    | 2015 |  ✓  |  ✓  |   ✓   |  ✓   |  ✓  |  ns    |  &   |    ✓    |
| iw     | InfinityWard| 2016 |  ?  |  ?  |   ?   |  ?   |  ?  |   ?    |  ?   |    ?    |
| wwii   | Sledgehammer| 2017 |  ?  |  ?  |   ?   |  ?   |  ?  |   ?    |  ?   |    ?    |
| bo4    | Treyarch    | 2018 |  ?  |  ?  |   ?   |  ?   |  ?  |   ?    |  ?   |    ?    |
| mw19   | InfinityWard| 2019 |  ?  |  ?  |   ?   |  ?   |  ?  |   ?    |  ?   |    ?    |
| bocw   | Treyarch    | 2020 |  ?  |  ?  |   ?   |  ?   |  ?  |   ?    |  ?   |    ?    |
| vg     | Sledgehammer| 2021 |  ?  |  ?  |   ?   |  ?   |  ?  |   ?    |  ?   |    ?    |
| mw22   | InfinityWard| 2022 |  ?  |  ?  |   ?   |  ?   |  ?  |   ?    |  ?   |    ?    |
| mw23   | Sledgehammer| 2023 |  ?  |  ?  |   ?   |  ?   |  ?  |   ?    |  ?   |    ?    |
| bo6    | Treyarch    | 2024 |  ?  |  ?  |   ?   |  ?   |  ?  |   ?    |  ?   |    ?    |

## The two axes you called out

**Function pointer (`fptr`).** BO3 made a pointer explicit with `&`: `level.f = &foo;` or
`&namespace::foo;`, and a bare `ns::foo` is always a call. Before BO3 the qualified name itself was
the pointer — `level.f = maps\mp\_utility::foo;` with **no** parentheses — and `::foo` referenced a
function in the same file. This is modelled as `FunctionPointerStyle` (`Ampersand` vs
`PathQualified`) on the profile. It is a per-dialect default now; when the parser fork lands (D2) it
can also be surfaced as a user setting to override per workspace, since some codebases mix eras.

**Arrays (`arr-ref`).** BO3 passes arrays to functions **by reference only** — a callee that mutates
an array changes the caller's. Earlier games copy arrays by value. This is `ArraysPassedByReference`
on the profile; it affects analysis (aliasing), not syntax.

## Other differences to record

You mentioned there are more (directives especially). Add them here as you find them, and we will
grow the capability set to match. Candidates already on the radar for the IW family:

- `#include` vs `#using` (captured as `import` above)
- No `function` keyword; a declaration is `name( args ) { }` (captured as `func`)
- No `#namespace`; a file's namespace is its path (captured as `#ns`)
- `#using_animtree( "name" )` — present in both families, so probably not a difference
- Any game-specific directives not in the T7 set (`#precache` variants, etc.) — list them per game:

| game | directive / feature | notes |
|------|---------------------|-------|
|      |                     |       |

- mw2 supports file-scope constants: `CONST_FOO = 4;` outside any function, and they can reference
  each other (`RUN_N_GUN_TRANSITION_POINT = 60 / MAX_RUN_N_GUN_ANGLE;`). CoD4 does not — the axis is
  MW2-onward. Parser support is D2.
- ScriptDoc: BO3 uses `/@ @/`. Every earlier game (both IW and Treyarch) fences it with
  `///ScriptDocBegin` / `///ScriptDocEnd` lines inside an ordinary `/* */` comment. (Not `/# #/`;
  that is a dev block.)

## Feature evolution

The lineage alternates between two shapes — an **Infinity Ward shape** and a **Treyarch shape** —
that stay put through each studio's games, until BO3 rewrites everything. Each row is the diff from
the prior release, so the toggling is visible.

| game   | Δ from the prior release |
|--------|--------------------------|
| CoD4   | **Baseline (IW3).** `#include`; `::` inline path calls + pointers; `///ScriptDoc`; function-call precache. No csc, no `function` kw, no classes, no hash strings, no file-scope consts. |
| WaW    | **+ client scripts (`.csc`)** — Treyarch adds them. Everything else as CoD4. |
| MW2    | **− csc** (IW again); **+ file-scope constants** (`CONST = 4;`). |
| BO1    | **+ csc**, **+ hash strings** (`#"…"`); **− file-scope consts**. |
| MW3    | **− csc, − hash strings** (IW shape). |
| BO2    | **+ csc, + hash strings** (Treyarch shape). |
| Ghosts | **− csc, − hash strings** (IW shape). |
| AW     | ~ same as Ghosts — Sledgehammer on an IW-derived engine. |
| BO3    | **Wholesale rewrite (T7).** + `function` kw, + classes (`class`/`new`/`->`), + `#namespace`, + `#using` namespace imports (replaces `#include`), + `&` pointers (replaces `::`), + `.gsh`/`#insert`, + `#precache` directive, + `/@ @/` ScriptDoc, + arrays by-reference. **− inline path calls.** Keeps csc + hash strings. |
| IW     | (after BO3) Reverts to the **IW shape**, not T7: no `function` kw, `::` pointers. |
| WW2    | (after BO3) Sledgehammer. |

Capability axes on `GameProfile`:

- **HasInlinePathCalls** — call/reference a function by its file path, `maps\mp\_utility::foo()`.
  Every pre-BO3 game; BO3 has none (it uses `#using` + `ns::foo`).
- **HasHashStrings** — `#"some_string"`, hashed at compile time. Treyarch only (BO1, BO2, BO3); the
  IW games have none.
- **HasPrecacheDirective** — `#precache( "type", "asset" )`. BO3 only. Every earlier game precaches
  with function calls (`PrecacheModel`, `PrecacheItem`, …).
- **ScriptDocStyle** — `TripleSlash` (pre-BO3) vs `AtSign` (BO3).

Also: no `.csc` in any IW game; `.csc` in the Treyarch games; no `function` keyword, no classes, no
`->`/`new`/varargs, `#include` imports and `::` path-qualified pointers in every pre-BO3 game.

## Language constructs

Beyond the shape axes above, individual language constructs appear at different points in the
lineage. Modelled on `GameProfile` as `Has*` flags, set from what the scripts actually use.

| construct | availability |
|-----------|--------------|
| `foreach ( item in coll )` | **MW2 (2009) onward.** CoD4 and WaW have only `for`/`while` (0 uses; MW2 has 2,523). `HasForeach`. |
| `do { … } while ( … )` | **BO3.** Not seen in any pre-BO3 script (usage-derived for the middle games; CoD4 has none). `HasDoWhile`. |
| classes (`class`/`new`/`->`) | **BO3.** `HasClasses`. |
| varargs (`…`) | **BO3.** `HasClasses`-era; absent pre-BO3. |

Shared baseline — present in **every** game, so not axes: `for`, `while`, `if`/`else`, `switch`/
`case`/`default`, `break`, `continue`, `return`, `wait`, `waittill` / `notify` / `endon` /
`waittillframeend` / `waittillmatch`, `thread`, `isdefined`, `#define` macros, `#using_animtree`,
`%anim` references, `/# #/` dev blocks.

Still to pin down (constructs whose exact introduction point is unconfirmed): the ternary `?:`
(barely present in CoD4, common from MW2), `assert`/`assertmsg`, `breakpoint`, and any per-game
builtins. Add them as flags once a construct is confirmed to differ between games.

## Root discovery (where the raw scripts live)

Only BO3 has an install the extension can find on its own — the `TA_TOOLS_PATH` environment variable
plus the `share\raw` subfolder, both recorded on its profile. No other game ships that, so **every
non-BO3 game takes a user-defined raw path**: the existing `gscode.rawPath` (and `gscode.modsPath`)
settings, which override the profile's env-var lookup for any game. With none set, the workspace
runs in workspace-only mode, which is first-class.

So the model is: BO3 auto-detects via the env var; every earlier game points `gscode.rawPath` at
its own raw scripts folder. The profile already encodes this (`RootEnvironmentVariable` /
`RawSubfolder` / `ModsSubfolder` are null for every game but BO3), so nothing hardcodes BO3's paths.

Planned enhancement (not built): after switching to a non-BO3 game with no raw path set, prompt once
to configure `gscode.rawPath` — the same nudge as the game-mismatch prompt. A per-game raw path
(so switching games remembers each) is a possible later refinement; a workspace is one game, so a
single `gscode.rawPath` is enough for now.

## Coverage

Every game CoD4 through BO3 has its profile filled in. Still open (unsupported shells): everything
after BO3 except IW and WW2 — **BO4, MW19, BOCW, VG, MW22, MW23, BO6**.
